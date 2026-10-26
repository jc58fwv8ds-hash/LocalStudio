namespace SafeLocalHarness.Core;

public sealed record RuntimeCatalogStatus(
    bool Configured,
    string Product,
    string Message,
    RuntimeArtifact? Artifact);

/// <summary>
/// Build-time pinned artifact table. Phase 1a intentionally ships with no entries until the
/// official version, exact download URL, size, license, and SHA-256 have been reviewed together.
/// Runtime code never resolves "latest" and never turns metadata fetched at startup into authority.
/// </summary>
public sealed class OfficialRuntimeCatalog : IDisposable
{
    // Pinned 2026-10-06 after user review (reports/p1c2-20261001/official-values-20261006/OFFICIAL_VALUES.md).
    // SHA-256 agreed between the release's sha256sum.txt and the GitHub API asset digest; size from the GitHub API.
    private static readonly IReadOnlyDictionary<string, RuntimeArtifact> PinnedArtifacts =
        new Dictionary<string, RuntimeArtifact>(StringComparer.OrdinalIgnoreCase)
        {
            ["ollama"] = new(
                "ollama",
                RuntimeArtifactKind.Ollama,
                "0.35.1",
                "amd64",
                new Uri("https://github.com/ollama/ollama/releases/download/v0.35.1/ollama-windows-amd64.zip"),
                "dc50b9ca7f9023c86525012632cd1615b093d0407987444a7f62ecab617e8e93",
                "MIT",
                RuntimeArtifactFormat.Zip,
                "ollama-windows-amd64.zip",
                "ollama.exe",
                1_471_094_402,
                "sha256sum.txt of release v0.35.1 and GitHub API asset digest (both reviewed 2026-10-06)")
        };

    private readonly IReadOnlyDictionary<string, RuntimeArtifact> _pinned;

    public OfficialRuntimeCatalog() : this(PinnedArtifacts) { }

    /// <summary>Test seam only: shipped builds always use the compiled <see cref="PinnedArtifacts"/> table.</summary>
    internal OfficialRuntimeCatalog(IReadOnlyDictionary<string, RuntimeArtifact> pinned) => _pinned = pinned;

    public RuntimeCatalogStatus GetOllamaStatus()
    {
        // Only the reviewed x64 build is pinned; other architectures stay unconfigured rather than running under emulation.
        if (System.Runtime.InteropServices.RuntimeInformation.OSArchitecture != System.Runtime.InteropServices.Architecture.X64)
            return new(false, "ollama", "Ollamaの固定値はx64版のみ設定済みです。このPCの種類向けの値は未設定のため、ダウンロードは行いません。", null);
        if (_pinned.TryGetValue("ollama", out var artifact))
            return new(true, "ollama", "Pinned official Ollama artifact is configured.", artifact);
        return new(false, "ollama", "Ollama download is not configured. No network request will be made.", null);
    }

    public Task<RuntimeArtifact> ResolveOllamaAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var status = GetOllamaStatus();
        return status.Artifact is not null
            ? Task.FromResult(status.Artifact)
            : Task.FromException<RuntimeArtifact>(new RuntimeProvisioningException(
                RuntimeFailureKind.InvalidSource,
                status.Message));
    }

    public Task<RuntimeArtifact> ResolveNodeAsync(string version, CancellationToken cancellationToken = default) =>
        Task.FromException<RuntimeArtifact>(new NotSupportedException("Managed Node/npm is deferred to Phase 1b."));

    public Task<RuntimeArtifact> ResolveDotNetSdkAsync(string channel = "10.0", CancellationToken cancellationToken = default) =>
        Task.FromException<RuntimeArtifact>(new NotSupportedException("Managed .NET SDK provisioning is deferred to Phase 1b."));

    public Task<RuntimeArtifact> ResolveDotNetDesktopRuntimeAsync(string channel = "10.0", CancellationToken cancellationToken = default) =>
        Task.FromException<RuntimeArtifact>(new NotSupportedException("Self-contained runtime-pack provisioning is deferred to Phase 1b."));

    public static string ParseChecksum(string text, string assetName)
    {
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[^1].TrimStart('*').Equals(assetName, StringComparison.OrdinalIgnoreCase))
                return parts[0].ToLowerInvariant();
        }
        throw new InvalidDataException($"No checksum was published for {assetName}.");
    }

    public void Dispose() { }
}
