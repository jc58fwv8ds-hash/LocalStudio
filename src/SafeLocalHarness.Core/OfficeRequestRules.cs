using System.Globalization;
using System.Text.RegularExpressions;

namespace SafeLocalHarness.Core;

/// <summary>
/// Common Excel requests with a fixed meaning, planned without the AI (as common PDF and rename requests are):
/// "「テスト追加」というシートを追加して" and "「△区」を含む行を黄色で塗りつぶして太字にして". Anything else goes to the AI,
/// whose plan is checked against the request's own words and colours.
/// </summary>
public static class OfficeRequestRules
{
    private const string ColourWords = "黄色|黄|赤色|赤|青色|青|緑色|緑|オレンジ|橙|灰色|グレー|水色|ピンク|黒色|黒|白色|白";

    public static IReadOnlyList<OfficeEdit>? TryRules(string request, OfficeSummary summary, string? path = null)
    {
        if (summary.Kind == "powerpoint") return TryPowerPointOperation(request.Normalize(System.Text.NormalizationForm.FormKC), summary.Slides?.Count ?? 0) is { } slideOperation ? [slideOperation] : null;
        if (summary.Kind == "word") return TryWordOperation(request.Normalize(System.Text.NormalizationForm.FormKC)) is { } wordOperation ? [wordOperation] : null;
        if (summary.Kind != "excel") return null;
        var text = request.Normalize(System.Text.NormalizationForm.FormKC);
        if (TryOperation(text, summary, path) is { } operation) return [operation];
        var quotes = Regex.Matches(text, "「([^」]{1,100})」").Select(match => match.Groups[1].Value).ToArray();
        var sheet = Regex.Match(text, "^「([^」]{1,31})」(?:という|の)?(?:名前の)?(?:新しい)?シートを(?:追加|作|足|増や)");
        if (sheet.Success && quotes.Length == 1) return [new OfficeEdit("add-sheet", Name: sheet.Groups[1].Value)];
        // "〇〇市△区に居住する者の関係する部分を黄色で…": without 「」, the rows are those holding the longest part of the
        // request found in the workbook, when every clause is about that formatting (hands-on 2026-10-13, H5).
        if (quotes.Length == 0 && path is not null && Regex.IsMatch(text, "塗|太字|太文字|ボールド|文字色|色を|色に|赤く|青く") &&
            Regex.Split(text, "[、。,]").Where(clause => clause.Trim().Length > 0).All(clause => Regex.IsMatch(clause, $"塗|太字|太文字|ボールド|太く|色|{ColourWords}")) &&
            ExcelFormatting.LongestCellMatch(text, ExcelFormatting.AllRows(path)) is { } found)
            quotes = [found];
        else if (quotes.Length != 1 || !Regex.IsMatch(text, "行|部分|セル|含む|ある")) return null;
        var fill = Regex.Match(text, $"({ColourWords})(?:色)?(?:で|に)(?:塗りつぶ|塗って|塗る|背景)");
        var colour = Regex.Match(text, $"(?:文字色|文字の色|字の色)(?:も|を)?\\s*({ColourWords})|({ColourWords})(?:色)?(?:く|い|の)?(?:文字|字)|({ColourWords})く(?:して|変更|する)|({ColourWords})(?:色)?く[^、。]{{0,4}}(?:文字色|字の色|色を|色に|色も)");
        var bold = Regex.IsMatch(text, "太字|太文字|ボールド|太く");
        if (Regex.IsMatch(text, "塗") && !fill.Success) return null;
        if (!fill.Success && !colour.Success && !bold) return null;
        // Anything else asked in the same request (another change) is left to the AI and its checks.
        var rest = Regex.Replace(text, "「[^」]*」|を含む|が含まれる|がある|の(?:ある)?|行|部分|セル|全部|すべて|全て|を|は|に|で|と|も|、|。|して|させて|ください|欲しい|ほしい|変更|変えて|塗りつぶし|塗りつぶして|塗って|太字|太文字|ボールド|太く|文字色|文字の色|字の色|文字|字|色|く|い|" + ColourWords, "");
        if (Regex.IsMatch(request, "「") && rest.Trim().Length > 6) return null;
        string? Name(Match match) => match.Success ? match.Groups.Cast<Group>().Skip(1).First(group => group.Success).Value : null;
        return [new OfficeEdit("format-rows", Contains: quotes[0], Fill: Name(fill), Bold: bold ? true : null, Color: Name(colour))];
    }

