using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SafeLocalHarness.Core;

public enum RuntimeArtifactKind
{
    DotNetRuntime,
    DotNetSdk,
    Node,
    Ollama,
    Model,
    OptionalToolchain
}

public enum RuntimeArtifactFormat
{
    Zip,
    File
}

public enum RuntimeArtifactStatus
{
    Missing,
    Downloading,
    Verifying,
    Ready,
    Failed,
    RollbackAvailable
}

public enum RuntimeFailureKind
{
    None,
    Dns,
    Proxy,
    Tls,
    Http,
    AccessDenied,
    InsufficientDisk,
    ChecksumMismatch,
    InvalidSource,
    InvalidArchive,
    SmokeFailure,
    Interrupted,
    EnvironmentRestriction,
    Unknown
}

public sealed record RuntimeArtifact(
    string Product,
    RuntimeArtifactKind Kind,
    string Version,
    string Architecture,
    Uri OfficialSource,
    string ExpectedSha256,
    string License,
    RuntimeArtifactFormat Format,
    string FileName,
    string? ExecutableRelativePath = null,
    long? ExpectedSizeBytes = null,
    string? SignatureInformation = null);

public sealed record RuntimeArtifactReceipt(
    int SchemaVersion,
    string Product,
    RuntimeArtifactKind Kind,
    string Version,
    string Architecture,
    string OfficialSource,
    string ExpectedSha256,
    string ActualSha256,
    string License,
    string LocalPath,
    RuntimeArtifactStatus Status,
    DateTimeOffset? PromotedAt,
    string? RollbackPath,
    string? SignatureInformation,
    RuntimeFailureKind FailureKind = RuntimeFailureKind.None,
    string? FailureMessage = null);

public sealed record RuntimeProvisioningResult(
    bool Passed,
    RuntimeArtifactReceipt Receipt,
    RuntimeFailureKind FailureKind,
    string Summary,
    RuntimeProcessTelemetry? SmokeTelemetry = null);

public sealed record RuntimeDownloadProgress(
    long CompletedBytes,
    long? TotalBytes,
    double? BytesPerSecond,
    TimeSpan? EstimatedRemaining,
    bool Resumed);

public sealed record RuntimeDownloadResult(long BytesWritten, bool Resumed, Uri FinalSource);

public sealed record RuntimeProcessTelemetry(
    string Executable,
    DateTimeOffset StartedAt,
    int ExitCode,
    long PeakWorkingSetBytes,
    long AvailableMemoryBytes,
    int RetryCount,
    bool CleanupComplete,
    TimeSpan Duration);

public interface IRuntimeSourcePolicy
{
    void EnsureAllowed(RuntimeArtifact artifact, Uri source);
}

public sealed class OfficialRuntimeSourcePolicy : IRuntimeSourcePolicy
{
    private static readonly IReadOnlyDictionary<RuntimeArtifactKind, HashSet<string>> AllowedHosts =
        new Dictionary<RuntimeArtifactKind, HashSet<string>>
        {
            [RuntimeArtifactKind.DotNetRuntime] = new(StringComparer.OrdinalIgnoreCase)
            {
                "builds.dotnet.microsoft.com", "dotnetcli.azureedge.net", "download.visualstudio.microsoft.com"
            },
            [RuntimeArtifactKind.DotNetSdk] = new(StringComparer.OrdinalIgnoreCase)
            {
                "builds.dotnet.microsoft.com", "dotnetcli.azureedge.net", "download.visualstudio.microsoft.com"
            },
            [RuntimeArtifactKind.Node] = new(StringComparer.OrdinalIgnoreCase) { "nodejs.org" },
            [RuntimeArtifactKind.Ollama] = new(StringComparer.OrdinalIgnoreCase)
            {
                "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com"
            },
            [RuntimeArtifactKind.Model] = new(StringComparer.OrdinalIgnoreCase)
            {
                "registry.ollama.ai", "ollama.com"
            },
            [RuntimeArtifactKind.OptionalToolchain] = new(StringComparer.OrdinalIgnoreCase)
        };

