using System.Buffers.Binary;
using PdfSharp.Drawing;
using Pig = UglyToad.PdfPig;
using Sharp = PdfSharp.Pdf;

namespace SafeLocalHarness.Core;

/// <summary>
/// 4-4b steps that need page images: OCR text layers, redaction (the page becomes a picture with the words painted
/// black, then OCR confirms they can no longer be read) and pages saved as PNG files.
/// </summary>
public static class PdfImagingSteps
{
    /// <summary>
    /// Pages are rendered at 4 × 72 dpi = 288 dpi for OCR and redaction (A4 = 2380 × 3368 pixels): Windows OCR missed a
    /// small red 14 pt 「社外秘」 stamp at 144 and 216 dpi and read it at 288 dpi (drill 2026-10-12).
    /// </summary>
    public const double Scale = 4;

    /// <summary>Pages saved as PNG files are 2 × 72 dpi = 144 dpi.</summary>
    public const double ImageScale = 2;

    public static bool NeedsImaging(PdfEditPreview preview) =>
        preview.Ops.Any(op => op.Op is "ocr" or "redact" or "to-images");

    /// <summary>The preview with OCR text filled into its "ocr" marks, and the redacted page images by output page.</summary>
    public sealed record Prepared(PdfEditPreview Preview, IReadOnlyDictionary<(int Output, int Position), byte[]> PageImages);

    public static async Task<Prepared> PrepareAsync(PdfEditPreview preview, IPdfImaging imaging, CancellationToken cancellationToken)
    {
        var images = new Dictionary<(int, int), byte[]>();
        var outputs = new List<IReadOnlyList<PdfPageRef>>();
        for (var index = 0; index < preview.Outputs.Count; index++)
        {
            var pages = new List<PdfPageRef>();
            for (var position = 0; position < preview.Outputs[index].Count; position++)
            {
                var page = preview.Outputs[index][position];
                var marks = page.Marks ?? [];
                if (marks.Any(mark => mark.Kind == "redact"))
                {
                    var png = await imaging.RenderPageAsync(preview.Sources[page.File], page.Page, Scale, cancellationToken);
                    var boxes = new List<PixelBox>();
                    foreach (var mark in marks.Where(mark => mark.Kind == "redact"))
                    {
                        var found = await FindAsync(preview.Sources[page.File], page.Page, mark.Text, png, imaging, cancellationToken);
                        if (found.Count == 0) throw new InvalidOperationException($"{position + 1}ページ目に「{mark.Text}」が見つからないため、黒塗りのファイルは作りません。");
                        boxes.AddRange(found);
                    }
                    images[(index, position)] = await imaging.FillBoxesAsync(png, boxes, cancellationToken);
                }
                if (marks.Any(mark => mark.Kind == "ocr"))
                {
                    var png = await imaging.RenderPageAsync(preview.Sources[page.File], page.Page, Scale, cancellationToken);
                    var ocr = await imaging.RecognizeAsync(png, cancellationToken);
                    var lines = ocr.Lines.Select(line => new OcrLine(line.Text, line.Words.Select(word =>
                        new OcrWord(word.Text, word.X / Scale, word.Y / Scale, word.Width / Scale, word.Height / Scale)).ToArray())).ToArray();
                    page = page with { Marks = marks.Select(mark => mark.Kind == "ocr" ? mark with { Text = new OcrPage(ocr.Width, ocr.Height, lines).Text, Lines = lines } : mark).ToArray() };
                }
                pages.Add(page);
            }
            outputs.Add(pages);
        }
        return new Prepared(preview with { Outputs = outputs }, images);
    }

    /// <summary>
    /// Boxes (pixels at <see cref="Scale"/>) around every occurrence of the text: from the PDF's own letters when the page
    /// has text, otherwise from OCR of the page image.
    /// </summary>
    public static async Task<IReadOnlyList<PixelBox>> FindAsync(string pdfPath, int pageNumber, string text, byte[] png, IPdfImaging imaging, CancellationToken cancellationToken)
    {
        var target = PdfDocumentReader.Fingerprint(text);
        if (target.Length == 0) return [];
        var boxes = new List<PixelBox>();
        using (var document = Pig.PdfDocument.Open(pdfPath))
        {
            var page = document.GetPage(pageNumber);
            var letters = page.Letters.Where(letter => !string.IsNullOrWhiteSpace(letter.Value)).ToArray();
            var joined = string.Concat(letters.Select(letter => letter.Value));
            if (joined.Length > 0)
            {
                // One character of the joined text per letter (letters are single characters in practice).
                if (joined.Length == letters.Length)
                    foreach (var start in Occurrences(joined, target))
                    {
                        var hit = letters.Skip(start).Take(target.Length).ToArray();
                        var left = hit.Min(letter => letter.BoundingBox.Left);
                        var right = hit.Max(letter => letter.BoundingBox.Right);
                        var top = hit.Max(letter => letter.BoundingBox.Top);
                        var bottom = hit.Min(letter => letter.BoundingBox.Bottom);
                        boxes.Add(Padded(left * Scale, (page.Height - top) * Scale, (right - left) * Scale, (top - bottom) * Scale));
                    }
                return boxes;
            }
        }
        var ocr = await imaging.RecognizeAsync(png, cancellationToken);
        var words = ocr.Lines.SelectMany(line => line.Words).Where(word => PdfDocumentReader.Fingerprint(word.Text).Length > 0).ToArray();
        var owners = new List<int>();
        var text2 = new System.Text.StringBuilder();
        for (var index = 0; index < words.Length; index++)
            foreach (var character in PdfDocumentReader.Fingerprint(words[index].Text)) { text2.Append(character); owners.Add(index); }
        var exact = Occurrences(text2.ToString(), target).ToArray();
        // Scanned pages: OCR may misread a character of the word, so near matches are painted too.
        foreach (var start in exact.Length > 0 || target.Length < 3 ? exact : Similar(text2.ToString(), target))
        {
            var hit = owners.Skip(start).Take(target.Length).Distinct().Select(index => words[index]).ToArray();
            var left = hit.Min(word => word.X);
            var top = hit.Min(word => word.Y);
            boxes.Add(Padded(left, top, hit.Max(word => word.X + word.Width) - left, hit.Max(word => word.Y + word.Height) - top));
        }
        return boxes;
    }

