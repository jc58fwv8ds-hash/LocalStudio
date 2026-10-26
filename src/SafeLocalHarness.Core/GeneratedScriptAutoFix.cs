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
        @"(?<target>[A-Za-z_$][A-Za-z0-9_$]*(?:\.[A-Za-z_$][A-Za-z0-9_$]*|\[[^\]\r\n]{1,40}\])*)\.innerHTML\s*=(?!=)\s*(?<value>[^;\r\n]*);",
        RegexOptions.CultureInvariant);

    public sealed record Result(string JavaScript, int Fixes);

    public static Result Apply(string javaScript)
    {
        var fixes = 0;
        // When the script builds markup anywhere (e.g. '<li>' + text), a variable assigned to innerHTML may hold that
        // markup; turning it into textContent would show raw tags, so only the clearing fix is applied then.
        var buildsMarkup = Regex.IsMatch(javaScript, @"<[A-Za-z/!]", RegexOptions.CultureInvariant);
        var fixedScript = InnerHtmlAssignment.Replace(javaScript, match =>
        {
            var target = match.Groups["target"].Value;
            var value = match.Groups["value"].Value.Trim();
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
