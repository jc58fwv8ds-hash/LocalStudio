using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace SafeLocalHarness.Core;

/// <summary>
/// Phase C: the text of a PDF written out as plain text or as a Word document (one page per Word page), and two PDFs
/// compared line by line. The written file is read back and must hold exactly the PDF's text.
/// </summary>
public static class PdfExport
{
    public static void WriteDocx(string path, IReadOnlyList<string> pages)
    {
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var body = new W.Body();
        for (var index = 0; index < pages.Count; index++)
        {
            foreach (var line in pages[index].Replace("\r\n", "\n").Split('\n'))
                body.Append(new W.Paragraph(new W.Run(new W.Text(line) { Space = SpaceProcessingModeValues.Preserve })));
            if (index < pages.Count - 1) body.Append(new W.Paragraph(new W.Run(new W.Break { Type = W.BreakValues.Page })));
        }
        body.Append(new W.SectionProperties());
        document.AddMainDocumentPart().Document = new W.Document(body);
    }

    /// <summary>The written file holds every page's text (spaces aside), in order.</summary>
    public static IEnumerable<string> Verify(string path, IReadOnlyList<string> pages)
    {
        if (!File.Exists(path)) { yield return "書き出したファイルが見つかりません"; yield break; }
        string written;
        if (path.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
        {
            using var document = WordprocessingDocument.Open(path, false);
            written = string.Concat(document.MainDocumentPart!.Document!.Body!.Descendants<W.Text>().Select(text => text.Text));
        }
        else written = File.ReadAllText(path, Encoding.UTF8);
        if (AnswerEvidence.Squash(written) != AnswerEvidence.Squash(string.Concat(pages)))
            yield return "書き出した文字が PDF の文字と合いません";
    }

    /// <summary>
    /// The lines that differ between two PDFs (by their text; spaces ignored), as "only in A" / "only in B" with page
    /// numbers. Pictures and layout are not compared, and the answer says so.
    /// </summary>
    public static string Compare(PdfSummary first, PdfSummary second)
    {
        static List<(int Page, string Line)> Lines(PdfSummary summary) => summary.Pages
            .SelectMany(page => Regex.Split(page.Text.Replace("\r\n", "\n"), @"\n|(?<=[。．])").Select(line => (page.Number, Line: line.Trim())))
            .Where(item => AnswerEvidence.Squash(item.Line).Length > 0).ToList();
        var a = Lines(first);
        var b = Lines(second);
        var keyA = a.Select(item => AnswerEvidence.Squash(item.Line)).ToArray();
        var keyB = b.Select(item => AnswerEvidence.Squash(item.Line)).ToArray();
        // Longest common subsequence of lines (bounded for large files).
        if ((long)keyA.Length * keyB.Length > 4_000_000) return "ページが多すぎるため、ここでは比べられません（それぞれ数十ページまで）。";
        var table = new int[keyA.Length + 1, keyB.Length + 1];
        for (var i = keyA.Length - 1; i >= 0; i--)
            for (var j = keyB.Length - 1; j >= 0; j--)
                table[i, j] = keyA[i] == keyB[j] ? table[i + 1, j + 1] + 1 : Math.Max(table[i + 1, j], table[i, j + 1]);
        var onlyA = new List<string>();
        var onlyB = new List<string>();
        int x = 0, y = 0;
        while (x < keyA.Length && y < keyB.Length)
        {
            if (keyA[x] == keyB[y]) { x++; y++; }
            else if (table[x + 1, y] >= table[x, y + 1]) onlyA.Add($"{a[x].Page}ページ目「{Short(a[x++].Line)}」");
            else onlyB.Add($"{b[y].Page}ページ目「{Short(b[y++].Line)}」");
        }
        while (x < keyA.Length) onlyA.Add($"{a[x].Page}ページ目「{Short(a[x++].Line)}」");
        while (y < keyB.Length) onlyB.Add($"{b[y].Page}ページ目「{Short(b[y++].Line)}」");
        var nameA = Path.GetFileName(first.Path);
        var nameB = Path.GetFileName(second.Path);
        if (onlyA.Count == 0 && onlyB.Count == 0)
            return $"「{nameA}」と「{nameB}」の文字は同じです（{first.Pages.Count}ページと{second.Pages.Count}ページ。図や配置は比べていません）。";
        var text = new StringBuilder($"「{nameA}」と「{nameB}」で、文字の違う箇所が {onlyA.Count + onlyB.Count} か所あります（図や配置は比べていません）。\n");
        if (onlyA.Count > 0) text.Append($"\n「{nameA}」だけにある文:\n").Append(string.Join("\n", onlyA.Take(20).Select(item => "・" + item))).Append(onlyA.Count > 20 ? $"\n…ほか {onlyA.Count - 20} か所" : "");
        if (onlyB.Count > 0) text.Append($"\n\n「{nameB}」だけにある文:\n").Append(string.Join("\n", onlyB.Take(20).Select(item => "・" + item))).Append(onlyB.Count > 20 ? $"\n…ほか {onlyB.Count - 20} か所" : "");
        return text.ToString();
    }

    public static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg"];
    public const int MaxImages = 50;

    /// <summary>
    /// Images made into one PDF, one image per A4 page (turned to the image's orientation), scaled to fit with a small
    /// margin. Read back: one page per image, each holding a picture, in the image's orientation.
    /// </summary>
    public static IReadOnlyList<string> FromImages(IReadOnlyList<string> images, string output)
    {
        if (images.Count is 0 or > MaxImages) return [$"画像は 1～{MaxImages} 枚にしてください"];
        var orientations = new List<bool>();
        using (var document = new PdfSharp.Pdf.PdfDocument())
        {
            foreach (var path in images)
            {
                PdfSharp.Drawing.XImage image;
                try { image = PdfSharp.Drawing.XImage.FromFile(path); }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or NotSupportedException)
                {
                    return [$"画像「{Path.GetFileName(path)}」を読めません（PNG・JPEG を選んでください）"];
                }
                using var disposeImage = image;
                var landscape = image.PixelWidth > image.PixelHeight;
                orientations.Add(landscape);
                var page = document.AddPage();
                page.Width = PdfSharp.Drawing.XUnit.FromPoint(landscape ? 842 : 595);
                page.Height = PdfSharp.Drawing.XUnit.FromPoint(landscape ? 595 : 842);
                using var graphics = PdfSharp.Drawing.XGraphics.FromPdfPage(page);
                const double margin = 18;
                var scale = Math.Min((page.Width.Point - 2 * margin) / image.PixelWidth, (page.Height.Point - 2 * margin) / image.PixelHeight);
                var (width, height) = (image.PixelWidth * scale, image.PixelHeight * scale);
                graphics.DrawImage(image, (page.Width.Point - width) / 2, (page.Height.Point - height) / 2, width, height);
            }
            document.Save(output);
        }
        var problems = new List<string>();
        using var written = UglyToad.PdfPig.PdfDocument.Open(output);
        if (written.NumberOfPages != images.Count) problems.Add($"ページ数が {written.NumberOfPages}（画像 {images.Count} 枚）");
        for (var number = 1; number <= Math.Min(written.NumberOfPages, images.Count); number++)
        {
            var page = written.GetPage(number);
            if (!page.GetImages().Any()) problems.Add($"{number}ページ目に画像がありません");
            if ((page.Width > page.Height) != orientations[number - 1]) problems.Add($"{number}ページ目の向きが画像と違います");
        }
        return problems;
    }

    private static string Short(string line) => line.Length <= 80 ? line : line[..80] + "…";
}
