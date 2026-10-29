using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;
using Pig = UglyToad.PdfPig;

namespace SafeLocalHarness.Core;

/// <summary>What an in-place edit did, or why it could not be done exactly (then nothing is written).</summary>
public sealed record PdfInPlaceResult(bool Done, string Detail, string? OutputPath = null);

/// <summary>What the saved PDF shows compared with the original (read back, never trusted from the edit itself).</summary>
public sealed record PdfEditVerification(bool Passed, IReadOnlyList<string> Problems);

/// <summary>
/// Editing a PDF's own objects instead of painting over them (指示書 3.2/3.3): text is changed inside the show command
/// with the letters its embedded font already has, and a rule drawn as a thin filled rectangle gets a new thickness.
/// When the exact edit is not possible (a letter the subset font lacks, a word split over commands) nothing is written
/// and the reason is returned. The original is opened read-only and the result goes to a new file.
/// </summary>
public static class PdfInPlaceEdit
{
    public static PdfInPlaceResult ReplaceText(string source, string output, int pageNumber, string find, string replacement, int occurrence = 1)
    {
        if (Same(source, output)) return new(false, "the original is never overwritten");
        using var document = PdfSharp.Pdf.IO.PdfReader.Open(source, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify);
        if (pageNumber < 1 || pageNumber > document.PageCount) return new(false, $"page {pageNumber} does not exist");
        var page = document.Pages[pageNumber - 1];
        var fonts = PdfStructureSurvey.FontsOf(page);
        var content = ContentReader.ReadContent(page);
        PdfStructureSurvey.FontInfo? font = null;
        var seen = 0;
        // The pieces of one text block (BT…ET) in drawing order: a word is often split over several show commands, each
        // placed on its own (「挨」 and 「拶」, 開催要領 2026-10-20).
        var block = new List<(CString Piece, PdfStructureSurvey.FontInfo? Font, string Text)>();
        foreach (var op in Operators(content))
        {
            switch (op.OpCode.OpCodeName)
            {
                case OpCodeName.BT: block.Clear(); continue;
                case OpCodeName.Tf when op.Operands.Count > 0 && op.Operands[0] is CName name: fonts.TryGetValue(name.Name, out font); continue;
                case OpCodeName.ET:
                    var joined = string.Concat(block.Select(part => part.Text));
                    for (var at = joined.IndexOf(find, StringComparison.Ordinal); at >= 0; at = joined.IndexOf(find, at + find.Length, StringComparison.Ordinal))
                    {
                        if (++seen != occurrence) continue;
                        var (done, detail) = Splice(block, at, find.Length, replacement);
                        if (!done) return new(false, detail);
                        Write(page, content);
                        document.Save(output);
                        return new(true, $"page {pageNumber}: 「{find}」→「{replacement}」 {detail}", output);
                    }
                    block.Clear();
                    continue;
            }
            foreach (var piece in Strings(op)) block.Add((piece, font, PdfStructureSurvey.Decode(piece.Value, font)));
        }
        return new(false, seen == 0 ? $"「{find}」 is not on page {pageNumber}" : $"only {seen} occurrence(s)");
    }

    /// <summary>
    /// The replacement goes into the piece where the match starts (with that piece's font); the rest of the match is
    /// taken out of the following pieces, which keep their own positions. Every piece is encoded before any is changed.
    /// </summary>
    private static (bool Done, string Detail) Splice(List<(CString Piece, PdfStructureSurvey.FontInfo? Font, string Text)> block, int at, int length, string replacement)
    {
        var updates = new List<(CString Piece, string Value)>();
        var offset = 0;
        var placed = false;
        foreach (var (piece, font, text) in block)
        {
            var start = offset;
            var end = offset + text.Length;
            offset = end;
            if (end <= at || start >= at + length) continue;
            if (font is null) return (false, "the text has no font");
            var from = Math.Max(at, start) - start;
            var to = Math.Min(at + length, end) - start;
            var next = text[..from] + (placed ? "" : replacement) + text[to..];
            placed = true;
            var encoded = Encode(next, font, out var missing);
            if (encoded is null) return (false, $"the embedded font {font.Facts.BaseFont} has no 「{missing}」; a substitute font would be needed (preview required)");
            updates.Add((piece, encoded));
        }
        foreach (var (piece, value) in updates) piece.Value = value;
        return (true, updates.Count == 1 ? "in its show command, same font" : $"across {updates.Count} show commands, same font");
    }

