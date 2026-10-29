using System.Text.RegularExpressions;

namespace SafeLocalHarness.Core;

/// <summary>
/// Mechanical repairs of the most common forbidden pattern before validation, in the spirit of a linter's --fix:
/// a small model often writes innerHTML even when told not to, and asking it again costs minutes and memory.
/// Only rewrites that make the code strictly safer are applied: clearing becomes replaceChildren(), and assigning
/// a value that contains no markup becomes textContent. Anything that builds markup is left for the validator to reject.
/// </summary>
public static class GeneratedScriptAutoFix
{
    private static readonly Regex InnerHtmlAssignment = new(
        @"(?<target>[A-Za-z_$][A-Za-z0-9_$]*(?:\.[A-Za-z_$][A-Za-z0-9_$]*|\[[^\]\r\n]{1,40}\])*)\.innerHTML\s*(?<append>\+)?=(?!=)\s*(?<value>[^;\r\n]*);",
        RegexOptions.CultureInvariant);

    // A template literal that is only simple elements in a row, e.g. `<td>${e.date}</td><td>${e.amount}円</td>`.
    private static readonly Regex FlatTemplate = new(
        @"^`(?<cells>(?:\s*<(?<tag>td|th|li|span|strong|b|em|p|div|label|small)>(?:(?!</?[A-Za-z!])[^`<])*</\k<tag>>\s*)+)`$",
        RegexOptions.CultureInvariant);
    private static readonly Regex Cell = new(@"<(?<tag>[a-z]+)>(?<content>(?:(?!</?[A-Za-z!])[^`<])*)</\k<tag>>", RegexOptions.CultureInvariant);

    public sealed record Result(string JavaScript, int Fixes);

    // A quoted newline written as a real line break ('⏎'), from a "\n" decoded one level too far: always a syntax error
    // (CSV cleaner, catalogue 2026-10-19). It becomes the escape the model meant.
    private static readonly Regex RawNewlineLiteral = new(@"(?<q>['""])\r?\n\k<q>", RegexOptions.CultureInvariant);

    public static Result Apply(string javaScript)
    {
        var fixes = 0;
        javaScript = RawNewlineLiteral.Replace(javaScript, match => { fixes++; return match.Groups["q"].Value + "\\n" + match.Groups["q"].Value; });
        // pdf.js 3.x must open documents with isEvalSupported: false (CVE-2024-4367); a PDF editor failed only on this
        // (catalogue 2026-10-20). An object argument gets the setting; a plain argument (the bytes) is wrapped as data
        // or, when it is a quoted address, as url.
        javaScript = Regex.Replace(javaScript, @"pdfjsLib\s*\.\s*getDocument\s*\(\s*(?<arg>\{[^{}]*\}|[^(){};]+?)\s*\)", match =>
        {
            var argument = match.Groups["arg"].Value.Trim();
            if (Regex.IsMatch(argument, @"isEvalSupported\s*:", RegexOptions.CultureInvariant)) return match.Value;
            fixes++;
            if (argument.StartsWith('{'))
                return "pdfjsLib.getDocument({ isEvalSupported: false, " + argument[1..].TrimStart() + ")";
            var key = Regex.IsMatch(argument, @"^['""`]|url", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase) ? "url" : "data";
            return "pdfjsLib.getDocument({ " + key + ": " + argument + ", isEvalSupported: false })";
        }, RegexOptions.CultureInvariant);
        // A const that is assigned again later ("entries = []" in a clear button) throws when used; it becomes let
        // (household budget, catalogue 2026-10-19). Only simple names, and only when a real reassignment exists.
        foreach (var name in Regex.Matches(javaScript, @"\bconst\s+(?<name>[A-Za-z_$][\w$]*)\s*=(?!=)", RegexOptions.CultureInvariant).Select(match => match.Groups["name"].Value).Distinct().ToArray())
        {
            var reassigned = Regex.Matches(javaScript, @"(?<![\w$.])" + Regex.Escape(name) + @"\s*(=(?![=>])|\+=|-=|\*=|/=|\+\+|--)", RegexOptions.CultureInvariant).Count;
            var declared = Regex.Matches(javaScript, @"\b(const|let|var)\s+" + Regex.Escape(name) + @"\s*=(?!=)", RegexOptions.CultureInvariant).Count;
            if (declared != 1 || reassigned <= declared) continue;
            javaScript = Regex.Replace(javaScript, @"\bconst(\s+" + Regex.Escape(name) + @"\s*=(?!=))", "let$1", RegexOptions.CultureInvariant);
            fixes++;
        }
        // When the script builds markup anywhere (e.g. '<li>' + text), a variable assigned to innerHTML may hold that
        // markup; turning it into textContent would show raw tags, so only the clearing fix is applied then.
        var buildsMarkup = Regex.IsMatch(javaScript, @"<[A-Za-z/!]", RegexOptions.CultureInvariant);
        var fixedScript = InnerHtmlAssignment.Replace(javaScript, match =>
        {
            var target = match.Groups["target"].Value;
            var value = match.Groups["value"].Value.Trim();
            var append = match.Groups["append"].Success;
            // Rows built from simple elements become elements whose text is set: the same screen, no markup parsing
            // (a household-budget app failed on this five times, catalogue 2026-10-19).
            if (FlatTemplate.Match(value) is { Success: true } flat)
            {
                var cells = Cell.Matches(flat.Groups["cells"].Value)
                    .Select(cell => $"[\"{cell.Groups["tag"].Value}\", `{cell.Groups["content"].Value}`]");
                fixes++;
                return $"{target}.{(append ? "append" : "replaceChildren")}(...[{string.Join(", ", cells)}].map(([tag, text]) => {{ const cell = document.createElement(tag); cell.textContent = text; return cell; }}));";
            }
            if (append) return match.Value;
            if (value is "''" or "\"\"" or "``")
            {
                fixes++;
                return $"{target}.replaceChildren();";
            }
            if (buildsMarkup || value.Length == 0 || value.Contains('<') || value.Contains('`') || value.Contains("innerHTML", StringComparison.Ordinal))
                return match.Value;
            fixes++;
            return $"{target}.textContent = {value};";
        });
        return new(fixedScript, fixes);
    }
}
