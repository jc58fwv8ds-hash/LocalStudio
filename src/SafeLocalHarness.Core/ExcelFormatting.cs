using System.Globalization;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace SafeLocalHarness.Core;

/// <summary>
/// Excel fill colour, bold and text colour, and new sheets (hands-on 2026-10-13, H5: "黄色で塗りつぶし、太字にして赤く"
/// and "シートを追加" could not be done at all). A cell keeps everything else of its own format — number format,
/// borders, alignment, font name and size — because its format is copied and only the asked parts are changed.
/// Each change is read back from the saved file.
/// </summary>
public static class ExcelFormatting
{
    public const int MaxCells = 5_000;

    private static readonly Dictionary<string, string> Colors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["黄"] = "FFFFFF00", ["黄色"] = "FFFFFF00", ["yellow"] = "FFFFFF00",
        ["赤"] = "FFFF0000", ["赤色"] = "FFFF0000", ["red"] = "FFFF0000",
        ["青"] = "FF0070C0", ["青色"] = "FF0070C0", ["blue"] = "FF0070C0",
        ["緑"] = "FF00B050", ["緑色"] = "FF00B050", ["green"] = "FF00B050",
        ["オレンジ"] = "FFFFC000", ["橙"] = "FFFFC000", ["orange"] = "FFFFC000",
        ["灰色"] = "FFBFBFBF", ["グレー"] = "FFBFBFBF", ["gray"] = "FFBFBFBF", ["grey"] = "FFBFBFBF",
        ["水色"] = "FF00B0F0", ["lightblue"] = "FF00B0F0",
        ["ピンク"] = "FFFF99CC", ["pink"] = "FFFF99CC",
        ["黒"] = "FF000000", ["黒色"] = "FF000000", ["black"] = "FF000000",
        ["白"] = "FFFFFFFF", ["白色"] = "FFFFFFFF", ["white"] = "FFFFFFFF"
    };

    /// <summary>The ARGB of a colour name (Japanese or English) or "#RRGGBB"; null when unknown.</summary>
    public static string? Argb(string? color)
    {
        if (string.IsNullOrWhiteSpace(color)) return null;
        var name = color.Trim();
        if (Colors.TryGetValue(name, out var argb)) return argb;
        var hex = Regex.Match(name, "^#?([0-9A-Fa-f]{6})$");
        return hex.Success ? "FF" + hex.Groups[1].Value.ToUpperInvariant() : null;
    }

    private static string ColorName(string argb) => Colors.Where(item => item.Value == argb && !Regex.IsMatch(item.Key, "^[a-z]")).OrderByDescending(item => item.Key.Length).Select(item => item.Key).FirstOrDefault() ?? "#" + argb[2..];

    /// <summary>The cells a format edit reaches, by sheet.</summary>
    public static IReadOnlyList<(WorksheetPart Part, Cell Cell)> Targets(WorkbookPart workbook, SharedStringTable? shared, OfficeEdit edit)
    {
        var parts = string.IsNullOrWhiteSpace(edit.Sheet) && edit.Op == "format-rows"
            ? workbook.WorksheetParts.ToArray()
            : OfficeEditor.FindSheet(workbook, edit.Sheet) is { } one ? [one] : [];
        var targets = new List<(WorksheetPart, Cell)>();
        foreach (var part in parts)
        {
            var rows = part.Worksheet!.Descendants<Row>();
            if (edit.Op == "format-rows")
            {
                if (string.IsNullOrWhiteSpace(edit.Contains)) return [];
                foreach (var row in rows.Where(row => row.Elements<Cell>().Any(cell => OfficeDocumentReader.CellText(cell, shared).Contains(edit.Contains, StringComparison.Ordinal))))
                    targets.AddRange(row.Elements<Cell>().Select(cell => (part, cell)));
            }
            else if (ParseRange(edit.Range) is var (left, top, right, bottom))
            {
                foreach (var cell in rows.SelectMany(row => row.Elements<Cell>()))
                {
                    var reference = cell.CellReference?.Value ?? "";
                    var column = OfficeDocumentReader.ColumnIndex(reference);
                    var rowNumber = int.TryParse(new string(reference.SkipWhile(char.IsLetter).ToArray()), out var number) ? number : 0;
                    if (column >= left && column <= right && rowNumber >= top && rowNumber <= bottom) targets.Add((part, cell));
                }
            }
        }
        return targets;
    }

    /// <summary>The preview line, with "invalid" when the format cannot be made exactly.</summary>
    public static OfficeChange? Describe(WorkbookPart workbook, SharedStringTable? shared, OfficeEdit edit)
    {
        var fill = Argb(edit.Fill);
        var color = Argb(edit.Color);
        var what = string.Join("・", new[]
        {
            fill is null ? null : $"塗りつぶし {ColorName(fill)}",
            edit.Bold == true ? "太字" : null,
            color is null ? null : $"文字色 {ColorName(color)}"
        }.OfType<string>());
        if (what.Length == 0 || (edit.Fill is { Length: > 0 } && fill is null) || (edit.Color is { Length: > 0 } && color is null))
            return new($"書式の指定を読み取れません（{edit.Fill} {edit.Color}）", "invalid");
        if (edit.Op == "format-cells" && (ParseRange(edit.Range) is null || OfficeEditor.FindSheet(workbook, edit.Sheet) is null))
            return new($"範囲を読み取れません（{edit.Sheet} {edit.Range}）", "invalid");
        var targets = Targets(workbook, shared, edit);
        var rows = targets.Select(item => (OfficeEditor.SheetName(workbook, item.Part), RowOf(item.Cell))).Distinct().ToArray();
        var place = edit.Op == "format-rows"
            ? $"「{edit.Contains}」を含む {rows.Length}行（" + string.Join("、", rows.Take(8).Select(item => $"{item.Item1}の行{item.Item2}")) + (rows.Length > 8 ? " ほか" : "") + "）"
            : $"シート「{OfficeEditor.SheetName(workbook, OfficeEditor.FindSheet(workbook, edit.Sheet)!)}」の {edit.Range!.ToUpperInvariant()}";
        if (targets.Count == 0) return new(place + ": " + what, "not-found");
        if (targets.Count > MaxCells) return new(place + $": {targets.Count}セルは多すぎます（{MaxCells}まで）", "invalid");
        return new($"{place}: {what}（{targets.Count}セル）", "ok");
    }

    public static void Apply(WorkbookPart workbook, OfficeEdit edit)
    {
        var shared = workbook.SharedStringTablePart?.SharedStringTable;
        var stylesheet = EnsureStylesheet(workbook);
        var cache = new Dictionary<uint, uint>();
        foreach (var (_, cell) in Targets(workbook, shared, edit))
        {
            var current = cell.StyleIndex?.Value ?? 0u;
            if (!cache.TryGetValue(current, out var next)) cache[current] = next = DerivedFormat(stylesheet, current, Argb(edit.Fill), edit.Bold == true, Argb(edit.Color));
            cell.StyleIndex = next;
        }
    }

    public static IEnumerable<string> Verify(WorkbookPart workbook, OfficeEdit edit)
    {
        var stylesheet = workbook.WorkbookStylesPart?.Stylesheet;
        var targets = Targets(workbook, workbook.SharedStringTablePart?.SharedStringTable, edit);
        if (targets.Count == 0) { yield return "書式を変えるセルが見つかりません"; yield break; }
        if (stylesheet?.CellFormats is null) { yield return "書式の情報がありません"; yield break; }
        var fill = Argb(edit.Fill);
        var color = Argb(edit.Color);
        var wrong = 0;
        foreach (var (_, cell) in targets)
        {
            var format = stylesheet.CellFormats.Elements<CellFormat>().ElementAtOrDefault((int)(cell.StyleIndex?.Value ?? 0u));
            var font = format is null ? null : stylesheet.Fonts?.Elements<Font>().ElementAtOrDefault((int)(format.FontId?.Value ?? 0u));
            var pattern = format is null ? null : stylesheet.Fills?.Elements<Fill>().ElementAtOrDefault((int)(format.FillId?.Value ?? 0u))?.PatternFill;
            var ok = format is not null &&
                     (fill is null || (pattern?.PatternType?.Value == PatternValues.Solid && pattern.ForegroundColor?.Rgb?.Value == fill)) &&
                     (edit.Bold != true || font?.Bold is not null) &&
                     (color is null || font?.Color?.Rgb?.Value == color);
            if (!ok) wrong++;
        }
        if (wrong > 0) yield return $"{wrong}セルの書式が予定どおりになっていません";
    }

    /// <summary>Every row of every sheet as text (sheet name, row number, cell texts), bounded for large workbooks.</summary>
    public static IReadOnlyList<(string Sheet, int Row, IReadOnlyList<string> Cells)> AllRows(string path, int maxRows = 20_000)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var document = SpreadsheetDocument.Open(stream, false);
        var workbook = document.WorkbookPart!;
        var shared = workbook.SharedStringTablePart?.SharedStringTable;
        var rows = new List<(string, int, IReadOnlyList<string>)>();
        foreach (var part in workbook.WorksheetParts)
            foreach (var row in part.Worksheet!.Descendants<Row>())
            {
                if (rows.Count >= maxRows) return rows;
                rows.Add((OfficeEditor.SheetName(workbook, part), (int)(row.RowIndex?.Value ?? 0u), row.Elements<Cell>().Select(cell => OfficeDocumentReader.CellText(cell, shared)).ToArray()));
            }
        return rows;
    }

    /// <summary>
    /// The longest part of the request (3 characters or more) found in a cell: "〇〇市△区に居住する者の…" → "〇〇市△区".
    /// A header alone is not a match (it is in every row's column, not a row's content).
    /// </summary>
    public static string? LongestCellMatch(string request, IReadOnlyList<(string Sheet, int Row, IReadOnlyList<string> Cells)> rows)
    {
        var text = Regex.Replace(request.Normalize(System.Text.NormalizationForm.FormKC), "「|」", "");
        var cells = rows.Skip(1).SelectMany(row => row.Cells).Where(cell => cell.Length > 0).Distinct().ToArray();
        for (var length = Math.Min(30, text.Length); length >= 3; length--)
            for (var start = 0; start + length <= text.Length; start++)
            {
                var part = text.Substring(start, length);
                if (Regex.IsMatch(part, @"[\s、。,]")) continue;
                if (cells.Any(cell => cell.Contains(part, StringComparison.Ordinal))) return part;
            }
        return null;
    }

    public static string? SheetNameProblem(WorkbookPart workbook, string? name)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length is 0 or > 31) return "名前は1～31文字にしてください";
        if (Regex.IsMatch(trimmed, @"[\[\]:*?/\\]")) return "[ ] : * ? / \\ は使えません";
        if (workbook.Workbook!.Sheets?.Elements<Sheet>().Any(sheet => string.Equals(sheet.Name?.Value, trimmed, StringComparison.OrdinalIgnoreCase)) ?? false) return "同じ名前のシートがあります";
        return null;
    }

    public static void AddSheet(WorkbookPart workbook, string name)
    {
        var part = workbook.AddNewPart<WorksheetPart>();
        part.Worksheet = new Worksheet(new SheetData());
        part.Worksheet.Save();
        var sheets = workbook.Workbook!.Sheets ??= workbook.Workbook.AppendChild(new Sheets());
        var id = sheets.Elements<Sheet>().Select(sheet => sheet.SheetId?.Value ?? 0u).DefaultIfEmpty(0u).Max() + 1;
        sheets.Append(new Sheet { Id = workbook.GetIdOfPart(part), SheetId = id, Name = name });
    }

    private static int RowOf(Cell cell) =>
        int.TryParse(new string((cell.CellReference?.Value ?? "").SkipWhile(char.IsLetter).ToArray()), out var number) ? number : 0;

    private static (int Left, int Top, int Right, int Bottom)? ParseRange(string? range)
    {
        var match = Regex.Match(range ?? "", @"^([A-Za-z]{1,3})([1-9][0-9]{0,6})(?::([A-Za-z]{1,3})([1-9][0-9]{0,6}))?$");
        if (!match.Success) return null;
        var left = OfficeDocumentReader.ColumnIndex(match.Groups[1].Value + "1");
        var top = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var right = match.Groups[3].Success ? OfficeDocumentReader.ColumnIndex(match.Groups[3].Value + "1") : left;
        var bottom = match.Groups[4].Success ? int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture) : top;
        return (Math.Min(left, right), Math.Min(top, bottom), Math.Max(left, right), Math.Max(top, bottom));
    }

    /// <summary>A workbook without styles gets the smallest valid set (one font, the two required fills, one border, one format).</summary>
    private static Stylesheet EnsureStylesheet(WorkbookPart workbook)
    {
        var part = workbook.WorkbookStylesPart ?? workbook.AddNewPart<WorkbookStylesPart>();
        if (part.Stylesheet is null)
            part.Stylesheet = new Stylesheet(
                new Fonts(new Font(new FontSize { Val = 11 }, new FontName { Val = "Yu Gothic" })) { Count = 1 },
                new Fills(new Fill(new PatternFill { PatternType = PatternValues.None }), new Fill(new PatternFill { PatternType = PatternValues.Gray125 })) { Count = 2 },
                new Borders(new Border(new LeftBorder(), new RightBorder(), new TopBorder(), new BottomBorder(), new DiagonalBorder())) { Count = 1 },
                new CellStyleFormats(new CellFormat { NumberFormatId = 0, FontId = 0, FillId = 0, BorderId = 0 }) { Count = 1 },
                new CellFormats(new CellFormat { NumberFormatId = 0, FontId = 0, FillId = 0, BorderId = 0, FormatId = 0 }) { Count = 1 });
        var stylesheet = part.Stylesheet;
        stylesheet.Fonts ??= new Fonts(new Font()) { Count = 1 };
        stylesheet.Fills ??= new Fills(new Fill(new PatternFill { PatternType = PatternValues.None }), new Fill(new PatternFill { PatternType = PatternValues.Gray125 })) { Count = 2 };
        stylesheet.CellFormats ??= new CellFormats(new CellFormat()) { Count = 1 };
        return stylesheet;
    }

    /// <summary>A copy of the cell's format with only the asked parts changed; returns its index.</summary>
    private static uint DerivedFormat(Stylesheet stylesheet, uint baseIndex, string? fill, bool bold, string? color)
    {
        var formats = stylesheet.CellFormats!;
        var source = formats.Elements<CellFormat>().ElementAtOrDefault((int)baseIndex) ?? formats.Elements<CellFormat>().First();
        var format = (CellFormat)source.CloneNode(true);
        if (fill is not null)
        {
            var fills = stylesheet.Fills!;
            fills.Append(new Fill(new PatternFill(new ForegroundColor { Rgb = fill }, new BackgroundColor { Indexed = 64 }) { PatternType = PatternValues.Solid }));
            fills.Count = (uint)fills.Elements<Fill>().Count();
            format.FillId = fills.Count - 1;
            format.ApplyFill = true;
        }
        if (bold || color is not null)
        {
            var fonts = stylesheet.Fonts!;
            var font = (Font)(fonts.Elements<Font>().ElementAtOrDefault((int)(format.FontId?.Value ?? 0u)) ?? fonts.Elements<Font>().First()).CloneNode(true);
            if (bold && font.Bold is null) font.PrependChild(new Bold());
            if (color is not null)
            {
                font.RemoveAllChildren<Color>();
                // The schema order of a font: … sz, color, name, family, charset, scheme.
                var after = (OpenXmlElement?)font.GetFirstChild<FontName>() ?? (OpenXmlElement?)font.GetFirstChild<FontFamilyNumbering>() ?? (OpenXmlElement?)font.GetFirstChild<FontCharSet>() ?? font.GetFirstChild<FontScheme>();
                if (after is null) font.Append(new Color { Rgb = color }); else font.InsertBefore(new Color { Rgb = color }, after);
            }
            fonts.Append(font);
            fonts.Count = (uint)fonts.Elements<Font>().Count();
            format.FontId = fonts.Count - 1;
            format.ApplyFont = true;
        }
        formats.Append(format);
        formats.Count = (uint)formats.Elements<CellFormat>().Count();
        return formats.Count - 1;
    }
}
