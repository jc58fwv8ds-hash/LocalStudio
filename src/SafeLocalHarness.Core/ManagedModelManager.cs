using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SafeLocalHarness.Core;

public sealed record ManagedModelCandidate(
    string ProfileId,
    string Model,
    string Quantization,
    long ApproximateDownloadBytes,
    string OfficialSource,
    string License,
    RuntimeResourceBand MinimumBand,
    int MaximumContextTokens,
    string? ExpectedManifestSha256);

public sealed record ModelBenchmarkResult(
    string Model,
    TimeSpan LoadTime,
    TimeSpan TimeToFirstToken,
    int PromptEvaluationCount,
    TimeSpan PromptEvaluationDuration,
    int DecodeCount,
    TimeSpan DecodeDuration,
    double DecodeTokensPerSecond,
    long PeakWorkingSetBytes,
    long AvailableMemoryBeforeBytes,
    long AvailableMemoryAfterBytes,
    double? PeakCpuPercent,
    bool Responsive,
    string Summary);

public sealed record LocalModelProfile(
    int SchemaVersion,
    string ProfileId,
    string FastModel,
    string MainModel,
    int ContextBudget,
    string ThinkingPolicy,
    string KeepAlive,
    int HeavyModelConcurrency,
    string License,
    DateTimeOffset VerifiedAt,
    ModelBenchmarkResult Benchmark,
    IReadOnlyList<string> VerifiedBlobSha256);

public interface IModelBenchmarkRunner
{
    Task<ModelBenchmarkResult> RunAsync(string model, int contextTokens, CancellationToken cancellationToken = default);
}

public sealed class OllamaModelBenchmarkRunner(ILocalModelRuntime runtime, HttpClient? http = null) : IModelBenchmarkRunner, IDisposable
{
    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    private readonly bool _ownsClient = http is null;

    public async Task<ModelBenchmarkResult> RunAsync(string model, int contextTokens, CancellationToken cancellationToken = default)
    {
        await runtime.EnsureServerAsync(cancellationToken);
        var availableBefore = SystemHardwareProbe.CaptureMemoryAvailability();
        var started = Stopwatch.StartNew();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(runtime.ApiBaseUri, "api/generate"))
        {
            Content = JsonContent.Create(ModelRequestPolicy.Prepare(new
            {
                model,
                prompt = "Return exactly the word READY.",
                stream = true,
                options = new { num_ctx = Math.Min(contextTokens, 4096), num_predict = 16, temperature = 0 }
            }))
        };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken));
        TimeSpan? firstToken = null;
        JsonElement final = default;
        long peakWorkingSet = 0;
        var previousCpu = TimeSpan.Zero;
        var previousAt = DateTime.UtcNow;
        double peakCpu = 0;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0) continue;
            using var item = JsonDocument.Parse(line);
            var root = item.RootElement;
            if (firstToken is null && root.TryGetProperty("response", out var token) && !string.IsNullOrEmpty(token.GetString()))
                firstToken = started.Elapsed;
            // Measure the server and the model runner together; the runner holds the model memory.
            var owned = runtime.OwnedProcessIds;
            if (owned.Count > 0)
            {
                var (workingSet, processorTime) = OwnedProcessMetrics.Measure(owned);
                peakWorkingSet = Math.Max(peakWorkingSet, workingSet);
                var now = DateTime.UtcNow;
                var elapsed = (now - previousAt).TotalSeconds;
                if (elapsed > 0 && previousCpu != TimeSpan.Zero && processorTime >= previousCpu)
                    peakCpu = Math.Max(peakCpu, (processorTime - previousCpu).TotalSeconds / elapsed / Environment.ProcessorCount * 100);
                previousCpu = processorTime;
                previousAt = now;
            }
            if (root.TryGetProperty("done", out var done) && done.GetBoolean()) final = root.Clone();
        }
        var availableAfter = SystemHardwareProbe.CaptureMemoryAvailability();
        var load = Duration(final, "load_duration");
        var promptDuration = Duration(final, "prompt_eval_duration");
        var decodeDuration = Duration(final, "eval_duration");
        var promptCount = Count(final, "prompt_eval_count");
        var decodeCount = Count(final, "eval_count");
        var decodeRate = decodeDuration.TotalSeconds > 0 ? decodeCount / decodeDuration.TotalSeconds : 0;
        var ttft = firstToken ?? started.Elapsed;
        var responsive = ttft <= TimeSpan.FromSeconds(30) && decodeRate >= 0.5 && availableAfter > 512L * 1024 * 1024;
        return new(model, load, ttft, promptCount, promptDuration, decodeCount, decodeDuration, decodeRate,
            peakWorkingSet, availableBefore, availableAfter, peakCpu > 0 ? peakCpu : null, responsive,
            responsive ? "Benchmark passed." : "Benchmark requires a lighter profile.");
    }

    private static TimeSpan Duration(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var item)
            ? TimeSpan.FromTicks(item.GetInt64() / 100)
            : TimeSpan.Zero;

    private static int Count(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var item) ? item.GetInt32() : 0;

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }
}

