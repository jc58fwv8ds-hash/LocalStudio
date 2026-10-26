using System.Text.RegularExpressions;

namespace SafeLocalHarness.Core;

/// <summary>
/// The request side of renaming, decided without the AI. Common one-clause requests become plans by fixed rules, and
/// any plan (from the rules or the AI) is checked against what the request asks for. In the first real drill
/// (2026-10-10) a 1.5B model returned plans that were valid but not what was asked — "更新日_3桁の連番" became the
/// literal prefix "更新日_", and 「 final」を消して removed the quote marks — the same kind of false success a
/// checker misses when it only checks form.
/// </summary>
public static class RenameRequestRules
{
    private const RegexOptions Options = RegexOptions.CultureInvariant;
    private static readonly string[] Extensions = ["jpg", "jpeg", "png", "gif", "heic", "pdf", "docx", "doc", "xlsx", "xls", "pptx", "csv", "txt", "mp4", "mov", "mp3", "wav", "zip"];

    /// <summary>A plan built by rules, or null when the request needs the AI.</summary>
    public static RenamePlan? TryRules(string request)
    {
        var text = request.Trim().TrimEnd('。', '.', '！', '!');
        var extension = ExtensionIn(text);
        RenameStep? step = null;
        Match match;
        if ((match = Regex.Match(text, @"「(?<from>[^」]+)」を「(?<to>[^」]*)」に(変え|変更|置き換え|置換|直)", Options)).Success)
            step = new RenameStep("replace", Find: match.Groups["from"].Value, With: match.Groups["to"].Value);
        else if ((match = Regex.Match(text, @"「(?<text>[^」]+)」を(消|削除|取|除|外)", Options)).Success)
            step = new RenameStep("remove", Find: match.Groups["text"].Value);
        else if ((match = Regex.Match(text, @"(先頭|最初|頭|前)に「(?<text>[^」]+)」を(付け|つけ|追加|入れ)", Options)).Success)
            step = new RenameStep("prefix", Text: match.Groups["text"].Value);
        else if ((match = Regex.Match(text, @"(末尾|最後|後ろ|うしろ)に「(?<text>[^」]+)」を(付け|つけ|追加|入れ)", Options)).Success)
            step = new RenameStep("suffix", Text: match.Groups["text"].Value);
        else if (Regex.IsMatch(text, @"小文字に", Options))
            step = new RenameStep("case", To: "lower");
        else if (Regex.IsMatch(text, @"大文字に", Options))
            step = new RenameStep("case", To: "upper");
        // Anything more (dates, numbering, several changes at once) is left to the AI.
        if (step is null || Regex.IsMatch(text, @"日付|更新日|撮影日|作成日|連番|番号|、.*(して|付け|消)", Options)) return null;
        return new RenamePlan(extension, null, "name", [step]);
    }

    /// <summary>
    /// Slips with one obvious meaning: "clear" anywhere but first would throw away the steps before it (drill 2 put it
    /// last and every name became empty), so it is moved to the front and kept once.
    /// </summary>
    public static RenamePlan Normalize(RenamePlan plan)
    {
        var steps = plan.Steps ?? [];
        if (!steps.Any(step => step?.Op == "clear")) return plan;
        return plan with { Steps = new[] { new RenameStep("clear") }.Concat(steps.Where(step => step?.Op != "clear")).ToArray() };
    }

    /// <summary>What the request asks for that the plan does not do (empty when they agree).</summary>
    public static IReadOnlyList<string> Mismatches(string request, RenamePlan plan)
    {
        var problems = new List<string>();
        var steps = plan.Steps ?? [];
        if (Regex.IsMatch(request, @"日付|更新日|撮影日|作成日|日にち", Options) && steps.All(step => step.Op != "date"))
            problems.Add("rename.request-date-missing (use op date for the file date)");
        if (Regex.IsMatch(request, @"連番|番号|ナンバー", Options) && steps.All(step => step.Op != "number"))
            problems.Add("rename.request-number-missing (use op number)");
        // "名前を（日付・連番）にして" asks for the whole name to become that, not for it to be added to the old name.
        if (Regex.IsMatch(request, @"名前を.{0,30}(にして|に変えて|に変更|にする)", Options) &&
            Regex.IsMatch(request, @"日付|更新日|撮影日|作成日|連番|番号", Options) &&
            !Regex.IsMatch(request, @"付け|つけ|追加|先頭|末尾|最後|後ろ|残し", Options) &&
            steps.All(step => step.Op != "clear"))
            problems.Add("rename.request-whole-name (start with op clear: the request replaces the whole name)");
        if (ExtensionIn(request) is { } extension && !string.Equals(plan.Extension?.Trim().TrimStart('.'), extension, StringComparison.OrdinalIgnoreCase))
            problems.Add($"rename.request-extension-missing (set extension to {extension})");
        // Words of the request copied into names instead of being carried out.
        foreach (var word in new[] { "更新日", "撮影日", "作成日", "連番", "日付" })
            if (steps.Any(step => (step.Text ?? "").Contains(word, StringComparison.Ordinal) || (step.With ?? "").Contains(word, StringComparison.Ordinal)))
                problems.Add($"rename.request-word-copied:{word} (do not write the word; use op date or number)");
        foreach (Match quoted in Regex.Matches(request, @"「(?<text>[^」]+)」", Options))
        {
            var value = quoted.Groups["text"].Value;
            if (Regex.IsMatch(value, @"更新日|撮影日|作成日|連番|日付", Options)) continue;
            if (!steps.Any(step => step.Find == value || step.Text == value || step.With == value))
                problems.Add($"rename.request-text-unused:{value} (use exactly this text, without the 「」 marks)");
        }
        if (steps.Any(step => (step.Find ?? "") is "「" or "」" || (step.Text ?? "").Contains('「') || (step.Text ?? "").Contains('」')))
            problems.Add("rename.quote-marks (「」 only mark the text in the request)");
        return problems;
    }

    private static string? ExtensionIn(string text)
    {
        var lower = text.ToLowerInvariant();
        var found = Extensions.FirstOrDefault(extension => Regex.IsMatch(lower, $@"(?<![a-z]){extension}(?![a-z])", Options));
        if (found is null && Regex.IsMatch(text, "写真|画像", Options) && !Regex.IsMatch(lower, "png|heic|gif", Options)) return null;
        return found == "jpeg" ? "jpeg" : found;
    }
}
