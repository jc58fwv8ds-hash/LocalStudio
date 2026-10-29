using System.Globalization;
using System.Text.RegularExpressions;

namespace SafeLocalHarness.Core;

/// <summary>
/// Compiler errors a small model keeps failing to repair, fixed from the error's code and line (catalogue 2026-10-20:
/// three EXEs were given up after 20 repair steps on one of these). Each fix is the standard C# idiom, not app code:
/// <list type="bullet">
/// <item>CS0029 / CS0266 between an array, a List and a sequence: the value is converted with .ToList() / .ToArray().</item>
/// <item>CS0131 / CS1612 assigning to a member of a struct returned by a property or list (ball.Location.X = 5): the
/// struct is copied, changed and put back.</item>
/// </list>
/// Lines that do not have the expected shape are left alone; the model gets the error as before.
/// </summary>
public static class CompileErrorFixes
{
    private static readonly Regex Error = new(@"Program\.cs\((?<line>\d+),(?<column>\d+)\): error (?<code>CS\d+): (?<text>[^\r\n]*)", RegexOptions.CultureInvariant);

    public static string? Apply(string source, string diagnostic)
    {
        var lines = source.Replace("\r\n", "\n").Split('\n');
        var changed = false;
        foreach (var error in Error.Matches(diagnostic).Cast<Match>()
                     .GroupBy(match => int.Parse(match.Groups["line"].Value, CultureInfo.InvariantCulture))
                     .OrderByDescending(group => group.Key))
        {
            var index = error.Key - 1;
            if (index < 0 || index >= lines.Length) continue;
            var first = error.First();
            var fixedLine = first.Groups["code"].Value switch
            {
                "CS0029" or "CS0266" => Convert(lines[index], first.Groups["text"].Value),
                "CS0131" or "CS1612" => CopyChangePutBack(lines[index]),
                _ => null
            };
            if (fixedLine is null || fixedLine == lines[index]) continue;
            lines[index] = fixedLine;
            changed = true;
        }
        return changed ? string.Join("\n", lines) : null;
    }

    private static string? Convert(string line, string message)
    {
        var types = Regex.Match(message, @"type '(?<from>[^']+)' to '(?<to>[^']+)'", RegexOptions.CultureInvariant);
        if (!types.Success) return null;
        string from = types.Groups["from"].Value, to = types.Groups["to"].Value;
        var call = to.Contains("List<", StringComparison.Ordinal) && (from.EndsWith("[]", StringComparison.Ordinal) || from.Contains("IEnumerable<", StringComparison.Ordinal)) ? ".ToList()"
                 : to.EndsWith("[]", StringComparison.Ordinal) && (from.Contains("List<", StringComparison.Ordinal) || from.Contains("IEnumerable<", StringComparison.Ordinal)) ? ".ToArray()"
                 : null;
        if (call is null) return null;
        // "x = value;", "Type x = value;" or "return value;" on one line.
        var statement = Regex.Match(line, @"^(?<head>.*?(?:[^=!<>+\-*/]=(?!=)|\breturn\b)\s*)(?<value>[^;]+?)\s*;(?<tail>\s*(?://.*)?)$", RegexOptions.CultureInvariant);
        if (!statement.Success) return null;
        var value = statement.Groups["value"].Value;
        var wrapped = Regex.IsMatch(value, @"^[\w.\[\]()""]+$", RegexOptions.CultureInvariant) && !value.Contains(' ') ? value : "(" + value + ")";
        return statement.Groups["head"].Value + wrapped + call + ";" + statement.Groups["tail"].Value;
    }

    private static string? CopyChangePutBack(string line)
    {
        var statement = Regex.Match(line, @"^(?<indent>\s*)(?<target>[A-Za-z_][\w]*(?:\.[A-Za-z_]\w*|\[[^\]]+\])*)\.(?<field>[A-Za-z_]\w*)\s*(?<op>[+\-*/]?=)(?<value>[^;]+);(?<tail>\s*(?://.*)?)$", RegexOptions.CultureInvariant);
        if (!statement.Success || statement.Groups["op"].Value == "==") return null;
        var target = statement.Groups["target"].Value;
        return $"{statement.Groups["indent"].Value}{{ var lsCopy = {target}; lsCopy.{statement.Groups["field"].Value} {statement.Groups["op"].Value}{statement.Groups["value"].Value}; {target} = lsCopy; }}{statement.Groups["tail"].Value}";
    }
}
