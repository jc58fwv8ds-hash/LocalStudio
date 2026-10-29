using System.Text.Json;
using System.Text.Json.Serialization;

namespace SafeLocalHarness.Core;

/// <summary>
/// One thing a user does: type into a field, choose an option, press a button, let time pass or reopen the app.
/// Names may list other wordings of the same control separated by "|" (数量|個数). An optional step is skipped when the
/// app has no such control (an app that totals as you type needs no 計算 button).
/// </summary>
public sealed record AcceptanceStep(
    [property: JsonPropertyName("set")] string? Set = null,
    [property: JsonPropertyName("value")] string? Value = null,
    [property: JsonPropertyName("choose")] string? Choose = null,
    [property: JsonPropertyName("option")] string? Option = null,
    [property: JsonPropertyName("press")] string? Press = null,
    [property: JsonPropertyName("tick")] bool Tick = false,
    [property: JsonPropertyName("reload")] bool Reload = false,
    [property: JsonPropertyName("optional")] bool Optional = false,
    [property: JsonPropertyName("nextDay")] bool NextDay = false,
    [property: JsonPropertyName("file")] string? File = null,
    [property: JsonPropertyName("content")] string? Content = null)
{
    public string Describe() =>
        (Set is not null ? $"「{Set}」に「{Value}」を入れる" :
         File is not null ? $"「{File}」でファイルを選ぶ" :
         Choose is not null ? $"「{Choose}」で「{Option}」を選ぶ" :
         Press is not null ? $"「{Press}」を押す" :
         Tick ? "少し時間をおく" :
         Reload ? "開き直す" :
         NextDay ? "翌日に開き直す" : "（何もしない）") + (Optional ? "（あれば）" : "");

    internal int Actions => new[] { Set is not null, Choose is not null, Press is not null, Tick, Reload, NextDay, File is not null }.Count(done => done);
}

/// <summary>
/// An acceptance example for one requirement: what a user does and the text that must then be on the screen (and text
/// that must not). Written from the request before the code exists, then played on the finished app, so an app that
/// runs without errors but does the wrong thing (a converter whose result equals its input, 2026-10-20) is caught.
/// Controls are found by what the user sees (label, button text, placeholder), not by ids in the code. An expected
/// text may list other acceptable forms separated by "|" (660|６６０円).
/// </summary>
public sealed record AcceptanceScenario(
    [property: JsonPropertyName("requirement")] string Requirement,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("steps")] IReadOnlyList<AcceptanceStep> Steps,
    [property: JsonPropertyName("expect")] IReadOnlyList<string> Expect,
    [property: JsonPropertyName("absent")] IReadOnlyList<string>? Absent = null,
    [property: JsonPropertyName("fileExpect")] IReadOnlyList<string>? FileExpect = null,
    [property: JsonPropertyName("fileAbsent")] IReadOnlyList<string>? FileAbsent = null)
{
    public const string FileName = "acceptance.json";
    public const int MaxScenarios = 4;
    public const int MaxSteps = 10;

    public string Describe() => string.Join("、", Steps.Select(step => step.Describe()));

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault };

    public static string Serialize(IReadOnlyList<AcceptanceScenario> scenarios) => JsonSerializer.Serialize(new { schema = 1, scenarios }, Json) + "\n";

    /// <summary>The scenarios kept with a project (empty when there are none or the file cannot be read).</summary>
    public static IReadOnlyList<AcceptanceScenario> Load(string projectRoot)
    {
        var path = Path.Combine(projectRoot, FileName);
        if (!File.Exists(path)) return [];
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("scenarios", out var list) ? Usable(list.Deserialize<AcceptanceScenario[]>() ?? []) : [];
        }
        catch (JsonException) { return []; }
    }

    /// <summary>Scenarios that can be played: a title, 1–10 steps that each do one thing, and something to expect.</summary>
    public static IReadOnlyList<AcceptanceScenario> Usable(IEnumerable<AcceptanceScenario> scenarios) =>
        scenarios.Where(scenario => !string.IsNullOrWhiteSpace(scenario.Title) && scenario.Steps is { Count: > 0 and <= MaxSteps } &&
                                    scenario.Steps.All(step => step.Actions == 1) &&
                                    (scenario.Expect ?? []).Concat(scenario.FileExpect ?? []).Any(text => !string.IsNullOrWhiteSpace(text)))
            .Take(MaxScenarios).ToArray();
}