    /// <summary>
    /// Start positions where <paramref name="text"/> holds the target allowing OCR misreadings: up to one differing
    /// character per four (at least one). OCR read "1250" as "凵250" (drill 2026-10-12), so an exact search would call a
    /// still-readable word gone.
    /// </summary>
    public static IReadOnlyList<int> Similar(string text, string target)
    {
        var allowed = Math.Max(1, target.Length / 4);
        var starts = new List<int>();
        for (var start = 0; start + target.Length <= text.Length; start++)
        {
            var differences = 0;
            for (var index = 0; index < target.Length && differences <= allowed; index++)
                if (char.ToUpperInvariant(text[start + index]) != char.ToUpperInvariant(target[index])) differences++;
            if (differences <= allowed) starts.Add(start);
        }
        return starts;
    }

    private static PixelBox Padded(double x, double y, double width, double height) => new(x - 3, y - 3, width + 6, height + 6);

    private static IEnumerable<int> Occurrences(string text, string target)
    {
        for (var index = text.IndexOf(target, StringComparison.Ordinal); index >= 0; index = text.IndexOf(target, index + 1, StringComparison.Ordinal))
            yield return index;
    }

    /// <summary>A page that is only the given picture, the size of the source page.</summary>
    public static Sharp.PdfPage ImagePage(Sharp.PdfPage page, double width, double height, byte[] png)
    {
        page.Width = XUnit.FromPoint(width);
        page.Height = XUnit.FromPoint(height);
        using var graphics = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Replace);
        using var stream = new MemoryStream(png);
        using var image = XImage.FromStream(stream);
        graphics.DrawImage(image, 0, 0, width, height);
        return page;
    }

    /// <summary>A redacted word must not be readable any more: neither in the page's text nor by OCR of the result.</summary>
    public static async Task<IReadOnlyList<string>> VerifyRedactionsAsync(PdfEditPreview preview, IReadOnlyList<string> outputs, IPdfImaging imaging, CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        for (var index = 0; index < outputs.Count; index++)
            for (var position = 0; position < preview.Outputs[index].Count; position++)
            {
                var redacted = (preview.Outputs[index][position].Marks ?? []).Where(mark => mark.Kind == "redact").Select(mark => PdfDocumentReader.Fingerprint(mark.Text)).ToArray();
                if (redacted.Length == 0) continue;
                var png = await imaging.RenderPageAsync(outputs[index], position + 1, Scale, cancellationToken);
                var read = PdfDocumentReader.Fingerprint((await imaging.RecognizeAsync(png, cancellationToken)).Text);
                var text = PdfDocumentReader.Fingerprint(PdfDocumentReader.Read(outputs[index]).Pages[position].Text);
                foreach (var word in redacted.Where(word => text.Contains(word, StringComparison.Ordinal) || (word.Length >= 3 ? Similar(read, word).Count > 0 : read.Contains(word, StringComparison.Ordinal))))
                    problems.Add($"{Path.GetFileName(outputs[index])} の{position + 1}ページ目で「{word}」がまだ読み取れます（読み違いを含めて照合）");
            }
        return problems;
    }

    /// <summary>Each page of the plan as a PNG file in a new folder next to the PDF ("…_LS画像\p001.png").</summary>
    public static async Task<IReadOnlyList<string>> WriteImagesAsync(PdfEditPreview preview, IPdfImaging imaging, CancellationToken cancellationToken)
    {
        var source = preview.Sources[0];
        var folder = Path.Combine(Path.GetDirectoryName(source)!, Path.GetFileNameWithoutExtension(source) + "_LS画像");
        for (var index = 2; Directory.Exists(folder); index++)
            folder = Path.Combine(Path.GetDirectoryName(source)!, $"{Path.GetFileNameWithoutExtension(source)}_LS画像({index})");
        Directory.CreateDirectory(folder);
        var files = new List<string>();
        foreach (var page in preview.Outputs.SelectMany(output => output))
        {
            var png = await imaging.RenderPageAsync(preview.Sources[page.File], page.Page, ImageScale, cancellationToken);
            var path = Path.Combine(folder, $"p{page.Page:D3}.png");
            await File.WriteAllBytesAsync(path, png, cancellationToken);
            files.Add(path);
        }
        return files;
    }

    /// <summary>Every planned page has a PNG whose header names a real picture size.</summary>
    public static IReadOnlyList<string> VerifyImages(PdfEditPreview preview, IReadOnlyList<string> files)
    {
        var problems = new List<string>();
        var expected = preview.Outputs.Sum(output => output.Count);
        if (files.Count != expected) problems.Add($"画像が {files.Count}枚（予定 {expected}枚）");
        foreach (var file in files)
        {
            var bytes = File.Exists(file) ? File.ReadAllBytes(file) : [];
            var png = bytes.Length > 24 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
            if (!png || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16)) == 0 || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20)) == 0)
                problems.Add($"{Path.GetFileName(file)} が画像として読めません");
        }
        return problems;
    }
}