    /// <summary>Thin filled rectangles (rules) on a page get the new thickness, kept centred on the old line.</summary>
    public static PdfInPlaceResult SetRuleThickness(string source, string output, int pageNumber, double thickness, double maxOldThickness = 2.0, int? onlyIndex = null)
    {
        if (Same(source, output)) return new(false, "the original is never overwritten");
        using var document = PdfSharp.Pdf.IO.PdfReader.Open(source, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify);
        if (pageNumber < 1 || pageNumber > document.PageCount) return new(false, $"page {pageNumber} does not exist");
        var page = document.Pages[pageNumber - 1];
        var content = ContentReader.ReadContent(page);
        var index = 0;
        var changed = 0;
        // Sizes are judged in points on the page: the drawing is often scaled (0.24 × 4.16667 in 開催要領, where a
        // 2.75-unit rectangle is a 0.66 pt rule), so the current transformation's scale is followed through q/Q/cm.
        var scales = new Stack<(double X, double Y)>();
        var scale = (X: 1.0, Y: 1.0);
        foreach (var op in Operators(content))
        {
            switch (op.OpCode.OpCodeName)
            {
                case OpCodeName.q: scales.Push(scale); continue;
                case OpCodeName.Q: if (scales.Count > 0) scale = scales.Pop(); continue;
                case OpCodeName.cm when op.Operands.Count == 6 && op.Operands.Select(PdfStructureSurvey.Number).ToArray() is { } m && m.All(value => value is not null):
                    scale = (scale.X * Math.Sqrt(m[0]!.Value * m[0]!.Value + m[1]!.Value * m[1]!.Value), scale.Y * Math.Sqrt(m[2]!.Value * m[2]!.Value + m[3]!.Value * m[3]!.Value));
                    continue;
            }
            if (op.OpCode.OpCodeName != OpCodeName.re || op.Operands.Count != 4) continue;
            var values = op.Operands.Select(PdfStructureSurvey.Number).ToArray();
            if (values.Any(value => value is null)) continue;
            double x = values[0]!.Value, y = values[1]!.Value, w = values[2]!.Value, h = values[3]!.Value;
            double pw = Math.Abs(w * scale.X), ph = Math.Abs(h * scale.Y);
            var horizontal = ph <= maxOldThickness && pw > ph * 4;
            var vertical = pw <= maxOldThickness && ph > pw * 4;
            if (!horizontal && !vertical) continue;
            if (onlyIndex is { } only && index++ != only) continue;
            if (horizontal) { var nh = Math.Sign(h == 0 ? 1 : h) * thickness / scale.Y; y += (h - nh) / 2; h = nh; }
            else { var nw = Math.Sign(w == 0 ? 1 : w) * thickness / scale.X; x += (w - nw) / 2; w = nw; }
            op.Operands.Clear();
            foreach (var value in new[] { x, y, w, h }) op.Operands.Add(new CReal { Value = Math.Round(value, 4) });
            changed++;
        }
        if (changed == 0) return new(false, $"no thin rule rectangle on page {pageNumber}");
        Write(page, content);
        document.Save(output);
        return new(true, $"page {pageNumber}: {changed} rule(s) set to {thickness} pt", output);
    }

