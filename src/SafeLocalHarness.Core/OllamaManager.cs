using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

namespace SafeLocalHarness.Core;

public sealed record SetupProgress(
    string Stage,
    double? Fraction,
    string Message,
    long? CompletedBytes = null,
    long? TotalBytes = null,
    double? BytesPerSecond = null,
    TimeSpan? EstimatedRemaining = null);

public interface ILocalModelRuntime : IAsyncDisposable
{
    string StorageRoot { get; }
    Uri ApiBaseUri { get; }
    int? OwnedProcessId => null;

    /// <summary>All owned processes: the server and any children it started (e.g. the model runner).</summary>
    IReadOnlyList<int> OwnedProcessIds => OwnedProcessId is int id ? [id] : [];
    string? FindInstalledExecutable();
    Task<IReadOnlyList<string>> GetInstalledModelsAsync(CancellationToken cancellationToken = default);
    Task EnsureServerAsync(CancellationToken cancellationToken = default);
    Task PullModelAsync(string model, IProgress<SetupProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Releases model memory held by the owned runtime (a no-op when nothing is loaded).</summary>
    Task UnloadModelsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>Stops before loading a model that does not fit the current memory budget (a no-op without a monitor).</summary>
    Task EnsureModelFitsAsync(string model, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>
/// Owns only the Ollama runtime promoted under LocalStudio's runtime store.
/// An independently installed per-user Ollama can be detected for display, but is never executed,
/// modified, or used as LocalStudio's model store.
/// </summary>
public sealed class OllamaManager : ILocalModelRuntime
{
    private readonly AppPaths _paths;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly RuntimeResourcePlan? _resourcePlan;
    private readonly SemaphoreSlim _serverGate = new(1, 1);
    private readonly string _externalUserExecutable;
    private readonly MemoryPressureMonitor? _pressureMonitor;
    private Process? _server;
    private WindowsProcessJob? _serverJob;
    private Uri? _apiBaseUri;

    public OllamaManager(
        AppPaths paths,
        HttpClient? http = null,
        string? officialUserExecutable = null,
        RuntimeResourcePlan? resourcePlan = null,
        MemoryPressureMonitor? pressureMonitor = null)
    {
        _paths = paths;
        _pressureMonitor = pressureMonitor;
        _ownsHttpClient = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        _resourcePlan = resourcePlan;
        _externalUserExecutable = officialUserExecutable ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Ollama", "ollama.exe");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("LocalStudio/0.16.0");
    }

    public Uri ApiBaseUri => _apiBaseUri
        ?? throw new InvalidOperationException("LocalStudio managed AI runtime has not started.");

    public string StorageRoot => _paths.Root;
    public int? OwnedProcessId => _server is { HasExited: false } ? _server.Id : null;

    // The model runs in a child process whose name may differ from the server, so membership comes from the job.
    public IReadOnlyList<int> OwnedProcessIds
    {
        get
        {
            var fromJob = _serverJob?.ProcessIds();
            if (fromJob is { Count: > 0 }) return fromJob;
            return OwnedProcessId is int id ? [id] : [];
        }
    }

    public string? FindInstalledExecutable()
    {
        var stableContent = Path.Combine(_paths.RuntimeRoot, "ollama", "stable", "content");
        UserSpaceGuard.EnsureAllowed(stableContent);
        return Directory.Exists(stableContent)
            ? Directory.EnumerateFiles(stableContent, "ollama.exe", SearchOption.AllDirectories).FirstOrDefault()
            : null;
    }

    public string? DetectExternalUserInstallation()
    {
        if (!File.Exists(_externalUserExecutable)) return null;
        UserSpaceGuard.EnsureAllowed(_externalUserExecutable);
        return Path.GetFullPath(_externalUserExecutable);
    }

    public async Task<IReadOnlyList<string>> GetInstalledModelsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureServerAsync(cancellationToken);
        using var response = await _http.GetAsync(new Uri(ApiBaseUri, "api/tags"), cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!json.RootElement.TryGetProperty("models", out var models)) return [];
        return models.EnumerateArray()
            .Select(item => item.TryGetProperty("name", out var name) ? name.GetString() : null)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public string KvCacheType => _resourcePlan?.KvCacheType ?? ModelMemoryEstimator.DefaultKvCacheType;

    public async Task<IReadOnlyList<string>> GetLoadedModelsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureServerAsync(cancellationToken);
        using var response = await _http.GetAsync(new Uri(ApiBaseUri, "api/ps"), cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!json.RootElement.TryGetProperty("models", out var models)) return [];
        return models.EnumerateArray()
            .Select(item => item.TryGetProperty("name", out var name) ? name.GetString() : null)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToArray();
    }

    public async Task EnsureModelFitsAsync(string model, CancellationToken cancellationToken = default)
    {
        if (_pressureMonitor is null) return;
        // Once loaded, the model's memory is already outside "available", so only an unloaded model is checked.
        if ((await GetLoadedModelsAsync(cancellationToken)).Any(name => name.Equals(model, StringComparison.OrdinalIgnoreCase))) return;
        var assessment = _pressureMonitor.SampleAndAssess();
        var required = ModelMemoryEstimator.RequiredBytes(model, _resourcePlan?.ContextTokens ?? 8192, KvCacheType);
        if (required == long.MaxValue || ModelFitPolicy.Fits(required, assessment.BudgetBytes)) return;
        // Short of memory: other apps are asked to give back memory they are not using, only as much as the model needs
        // (user, 2026-10-14). The reserve the budget keeps for Windows stays in place.
        var reserve = Math.Max(0, assessment.AvailableBytes - assessment.BudgetBytes);
        var relief = MemoryRelief.MakeRoom(required + reserve + 256L * 1024 * 1024,
            () => _pressureMonitor.SampleAndAssess().AvailableBytes, _serverJob?.ProcessIds() ?? []);
        assessment = _pressureMonitor.SampleAndAssess();
        if (!ModelFitPolicy.Fits(required, assessment.BudgetBytes))
            throw new ResourcePressureException(ModelFitPolicy.Explain(required, assessment.AvailableBytes) +
                (relief.AppsAsked > 0 ? $"（ほかのアプリ {relief.AppsAsked} 個に未使用のメモリを手放してもらいましたが、まだ足りません）" : ""));
    }

    // Runtime installation is intentionally not part of this class. The only download path is the
    // user-approved plan in LocalAiSetupService -> ZeroAdminBootstrapService -> RuntimeArtifactManager.

    public async Task EnsureServerAsync(CancellationToken cancellationToken = default)
    {
        RejectElevation();
        // Every AI path (planning, generation, model setup, benchmark) passes here before loading a model.
        // Under any pressure sign, model use is not expanded; the reason is reported to the caller.
        _pressureMonitor?.ThrowIfHeavyWorkUnsafe();
        await _serverGate.WaitAsync(cancellationToken);
        try
        {
            if (_server is { HasExited: false } && _apiBaseUri is not null && await ProbeOwnedServerAsync(cancellationToken))
                return;

            DisposeOwnedServer();
            var executable = FindInstalledExecutable()
                ?? throw new FileNotFoundException("LocalStudio managed Ollama is not ready.");
            UserSpaceGuard.EnsureAllowed(executable);
            Directory.CreateDirectory(_paths.ModelRoot);

            var port = FindAvailableLoopbackPort();
            _apiBaseUri = new Uri($"http://127.0.0.1:{port}/");
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = false,
                RedirectStandardError = false
            };
            startInfo.ArgumentList.Add("serve");
            startInfo.Environment["OLLAMA_HOST"] = $"127.0.0.1:{port}";
            startInfo.Environment["OLLAMA_MODELS"] = _paths.ModelRoot;
            startInfo.Environment["OLLAMA_NO_CLOUD"] = "true";
            startInfo.Environment["OLLAMA_MAX_LOADED_MODELS"] = "1";
            startInfo.Environment["OLLAMA_NUM_PARALLEL"] = Math.Max(1, _resourcePlan?.HeavyModelConcurrency ?? 1)
                .ToString(CultureInfo.InvariantCulture);
            startInfo.Environment["OLLAMA_MAX_QUEUE"] = "4";
            startInfo.Environment["OLLAMA_KEEP_ALIVE"] = _resourcePlan?.OllamaKeepAlive ?? "30s";
            if ((_resourcePlan?.ContextTokens ?? 0) > 0)
                startInfo.Environment["OLLAMA_CONTEXT_LENGTH"] = _resourcePlan!.ContextTokens.ToString(CultureInfo.InvariantCulture);
            // A quantized attention cache needs Flash Attention; together they shrink the per-context memory.
            startInfo.Environment["OLLAMA_FLASH_ATTENTION"] = "1";
            startInfo.Environment["OLLAMA_KV_CACHE_TYPE"] = KvCacheType;

            _server = Process.Start(startInfo)
                ?? throw new InvalidOperationException("LocalStudio managed Ollama did not start.");
            try { _server.PriorityClass = ProcessPriorityClass.BelowNormal; }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }

            try { _serverJob = WindowsProcessJob.Attach(_server); }
            catch
            {
                DisposeOwnedServer();
                throw;
            }

            for (var attempt = 0; attempt < 80; attempt++)
            {
                await Task.Delay(250, cancellationToken);
                if (await ProbeOwnedServerAsync(cancellationToken)) return;
                if (_server.HasExited)
                    throw new InvalidOperationException($"LocalStudio managed Ollama exited with code {_server.ExitCode}.");
            }
            throw new TimeoutException("LocalStudio managed Ollama did not become ready.");
        }
        finally
        {
            _serverGate.Release();
        }
    }