    public void EnsureAllowed(RuntimeArtifact artifact, Uri source)
    {
        if (!source.IsAbsoluteUri || source.Scheme != Uri.UriSchemeHttps)
            throw new RuntimeProvisioningException(RuntimeFailureKind.InvalidSource, "Runtime artifacts require an absolute HTTPS source.");
        if (!AllowedHosts.TryGetValue(artifact.Kind, out var hosts) || !hosts.Contains(source.IdnHost))
            throw new RuntimeProvisioningException(RuntimeFailureKind.InvalidSource, $"The source host is not allowlisted for {artifact.Kind}: {source.IdnHost}");
    }
}

public interface IRuntimeArtifactDownloader
{
    Task<RuntimeDownloadResult> DownloadAsync(
        RuntimeArtifact artifact,
        string partialPath,
        IProgress<RuntimeDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class HttpRuntimeArtifactDownloader : IRuntimeArtifactDownloader, IDisposable
{
    private readonly HttpClient _http;
    private readonly IRuntimeSourcePolicy _sourcePolicy;
    private readonly bool _ownsClient;

    public HttpRuntimeArtifactDownloader(IRuntimeSourcePolicy sourcePolicy, HttpClient? http = null)
    {
        _sourcePolicy = sourcePolicy;
        _http = http ?? new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(30)
        }) { Timeout = TimeSpan.FromHours(2) };
        _ownsClient = http is null;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("LocalStudio-Bootstrap/1.0");
    }

    public async Task<RuntimeDownloadResult> DownloadAsync(
        RuntimeArtifact artifact,
        string partialPath,
        IProgress<RuntimeDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        _sourcePolicy.EnsureAllowed(artifact, artifact.OfficialSource);
        var existing = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, artifact.OfficialSource);
        if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var finalSource = response.RequestMessage?.RequestUri ?? artifact.OfficialSource;
        _sourcePolicy.EnsureAllowed(artifact, finalSource);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            File.Delete(partialPath);
            return await DownloadAsync(artifact, partialPath, progress, cancellationToken);
        }
        response.EnsureSuccessStatusCode();
        var resumed = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!resumed) existing = 0;
        var contentLength = response.Content.Headers.ContentLength;
        var total = contentLength is null ? artifact.ExpectedSizeBytes : existing + contentLength.Value;
        var mode = resumed ? FileMode.Append : FileMode.Create;
        Directory.CreateDirectory(Path.GetDirectoryName(partialPath)!);
        await using var destination = new FileStream(partialPath, mode, FileAccess.Write, FileShare.None, 256 * 1024, true);
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[256 * 1024];
        var clock = Stopwatch.StartNew();
        var received = existing;
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            received += count;
            var speed = clock.Elapsed.TotalSeconds >= 0.25 ? (received - existing) / clock.Elapsed.TotalSeconds : 0;
            TimeSpan? remaining = total > received && speed > 0 ? TimeSpan.FromSeconds((total.Value - received) / speed) : null;
            progress?.Report(new(received, total, speed > 0 ? speed : null, remaining, resumed));
        }
        await destination.FlushAsync(cancellationToken);
        return new(received, resumed, finalSource);
    }

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }
}

public interface IRuntimeStorageProbe
{
    long GetAvailableBytes(string path);
}

public sealed class RuntimeStorageProbe : IRuntimeStorageProbe
{
    public long GetAvailableBytes(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path)) ?? throw new IOException("Storage root is unavailable.");
        return new DriveInfo(root).AvailableFreeSpace;
    }
}

public interface IRuntimeSmokeVerifier
{
    Task<RuntimeProcessTelemetry> VerifyAsync(RuntimeArtifact artifact, string candidateContentRoot, CancellationToken cancellationToken = default);
}

