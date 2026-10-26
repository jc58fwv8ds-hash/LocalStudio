using System.Text.Json.Serialization;

namespace SafeLocalHarness.Core;

public enum StudioProjectKind
{
    DesktopOrWebApp,
    WindowsApp,
    WindowsGame,
    GoogleAppsScript,
    Agent
}

public sealed record BlueprintStep(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("outcome")] string Outcome);

public sealed record StudioBlueprint(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("features")] IReadOnlyList<string> Features,
    [property: JsonPropertyName("steps")] IReadOnlyList<BlueprintStep> Steps,
    [property: JsonPropertyName("questions")] IReadOnlyList<string> Questions,
    [property: JsonPropertyName("safety_notes")] IReadOnlyList<string> SafetyNotes,
    [property: JsonPropertyName("capabilities")] IReadOnlyList<string> Capabilities)
{
    public static StudioBlueprint Empty => new("新しいプロジェクト", "", "app", [], [], [], [], []);
}

public static class BlueprintValidator
{
    public static StudioBlueprint Validate(StudioBlueprint value)
    {
        var name = Clean(value.Name, 80, "新しいプロジェクト");
        var summary = Clean(value.Summary, 500, "目的を確認中です。");
        var features = CleanList(value.Features, 8, 160);
        var questions = CleanList(value.Questions, 3, 180);
        var safety = CleanList(value.SafetyNotes, 5, 180);
        var capabilities = (value.Capabilities ?? [])
            .Where(KnownCapabilities.Contains)
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();
        var steps = value.Steps.Take(8)
            .Select(x => new BlueprintStep(Clean(x.Title, 80, "実装"), Clean(x.Outcome, 240, "動作を確認します。")))
            .ToArray();
        if (features.Count == 0) features = ["会話から要件を整理する"];
        if (steps.Length == 0) steps = [new("設計", "確認可能な計画を作成します。")];
        return value with { Name = name, Summary = summary, Features = features, Questions = questions, SafetyNotes = safety, Steps = steps, Capabilities = capabilities };
    }

    private static string Clean(string? value, int length, string fallback)
    {
        var result = (value ?? "").Replace('\0', ' ').Trim();
        return string.IsNullOrWhiteSpace(result) ? fallback : result[..Math.Min(result.Length, length)];
    }

    private static IReadOnlyList<string> CleanList(IReadOnlyList<string>? values, int count, int length) =>
        // Repeated entries are kept once (drill 2026-10-12: qwen2.5-coder:1.5b wrote the same feature eight times).
        (values ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => Clean(x, length, "")).Distinct(StringComparer.Ordinal).Take(count).ToArray();

    public static readonly IReadOnlySet<string> KnownCapabilities = new HashSet<string>(StringComparer.Ordinal)
    {
        "workspace.read", "workspace.write", "network.fetch", "account.authorize", "message.send", "external.publish"
    };
}
