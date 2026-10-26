namespace SafeLocalHarness.Core;

/// <summary>
/// Build outputs are regenerated from sources and are never opened by Local Studio. Reading an unsigned generated EXE
/// triggers on-access antivirus scanning; on 2026-10-06 that removed a generated app (Trend Micro TSC_GENCLEAN).
/// Classification uses the path only, so excluded files are never read.
/// </summary>
public static class ProjectFilePolicy
{
    public const long MaxCartridgeFileBytes = 2L * 1024 * 1024;
    public const long MaxCartridgeTotalBytes = 50L * 1024 * 1024;
    public const int MaxCartridgeFiles = 2048;

    private static readonly HashSet<string> BuildOutputExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".pdb", ".msi", ".msix", ".appx", ".com", ".scr", ".sys", ".lib", ".obj", ".nupkg", ".cache"
    };

    private static readonly HashSet<string> BuildOutputDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".vs", "node_modules", "target"
    };

    /// <summary>
    /// Sources, documents and small images that a cartridge may carry (user-approved list, 2026-10-06; .gs added with
    /// GAS generation in 3-3, 2026-10-09 — Apps Script source text, which versions and cartridges must keep).
    /// </summary>
    private static readonly HashSet<string> CartridgeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".xaml", ".csproj", ".slnx", ".json", ".md", ".txt", ".html", ".htm", ".css",
        ".js", ".mjs", ".ts", ".tsx", ".jsx", ".manifest", ".png", ".ico", ".svg", ".gs"
    };

    public static bool IsBuildOutput(string relativePath)
    {
        var parts = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        if (parts.Take(parts.Length - 1).Any(BuildOutputDirectories.Contains)) return true;
        return BuildOutputExtensions.Contains(Path.GetExtension(parts[^1]));
    }

    public static bool IsCartridgeEligible(string relativePath) =>
        !IsBuildOutput(relativePath) && HasSafeWindowsNames(relativePath) && CartridgeExtensions.Contains(Path.GetExtension(relativePath));

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>Rejects device names (CON, NUL, ...), invalid characters and trailing dots/spaces in every segment.</summary>
    public static bool HasSafeWindowsNames(string relativePath)
    {
        var parts = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 && parts.All(part =>
            part.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
            !part.EndsWith('.') && !part.EndsWith(' ') &&
            !ReservedNames.Contains(Path.GetFileNameWithoutExtension(part)));
    }
}
