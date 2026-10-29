using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;
using Pig = UglyToad.PdfPig;

namespace SafeLocalHarness.Core;

/// <summary>A font a page uses, and whether its own letters can be reused when its text is changed.</summary>
public sealed record PdfFontFacts(string Resource, string BaseFont, string Subtype, string Encoding, bool Embedded, bool Subset, int MappedCharacters);

/// <summary>Stroke widths and how often each is set on a page (罫線の太さ).</summary>
public sealed record PdfWidthCount(double Width, int Count);

/// <summary>
/// What a page is made of, counted from its content stream (never its text): the fonts, text-showing commands, the
/// path commands that draw rules and boxes, stroke widths, images and forms. Query words are only counted.
/// </summary>
public sealed record PdfPageStructure(
    int Number, double Width, double Height, int Rotation,
    IReadOnlyList<PdfFontFacts> Fonts, int TextShows, int Characters, int Rectangles, int LineSegments, int Strokes, int Fills,
    IReadOnlyList<PdfWidthCount> StrokeWidths, int Images, int FormObjects, int Annotations, bool ScannedLooking,
    IReadOnlyDictionary<string, int> QueryHitsInShows, IReadOnlyDictionary<string, int> QueryHitsInPageText);

/// <summary>Whether a replacement text can be written with letters the PDF already carries (subset fonts carry only the letters used).</summary>
public sealed record PdfReplacementFacts(string Text, IReadOnlyList<string> FontsWithAllLetters, IReadOnlyList<string> MissingEverywhere);

