using System.Buffers.Binary;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf.Annotations;
using Pig = UglyToad.PdfPig;
using Sharp = PdfSharp.Pdf;

namespace SafeLocalHarness.Core;

/// <summary>
/// 4-4: text drawn onto pages (stamp, watermark, page numbers), notes, document information and form values, with
/// PDFsharp. Japanese text uses a Windows font whose licence allows embedding (checked from the font's own fsType
/// field); only the characters used are embedded.
/// </summary>
public static class PdfMarkup
{
    public const int MaxTextLength = 100;
    public const string FontFamily = "LocalStudio Gothic";

    private static readonly object FontSync = new();
    private static bool _fontReady;

    private static readonly IReadOnlyDictionary<string, string> PositionNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["top-left"] = "左上", ["top"] = "上中央", ["top-right"] = "右上", ["center"] = "中央",
        ["bottom-left"] = "左下", ["bottom"] = "下中央", ["bottom-right"] = "右下"
    };

    /// <summary>English or Japanese position words → one of the seven positions; null when unknown.</summary>
    public static string? NormalizePosition(string? position, string fallback)
    {
        if (string.IsNullOrWhiteSpace(position)) return fallback;
        var text = position.Trim().ToLowerInvariant().Replace('_', '-').Replace(' ', '-');
        if (PositionNames.ContainsKey(text)) return text;
        if (text is "bottom-center" or "bottom-centre") return "bottom";
        if (text is "top-center" or "top-centre") return "top";
        if (text is "middle" or "centre") return "center";
        var top = text.Contains('上'); var bottom = text.Contains('下'); var left = text.Contains('左'); var right = text.Contains('右');
        if (text.Contains("中央") || text.Contains("真ん中") || text.Contains("まんなか"))
            return top ? "top" : bottom ? "bottom" : "center";
        return (top, bottom, left, right) switch
        {
            (true, false, true, false) => "top-left",
            (true, false, false, true) => "top-right",
            (true, false, false, false) => "top",
            (false, true, true, false) => "bottom-left",
            (false, true, false, true) => "bottom-right",
            (false, true, false, false) => "bottom",
            _ => null
        };
    }

    public static string PositionName(string position) => PositionNames.GetValueOrDefault(position, position);

    public static string KindName(string kind) => kind switch { "stamp" => "文字", "watermark" => "透かし", "note" => "注釈", _ => kind };

    public static string Number(string format, int number, int total) =>
        format.Replace("{n}", number.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
              .Replace("{total}", total.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

    /// <summary>Names of the text input fields of a PDF form (empty when it has none).</summary>
    public static IReadOnlySet<string> FormFields(string path)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        using var document = Pig.PdfDocument.Open(path);
        if (!document.TryGetForm(out var form)) return names;
        void Collect(IEnumerable<Pig.AcroForms.Fields.AcroFieldBase> fields)
        {
            foreach (var field in fields)
            {
                if (field is Pig.AcroForms.Fields.AcroTextField && field.Information.PartialName is { Length: > 0 } name) names.Add(name);
                if (field is Pig.AcroForms.Fields.AcroNonTerminalField parent) Collect(parent.Children);
            }
        }
        Collect(form.Fields);
        return names;
    }

    public static void Draw(Sharp.PdfDocument document, Sharp.PdfPage page, IReadOnlyList<PdfMark> marks)
    {
        EnsureFont();
        var width = page.Width.Point;
        var height = page.Height.Point;
        const double margin = 28;
        using (var graphics = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append))
        {
            foreach (var mark in marks.Where(mark => mark.Kind is not ("note" or "redact" or "crop")))
            {
                if (mark.Kind == "ocr")
                {
                    // Invisible text over the recognised words: the page looks the same, its text can be searched and copied.
                    var invisible = new XSolidBrush(XColor.FromArgb(0, 0, 0, 0));
                    foreach (var line in mark.Lines ?? [])
                    {
                        if (line.Words.Count == 0) continue;
                        var size = Math.Clamp(line.Words.Average(word => word.Height) * 0.9, 4, 72);
                        graphics.DrawString(OcrLineText(line), Font(size), invisible, new XPoint(line.Words.Min(word => word.X), line.Words.Min(word => word.Y)), XStringFormats.TopLeft);
                    }
                    continue;
                }
                var area = new XRect(margin, margin, width - 2 * margin, height - 2 * margin);
                var style = mark.Style ?? new PdfTextStyle();
                // dx moves right and dy moves up (PDFsharp's y grows downwards).
                area.Offset(style.Dx, -style.Dy);
                if (mark.Kind == "watermark")
                {
                    var size = style.Size ?? Math.Clamp(Math.Min(width, height) / Math.Max(2, mark.Text.Length) * 1.1, 24, 96);
                    var state = graphics.Save();
                    graphics.RotateAtTransform(-45, new XPoint(width / 2 + style.Dx, height / 2 - style.Dy));
                    graphics.DrawString(mark.Text, Font(size, style), new XSolidBrush(Colour(style.Color, 70, XColor.FromArgb(70, 120, 120, 120))), new XRect(style.Dx, -style.Dy, width, height), XStringFormats.Center);
                    graphics.Restore(state);
                    continue;
                }
                var brush = new XSolidBrush(Colour(style.Color, 255, mark.Kind == "stamp" ? XColor.FromArgb(255, 176, 32, 32) : XColors.Black));
                graphics.DrawString(mark.Text, Font(style.Size ?? (mark.Kind == "stamp" ? 14 : 10), style), brush, area, Format(mark.Position));
            }
        }
        foreach (var mark in marks.Where(mark => mark.Kind == "note"))
        {
            var annotation = new PdfTextAnnotation(document) { Title = "Local Studio", Contents = mark.Text, Icon = PdfTextAnnotationIcon.Note };
            var (x, y) = mark.Position switch
            {
                "top-right" => (width - margin - 20, height - margin - 20),
                "bottom-left" => (margin, margin),
                "bottom-right" => (width - margin - 20, margin),
                _ => (margin, height - margin - 20)
            };
            annotation.Rectangle = new Sharp.PdfRectangle(new XRect(x, y, 20, 20));
            page.Annotations.Add(annotation);
        }
    }

    public static void ApplyDocumentChanges(Sharp.PdfDocument document, IReadOnlyList<PdfOp> changes)
    {
        foreach (var change in changes)
        {
            switch (change.Op)
            {
                case "bookmark" when int.TryParse(change.Pages, out var page) && page >= 1 && page <= document.PageCount:
                    document.Outlines.Add(change.Text!, document.Pages[page - 1], true);
                    break;
                case "password":
                    document.SecuritySettings.UserPassword = change.Value!;
                    document.SecuritySettings.OwnerPassword = change.Value!;
                    break;
                case "info" when change.Field == "title": document.Info.Title = change.Value!; break;
                case "info" when change.Field == "author": document.Info.Author = change.Value!; break;
                case "info" when change.Field == "subject": document.Info.Subject = change.Value!; break;
                case "fill":
                    var form = document.AcroForm ?? throw new InvalidOperationException("このPDFには入力欄がありません。");
                    if (form.Fields[change.Field!] is not Sharp.AcroForms.PdfTextField field) throw new InvalidOperationException($"入力欄「{change.Field}」は文字の欄ではありません。");
                    field.Value = new Sharp.PdfString(change.Value ?? "", Sharp.PdfStringEncoding.Unicode);
                    // Viewers redraw the field from its value (the stored appearance may not have the new text).
                    form.Elements.SetBoolean("/NeedAppearances", true);
                    break;
            }
        }
    }

    /// <summary>Notes, document information and form values of a written file, compared with the plan.</summary>
    public static IEnumerable<string> VerifyExtras(string path, IReadOnlyList<PdfPageRef> pages, IReadOnlyList<PdfOp> changes)
    {
        var problems = new List<string>();
        using var document = Pig.PdfDocument.Open(path);
        for (var index = 0; index < pages.Count; index++)
        {
            var notes = (pages[index].Marks ?? []).Where(mark => mark.Kind == "note").ToArray();
            if (notes.Length == 0) continue;
            var contents = document.GetPage(index + 1).GetAnnotations().Select(annotation => annotation.Content).ToArray();
            foreach (var note in notes.Where(note => !contents.Contains(note.Text)))
                problems.Add($"{index + 1}ページ目に注釈「{note.Text}」が見つかりません");
        }
        // The look of drawn text is read back from the letters themselves: font family and size (hands-on 2026-10-13).
        for (var index = 0; index < pages.Count; index++)
        {
            var styled = (pages[index].Marks ?? []).Where(mark => mark.Kind is "stamp" or "number" or "watermark" && mark.Style is { } style && (style.Size is not null || style.Font != "gothic")).ToArray();
            if (styled.Length == 0) continue;
            var letters = document.GetPage(index + 1).Letters;
            foreach (var mark in styled)
            {
                var style = mark.Style!;
                var wanted = mark.Text.Where(character => !char.IsWhiteSpace(character)).ToArray();
                var family = style.Font switch { "mincho" => new[] { "Mincho" }, "ud" => new[] { "UDDigi", "UD Digi", "Kyokasho" }, _ => new[] { "Gothic", "Meiryo" } };
                var matching = letters.Count(letter => letter.Value.Length == 1 && wanted.Contains(letter.Value[0]) &&
                    family.Any(name => letter.FontName?.Contains(name, StringComparison.OrdinalIgnoreCase) == true) &&
                    (style.Size is not { } size || mark.Kind == "watermark" || Math.Abs(letter.PointSize - size) < 0.3));
                if (matching < wanted.Length)
                    problems.Add($"{index + 1}ページ目の「{mark.Text}」が指定の書式（{style.Describe()}）で見つかりません");
            }
        }
        // Phase C: crop boxes and bookmarks.
        for (var index = 0; index < pages.Count; index++)
            if ((pages[index].Marks ?? []).FirstOrDefault(mark => mark.Kind == "crop") is { } crop && index < document.NumberOfPages)
            {
                var margin = double.Parse(crop.Text, System.Globalization.CultureInfo.InvariantCulture);
                var page = document.GetPage(index + 1);
                if (Math.Abs(page.CropBox.Bounds.Width - (page.MediaBox.Bounds.Width - 2 * margin)) > 1 || Math.Abs(page.CropBox.Bounds.Height - (page.MediaBox.Bounds.Height - 2 * margin)) > 1)
                    problems.Add($"{index + 1}ページ目の余白の切り取りが予定どおりになっていません");
            }
        var bookmarks = changes.Where(change => change.Op == "bookmark").ToArray();
        if (bookmarks.Length > 0)
        {
            var found = document.TryGetBookmarks(out var tree) ? tree.GetNodes().OfType<Pig.Outline.DocumentBookmarkNode>().Select(node => (node.Title, node.PageNumber)).ToArray() : [];
            foreach (var bookmark in bookmarks.Where(bookmark => !found.Contains((bookmark.Text!, int.Parse(bookmark.Pages!, System.Globalization.CultureInfo.InvariantCulture)))))
                problems.Add($"しおり「{bookmark.Text}」（{bookmark.Pages}ページ目）が見つかりません");
        }
        foreach (var change in changes.Where(change => change.Op == "info"))
        {
            var actual = change.Field switch { "title" => document.Information.Title, "author" => document.Information.Author, _ => document.Information.Subject };
            if (actual != change.Value) problems.Add($"文書情報（{change.Field}）が「{actual}」（予定「{change.Value}」）");
        }
        var fills = changes.Where(change => change.Op == "fill").ToArray();
        if (fills.Length > 0)
        {
            var values = new Dictionary<string, string?>(StringComparer.Ordinal);
            if (document.TryGetForm(out var form))
            {
                void Collect(IEnumerable<Pig.AcroForms.Fields.AcroFieldBase> fields)
                {
                    foreach (var field in fields)
                    {
                        if (field is Pig.AcroForms.Fields.AcroTextField text && text.Information.PartialName is { } name) values[name] = text.Value;
                        if (field is Pig.AcroForms.Fields.AcroNonTerminalField parent) Collect(parent.Children);
                    }
                }
                Collect(form.Fields);
            }
            foreach (var fill in fills.Where(fill => values.GetValueOrDefault(fill.Field!) != fill.Value))
                problems.Add($"入力欄「{fill.Field}」の値が「{values.GetValueOrDefault(fill.Field!)}」（予定「{fill.Value}」）");
        }
        return problems;
    }

    private static string OcrLineText(OcrLine line) => new OcrPage(0, 0, [line]).Text;

    private static XStringFormat Format(string position) => position switch
    {
        "top-left" => XStringFormats.TopLeft,
        "top" => XStringFormats.TopCenter,
        "top-right" => XStringFormats.TopRight,
        "bottom-left" => XStringFormats.BottomLeft,
        "bottom" => XStringFormats.BottomCenter,
        "bottom-right" => XStringFormats.BottomRight,
        _ => XStringFormats.Center
    };

    private static XFont Font(double size) => new(FontFamily, size, XFontStyleEx.Regular, new XPdfFontOptions(Sharp.PdfFontEncoding.Unicode));

    private static XFont Font(double size, PdfTextStyle style) =>
        new(FamilyOf(style.Font), size, style.Bold ? XFontStyleEx.Bold : XFontStyleEx.Regular, new XPdfFontOptions(Sharp.PdfFontEncoding.Unicode));

    public static string FamilyOf(string font) => font switch { "mincho" => "LocalStudio Mincho", "ud" => "LocalStudio UD", _ => FontFamily };

    private static XColor Colour(string? name, int alpha, XColor fallback)
    {
        if (ExcelFormatting.Argb(name) is not { } argb) return fallback;
        var value = Convert.ToUInt32(argb, 16);
        return XColor.FromArgb(alpha, (int)((value >> 16) & 0xFF), (int)((value >> 8) & 0xFF), (int)(value & 0xFF));
    }

    /// <summary>
    /// Installs the font resolver once per process. Called before any PDF is written, not only before drawing: PDFsharp
    /// also needs a font for form fields (drill 2026-10-12: a fill-only run failed looking for "Courier New").
    /// </summary>
    public static void EnsureFont()
    {
        lock (FontSync)
        {
            if (_fontReady) return;
            GlobalFontSettings.FontResolver ??= new WindowsJapaneseFontResolver();
            _fontReady = true;
        }
    }

    /// <summary>
    /// Serves Japanese Windows fonts whose fsType allows embedding: gothic (Yu Gothic, Meiryo, MS Gothic), mincho (Yu
    /// Mincho, MS Mincho) and UD (UD Digi Kyokasho), each with its bold face where Windows has one (otherwise PDFsharp
    /// thickens the regular face). Every other family PDFsharp asks for (form fields) gets the gothic face. Font
    /// collections (.ttc) are cut down to their first face.
    /// </summary>
    public sealed class WindowsJapaneseFontResolver : IFontResolver
    {
        private static readonly Dictionary<string, string[]> Faces = new(StringComparer.Ordinal)
        {
            ["gothic"] = ["YuGothR.ttc", "meiryo.ttc", "msgothic.ttc"],
            ["gothic-bold"] = ["YuGothB.ttc", "meiryob.ttc"],
            ["mincho"] = ["yumin.ttf", "msmincho.ttc"],
            ["mincho-bold"] = ["yumindb.ttf"],
            ["ud"] = ["UDDigiKyokashoN-R.ttc"],
            ["ud-bold"] = ["UDDigiKyokashoN-B.ttc"]
        };
        private readonly Dictionary<string, byte[]?> _loaded = new(StringComparer.Ordinal);

        public FontResolverInfo ResolveTypeface(string familyName, bool bold, bool italic)
        {
            var key = familyName switch { "LocalStudio Mincho" => "mincho", "LocalStudio UD" => "ud", _ => "gothic" };
            if (bold && Load(key + "-bold") is not null) return new FontResolverInfo(key + "-bold");
            if (Load(key) is null) key = "gothic";
            return new FontResolverInfo(key, bold, false);
        }

        public byte[]? GetFont(string faceName) => Load(faceName) ?? Load("gothic") ??
            throw new InvalidOperationException("PDFに埋め込める日本語フォント（游ゴシック・メイリオ・MS ゴシック）が見つかりません。");

        private byte[]? Load(string face)
        {
            lock (_loaded)
            {
                if (_loaded.TryGetValue(face, out var cached)) return cached;
                byte[]? found = null;
                var folder = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
                foreach (var name in Faces.GetValueOrDefault(face, []))
                {
                    var path = Path.Combine(folder, name);
                    if (!File.Exists(path)) continue;
                    var bytes = FirstFace(File.ReadAllBytes(path));
                    if (EmbeddingAllowed(bytes)) { found = bytes; break; }
                }
                return _loaded[face] = found;
            }
        }

        /// <summary>fsType bit 1 (restricted licence) forbids embedding; 0, print/preview and editable allow it.</summary>
        public static bool EmbeddingAllowed(byte[] font)
        {
            var tables = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(4));
            for (var index = 0; index < tables; index++)
            {
                var record = 12 + 16 * index;
                if (System.Text.Encoding.ASCII.GetString(font, record, 4) != "OS/2") continue;
                var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(record + 8));
                var fsType = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(offset + 8));
                return (fsType & 0x000F) != 0x0002 && (fsType & 0x0200) == 0;
            }
            return false;
        }

        /// <summary>The first face of a TrueType collection as a stand-alone font file (a plain font is returned as is).</summary>
        public static byte[] FirstFace(byte[] collection)
        {
            if (System.Text.Encoding.ASCII.GetString(collection, 0, 4) != "ttcf") return collection;
            var start = (int)BinaryPrimitives.ReadUInt32BigEndian(collection.AsSpan(12));
            var tables = BinaryPrimitives.ReadUInt16BigEndian(collection.AsSpan(start + 4));
            var header = 12 + 16 * tables;
            var records = Enumerable.Range(0, tables).Select(index =>
            {
                var record = start + 12 + 16 * index;
                return (Record: record, Offset: (int)BinaryPrimitives.ReadUInt32BigEndian(collection.AsSpan(record + 8)), Length: (int)BinaryPrimitives.ReadUInt32BigEndian(collection.AsSpan(record + 12)));
            }).ToArray();
            var size = header + records.Sum(item => (item.Length + 3) & ~3);
            var font = new byte[size];
            collection.AsSpan(start, 12).CopyTo(font);
            var position = header;
            for (var index = 0; index < records.Length; index++)
            {
                var (record, offset, length) = records[index];
                collection.AsSpan(record, 16).CopyTo(font.AsSpan(12 + 16 * index));
                BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(12 + 16 * index + 8), (uint)position);
                collection.AsSpan(offset, length).CopyTo(font.AsSpan(position));
                position += (length + 3) & ~3;
            }
            return font;
        }
    }
}