    /// <summary>
    /// The saved PDF against the original: it opens, the expected text is there and the removed text is not, every
    /// other page reads exactly as before, the page's other text is unchanged, the number of drawn shapes is the same,
    /// and no letters of the page overlap letters that did not overlap before.
    /// </summary>
    public static PdfEditVerification Verify(string original, string edited, int pageNumber, string? expected = null, string? removed = null, string? find = null, double? ruleThickness = null)
    {
        var problems = new List<string>();
        try
        {
            // The rules really have the new thickness in the saved file (counting shapes alone passed an edit that wrote nothing).
            if (ruleThickness is { } wanted)
            {
                // Rectangle by rectangle in drawing order: each one that was a rule now has the new thickness across it and
                // its old length along it; every other rectangle is unchanged.
                var was = RectangleSizes(original, pageNumber);
                var now = RectangleSizes(edited, pageNumber);
                if (now.Count != was.Count) problems.Add($"page {pageNumber} had {was.Count} rectangles and now has {now.Count}");
                int rules = 0, off = 0, moved = 0;
                for (var i = 0; i < Math.Min(was.Count, now.Count); i++)
                {
                    var (w, h) = was[i];
                    var horizontal = h <= 2.0 && w > h * 4;
                    var vertical = w <= 2.0 && h > w * 4;
                    bool Near(double a, double b) => Math.Abs(a - b) <= 0.01;
                    if (horizontal || vertical)
                    {
                        rules++;
                        var across = horizontal ? now[i].Height : now[i].Width;
                        var along = horizontal ? (now[i].Width, w) : (now[i].Height, h);
                        if (!Near(across, wanted) || !Near(along.Item1, along.Item2)) off++;
                    }
                    else if (!Near(now[i].Width, w) || !Near(now[i].Height, h)) moved++;
                }
                if (rules == 0) problems.Add($"page {pageNumber} has no rules to change");
                if (off > 0) problems.Add($"{off} of {rules} rule(s) on page {pageNumber} are not {wanted} pt in the saved file");
                if (moved > 0) problems.Add($"{moved} rectangle(s) that are not rules changed size on page {pageNumber}");
            }
            using var before = Pig.PdfDocument.Open(original);
            using var after = Pig.PdfDocument.Open(edited);
            if (before.NumberOfPages != after.NumberOfPages) problems.Add($"page count changed {before.NumberOfPages} → {after.NumberOfPages}");
            for (var number = 1; number <= Math.Min(before.NumberOfPages, after.NumberOfPages); number++)
            {
                string Plain(Pig.PdfDocument document) => Regex.Replace(document.GetPage(number).Text, @"\s+", "");
                var was = Plain(before);
                var now = Plain(after);
                if (number != pageNumber) { if (was != now) problems.Add($"page {number} was not edited but its text changed"); continue; }
                if (expected is not null && !now.Contains(expected, StringComparison.Ordinal)) problems.Add($"「{expected}」 cannot be read from page {number}");
                if (removed is not null && Count(now, removed) >= Count(was, removed)) problems.Add($"「{removed}」 is still in page {number}'s text (hidden, not removed?)");
                // The rest of the page reads the same once the edit itself is taken out.
                if (find is not null && expected is not null && ReplaceFirst(was, find, expected) != now && !was.Equals(now, StringComparison.Ordinal))
                    problems.Add($"text other than the edit changed on page {number}");
                var overlapsBefore = Overlaps(before.GetPage(number));
                var overlapsAfter = Overlaps(after.GetPage(number));
                if (overlapsAfter > overlapsBefore) problems.Add($"{overlapsAfter - overlapsBefore} letter(s) now overlap other letters on page {number} (the new text collides)");
            }
            var shapesBefore = PdfStructureSurvey.Survey(original);
            var shapesAfter = PdfStructureSurvey.Survey(edited);
            foreach (var (was, now) in shapesBefore.Zip(shapesAfter))
                if (was.Rectangles != now.Rectangles || was.LineSegments != now.LineSegments || was.Fills != now.Fills || was.Strokes != now.Strokes)
                    problems.Add($"page {was.Number}: the drawn shapes changed (rectangles {was.Rectangles}→{now.Rectangles}, fills {was.Fills}→{now.Fills})");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            problems.Add("the saved PDF cannot be read back: " + ex.GetType().Name);
        }
        return new(problems.Count == 0, problems);
    }

    private static int Overlaps(Pig.Content.Page page)
    {
        var boxes = page.Letters.Where(letter => !string.IsNullOrWhiteSpace(letter.Value)).Select(letter => letter.BoundingBox).ToArray();
        var count = 0;
        for (var i = 0; i < boxes.Length; i++)
            for (var j = i + 1; j < boxes.Length; j++)
            {
                var a = boxes[i]; var b = boxes[j];
                var dx = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
                var dy = Math.Min(a.Top, b.Top) - Math.Max(a.Bottom, b.Bottom);
                if (dx > Math.Min(a.Width, b.Width) * 0.3 && dy > Math.Min(a.Height, b.Height) * 0.3) count++;
            }
        return count;
    }