public sealed class ExecutableRuntimeSmokeVerifier(
    IReadOnlyList<string> arguments,
    TimeSpan? timeout = null,
    Func<string, string>? executableResolver = null,
    IReadOnlyDictionary<string, string?>? environment = null) : IRuntimeSmokeVerifier
{
    public IReadOnlyDictionary<string, string?> EnvironmentOverrides { get; } = environment ?? new Dictionary<string, string?>();

    public async Task<RuntimeProcessTelemetry> VerifyAsync(RuntimeArtifact artifact, string candidateContentRoot, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(artifact.ExecutableRelativePath))
            throw new RuntimeProvisioningException(RuntimeFailureKind.SmokeFailure, "No smoke-test executable is declared.");
        var executable = executableResolver?.Invoke(candidateContentRoot)
            ?? Path.GetFullPath(Path.Combine(candidateContentRoot, artifact.ExecutableRelativePath));
        var contentPrefix = Path.GetFullPath(candidateContentRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!executable.StartsWith(contentPrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(executable))
            throw new RuntimeProvisioningException(RuntimeFailureKind.SmokeFailure, "The smoke-test executable is missing or outside the candidate.");

        var startedAt = DateTimeOffset.UtcNow;
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = candidateContentRoot
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var item in EnvironmentOverrides) start.Environment[item.Key] = item.Value;
        using var process = Process.Start(start)
            ?? throw new RuntimeProvisioningException(RuntimeFailureKind.SmokeFailure, "The runtime smoke process did not start.");
        WindowsProcessJob? job = null;
        try
        {
            // A version check can finish before it is attached or sampled (seen 2026-10-06: "Process has exited").
            // A process that already ended needs no containment, and an exit between checks simply ends sampling.
            if (OperatingSystem.IsWindows())
            {
                try { job = WindowsProcessJob.Attach(process); }
                catch (System.ComponentModel.Win32Exception) when (process.HasExited) { }
            }
            var peak = 0L;
            var limit = timeout ?? TimeSpan.FromSeconds(30);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(limit);
            try
            {
                while (!process.HasExited)
                {
                    try
                    {
                        process.Refresh();
                        peak = Math.Max(peak, process.WorkingSet64);
                    }
                    catch (InvalidOperationException) when (process.HasExited)
                    {
                        break;
                    }
                    await Task.Delay(100, deadline.Token);
                }
                await process.WaitForExitAsync(deadline.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (!process.HasExited) process.Kill(true);
                throw new RuntimeProvisioningException(RuntimeFailureKind.SmokeFailure, "The runtime smoke test timed out.");
            }
            var standardOutput = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardError = await process.StandardError.ReadToEndAsync(cancellationToken);
            if (process.ExitCode != 0)
                throw new RuntimeProvisioningException(RuntimeFailureKind.SmokeFailure, $"Runtime smoke failed with exit {process.ExitCode}: {standardError.Trim()}");
            if (string.IsNullOrWhiteSpace(standardOutput) && string.IsNullOrWhiteSpace(standardError))
                throw new RuntimeProvisioningException(RuntimeFailureKind.SmokeFailure, "Runtime smoke returned no version or status evidence.");
            var available = SystemHardwareProbe.CaptureMemoryAvailability();
            return new(executable, startedAt, process.ExitCode, peak, available, 0, process.HasExited, DateTimeOffset.UtcNow - startedAt);
        }
        finally
        {
            if (!process.HasExited) process.Kill(true);
            job?.Dispose();
        }
    }
}

