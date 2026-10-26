using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace SafeLocalHarness.Core;

/// <summary>
/// One edit. op: replace {find, with} (Word paragraphs, Excel text cells) | set-cell {sheet, cell, value} (Excel)
/// | append-row {sheet, values} (Excel) | format-rows {sheet or empty for all, contains, fill, bold, color} (Excel: every row
/// with a cell containing the text) | format-cells {sheet, range like A1:C3, fill, bold, color} | add-sheet {name}.
/// </summary>
public sealed record OfficeEdit(
    [property: JsonPropertyName("op")] string Op,
    [property: JsonPropertyName("find")] string? Find = null,
    [property: JsonPropertyName("with")] string? With = null,
    [property: JsonPropertyName("sheet")] string? Sheet = null,
    [property: JsonPropertyName("cell")] string? Cell = null,
    [property: JsonPropertyName("value")] string? Value = null,
    [property: JsonPropertyName("values")] IReadOnlyList<string>? Values = null,
    [property: JsonPropertyName("contains")] string? Contains = null,
    [property: JsonPropertyName("range")] string? Range = null,
    [property: JsonPropertyName("fill")] string? Fill = null,
    [property: JsonPropertyName("bold")] bool? Bold = null,
    [property: JsonPropertyName("color")] string? Color = null,
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("column")] string? Column = null,
    [property: JsonPropertyName("order")] string? Order = null,
    [property: JsonPropertyName("formula")] string? Formula = null,
    [property: JsonPropertyName("format")] string? Format = null,
    [property: JsonPropertyName("width")] double? Width = null,
    [property: JsonPropertyName("operator")] string? Operator = null,
    [property: JsonPropertyName("rows")] string? Rows = null,
    [property: JsonPropertyName("count")] int? Count = null,
    [property: JsonPropertyName("italic")] bool? Italic = null,
    [property: JsonPropertyName("underline")] bool? Underline = null,
    [property: JsonPropertyName("highlight")] string? Highlight = null,
    [property: JsonPropertyName("size")] double? Size = null,
    [property: JsonPropertyName("font")] string? Font = null,
    [property: JsonPropertyName("level")] int? Level = null,
    [property: JsonPropertyName("table")] int? Table = null,
    [property: JsonPropertyName("text")] string? Text = null,
    [property: JsonPropertyName("position")] string? Position = null);

public sealed record OfficeChange(string Description, string Status);

public sealed record OfficeEditPreview(string Id, string SourcePath, IReadOnlyList<OfficeEdit> Edits, IReadOnlyList<OfficeChange> Changes, string SourceSha256)
{
    public bool CanApply => Changes.Count > 0 && Changes.All(change => change.Status == "ok");
}

public sealed record OfficeEditResult(string OutputPath, IReadOnlyList<string> Mismatches)
{
    public bool Succeeded => Mismatches.Count == 0;
}

/// <summary>
/// Edits for 4-2. The original file is never written: the result is a new file next to it ("…_LS編集.docx"), checked
/// by reading it back (every replaced text gone and the new text present, every cell holding its new value), and the
/// original's hash is compared before and after. Cells with formulas are not overwritten, and Excel recalculates
/// formulas when the edited file is opened.
/// </summary>
public static class OfficeEditor
{
    public const int MaxEdits = 20;