    private static int Count(string text, string word)
    {
        var count = 0;
        for (var at = text.IndexOf(word, StringComparison.Ordinal); at >= 0; at = text.IndexOf(word, at + word.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    private static string ReplaceFirst(string text, string find, string replacement)
    {
        var at = text.IndexOf(find, StringComparison.Ordinal);
        return at < 0 ? text : text[..at] + replacement + text[(at + find.Length)..];
    }

    private static bool Same(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<COperator> Operators(CSequence sequence)
    {
        foreach (var item in sequence)
            if (item is CSequence inner) { foreach (var op in Operators(inner)) yield return op; }
            else if (item is COperator op) yield return op;
    }

    private static IEnumerable<CString> Strings(COperator op) => op.OpCode.OpCodeName switch
    {
        OpCodeName.Tj or OpCodeName.QuoteSingle or OpCodeName.QuoteDouble when op.Operands.Count > 0 && op.Operands[^1] is CString single => [single],
        OpCodeName.TJ when op.Operands.Count > 0 && op.Operands[0] is CArray array => array.OfType<CString>().ToArray(),
        _ => []
    };

    /// <summary>The codes the font draws the text with, or null with the first letter it does not have.</summary>
    private static string? Encode(string text, PdfStructureSurvey.FontInfo font, out string missing)
    {
        missing = "";
        var inverse = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (code, unicode) in font.ToUnicode) inverse.TryAdd(unicode, code);
        var builder = new StringBuilder();
        foreach (var letter in text.EnumerateRunes().Select(rune => rune.ToString()))
        {
            if (!inverse.TryGetValue(letter, out var code)) { missing = letter; return null; }
            if (font.TwoByte) builder.Append((char)(code >> 8)).Append((char)(code & 0xFF));
            else builder.Append((char)code);
        }
        return builder.ToString();
    }

    // The page's /Contents is pointed to one new stream with the edited commands (writing into the merged stream
    // PDFsharp hands out did not reach the saved file: the edit "succeeded" and nothing changed, caught by Verify).
    private static void Write(PdfPage page, CSequence content)
    {
        var stream = new PdfDictionary(page.Owner);
        stream.CreateStream(content.ToContent());
        page.Owner.Internals.AddObject(stream);
        page.Elements.SetReference("/Contents", stream);
    }

    /// <summary>Thickness in points of every thin filled rectangle (rule) on a page, in drawing order.</summary>
    public static IReadOnlyList<double> RuleThicknesses(string path, int pageNumber, double maxThickness = 2.0) =>
        RectangleSizes(path, pageNumber).Select(size => size.Height <= maxThickness && size.Width > size.Height * 4 ? size.Height :
                                                        size.Width <= maxThickness && size.Height > size.Width * 4 ? size.Width : -1)
            .Where(thickness => thickness >= 0).Select(thickness => Math.Round(thickness, 3)).ToArray();

    /// <summary>The size in points of every rectangle (re) on a page, in drawing order, so a saved file can be compared one by one.</summary>
    public static IReadOnlyList<(double Width, double Height)> RectangleSizes(string path, int pageNumber)
    {
        using var document = PdfSharp.Pdf.IO.PdfReader.Open(path, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        var result = new List<(double Width, double Height)>();
        var scales = new Stack<(double X, double Y)>();
        var scale = (X: 1.0, Y: 1.0);
        foreach (var op in Operators(ContentReader.ReadContent(document.Pages[pageNumber - 1])))
        {
            switch (op.OpCode.OpCodeName)
            {
                case OpCodeName.q: scales.Push(scale); continue;
                case OpCodeName.Q: if (scales.Count > 0) scale = scales.Pop(); continue;
                case OpCodeName.cm when op.Operands.Count == 6 && op.Operands.Select(PdfStructureSurvey.Number).ToArray() is { } m && m.All(value => value is not null):
                    scale = (scale.X * Math.Sqrt(m[0]!.Value * m[0]!.Value + m[1]!.Value * m[1]!.Value), scale.Y * Math.Sqrt(m[2]!.Value * m[2]!.Value + m[3]!.Value * m[3]!.Value));
                    continue;
            }
            if (op.OpCode.OpCodeName != OpCodeName.re || op.Operands.Count != 4) continue;
            var values = op.Operands.Select(PdfStructureSurvey.Number).ToArray();
            if (values.Any(value => value is null)) continue;
            result.Add((Math.Abs(values[2]!.Value * scale.X), Math.Abs(values[3]!.Value * scale.Y)));
        }
        return result;
    }
}