public sealed class RuntimeArtifactStateStore(AppPaths paths)
{
    private static readonly SemaphoreSlim StateGate = new(1, 1);
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };
    private string StatePath => Path.Combine(paths.StateRoot, "runtime-artifacts.json");

    public async Task<IReadOnlyList<RuntimeArtifactReceipt>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await StateGate.WaitAsync(cancellationToken);
        try { return await LoadCoreAsync(cancellationToken); }
        finally { StateGate.Release(); }
    }

    private async Task<IReadOnlyList<RuntimeArtifactReceipt>> LoadCoreAsync(CancellationToken cancellationToken)
    {
        paths.EnsureCreated();
        if (!File.Exists(StatePath)) return [];
        await using var stream = File.OpenRead(StatePath);
        return await JsonSerializer.DeserializeAsync<List<RuntimeArtifactReceipt>>(stream, _json, cancellationToken) ?? [];
    }

    public async Task SaveAsync(RuntimeArtifactReceipt receipt, CancellationToken cancellationToken = default)
    {
        await StateGate.WaitAsync(cancellationToken);
        try
        {
            var items = (await LoadCoreAsync(cancellationToken)).ToList();
            items.RemoveAll(item => item.Product.Equals(receipt.Product, StringComparison.OrdinalIgnoreCase));
            items.Add(receipt);
            var temporary = StatePath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(items, _json), new UTF8Encoding(false), cancellationToken);
                File.Move(temporary, StatePath, true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        finally { StateGate.Release(); }
    }
}

public sealed class RuntimeArtifactManager
{
    private readonly AppPaths _paths;
    private readonly IRuntimeSourcePolicy _sourcePolicy;
    private readonly IRuntimeArtifactDownloader _downloader;
    private readonly IRuntimeStorageProbe _storage;
    private readonly RuntimeArtifactStateStore _state;

    public RuntimeArtifactManager(
        AppPaths paths,
        IRuntimeSourcePolicy? sourcePolicy = null,
        IRuntimeArtifactDownloader? downloader = null,
        IRuntimeStorageProbe? storage = null)
    {
        _paths = paths;
        _sourcePolicy = sourcePolicy ?? new OfficialRuntimeSourcePolicy();
        _downloader = downloader ?? new HttpRuntimeArtifactDownloader(_sourcePolicy);
        _storage = storage ?? new RuntimeStorageProbe();
        _state = new RuntimeArtifactStateStore(paths);
    }

