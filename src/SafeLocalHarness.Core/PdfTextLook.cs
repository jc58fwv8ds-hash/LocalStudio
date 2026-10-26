using System.Globalization;
using System.Text.RegularExpressions;

namespace SafeLocalHarness.Core;

/// <summary>
/// The look of drawn PDF text from the user's words, and adjustments of the last drawing (hands-on 2026-10-13: the
/// user wants the fine changes a chat assistant can make — "明朝体で10pt", "下から20ptの位置に", "1－30の形で",
/// "もう少し上に", "もっと小さく（9ptくらいに）", "ハイフンをちょっとだけ長くして", "中間の長さに").
/// </summary>
public static class PdfTextLook
{
    private const double Margin = 28;
    private const string Colours = "黄色|黄|赤色|赤|青色|青|緑色|緑|オレンジ|橙|灰色|グレー|水色|ピンク|黒色|黒|白色|白";
    private const string Points = @"(\d+(?:\.\d+)?)\s*(?:pt|ポイント|ﾎﾟｲﾝﾄ|point)";

    public static bool Draws(PdfOp op) => op.Op is "stamp" or "watermark" or "page-numbers";

    /// <summary>The op with the look named in the words (text inside 「」 is not read).</summary>
    public static PdfOp WithLook(PdfOp op, string words)
    {
        if (!Draws(op)) return op;
        var text = Regex.Replace(words.Normalize(System.Text.NormalizationForm.FormKC), "「[^」]*」", "「」");
        var font = Regex.IsMatch(text, "明朝") ? "mincho" : Regex.IsMatch(text, "UD|教科書体") ? "ud" : Regex.IsMatch(text, "ゴシック") ? "gothic" : op.Font;
        var size = Regex.Match(text, Points) is { Success: true } points ? double.Parse(points.Groups[1].Value, CultureInfo.InvariantCulture) : op.Size;
        var colour = Regex.Match(text, $"({Colours})(?:色)?(?:の|い|く|で)?(?:文字|字)|(?:文字色|文字の色|文字|字|色)(?:を|は)?\\s*({Colours})");
        var bold = Regex.IsMatch(text, "太字|太く|ボールド") ? true : op.Bold;
        var (dx, dy) = (op.Dx, op.Dy);
        // "下から20pt": a distance from that edge (the standard is 28pt); "20pt上に": a shift.
        foreach (Match edge in Regex.Matches(text, @"(上|下|左|右)(?:端)?から\s*" + Points))
        {
            var distance = double.Parse(edge.Groups[2].Value, CultureInfo.InvariantCulture) - Margin;
            switch (edge.Groups[1].Value) { case "下": dy = distance; break; case "上": dy = -distance; break; case "左": dx = distance; break; default: dx = -distance; break; }
        }
        foreach (Match shift in Regex.Matches(text, @"(?:(上|下|左|右)(?:に|へ)\s*" + Points + "|" + Points + @"\s*(?:分)?(上|下|左|右)(?:に|へ))"))
        {
            var direction = shift.Groups[1].Success ? shift.Groups[1].Value : shift.Groups[4].Value;
            var amount = double.Parse(shift.Groups[2].Success ? shift.Groups[2].Value : shift.Groups[3].Value, CultureInfo.InvariantCulture);
            (dx, dy) = Move(dx ?? 0, dy ?? 0, direction, amount);
        }
        var format = op.Op == "page-numbers" ? NumberFormat(text) ?? op.Text : op.Text;
        return op with
        {
            Font = font,
            Size = size,
            Color = colour.Success ? (colour.Groups[1].Success ? colour.Groups[1].Value : colour.Groups[2].Value) : op.Color,
            Bold = bold,
            Dx = dx,
            Dy = dy,
            Text = format
        };
    }

    /// <summary>"1－30の形" / "1/30" / "ハイフンで" → the page number format; null when the words name none.</summary>
    public static string? NumberFormat(string text)
    {
        var shown = Regex.Match(text, @"(?<!\d)1\s*([-－―–—ー~〜/／])\s*\d{1,4}(?!\d)");
        if (shown.Success) return "{n}" + (shown.Groups[1].Value is "/" or "／" ? " / " : shown.Groups[1].Value.Replace('ー', '－')) + "{total}";
        if (Regex.IsMatch(text, "ハイフン")) return "{n}-{total}";
        if (Regex.IsMatch(text, "/|分の|総ページ|全体")) return "{n} / {total}";
        return null;
    }

    private static (double? Dx, double? Dy) Move(double dx, double dy, string direction, double amount) => direction switch
    {
        "上" => (dx, dy + amount),
        "下" => (dx, dy - amount),
        "左" => (dx - amount, dy),
        _ => (dx + amount, dy)
    };

