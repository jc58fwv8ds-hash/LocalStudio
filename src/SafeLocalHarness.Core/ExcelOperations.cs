using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace SafeLocalHarness.Core;

/// <summary>
/// Excel operations of phase A (user: "Office や Acrobat でできる操作は基本的にすべてチャットから"): sort, remove duplicate
/// rows, insert / delete rows, formulas, number formats, column widths, frozen panes, borders, conditional formats,
/// filters, renaming / deleting sheets and CSV export. Each one says what it will change, changes the working copy,
/// and is read back from the saved file against what the original file says it should become.
/// Operations that move rows refuse a sheet whose formulas point at other rows or that has merged cells, rather than
/// leave broken references; they run alone in a request so their read-back is exact.
/// </summary>
public static class ExcelOperations
{
    public static readonly string[] Structural = ["sort", "dedupe-rows", "insert-rows", "delete-rows", "delete-sheet", "csv"];
    public static readonly string[] All = ["sort", "dedupe-rows", "insert-rows", "delete-rows", "formula", "number-format", "column-width", "freeze", "borders", "conditional-format", "autofilter", "rename-sheet", "delete-sheet", "csv"];

    private static readonly Dictionary<string, string> NumberFormats = new(StringComparer.Ordinal)
    {
        ["桁区切り"] = "#,##0", ["カンマ"] = "#,##0", ["comma"] = "#,##0", ["#,##0"] = "#,##0",
        ["小数1桁"] = "#,##0.0", ["小数2桁"] = "#,##0.00",
        ["パーセント"] = "0%", ["%"] = "0%", ["percent"] = "0%", ["0%"] = "0%", ["0.0%"] = "0.0%",
        ["円"] = "\"¥\"#,##0", ["通貨"] = "\"¥\"#,##0", ["currency"] = "\"¥\"#,##0",
        ["日付"] = "yyyy/m/d", ["date"] = "yyyy/m/d", ["yyyy/mm/dd"] = "yyyy/mm/dd", ["和暦"] = "[$-ja-JP]ggge\"年\"m\"月\"d\"日\"",
        ["文字列"] = "@", ["標準"] = "General"
    };

    public static string? FormatCode(string? format) =>
        string.IsNullOrWhiteSpace(format) ? null : NumberFormats.TryGetValue(format.Trim(), out var code) ? code : Regex.IsMatch(format, @"^[#0,.%¥""\$\-\[\]a-zA-Z/: ]{1,40}$") ? format.Trim() : null;

    // ---------- reading ----------

    private sealed record SheetTable(WorksheetPart Part, string Name, List<Row> Rows, int Columns);

    private static SheetTable? Table(WorkbookPart workbook, string? sheet)
    {
        if (OfficeEditor.FindSheet(workbook, sheet) is not { } part) return null;
        var rows = part.Worksheet!.GetFirstChild<SheetData>()?.Elements<Row>().ToList() ?? [];
        var columns = rows.SelectMany(row => row.Elements<Cell>()).Select(cell => OfficeDocumentReader.ColumnIndex(cell.CellReference?.Value) + 1).DefaultIfEmpty(0).Max();
        return new SheetTable(part, OfficeEditor.SheetName(workbook, part), rows, columns);
    }

    private static int RowNumber(Row row) => (int)(row.RowIndex?.Value ?? 0u);

    private static string Text(Cell cell, SharedStringTable? shared) => OfficeDocumentReader.CellText(cell, shared);

    private static string[] Values(Row row, int columns, SharedStringTable? shared, bool skipFormulas = false)
    {
        var values = new string[columns];
        foreach (var cell in row.Elements<Cell>())
        {
            // A formula's text follows moved rows (SUM(D2:D7) → SUM(D2:D6)); it is checked separately.
            if (skipFormulas && cell.CellFormula is not null) continue;
            var index = OfficeDocumentReader.ColumnIndex(cell.CellReference?.Value);
            if (index >= 0 && index < columns) values[index] = Text(cell, shared);
        }
        return values.Select(value => value ?? "").ToArray();
    }

    /// <summary>A column named by its letter ("B列", "B") or by its header text in row 1.</summary>
    public static int? ColumnOf(WorkbookPart workbook, string? sheet, string? column)
    {
        if (string.IsNullOrWhiteSpace(column)) return null;
        var name = column.Trim().TrimEnd('列');
        if (Regex.IsMatch(name, "^[A-Za-z]{1,3}$")) return OfficeDocumentReader.ColumnIndex(name.ToUpperInvariant() + "1");
        if (Table(workbook, sheet) is not { } table || table.Rows.Count == 0) return null;
        var headers = Values(table.Rows[0], table.Columns, workbook.SharedStringTablePart?.SharedStringTable);
        var index = Array.FindIndex(headers, header => header.Trim() == name);
        if (index < 0) index = Array.FindIndex(headers, header => header.Length > 0 && (header.Contains(name, StringComparison.Ordinal) || name.Contains(header, StringComparison.Ordinal)));
        return index < 0 ? null : index;
    }

