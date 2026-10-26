namespace SafeLocalHarness.Core;

public static class ArchitectureRules
{
    public static readonly IReadOnlyDictionary<string, string[]> AllowedDependencies =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Verification"] = ["Host", "Presentation", "Infrastructure", "Application", "Domain"],
            ["Host"] = ["Presentation", "Infrastructure", "Application", "Domain"],
            ["Presentation"] = ["Application", "Domain"],
            ["Application"] = ["Domain"],
            ["Infrastructure"] = ["Application", "Domain"],
            ["Domain"] = []
        };

    public static bool IsAllowed(string from, string to) =>
        AllowedDependencies.TryGetValue(from, out var allowed) && allowed.Contains(to, StringComparer.OrdinalIgnoreCase);
}
