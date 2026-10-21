using System.Text.RegularExpressions;

namespace SafeLocalHarness.Core;

/// <summary>
/// One representative request with expectations a generated app must meet, checked without running a model
/// (in the spirit of an emulator verified against a reference log: fixed inputs, mechanical judgement).
/// </summary>
public sealed record GoldenCase(
    string Id,
    string Request,
    IReadOnlyList<string> RequiredScriptPatterns,
    IReadOnlyList<string> RequiredMarkupPatterns,
    string? Improvement = null,
    bool ImprovementIsVisual = false,
    StudioProjectKind Kind = StudioProjectKind.DesktopOrWebApp);

public sealed record BenchmarkRun(
    string CaseId,
    bool Created,
    bool GoldenPassed,
    IReadOnlyList<string> GoldenMisses,
    int Attempts,
    int AutoFixes,
    double Seconds,
    long PeakOwnedBytes,
    long MinAvailableBytes,
    string? Failure,
    bool? Improved = null,
    double? ImprovementSeconds = null,
    bool UsedAppSpec = false);

public sealed record BenchmarkScore(double Total, double Quality, double Latency, double Memory, double Repairs);

/// <summary>
/// Task-specific benchmark for Local Studio on a shared office PC. General model benchmarks reward the wrong
/// things here, so runs are scored on what matters for this app (weights after R. Shimizu's NPC benchmark idea):
/// quality 55, latency 20, memory headroom left for other work 15, repairs needed 10.
/// </summary>
public static class LocalStudioBenchmark
{
    private const long GiB = 1024L * 1024 * 1024;

    public static IReadOnlyList<GoldenCase> WebCases { get; } =
    [
        new("memo",
            "ブラウザーで使うWebアプリで、メモを1行ずつ追加して一覧表示できるシンプルなメモ帳。保存はこのPCのブラウザー内だけ。",
            [@"localStorage\.setItem", @"localStorage\.getItem", @"addEventListener"],
            [@"<(input|textarea)\b", @"<button\b"],
            "追加ボタンをもっと大きくして、目立つ色にしてください",
            ImprovementIsVisual: true),
        new("counter",
            "ブラウザーで使うWebアプリで、ボタンを押すたびに数が1ずつ増えるカウンター。リセットボタンも付けて。",
            [@"addEventListener", @"(\+\+|\+=\s*1\b|\+\s*1\b)"],
            [@"<button\b[\s\S]*<button\b"]),
        new("character-count",
            "ブラウザーで使うWebアプリで、入力した文章の文字数をリアルタイムで表示するツール。",
            [@"addEventListener\s*\(\s*['""]input['""]", @"\.length\b"],
            [@"<(textarea|input)\b"]),
        new("dice",
            "ブラウザーで使うWebアプリで、ボタンを押すと1から6のサイコロの目をランダムに表示する。",
            [@"Math\.random", @"addEventListener"],
            [@"<button\b"]),
        new("stopwatch",
            "ブラウザーで使うWebアプリで、スタート・ストップ・リセットができるストップウォッチ。",
            [@"(setInterval|requestAnimationFrame)", @"(clearInterval|cancelAnimationFrame)"],
            [@"<button\b"]),
        new("bmi",
            "ブラウザーで使うWebアプリで、身長と体重を入れるとBMIを計算して表示する。",
            [@"(toFixed|Math\.round)", @"addEventListener"],
            [@"<input\b"])
    ];

