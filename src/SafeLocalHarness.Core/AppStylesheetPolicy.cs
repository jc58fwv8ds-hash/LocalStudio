using System.Text.RegularExpressions;

namespace SafeLocalHarness.Core;

/// <summary>
/// Which evidence a requirement needs. Background behaviour such as saving has no screen element of its own
/// (2026-10-09: "ブラウザ内での保存" failed all three attempts on "no UI element id"), so it is proven by its
/// mechanical behaviour contract (localStorage save and restore, checked separately) and JavaScript evidence only.
/// </summary>
public static class RequirementEvidencePolicy
{
    // "ブラウザを閉じても残る" is saving said another way (trial 2026-10-13).
    private static readonly string[] BackgroundSignals = ["保存", "記憶", "復元", "自動", "残る", "残す", "閉じても", "再読み込み"];

    public static bool NeedsUiEvidence(string requirementText) =>
        !VisualRequirementPolicy.IsVisual(requirementText) &&
        !BackgroundSignals.Any(signal => requirementText.Contains(signal, StringComparison.Ordinal));
}

/// <summary>
/// Requirements come in two kinds. Behaviour is proven by JavaScript evidence; a purely visual change has no
/// behaviour, so it is proven mechanically by an app.css rule that targets an element that really exists
/// (2026-10-09: "make the button bigger" could never pass a review that demanded JavaScript evidence).
/// </summary>
public static class VisualRequirementPolicy
{
    public const string Prefix = "見た目: ";

    private static readonly string[] VisualSignals =
    [
        "大きく", "小さく", "色", "目立", "太字", "フォント", "文字サイズ", "余白", "配置", "幅", "高さ",
        "背景", "デザイン", "見た目", "レイアウト", "角丸", "枠", "間隔", "揃え", "中央"
    ];

    private static readonly string[] BehaviourSignals =
    [
        "保存", "削除", "消去", "計算", "検索", "並べ替", "集計", "通知", "読み込", "書き出", "件数", "機能を", "できるように"
    ];

    /// <summary>Plain-language change requests that only ask for appearance become visual requirements.</summary>
    public static bool IsVisualChange(string request) =>
        VisualSignals.Any(signal => request.Contains(signal, StringComparison.Ordinal)) &&
        !BehaviourSignals.Any(signal => request.Contains(signal, StringComparison.Ordinal));

    public static bool IsVisual(string requirementText) => requirementText.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>True when some app.css selector names an id or class present in the markup.</summary>
    public static bool HasStyleEvidence(string css, string html)
    {
        if (string.IsNullOrWhiteSpace(css)) return false;
        var ids = Regex.Matches(html, @"\bid\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var classes = Regex.Matches(html, @"\bclass\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .SelectMany(match => match.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToHashSet(StringComparer.Ordinal);
        foreach (Match rule in Regex.Matches(css, @"(?<selectors>[^{}]+)\{(?<body>[^{}]*)\}", RegexOptions.CultureInvariant))
        {
            if (string.IsNullOrWhiteSpace(rule.Groups["body"].Value)) continue;
            foreach (Match token in Regex.Matches(rule.Groups["selectors"].Value, @"(?<kind>[#.])(?<name>[A-Za-z_][A-Za-z0-9_-]*)", RegexOptions.CultureInvariant))
            {
                var name = token.Groups["name"].Value;
                if (token.Groups["kind"].Value == "#" ? ids.Contains(name) : classes.Contains(name)) return true;
            }
        }
        return false;
    }
}

/// <summary>
/// Rules for the optional app-specific stylesheet (app.css) of a generated web app. It exists so visual requests
/// (size, colour, spacing, layout) can be met; it may never load anything or hide content behind CSS escapes.
/// Both the generator and the independent verifier apply the same rules.
/// </summary>
public static class AppStylesheetPolicy
{
    public const string FileName = "app.css";
    public const int MaxCharacters = 2_000;

    private static readonly (string Token, string Reason)[] Forbidden =
    [
        ("url(", "url"),
        ("@import", "import"),
        ("@font-face", "font-face"),
        ("@namespace", "namespace"),
        ("image-set(", "image-set"),
        ("expression(", "expression"),
        ("javascript:", "javascript"),
        ("behavior", "behavior"),
        ("-moz-binding", "binding"),
        ("http:", "external"),
        ("https:", "external"),
        ("\\", "escape"),
        ("<", "markup")
    ];

    public static IReadOnlyList<string> Validate(string css)
    {
        var errors = new List<string>();
        if (css.Length > MaxCharacters) errors.Add("too-large");
        foreach (var (token, reason) in Forbidden)
            if (css.Contains(token, StringComparison.OrdinalIgnoreCase) && !errors.Contains(reason))
                errors.Add(reason);
        if (css.Count(character => character == '{') != css.Count(character => character == '}')) errors.Add("unbalanced");
        return errors;
    }
}
