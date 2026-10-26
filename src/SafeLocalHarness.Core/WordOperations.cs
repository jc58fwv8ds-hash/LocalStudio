using System.Globalization;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace SafeLocalHarness.Core;

/// <summary>
/// Word operations of phase B (user: "Office でできる操作は基本的にすべてチャットから"): the look of chosen words, headings,
/// paragraphs added or removed, table rows and cells, header and footer text, page numbers, comments, and accepting or
/// rejecting tracked changes. Each one says what it will change, changes the working copy, and is read back from the
/// saved file against what the original says it should become. Text in fields, tabs or pictures is left alone and the
/// request is refused rather than broken.
/// </summary>
public static class WordOperations
{
    public static readonly string[] All = ["format-text", "heading", "add-paragraph", "delete-paragraph", "table-set-cell", "table-add-row", "table-delete-row", "header-text", "footer-text", "page-numbers", "comment", "accept-changes", "reject-changes"];

    private static readonly Dictionary<string, W.HighlightColorValues> Highlights = new(StringComparer.Ordinal)
    {
        ["黄色"] = W.HighlightColorValues.Yellow, ["黄"] = W.HighlightColorValues.Yellow, ["yellow"] = W.HighlightColorValues.Yellow,
        ["緑"] = W.HighlightColorValues.Green, ["緑色"] = W.HighlightColorValues.Green, ["green"] = W.HighlightColorValues.Green,
        ["水色"] = W.HighlightColorValues.Cyan, ["cyan"] = W.HighlightColorValues.Cyan,
        ["ピンク"] = W.HighlightColorValues.Magenta, ["magenta"] = W.HighlightColorValues.Magenta,
        ["青"] = W.HighlightColorValues.Blue, ["青色"] = W.HighlightColorValues.Blue, ["赤"] = W.HighlightColorValues.Red, ["赤色"] = W.HighlightColorValues.Red,
        ["灰色"] = W.HighlightColorValues.LightGray, ["グレー"] = W.HighlightColorValues.LightGray
    };

    private static string FontOf(string? font) => font switch { "mincho" or "明朝" or "明朝体" => "游明朝", "ud" or "UD" or "教科書体" => "UD デジタル 教科書体 N-R", "gothic" or "ゴシック" or "ゴシック体" => "游ゴシック", { Length: > 0 } other => other, _ => "" };

    // ---------- text and runs ----------

    private static W.Body Body(WordprocessingDocument document) => document.MainDocumentPart!.Document!.Body!;

    /// <summary>The runs of a paragraph that carry plain text, in order (runs inside hyperlinks included).</summary>
    private static List<W.Run> TextRuns(W.Paragraph paragraph) =>
        paragraph.Descendants<W.Run>().Where(run => run.Ancestors<W.Paragraph>().First() == paragraph && run.GetFirstChild<W.Text>() is not null).ToList();

    private static string RunText(W.Run run) => string.Concat(run.Elements<W.Text>().Select(text => text.Text));

    /// <summary>A run that holds only its properties and one text can be split; others (tabs, fields, pictures) cannot.</summary>
    private static bool Simple(W.Run run) => run.ChildElements.All(child => child is W.RunProperties or W.Text) && run.Elements<W.Text>().Count() == 1;

    /// <summary>
    /// The runs that hold exactly each occurrence of the words in the paragraph, splitting runs at its ends (their look is
    /// kept on both halves). Null when an occurrence crosses a run that cannot be split.
    /// </summary>
    private static List<List<W.Run>>? Isolate(W.Paragraph paragraph, string find, bool apply)
    {
        var occurrences = new List<List<W.Run>>();
        var runs = TextRuns(paragraph);
        var text = string.Concat(runs.Select(RunText));
        var positions = new List<int>();
        for (var index = text.IndexOf(find, StringComparison.Ordinal); index >= 0; index = text.IndexOf(find, index + find.Length, StringComparison.Ordinal)) positions.Add(index);
        if (positions.Count == 0) return occurrences;
        foreach (var start in positions)
        {
            // Which runs it touches, and whether they can be split.
            var offset = 0;
            var touched = new List<W.Run>();
            foreach (var run in runs)
            {
                var length = RunText(run).Length;
                if (offset < start + find.Length && offset + length > start) touched.Add(run);
                offset += length;
            }
            if (touched.Any(run => !Simple(run))) return null;
        }
        if (!apply) return positions.Select(_ => new List<W.Run>()).ToList();
        // Split from the last occurrence backwards so earlier positions stay valid.
        foreach (var start in positions.AsEnumerable().Reverse())
        {
            SplitAt(paragraph, start + find.Length);
            SplitAt(paragraph, start);
        }
        runs = TextRuns(paragraph);
        foreach (var start in positions)
        {
            var offset = 0;
            var covered = new List<W.Run>();
            foreach (var run in runs)
            {
                var length = RunText(run).Length;
                if (offset >= start && offset + length <= start + find.Length && length > 0) covered.Add(run);
                offset += length;
            }
            occurrences.Add(covered);
        }
        return occurrences;
    }