public sealed class ManagedModelManager
{
    private readonly AppPaths _paths;
    private readonly ILocalModelRuntime _runtime;
    private readonly IModelBenchmarkRunner _benchmark;

    public ManagedModelManager(AppPaths paths, ILocalModelRuntime runtime, IModelBenchmarkRunner benchmark)
    {
        _paths = paths;
        _runtime = runtime;
        _benchmark = benchmark;
    }

    public static IReadOnlyList<ManagedModelCandidate> DefaultCandidates { get; } =
    [
        // Pinned 2026-10-06 after user review (reports/p1c2-20261001/official-values-20261006/OFFICIAL_VALUES.md):
        // manifest SHA-256 and exact size (config + 4 layers) from registry.ollama.ai; licence Apache-2.0.
        new("fast-qwen25-7b-q3", "qwen2.5-coder:7b-instruct-q3_K_S", "Q3_K_S", 3_492_382_409, "https://ollama.com/library/qwen2.5-coder:7b-instruct-q3_K_S", "Apache-2.0", RuntimeResourceBand.Lightweight, 4096,
            "748645786a08b8f96c5e5723b85e5858c2a8b28334c8c54b19325705847af993"),
        // Pinned 2026-10-09 after user approval (reports/phase3-design-20261007/official-values-20261009/OFFICIAL_VALUES.md):
        // manifest SHA-256 and exact total size from registry.ollama.ai; Apache-2.0. Qwen3.5 includes an image projector.
        new("small-qwen35-2b-q4", "qwen3.5:2b-q4_K_M", "Q4_K_M", 1_945_781_358, "https://ollama.com/library/qwen3.5:2b-q4_K_M", "Apache-2.0", RuntimeResourceBand.Lightweight, 8192,
            "1e2d21a4f03ab9e4a9c1976a4b7e88f21de61d65f2c5485d8fde8942be73f1e7"),
        new("small-qwen35-4b-q4", "qwen3.5:4b-q4_K_M", "Q4_K_M", 3_324_173_934, "https://ollama.com/library/qwen3.5:4b-q4_K_M", "Apache-2.0", RuntimeResourceBand.Lightweight, 8192,
            "d8b0f5e9760cd1682034f292d7ef72ec46f432149be0df7574bf2d6e92e38c04"),
        new("tiny-qwen25-1_5b-q4", "qwen2.5-coder:1.5b-instruct-q4_K_M", "Q4_K_M", 986_062_089, "https://ollama.com/library/qwen2.5-coder:1.5b-instruct-q4_K_M", "Apache-2.0", RuntimeResourceBand.Lightweight, 8192,
            "d7372fd828518a4d38b1eb196c673c31a85f2ed302b3d1e406c4c2d1b64a0668"),
        // Not reviewed: stays fail-closed (never downloaded) until its manifest digest is reviewed and pinned.
        new("main-qwen25-14b-q4", "qwen2.5-coder:14b-instruct-q4_K_M", "Q4_K_M", 9_000_000_000, "https://ollama.com/library/qwen2.5-coder:14b-instruct-q4_K_M", "Apache-2.0", RuntimeResourceBand.HighPerformance, 16384, null)
    ];