    public static OfficeEditPreview Preview(string path, IReadOnlyList<OfficeEdit> edits)
    {
        if (OfficeDocumentReader.Check(path) is { } reason) throw new InvalidOperationException(reason);
        if (edits.Count is 0 or > MaxEdits) throw new InvalidDataException($"変更は1～{MaxEdits}件にしてください。");
        var word = Path.GetExtension(path).Equals(".docx", StringComparison.OrdinalIgnoreCase);
        var changes = new List<OfficeChange>();
        if (IsPowerPoint(path))
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var presentation = PresentationDocument.Open(stream, false);
            foreach (var edit in edits) changes.Add(PowerPointOperations.Describe(presentation, edit, edits.Count));
        }
        else if (word)
        {
            using var document = WordprocessingDocument.Open(path, false);
            var text = string.Join("\n", document.MainDocumentPart!.Document!.Body!.Descendants<W.Paragraph>().Select(paragraph => paragraph.InnerText));
            foreach (var edit in edits)
            {
                if (edit.Op != "replace")
                {
                    // Phase B: one Word operation per request, so its read-back is exact.
                    if (edits.Count > 1) { changes.Add(new($"「{edit.Op}」は、ほかの変更と一緒にはできません（1つずつ依頼してください）", "invalid")); continue; }
                    changes.Add(WordOperations.Describe(document, edit) ?? new($"Word では使えない変更です（{edit.Op}）", "invalid"));
                    continue;
                }
                if (string.IsNullOrEmpty(edit.Find) || edit.With is null) { changes.Add(new("置き換える文字を指定してください", "invalid")); continue; }
                var count = Count(text, edit.Find);
                changes.Add(new($"「{edit.Find}」→「{edit.With}」（{count}か所）", count > 0 ? "ok" : "not-found"));
            }
        }
        else
        {
            var sheetsRead = OfficeDocumentReader.Read(path).Sheets;
            using var document = SpreadsheetDocument.Open(path, false);
            var workbook = document.WorkbookPart!;
            var shared = workbook.SharedStringTablePart?.SharedStringTable;
            // "C3（みかん の 単価）": the row and column names, so a change aimed at the wrong place is visible.
            string Where(string sheetName, string cell)
            {
                var sheet = sheetsRead.FirstOrDefault(item => item.Name == sheetName);
                if (sheet is null) return cell;
                var row = int.Parse(new string(cell.SkipWhile(char.IsLetter).ToArray()), CultureInfo.InvariantCulture);
                var column = OfficeDocumentReader.ColumnIndex(cell);
                var label = sheet.RowsByLabel.FirstOrDefault(item => item.Value == row).Key;
                var header = column < sheet.Headers.Count ? sheet.Headers[column] : null;
                return label is null && header is null ? cell : $"{cell}（{label ?? $"行{row}"} の {header ?? "列" + OfficeDocumentReader.ColumnName(column)}）";
            }
            foreach (var edit in edits)
            {
                switch (edit.Op)
                {
                    case "replace" when !string.IsNullOrEmpty(edit.Find) && edit.With is not null:
                        var count = workbook.WorksheetParts.SelectMany(part => part.Worksheet!.Descendants<Cell>())
                            .Where(cell => cell.CellFormula is null)
                            .Sum(cell => Count(OfficeDocumentReader.CellText(cell, shared), edit.Find));
                        changes.Add(new($"「{edit.Find}」→「{edit.With}」（{count}セル）", count > 0 ? "ok" : "not-found"));
                        break;
                    case "set-cell" when FindSheet(workbook, edit.Sheet) is { } part && IsCellReference(edit.Cell) && edit.Value is not null:
                        var existing = part.Worksheet!.Descendants<Cell>().FirstOrDefault(cell => string.Equals(cell.CellReference?.Value, edit.Cell, StringComparison.OrdinalIgnoreCase));
                        if (existing?.CellFormula is not null) { changes.Add(new($"{edit.Cell} は式のセルなので変えません", "formula")); break; }
                        var before = existing is null ? "（空）" : OfficeDocumentReader.CellText(existing, shared);
                        changes.Add(new($"シート「{SheetName(workbook, part)}」{Where(SheetName(workbook, part), edit.Cell!.ToUpperInvariant())}: {before} → {edit.Value}", "ok"));
                        break;
                    case "append-row" when FindSheet(workbook, edit.Sheet) is { } part && edit.Values is { Count: > 0 and <= 50 }:
                        changes.Add(new($"シート「{SheetName(workbook, part)}」の最後に1行追加: {string.Join(" | ", edit.Values)}", "ok"));
                        break;
                    case var structural when ExcelOperations.Structural.Contains(structural) && edits.Count > 1:
                        changes.Add(new($"「{edit.Op}」は表の形が変わる操作のため、ほかの変更と一緒にはできません（1つずつ依頼してください）", "invalid"));
                        break;
                    case var operation when ExcelOperations.All.Contains(operation) && ExcelOperations.Describe(workbook, edit) is { } described:
                        changes.Add(described);
                        break;
                    case "format-rows" or "format-cells" when ExcelFormatting.Describe(workbook, shared, edit) is { } format:
                        changes.Add(format);
                        break;
                    case "add-sheet" when ExcelFormatting.SheetNameProblem(workbook, edit.Name) is var problem:
                        changes.Add(new($"シート「{edit.Name}」を追加" + (problem is null ? "" : $"（{problem}）"), problem is null ? "ok" : "invalid"));
                        break;
                    default:
                        changes.Add(new($"確認できない変更です（{edit.Op} {edit.Sheet} {edit.Cell}）", "invalid"));
                        break;
                }
            }
        }
        return new OfficeEditPreview(Guid.NewGuid().ToString("N")[..12], Path.GetFullPath(path), edits, changes, Hash(path));
    }

    public static OfficeEditResult Apply(OfficeEditPreview preview)
    {
        if (!preview.CanApply) throw new InvalidOperationException("確認できない変更があるため、ファイルを作りません。");
        if (Hash(preview.SourcePath) != preview.SourceSha256) throw new InvalidOperationException("プレビューの後で元のファイルが変わったため、作りません。もう一度依頼してください。");
        if (preview.Edits is [{ Op: "csv" } csvEdit])
        {
            var csvPath = Path.ChangeExtension(UniqueOutputPath(preview.SourcePath), ".csv");
            File.WriteAllText(csvPath, ExcelOperations.Csv(preview.SourcePath, csvEdit.Sheet), new System.Text.UTF8Encoding(true));
            var csvProblems = ExcelOperations.Verify(preview.SourcePath, csvPath, csvEdit).ToList();
            if (Hash(preview.SourcePath) != preview.SourceSha256) csvProblems.Add("元のファイルが変わっています");
            return new OfficeEditResult(csvPath, csvProblems);
        }
        var output = UniqueOutputPath(preview.SourcePath);
        File.Copy(preview.SourcePath, output);
        try
        {
            if (Path.GetExtension(output).Equals(".docx", StringComparison.OrdinalIgnoreCase)) ApplyWord(output, preview.Edits);
            else if (IsPowerPoint(output))
            {
                using var presentation = PresentationDocument.Open(output, true);
                foreach (var edit in preview.Edits) PowerPointOperations.Apply(presentation, edit);
            }
            else ApplyExcel(output, preview.Edits);
        }
        catch
        {
            // A half-written copy is moved aside, never left looking like a result.
            var failed = output + ".failed";
            if (File.Exists(output)) File.Move(output, failed, overwrite: true);
            throw;
        }
        var mismatches = Verify(preview, output).ToList();
        if (Hash(preview.SourcePath) != preview.SourceSha256) mismatches.Add("元のファイルが変わっています");
        // A file can hold the right text and still be one Word or Excel refuses to open.
        var errorsBefore = FormatErrors(preview.SourcePath);
        var errorsAfter = FormatErrors(output);
        if (errorsAfter > errorsBefore) mismatches.Add($"ファイルの形式に誤りが増えています（{errorsBefore}件 → {errorsAfter}件）");
        return new OfficeEditResult(output, mismatches);
    }

    private static void ApplyWord(string path, IReadOnlyList<OfficeEdit> edits)
    {
        using var document = WordprocessingDocument.Open(path, true);
        foreach (var operation in edits.Where(edit => edit.Op != "replace")) WordOperations.Apply(document, operation);
        foreach (var paragraph in document.MainDocumentPart!.Document!.Body!.Descendants<W.Paragraph>().ToArray())
        {
            foreach (var edit in edits.Where(edit => edit.Op == "replace"))
            {
                if (!paragraph.InnerText.Contains(edit.Find!, StringComparison.Ordinal)) continue;
                // Inside one run the formatting stays; text split across runs is joined into the first run.
                foreach (var text in paragraph.Descendants<W.Text>()) text.Text = text.Text.Replace(edit.Find!, edit.With!, StringComparison.Ordinal);
                if (!paragraph.InnerText.Contains(edit.Find!, StringComparison.Ordinal)) continue;
                var texts = paragraph.Descendants<W.Text>().ToArray();
                if (texts.Length == 0) continue;
                texts[0].Text = string.Concat(texts.Select(text => text.Text)).Replace(edit.Find!, edit.With!, StringComparison.Ordinal);
                texts[0].Space = SpaceProcessingModeValues.Preserve;
                foreach (var text in texts.Skip(1)) text.Text = "";
            }
        }
        document.MainDocumentPart.Document.Save();
    }

    private static void ApplyExcel(string path, IReadOnlyList<OfficeEdit> edits)
    {
        using var document = SpreadsheetDocument.Open(path, true);
        var workbook = document.WorkbookPart!;
        foreach (var edit in edits)
        {
            switch (edit.Op)
            {
                case "replace":
                    if (workbook.SharedStringTablePart?.SharedStringTable is { } table)
                        foreach (var text in table.Descendants<Text>()) text.Text = text.Text.Replace(edit.Find!, edit.With!, StringComparison.Ordinal);
                    foreach (var text in workbook.WorksheetParts.SelectMany(part => part.Worksheet!.Descendants<InlineString>()).SelectMany(inline => inline.Descendants<Text>()))
                        text.Text = text.Text.Replace(edit.Find!, edit.With!, StringComparison.Ordinal);
                    break;
                case "set-cell":
                    var part = FindSheet(workbook, edit.Sheet)!;
                    WriteValue(GetOrCreateCell(part, edit.Cell!.ToUpperInvariant()), edit.Value!);
                    break;
                case "append-row":
                    var target = FindSheet(workbook, edit.Sheet)!;
                    var data = target.Worksheet!.GetFirstChild<SheetData>()!;
                    var next = (data.Elements<Row>().Select(row => row.RowIndex?.Value ?? 0u).DefaultIfEmpty(0u).Max()) + 1;
                    var newRow = new Row { RowIndex = next };
                    for (var column = 0; column < edit.Values!.Count; column++)
                    {
                        var cell = new Cell { CellReference = OfficeDocumentReader.ColumnName(column) + next };
                        WriteValue(cell, edit.Values[column]);
                        newRow.Append(cell);
                    }
                    data.Append(newRow);
                    break;
                case "format-rows" or "format-cells":
                    ExcelFormatting.Apply(workbook, edit);
                    break;
                case "add-sheet":
                    ExcelFormatting.AddSheet(workbook, edit.Name!.Trim());
                    break;
                case var operation when ExcelOperations.All.Contains(operation) && operation != "csv":
                    ExcelOperations.Apply(workbook, edit);
                    break;
            }
        }
        workbook.WorkbookStylesPart?.Stylesheet?.Save();
        // Formulas that depend on the edited cells are recalculated by Excel when the file is opened.
        // calcPr has a fixed place in workbook.xml (after sheets and definedNames); Excel refuses files that get it wrong.
        var calculation = workbook.Workbook!.CalculationProperties;
        if (calculation is null)
        {
            calculation = new CalculationProperties();
            OpenXmlElement anchor = (OpenXmlElement?)workbook.Workbook!.DefinedNames ?? workbook.Workbook!.Sheets!;
            workbook.Workbook!.InsertAfter(calculation, anchor);
        }
        calculation.FullCalculationOnLoad = true;
        foreach (var part in workbook.WorksheetParts) part.Worksheet!.Save();
        workbook.SharedStringTablePart?.SharedStringTable?.Save();
        workbook.Workbook!.Save();
    }

    /// <summary>Reads the new file back: replaced texts gone and new ones present, cells holding their new values.</summary>
    private static IEnumerable<string> Verify(OfficeEditPreview preview, string output)
    {
        if (IsPowerPoint(output))
        {
            foreach (var edit in preview.Edits)
                foreach (var problem in PowerPointOperations.Verify(preview.SourcePath, output, edit)) yield return problem;
            yield break;
        }
        var after = OfficeDocumentReader.Read(output);
        var word = after.Kind == "word";
        var allText = word ? string.Join("\n", after.Paragraphs) : null;
        using var excel = word ? null : SpreadsheetDocument.Open(output, false);
        var shared = excel?.WorkbookPart?.SharedStringTablePart?.SharedStringTable;
        foreach (var edit in preview.Edits)
        {
            switch (edit.Op)
            {
                case "replace":
                    var text = allText ?? string.Join("\n", excel!.WorkbookPart!.WorksheetParts.SelectMany(part => part.Worksheet!.Descendants<Cell>()).Select(cell => OfficeDocumentReader.CellText(cell, shared)));
                    if (!edit.With!.Contains(edit.Find!, StringComparison.Ordinal) && text.Contains(edit.Find!, StringComparison.Ordinal)) yield return $"「{edit.Find}」が残っています";
                    if (edit.With!.Length > 0 && !text.Contains(edit.With, StringComparison.Ordinal)) yield return $"「{edit.With}」が見つかりません";
                    break;
                case "set-cell":
                    var cell = FindSheet(excel!.WorkbookPart!, edit.Sheet)!.Worksheet!.Descendants<Cell>().FirstOrDefault(item => string.Equals(item.CellReference?.Value, edit.Cell, StringComparison.OrdinalIgnoreCase));
                    if (cell is null || OfficeDocumentReader.CellText(cell, shared) != NormalizedValue(edit.Value!)) yield return $"{edit.Cell} が {edit.Value} になっていません";
                    break;
                case "append-row":
                    var last = FindSheet(excel!.WorkbookPart!, edit.Sheet)!.Worksheet!.Descendants<Row>().LastOrDefault();
                    var values = last?.Elements<Cell>().Select(item => OfficeDocumentReader.CellText(item, shared)).ToArray() ?? [];
                    if (!values.SequenceEqual(edit.Values!.Select(NormalizedValue))) yield return "追加した行が見つかりません";
                    break;
                case "format-rows" or "format-cells":
                    foreach (var problem in ExcelFormatting.Verify(excel!.WorkbookPart!, edit)) yield return problem;
                    break;
                case var wordOperation when word && WordOperations.All.Contains(wordOperation):
                    foreach (var problem in WordOperations.Verify(preview.SourcePath, output, edit)) yield return problem;
                    break;
                case "add-sheet":
                    if (!(excel!.WorkbookPart!.Workbook!.Sheets?.Elements<Sheet>().Any(sheet => sheet.Name?.Value == edit.Name!.Trim()) ?? false)) yield return $"シート「{edit.Name}」が見つかりません";
                    break;
                case var operation when ExcelOperations.All.Contains(operation):
                    foreach (var problem in ExcelOperations.Verify(preview.SourcePath, output, edit)) yield return problem;
                    break;
            }
        }
    }

    private static void WriteValue(Cell cell, string value)
    {
        cell.CellFormula = null;
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            cell.DataType = null;
            cell.CellValue = new CellValue(number.ToString(CultureInfo.InvariantCulture));
            cell.InlineString = null;
        }
        else
        {
            cell.CellValue = null;
            cell.DataType = CellValues.InlineString;
            cell.InlineString = new InlineString(new Text(value) { Space = SpaceProcessingModeValues.Preserve });
        }
    }

    private static string NormalizedValue(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number.ToString(CultureInfo.InvariantCulture) : value;

    private static Cell GetOrCreateCell(WorksheetPart part, string reference)
    {
        var data = part.Worksheet!.GetFirstChild<SheetData>()!;
        var rowNumber = uint.Parse(new string(reference.SkipWhile(char.IsLetter).ToArray()), CultureInfo.InvariantCulture);
        var row = data.Elements<Row>().FirstOrDefault(item => item.RowIndex?.Value == rowNumber);
        if (row is null)
        {
            row = new Row { RowIndex = rowNumber };
            var after = data.Elements<Row>().LastOrDefault(item => (item.RowIndex?.Value ?? 0) < rowNumber);
            if (after is null) data.PrependChild(row); else data.InsertAfter(row, after);
        }
        var existing = row.Elements<Cell>().FirstOrDefault(cell => string.Equals(cell.CellReference?.Value, reference, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;
        var cellNew = new Cell { CellReference = reference };
        var column = OfficeDocumentReader.ColumnIndex(reference);
        var before = row.Elements<Cell>().FirstOrDefault(cell => OfficeDocumentReader.ColumnIndex(cell.CellReference?.Value) > column);
        if (before is null) row.Append(cellNew); else row.InsertBefore(cellNew, before);
        return cellNew;
    }

    internal static WorksheetPart? FindSheet(WorkbookPart workbook, string? name)
    {
        var sheets = workbook.Workbook!.Sheets?.Elements<Sheet>().ToArray() ?? [];
        var sheet = string.IsNullOrWhiteSpace(name) ? sheets.FirstOrDefault() : sheets.FirstOrDefault(item => string.Equals(item.Name?.Value, name, StringComparison.OrdinalIgnoreCase));
        return sheet?.Id?.Value is { } id ? workbook.GetPartById(id) as WorksheetPart : null;
    }

    internal static string SheetName(WorkbookPart workbook, WorksheetPart part) =>
        workbook.Workbook!.Sheets?.Elements<Sheet>().FirstOrDefault(sheet => sheet.Id?.Value == workbook.GetIdOfPart(part))?.Name?.Value ?? "";

    private static bool IsCellReference(string? value) =>
        value is not null && System.Text.RegularExpressions.Regex.IsMatch(value, "^[A-Za-z]{1,3}[1-9][0-9]{0,6}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static int Count(string text, string find)
    {
        var count = 0;
        for (var index = text.IndexOf(find, StringComparison.Ordinal); index >= 0; index = text.IndexOf(find, index + find.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    private static string UniqueOutputPath(string source)
    {
        var folder = Path.GetDirectoryName(source)!;
        var name = Path.GetFileNameWithoutExtension(source);
        var extension = Path.GetExtension(source);
        for (var index = 1; index < 1000; index++)
        {
            var candidate = Path.Combine(folder, index == 1 ? $"{name}_LS編集{extension}" : $"{name}_LS編集{index}{extension}");
            if (!File.Exists(candidate)) return candidate;
        }
        throw new IOException("新しいファイル名を決められませんでした。");
    }

    private static int FormatErrors(string path)
    {
        var validator = new DocumentFormat.OpenXml.Validation.OpenXmlValidator();
        if (Path.GetExtension(path).Equals(".docx", StringComparison.OrdinalIgnoreCase))
        {
            using var document = WordprocessingDocument.Open(path, false);
            return validator.Validate(document).Take(1_000).Count();
        }
        if (IsPowerPoint(path))
        {
            using var presentation = PresentationDocument.Open(path, false);
            return validator.Validate(presentation).Take(1_000).Count();
        }
        using var workbook = SpreadsheetDocument.Open(path, false);
        return validator.Validate(workbook).Take(1_000).Count();
    }

    private static bool IsPowerPoint(string path) => Path.GetExtension(path).Equals(".pptx", StringComparison.OrdinalIgnoreCase);

    private static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