    private static void SplitAt(W.Paragraph paragraph, int position)
    {
        var offset = 0;
        foreach (var run in TextRuns(paragraph))
        {
            var text = RunText(run);
            if (position > offset && position < offset + text.Length)
            {
                var cut = position - offset;
                var second = (W.Run)run.CloneNode(true);
                run.GetFirstChild<W.Text>()!.Text = text[..cut];
                run.GetFirstChild<W.Text>()!.Space = SpaceProcessingModeValues.Preserve;
                second.GetFirstChild<W.Text>()!.Text = text[cut..];
                second.GetFirstChild<W.Text>()!.Space = SpaceProcessingModeValues.Preserve;
                run.InsertAfterSelf(second);
                return;
            }
            offset += text.Length;
        }
    }

    private static readonly Type[] RunPropertyOrder = [typeof(W.RunStyle), typeof(W.RunFonts), typeof(W.Bold), typeof(W.BoldComplexScript), typeof(W.Italic), typeof(W.ItalicComplexScript), typeof(W.Caps), typeof(W.SmallCaps), typeof(W.Strike), typeof(W.DoubleStrike), typeof(W.Outline), typeof(W.Shadow), typeof(W.Emboss), typeof(W.Imprint), typeof(W.NoProof), typeof(W.SnapToGrid), typeof(W.Vanish), typeof(W.WebHidden), typeof(W.Color), typeof(W.Spacing), typeof(W.CharacterScale), typeof(W.Kern), typeof(W.Position), typeof(W.FontSize), typeof(W.FontSizeComplexScript), typeof(W.Highlight), typeof(W.Underline)];

    private static void Set<T>(W.RunProperties properties, T element) where T : OpenXmlElement
    {
        properties.RemoveAllChildren<T>();
        var position = Array.IndexOf(RunPropertyOrder, typeof(T));
        var after = properties.ChildElements.LastOrDefault(child => Array.IndexOf(RunPropertyOrder, child.GetType()) is var index && index >= 0 && index < position);
        if (after is null) properties.PrependChild(element); else properties.InsertAfter(element, after);
    }

    private static void Format(W.Run run, OfficeEdit edit)
    {
        var properties = run.RunProperties ?? run.PrependChild(new W.RunProperties());
        if (edit.Bold == true) Set(properties, new W.Bold());
        if (edit.Italic == true) Set(properties, new W.Italic());
        if (edit.Underline == true) Set(properties, new W.Underline { Val = W.UnderlineValues.Single });
        if (ExcelFormatting.Argb(edit.Color) is { } colour) Set(properties, new W.Color { Val = colour[2..] });
        if (edit.Highlight is { Length: > 0 } && Highlights.TryGetValue(edit.Highlight, out var highlight)) Set(properties, new W.Highlight { Val = highlight });
        if (edit.Size is { } size) { Set(properties, new W.FontSize { Val = ((int)Math.Round(size * 2)).ToString(CultureInfo.InvariantCulture) }); Set(properties, new W.FontSizeComplexScript { Val = ((int)Math.Round(size * 2)).ToString(CultureInfo.InvariantCulture) }); }
        if (FontOf(edit.Font) is { Length: > 0 } font) Set(properties, new W.RunFonts { Ascii = font, HighAnsi = font, EastAsia = font });
    }

    private static string Look(OfficeEdit edit) => string.Join("・", new[]
    {
        edit.Bold == true ? "太字" : null, edit.Italic == true ? "斜体" : null, edit.Underline == true ? "下線" : null,
        edit.Color is { Length: > 0 } ? $"文字色 {edit.Color}" : null, edit.Highlight is { Length: > 0 } ? $"蛍光ペン {edit.Highlight}" : null,
        edit.Size is { } size ? $"{size:0.#}pt" : null, FontOf(edit.Font) is { Length: > 0 } font ? font : null
    }.OfType<string>());

    private static IEnumerable<W.Paragraph> ParagraphsWith(W.Body body, string find) =>
        body.Descendants<W.Paragraph>().Where(paragraph => string.Concat(TextRuns(paragraph).Select(RunText)).Contains(find, StringComparison.Ordinal));