/// <summary>
/// Phase 0 of PDF editing (指示書 2026-10-20): reads a PDF's structure so the harness knows, before anything is built,
/// whether text and rules can be changed in place (direct object edit) or only by a fallback, and can check a saved
/// result against the original. Document text is never returned, only counts and the caller's own query words.
/// </summary>
public static class PdfStructureSurvey
{
    public static IReadOnlyList<PdfPageStructure> Survey(string path, IReadOnlyList<string>? queries = null)
    {
        queries ??= [];
        var pages = new List<PdfPageStructure>();
        using var pig = Pig.PdfDocument.Open(path);
        using var document = PdfSharp.Pdf.IO.PdfReader.Open(path, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        for (var index = 0; index < document.PageCount; index++)
        {
            var page = document.Pages[index];
            var fonts = FontsOf(page);
            var xobjects = page.Resources?.Elements.GetDictionary("/XObject");
            var forms = new HashSet<string>(StringComparer.Ordinal);
            if (xobjects is not null)
                foreach (var key in xobjects.Elements.Keys)
                    if (xobjects.Elements.GetDictionary(key)?.Elements.GetName("/Subtype") == "/Form") forms.Add(key);
            var counter = new Counter(fonts, forms);
            counter.Walk(ContentReader.ReadContent(page));
            var pageText = pig.GetPage(index + 1).Text;
            var annotations = page.Elements.GetArray("/Annots")?.Elements.Count ?? 0;
            pages.Add(new PdfPageStructure(index + 1, page.Width.Point, page.Height.Point, page.Rotate,
                fonts.Values.Select(font => font.Facts).ToArray(), counter.TextShows, counter.Characters, counter.Rectangles, counter.LineSegments, counter.Strokes, counter.Fills,
                counter.Widths.OrderByDescending(pair => pair.Value).Select(pair => new PdfWidthCount(pair.Key, pair.Value)).ToArray(),
                counter.Images, counter.Forms, annotations,
                ScannedLooking: counter.Images > 0 && counter.Characters == 0,
                queries.ToDictionary(query => query, query => counter.Shown.Sum(shown => Occurrences(shown, query))),
                queries.ToDictionary(query => query, query => Occurrences(Regex.Replace(pageText, @"\s+", ""), query))));
        }
        return pages;
    }

    /// <summary>For each wanted text: the fonts whose embedded letters already cover it, and letters no font in the file has.</summary>
    public static IReadOnlyList<PdfReplacementFacts> Replacements(string path, IReadOnlyList<string> texts)
    {
        using var document = PdfSharp.Pdf.IO.PdfReader.Open(path, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        var fonts = new Dictionary<string, HashSet<char>>(StringComparer.Ordinal);
        foreach (var page in document.Pages)
            foreach (var font in FontsOf(page).Values)
            {
                if (!fonts.TryGetValue(font.Facts.BaseFont, out var letters)) fonts[font.Facts.BaseFont] = letters = [];
                foreach (var text in font.ToUnicode.Values) foreach (var ch in text) letters.Add(ch);
            }
        return texts.Select(text => new PdfReplacementFacts(text,
                fonts.Where(font => text.Where(ch => !char.IsWhiteSpace(ch)).All(font.Value.Contains)).Select(font => font.Key).ToArray(),
                text.Where(ch => !char.IsWhiteSpace(ch) && !fonts.Values.Any(letters => letters.Contains(ch))).Distinct().Select(ch => ch.ToString()).ToArray()))
            .ToArray();
    }

    private static int Occurrences(string text, string query)
    {
        if (query.Length == 0) return 0;
        var count = 0;
        for (var at = text.IndexOf(query, StringComparison.Ordinal); at >= 0; at = text.IndexOf(query, at + query.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    internal sealed record FontInfo(PdfFontFacts Facts, bool TwoByte, IReadOnlyDictionary<int, string> ToUnicode);

    internal static Dictionary<string, FontInfo> FontsOf(PdfPage page)
    {
        var result = new Dictionary<string, FontInfo>(StringComparer.Ordinal);
        var fontResources = page.Resources?.Elements.GetDictionary("/Font");
        if (fontResources is null) return result;
        foreach (var key in fontResources.Elements.Keys)
        {
            var font = fontResources.Elements.GetDictionary(key);
            if (font is null) continue;
            var subtype = font.Elements.GetName("/Subtype");
            var baseFont = font.Elements.GetName("/BaseFont").TrimStart('/');
            var encoding = font.Elements["/Encoding"] is PdfName name ? name.Value : font.Elements.ContainsKey("/Encoding") ? "(dictionary)" : "";
            var descriptor = font.Elements.GetDictionary("/FontDescriptor");
            if (subtype == "/Type0" && font.Elements.GetArray("/DescendantFonts") is { } descendants && descendants.Elements.Count > 0)
                descriptor = (descendants.Elements.GetDictionary(0))?.Elements.GetDictionary("/FontDescriptor");
            var embedded = descriptor is not null && (descriptor.Elements.ContainsKey("/FontFile") || descriptor.Elements.ContainsKey("/FontFile2") || descriptor.Elements.ContainsKey("/FontFile3"));
            var toUnicode = ReadToUnicode(font.Elements.GetDictionary("/ToUnicode"));
            result[key] = new FontInfo(new PdfFontFacts(key, baseFont, subtype.TrimStart('/'), encoding.TrimStart('/'), embedded,
                Regex.IsMatch(baseFont, "^[A-Z]{6}\\+"), toUnicode.Count), subtype == "/Type0", toUnicode);
        }
        return result;
    }

    /// <summary>A ToUnicode CMap's bfchar and bfrange entries (code → text).</summary>
    internal static IReadOnlyDictionary<int, string> ReadToUnicode(PdfDictionary? cmap)
    {
        var map = new Dictionary<int, string>();
        if (cmap?.Stream is null) return map;
        string text;
        try { text = Encoding.Latin1.GetString(cmap.Stream.UnfilteredValue); }
        catch (Exception) { return map; }
        static string Hex(string hex)
        {
            var builder = new StringBuilder();
            for (var i = 0; i + 4 <= hex.Length; i += 4) builder.Append((char)int.Parse(hex.AsSpan(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            return builder.ToString();
        }
        foreach (Match block in Regex.Matches(text, @"beginbfchar(?<body>.*?)endbfchar", RegexOptions.Singleline))
            foreach (Match pair in Regex.Matches(block.Groups["body"].Value, @"<(?<code>[0-9A-Fa-f]+)>\s*<(?<text>[0-9A-Fa-f]*)>"))
                map[int.Parse(pair.Groups["code"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)] = Hex(pair.Groups["text"].Value);
        foreach (Match block in Regex.Matches(text, @"beginbfrange(?<body>.*?)endbfrange", RegexOptions.Singleline))
            foreach (Match range in Regex.Matches(block.Groups["body"].Value, @"<(?<lo>[0-9A-Fa-f]+)>\s*<(?<hi>[0-9A-Fa-f]+)>\s*(?:<(?<start>[0-9A-Fa-f]+)>|\[(?<list>[^\]]*)\])"))
            {
                var lo = int.Parse(range.Groups["lo"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var hi = Math.Min(int.Parse(range.Groups["hi"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture), lo + 65535);
                if (range.Groups["start"].Success)
                {
                    var start = Hex(range.Groups["start"].Value);
                    for (var code = lo; code <= hi && start.Length > 0; code++)
                        map[code] = start[..^1] + (char)(start[^1] + (code - lo));
                }
                else
                {
                    var items = Regex.Matches(range.Groups["list"].Value, @"<([0-9A-Fa-f]*)>").Select(item => Hex(item.Groups[1].Value)).ToArray();
                    for (var i = 0; i < items.Length && lo + i <= hi; i++) map[lo + i] = items[i];
                }
            }
        return map;
    }

    /// <summary>The text a show command draws, through the font's ToUnicode map (two-byte codes for Type0 fonts).</summary>
    internal static string Decode(string raw, FontInfo? font)
    {
        if (font is null) return raw;
        var builder = new StringBuilder();
        if (font.TwoByte)
            for (var i = 0; i + 1 < raw.Length; i += 2) { var code = (raw[i] << 8) | raw[i + 1]; builder.Append(font.ToUnicode.TryGetValue(code, out var text) ? text : "\uFFFD"); }
        else
            foreach (var ch in raw) builder.Append(font.ToUnicode.TryGetValue(ch, out var text) ? text : ch.ToString());
        return builder.ToString();
    }

    // Form objects carry their own commands (text and rules inside them are not counted here, only the form itself).
    private sealed class Counter(Dictionary<string, FontInfo> fonts, HashSet<string> forms)
    {
        public int TextShows, Characters, Rectangles, LineSegments, Strokes, Fills, Images, Forms;
        public readonly Dictionary<double, int> Widths = [];
        // Text drawn between BT and ET, joined, so a word split over several show commands is still found.
        public readonly List<string> Shown = [];
        private readonly StringBuilder _block = new();
        private FontInfo? _font;

        public void Walk(CSequence sequence)
        {
            foreach (var item in sequence)
            {
                if (item is CSequence inner) { Walk(inner); continue; }
                if (item is not COperator op) continue;
                switch (op.OpCode.OpCodeName)
                {
                    case OpCodeName.BT: _block.Clear(); break;
                    case OpCodeName.ET: if (_block.Length > 0) Shown.Add(_block.ToString()); _block.Clear(); break;
                    case OpCodeName.Tf when op.Operands.Count > 0 && op.Operands[0] is CName name: fonts.TryGetValue(name.Name, out _font); break;
                    case OpCodeName.Tj or OpCodeName.QuoteSingle or OpCodeName.QuoteDouble:
                        TextShows++;
                        if (op.Operands.Count > 0 && op.Operands[^1] is CString single) Show(single.Value);
                        break;
                    case OpCodeName.TJ:
                        TextShows++;
                        if (op.Operands.Count > 0 && op.Operands[0] is CArray array)
                            foreach (var part in array) if (part is CString piece) Show(piece.Value);
                        break;
                    case OpCodeName.re: Rectangles++; break;
                    case OpCodeName.l: LineSegments++; break;
                    case OpCodeName.S or OpCodeName.s: Strokes++; break;
                    case OpCodeName.f or OpCodeName.F or OpCodeName.fx: Fills++; break;
                    case OpCodeName.B or OpCodeName.Bx or OpCodeName.b or OpCodeName.bx: Strokes++; Fills++; break;
                    case OpCodeName.w when op.Operands.Count > 0 && Number(op.Operands[0]) is { } width:
                        Widths[Math.Round(width, 3)] = Widths.GetValueOrDefault(Math.Round(width, 3)) + 1; break;
                    case OpCodeName.Do when op.Operands.Count > 0 && op.Operands[0] is CName xobject && forms.Contains(xobject.Name): Forms++; break;
                    case OpCodeName.Do: Images++; break;
                }
            }
        }

        private void Show(string raw)
        {
            var text = Decode(raw, _font);
            Characters += text.Length;
            _block.Append(text);
        }
    }

    internal static double? Number(CObject value) => value switch
    {
        CInteger integer => integer.Value,
        CReal real => real.Value,
        _ => null
    };
}