    /// <summary>
    /// GAS requests. Script patterns are checked against all .gs files; "markup" patterns against appsscript.json, so
    /// the permissions LocalStudio derived are part of the expectation.
    /// </summary>
    public static IReadOnlyList<GoldenCase> GasCases { get; } =
    [
        new("gas-dedupe",
            "Googleスプレッドシートで、メニューから実行するとA列の値が重複している行を削除するGAS。",
            [@"function\s+onOpen\s*\(", @"createMenu", @"SpreadsheetApp", @"deleteRows?\s*\(|setValues\s*\(|clearContents\s*\("],
            [@"auth/spreadsheets", @"auth/script\.container\.ui"],
            Kind: StudioProjectKind.GoogleAppsScript),
        new("gas-calendar",
            "Googleカレンダーの今日から7日間の予定を、スプレッドシートの新しいシートに一覧で書き出すGAS。メニューから実行したい。",
            [@"CalendarApp", @"getEvents\s*\(", @"setValues\s*\(|appendRow\s*\("],
            [@"auth/calendar", @"auth/spreadsheets"],
            Kind: StudioProjectKind.GoogleAppsScript),
        new("gas-overdue",
            "Googleスプレッドシートで、B列の日付が今日より前の行の背景を灰色にするGAS。メニューから実行。",
            [@"setBackgrounds?\s*\(", @"new\s+Date\s*\(", @"SpreadsheetApp"],
            [@"auth/spreadsheets"],
            "灰色ではなく薄い赤色にしてください",
            Kind: StudioProjectKind.GoogleAppsScript),
        new("gas-doc",
            "Googleドキュメントの末尾に、今日の日付の見出しと空の箇条書きを追加するGAS。",
            [@"DocumentApp", @"append(Paragraph|ListItem)\s*\("],
            [@"auth/documents"],
            Kind: StudioProjectKind.GoogleAppsScript)
    ];

    /// <summary>Returns the expectations a generated project misses (empty when it meets all of them).</summary>
    public static IReadOnlyList<string> CheckGolden(GoldenCase golden, string projectRoot)
    {
        var misses = new List<string>();
        var gas = golden.Kind == StudioProjectKind.GoogleAppsScript;
        var script = gas
            ? string.Join("\n", Directory.EnumerateFiles(projectRoot, "*.gs", SearchOption.TopDirectoryOnly).Select(File.ReadAllText))
            : ReadOrEmpty(Path.Combine(projectRoot, "app.js"));
        var html = ReadOrEmpty(Path.Combine(projectRoot, gas ? GasStaticVerifier.ManifestFileName : "index.html"));
        foreach (var pattern in golden.RequiredScriptPatterns)
            if (!Regex.IsMatch(script, pattern, RegexOptions.CultureInvariant)) misses.Add("script:" + pattern);
        foreach (var pattern in golden.RequiredMarkupPatterns)
            if (!Regex.IsMatch(html, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) misses.Add("markup:" + pattern);
        return misses;
    }

    public static BenchmarkScore Score(IReadOnlyList<BenchmarkRun> runs)
    {
        if (runs.Count == 0) return new(0, 0, 0, 0, 0);
        var quality = runs.Average(run => run.Created ? run.GoldenPassed ? 1.0 : 0.5 : 0.0);
        // Two minutes or less is full marks; fifteen minutes or more is none.
        var latency = runs.Average(run => run.Created ? Math.Clamp(1 - (run.Seconds - 120) / (900 - 120), 0, 1) : 0);
        // Free memory left for the user's other work at the worst moment: 0.5 GB or less is none, 2.5 GB or more is full.
        var memory = runs.Average(run => Math.Clamp((run.MinAvailableBytes - 0.5 * GiB) / (2.0 * GiB), 0, 1));
        var repairs = runs.Average(run => !run.Created ? 0 : run.Attempts <= 1 ? 1.0 : run.Attempts == 2 ? 0.5 : 0.0);
        var total = 100 * (0.55 * quality + 0.20 * latency + 0.15 * memory + 0.10 * repairs);
        return new(Math.Round(total, 1), Math.Round(quality * 100, 1), Math.Round(latency * 100, 1), Math.Round(memory * 100, 1), Math.Round(repairs * 100, 1));
    }

    private static string ReadOrEmpty(string path) => File.Exists(path) ? File.ReadAllText(path) : "";
}