    public static bool IsPinnedModel(string model) => DefaultCandidates.Any(candidate =>
        candidate.Model.Equals(model, StringComparison.OrdinalIgnoreCase) &&
        IsSha256(candidate.ExpectedManifestSha256));

    public static ManagedModelCandidate? GetPinnedCandidate(string model) => DefaultCandidates.FirstOrDefault(candidate =>
        candidate.Model.Equals(model, StringComparison.OrdinalIgnoreCase) &&
        IsSha256(candidate.ExpectedManifestSha256));

    public async Task<LocalModelProfile?> PrepareAsync(
        HardwareSnapshot hardware,
        RuntimeResourcePlan resourcePlan,
        IEnumerable<ManagedModelCandidate>? candidates = null,
        IProgress<SetupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (resourcePlan.Band == RuntimeResourceBand.DeterministicOnly || resourcePlan.RecommendedModel is null) return null;
        if (UserSpaceGuard.IsElevated())
            throw new RuntimeProvisioningException(RuntimeFailureKind.EnvironmentRestriction, "Model provisioning never runs elevated.");
        var eligible = (candidates ?? DefaultCandidates)
            .Where(candidate => candidate.MinimumBand <= resourcePlan.Band)
            .Where(candidate => candidate.ApproximateDownloadBytes * 2 < hardware.FreeDiskBytes)
            .Where(candidate => IsSha256(candidate.ExpectedManifestSha256))
            .OrderByDescending(candidate => candidate.MinimumBand)
            .ThenBy(candidate => candidate.ApproximateDownloadBytes)
            .ToArray();
        if (eligible.Length == 0) return null;

        // Resource policy is a ceiling. Benchmarks may downgrade, never silently upgrade.
        var planned = eligible.FirstOrDefault(candidate => candidate.Model.Equals(resourcePlan.RecommendedModel, StringComparison.OrdinalIgnoreCase))
            ?? eligible.OrderBy(candidate => candidate.ApproximateDownloadBytes).First();
        await _runtime.PullModelAsync(planned.Model, progress, cancellationToken);
        var verified = await VerifyOllamaManifestBlobsAsync(planned.Model, planned.ExpectedManifestSha256!, cancellationToken);
        var benchmark = await _benchmark.RunAsync(planned.Model, Math.Min(resourcePlan.ContextTokens, planned.MaximumContextTokens), cancellationToken);
        if (!benchmark.Responsive)
        {
            var lighter = eligible.Where(candidate => candidate.ApproximateDownloadBytes < planned.ApproximateDownloadBytes)
                .OrderBy(candidate => candidate.ApproximateDownloadBytes).FirstOrDefault();
            if (lighter is null) return null;
            await _runtime.PullModelAsync(lighter.Model, progress, cancellationToken);
            verified = await VerifyOllamaManifestBlobsAsync(lighter.Model, lighter.ExpectedManifestSha256!, cancellationToken);
            benchmark = await _benchmark.RunAsync(lighter.Model, Math.Min(resourcePlan.ContextTokens, lighter.MaximumContextTokens), cancellationToken);
            if (!benchmark.Responsive) return null;
            planned = lighter;
        }

        var profile = new LocalModelProfile(1, planned.ProfileId, planned.Model, planned.Model,
            Math.Min(resourcePlan.ContextTokens, planned.MaximumContextTokens),
            resourcePlan.Band == RuntimeResourceBand.HighPerformance ? "adaptive" : "concise",
            resourcePlan.OllamaKeepAlive, Math.Min(1, resourcePlan.HeavyModelConcurrency), planned.License,
            DateTimeOffset.UtcNow, benchmark, verified);
        await SaveProfileAsync(profile, cancellationToken);
        return profile;
    }