    public async Task PullModelAsync(
        string model,
        IProgress<SetupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!SettingsStore.IsAllowedModel(model))
            throw new InvalidDataException("The requested model is not allowlisted.");
        if (!ManagedModelManager.IsPinnedModel(model))
            throw new RuntimeProvisioningException(
                RuntimeFailureKind.InvalidSource,
                "The model manifest digest is not pinned. No model download will be started.");

        var installed = await GetInstalledModelsAsync(cancellationToken);
        if (ModelSelectionPolicy.FindCompatible(model, installed) is not null)
        {
            progress?.Report(new("model", 1, "The verified model is already installed."));
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ApiBaseUri, "api/pull"))
        {
            Content = JsonContent.Create(new { model, stream = true })
        };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken));
        var clock = Stopwatch.StartNew();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            using var item = JsonDocument.Parse(line);
            var root = item.RootElement;
            var status = root.TryGetProperty("status", out var statusNode) ? statusNode.GetString() ?? "Downloading" : "Downloading";
            var completed = root.TryGetProperty("completed", out var completedNode) ? completedNode.GetInt64() : 0;
            var total = root.TryGetProperty("total", out var totalNode) ? totalNode.GetInt64() : 0;
            var speed = clock.Elapsed.TotalSeconds > 0.5 && completed > 0 ? completed / clock.Elapsed.TotalSeconds : 0;
            TimeSpan? remaining = total > completed && speed > 0 ? TimeSpan.FromSeconds((total - completed) / speed) : null;
            progress?.Report(new SetupProgress(
                "model",
                total > 0 ? (double)completed / total : null,
                status,
                completed,
                total > 0 ? total : null,
                speed > 0 ? speed : null,
                remaining));
        }
    }

    /// <summary>
    /// Asks the owned server to unload every loaded model (keep_alive 0). A model that is still answering a request
    /// is released as soon as that request completes. The user's own Ollama is never contacted.
    /// </summary>
    public async Task UnloadModelsAsync(CancellationToken cancellationToken = default)
    {
        var apiBase = _apiBaseUri;
        if (_server is not { HasExited: false } || apiBase is null) return;
        try
        {
            using var response = await _http.GetAsync(new Uri(apiBase, "api/ps"), cancellationToken);
            if (!response.IsSuccessStatusCode) return;
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!json.RootElement.TryGetProperty("models", out var models)) return;
            foreach (var item in models.EnumerateArray())
            {
                if (!item.TryGetProperty("name", out var name) || name.GetString() is not { Length: > 0 } model) continue;
                using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(apiBase, "api/generate"))
                {
                    Content = JsonContent.Create(new { model, keep_alive = 0 })
                };
                using var unload = await _http.SendAsync(request, cancellationToken);
            }
        }
        catch (HttpRequestException) { }
        catch (JsonException) { }
    }

    private async Task<bool> ProbeOwnedServerAsync(CancellationToken cancellationToken)
    {
        if (_server is null || _server.HasExited || _apiBaseUri is null) return false;
        try
        {
            using var probe = await _http.GetAsync(new Uri(_apiBaseUri, "api/tags"), cancellationToken);
            return probe.IsSuccessStatusCode && _server is { HasExited: false };
        }
        catch (HttpRequestException) { return false; }
    }

    /// <summary>
    /// `ollama --version` also queries a server at OLLAMA_HOST (Ollama's default local port when unset),
    /// which may be the user's own Ollama. Point it at an unused loopback port so the check never contacts it.
    /// </summary>
    public static ExecutableRuntimeSmokeVerifier CreateIsolatedVersionVerifier(Func<string, string>? executableResolver = null) =>
        new(
            ["--version"],
            executableResolver: executableResolver,
            environment: new Dictionary<string, string?>
            {
                ["OLLAMA_HOST"] = $"127.0.0.1:{FindAvailableLoopbackPort().ToString(CultureInfo.InvariantCulture)}"
            });

    private static int FindAvailableLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private void DisposeOwnedServer()
    {
        _serverJob?.Dispose();
        _serverJob = null;
        if (_server is { HasExited: false }) _server.Kill(true);
        _server?.Dispose();
        _server = null;
        _apiBaseUri = null;
    }

    private static void RejectElevation()
    {
        if (UserSpaceGuard.IsElevated())
            throw new InvalidOperationException("LocalStudio runtime operations must not run elevated.");
    }

    public ValueTask DisposeAsync()
    {
        DisposeOwnedServer();
        _serverGate.Dispose();
        if (_ownsHttpClient) _http.Dispose();
        return ValueTask.CompletedTask;
    }
}
