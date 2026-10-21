using System.Text.RegularExpressions;

namespace SafeLocalHarness.Core;

/// <summary>
/// Small models (1.5B–4B) often answer the "body fragment" field with a whole HTML document: doctype, head, inline
/// &lt;style&gt; and &lt;script&gt;. Like the script auto-fix, this splits such a document mechanically into body, script
/// and stylesheet instead of spending another model call. It only moves code the model already wrote; a script loaded
/// from elsewhere is dropped (the app loads nothing from outside), and a split that would
/// merge two different scripts is not attempted. Validation always runs afterwards.
/// </summary>
public static class GeneratedDocumentSplitter
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline;
    private static readonly Regex DocumentMarkup = new(@"<(!doctype|html|head|body|meta|link|style|script|title)\b", Options);
    // An external script would silently break the app once removed, so its answer is rejected instead. An external
    // stylesheet link (web fonts, Bootstrap CSS) only decorates; it is dropped with the other <link> tags (user decision
    // 2026-10-09), so the app still loads nothing from outside.
    // A relative src ("script.js") points at the app's own script, which LocalStudio packages itself, so that tag is
    // simply dropped; only code from another origin (http:, https:, protocol-relative //, data:) blocks the split.
    private static readonly Regex ExternalLoad = new(@"<script\b[^>]*\bsrc\s*=\s*[""']?\s*(https?:|//|data:)", Options);
    private static readonly Regex ExternalScriptTag = new(@"<script\b[^>]*\bsrc\s*=\s*[""']?\s*(https?:|//|data:)[^>]*>\s*(</script\s*>)?", Options);
    private static readonly Regex LocalScriptTag = new(@"<script\b[^>]*\bsrc\s*=[^>]*>\s*</script\s*>", Options);
    private static readonly Regex InlineScript = new(@"<script\b[^>]*>(?<code>.*?)</script\s*>", Options);
    private static readonly Regex InlineStyle = new(@"<style\b[^>]*>(?<code>.*?)</style\s*>", Options);
    private static readonly Regex BodyContent = new(@"<body\b[^>]*>(?<content>.*?)(</body\s*>|$)", Options);
    private static readonly Regex HeadElement = new(@"<head\b[^>]*>.*?</head\s*>", Options);
    private static readonly Regex DocumentTags = new(@"<!doctype[^>]*>|</?(html|head|body)\b[^>]*>|<(meta|link)\b[^>]*>|<title\b[^>]*>.*?</title\s*>", Options);

    /// <summary>Skipped explains why a whole document was not split (null when there was nothing to split).</summary>
    public sealed record Result(GeneratedWebAppCandidate Candidate, bool Split, string? Skipped = null);

    public static Result Apply(GeneratedWebAppCandidate candidate)
    {
        var body = candidate.Body ?? "";
        if (!DocumentMarkup.IsMatch(body)) return new(candidate, false);
        // A script from elsewhere (a CDN library) is dropped like the app's own script tag: the app never loads code from
        // outside, and the behaviour check that follows shows whether it still works without it (repairs kept returning
        // the same unsplittable page, trial 2026-10-13).
        body = ExternalScriptTag.Replace(body, "");
        body = LocalScriptTag.Replace(body, "");

        var script = string.Join("\n", InlineScript.Matches(body).Select(match => match.Groups["code"].Value.Trim()).Where(code => code.Length > 0));
        var style = string.Join("\n", InlineStyle.Matches(body).Select(match => match.Groups["code"].Value.Trim()).Where(code => code.Length > 0));
        var javaScript = Merge(candidate.JavaScript, script);
        if (javaScript is null) return new(candidate, false, "two-different-scripts");
        var css = string.IsNullOrWhiteSpace(style)
            ? candidate.Css
            : string.IsNullOrWhiteSpace(candidate.Css) ? style : candidate.Css.TrimEnd() + "\n" + style;

        // Scripts and styles inside <head> are already captured above, so the head itself can go.
        var withoutCode = InlineStyle.Replace(InlineScript.Replace(body, ""), "");
        var bodyMatch = BodyContent.Match(withoutCode);
        var fragment = bodyMatch.Success ? bodyMatch.Groups["content"].Value : HeadElement.Replace(withoutCode, "");
        fragment = DocumentTags.Replace(fragment, "").Trim();
        if (fragment.Length == 0) return new(candidate, false, "empty-body");
        return new(candidate with { Body = fragment, JavaScript = javaScript, Css = css }, true);
    }

    // The model sometimes repeats its script in both places. The same code once is kept; two different scripts are
    // not merged, because both would register handlers on the same buttons.
    private static string? Merge(string? field, string extracted)
    {
        var existing = field?.Trim() ?? "";
        if (extracted.Length == 0) return field ?? "";
        if (existing.Length == 0) return extracted;
        return Normalize(existing).Contains(Normalize(extracted), StringComparison.Ordinal) ? field : null;
    }

    private static string Normalize(string code) => Regex.Replace(code, @"\s+", "", RegexOptions.CultureInvariant);
}