    /// <summary>A formula that points at a row other than its own (a total such as SUM(D2:D7)).</summary>
    private static bool PointsElsewhere(Row row) => row.Elements<Cell>().Any(cell => cell.CellFormula is { } formula &&
        Regex.Matches(formula.Text ?? "", @"\$?[A-Z]{1,3}\$?(\d+)").Any(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) != RowNumber(row)));

    /// <summary>The rows that hold data: from row 2 up to the first row with a total-like formula (that row and later ones stay).</summary>
    private static List<Row> DataRows(SheetTable table) =>
        table.Rows.Where(row => RowNumber(row) >= 2).OrderBy(RowNumber).TakeWhile(row => !PointsElsewhere(row)).ToList();

    private static readonly Regex Reference = new(@"(\$?[A-Z]{1,3}\$?)(\d+)(?::(\$?[A-Z]{1,3}\$?)(\d+))?", RegexOptions.CultureInvariant);

    /// <summary>
    /// Moves the row numbers in every formula of the sheet as rows are deleted or inserted (map gives a row's new number,
    /// null when it is deleted); a range keeps its surviving ends, as Excel does. Returns the problem when a formula
    /// points only at deleted rows (it would become #REF!).
    /// </summary>
    private static string? RemapFormulas(SheetTable table, Func<int, int?> map, bool apply)
    {
        foreach (var cell in table.Part.Worksheet!.Descendants<Cell>().Where(cell => cell.CellFormula is not null))
        {
            string? problem = null;
            var text = Reference.Replace(cell.CellFormula!.Text ?? "", match =>
            {
                var first = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                if (!match.Groups[3].Success)
                {
                    if (map(first) is { } moved) return match.Groups[1].Value + moved.ToString(CultureInfo.InvariantCulture);
                    problem = $"{cell.CellReference} の式が削除する行（{first}行目）だけを参照しているため、削除できません";
                    return match.Value;
                }
                var last = int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture);
                int? start = null, end = null;
                for (var row = first; row <= last && start is null; row++) start = map(row);
                for (var row = last; row >= first && end is null; row--) end = map(row);
                if (start is null || end is null) { problem = $"{cell.CellReference} の式の範囲がすべて削除されるため、削除できません"; return match.Value; }
                return match.Groups[1].Value + start.Value.ToString(CultureInfo.InvariantCulture) + ":" + match.Groups[3].Value + end.Value.ToString(CultureInfo.InvariantCulture);
            });
            if (problem is not null) return problem;
            if (apply) cell.CellFormula.Text = text;
        }
        // Ranges of conditional formats and the filter follow the rows too (a range fully deleted is dropped).
        if (apply)
        {
            var worksheet = table.Part.Worksheet!;
            foreach (var formatting in worksheet.Elements<ConditionalFormatting>().ToArray())
            {
                var moved = (formatting.SequenceOfReferences?.Items ?? []).Select(item => MoveRange(item.Value ?? "", map)).OfType<string>().ToArray();
                if (moved.Length == 0) formatting.Remove(); else formatting.SequenceOfReferences = new ListValue<StringValue>(moved.Select(item => new StringValue(item)));
            }
            if (worksheet.GetFirstChild<AutoFilter>() is { Reference.Value: { } filter } autoFilter)
            {
                if (MoveRange(filter, map) is { } movedFilter) autoFilter.Reference = movedFilter; else autoFilter.Remove();
            }
        }
        return null;
    }

    private static string? MoveRange(string range, Func<int, int?> map)
    {
        var match = Reference.Match(range);
        if (!match.Success) return range;
        var first = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var last = match.Groups[3].Success ? int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture) : first;
        int? start = null, end = null;
        for (var row = first; row <= last && start is null; row++) start = map(row);
        for (var row = last; row >= first && end is null; row--) end = map(row);
        if (start is null || end is null) return null;
        return match.Groups[1].Value + start.Value.ToString(CultureInfo.InvariantCulture) + (match.Groups[3].Success ? ":" + match.Groups[3].Value + end.Value.ToString(CultureInfo.InvariantCulture) : "");
    }

    private static Func<int, int?> DeletionMap(IReadOnlySet<int> deleted) => row => deleted.Contains(row) ? null : row - deleted.Count(number => number < row);

    /// <summary>Merged cells cannot follow moved rows here; the sheet is refused rather than left with wrong merges.</summary>
    private static string? MergeProblem(SheetTable table) =>
        table.Part.Worksheet!.GetFirstChild<MergeCells>()?.Any() == true ? "結合されたセルがあるため、行を動かせません（結合を解除してからお試しください）" : null;

    /// <summary>Data rows equal in every column to an earlier data row (the first one is kept).</summary>
    private static List<Row> DuplicateRows(SheetTable table, SharedStringTable? shared)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return DataRows(table).Where(row => !seen.Add(string.Join("\u001f", Values(row, table.Columns, shared)))).ToList();
    }

    private static (int First, int Last)? RowRange(string? rows)
    {
        var match = Regex.Match(rows ?? "", @"^\s*(\d+)\s*(?:[-~〜]\s*(\d+))?\s*$");
        if (!match.Success) return null;
        var first = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var last = match.Groups[2].Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : first;
        return first < 1 || last < first ? null : (first, last);
    }

    private static (int Left, int Top, int Right, int Bottom)? Range(string? range)
    {
        var match = Regex.Match(range ?? "", @"^([A-Za-z]{1,3})([1-9][0-9]{0,6})(?::([A-Za-z]{1,3})([1-9][0-9]{0,6}))?$");
        if (!match.Success) return null;
        var left = OfficeDocumentReader.ColumnIndex(match.Groups[1].Value.ToUpperInvariant() + "1");
        var top = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var right = match.Groups[3].Success ? OfficeDocumentReader.ColumnIndex(match.Groups[3].Value.ToUpperInvariant() + "1") : left;
        var bottom = match.Groups[4].Success ? int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture) : top;
        return (Math.Min(left, right), Math.Min(top, bottom), Math.Max(left, right), Math.Max(top, bottom));
    }

    private static string Letter(int column) => OfficeDocumentReader.ColumnName(column);

    /// <summary>The whole used table ("A1:D6"), or a column's data cells ("C2:C6").</summary>
    private static string UsedRange(SheetTable table) => $"A1:{Letter(Math.Max(0, table.Columns - 1))}{Math.Max(1, table.Rows.Select(RowNumber).DefaultIfEmpty(1).Max())}";

    private static string ColumnData(SheetTable table, int column) => $"{Letter(column)}2:{Letter(column)}{Math.Max(2, table.Rows.Select(RowNumber).DefaultIfEmpty(2).Max())}";

    // ---------- preview ----------

    public static OfficeChange? Describe(WorkbookPart workbook, OfficeEdit edit)
    {
        var shared = workbook.SharedStringTablePart?.SharedStringTable;
        var table = Table(workbook, edit.Sheet);
        if (edit.Op is not ("rename-sheet" or "delete-sheet") && table is null) return new($"シート「{edit.Sheet}」が見つかりません", "invalid");
        switch (edit.Op)
        {
            case "sort":
                if (ColumnOf(workbook, edit.Sheet, edit.Column) is not { } sortColumn) return new($"並べ替える列「{edit.Column}」が見つかりません", "invalid");
                if (MergeProblem(table!) is { } sortProblem) return new(sortProblem, "invalid");
                var sortRows = DataRows(table!);
                if (sortRows.Count < 2) return new("並べ替える行がありません", "not-found");
                return new($"シート「{table!.Name}」の {RowNumber(sortRows[0])}～{RowNumber(sortRows[^1])}行目を、{Letter(sortColumn)}列（{Values(table.Rows[0], table.Columns, shared)[sortColumn]}）の{(Descending(edit.Order) ? "大きい順（降順）" : "小さい順（昇順）")}に並べ替え（1行目は見出しとしてそのまま）", "ok");
            case "dedupe-rows":
                if (MergeProblem(table!) is { } dedupeProblem) return new(dedupeProblem, "invalid");
                var dedupeDeleted = DuplicateRows(table!, shared).Select(RowNumber).ToHashSet();
                if (RemapFormulas(table!, DeletionMap(dedupeDeleted), apply: false) is { } dedupeFormula) return new(dedupeFormula, "invalid");
                var duplicates = dedupeDeleted.Count;
                return new($"シート「{table!.Name}」で、ほかの行とすべての列が同じ行を {duplicates}行 削除（最初の行を残す）", duplicates > 0 ? "ok" : "not-found");
            case "delete-rows" or "insert-rows":
                if (RowRange(edit.Rows) is not { } rows) return new($"行の指定「{edit.Rows}」が読めません（例: 3 や 3-5）", "invalid");
                if (MergeProblem(table!) is { } rowProblem) return new(rowProblem, "invalid");
                if (edit.Op == "delete-rows")
                {
                    if (RemapFormulas(table!, DeletionMap(Enumerable.Range(rows.First, rows.Last - rows.First + 1).ToHashSet()), apply: false) is { } deleteFormula) return new(deleteFormula, "invalid");
                    var existing = table!.Rows.Count(row => RowNumber(row) >= rows.First && RowNumber(row) <= rows.Last);
                    return new($"シート「{table.Name}」の {rows.First}{(rows.Last > rows.First ? $"～{rows.Last}" : "")}行目を削除（下の行を上へ詰める）", existing > 0 ? "ok" : "not-found");
                }
                var count = Math.Clamp(edit.Count ?? 1, 1, 1000);
                return new($"シート「{table!.Name}」の {rows.First}行目に空の行を {count}行 挿入（下の行を下へずらす）", "ok");
            case "formula":
                if (!IsCell(edit.Cell) || string.IsNullOrWhiteSpace(edit.Formula)) return new("式を入れるセルと式を指定してください（例: B10 に =SUM(B2:B9)）", "invalid");
                var formula = edit.Formula.Trim().TrimStart('=');
                if (!Regex.IsMatch(formula, @"^[A-Z][A-Z0-9.]*\(.*\)$|^[A-Z$0-9:+\-*/(). ,]+$") || Regex.IsMatch(formula, "(?i)WEBSERVICE|HYPERLINK|CALL|REGISTER|EXEC|INDIRECT|RTD"))
                    return new($"使えない式です（{formula}）", "invalid");
                return new($"シート「{table!.Name}」の {edit.Cell!.ToUpperInvariant()} に式 ={formula}（Excel で開いたときに計算されます）", "ok");
            case "number-format":
                if (FormatCode(edit.Format) is not { } code) return new($"表示形式「{edit.Format}」が読めません（桁区切り・パーセント・円・日付など）", "invalid");
                if (Range(edit.Range) is null) return new($"範囲「{edit.Range}」が読めません", "invalid");
                return new($"シート「{table!.Name}」の {edit.Range!.ToUpperInvariant()} の表示形式を「{edit.Format}」（{code}）に", "ok");
            case "column-width":
                if (ColumnSpan(edit.Column) is not { } span || edit.Width is not (> 0 and <= 255)) return new("列（例: A や A:C）と幅（1～255）を指定してください", "invalid");
                return new($"シート「{table!.Name}」の {Letter(span.First)}{(span.Last > span.First ? ":" + Letter(span.Last) : "")}列の幅を {edit.Width:0.#} に", "ok");
            case "freeze":
                if (!IsCell(edit.Cell)) return new("固定の位置をセルで指定してください（例: 見出し行なら A2）", "invalid");
                return new($"シート「{table!.Name}」のウィンドウ枠を {edit.Cell!.ToUpperInvariant()} で固定（それより上の行・左の列が動かない）", "ok");
            case "borders":
                if (Range(edit.Range) is null) return new($"範囲「{edit.Range}」が読めません", "invalid");
                return new($"シート「{table!.Name}」の {edit.Range!.ToUpperInvariant()} に{(edit.Format == "medium" ? "太い" : "細い")}罫線（格子）", "ok");
            case "conditional-format":
                if (Range(edit.Range) is null || ConditionalOperator(edit.Operator) is null || string.IsNullOrWhiteSpace(edit.Value) || (ExcelFormatting.Argb(edit.Fill) is null && ExcelFormatting.Argb(edit.Color) is null))
                    return new("範囲・条件（以上・以下・より大きい・より小さい・等しい）・値・色を指定してください", "invalid");
                return new($"シート「{table!.Name}」の {edit.Range!.ToUpperInvariant()} で、値が {edit.Value} {OperatorName(edit.Operator!)} のセルを{(edit.Fill is { Length: > 0 } ? $"塗りつぶし {edit.Fill}" : "")}{(edit.Color is { Length: > 0 } ? $" 文字色 {edit.Color}" : "")}に（条件付き書式）", "ok");
            case "autofilter":
                var filterRange = Range(edit.Range) is not null ? edit.Range!.ToUpperInvariant() : UsedRange(table!);
                return new($"シート「{table!.Name}」の {filterRange} にフィルター（見出しに ▼ ボタン）", "ok");
            case "rename-sheet":
                if (OfficeEditor.FindSheet(workbook, edit.Sheet) is null) return new($"シート「{edit.Sheet}」が見つかりません", "invalid");
                return ExcelFormatting.SheetNameProblem(workbook, edit.Name) is { } nameProblem
                    ? new($"シート名「{edit.Name}」にできません（{nameProblem}）", "invalid")
                    : new($"シート「{edit.Sheet}」の名前を「{edit.Name}」に", "ok");
            case "delete-sheet":
                if (OfficeEditor.FindSheet(workbook, edit.Sheet) is null || string.IsNullOrWhiteSpace(edit.Sheet)) return new($"削除するシート「{edit.Sheet}」が見つかりません", "invalid");
                if ((workbook.Workbook!.Sheets?.Elements<Sheet>().Count() ?? 0) <= 1) return new("シートが1つしかないため、削除できません", "invalid");
                if (workbook.Workbook.DefinedNames?.Any() == true || workbook.WorksheetParts.Any(part => part.Worksheet!.Descendants<CellFormula>().Any(cell => (cell.Text ?? "").Contains(edit.Sheet!, StringComparison.Ordinal))))
                    return new($"ほかの式や名前がシート「{edit.Sheet}」を参照しているため、削除できません", "invalid");
                return new($"シート「{edit.Sheet}」を削除（作業用のコピーから。元のファイルはそのまま）", "ok");
            case "csv":
                return new($"シート「{table!.Name}」を CSV（UTF-8、{table.Rows.Count}行）として書き出す", table.Rows.Count > 0 ? "ok" : "not-found");
            default:
                return null;
        }
    }

    private static bool IsCell(string? cell) => cell is not null && Regex.IsMatch(cell, "^[A-Za-z]{1,3}[1-9][0-9]{0,6}$");

    private static bool Descending(string? order) => order is not null && Regex.IsMatch(order, "(?i)desc|降順|大きい|多い|高い|新しい|遅い");

    private static (int First, int Last)? ColumnSpan(string? columns)
    {
        var match = Regex.Match((columns ?? "").Replace("列", ""), "^([A-Za-z]{1,3})(?::([A-Za-z]{1,3}))?$");
        if (!match.Success) return null;
        var first = OfficeDocumentReader.ColumnIndex(match.Groups[1].Value.ToUpperInvariant() + "1");
        var last = match.Groups[2].Success ? OfficeDocumentReader.ColumnIndex(match.Groups[2].Value.ToUpperInvariant() + "1") : first;
        return (Math.Min(first, last), Math.Max(first, last));
    }

    private static ConditionalFormattingOperatorValues? ConditionalOperator(string? op) => op switch
    {
        "greaterThanOrEqual" or ">=" or "以上" => ConditionalFormattingOperatorValues.GreaterThanOrEqual,
        "greaterThan" or ">" or "より大きい" => ConditionalFormattingOperatorValues.GreaterThan,
        "lessThanOrEqual" or "<=" or "以下" => ConditionalFormattingOperatorValues.LessThanOrEqual,
        "lessThan" or "<" or "未満" or "より小さい" => ConditionalFormattingOperatorValues.LessThan,
        "equal" or "=" or "等しい" => ConditionalFormattingOperatorValues.Equal,
        _ => null
    };

    private static string OperatorName(string op)
    {
        var value = ConditionalOperator(op);
        return value == ConditionalFormattingOperatorValues.GreaterThanOrEqual ? "以上" : value == ConditionalFormattingOperatorValues.GreaterThan ? "より大きい"
            : value == ConditionalFormattingOperatorValues.LessThanOrEqual ? "以下" : value == ConditionalFormattingOperatorValues.LessThan ? "未満" : "と等しい";
    }

    // ---------- apply ----------

    public static void Apply(WorkbookPart workbook, OfficeEdit edit)
    {
        var shared = workbook.SharedStringTablePart?.SharedStringTable;
        var table = Table(workbook, edit.Sheet);
        switch (edit.Op)
        {
            case "sort":
            {
                var column = ColumnOf(workbook, edit.Sheet, edit.Column)!.Value;
                var data = DataRows(table!);
                var keyed = data.Select(row => (Row: row, Key: Values(row, table!.Columns, shared)[column])).ToList();
                var numeric = keyed.All(item => item.Key.Length == 0 || double.TryParse(item.Key, NumberStyles.Float, CultureInfo.InvariantCulture, out _));
                var comparer = StringComparer.Create(new CultureInfo("ja-JP"), false);
                IEnumerable<(Row Row, string Key)> ordered = numeric
                    ? keyed.OrderBy(item => item.Key.Length == 0 ? double.MaxValue : double.Parse(item.Key, CultureInfo.InvariantCulture))
                    : keyed.OrderBy(item => item.Key, comparer);
                if (Descending(edit.Order)) ordered = numeric
                    ? keyed.OrderByDescending(item => item.Key.Length == 0 ? double.MinValue : double.Parse(item.Key, CultureInfo.InvariantCulture))
                    : keyed.OrderByDescending(item => item.Key, comparer);
                var numbers = data.Select(RowNumber).ToList();
                var sheetData = table!.Part.Worksheet!.GetFirstChild<SheetData>()!;
                var after = table.Rows.Where(row => RowNumber(row) > numbers[^1]).OrderBy(RowNumber).FirstOrDefault();
                foreach (var row in data) row.Remove();
                var position = 0;
                foreach (var (row, _) in ordered.ToList())
                {
                    Renumber(row, numbers[position++], sameRowFormulas: true);
                    if (after is null) sheetData.Append(row); else sheetData.InsertBefore(row, after);
                }
                break;
            }
            case "dedupe-rows":
            {
                var removed = DuplicateRows(table!, shared);
                RemapFormulas(table!, DeletionMap(removed.Select(RowNumber).ToHashSet()), apply: true);
                Compact(table!, removed);
                break;
            }
            case "delete-rows":
            {
                var (first, last) = RowRange(edit.Rows)!.Value;
                RemapFormulas(table!, DeletionMap(Enumerable.Range(first, last - first + 1).ToHashSet()), apply: true);
                Compact(table!, table!.Rows.Where(row => RowNumber(row) >= first && RowNumber(row) <= last).ToList(), first, last - first + 1);
                break;
            }
            case "insert-rows":
            {
                var (first, _) = RowRange(edit.Rows)!.Value;
                var count = Math.Clamp(edit.Count ?? 1, 1, 1000);
                RemapFormulas(table!, row => row >= first ? row + count : row, apply: true);
                foreach (var row in table!.Rows.Where(row => RowNumber(row) >= first).OrderByDescending(RowNumber)) Renumber(row, RowNumber(row) + count, sameRowFormulas: false);
                break;
            }
            case "formula":
            {
                var cell = GetOrCreateCell(table!.Part, edit.Cell!.ToUpperInvariant());
                cell.CellValue = null; cell.DataType = null; cell.InlineString = null;
                cell.CellFormula = new CellFormula(edit.Formula!.Trim().TrimStart('='));
                break;
            }
            case "number-format":
                Restyle(workbook, table!, Range(edit.Range)!.Value, format => format.NumberFormatId = NumberFormatId(Stylesheet(workbook), FormatCode(edit.Format)!), format => format.ApplyNumberFormat = true);
                break;
            case "column-width":
            {
                var (first, last) = ColumnSpan(edit.Column)!.Value;
                var worksheet = table!.Part.Worksheet!;
                var columns = worksheet.GetFirstChild<Columns>() ?? InsertInOrder(worksheet, new Columns());
                foreach (var existing in columns.Elements<Column>().Where(item => item.Min!.Value - 1 <= last && item.Max!.Value - 1 >= first).ToArray()) existing.Remove();
                columns.Append(new Column { Min = (uint)first + 1, Max = (uint)last + 1, Width = edit.Width!.Value, CustomWidth = true });
                var sorted = columns.Elements<Column>().OrderBy(item => item.Min!.Value).ToArray();
                columns.RemoveAllChildren<Column>();
                foreach (var item in sorted) columns.Append(item);
                break;
            }
            case "freeze":
            {
                var cell = edit.Cell!.ToUpperInvariant();
                var column = OfficeDocumentReader.ColumnIndex(cell);
                var row = int.Parse(new string(cell.SkipWhile(char.IsLetter).ToArray()), CultureInfo.InvariantCulture) - 1;
                var worksheet = table!.Part.Worksheet!;
                var views = worksheet.GetFirstChild<SheetViews>() ?? InsertInOrder(worksheet, new SheetViews());
                var view = views.GetFirstChild<SheetView>() ?? views.AppendChild(new SheetView { WorkbookViewId = 0 });
                view.RemoveAllChildren<Pane>(); view.RemoveAllChildren<Selection>();
                var paneName = row > 0 && column > 0 ? PaneValues.BottomRight : row > 0 ? PaneValues.BottomLeft : PaneValues.TopRight;
                var pane = new Pane { TopLeftCell = cell, ActivePane = paneName, State = PaneStateValues.Frozen };
                if (column > 0) pane.HorizontalSplit = column;
                if (row > 0) pane.VerticalSplit = row;
                view.PrependChild(pane);
                view.InsertAfter(new Selection { Pane = paneName, ActiveCell = cell, SequenceOfReferences = new ListValue<StringValue>([new StringValue(cell)]) }, pane);
                break;
            }
            case "borders":
            {
                var style = edit.Format == "medium" ? BorderStyleValues.Medium : BorderStyleValues.Thin;
                var stylesheet = Stylesheet(workbook);
                var borders = stylesheet.Borders ??= InsertStyle(stylesheet, new Borders(new Border(new LeftBorder(), new RightBorder(), new TopBorder(), new BottomBorder(), new DiagonalBorder())) { Count = 1 });
                borders.Append(new Border(new LeftBorder { Style = style }, new RightBorder { Style = style }, new TopBorder { Style = style }, new BottomBorder { Style = style }, new DiagonalBorder()));
                borders.Count = (uint)borders.Elements<Border>().Count();
                var borderId = borders.Count.Value - 1;
                Restyle(workbook, table!, Range(edit.Range)!.Value, format => format.BorderId = borderId, format => format.ApplyBorder = true, createMissing: true);
                break;
            }
            case "conditional-format":
            {
                var stylesheet = Stylesheet(workbook);
                var dxfs = stylesheet.DifferentialFormats ??= InsertStyle(stylesheet, new DifferentialFormats { Count = 0 });
                var dxf = new DifferentialFormat();
                if (ExcelFormatting.Argb(edit.Color) is { } font) dxf.Append(new Font(new Color { Rgb = font }));
                if (ExcelFormatting.Argb(edit.Fill) is { } fill) dxf.Append(new Fill(new PatternFill(new BackgroundColor { Rgb = fill }) { PatternType = PatternValues.Solid }));
                dxfs.Append(dxf);
                dxfs.Count = (uint)dxfs.Elements<DifferentialFormat>().Count();
                var worksheet = table!.Part.Worksheet!;
                var priority = worksheet.Descendants<ConditionalFormattingRule>().Select(rule => rule.Priority?.Value ?? 0).DefaultIfEmpty(0).Max() + 1;
                var value = Regex.IsMatch(edit.Value!.Trim(), @"^-?\d+(\.\d+)?$") ? edit.Value.Trim() : "\"" + edit.Value!.Trim().Replace("\"", "\"\"") + "\"";
                InsertInOrder(worksheet, new ConditionalFormatting(new ConditionalFormattingRule(new Formula(value))
                {
                    Type = ConditionalFormatValues.CellIs, Operator = ConditionalOperator(edit.Operator)!.Value, FormatId = dxfs.Count.Value - 1, Priority = priority
                }) { SequenceOfReferences = new ListValue<StringValue>([new StringValue(edit.Range!.ToUpperInvariant())]) });
                break;
            }
            case "autofilter":
            {
                var worksheet = table!.Part.Worksheet!;
                worksheet.RemoveAllChildren<AutoFilter>();
                InsertInOrder(worksheet, new AutoFilter { Reference = Range(edit.Range) is not null ? edit.Range!.ToUpperInvariant() : UsedRange(table) });
                break;
            }
            case "rename-sheet":
            {
                var sheet = workbook.Workbook!.Sheets!.Elements<Sheet>().First(item => string.Equals(item.Name?.Value, edit.Sheet, StringComparison.OrdinalIgnoreCase));
                foreach (var formula in workbook.WorksheetParts.SelectMany(part => part.Worksheet!.Descendants<CellFormula>()))
                    formula.Text = Regex.Replace(formula.Text ?? "", $"'?{Regex.Escape(sheet.Name!.Value!)}'?!", $"'{edit.Name!.Trim().Replace("'", "''")}'!");
                sheet.Name = edit.Name!.Trim();
                break;
            }
            case "delete-sheet":
            {
                var sheet = workbook.Workbook!.Sheets!.Elements<Sheet>().First(item => string.Equals(item.Name?.Value, edit.Sheet, StringComparison.OrdinalIgnoreCase));
                var part = (WorksheetPart)workbook.GetPartById(sheet.Id!.Value!);
                sheet.Remove();
                workbook.DeletePart(part);
                break;
            }
        }
    }

    /// <summary>Removes rows and moves the rows below up (by the removed count above each of them).</summary>
    private static void Compact(SheetTable table, IReadOnlyList<Row> removed, int? from = null, int? count = null)
    {
        var numbers = removed.Select(RowNumber).ToHashSet();
        foreach (var row in removed) row.Remove();
        foreach (var row in table.Rows.Where(row => !numbers.Contains(RowNumber(row))).OrderBy(RowNumber))
        {
            var shift = from is not null ? (RowNumber(row) > from + count - 1 ? count!.Value : 0) : numbers.Count(number => number < RowNumber(row));
            if (shift > 0) Renumber(row, RowNumber(row) - shift, sameRowFormulas: false);
        }
    }

    /// <summary>A row gets a new number; with sameRowFormulas its own-row references follow it (sorting), otherwise the formulas were already remapped.</summary>
    private static void Renumber(Row row, int number, bool sameRowFormulas)
    {
        var old = RowNumber(row);
        row.RowIndex = (uint)number;
        foreach (var cell in row.Elements<Cell>())
        {
            var letters = new string((cell.CellReference?.Value ?? "").TakeWhile(char.IsLetter).ToArray());
            cell.CellReference = letters + number.ToString(CultureInfo.InvariantCulture);
            if (sameRowFormulas && cell.CellFormula is { } formula)
                formula.Text = Regex.Replace(formula.Text ?? "", @"(\$?[A-Z]{1,3}\$?)" + old.ToString(CultureInfo.InvariantCulture) + @"(?!\d)", "${1}" + number.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static Cell GetOrCreateCell(WorksheetPart part, string reference)
    {
        var data = part.Worksheet!.GetFirstChild<SheetData>()!;
        var number = uint.Parse(new string(reference.SkipWhile(char.IsLetter).ToArray()), CultureInfo.InvariantCulture);
        var row = data.Elements<Row>().FirstOrDefault(item => item.RowIndex?.Value == number);
        if (row is null)
        {
            row = new Row { RowIndex = number };
            var after = data.Elements<Row>().LastOrDefault(item => (item.RowIndex?.Value ?? 0) < number);
            if (after is null) data.PrependChild(row); else data.InsertAfter(row, after);
        }
        var existing = row.Elements<Cell>().FirstOrDefault(cell => string.Equals(cell.CellReference?.Value, reference, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;
        var created = new Cell { CellReference = reference };
        var column = OfficeDocumentReader.ColumnIndex(reference);
        var before = row.Elements<Cell>().FirstOrDefault(cell => OfficeDocumentReader.ColumnIndex(cell.CellReference?.Value) > column);
        if (before is null) row.Append(created); else row.InsertBefore(created, before);
        return created;
    }

    private static Stylesheet Stylesheet(WorkbookPart workbook)
    {
        var part = workbook.WorkbookStylesPart ?? workbook.AddNewPart<WorkbookStylesPart>();
        part.Stylesheet ??= new Stylesheet(
            new Fonts(new Font(new FontSize { Val = 11 })) { Count = 1 },
            new Fills(new Fill(new PatternFill { PatternType = PatternValues.None }), new Fill(new PatternFill { PatternType = PatternValues.Gray125 })) { Count = 2 },
            new Borders(new Border(new LeftBorder(), new RightBorder(), new TopBorder(), new BottomBorder(), new DiagonalBorder())) { Count = 1 },
            new CellStyleFormats(new CellFormat { NumberFormatId = 0, FontId = 0, FillId = 0, BorderId = 0 }) { Count = 1 },
            new CellFormats(new CellFormat { NumberFormatId = 0, FontId = 0, FillId = 0, BorderId = 0, FormatId = 0 }) { Count = 1 });
        return part.Stylesheet;
    }

    private static readonly Type[] StyleOrder = [typeof(NumberingFormats), typeof(Fonts), typeof(Fills), typeof(Borders), typeof(CellStyleFormats), typeof(CellFormats), typeof(CellStyles), typeof(DifferentialFormats), typeof(TableStyles), typeof(Colors), typeof(StylesheetExtensionList)];

    private static T InsertStyle<T>(Stylesheet stylesheet, T element) where T : OpenXmlElement
    {
        var position = Array.IndexOf(StyleOrder, typeof(T));
        var after = stylesheet.ChildElements.LastOrDefault(child => Array.IndexOf(StyleOrder, child.GetType()) is var index && index >= 0 && index < position);
        if (after is null) stylesheet.PrependChild(element); else stylesheet.InsertAfter(element, after);
        return element;
    }

    private static uint NumberFormatId(Stylesheet stylesheet, string code)
    {
        var builtIn = code switch { "General" => 0u, "0" => 1u, "0.00" => 2u, "#,##0" => 3u, "#,##0.00" => 4u, "0%" => 9u, "0.00%" => 10u, "@" => 49u, _ => uint.MaxValue };
        if (builtIn != uint.MaxValue) return builtIn;
        var formats = stylesheet.NumberingFormats ??= InsertStyle(stylesheet, new NumberingFormats { Count = 0 });
        if (formats.Elements<NumberingFormat>().FirstOrDefault(item => item.FormatCode?.Value == code) is { } existing) return existing.NumberFormatId!.Value;
        var id = Math.Max(164u, formats.Elements<NumberingFormat>().Select(item => item.NumberFormatId?.Value ?? 0u).DefaultIfEmpty(163u).Max() + 1);
        formats.Append(new NumberingFormat { NumberFormatId = id, FormatCode = code });
        formats.Count = (uint)formats.Elements<NumberingFormat>().Count();
        return id;
    }

    /// <summary>Gives the cells of a range a copy of their own format with one part changed (everything else kept).</summary>
    private static void Restyle(WorkbookPart workbook, SheetTable table, (int Left, int Top, int Right, int Bottom) range, Action<CellFormat> change, Action<CellFormat> apply, bool createMissing = false)
    {
        var stylesheet = Stylesheet(workbook);
        var formats = stylesheet.CellFormats!;
        var cache = new Dictionary<uint, uint>();
        var cells = new List<Cell>();
        if (createMissing)
            for (var row = range.Top; row <= range.Bottom; row++)
                for (var column = range.Left; column <= range.Right; column++)
                    cells.Add(GetOrCreateCell(table.Part, Letter(column) + row.ToString(CultureInfo.InvariantCulture)));
        else
            cells.AddRange(table.Part.Worksheet!.Descendants<Cell>().Where(cell =>
            {
                var reference = cell.CellReference?.Value ?? "";
                var column = OfficeDocumentReader.ColumnIndex(reference);
                var row = int.TryParse(new string(reference.SkipWhile(char.IsLetter).ToArray()), out var number) ? number : 0;
                return column >= range.Left && column <= range.Right && row >= range.Top && row <= range.Bottom;
            }));
        foreach (var cell in cells)
        {
            var current = cell.StyleIndex?.Value ?? 0u;
            if (!cache.TryGetValue(current, out var next))
            {
                var copy = (CellFormat)(formats.Elements<CellFormat>().ElementAtOrDefault((int)current) ?? formats.Elements<CellFormat>().First()).CloneNode(true);
                change(copy); apply(copy);
                formats.Append(copy);
                formats.Count = (uint)formats.Elements<CellFormat>().Count();
                cache[current] = next = formats.Count.Value - 1;
            }
            cell.StyleIndex = next;
        }
    }

    private static readonly Type[] SheetOrder = [typeof(SheetProperties), typeof(SheetDimension), typeof(SheetViews), typeof(SheetFormatProperties), typeof(Columns), typeof(SheetData), typeof(SheetCalculationProperties), typeof(SheetProtection), typeof(ProtectedRanges), typeof(Scenarios), typeof(AutoFilter), typeof(SortState), typeof(DataConsolidate), typeof(CustomSheetViews), typeof(MergeCells), typeof(PhoneticProperties), typeof(ConditionalFormatting), typeof(DataValidations), typeof(Hyperlinks), typeof(PrintOptions), typeof(PageMargins), typeof(PageSetup), typeof(HeaderFooter)];

    /// <summary>Puts an element where the schema wants it in a worksheet (Excel refuses files with the wrong order).</summary>
    private static T InsertInOrder<T>(Worksheet worksheet, T element) where T : OpenXmlElement
    {
        var position = Array.IndexOf(SheetOrder, typeof(T));
        var after = worksheet.ChildElements.LastOrDefault(child => Array.IndexOf(SheetOrder, child.GetType()) is var index && index >= 0 && index <= position);
        if (after is null) worksheet.PrependChild(element); else worksheet.InsertAfter(element, after);
        return element;
    }

    /// <summary>The last data row of a sheet in a file (before a total row with a formula over other rows).</summary>
    public static int? DataLastRow(string path, string? sheet)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var document = SpreadsheetDocument.Open(stream, false);
        return Table(document.WorkbookPart!, sheet) is { } table && DataRows(table) is { Count: > 0 } rows ? RowNumber(rows[^1]) : null;
    }

    // ---------- CSV ----------

    public static string Csv(string path, string? sheet)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var document = SpreadsheetDocument.Open(stream, false);
        var workbook = document.WorkbookPart!;
        var table = Table(workbook, sheet) ?? throw new InvalidOperationException("シートが見つかりません。");
        var shared = workbook.SharedStringTablePart?.SharedStringTable;
        var text = new StringBuilder();
        foreach (var row in table.Rows.OrderBy(RowNumber))
            text.Append(string.Join(",", Values(row, table.Columns, shared).Select(value => Regex.IsMatch(value, "[\",\r\n]") ? "\"" + value.Replace("\"", "\"\"") + "\"" : value))).Append("\r\n");
        return text.ToString();
    }

    // ---------- read-back ----------

    /// <summary>The saved file compared with what the original says it should become.</summary>
    public static IEnumerable<string> Verify(string sourcePath, string outputPath, OfficeEdit edit)
    {
        if (edit.Op == "csv")
        {
            var expected = Csv(sourcePath, edit.Sheet);
            if (!File.Exists(outputPath) || File.ReadAllText(outputPath, Encoding.UTF8).TrimStart('﻿') != expected) yield return "CSV の内容が元のシートと合いません";
            yield break;
        }
        using var beforeStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var before = SpreadsheetDocument.Open(beforeStream, false);
        using var after = SpreadsheetDocument.Open(outputPath, false);
        var oldBook = before.WorkbookPart!;
        var newBook = after.WorkbookPart!;
        var oldShared = oldBook.SharedStringTablePart?.SharedStringTable;
        var newShared = newBook.SharedStringTablePart?.SharedStringTable;
        var sheetName = edit.Op == "rename-sheet" ? edit.Name!.Trim() : edit.Sheet;
        var oldTable = Table(oldBook, edit.Sheet);
        var newTable = edit.Op == "delete-sheet" ? null : Table(newBook, sheetName);
        if (edit.Op != "delete-sheet" && newTable is null) { yield return $"シート「{sheetName}」が見つかりません"; yield break; }
        string[][] Rows(SheetTable table, SharedStringTable? shared) => table.Rows.OrderBy(RowNumber).Select(row => Values(row, Math.Max(table.Columns, oldTable!.Columns), shared)).ToArray();
        switch (edit.Op)
        {
            case "sort":
            {
                var column = ColumnOf(oldBook, edit.Sheet, edit.Column)!.Value;
                var oldRows = Rows(oldTable!, oldShared);
                var newRows = Rows(newTable!, newShared);
                var dataCount = DataRows(oldTable!).Count;
                var keys = newRows.Skip(1).Take(dataCount).Select(row => row[column]).ToArray();
                var numeric = keys.All(key => key.Length == 0 || double.TryParse(key, NumberStyles.Float, CultureInfo.InvariantCulture, out _));
                var comparer = StringComparer.Create(new CultureInfo("ja-JP"), false);
                var ordered = keys.Zip(keys.Skip(1)).All(pair => Compare(pair.First, pair.Second) * (Descending(edit.Order) ? -1 : 1) <= 0);
                int Compare(string a, string b) => numeric ? (a.Length == 0 ? double.MaxValue : double.Parse(a, CultureInfo.InvariantCulture)).CompareTo(b.Length == 0 ? double.MaxValue : double.Parse(b, CultureInfo.InvariantCulture)) : comparer.Compare(a, b);
                if (!ordered) yield return "並べ替えた順になっていません";
                if (!oldRows[0].SequenceEqual(newRows[0]) || !oldRows.Skip(1).Select(row => string.Join("\u001f", row)).Order().SequenceEqual(newRows.Skip(1).Select(row => string.Join("\u001f", row)).Order()))
                    yield return "並べ替えの前後で行の中身が変わっています";
                break;
            }
            case "dedupe-rows":
            {
                var removedNumbers = DuplicateRows(oldTable!, oldShared).Select(RowNumber).ToHashSet();
                var expected = oldTable!.Rows.OrderBy(RowNumber).Where(row => !removedNumbers.Contains(RowNumber(row))).Select(row => string.Join("\u001f", Values(row, oldTable.Columns, oldShared, skipFormulas: true))).ToArray();
                if (!expected.SequenceEqual(newTable!.Rows.OrderBy(RowNumber).Select(row => string.Join("\u001f", Values(row, oldTable.Columns, newShared, skipFormulas: true))))) yield return "重複を除いた結果が予定と違います";
                foreach (var problem in FormulasFollow(oldTable, newTable!, DeletionMap(removedNumbers))) yield return problem;
                break;
            }
            case "delete-rows" or "insert-rows":
            {
                var (first, last) = RowRange(edit.Rows)!.Value;
                var count = edit.Op == "insert-rows" ? Math.Clamp(edit.Count ?? 1, 1, 1000) : 0;
                var oldByNumber = oldTable!.Rows.ToDictionary(RowNumber, row => string.Join("\u001f", Values(row, oldTable.Columns, oldShared, skipFormulas: true)));
                var expected = edit.Op == "delete-rows"
                    ? oldByNumber.Where(item => item.Key < first || item.Key > last).ToDictionary(item => item.Key > last ? item.Key - (last - first + 1) : item.Key, item => item.Value)
                    : oldByNumber.ToDictionary(item => item.Key >= first ? item.Key + count : item.Key, item => item.Value);
                var actual = newTable!.Rows.ToDictionary(RowNumber, row => string.Join("\u001f", Values(row, oldTable.Columns, newShared, skipFormulas: true)));
                if (expected.Count != actual.Count || expected.Any(item => !actual.TryGetValue(item.Key, out var value) || value != item.Value)) yield return "行の削除・挿入の結果が予定と違います";
                Func<int, int?> rowMap = edit.Op == "delete-rows" ? DeletionMap(Enumerable.Range(first, last - first + 1).ToHashSet()) : row => row >= first ? row + count : row;
                foreach (var problem in FormulasFollow(oldTable, newTable, rowMap)) yield return problem;
                break;
            }
            case "formula":
            {
                var cell = newTable!.Part.Worksheet!.Descendants<Cell>().FirstOrDefault(item => string.Equals(item.CellReference?.Value, edit.Cell, StringComparison.OrdinalIgnoreCase));
                if (cell?.CellFormula?.Text != edit.Formula!.Trim().TrimStart('=')) yield return $"{edit.Cell} に式が入っていません";
                break;
            }
            case "number-format":
            {
                var code = FormatCode(edit.Format)!;
                var styles = newBook.WorkbookStylesPart?.Stylesheet;
                var wrong = Cells(newTable!, Range(edit.Range)!.Value).Count(cell =>
                {
                    var format = styles?.CellFormats?.Elements<CellFormat>().ElementAtOrDefault((int)(cell.StyleIndex?.Value ?? 0u));
                    var id = format?.NumberFormatId?.Value ?? 0u;
                    return id != NumberFormatId(new Stylesheet(), code) && styles?.NumberingFormats?.Elements<NumberingFormat>().FirstOrDefault(item => item.NumberFormatId?.Value == id)?.FormatCode?.Value != code;
                });
                if (wrong > 0) yield return $"{wrong}セルの表示形式が予定どおりになっていません";
                break;
            }
            case "column-width":
            {
                var (first, last) = ColumnSpan(edit.Column)!.Value;
                var columns = newTable!.Part.Worksheet!.GetFirstChild<Columns>()?.Elements<Column>().ToArray() ?? [];
                for (var column = first; column <= last; column++)
                    if (!columns.Any(item => item.Min!.Value - 1 <= column && item.Max!.Value - 1 >= column && Math.Abs(item.Width!.Value - edit.Width!.Value) < 0.01))
                    { yield return $"{Letter(column)}列の幅が予定どおりになっていません"; break; }
                break;
            }
            case "freeze":
                if (newTable!.Part.Worksheet!.Descendants<Pane>().FirstOrDefault() is not { } pane || pane.State?.Value != PaneStateValues.Frozen || pane.TopLeftCell?.Value != edit.Cell!.ToUpperInvariant())
                    yield return "ウィンドウ枠の固定が見つかりません";
                break;
            case "borders":
            {
                var styles = newBook.WorkbookStylesPart?.Stylesheet;
                var range = Range(edit.Range)!.Value;
                var wrong = Cells(newTable!, range).Count(cell =>
                {
                    var format = styles?.CellFormats?.Elements<CellFormat>().ElementAtOrDefault((int)(cell.StyleIndex?.Value ?? 0u));
                    var border = styles?.Borders?.Elements<Border>().ElementAtOrDefault((int)(format?.BorderId?.Value ?? 0u));
                    return border?.LeftBorder?.Style is null || border.RightBorder?.Style is null || border.TopBorder?.Style is null || border.BottomBorder?.Style is null;
                });
                var expectedCells = (range.Right - range.Left + 1) * (range.Bottom - range.Top + 1);
                if (wrong > 0 || Cells(newTable!, range).Count() < expectedCells) yield return "罫線が予定どおりに引かれていません";
                break;
            }
            case "conditional-format":
                if (!newTable!.Part.Worksheet!.Elements<ConditionalFormatting>().Any(item => item.SequenceOfReferences?.InnerText == edit.Range!.ToUpperInvariant() && item.Elements<ConditionalFormattingRule>().Any(rule => rule.Operator?.Value == ConditionalOperator(edit.Operator))))
                    yield return "条件付き書式が見つかりません";
                break;
            case "autofilter":
                if (newTable!.Part.Worksheet!.GetFirstChild<AutoFilter>() is null) yield return "フィルターが見つかりません";
                break;
            case "rename-sheet":
                if (Table(newBook, edit.Sheet) is not null && !string.Equals(edit.Sheet, edit.Name, StringComparison.OrdinalIgnoreCase)) yield return $"元の名前「{edit.Sheet}」のシートが残っています";
                break;
            case "delete-sheet":
                if (OfficeEditor.FindSheet(newBook, edit.Sheet) is not null && !string.IsNullOrWhiteSpace(edit.Sheet)) yield return $"シート「{edit.Sheet}」が残っています";
                break;
        }
    }

    /// <summary>Every formula of the sheet is where its row moved to, with its references moved the same way.</summary>
    private static IEnumerable<string> FormulasFollow(SheetTable before, SheetTable after, Func<int, int?> map)
    {
        foreach (var row in before.Rows)
            foreach (var cell in row.Elements<Cell>().Where(cell => cell.CellFormula is not null))
            {
                if (map(RowNumber(row)) is not { } moved) continue;
                var letters = new string((cell.CellReference?.Value ?? "").TakeWhile(char.IsLetter).ToArray());
                var target = after.Part.Worksheet!.Descendants<Cell>().FirstOrDefault(item => item.CellReference?.Value == letters + moved.ToString(CultureInfo.InvariantCulture));
                var text = Reference.Replace(cell.CellFormula!.Text ?? "", match =>
                {
                    var first = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                    if (!match.Groups[3].Success) return match.Groups[1].Value + (map(first) ?? first).ToString(CultureInfo.InvariantCulture);
                    var last = int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture);
                    int? start = null, end = null;
                    for (var number = first; number <= last && start is null; number++) start = map(number);
                    for (var number = last; number >= first && end is null; number--) end = map(number);
                    return match.Groups[1].Value + (start ?? first).ToString(CultureInfo.InvariantCulture) + ":" + match.Groups[3].Value + (end ?? last).ToString(CultureInfo.InvariantCulture);
                });
                if (target?.CellFormula?.Text != text) yield return $"式 {cell.CellReference} が予定（{letters}{moved}: ={text}）になっていません";
            }
    }

    private static IEnumerable<Cell> Cells(SheetTable table, (int Left, int Top, int Right, int Bottom) range) =>
        table.Part.Worksheet!.Descendants<Cell>().Where(cell =>
        {
            var reference = cell.CellReference?.Value ?? "";
            var column = OfficeDocumentReader.ColumnIndex(reference);
            var row = int.TryParse(new string(reference.SkipWhile(char.IsLetter).ToArray()), out var number) ? number : 0;
            return column >= range.Left && column <= range.Right && row >= range.Top && row <= range.Bottom;
        });
}