    private static W.Table? TableOf(W.Body body, int? index) => body.Descendants<W.Table>().ElementAtOrDefault(Math.Max(1, index ?? 1) - 1);

    private static string CellText(W.TableCell cell) => string.Join("\n", cell.Elements<W.Paragraph>().Select(paragraph => string.Concat(paragraph.Descendants<W.Text>().Select(text => text.Text))));

    // ---------- preview ----------

    public static OfficeChange? Describe(WordprocessingDocument document, OfficeEdit edit)
    {
        var body = Body(document);
        switch (edit.Op)
        {
            case "format-text":
            {
                if (string.IsNullOrEmpty(edit.Find)) return new("書式を変える文字を「」で指定してください", "invalid");
                var look = Look(edit);
                if (look.Length == 0 || (edit.Color is { Length: > 0 } && ExcelFormatting.Argb(edit.Color) is null) || (edit.Highlight is { Length: > 0 } && !Highlights.ContainsKey(edit.Highlight)) || edit.Size is < 4 or > 144)
                    return new($"書式の指定を読み取れません（{edit.Color}{edit.Highlight}{edit.Size}）", "invalid");
                var paragraphs = ParagraphsWith(body, edit.Find).ToList();
                var count = paragraphs.Sum(paragraph => Isolate(paragraph, edit.Find, apply: false)?.Count ?? 0);
                if (paragraphs.Any(paragraph => Isolate(paragraph, edit.Find, apply: false) is null)) return new($"「{edit.Find}」の一部が図やフィールドと同じ場所にあるため、書式を変えられません", "invalid");
                return new($"「{edit.Find}」（{count}か所）を {look} に", count > 0 ? "ok" : "not-found");
            }
            case "heading":
            {
                if (string.IsNullOrEmpty(edit.Find) || edit.Level is not (1 or 2 or 3)) return new("見出しにする段落の文字と、見出しの段階（1～3）を指定してください", "invalid");
                var count = ParagraphsWith(body, edit.Find).Count();
                return new($"「{edit.Find}」を含む段落（{count}か所）を見出し {edit.Level} に", count is > 0 and <= 20 ? "ok" : count == 0 ? "not-found" : "invalid");
            }
            case "add-paragraph":
            {
                if (string.IsNullOrWhiteSpace(edit.Text) || edit.Text.Length > 2000) return new("追加する文を「」で指定してください（2000文字まで）", "invalid");
                if (edit.Find is { Length: > 0 } anchor && !ParagraphsWith(body, anchor).Any()) return new($"「{anchor}」を含む段落が見つかりません", "not-found");
                var where = edit.Find is { Length: > 0 } ? $"「{edit.Find}」の段落の{(edit.Position == "before" ? "前" : "後")}" : edit.Position == "start" ? "文書の最初" : "文書の最後";
                return new($"{where}に段落「{edit.Text}」を追加", "ok");
            }
            case "delete-paragraph":
            {
                if (string.IsNullOrEmpty(edit.Find)) return new("削除する段落の文字を「」で指定してください", "invalid");
                var count = ParagraphsWith(body, edit.Find).Count(paragraph => paragraph.Parent is W.Body);
                return new($"「{edit.Find}」を含む段落（{count}か所）を削除", count is > 0 and <= 20 ? "ok" : count == 0 ? "not-found" : "invalid");
            }
            case "table-set-cell" or "table-add-row" or "table-delete-row":
            {
                if (TableOf(body, edit.Table) is not { } table) return new($"表{edit.Table ?? 1}が見つかりません（文書の表は {body.Descendants<W.Table>().Count()} 個）", "invalid");
                var rows = table.Elements<W.TableRow>().ToList();
                if (edit.Op == "table-add-row")
                    return edit.Values is { Count: > 0 } values && values.Count <= rows.Last().Elements<W.TableCell>().Count()
                        ? new($"表{edit.Table ?? 1}の最後に行を追加: {string.Join(" | ", values)}", "ok")
                        : new("追加する行の値を、列の数まで指定してください", "invalid");
                if (!int.TryParse(edit.Rows, out var row) || row < 1 || row > rows.Count) return new($"表{edit.Table ?? 1}の行「{edit.Rows}」がありません（{rows.Count}行）", "invalid");
                if (edit.Op == "table-delete-row") return rows.Count > 1 ? new($"表{edit.Table ?? 1}の {row}行目（{string.Join(" | ", rows[row - 1].Elements<W.TableCell>().Select(CellText))}）を削除", "ok") : new("表の最後の1行は削除できません", "invalid");
                var cells = rows[row - 1].Elements<W.TableCell>().ToList();
                if (!int.TryParse(edit.Column, out var column) || column < 1 || column > cells.Count || edit.Value is null) return new($"表{edit.Table ?? 1}の {row}行目に列「{edit.Column}」がありません（{cells.Count}列）", "invalid");
                return new($"表{edit.Table ?? 1}の {row}行目 {column}列目: {CellText(cells[column - 1])} → {edit.Value}", "ok");
            }
            case "header-text" or "footer-text":
                return string.IsNullOrWhiteSpace(edit.Text) || edit.Text.Length > 200
                    ? new("ヘッダー・フッターに入れる文字を「」で指定してください（200文字まで）", "invalid")
                    : new($"{(edit.Op == "header-text" ? "ヘッダー" : "フッター")}に「{edit.Text}」（{(edit.Position is "left" ? "左" : edit.Position is "right" ? "右" : "中央")}）", "ok");
            case "page-numbers":
                return new($"フッターにページ番号「{(edit.Format is { Length: > 0 } format ? format : "{n}").Replace("{n}", "1").Replace("{total}", "N")}」（{(edit.Position is "left" ? "左" : edit.Position is "right" ? "右" : "中央")}）", "ok");
            case "comment":
            {
                if (string.IsNullOrEmpty(edit.Find) || string.IsNullOrWhiteSpace(edit.Text)) return new("コメントを付ける文字とコメントを「」で指定してください", "invalid");
                var paragraph = ParagraphsWith(body, edit.Find).FirstOrDefault();
                if (paragraph is null) return new($"「{edit.Find}」が見つかりません", "not-found");
                if (Isolate(paragraph, edit.Find, apply: false) is null) return new($"「{edit.Find}」の一部が図やフィールドと同じ場所にあるため、コメントを付けられません", "invalid");
                return new($"最初の「{edit.Find}」にコメント「{edit.Text}」", "ok");
            }
            case "accept-changes" or "reject-changes":
            {
                var insertions = body.Descendants<W.InsertedRun>().Count() + body.Descendants<W.MoveToRun>().Count();
                var deletions = body.Descendants<W.DeletedRun>().Count() + body.Descendants<W.MoveFromRun>().Count();
                var formats = body.Descendants<W.RunPropertiesChange>().Count() + body.Descendants<W.ParagraphPropertiesChange>().Count();
                if (edit.Op == "reject-changes" && formats > 0) return new("書式の変更履歴があるため、すべてを元に戻すことはできません（文字の追加・削除の履歴だけなら元に戻せます）", "invalid");
                var total = insertions + deletions + formats;
                return new($"変更履歴（挿入 {insertions}・削除 {deletions}{(formats > 0 ? $"・書式 {formats}" : "")}）をすべて{(edit.Op == "accept-changes" ? "承諾（反映）" : "却下（元に戻す）")}", total > 0 ? "ok" : "not-found");
            }
            default:
                return null;
        }
    }