    /// <summary>"もう少し上に", "小さく", "明朝体にして", "ハイフンを長く": a change of how the last drawing looks.</summary>
    public static bool IsAdjustment(string request)
    {
        var text = request.Normalize(System.Text.NormalizationForm.FormKC);
        if (Regex.IsMatch(text, "「")) return false;
        if (Regex.IsMatch(text, $"(?:文字|字|色)を?(?:{Colours})(?:色)?(?:に|へ)|(?:{Colours})(?:色)?(?:の文字|で書|に変え|にして)")) return true;
        return Regex.IsMatch(text, "上に|下に|左に|右に|上げ|下げ|大きく|小さく|明朝|ゴシック|教科書体|太字|太く|細く|ハイフン|色に|色を|" + Points + "|中間|長く|短く|スペース") &&
               Regex.IsMatch(text, "もう少し|もっと|少し|ちょっと|やっぱり|にして|に変えて|くして|変更|くらい|ぐらい|程度|して$|中間|長く|短く");
    }

    /// <summary>The last drawing ops with the adjustment applied; null when nothing drawn could be adjusted.</summary>
    public static IReadOnlyList<PdfOp>? Adjust(string request, IReadOnlyList<PdfOp> last)
    {
        var text = request.Normalize(System.Text.NormalizationForm.FormKC);
        var drawn = last.Where(Draws).ToArray();
        if (drawn.Length == 0) return null;
        // Which drawing: the one named ("ページ番号を…", "透かしを…"), else all of them.
        bool Named(PdfOp op) => op.Op switch
        {
            "page-numbers" => Regex.IsMatch(text, "ページ番号|番号|ハイフン|ノンブル"),
            "watermark" => Regex.IsMatch(text, "透かし"),
            _ => Regex.IsMatch(text, "文字|スタンプ|社外秘")
        };
        var targets = drawn.Any(Named) ? drawn.Where(Named).ToHashSet() : drawn.ToHashSet();
        var step = Regex.IsMatch(text, "ちょっと|少しだけ|わずかに") ? 4 : Regex.IsMatch(text, "もう少し|少し") ? 8 : 12;
        var changed = false;
        var result = last.Select(op =>
        {
            if (!targets.Contains(op)) return op;
            var next = WithLook(op, text);
            var (dx, dy) = (next.Dx ?? 0, next.Dy ?? 0);
            if (!Regex.IsMatch(text, Points))
                foreach (Match move in Regex.Matches(text, "(上|下|左|右)(?:に|へ|げ)"))
                {
                    var moved = Move(dx, dy, move.Groups[1].Value, step);
                    (dx, dy) = (moved.Dx!.Value, moved.Dy!.Value);
                }
            var size = next.Size;
            // Standard sizes as drawn: numbers 10pt, stamps 14pt, a short watermark up to 96pt.
            var standard = op.Op == "page-numbers" ? 10 : op.Op == "stamp" ? 14 : 96;
            var sizeStep = op.Op == "watermark" ? (step >= 8 ? 12 : 6) : (step >= 8 ? 2 : 1);
            if (!Regex.IsMatch(text, Points) && Regex.IsMatch(text, "大きく")) size = Math.Min(144, (size ?? standard) + sizeStep);
            if (!Regex.IsMatch(text, Points) && Regex.IsMatch(text, "小さく")) size = Math.Max(4, (size ?? standard) - sizeStep);
            var bold = Regex.IsMatch(text, "細く|太字をやめ|太字を外") ? false : next.Bold;
            var format = next.Text;
            if (op.Op == "page-numbers" && format is { Length: > 0 })
            {
                if (Regex.IsMatch(text, "ハイフン|-|線"))
                {
                    var dash = Regex.IsMatch(text, "中間") ? "–" : Regex.IsMatch(text, "長く") ? "－" : Regex.IsMatch(text, "短く") ? "-" : null;
                    if (dash is not null) format = Regex.Replace(format, "[-－―–—]", dash);
                }
                if (Regex.IsMatch(text, "スペースを(?:入れ|空け|あけ)")) format = Regex.Replace(format, @"\s*([-－―–—/])\s*", " $1 ");
                if (Regex.IsMatch(text, "スペースを(?:なく|消|詰め|取)")) format = Regex.Replace(format, @"\s*([-－―–—/])\s*", "$1");
            }
            var adjusted = next with { Dx = dx == 0 ? null : dx, Dy = dy == 0 ? null : dy, Size = size, Bold = bold, Text = format };
            if (adjusted != op) changed = true;
            return adjusted;
        }).ToArray();
        return changed ? result : null;
    }
}
