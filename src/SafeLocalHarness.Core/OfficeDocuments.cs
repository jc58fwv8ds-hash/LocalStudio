using System.Globalization;
using System.IO.Compression;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace SafeLocalHarness.Core;

/// <summary>Computed by the tool for one numeric column; MinLabel / MaxLabel name the row (e.g. 品名) holding the value.</summary>
public sealed record ColumnStats(string Header, string Letter, int Numbers, double Sum, double Average, double Min, double Max, string? MinLabel, string? MaxLabel);

public sealed record SheetRow(int Number, IReadOnlyList<string> Values);

/// <summary>LabelColumn holds the row names; RowsByLabel maps each unique row name to its row number.</summary>
public sealed record SheetSummary(string Name, int Rows, int Columns, IReadOnlyList<SheetRow> Sample, IReadOnlyList<ColumnStats> Numeric,
    IReadOnlyList<string> Headers, string? LabelColumn, IReadOnlyDictionary<string, int> RowsByLabel);

public sealed record OfficeSummary(string Kind, string FileName, IReadOnlyList<string> Headings, IReadOnlyList<string> Paragraphs, int Characters, int Tables, IReadOnlyList<SheetSummary> Sheets, IReadOnlyList<SlideSummary>? Slides = null)
{
    /// <summary>The facts the AI may use, in plain text (bounded so it fits a small model's context).</summary>
    public string ToFacts(int maxCharacters = 6_000)
    {
        var text = new StringBuilder($"ファイル: {FileName}（{(Kind switch { "word" => "Word", "powerpoint" => "PowerPoint", _ => "Excel" })}）\n");
        if (Kind == "powerpoint")
        {
            text.Append($"スライド: {Slides?.Count ?? 0}枚\n");
            foreach (var slide in Slides ?? [])
            {
                if (text.Length > maxCharacters) { text.Append("（以下省略）\n"); break; }
                text.Append($"{slide.Number}枚目「{slide.Title}」: ").Append(string.Join(" / ", slide.Texts.Where(item => item != slide.Title))).Append('\n');
                if (slide.Notes.Length > 0) text.Append("  ノート: ").Append(slide.Notes.Replace("\n", " / ")).Append('\n');
            }
        }
        else if (Kind == "word")
        {
            text.Append($"文字数: {Characters}、段落: {Paragraphs.Count}、表: {Tables}\n");
            if (Headings.Count > 0) text.Append("見出し: ").Append(string.Join(" / ", Headings.Take(30))).Append('\n');
            text.Append("本文:\n");
            foreach (var paragraph in Paragraphs)
            {
                if (text.Length + paragraph.Length > maxCharacters) { text.Append("（以下省略）\n"); break; }
                text.Append(paragraph).Append('\n');
            }
        }
        else
        {
            foreach (var sheet in Sheets)
            {
                text.Append($"シート「{sheet.Name}」: {sheet.Rows}行 × {sheet.Columns}列（1行目は見出し")
                    .Append(sheet.LabelColumn is null ? "）\n" : $"、行の名前は列{sheet.LabelColumn}）\n");
                text.Append("  列: ").Append(string.Join(", ", sheet.Headers.Select((header, column) => $"{OfficeDocumentReader.ColumnName(column)}={header}"))).Append('\n');
                foreach (var stats in sheet.Numeric)
                    text.Append($"  列「{stats.Header}」({stats.Letter}): 数値{stats.Numbers}件、合計 {Format(stats.Sum)}、平均 {Format(stats.Average)}、" +
                                $"最小 {Format(stats.Min)}{(stats.MinLabel is null ? "" : $"（{stats.MinLabel}）")}、最大 {Format(stats.Max)}{(stats.MaxLabel is null ? "" : $"（{stats.MaxLabel}）")}\n");
                foreach (var row in sheet.Sample.Skip(1))
                {
                    if (text.Length > maxCharacters) { text.Append("（以下省略）\n"); break; }
                    text.Append($"  行{row.Number} | ").Append(string.Join(" | ", row.Values.Select((value, column) => $"{OfficeDocumentReader.ColumnName(column)}:{value}"))).Append('\n');
                }
            }
        }
        return text.Length <= maxCharacters ? text.ToString() : text.ToString()[..maxCharacters];
    }

