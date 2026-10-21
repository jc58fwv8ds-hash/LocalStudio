using System.Security.Cryptography;
using System.Text.Json;

namespace SafeLocalHarness.Core;

public sealed record DevelopmentArtifactFile(string Path, string Content);

public sealed record DevelopmentUnitArtifact(
    int Schema,
    string UnitId,
    IReadOnlyList<DevelopmentArtifactFile> Files);

public sealed record DevelopmentUnitArtifactValidation(
    bool Passed,
    IReadOnlyList<string> Errors);

public static class DevelopmentUnitArtifactPolicy
{
    public const int MaxFilesPerUnit = 12;
    public const int MaxCharactersPerFile = 40_000;
    public const int MaxCharactersPerUnit = 120_000;

    private static readonly IReadOnlySet<string> AllowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".json", ".xaml", ".resx", ".manifest", ".md",
        ".html", ".htm", ".css", ".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx",
        ".svg", ".toml", ".rs", ".lock", ".sql", ".geojson"
    };

    private static readonly IReadOnlySet<string> ForbiddenSegments = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".localstudio", "bin", "obj"
    };

    public static DevelopmentUnitArtifactValidation Validate(
        DevelopmentUnit unit,
        DevelopmentUnitArtifact artifact)
    {
        var errors = new List<string>();
        if (artifact.Schema != 1) errors.Add("artifact.schema");
        if (!artifact.UnitId.Equals(unit.Id, StringComparison.Ordinal)) errors.Add("artifact.unit-id");
        if (artifact.Files.Count is < 1 or > MaxFilesPerUnit) errors.Add("artifact.file-count");

        var totalCharacters = 0;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in artifact.Files)
        {
            var normalized = NormalizeRelativePath(file.Path);
            if (normalized is null)
            {
                errors.Add("artifact.unsafe-path:" + file.Path);
                continue;
            }
            if (!paths.Add(normalized)) errors.Add("artifact.duplicate-path:" + normalized);
            if (!AllowedExtensions.Contains(Path.GetExtension(normalized)))
                errors.Add("artifact.extension:" + normalized);
            if (normalized.Split('/').Any(ForbiddenSegments.Contains))
                errors.Add("artifact.forbidden-segment:" + normalized);
            if (!IsLayerOwnedPath(unit.Layer, normalized))
                errors.Add("artifact.layer-boundary:" + normalized);
            if (string.IsNullOrWhiteSpace(file.Content) || file.Content.Length > MaxCharactersPerFile)
                errors.Add("artifact.file-size:" + normalized);
            totalCharacters += file.Content?.Length ?? 0;
        }
        if (totalCharacters > MaxCharactersPerUnit) errors.Add("artifact.total-size");
        return new(errors.Count == 0, errors.Distinct(StringComparer.Ordinal).ToArray());
    }

    public static string Fingerprint(DevelopmentUnitArtifact artifact)
    {
        var canonical = artifact with
        {
            Files = artifact.Files
                .OrderBy(file => NormalizeRelativePath(file.Path), StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(canonical))).ToLowerInvariant();
    }

    internal static string? NormalizeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains(':')) return null;
        var normalized = path.Replace('\\', '/').Trim('/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || segments.Any(segment => segment is "." or ".." || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            return null;
        return string.Join('/', segments);
    }

    private static bool IsLayerOwnedPath(string layer, string path)
    {
        if (path.StartsWith("tests/", StringComparison.OrdinalIgnoreCase)) return true;
        if (!path.StartsWith("src/", StringComparison.OrdinalIgnoreCase)) return false;
        var marker = layer switch
        {
            "Domain" => "Domain",
            "Application" => "Application",
            "Infrastructure" => "Infrastructure",
            "Presentation" => "Presentation",
            "Host" => "Host",
            "Verification" => "Verification",
            _ => ""
        };
        if (marker.Length == 0) return false;
        var segments = path.Split('/');
        return segments.Skip(1).Take(Math.Max(segments.Length - 2, 0)).Any(segment =>
            segment.Equals(marker, StringComparison.OrdinalIgnoreCase) ||
            segment.EndsWith("." + marker, StringComparison.OrdinalIgnoreCase) ||
            (layer == "Presentation" && segment.EndsWith(".App", StringComparison.OrdinalIgnoreCase)) ||
            (layer == "Host" && segment.EndsWith(".App", StringComparison.OrdinalIgnoreCase)));
    }
}