    // The only other way a model is fetched: a pinned candidate, verified against its reviewed manifest SHA-256.
    public async Task<IReadOnlyList<string>> PullPinnedAsync(string model, IProgress<SetupProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var candidate = GetPinnedCandidate(model)
            ?? throw new InvalidOperationException("Only pinned models can be downloaded: " + model);
        if (UserSpaceGuard.IsElevated())
            throw new RuntimeProvisioningException(RuntimeFailureKind.EnvironmentRestriction, "Model provisioning never runs elevated.");
        await _runtime.PullModelAsync(candidate.Model, progress, cancellationToken);
        return await VerifyOllamaManifestBlobsAsync(candidate.Model, candidate.ExpectedManifestSha256!, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> VerifyOllamaManifestBlobsAsync(
        string model,
        string expectedManifestSha256,
        CancellationToken cancellationToken = default)
    {
        if (!IsSha256(expectedManifestSha256))
            throw new RuntimeProvisioningException(RuntimeFailureKind.InvalidSource, "A pinned model manifest SHA-256 is required.");
        _paths.EnsureCreated();
        var parts = model.Split(':', 2);
        var name = parts[0];
        var tag = parts.Length == 2 ? parts[1] : "latest";
        var manifest = Path.Combine(_paths.ModelRoot, "manifests", "registry.ollama.ai", "library", name, tag);
        UserSpaceGuard.EnsureAllowed(manifest);
        if (!File.Exists(manifest))
            throw new RuntimeProvisioningException(RuntimeFailureKind.ChecksumMismatch, "The downloaded model manifest is missing.");
        var manifestBytes = await File.ReadAllBytesAsync(manifest, cancellationToken);
        var actualManifestSha256 = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
        if (!actualManifestSha256.Equals(expectedManifestSha256, StringComparison.OrdinalIgnoreCase))
            throw new RuntimeProvisioningException(RuntimeFailureKind.ChecksumMismatch, "The model manifest failed pinned SHA-256 verification.");
        using var json = JsonDocument.Parse(manifestBytes);
        var digests = new List<string>();
        CollectDigest(json.RootElement, "config", digests);
        if (json.RootElement.TryGetProperty("layers", out var layers))
            foreach (var layer in layers.EnumerateArray())
                if (layer.TryGetProperty("digest", out var digest) && digest.GetString() is { } value) digests.Add(value);
        var verified = new List<string>();
        foreach (var digest in digests.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) || digest.Length != 71)
                throw new RuntimeProvisioningException(RuntimeFailureKind.ChecksumMismatch, "The model manifest contains an unsupported digest.");
            var expected = digest[7..].ToLowerInvariant();
            var blob = Path.Combine(_paths.ModelRoot, "blobs", "sha256-" + expected);
            UserSpaceGuard.EnsureAllowed(blob);
            if (!File.Exists(blob)) throw new RuntimeProvisioningException(RuntimeFailureKind.ChecksumMismatch, "A model blob is missing.");
            await using var stream = File.OpenRead(blob);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            if (!actual.Equals(expected, StringComparison.Ordinal))
                throw new RuntimeProvisioningException(RuntimeFailureKind.ChecksumMismatch, "A model blob failed SHA-256 verification.");
            verified.Add(expected);
        }
        if (verified.Count == 0)
            throw new RuntimeProvisioningException(RuntimeFailureKind.ChecksumMismatch, "No model blobs were declared by the manifest.");
        return verified;
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static void CollectDigest(JsonElement root, string property, List<string> target)
    {
        if (root.TryGetProperty(property, out var value) && value.TryGetProperty("digest", out var digest) && digest.GetString() is { } text)
            target.Add(text);
    }

    private async Task SaveProfileAsync(LocalModelProfile profile, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_paths.StateRoot, "model-profile.json");
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false), cancellationToken);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