    // ---------- apply ----------

    public static void Apply(WordprocessingDocument document, OfficeEdit edit)
    {
        var main = document.MainDocumentPart!;
        var body = Body(document);
        switch (edit.Op)
        {
            case "format-text":
                foreach (var paragraph in ParagraphsWith(body, edit.Find!).ToList())
                    foreach (var occurrence in Isolate(paragraph, edit.Find!, apply: true)!)
                        foreach (var run in occurrence) Format(run, edit);
                break;
            case "heading":
            {
                var style = HeadingStyle(main, edit.Level!.Value);
                foreach (var paragraph in ParagraphsWith(body, edit.Find!).ToList())
                {
                    var properties = paragraph.ParagraphProperties ?? paragraph.PrependChild(new W.ParagraphProperties());
                    properties.ParagraphStyleId = new W.ParagraphStyleId { Val = style };
                }
                break;
            }
            case "add-paragraph":
            {
                var anchor = edit.Find is { Length: > 0 } find ? ParagraphsWith(body, find).First() : null;
                var model = anchor ?? body.Elements<W.Paragraph>().LastOrDefault();
                var paragraph = new W.Paragraph();
                if (model?.ParagraphProperties is { } properties)
                {
                    var copy = (W.ParagraphProperties)properties.CloneNode(true);
                    copy.RemoveAllChildren<W.SectionProperties>();
                    copy.RemoveAllChildren<W.NumberingProperties>();
                    paragraph.Append(copy);
                }
                var run = new W.Run();
                if (model is not null && TextRuns(model).FirstOrDefault()?.RunProperties is { } look) run.Append(look.CloneNode(true));
                run.Append(new W.Text(edit.Text!) { Space = SpaceProcessingModeValues.Preserve });
                paragraph.Append(run);
                if (anchor is not null) { if (edit.Position == "before") anchor.InsertBeforeSelf(paragraph); else anchor.InsertAfterSelf(paragraph); }
                else if (edit.Position == "start") body.PrependChild(paragraph);
                else if (body.GetFirstChild<W.SectionProperties>() is { } section && section == body.LastChild) section.InsertBeforeSelf(paragraph);
                else body.Append(paragraph);
                break;
            }
            case "delete-paragraph":
                foreach (var paragraph in ParagraphsWith(body, edit.Find!).Where(paragraph => paragraph.Parent is W.Body).ToList())
                {
                    // A paragraph holding the section break keeps it on the paragraph before.
                    if (paragraph.ParagraphProperties?.SectionProperties is { } section && paragraph.PreviousSibling<W.Paragraph>() is { } previous)
                        (previous.ParagraphProperties ?? previous.PrependChild(new W.ParagraphProperties())).Append(section.CloneNode(true));
                    paragraph.Remove();
                }
                break;
            case "table-set-cell":
            {
                var cell = TableOf(body, edit.Table)!.Elements<W.TableRow>().ElementAt(int.Parse(edit.Rows!, CultureInfo.InvariantCulture) - 1).Elements<W.TableCell>().ElementAt(int.Parse(edit.Column!, CultureInfo.InvariantCulture) - 1);
                SetCell(cell, edit.Value!);
                break;
            }
            case "table-add-row":
            {
                var table = TableOf(body, edit.Table)!;
                var last = table.Elements<W.TableRow>().Last();
                var row = (W.TableRow)last.CloneNode(true);
                var cells = row.Elements<W.TableCell>().ToList();
                for (var index = 0; index < cells.Count; index++) SetCell(cells[index], index < edit.Values!.Count ? edit.Values[index] : "");
                last.InsertAfterSelf(row);
                break;
            }
            case "table-delete-row":
                TableOf(body, edit.Table)!.Elements<W.TableRow>().ElementAt(int.Parse(edit.Rows!, CultureInfo.InvariantCulture) - 1).Remove();
                break;
            case "header-text" or "footer-text" or "page-numbers":
            {
                var header = edit.Op == "header-text";
                var paragraph = new W.Paragraph(new W.ParagraphProperties(new W.Justification { Val = edit.Position is "left" ? W.JustificationValues.Left : edit.Position is "right" ? W.JustificationValues.Right : W.JustificationValues.Center }));
                if (edit.Op == "page-numbers")
                {
                    var format = edit.Format is { Length: > 0 } given ? given : "{n}";
                    foreach (var part in Regex.Split(format, @"(\{n\}|\{total\})").Where(part => part.Length > 0))
                        paragraph.Append(part switch
                        {
                            "{n}" => new W.SimpleField(new W.Run(new W.Text("1"))) { Instruction = " PAGE " },
                            "{total}" => new W.SimpleField(new W.Run(new W.Text("1"))) { Instruction = " NUMPAGES " },
                            _ => new W.Run(new W.Text(part) { Space = SpaceProcessingModeValues.Preserve })
                        });
                }
                else paragraph.Append(new W.Run(new W.Text(edit.Text!) { Space = SpaceProcessingModeValues.Preserve }));
                var section = body.Elements<W.SectionProperties>().LastOrDefault() ?? body.AppendChild(new W.SectionProperties());
                if (header)
                {
                    var reference = section.Elements<W.HeaderReference>().FirstOrDefault(item => item.Type is null || item.Type.Value == W.HeaderFooterValues.Default);
                    if (reference is not null && main.GetPartById(reference.Id!.Value!) is HeaderPart existing) existing.Header!.Append(paragraph);
                    else
                    {
                        var part = main.AddNewPart<HeaderPart>();
                        part.Header = new W.Header(paragraph);
                        section.PrependChild(new W.HeaderReference { Type = W.HeaderFooterValues.Default, Id = main.GetIdOfPart(part) });
                    }
                }
                else
                {
                    var reference = section.Elements<W.FooterReference>().FirstOrDefault(item => item.Type is null || item.Type.Value == W.HeaderFooterValues.Default);
                    if (reference is not null && main.GetPartById(reference.Id!.Value!) is FooterPart existing) existing.Footer!.Append(paragraph);
                    else
                    {
                        var part = main.AddNewPart<FooterPart>();
                        part.Footer = new W.Footer(paragraph);
                        var after = section.Elements<W.HeaderReference>().LastOrDefault();
                        var footerReference = new W.FooterReference { Type = W.HeaderFooterValues.Default, Id = main.GetIdOfPart(part) };
                        if (after is null) section.PrependChild(footerReference); else after.InsertAfterSelf(footerReference);
                    }
                }
                break;
            }
            case "comment":
            {
                var part = main.WordprocessingCommentsPart ?? main.AddNewPart<WordprocessingCommentsPart>();
                part.Comments ??= new W.Comments();
                var id = (part.Comments.Elements<W.Comment>().Select(comment => int.TryParse(comment.Id?.Value, out var number) ? number : 0).DefaultIfEmpty(-1).Max() + 1).ToString(CultureInfo.InvariantCulture);
                part.Comments.Append(new W.Comment(new W.Paragraph(new W.Run(new W.Text(edit.Text!) { Space = SpaceProcessingModeValues.Preserve }))) { Id = id, Author = "Local Studio", Initials = "LS", Date = DateTime.Now });
                var paragraph = ParagraphsWith(body, edit.Find!).First();
                var runs = Isolate(paragraph, edit.Find!, apply: true)![0];
                runs[0].InsertBeforeSelf(new W.CommentRangeStart { Id = id });
                var end = runs[^1].InsertAfterSelf(new W.CommentRangeEnd { Id = id });
                end.InsertAfterSelf(new W.Run(new W.CommentReference { Id = id }));
                break;
            }
            case "accept-changes":
                foreach (var inserted in body.Descendants<W.InsertedRun>().Cast<OpenXmlElement>().Concat(body.Descendants<W.MoveToRun>()).ToList()) Unwrap(inserted);
                foreach (var removed in body.Descendants<W.DeletedRun>().Cast<OpenXmlElement>().Concat(body.Descendants<W.MoveFromRun>()).ToList()) removed.Remove();
                foreach (var change in body.Descendants().Where(element => element is W.RunPropertiesChange or W.ParagraphPropertiesChange or W.Inserted or W.Deleted or W.MoveFromRangeStart or W.MoveFromRangeEnd or W.MoveToRangeStart or W.MoveToRangeEnd).ToList()) change.Remove();
                break;
            case "reject-changes":
                foreach (var inserted in body.Descendants<W.InsertedRun>().Cast<OpenXmlElement>().Concat(body.Descendants<W.MoveToRun>()).ToList()) inserted.Remove();
                foreach (var removed in body.Descendants<W.DeletedRun>().Cast<OpenXmlElement>().Concat(body.Descendants<W.MoveFromRun>()).ToList())
                {
                    foreach (var deletedText in removed.Descendants<W.DeletedText>().ToList())
                        deletedText.InsertAfterSelf(new W.Text(deletedText.Text) { Space = SpaceProcessingModeValues.Preserve }).Parent!.RemoveChild(deletedText);
                    Unwrap(removed);
                }
                foreach (var change in body.Descendants().Where(element => element is W.Inserted or W.Deleted or W.MoveFromRangeStart or W.MoveFromRangeEnd or W.MoveToRangeStart or W.MoveToRangeEnd).ToList()) change.Remove();
                break;
        }
    }

