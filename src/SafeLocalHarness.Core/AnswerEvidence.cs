using System.Text;
using System.Text.RegularExpressions;

namespace SafeLocalHarness.Core;

/// <summary>
/// Questions about a file (hands-on 2026-10-13, H4/H6/H7): the answer is a sentence with its evidence — the exact words of
/// the file in 「」 — so the user can see where it came from; and a sentence that ends as a question is a question even
/// when it names an edit ("係名に追加して欲しいものは指定されていたか" had become a replacement).
/// </summary>
public static class AnswerEvidence
{
    public const string PromptRule =
        "Answers: one or two Japanese sentences that answer the question directly, followed by the evidence: the exact words of FACTS in 「」 " +
        "with where they are (page, sheet and cell, or paragraph), e.g. ＜answer＞（根拠: 「the exact words」2ページ目）. Use only FACTS. " +
        "When FACTS do not contain the answer, say 「ファイルの中に見つかりませんでした」.";

    private const string NotFoundPattern = "見つかりませんでした|見つかりません|書かれていません|記載がありません|含まれていません|ありませんでした";

    /// <summary>Quoted words of the answer that are not in the file, and a missing quote, as model feedback.</summary>
    public static IReadOnlyList<string> Problems(string answer, string facts)
    {
        // A copy of the file is not an answer, even though its own 「」 would pass the quote check (drill 2026-10-13).
        if (CopiesFacts(answer, facts)) return ["answer.copied-facts (do not copy FACTS: answer the question in one or two sentences, then quote only the few words that show it)"];
        if (Regex.IsMatch(answer, NotFoundPattern)) return [];
        var quotes = Quotes(answer);
        if (quotes.Count == 0) return ["answer.no-evidence (after the answer, quote in 「」 the exact words of FACTS that show it, with the page or cell)"];
        var text = Squash(facts);
        var missing = quotes.Where(quote => !text.Contains(Squash(quote), StringComparison.Ordinal)).ToArray();
        return missing.Length == quotes.Count ? missing.Select(quote => $"answer.quote-not-in-facts:{quote} (quote words exactly as in FACTS)").ToArray() : [];
    }

    /// <summary>A note for the user when the answer still has no evidence found in the file.</summary>
    public static string? Note(string answer, string facts)
    {
        if (CopiesFacts(answer, facts)) return "※ AIがファイルの文章をそのまま返したため、質問への答えになっていません。聞き方を変えて、もう一度お試しください。";
        if (Regex.IsMatch(answer, NotFoundPattern)) return null;
        var quotes = Quotes(answer);
        if (quotes.Count == 0) return "※ 根拠となる箇所を示せませんでした。元のファイルで確かめてください。";
        var text = Squash(facts);
        var missing = quotes.Where(quote => !text.Contains(Squash(quote), StringComparison.Ordinal)).ToArray();
        return missing.Length == 0 ? null : "※ 次の引用はファイルの中に見つかりませんでした（AIの誤りの可能性があります）: " + string.Join("、", missing.Select(quote => $"「{quote}」"));
    }

    /// <summary>A long answer whose text is mostly a stretch of the file.</summary>
    public static bool CopiesFacts(string answer, string facts)
    {
        var squashed = Squash(answer);
        if (squashed.Length < 160) return false;
        var text = Squash(facts);
        var copied = 0;
        for (var start = 0; start + 40 <= squashed.Length; start += 40)
            if (text.Contains(squashed.Substring(start, 40), StringComparison.Ordinal)) copied++;
        return copied * 40 >= squashed.Length / 2;
    }

    private static IReadOnlyList<string> Quotes(string answer) =>
        Regex.Matches(answer, "「([^」]{2,200})」").Select(match => match.Groups[1].Value).Where(quote => Squash(quote).Length >= 2).Distinct().ToArray();

    /// <summary>Text compared without spaces, line breaks and width differences (PDF text often has them in odd places).</summary>
    public static string Squash(string text) => Regex.Replace(text.Normalize(NormalizationForm.FormKC), @"\s+", "");

    /// <summary>A summary is about the whole file, so it is not held to quoting one passage.</summary>
    public static bool IsSummary(string request) => Regex.IsMatch(request, "要約|まとめて|概要|あらまし|ざっくり");

    /// <summary>"…指定されていたか" / "…あった？" end as questions; "…して" / "…してください" end as instructions.</summary>
    public static bool EndsAsQuestion(string request)
    {
        var text = request.Normalize(NormalizationForm.FormKC).Trim().TrimEnd('。', '.', ' ', '!', '！');
        if (Regex.IsMatch(text, "(?:て|てください|てほしい|て欲しい|てくれ|して|しろ|すること|にする|お願いします|お願い)$")) return false;
        return Regex.IsMatch(text, "(?:[?]|か|かな|かね|でしょうか|ますか|ですか|いるか|あるか|たか|のか|あった|いた|ある|いる)$");
    }
}