    public static string Format(double value) =>
        Math.Abs(value - Math.Round(value)) < 1e-9 ? Math.Round(value).ToString("#,0", CultureInfo.InvariantCulture) : value.ToString("#,0.##", CultureInfo.InvariantCulture);
}

/// <summary>
/// Word (.docx), Excel (.xlsx) and PowerPoint (.pptx) reading for 4-2, with the Open XML SDK (Microsoft, MIT). Files are opened read-only,
/// macro-enabled files are refused, and the archive is checked before opening (size, entry count, expansion ratio)
/// with the same caution as cartridges. Formulas are never evaluated: Excel's stored results are read.
/// </summary>
public static class OfficeDocumentReader
{
    public const long MaxFileBytes = 20L * 1024 * 1024;
    public const long MaxExpandedBytes = 200L * 1024 * 1024;
    public const int MaxEntries = 2_000;
    public const int MaxRowsRead = 50_000;

    /// <summary>Null when the file can be opened; otherwise the reason in plain Japanese.</summary>
    public static string? Check(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".docm" or ".xlsm" or ".dotm" or ".xltm" or ".pptm" or ".potm" or ".ppsm") return "マクロ付きのファイルは開きません。";
        if (extension is not (".docx" or ".xlsx" or ".pptx")) return "Word（.docx）・Excel（.xlsx）・PowerPoint（.pptx）のファイルだけを扱えます。";
        var info = new FileInfo(path);
        if (!info.Exists) return "ファイルが見つかりません。";
        if (info.Length is 0 or > MaxFileBytes) return $"ファイルは{MaxFileBytes / 1024 / 1024} MBまでです。";
        try
        {
            using var archive = ZipFile.OpenRead(path);
            if (archive.Entries.Count > MaxEntries) return "中身の数が多すぎるため開きません。";
            var expanded = archive.Entries.Sum(entry => entry.Length);
            if (expanded > MaxExpandedBytes || expanded > info.Length * 200) return "展開すると大きくなりすぎるため開きません。";
            if (archive.Entries.Any(entry => entry.FullName.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase))) return "マクロが含まれているため開きません。";
        }
        catch (InvalidDataException)
        {
            return "Office のファイルとして読み取れません（壊れているか、パスワード付きの可能性があります）。";
        }
        return null;
    }

    public static OfficeSummary Read(string path)
    {
        if (Check(path) is { } reason) throw new InvalidOperationException(reason);
        return Path.GetExtension(path).ToLowerInvariant() switch { ".docx" => ReadWord(path), ".pptx" => ReadPowerPoint(path), _ => ReadExcel(path) };
    }

    private static OfficeSummary ReadPowerPoint(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var document = PresentationDocument.Open(stream, false);
        var slides = PowerPointOperations.ReadSlides(document);
        var paragraphs = slides.SelectMany(slide => slide.Texts).ToArray();
        return new OfficeSummary("powerpoint", Path.GetFileName(path), slides.Select(slide => slide.Title).Where(title => title.Length > 0).ToArray(), paragraphs,
            paragraphs.Sum(text => text.Length), 0, [], slides);
    }

    private static OfficeSummary ReadWord(string path)
    {
        using var document = WordprocessingDocument.Open(path, false);
        var body = document.MainDocumentPart?.Document?.Body ?? throw new InvalidDataException("本文が見つかりません。");
        var paragraphs = new List<string>();
        var headings = new List<string>();
        foreach (var paragraph in body.Descendants<W.Paragraph>())
        {
            var text = paragraph.InnerText.Trim();
            if (text.Length == 0) continue;
            paragraphs.Add(text);
            var style = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? "";
            if (style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) || style.StartsWith("見出し", StringComparison.Ordinal) || style is "Title" or "1" or "2" or "3")
                headings.Add(text);
        }
        return new OfficeSummary("word", Path.GetFileName(path), headings, paragraphs, paragraphs.Sum(text => text.Length), body.Descendants<W.Table>().Count(), []);
    }

    private static OfficeSummary ReadExcel(string path)
    {
        using var document = SpreadsheetDocument.Open(path, false);
        var workbook = document.WorkbookPart ?? throw new InvalidDataException("ブックが見つかりません。");
        var shared = workbook.SharedStringTablePart?.SharedStringTable;
        var sheets = new List<SheetSummary>();
        foreach (var sheet in workbook.Workbook!.Sheets?.Elements<Sheet>() ?? [])
        {
            if (sheet.Id?.Value is not { } id || workbook.GetPartById(id) is not WorksheetPart part) continue;
            var rows = new List<SheetRow>();
            foreach (var row in part.Worksheet!.Descendants<Row>().Take(MaxRowsRead))
            {
                var values = new List<string>();
                foreach (var cell in row.Elements<Cell>())
                {
                    var column = ColumnIndex(cell.CellReference?.Value);
                    while (values.Count < column) values.Add("");
                    values.Add(CellText(cell, shared));
                }
                rows.Add(new SheetRow((int)(row.RowIndex?.Value ?? (uint)(rows.Count + 1)), values));
            }
            var width = rows.Count == 0 ? 0 : rows.Max(row => row.Values.Count);
            var header = rows.FirstOrDefault()?.Values ?? [];
            var data = rows.Skip(1).ToArray();
            static bool IsNumber(string value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
            string ValueAt(SheetRow row, int column) => column < row.Values.Count ? row.Values[column] : "";
            // The row names: the first column whose filled cells are mostly text (e.g. 品名).
            var labelColumn = Enumerable.Range(0, width).FirstOrDefault(column =>
                data.Count(row => ValueAt(row, column).Length > 0 && !IsNumber(ValueAt(row, column))) > data.Length / 2, -1);
            string? LabelOf(SheetRow row) => labelColumn >= 0 && ValueAt(row, labelColumn).Length > 0 ? ValueAt(row, labelColumn) : null;
            var numeric = new List<ColumnStats>();
            for (var column = 0; column < width; column++)
            {
                var numbers = data.Select(row => (Row: row, Text: ValueAt(row, column)))
                    .Select(item => double.TryParse(item.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? (item.Row, Value: (double?)number) : (item.Row, Value: null))
                    .Where(item => item.Value is not null).Select(item => (item.Row, Value: item.Value!.Value)).ToArray();
                if (numbers.Length == 0) continue;
                var name = column < header.Count && header[column].Length > 0 ? header[column] : ColumnName(column);
                var max = numbers.MaxBy(item => item.Value);
                var min = numbers.MinBy(item => item.Value);
                numeric.Add(new ColumnStats(name, ColumnName(column), numbers.Length, numbers.Sum(item => item.Value), numbers.Average(item => item.Value),
                    min.Value, max.Value, LabelOf(min.Row), LabelOf(max.Row)));
            }
            var rowsByLabel = data.Select(row => (Label: LabelOf(row), row.Number)).Where(item => item.Label is not null)
                .GroupBy(item => item.Label!, StringComparer.Ordinal).Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.Single().Number, StringComparer.Ordinal);
            var headers = Enumerable.Range(0, width).Select(column => column < header.Count && header[column].Length > 0 ? header[column] : ColumnName(column)).ToArray();
            sheets.Add(new SheetSummary(sheet.Name?.Value ?? "Sheet", rows.Count, width, rows.Take(16).ToArray(), numeric, headers,
                labelColumn >= 0 ? ColumnName(labelColumn) : null, rowsByLabel));
        }
        return new OfficeSummary("excel", Path.GetFileName(path), [], [], 0, 0, sheets);
    }

    internal static string CellText(Cell cell, SharedStringTable? shared)
    {
        var raw = cell.CellValue?.Text ?? cell.InnerText ?? "";
        if (cell.DataType?.Value == CellValues.SharedString && int.TryParse(raw, out var index) && shared is not null)
            return shared.ElementAt(index).InnerText;
        if (cell.DataType?.Value == CellValues.InlineString) return cell.InlineString?.InnerText ?? "";
        if (cell.DataType?.Value == CellValues.Boolean) return raw == "1" ? "TRUE" : "FALSE";
        return raw;
    }

    internal static int ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return 0;
        var index = 0;
        foreach (var character in reference.TakeWhile(char.IsLetter)) index = index * 26 + (char.ToUpperInvariant(character) - 'A' + 1);
        return Math.Max(0, index - 1);
    }

    internal static string ColumnName(int index)
    {
        var name = "";
        for (index++; index > 0; index = (index - 1) / 26) name = (char)('A' + (index - 1) % 26) + name;
        return name;
    }
}
