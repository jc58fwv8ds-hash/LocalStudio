using System.Text.Json;

namespace SafeLocalHarness.Core;

public sealed class DevelopmentUnitStaticVerifier : IDevelopmentUnitVerifier
{
    private static readonly IReadOnlySet<string> SourceExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".json", ".xaml", ".html", ".htm", ".css", ".js", ".mjs", ".cjs",
        ".ts", ".tsx", ".jsx", ".svg", ".toml", ".rs", ".sql", ".geojson"
    };

    private static readonly string[] ForbiddenCSharpTokens =
    [
        "Verb = \"runas\"",
        "Verb=\"runas\"",
        "UseShellExecute = true",
        "UseShellExecute=true",
        "cmd.exe",
        "powershell.exe",
        "Process.Start(",
        "Environment.SpecialFolder.Windows",
        "Environment.SpecialFolder.System",
        "HKEY_LOCAL_MACHINE"
    ];

    private static readonly string[] ForbiddenCrossStackTokens =
    [
        "node:child_process",
        "require('child_process')",
        "require(\"child_process\")",
        "std::process::Command",
        "Command::new(\"cmd",
        "Command::new(\"powershell",
        "requestedExecutionLevel level=\"requireAdministrator\""
    ];

    public async Task<DevelopmentUnitVerification> VerifyAsync(
        DevelopmentUnitPlan plan,
        DevelopmentUnit unit,
        string candidateRoot,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(candidateRoot);
        var metadataPath = SafePath(root, "artifact.json");
        if (!File.Exists(metadataPath)) return new(false, "artifact.metadata-missing");
        DevelopmentUnitArtifact? artifact;
        try
        {
            artifact = JsonSerializer.Deserialize<DevelopmentUnitArtifact>(
                await File.ReadAllTextAsync(metadataPath, cancellationToken),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return new(false, "artifact.metadata-invalid");
        }
        if (artifact is null) return new(false, "artifact.metadata-invalid");
        var policy = DevelopmentUnitArtifactPolicy.Validate(unit, artifact);
        if (!policy.Passed) return new(false, string.Join(", ", policy.Errors));

        var errors = new List<string>();
        var hasProductionSource = false;
        var hasTestSource = false;
        foreach (var file in artifact.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = DevelopmentUnitArtifactPolicy.NormalizeRelativePath(file.Path)!;
            var storedPath = SafePath(root, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(storedPath))
            {
                errors.Add("artifact.file-missing:" + relative);
                continue;
            }
            var stored = await File.ReadAllTextAsync(storedPath, cancellationToken);
            if (!stored.Equals(file.Content, StringComparison.Ordinal)) errors.Add("artifact.file-mismatch:" + relative);
            var extension = Path.GetExtension(relative);
            if (!SourceExtensions.Contains(extension)) continue;
            if (relative.StartsWith("tests/", StringComparison.OrdinalIgnoreCase) ||
                relative.Contains(".test.", StringComparison.OrdinalIgnoreCase) ||
                relative.Contains(".spec.", StringComparison.OrdinalIgnoreCase)) hasTestSource = true;
            else hasProductionSource = true;
            foreach (var token in ForbiddenCrossStackTokens)
                if (stored.Contains(token, StringComparison.OrdinalIgnoreCase))
                    errors.Add("source.forbidden-token:" + token);
            if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase))
                foreach (var token in ForbiddenCSharpTokens)
                    if (stored.Contains(token, StringComparison.OrdinalIgnoreCase))
                        errors.Add("source.forbidden-token:" + token);
            ValidateLayerSource(unit.Layer, stored, errors);
        }

        if (!hasProductionSource) errors.Add("source.production-missing");
        if (unit.VerificationKind is "unit-test" or "integration-test" && !hasTestSource)
            errors.Add("source.test-missing");
        if (unit.VerificationKind == "manual-spike")
            errors.Add("verification.manual-spike-required");

        return new(
            errors.Count == 0,
            errors.Count == 0 ? "static-contract-pass" : string.Join(", ", errors.Distinct(StringComparer.Ordinal).Take(12)));
    }

    private static void ValidateLayerSource(string layer, string source, ICollection<string> errors)
    {
        if (layer.Equals("Domain", StringComparison.OrdinalIgnoreCase))
        {
            var forbidden = new[] { "System.Windows", "UIAutomation", "Infrastructure", "Presentation", "Microsoft.Win32" };
            foreach (var token in forbidden)
                if (source.Contains(token, StringComparison.Ordinal)) errors.Add("layer.domain-dependency:" + token);
        }
        else if (layer.Equals("Application", StringComparison.OrdinalIgnoreCase))
        {
            var forbidden = new[] { "System.Windows.Forms", "System.Windows.Automation", "Presentation" };
            foreach (var token in forbidden)
                if (source.Contains(token, StringComparison.Ordinal)) errors.Add("layer.application-dependency:" + token);
        }
    }

    private static string SafePath(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("検証対象が候補領域を外れています。");
        return fullPath;
    }
}