    private static void Unwrap(OpenXmlElement wrapper)
    {
        foreach (var child in wrapper.ChildElements.ToList()) { child.Remove(); wrapper.InsertBeforeSelf(child); }
        wrapper.Remove();
    }

    private static void SetCell(W.TableCell cell, string value)
    {
        var paragraphs = cell.Elements<W.Paragraph>().ToList();
        var first = paragraphs.FirstOrDefault() ?? cell.AppendChild(new W.Paragraph());
        foreach (var extra in paragraphs.Skip(1)) extra.Remove();
        var look = TextRuns(first).FirstOrDefault()?.RunProperties?.CloneNode(true);
        foreach (var run in first.Elements<W.Run>().ToList()) run.Remove();
        var created = new W.Run();
        if (look is not null) created.Append(look);
        created.Append(new W.Text(value) { Space = SpaceProcessingModeValues.Preserve });
        first.Append(created);
    }

    /// <summary>The id of the "heading N" style, added (bold, larger, outline level) when the document has none.</summary>
    private static string HeadingStyle(MainDocumentPart main, int level)
    {
        var part = main.StyleDefinitionsPart ?? main.AddNewPart<StyleDefinitionsPart>();
        part.Styles ??= new W.Styles();
        var existing = part.Styles.Elements<W.Style>().FirstOrDefault(style => string.Equals(style.StyleName?.Val?.Value, $"heading {level}", StringComparison.OrdinalIgnoreCase));
        if (existing?.StyleId?.Value is { } id) return id;
        var styleId = $"Heading{level}";
        var style = new W.Style(
            new W.StyleName { Val = $"heading {level}" },
            new W.NextParagraphStyle { Val = "Normal" },
            new W.UIPriority { Val = 9 },
            new W.PrimaryStyle(),
            new W.StyleParagraphProperties(new W.KeepNext(), new W.OutlineLevel { Val = level - 1 }),
            new W.StyleRunProperties(new W.Bold(), new W.FontSize { Val = (36 - level * 4).ToString(CultureInfo.InvariantCulture) }))
        { Type = W.StyleValues.Paragraph, StyleId = styleId };
        part.Styles.Append(style);
        return styleId;
    }