    public async Task<RuntimeProvisioningResult> ProvisionAsync(
        RuntimeArtifact artifact,
        IRuntimeSmokeVerifier verifier,
        IProgress<RuntimeDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        RejectElevation();
        _paths.EnsureCreated();
        ValidateArtifact(artifact);
        _sourcePolicy.EnsureAllowed(artifact, artifact.OfficialSource);
        var product = SafeSegment(artifact.Product);
        var productRoot = Path.Combine(_paths.RuntimeRoot, product);
        var candidateRoot = Path.Combine(_paths.RuntimeRoot, ".candidates", product + "-" + Guid.NewGuid().ToString("N"));
        var partialRoot = Path.Combine(_paths.DownloadRoot, product);
        // The partial name is bound to the pinned version and hash, so a resume can never append to bytes of another release.
        var partial = Path.Combine(partialRoot, PartialFileName(artifact));
        var payload = Path.Combine(candidateRoot, artifact.FileName);
        var content = Path.Combine(candidateRoot, "content");
        var stable = Path.Combine(productRoot, "stable");
        var rollback = Path.Combine(productRoot, "rollback", DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff"));
        foreach (var path in new[] { productRoot, candidateRoot, partialRoot, partial, payload, content, stable, rollback })
            UserSpaceGuard.EnsureAllowed(path);

        var stableManifest = Path.Combine(stable, "runtime-artifact.json");
        if (File.Exists(stableManifest))
        {
            var existing = JsonSerializer.Deserialize<RuntimeArtifactReceipt>(await File.ReadAllTextAsync(stableManifest, cancellationToken));
            if (existing is not null &&
                existing.Product.Equals(artifact.Product, StringComparison.OrdinalIgnoreCase) &&
                existing.Version.Equals(artifact.Version, StringComparison.OrdinalIgnoreCase) &&
                existing.ActualSha256.Equals(artifact.ExpectedSha256, StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(Path.Combine(stable, "content")))
            {
                var telemetry = await verifier.VerifyAsync(artifact, Path.Combine(stable, "content"), cancellationToken);
                var ready = existing with { Status = Directory.Exists(Path.Combine(productRoot, "rollback")) ? RuntimeArtifactStatus.RollbackAvailable : RuntimeArtifactStatus.Ready };
                await _state.SaveAsync(ready, cancellationToken);
                return new(true, ready, RuntimeFailureKind.None, "Existing Stable runtime reverified.", telemetry);
            }
        }

        RuntimeArtifactReceipt receipt = Receipt(artifact, partial, RuntimeArtifactStatus.Downloading);
        await _state.SaveAsync(receipt, cancellationToken);
        try
        {
            var required = artifact.ExpectedSizeBytes is > 0 ? checked(artifact.ExpectedSizeBytes.Value * 2) : 256L * 1024 * 1024;
            if (_storage.GetAvailableBytes(_paths.Root) < required)
                throw new RuntimeProvisioningException(RuntimeFailureKind.InsufficientDisk, "There is not enough free disk space for candidate and rollback storage.");

            Directory.CreateDirectory(candidateRoot);
            Directory.CreateDirectory(partialRoot);
            foreach (var stale in Directory.EnumerateFiles(partialRoot, artifact.FileName + ".*.partial"))
                if (!stale.Equals(partial, StringComparison.OrdinalIgnoreCase)) SafeDeleteFile(stale);
            await _downloader.DownloadAsync(artifact, partial, progress, cancellationToken);
            File.Move(partial, payload, true);
            receipt = receipt with { Status = RuntimeArtifactStatus.Verifying, LocalPath = payload };
            await _state.SaveAsync(receipt, cancellationToken);

            var actual = await ComputeSha256Async(payload, cancellationToken);
            if (!actual.Equals(artifact.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new RuntimeProvisioningException(RuntimeFailureKind.ChecksumMismatch, $"SHA-256 mismatch for {artifact.Product}.");

            Directory.CreateDirectory(content);
            if (artifact.Format == RuntimeArtifactFormat.Zip) ExtractVerifiedZip(payload, content);
            else File.Copy(payload, Path.Combine(content, artifact.FileName), true);

            var telemetry = await verifier.VerifyAsync(artifact, content, cancellationToken);
            var metadata = receipt with
            {
                ActualSha256 = actual,
                LocalPath = Path.Combine(stable, "content"),
                Status = RuntimeArtifactStatus.Ready,
                PromotedAt = DateTimeOffset.UtcNow,
                RollbackPath = Directory.Exists(stable) ? rollback : null
            };
            await File.WriteAllTextAsync(
                Path.Combine(candidateRoot, "runtime-artifact.json"),
                JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false),
                cancellationToken);
            File.Delete(payload);
            Directory.CreateDirectory(productRoot);
            string? promotedRollback = null;
            if (Directory.Exists(stable))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(rollback)!);
                Directory.Move(stable, rollback);
                promotedRollback = rollback;
            }
            try
            {
                Directory.Move(candidateRoot, stable);
            }
            catch
            {
                if (promotedRollback is not null && Directory.Exists(promotedRollback) && !Directory.Exists(stable))
                    Directory.Move(promotedRollback, stable);
                throw;
            }
            metadata = metadata with
            {
                LocalPath = Path.Combine(stable, "content"),
                RollbackPath = promotedRollback,
                Status = promotedRollback is null ? RuntimeArtifactStatus.Ready : RuntimeArtifactStatus.RollbackAvailable
            };
            await _state.SaveAsync(metadata, cancellationToken);
            RemoveEmptyCandidatesRoot();
            return new(true, metadata, RuntimeFailureKind.None, "Runtime artifact verified and atomically promoted.", telemetry);
        }
        catch (OperationCanceledException)
        {
            receipt = receipt with { Status = RuntimeArtifactStatus.Failed, FailureKind = RuntimeFailureKind.Interrupted, FailureMessage = "The operation was interrupted; a valid partial download may be resumed." };
            await _state.SaveAsync(receipt, CancellationToken.None);
            SafeDeleteDirectory(candidateRoot);
            RemoveEmptyCandidatesRoot();
            return new(false, receipt, RuntimeFailureKind.Interrupted, receipt.FailureMessage);
        }
        catch (Exception exception)
        {
            var failure = RuntimeFailureClassifier.Classify(exception);
            // Transient network loss keeps the bytes already received; integrity and policy failures discard them.
            // Resumed bytes are never trusted: the whole payload is SHA-256 verified before promotion.
            var keepPartial = KeepsPartialForResume(failure) && File.Exists(partial) && new FileInfo(partial).Length > 0;
            if (!keepPartial) SafeDeleteFile(partial);
            SafeDeleteDirectory(candidateRoot);
            RemoveEmptyCandidatesRoot();
            var message = keepPartial ? exception.Message + " The received part is kept and the next attempt resumes from it." : exception.Message;
            receipt = receipt with { Status = RuntimeArtifactStatus.Failed, FailureKind = failure, FailureMessage = message };
            await _state.SaveAsync(receipt, CancellationToken.None);
            return new(false, receipt, failure, message);
        }
    }

    /// <summary>Removes the shared ".candidates" folder only when no candidate remains in it (never recursive).</summary>
    private void RemoveEmptyCandidatesRoot()
    {
        var candidates = Path.Combine(_paths.RuntimeRoot, ".candidates");
        try
        {
            if (Directory.Exists(candidates) && !Directory.EnumerateFileSystemEntries(candidates).Any())
                Directory.Delete(candidates, recursive: false);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static string PartialFileName(RuntimeArtifact artifact) =>
        $"{SafeSegment(artifact.FileName)}.{SafeSegment(artifact.Version)}.{artifact.ExpectedSha256[..16].ToLowerInvariant()}.partial";

    private static bool KeepsPartialForResume(RuntimeFailureKind failure) => failure is
        RuntimeFailureKind.Interrupted or RuntimeFailureKind.Dns or RuntimeFailureKind.Proxy or
        RuntimeFailureKind.Tls or RuntimeFailureKind.Http;

    public async Task<bool> RollbackAsync(string product, CancellationToken cancellationToken = default)
    {
        RejectElevation();
        var safe = SafeSegment(product);
        var productRoot = Path.Combine(_paths.RuntimeRoot, safe);
        var stable = Path.Combine(productRoot, "stable");
        var rollbackRoot = Path.Combine(productRoot, "rollback");
        UserSpaceGuard.EnsureAllowed(productRoot);
        if (!Directory.Exists(rollbackRoot)) return false;
        var previous = Directory.EnumerateDirectories(rollbackRoot).OrderByDescending(value => value, StringComparer.Ordinal).FirstOrDefault();
        if (previous is null) return false;
        var rejected = Path.Combine(productRoot, ".rejected-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(stable)) Directory.Move(stable, rejected);
        try
        {
            Directory.Move(previous, stable);
            SafeDeleteDirectory(rejected);
        }
        catch
        {
            if (Directory.Exists(rejected) && !Directory.Exists(stable)) Directory.Move(rejected, stable);
            throw;
        }
        var manifest = Path.Combine(stable, "runtime-artifact.json");
        if (File.Exists(manifest))
        {
            var restored = JsonSerializer.Deserialize<RuntimeArtifactReceipt>(await File.ReadAllTextAsync(manifest, cancellationToken));
            if (restored is not null) await _state.SaveAsync(restored with { Status = RuntimeArtifactStatus.Ready, RollbackPath = null }, cancellationToken);
        }
        return true;
    }

    private static RuntimeArtifactReceipt Receipt(RuntimeArtifact artifact, string path, RuntimeArtifactStatus status) =>
        new(1, artifact.Product, artifact.Kind, artifact.Version, artifact.Architecture, artifact.OfficialSource.AbsoluteUri,
            artifact.ExpectedSha256.ToLowerInvariant(), "", artifact.License, path, status, null, null, artifact.SignatureInformation);

    private static void ValidateArtifact(RuntimeArtifact artifact)
    {
        if (string.IsNullOrWhiteSpace(artifact.Product) || string.IsNullOrWhiteSpace(artifact.Version) || string.IsNullOrWhiteSpace(artifact.License))
            throw new RuntimeProvisioningException(RuntimeFailureKind.InvalidSource, "Product, version, and license metadata are required.");
        if (artifact.ExpectedSha256.Length != 64 || artifact.ExpectedSha256.Any(value => !Uri.IsHexDigit(value)))
            throw new RuntimeProvisioningException(RuntimeFailureKind.InvalidSource, "A pinned SHA-256 is required.");
        _ = SafeSegment(artifact.FileName);
    }

    private static string SafeSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || value.Contains('/') || value.Contains('\\'))
            throw new RuntimeProvisioningException(RuntimeFailureKind.InvalidSource, "An artifact path segment is invalid.");
        return value;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private static void ExtractVerifiedZip(string archive, string destination)
    {
        using var zip = ZipFile.OpenRead(archive);
        var prefix = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var entry in zip.Entries)
        {
            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixType == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new RuntimeProvisioningException(RuntimeFailureKind.InvalidArchive, "The archive contains a link or reparse-point entry.");
            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new RuntimeProvisioningException(RuntimeFailureKind.InvalidArchive, "The archive contains a path traversal entry.");
        }
        ZipFile.ExtractToDirectory(archive, destination, true);
        foreach (var extracted in Directory.EnumerateFileSystemEntries(destination, "*", SearchOption.AllDirectories))
            if (File.GetAttributes(extracted).HasFlag(FileAttributes.ReparsePoint))
                throw new RuntimeProvisioningException(RuntimeFailureKind.InvalidArchive, "The extracted runtime contains a reparse point.");
    }

    private static void SafeDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void SafeDeleteDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            var recursive = !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
            Directory.Delete(path, recursive);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void RejectElevation()
    {
        if (UserSpaceGuard.IsElevated())
            throw new RuntimeProvisioningException(RuntimeFailureKind.EnvironmentRestriction, "Runtime provisioning is disabled in an elevated process.");
    }
}

public static class RuntimeFailureClassifier
{
    public static RuntimeFailureKind Classify(Exception exception)
    {
        if (exception is RuntimeProvisioningException known) return known.Kind;
        if (exception is OperationCanceledException) return RuntimeFailureKind.Interrupted;
        if (exception is ResourcePressureException) return RuntimeFailureKind.EnvironmentRestriction;
        if (exception is UnauthorizedAccessException or SecurityException) return RuntimeFailureKind.AccessDenied;
        if (exception is AuthenticationException) return RuntimeFailureKind.Tls;
        if (exception is HttpRequestException http)
        {
            if (http.InnerException is AuthenticationException) return RuntimeFailureKind.Tls;
            if (http.InnerException is SocketException socket && socket.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain)
                return RuntimeFailureKind.Dns;
            if (http.StatusCode is HttpStatusCode.ProxyAuthenticationRequired) return RuntimeFailureKind.Proxy;
            if (http.Message.Contains("proxy", StringComparison.OrdinalIgnoreCase)) return RuntimeFailureKind.Proxy;
            return RuntimeFailureKind.Http;
        }
        // A connection dropped while reading the response body surfaces as an IOException, not HttpRequestException.
        if (exception is HttpIOException || exception is IOException { InnerException: SocketException })
            return RuntimeFailureKind.Http;
        if (exception is IOException io && (io.HResult == unchecked((int)0x80070070) || io.Message.Contains("disk", StringComparison.OrdinalIgnoreCase)))
            return RuntimeFailureKind.InsufficientDisk;
        if (exception is InvalidDataException) return RuntimeFailureKind.InvalidArchive;
        if (exception is Win32Exception) return RuntimeFailureKind.EnvironmentRestriction;
        return RuntimeFailureKind.Unknown;
    }
}

public sealed class RuntimeProvisioningException(RuntimeFailureKind kind, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public RuntimeFailureKind Kind { get; } = kind;
}