    /// <summary>The column the words name: a header of the first sheet (the longest found) or a letter ("B列").</summary>
    private static (int Index, string Letter)? NamedColumn(string text, SheetSummary sheet)
    {
        var letter = Regex.Match(text, "(?<![A-Za-z])([A-Za-z]{1,2})列");
        if (letter.Success) { var index = OfficeDocumentReader.ColumnIndex(letter.Groups[1].Value.ToUpperInvariant() + "1"); return (index, OfficeDocumentReader.ColumnName(index)); }
        var header = sheet.Headers.Select((name, index) => (name, index)).Where(item => item.name.Length > 0 && text.Contains(item.name, StringComparison.Ordinal)).OrderByDescending(item => item.name.Length).FirstOrDefault();
        return header.name is null ? null : (header.index, OfficeDocumentReader.ColumnName(header.index));
    }

    /// <summary>
    /// Phase A operations by their common wording: sorting, duplicates, rows, frozen panes, widths, number formats, totals,
    /// borders, filters, conditional colours, sheet names and CSV. Null when the words are not one of these.
    /// </summary>
    public static OfficeEdit? TryOperation(string text, OfficeSummary summary, string? path = null)
    {
        if (summary.Sheets.Count == 0) return null;
        var sheet = summary.Sheets[0];
        var quotes = Regex.Matches(text, "「([^」]{1,100})」").Select(match => match.Groups[1].Value).ToArray();
        var tableLastRow = Math.Max(2, sheet.Rows);
        // Formats, colours and totals cover the data, not a total row under it.
        var lastRow = (path is null ? null : ExcelOperations.DataLastRow(path, sheet.Name)) ?? tableLastRow;
        var lastColumn = OfficeDocumentReader.ColumnName(Math.Max(0, sheet.Columns - 1));
        if (Regex.IsMatch(text, "(?i)csv"))
            return Regex.IsMatch(text, "書き出|保存|出力|変換|にして") ? new OfficeEdit("csv", Sheet: sheet.Name) : null;
        if (Regex.IsMatch(text, "重複") && Regex.IsMatch(text, "削除|消|除|取り除|なくし")) return new OfficeEdit("dedupe-rows", Sheet: sheet.Name);
        var rename = Regex.Match(text, "(?:「([^」]{1,31})」(?:シート|という)?の?)?(?:シート)?の?名前を「([^」]{1,31})」に");
        if (rename.Success) return new OfficeEdit("rename-sheet", Sheet: rename.Groups[1].Success ? rename.Groups[1].Value : sheet.Name, Name: rename.Groups[2].Value);
        var deleteSheet = Regex.Match(text, "「([^」]{1,31})」(?:という)?シートを(?:削除|消)");
        if (deleteSheet.Success) return new OfficeEdit("delete-sheet", Sheet: deleteSheet.Groups[1].Value);
        var deleteRows = Regex.Match(text, @"(\d+)行目(?:\s*(?:から|~|〜|-)\s*(\d+)行目?)?(?:まで)?を?(?:削除|消)");
        if (deleteRows.Success) return new OfficeEdit("delete-rows", Sheet: sheet.Name, Rows: deleteRows.Groups[2].Success ? $"{deleteRows.Groups[1].Value}-{deleteRows.Groups[2].Value}" : deleteRows.Groups[1].Value);
        var insertRows = Regex.Match(text, @"(\d+)行目(?:の上)?に(?:空(?:の|白の)?行を?)?(?:(\d+)行)?(?:空(?:の|白の)?行を?)?(?:挿入|追加|入れ)");
        if (insertRows.Success) return new OfficeEdit("insert-rows", Sheet: sheet.Name, Rows: insertRows.Groups[1].Value, Count: insertRows.Groups[2].Success ? int.Parse(insertRows.Groups[2].Value, CultureInfo.InvariantCulture) : 1);
        if (Regex.IsMatch(text, "固定") && Regex.IsMatch(text, "見出し|1行目|先頭行|一番上|ウィンドウ枠|A列|1列目|先頭列|左端"))
        {
            var rowsFixed = Regex.IsMatch(text, "見出し|1行目|先頭行|一番上") || !Regex.IsMatch(text, "A列|1列目|先頭列|左端");
            var columnsFixed = Regex.IsMatch(text, "A列|1列目|先頭列|左端");
            return new OfficeEdit("freeze", Sheet: sheet.Name, Cell: (columnsFixed ? "B" : "A") + (rowsFixed ? "2" : "1"));
        }
        if (Regex.IsMatch(text, "フィルター|フィルタ") && !Regex.IsMatch(text, "解除|外")) return new OfficeEdit("autofilter", Sheet: sheet.Name);
        if (Regex.IsMatch(text, "罫線"))
            return new OfficeEdit("borders", Sheet: sheet.Name, Range: $"A1:{lastColumn}{tableLastRow}", Format: Regex.IsMatch(text, "太い|太く") ? "medium" : "thin");
        var width = Regex.Match(text, @"幅を\s*(\d+(?:\.\d+)?)");
        if (width.Success && NamedColumn(text, sheet) is { } widthColumn)
            return new OfficeEdit("column-width", Sheet: sheet.Name, Column: widthColumn.Letter, Width: double.Parse(width.Groups[1].Value, CultureInfo.InvariantCulture));
        var sort = Regex.Match(text, "(昇順|降順|大きい順|小さい順|高い順|低い順|多い順|少ない順|新しい順|古い順|五十音順|あいうえお順|順番)に?(?:並べ(?:替|か)え|並び替え|ソート)");
        if (sort.Success && NamedColumn(text, sheet) is { } sortColumn)
            return new OfficeEdit("sort", Sheet: sheet.Name, Column: sortColumn.Letter, Order: Regex.IsMatch(sort.Groups[1].Value, "降順|大きい|高い|多い|新しい") ? "desc" : "asc");
        var format = Regex.Match(text, "(桁区切り|カンマ|パーセント|%|円|通貨|日付|和暦|小数1桁|小数2桁)");
        if (format.Success && Regex.IsMatch(text, "表示|形式|にして|に変え|で表示|付け") && NamedColumn(text, sheet) is { } formatColumn)
            return new OfficeEdit("number-format", Sheet: sheet.Name, Range: $"{formatColumn.Letter}2:{formatColumn.Letter}{lastRow}", Format: format.Groups[1].Value);
        var total = Regex.Match(text, "(合計|平均|件数|個数|最大|最小)を?(?:一番下|最後|下|末尾)?(?:の行)?に?(?:入れ|出|追加|計算|表示|求め)");
        if (total.Success && NamedColumn(text, sheet) is { } totalColumn)
        {
            var function = total.Groups[1].Value switch { "平均" => "AVERAGE", "件数" or "個数" => "COUNTA", "最大" => "MAX", "最小" => "MIN", _ => "SUM" };
            return new OfficeEdit("formula", Sheet: sheet.Name, Cell: $"{totalColumn.Letter}{tableLastRow + 1}", Formula: $"{function}({totalColumn.Letter}2:{totalColumn.Letter}{lastRow})");
        }
        var condition = Regex.Match(text, @"(-?\d+(?:\.\d+)?)\s*(以上|以下|より大きい|より小さい|未満|を超える|と等しい)");
        var conditionColour = Regex.Match(text, $"({ColourWords})(?:色)?(?:に|で)(?:塗|して|表示|する|色付け)|({ColourWords})(?:色)?の(?:文字|字)");
        if (condition.Success && conditionColour.Success && NamedColumn(text, sheet) is { } conditionColumn)
        {
            var op = condition.Groups[2].Value switch { "以上" => "以上", "以下" => "以下", "より大きい" or "を超える" => "より大きい", "と等しい" => "等しい", _ => "未満" };
            var textColour = conditionColour.Groups[2].Success;
            var colourName = textColour ? conditionColour.Groups[2].Value : conditionColour.Groups[1].Value;
            return new OfficeEdit("conditional-format", Sheet: sheet.Name, Range: $"{conditionColumn.Letter}2:{conditionColumn.Letter}{lastRow}", Operator: op, Value: condition.Groups[1].Value,
                Fill: textColour ? null : colourName, Color: textColour ? colourName : null);
        }
        _ = quotes;
        return null;
    }