    // ---------- read-back ----------

    private static string Text(OpenXmlElement element, bool accepted)
    {
        var parts = element.Descendants().Where(node => node is W.Text or W.DeletedText).Where(node =>
            accepted ? node is W.Text && !node.Ancestors().Any(ancestor => ancestor is W.DeletedRun or W.MoveFromRun)
                     : !node.Ancestors().Any(ancestor => ancestor is W.InsertedRun or W.MoveToRun));
        return string.Concat(parts.Select(node => node is W.Text text ? text.Text : ((W.DeletedText)node).Text));
    }

    private static string[] ParagraphTexts(WordprocessingDocument document) =>
        Body(document).Descendants<W.Paragraph>().Select(paragraph => string.Concat(paragraph.Descendants<W.Text>().Where(text => !text.Ancestors<W.Comment>().Any()).Select(text => text.Text))).ToArray();

    public static IEnumerable<string> Verify(string sourcePath, string outputPath, OfficeEdit edit)
    {
        using var beforeStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var before = WordprocessingDocument.Open(beforeStream, false);
        using var after = WordprocessingDocument.Open(outputPath, false);
        var oldTexts = ParagraphTexts(before);
        var newTexts = ParagraphTexts(after);
        var body = Body(after);
        switch (edit.Op)
        {
            case "format-text":
            {
                if (!oldTexts.SequenceEqual(newTexts)) yield return "書式を変えたあとで文章が変わっています";
                foreach (var paragraph in ParagraphsWith(body, edit.Find!))
                {
                    var runs = TextRuns(paragraph);
                    var text = string.Concat(runs.Select(RunText));
                    for (var start = text.IndexOf(edit.Find!, StringComparison.Ordinal); start >= 0; start = text.IndexOf(edit.Find!, start + edit.Find!.Length, StringComparison.Ordinal))
                    {
                        var offset = 0;
                        foreach (var run in runs)
                        {
                            var length = RunText(run).Length;
                            if (offset < start + edit.Find!.Length && offset + length > start && !HasLook(run, edit)) { yield return $"「{edit.Find}」の書式が予定どおりになっていません"; yield break; }
                            offset += length;
                        }
                    }
                }
                break;
            }
            case "heading":
                if (!oldTexts.SequenceEqual(newTexts)) yield return "見出しにしたあとで文章が変わっています";
                if (ParagraphsWith(body, edit.Find!).Any(paragraph => paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value is not { } style ||
                        !string.Equals(after.MainDocumentPart!.StyleDefinitionsPart?.Styles?.Elements<W.Style>().FirstOrDefault(item => item.StyleId?.Value == style)?.StyleName?.Val?.Value, $"heading {edit.Level}", StringComparison.OrdinalIgnoreCase)))
                    yield return $"「{edit.Find}」が見出し {edit.Level} になっていません";
                break;
            case "add-paragraph":
                if (newTexts.Length != oldTexts.Length + 1 || !newTexts.Contains(edit.Text) || !newTexts.Where((text, index) => text != edit.Text || newTexts.Count(item => item == edit.Text) > 1).Except(oldTexts).All(text => text == edit.Text))
                    yield return $"段落「{edit.Text}」が予定どおりに追加されていません";
                break;
            case "delete-paragraph":
            {
                var expected = Body(before).Descendants<W.Paragraph>().Where(paragraph => !(paragraph.Parent is W.Body && string.Concat(TextRuns(paragraph).Select(RunText)).Contains(edit.Find!, StringComparison.Ordinal)))
                    .Select(paragraph => string.Concat(paragraph.Descendants<W.Text>().Where(text => !text.Ancestors<W.Comment>().Any()).Select(text => text.Text))).ToArray();
                if (!expected.SequenceEqual(newTexts)) yield return $"「{edit.Find}」の段落の削除が予定どおりになっていません";
                break;
            }
            case "table-set-cell":
            {
                var cell = TableOf(body, edit.Table)?.Elements<W.TableRow>().ElementAtOrDefault(int.Parse(edit.Rows!, CultureInfo.InvariantCulture) - 1)?.Elements<W.TableCell>().ElementAtOrDefault(int.Parse(edit.Column!, CultureInfo.InvariantCulture) - 1);
                if (cell is null || CellText(cell) != edit.Value) yield return $"表のセルが「{edit.Value}」になっていません";
                break;
            }
            case "table-add-row":
            {
                var oldRows = TableOf(Body(before), edit.Table)!.Elements<W.TableRow>().Count();
                var rows = TableOf(body, edit.Table)?.Elements<W.TableRow>().ToList() ?? [];
                if (rows.Count != oldRows + 1 || !rows[^1].Elements<W.TableCell>().Select(CellText).Take(edit.Values!.Count).SequenceEqual(edit.Values)) yield return "表に追加した行が予定どおりになっていません";
                break;
            }
            case "table-delete-row":
            {
                var oldRows = TableOf(Body(before), edit.Table)!.Elements<W.TableRow>().Select(row => string.Join("|", row.Elements<W.TableCell>().Select(CellText))).ToList();
                oldRows.RemoveAt(int.Parse(edit.Rows!, CultureInfo.InvariantCulture) - 1);
                var rows = TableOf(body, edit.Table)?.Elements<W.TableRow>().Select(row => string.Join("|", row.Elements<W.TableCell>().Select(CellText))).ToList() ?? [];
                if (!oldRows.SequenceEqual(rows)) yield return "表の行の削除が予定どおりになっていません";
                break;
            }
            case "header-text":
                if (!after.MainDocumentPart!.HeaderParts.Any(part => part.Header!.InnerText.Contains(edit.Text!, StringComparison.Ordinal))) yield return $"ヘッダーに「{edit.Text}」が見つかりません";
                break;
            case "footer-text":
                if (!after.MainDocumentPart!.FooterParts.Any(part => part.Footer!.InnerText.Contains(edit.Text!, StringComparison.Ordinal))) yield return $"フッターに「{edit.Text}」が見つかりません";
                break;
            case "page-numbers":
                if (!after.MainDocumentPart!.FooterParts.Any(part => part.Footer!.Descendants<W.SimpleField>().Any(field => field.Instruction?.Value?.Contains("PAGE", StringComparison.Ordinal) == true)))
                    yield return "フッターにページ番号が見つかりません";
                break;
            case "comment":
            {
                var comment = after.MainDocumentPart!.WordprocessingCommentsPart?.Comments?.Elements<W.Comment>().FirstOrDefault(item => item.InnerText == edit.Text);
                var start = comment is null ? null : body.Descendants<W.CommentRangeStart>().FirstOrDefault(item => item.Id?.Value == comment.Id?.Value);
                if (comment is null || start is null || !string.Concat(start.Ancestors<W.Paragraph>().First().Descendants<W.Text>().Select(text => text.Text)).Contains(edit.Find!, StringComparison.Ordinal))
                    yield return $"「{edit.Find}」へのコメント「{edit.Text}」が見つかりません";
                if (!oldTexts.SequenceEqual(newTexts)) yield return "コメントを付けたあとで文章が変わっています";
                break;
            }
            case "accept-changes" or "reject-changes":
            {
                var accepted = edit.Op == "accept-changes";
                var expected = Body(before).Descendants<W.Paragraph>().Select(paragraph => Text(paragraph, accepted)).ToArray();
                var actual = body.Descendants<W.Paragraph>().Select(paragraph => Text(paragraph, accepted)).ToArray();
                if (body.Descendants().Any(element => element is W.InsertedRun or W.DeletedRun or W.MoveToRun or W.MoveFromRun)) yield return "変更履歴が残っています";
                if (!expected.SequenceEqual(actual)) yield return "変更履歴を反映した文章が予定と違います";
                break;
            }
        }
    }

    private static bool HasLook(W.Run run, OfficeEdit edit)
    {
        var properties = run.RunProperties;
        if (properties is null) return false;
        if (edit.Bold == true && properties.Bold is null) return false;
        if (edit.Italic == true && properties.Italic is null) return false;
        if (edit.Underline == true && properties.Underline is null) return false;
        if (ExcelFormatting.Argb(edit.Color) is { } colour && properties.Color?.Val?.Value != colour[2..]) return false;
        if (edit.Highlight is { Length: > 0 } && (properties.Highlight?.Val is null || properties.Highlight.Val.Value != Highlights[edit.Highlight])) return false;
        if (edit.Size is { } size && properties.FontSize?.Val?.Value != ((int)Math.Round(size * 2)).ToString(CultureInfo.InvariantCulture)) return false;
        if (FontOf(edit.Font) is { Length: > 0 } font && properties.RunFonts?.EastAsia?.Value != font) return false;
        return true;
    }
}