    /// <summary>
    /// Word phase B by common wording: the look of quoted words, headings, paragraphs, table cells and rows, header and
    /// footer, page numbers, comments and tracked changes. Null when the words are not one of these.
    /// </summary>
    public static OfficeEdit? TryWordOperation(string text)
    {
        var quotes = Regex.Matches(text, "「([^」]{1,2000})」").Select(match => match.Groups[1].Value).ToArray();
        var outside = Regex.Replace(text, "「[^」]*」", "「」");
        if (Regex.IsMatch(outside, "変更履歴"))
            return Regex.IsMatch(outside, "元に戻|却下|破棄|取り消|拒否") ? new OfficeEdit("reject-changes") : Regex.IsMatch(outside, "承諾|反映|確定|受け入れ|承認") ? new OfficeEdit("accept-changes") : null;
        string? Side() => Regex.IsMatch(outside, "左") ? "left" : Regex.IsMatch(outside, "右") ? "right" : null;
        if (Regex.IsMatch(outside, "ページ番号") && Regex.IsMatch(outside, "付け|入れ|追加|表示")) return new OfficeEdit("page-numbers", Format: PdfTextLook.NumberFormat(outside) ?? "{n}", Position: Side());
        if (quotes.Length == 1 && Regex.IsMatch(outside, "ヘッダー")) return new OfficeEdit("header-text", Text: quotes[0], Position: Side());
        if (quotes.Length == 1 && Regex.IsMatch(outside, "フッター")) return new OfficeEdit("footer-text", Text: quotes[0], Position: Side());
        if (quotes.Length == 2 && Regex.IsMatch(outside, "コメント")) return new OfficeEdit("comment", Find: quotes[0], Text: quotes[1]);
        var table = Regex.Match(outside, @"表(\d)?の?\s*(\d+)行目");
        if (table.Success)
        {
            var tableNumber = table.Groups[1].Success ? int.Parse(table.Groups[1].Value, CultureInfo.InvariantCulture) : 1;
            var cell = Regex.Match(outside, @"(\d+)行目の?\s*(\d+)列目");
            if (cell.Success && quotes.Length == 1) return new OfficeEdit("table-set-cell", Table: tableNumber, Rows: cell.Groups[1].Value, Column: cell.Groups[2].Value, Value: quotes[0]);
            if (Regex.IsMatch(outside, "削除|消")) return new OfficeEdit("table-delete-row", Table: tableNumber, Rows: table.Groups[2].Value);
        }
        var addRow = Regex.Match(outside, @"表(\d)?に");
        if (addRow.Success && quotes.Length == 1 && Regex.IsMatch(outside, "行") && Regex.IsMatch(outside, "追加|足し|加え"))
            return new OfficeEdit("table-add-row", Table: addRow.Groups[1].Success ? int.Parse(addRow.Groups[1].Value, CultureInfo.InvariantCulture) : 1, Values: Regex.Split(quotes[0], "[、,，]").Select(value => value.Trim()).ToArray());
        var heading = Regex.Match(outside, @"見出し\s*(\d)?");
        if (quotes.Length == 1 && heading.Success && Regex.IsMatch(outside, "にして|に変え|にする|として"))
            return new OfficeEdit("heading", Find: quotes[0], Level: heading.Groups[1].Success ? int.Parse(heading.Groups[1].Value, CultureInfo.InvariantCulture) : 1);
        if (quotes.Length == 2 && Regex.IsMatch(outside, "追加|入れ|挿入|足し"))
            return new OfficeEdit("add-paragraph", Find: quotes[0], Text: quotes[1], Position: Regex.IsMatch(outside, "」の(?:前|上)") ? "before" : "after");
        if (quotes.Length == 1 && Regex.IsMatch(outside, "^(?:文書の)?(?:最後|末尾|一番下|最初|先頭|一番上)に") && Regex.IsMatch(outside, "追加|入れ|挿入|足し"))
            return new OfficeEdit("add-paragraph", Text: quotes[0], Position: Regex.IsMatch(outside, "最初|先頭|一番上") ? "start" : "end");
        if (quotes.Length == 1 && Regex.IsMatch(outside, "段落") && Regex.IsMatch(outside, "削除|消"))
            return new OfficeEdit("delete-paragraph", Find: quotes[0]);
        if (quotes.Length == 1)
        {
            var bold = Regex.IsMatch(outside, "太字|太く|ボールド") ? true : (bool?)null;
            var italic = Regex.IsMatch(outside, "斜体|イタリック") ? true : (bool?)null;
            var underline = Regex.IsMatch(outside, "下線|アンダーライン") ? true : (bool?)null;
            var highlight = Regex.Match(outside, $"({ColourWords})?(?:色)?(?:の)?(?:蛍光ペン|マーカー|ハイライト)");
            var colour = Regex.Match(outside, $"({ColourWords})(?:色)?(?:の文字|い文字|字|く|文字)|文字(?:の)?色を?({ColourWords})");
            var size = Regex.Match(outside, @"(\d+(?:\.\d+)?)\s*(?:pt|ポイント)");
            var font = Regex.IsMatch(outside, "明朝") ? "mincho" : Regex.IsMatch(outside, "ゴシック") ? "gothic" : Regex.IsMatch(outside, "UD|教科書体") ? "ud" : null;
            // A colour named but not as a highlighter is the text colour ("太字の赤にして"); a word is never silently dropped.
            var anyColour = Regex.Match(Regex.Replace(outside, $"({ColourWords})?(?:色)?(?:の)?(?:蛍光ペン|マーカー|ハイライト)", ""), ColourWords);
            var textColour = colour.Success ? (colour.Groups[1].Success ? colour.Groups[1].Value : colour.Groups[2].Value) : anyColour.Success ? anyColour.Value : null;
            if (bold is null && italic is null && underline is null && !highlight.Success && textColour is null && !size.Success && font is null) return null;
            return new OfficeEdit("format-text", Find: quotes[0], Bold: bold, Italic: italic, Underline: underline,
                Highlight: highlight.Success ? (highlight.Groups[1].Success ? highlight.Groups[1].Value : "黄色") : null,
                Color: textColour,
                Size: size.Success ? double.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture) : null, Font: font);
        }
        return null;
    }

    /// <summary>
    /// PowerPoint (phase D) by common wording: "3枚目のスライドを削除して", "最後に「まとめ」というスライドを追加して",
    /// "3枚目を1枚目に移動して", "2枚目のノートに「…」と書いて", "「A」を「B」に置き換えて", and the look of quoted words.
    /// Null when the words are not one of these.
    /// </summary>
    public static OfficeEdit? TryPowerPointOperation(string text, int slides)
    {
        var quotes = Regex.Matches(text, "「([^」]{1,2000})」").Select(match => match.Groups[1].Value).ToArray();
        var outside = Regex.Replace(text, "「[^」]*」", "「」");
        // "3枚目", "3ページ目", "スライド3", "最後のスライド": the slide numbers named, in order.
        var named = Regex.Matches(outside, @"(\d+)\s*(?:枚目|ページ目|番目)|スライド\s*(\d+)|(最後|最終|末尾)(?:の)?(?:スライド|ページ)|(最初|先頭|1番最初)(?:の)?(?:スライド|ページ)")
            .Select(match => match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Success ? slides.ToString(CultureInfo.InvariantCulture) : "1").ToArray();
        var replace = Regex.Match(text, "^「([^」]{1,200})」を「([^」]{0,200})」に(?:置き換|置換|変え|変更|書き換|直)");
        if (replace.Success && quotes.Length == 2) return new OfficeEdit("replace", Find: replace.Groups[1].Value, With: replace.Groups[2].Value);
        if (Regex.IsMatch(outside, "ノート") && quotes.Length == 1 && named.Length == 1 && Regex.IsMatch(outside, "書|入れ|追加|して|に変え|記入|メモ"))
            return new OfficeEdit("notes", Rows: named[0], Text: quotes[0]);
        if (Regex.IsMatch(outside, "移動|動かし|持って|並べ替え|入れ替え"))
        {
            var target = Regex.Match(outside, @"を\s*(?:(\d+)\s*(?:枚目|ページ目|番目)|(最初|先頭|一番前|最後|末尾|一番後ろ))");
            var from = named.FirstOrDefault();
            if (from is null || !target.Success) return null;
            var to = target.Groups[1].Success ? target.Groups[1].Value : Regex.IsMatch(target.Groups[2].Value, "最初|先頭|一番前") ? "1" : slides.ToString(CultureInfo.InvariantCulture);
            return new OfficeEdit("move-slide", Rows: from, Position: to);
        }
        if (quotes.Length is 1 or 2 && Regex.IsMatch(outside, "スライド|ページ") && Regex.IsMatch(outside, "追加|足し|作|入れ|挿入|増や"))
        {
            var after = Regex.Match(outside, @"(\d+)\s*(?:枚目|ページ目|番目)の(後|あと|次|前|手前)");
            string? rows = after.Success
                ? (Regex.IsMatch(after.Groups[2].Value, "前|手前") ? (int.Parse(after.Groups[1].Value, CultureInfo.InvariantCulture) - 1).ToString(CultureInfo.InvariantCulture) : after.Groups[1].Value)
                : Regex.IsMatch(outside, "^(?:最初|先頭|一番前|1枚目)に") ? "0" : null;
            var body = quotes.Length == 2 ? Regex.Split(quotes[1], "[、,，\n]|/").Select(line => line.Trim()).Where(line => line.Length > 0).ToArray() : null;
            return new OfficeEdit("add-slide", Text: quotes[0], Values: body, Rows: rows);
        }
        if (Regex.IsMatch(outside, "削除|消して|消す|取り除|なくして") && named.Length == 1 && Regex.IsMatch(outside, "スライド|枚目|ページ"))
            return new OfficeEdit("delete-slide", Rows: named[0]);
        if (quotes.Length == 1 && TryWordOperation(text) is { Op: "format-text" } look)
            return look with { Highlight = null };
        return null;
    }

    /// <summary>Whether the request names this colour (by any of its names).</summary>
    public static bool NamesColour(string request, string colour)
    {
        var wanted = ExcelFormatting.Argb(colour);
        return wanted is not null && Regex.Matches(request, ColourWords).Any(match => ExcelFormatting.Argb(match.Value) == wanted);
    }
}
