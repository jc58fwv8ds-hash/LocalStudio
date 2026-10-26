using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Pig = UglyToad.PdfPig;
using Sharp = PdfSharp.Pdf;

namespace SafeLocalHarness.Core;

public sealed record PdfPageInfo(int Number, double Width, double Height, int Rotation, string Text);

public sealed record PdfSummary(string Path, IReadOnlyList<PdfPageInfo> Pages, string? Title, bool Protected)
{
    public const int MaxFactCharactersPerPage = 1_200;
    public const int MaxFactCharacters = 6_000;

    /// <summary>Names of the form's text input fields (4-4 fill).</summary>
    public IReadOnlyList<string> Fields { get; init; } = [];

    /// <summary>What the model may use: page count, sizes and each page's text (shortened), as data.</summary>
    public string ToFacts()
    {
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"ファイル「{System.IO.Path.GetFileName(Path)}」: {Pages.Count}ページ");
        if (!string.IsNullOrWhiteSpace(Title)) builder.Append("、タイトル「").Append(Title).Append('」');
        if (Fields.Count > 0) builder.Append("、入力欄: ").Append(string.Join("、", Fields.Take(30).Select(field => "「" + field + "」")));
        builder.Append('\n');
        foreach (var page in Pages)
        {
            if (builder.Length >= MaxFactCharacters) { builder.Append("…（以降のページは省略）\n"); break; }
            var text = Regex.Replace(page.Text, @"\s+", " ").Trim();
            if (text.Length > MaxFactCharactersPerPage) text = text[..MaxFactCharactersPerPage] + "…";
            builder.Append(CultureInfo.InvariantCulture, $"[{page.Number}ページ目 {PdfDocumentReader.SizeName(page)}] ")
                .Append(text.Length == 0 ? "（文字なし。画像のみのページの可能性）" : text).Append('\n');
        }
        return builder.ToString();
    }
}

/// <summary>
/// PDF reading for 4-3 with PdfPig (Apache-2.0). Only files that start like a PDF are opened; password-protected
/// files are refused, and files protected against changes are read but never rewritten.
/// </summary>
public static class PdfDocumentReader
{
    public const long MaxFileBytes = 50L * 1024 * 1024;
    public const int MaxPages = 500;
    public const int MaxFiles = 10;

    /// <summary>Null when the file can be read; otherwise the reason in plain Japanese.</summary>
    public static string? Check(string path)
    {
        if (!Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return "PDF（.pdf）のファイルだけを扱えます。";
        var info = new FileInfo(path);
        if (!info.Exists) return "ファイルが見つかりません。";
        if (info.Length is 0 or > MaxFileBytes) return $"PDFは{MaxFileBytes / 1024 / 1024} MBまでです。";
        var head = new byte[1024];
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var read = stream.Read(head, 0, head.Length);
            if (Encoding.ASCII.GetString(head, 0, read).IndexOf("%PDF-", StringComparison.Ordinal) < 0) return "PDFとして読み取れません（中身がPDFではありません）。";
        }
        return null;
    }

    public static PdfSummary Read(string path)
    {
        if (Check(path) is { } reason) throw new InvalidOperationException(reason);
        try
        {
            using var document = Pig.PdfDocument.Open(path);
            if (document.NumberOfPages > MaxPages) throw new InvalidOperationException($"{MaxPages}ページを超えるPDFは扱いません。");
            var pages = new List<PdfPageInfo>();
            for (var number = 1; number <= document.NumberOfPages; number++)
            {
                var page = document.GetPage(number);
                pages.Add(new PdfPageInfo(number, Math.Round(page.Width, 1), Math.Round(page.Height, 1), page.Rotation.Value, page.Text ?? ""));
            }
            return new PdfSummary(Path.GetFullPath(path), pages, document.Information?.Title, document.IsEncrypted) { Fields = PdfMarkup.FormFields(path).Order(StringComparer.Ordinal).ToArray() };
        }
        catch (Pig.Exceptions.PdfDocumentEncryptedException)
        {
            throw new InvalidOperationException("パスワード付きのPDFは開きません。");
        }
        catch (Pig.Core.PdfDocumentFormatException ex)
        {
            throw new InvalidDataException("PDFが壊れているため読み取れません（" + ex.Message + "）。", ex);
        }
    }

    /// <summary>"A4 縦" and the like; other sizes in millimetres.</summary>
    public static string SizeName(PdfPageInfo page)
    {
        var (shortSide, longSide) = (Math.Min(page.Width, page.Height), Math.Max(page.Width, page.Height));
        var name = Near(shortSide, 595, longSide, 842) ? "A4" : Near(shortSide, 842, longSide, 1191) ? "A3" : Near(shortSide, 420, longSide, 595) ? "A5"
            : Near(shortSide, 612, longSide, 792) ? "レター" : Near(shortSide, 516, longSide, 729) ? "B5" : $"{Math.Round(page.Width / 72 * 25.4)}×{Math.Round(page.Height / 72 * 25.4)}mm";
        var landscape = page.Rotation % 180 == 0 ? page.Width > page.Height : page.Height > page.Width;
        return name + (landscape ? " 横" : " 縦") + (page.Rotation == 0 ? "" : $"・回転{page.Rotation}°");
    }

    private static bool Near(double a, double expectedA, double b, double expectedB) => Math.Abs(a - expectedA) < 6 && Math.Abs(b - expectedB) < 6;

    /// <summary>Page text with whitespace removed: what "the same page" means when results are checked.</summary>
    public static string Fingerprint(string text) => Regex.Replace(text, @"\s+", "");
}

/// <summary>
/// One page operation. op: merge (all chosen files, in order; first) | extract {pages} | delete {pages} |
/// rotate {pages (all when empty), degrees 90/180/270 clockwise} | reorder {order: every page once} |
/// split {pages: groups like "1-3,4-6"; one file per page when empty; last}.
/// 4-4 additions: stamp {text, pages (all when empty), position} | watermark {text, pages} | page-numbers {text: format
/// with {n} and {total}, position} | note {text, pages: one page} | info {field: title/author/subject, value} |
/// fill {field: form field name, value}. 4-4b: ocr {pages: pages without text when empty} | redact {text, pages} |
/// to-images {pages} (PNG files instead of a PDF; alone).
/// </summary>
public sealed record PdfOp(
    [property: JsonPropertyName("op")] string Op,
    [property: JsonPropertyName("pages")] string? Pages = null,
    [property: JsonPropertyName("degrees")] int? Degrees = null,
    [property: JsonPropertyName("order")] IReadOnlyList<int>? Order = null,
    [property: JsonPropertyName("text")] string? Text = null,
    [property: JsonPropertyName("position")] string? Position = null,
    [property: JsonPropertyName("field")] string? Field = null,
    [property: JsonPropertyName("value")] string? Value = null,
    [property: JsonPropertyName("font")] string? Font = null,
    [property: JsonPropertyName("size")] double? Size = null,
    [property: JsonPropertyName("color")] string? Color = null,
    [property: JsonPropertyName("bold")] bool? Bold = null,
    [property: JsonPropertyName("dx")] double? Dx = null,
    [property: JsonPropertyName("dy")] double? Dy = null);

/// <summary>
/// How drawn text looks (hands-on 2026-10-13: "明朝体で10pt、下から20ptに", "もう少し上に"): font (gothic / mincho / ud),
/// size in points, colour, bold, and a shift from the standard position in points (dx right, dy up).
/// </summary>
public sealed record PdfTextStyle(string Font = "gothic", double? Size = null, string? Color = null, bool Bold = false, double Dx = 0, double Dy = 0)
{
    public static readonly string[] Fonts = ["gothic", "mincho", "ud"];

    public static string FontName(string font) => font switch { "mincho" => "明朝体", "ud" => "UD教科書体", _ => "ゴシック体" };

    /// <summary>The style an operation asks for; the problem when it cannot be drawn exactly.</summary>
    public static (PdfTextStyle? Style, string? Problem) From(PdfOp op)
    {
        var font = string.IsNullOrWhiteSpace(op.Font) ? "gothic" : op.Font.Trim().ToLowerInvariant();
        if (!Fonts.Contains(font)) return (null, $"フォント「{op.Font}」は使えません（ゴシック体・明朝体・UD教科書体）");
        if (op.Size is { } size && size is < 4 or > 144) return (null, "文字の大きさは 4～144pt にしてください");
        if (op.Color is { Length: > 0 } && ExcelFormatting.Argb(op.Color) is null) return (null, $"色「{op.Color}」が読めません");
        if (Math.Abs(op.Dx ?? 0) > 400 || Math.Abs(op.Dy ?? 0) > 400) return (null, "位置のずらしは 400pt までにしてください");
        return (new PdfTextStyle(font, op.Size, op.Color is { Length: > 0 } ? op.Color : null, op.Bold == true, op.Dx ?? 0, op.Dy ?? 0), null);
    }

    /// <summary>"明朝体・10pt・赤・上に20pt" (empty for the standard look).</summary>
    public string Describe() => string.Join("・", new[]
    {
        Font == "gothic" ? null : FontName(Font),
        Size is { } size ? $"{size:0.#}pt" : null,
        Color,
        Bold ? "太字" : null,
        Dy > 0 ? $"上に{Dy:0.#}pt" : Dy < 0 ? $"下に{-Dy:0.#}pt" : null,
        Dx > 0 ? $"右に{Dx:0.#}pt" : Dx < 0 ? $"左に{-Dx:0.#}pt" : null
    }.OfType<string>());
}

/// <summary>Something drawn or attached on a result page: stamp, watermark, number (drawn text) or note (annotation).</summary>
/// <summary>4-4b: "ocr" carries the recognised lines (in points) drawn as invisible text; "redact" turns the page into a picture with the text painted black.</summary>
public sealed record PdfMark(string Kind, string Text, string Position, IReadOnlyList<OcrLine>? Lines = null, PdfTextStyle? Style = null);

/// <summary>A page of the result: which chosen file, which page in it, its final rotation, and what is added to it.</summary>
/// <remarks>File -1 is a blank page (phase C) of the given size.</remarks>
public sealed record PdfPageRef(int File, int Page, int Rotation, IReadOnlyList<PdfMark>? Marks = null, double BlankWidth = 0, double BlankHeight = 0)
{
    public bool Blank => File < 0;

    public PdfPageRef With(PdfMark mark) => this with { Marks = [.. Marks ?? [], mark] };
}

public sealed record PdfEditPreview(string Id, IReadOnlyList<string> Sources, IReadOnlyList<PdfOp> Ops, IReadOnlyList<IReadOnlyList<PdfPageRef>> Outputs,
    IReadOnlyList<string> Lines, IReadOnlyList<string> Problems, IReadOnlyList<string> SourceSha256)
{
    /// <summary>Document-level changes (info, fill) applied to every result.</summary>
    public IReadOnlyList<PdfOp> DocumentChanges { get; init; } = [];

    /// <summary>True when the pages stay as they are (no merge/extract/delete/reorder/split): the file is then edited as a whole, keeping forms and bookmarks.</summary>
    public bool KeepsStructure => Ops.All(op => op.Op is not ("merge" or "extract" or "delete" or "reorder" or "split" or "insert-blank"));

    public bool CanApply => Problems.Count == 0 && Outputs.Count > 0 && Outputs.All(output => output.Count > 0);
}

public sealed record PdfEditResult(IReadOnlyList<string> OutputPaths, IReadOnlyList<string> Mismatches)
{
    public bool Succeeded => Mismatches.Count == 0;
}

/// <summary>
/// Page edits for 4-3 with PDFsharp (MIT). The plan is turned into the exact list of pages each new file will hold,
/// shown before anything is written. Originals are never written; each result is read back with PdfPig and every page
/// is compared with the source page it should be (text, size and rotation), so "the right pages in the right order"
/// is checked rather than assumed.
/// </summary>
public static class PdfEditor
{
    public const int MaxOps = 5;
    public const int MaxOutputs = 100;

    public static PdfEditPreview Preview(IReadOnlyList<string> paths, IReadOnlyList<PdfOp> ops)
    {
        if (paths.Count is 0 || paths.Count > PdfDocumentReader.MaxFiles) throw new InvalidOperationException($"PDFは1～{PdfDocumentReader.MaxFiles}個にしてください。");
        if (ops.Count is 0 || ops.Count > MaxOps) throw new InvalidDataException($"操作は1～{MaxOps}個にしてください。");
        var summaries = paths.Select(PdfDocumentReader.Read).ToArray();
        var problems = new List<string>();
        if (summaries.FirstOrDefault(summary => summary.Protected) is { } locked)
            problems.Add($"「{Path.GetFileName(locked.Path)}」は変更が制限されたPDFのため、作り直しません");
        if (summaries.Length > 1 && ops[0].Op != "merge") problems.Add("複数のPDFを選んだときは、最初に結合します（1つだけ直すときは、そのPDFだけを選んでください）");
        var lines = new List<string>();
        var pages = summaries[0].Pages.Select(page => new PdfPageRef(0, page.Number, page.Rotation)).ToList();
        List<List<PdfPageRef>>? split = null;
        var documentChanges = new List<PdfOp>();
        for (var index = 0; index < ops.Count; index++)
        {
            var op = ops[index];
            if (split is not null) { problems.Add("分割のあとに別の操作は続けられません"); break; }
            switch (op.Op)
            {
                case "merge" when index == 0 && summaries.Length > 1:
                    pages = summaries.SelectMany((summary, file) => summary.Pages.Select(page => new PdfPageRef(file, page.Number, page.Rotation))).ToList();
                    lines.Add($"{summaries.Length}個のPDFをこの順で結合: " + string.Join(" → ", summaries.Select(summary => $"{Path.GetFileName(summary.Path)}（{summary.Pages.Count}ページ）")));
                    break;
                case "merge":
                    problems.Add(summaries.Length > 1 ? "結合は最初の操作にしてください" : "結合するには2つ以上のPDFを選んでください");
                    break;
                case "extract" or "delete":
                    ParsePages(op.Pages, pages.Count, out var chosen, out var error);
                    if (error is not null) { problems.Add(error); break; }
                    pages = op.Op == "extract" ? pages.Where((_, position) => chosen.Contains(position + 1)).ToList() : pages.Where((_, position) => !chosen.Contains(position + 1)).ToList();
                    lines.Add((op.Op == "extract" ? "残すページ: " : "削除するページ: ") + Ranges(chosen) + $"（結果 {pages.Count}ページ）");
                    if (pages.Count == 0) problems.Add("すべてのページがなくなります");
                    break;
                case "rotate" when op.Degrees is 90 or 180 or 270:
                    var targets = new HashSet<int>(Enumerable.Range(1, pages.Count));
                    if (!string.IsNullOrWhiteSpace(op.Pages))
                    {
                        ParsePages(op.Pages, pages.Count, out targets, out var rotateError);
                        if (rotateError is not null) { problems.Add(rotateError); break; }
                    }
                    pages = pages.Select((page, position) => targets.Contains(position + 1) ? page with { Rotation = (page.Rotation + op.Degrees.Value) % 360 } : page).ToList();
                    lines.Add($"{(targets.Count == pages.Count ? "全ページ" : Ranges(targets) + "ページ")}を時計回りに{op.Degrees}°回転");
                    break;
                case "rotate":
                    problems.Add("回転は 90・180・270 度のどれかです");
                    break;
                case "reorder" when op.Order is { } order && order.Count == pages.Count && order.Order().SequenceEqual(Enumerable.Range(1, pages.Count)):
                    pages = order.Select(number => pages[number - 1]).ToList();
                    lines.Add("ページの順番: " + string.Join(", ", order));
                    break;
                case "reorder":
                    problems.Add($"並べ替えは 1～{pages.Count} のすべてのページを1回ずつ指定してください");
                    break;
                case "split":
                    if (string.IsNullOrWhiteSpace(op.Pages))
                        split = pages.Select(page => new List<PdfPageRef> { page }).ToList();
                    else
                    {
                        split = [];
                        foreach (var group in op.Pages.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            ParsePages(group, pages.Count, out var groupPages, out var groupError);
                            if (groupError is not null) { problems.Add(groupError); break; }
                            split.Add(groupPages.Order().Select(number => pages[number - 1]).ToList());
                        }
                    }
                    if (split.Count > MaxOutputs) problems.Add($"分割して作るファイルは{MaxOutputs}個までです");
                    lines.Add($"{split.Count}個のファイルに分割: " + string.Join(" / ", split.Take(10).Select(group => Describe(group, summaries))) + (split.Count > 10 ? " …" : ""));
                    break;
                case "stamp" or "watermark" or "note":
                    if (string.IsNullOrWhiteSpace(op.Text) || op.Text.Length > PdfMarkup.MaxTextLength) { problems.Add($"書き込む文字を1～{PdfMarkup.MaxTextLength}文字で指定してください"); break; }
                    var markTargets = new HashSet<int>(Enumerable.Range(1, pages.Count));
                    if (!string.IsNullOrWhiteSpace(op.Pages) || op.Op == "note")
                    {
                        ParsePages(op.Pages ?? "", pages.Count, out markTargets, out var markError);
                        if (markError is not null) { problems.Add(markError); break; }
                    }
                    var position = PdfMarkup.NormalizePosition(op.Position, op.Op == "watermark" ? "center" : op.Op == "note" ? "top-left" : "top-right");
                    if (position is null) { problems.Add($"位置が読めません（{op.Position}）"); break; }
                    var (markStyle, styleProblem) = PdfTextStyle.From(op);
                    if (styleProblem is not null) { problems.Add(styleProblem); break; }
                    var mark = new PdfMark(op.Op, op.Text.Trim(), position, Style: op.Op == "note" ? null : markStyle);
                    pages = pages.Select((page, number) => markTargets.Contains(number + 1) ? page.With(mark) : page).ToList();
                    var markLook = mark.Style?.Describe() is { Length: > 0 } look ? "・" + look : "";
                    lines.Add($"{(markTargets.Count == pages.Count ? "全ページ" : Ranges(markTargets) + "ページ")}に{PdfMarkup.KindName(op.Op)}「{mark.Text}」（{PdfMarkup.PositionName(position)}{markLook}）");
                    break;
                case "ocr":
                    var ocrTargets = new HashSet<int>(Enumerable.Range(1, pages.Count));
                    if (!string.IsNullOrWhiteSpace(op.Pages))
                    {
                        ParsePages(op.Pages, pages.Count, out ocrTargets, out var ocrError);
                        if (ocrError is not null) { problems.Add(ocrError); break; }
                    }
                    // Only pages without text: OCR over existing text would put the words in twice.
                    ocrTargets = ocrTargets.Where(number => !pages[number - 1].Blank && summaries[pages[number - 1].File].Pages[pages[number - 1].Page - 1].Text.Trim().Length == 0).ToHashSet();
                    if (ocrTargets.Count == 0) { problems.Add("指定のページにはすでに文字があります（文字のないスキャンしたページだけを読み取ります）"); break; }
                    pages = pages.Select((page, number) => ocrTargets.Contains(number + 1) ? page.With(new PdfMark("ocr", "", "")) : page).ToList();
                    lines.Add($"{Ranges(ocrTargets)}ページの文字を読み取って（OCR）、検索・コピーできる文字を重ねる（見た目は変わりません）");
                    break;
                case "redact":
                    if (string.IsNullOrWhiteSpace(op.Text) || op.Text.Length > PdfMarkup.MaxTextLength) { problems.Add($"黒塗りする文字を1～{PdfMarkup.MaxTextLength}文字で指定してください"); break; }
                    var redactTargets = new HashSet<int>(Enumerable.Range(1, pages.Count));
                    if (!string.IsNullOrWhiteSpace(op.Pages))
                    {
                        ParsePages(op.Pages, pages.Count, out redactTargets, out var redactError);
                        if (redactError is not null) { problems.Add(redactError); break; }
                    }
                    // Only pages that contain the text (by their own letters); scanned pages are searched by OCR when the file is made.
                    var withText = redactTargets.Where(number => !pages[number - 1].Blank).Where(number =>
                    {
                        var source = summaries[pages[number - 1].File].Pages[pages[number - 1].Page - 1];
                        var text = PdfDocumentReader.Fingerprint(source.Text);
                        return text.Length == 0 || text.Contains(PdfDocumentReader.Fingerprint(op.Text), StringComparison.Ordinal);
                    }).ToHashSet();
                    if (withText.Count == 0) { problems.Add($"「{op.Text}」は指定のページに見つかりません"); break; }
                    if (withText.Any(number => pages[number - 1].Rotation != 0)) { problems.Add("回転しているページは黒塗りできません。先に回転を戻してください"); break; }
                    var redactMark = new PdfMark("redact", op.Text.Trim(), "");
                    pages = pages.Select((page, number) => withText.Contains(number + 1) ? page.With(redactMark) : page).ToList();
                    lines.Add($"{Ranges(withText)}ページの「{redactMark.Text}」を黒塗り（そのページは画像になり、文字の選択はできなくなります。塗った文字が読めないことをOCRで確かめます）");
                    break;
                case "to-images" when ops.Count == 1:
                    if (!string.IsNullOrWhiteSpace(op.Pages))
                    {
                        ParsePages(op.Pages, pages.Count, out var imageTargets, out var imageError);
                        if (imageError is not null) { problems.Add(imageError); break; }
                        pages = pages.Where((_, number) => imageTargets.Contains(number + 1)).ToList();
                    }
                    lines.Add($"{pages.Count}ページをPNG画像として保存（新しいフォルダーに、1ページ1ファイル・144dpi）");
                    break;
                case "to-images":
                    problems.Add("画像への変換は、ほかの操作と同時にはできません");
                    break;
                case "page-numbers":
                    var format = string.IsNullOrWhiteSpace(op.Text) ? "{n}" : op.Text.Trim();
                    if (!format.Contains("{n}", StringComparison.Ordinal) || format.Length > 40) { problems.Add("ページ番号の形には {n} を入れてください（例: {n} / {total}）"); break; }
                    var numberPosition = PdfMarkup.NormalizePosition(op.Position, "bottom");
                    if (numberPosition is null) { problems.Add($"位置が読めません（{op.Position}）"); break; }
                    var (numberStyle, numberStyleProblem) = PdfTextStyle.From(op);
                    if (numberStyleProblem is not null) { problems.Add(numberStyleProblem); break; }
                    var total = pages.Count;
                    pages = pages.Select((page, number) => page.With(new PdfMark("number", PdfMarkup.Number(format, number + 1, total), numberPosition, Style: numberStyle))).ToList();
                    var numberLook = numberStyle!.Describe() is { Length: > 0 } numberDescription ? "・" + numberDescription : "";
                    lines.Add($"全ページにページ番号「{PdfMarkup.Number(format, 1, total)}」…（{PdfMarkup.PositionName(numberPosition)}{numberLook}）");
                    break;
                case "export-text" or "export-docx" when ops.Count == 1:
                    lines.Add($"{pages.Count}ページの文字を{(op.Op == "export-text" ? "テキスト（.txt、UTF-8）" : "Word（.docx、1ページごとに改ページ）")}に書き出す" +
                              (pages.Any(page => !page.Blank && summaries[page.File].Pages[page.Page - 1].Text.Trim().Length == 0) ? "（文字のないページがあります。スキャンしたものは先に「OCRして」と依頼してください）" : ""));
                    break;
                case "export-text" or "export-docx":
                    problems.Add("書き出しは、ほかの操作と同時にはできません");
                    break;
                case "insert-blank":
                {
                    // Pages "3" = after page 3, "0" = at the start; Value = how many (1 when empty).
                    if (!int.TryParse(string.IsNullOrWhiteSpace(op.Pages) ? pages.Count.ToString(CultureInfo.InvariantCulture) : op.Pages.Trim(), out var after) || after < 0 || after > pages.Count)
                    { problems.Add($"白紙を入れる場所「{op.Pages}」が読めません（0～{pages.Count}）"); break; }
                    var blanks = int.TryParse(op.Value, out var blankCount) ? blankCount : 1;
                    if (blanks is < 1 or > 50) { problems.Add("白紙は 1～50 ページにしてください"); break; }
                    var neighbour = pages[Math.Max(0, Math.Min(pages.Count - 1, after - 1))];
                    var (blankWidth, blankHeight) = neighbour.Blank ? (neighbour.BlankWidth, neighbour.BlankHeight)
                        : (summaries[neighbour.File].Pages[neighbour.Page - 1].Width, summaries[neighbour.File].Pages[neighbour.Page - 1].Height);
                    pages.InsertRange(after, Enumerable.Range(0, blanks).Select(_ => new PdfPageRef(-1, 0, 0, null, blankWidth, blankHeight)));
                    lines.Add($"{(after == 0 ? "最初" : $"{after}ページ目の後")}に白紙を {blanks}ページ挿入（{PdfDocumentReader.SizeName(new PdfPageInfo(0, blankWidth, blankHeight, 0, ""))}、結果 {pages.Count}ページ）");
                    break;
                }
                case "bookmark":
                {
                    if (string.IsNullOrWhiteSpace(op.Text) || op.Text.Length > 100 || !int.TryParse(op.Pages, out var target) || target < 1 || target > pages.Count)
                    { problems.Add("しおりの名前（「」で）と、そのページ（1つ）を指定してください"); break; }
                    documentChanges.Add(op with { Text = op.Text.Trim(), Pages = target.ToString(CultureInfo.InvariantCulture) });
                    lines.Add($"しおり「{op.Text.Trim()}」→ {target}ページ目");
                    break;
                }
                case "crop":
                {
                    if (!double.TryParse(op.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var margin) || margin is <= 0 or > 200)
                    { problems.Add("切り取る余白を 1～200pt で指定してください"); break; }
                    var cropTargets = new HashSet<int>(Enumerable.Range(1, pages.Count));
                    if (!string.IsNullOrWhiteSpace(op.Pages)) { ParsePages(op.Pages, pages.Count, out cropTargets, out var cropError); if (cropError is not null) { problems.Add(cropError); break; } }
                    var cropMark = new PdfMark("crop", margin.ToString(CultureInfo.InvariantCulture), "");
                    pages = pages.Select((page, number) => cropTargets.Contains(number + 1) ? page.With(cropMark) : page).ToList();
                    lines.Add($"{(cropTargets.Count == pages.Count ? "全ページ" : Ranges(cropTargets) + "ページ")}の周りの余白を {margin:0.#}pt（約 {margin * 25.4 / 72:0.#}mm）切り取って表示（中身は消えません）");
                    break;
                }
                case "password":
                    if (string.IsNullOrEmpty(op.Value) || op.Value.Length is < 4 or > 64) { problems.Add("パスワードは「」で 4～64 文字にしてください"); break; }
                    if (index != ops.Count - 1) { problems.Add("パスワードの設定は最後の操作にしてください"); break; }
                    documentChanges.Add(op);
                    lines.Add("開くときのパスワードを設定（このあと Local Studio では編集を続けられません。保存してから開いて確かめてください）");
                    break;
                case "info" when op.Field is "title" or "author" or "subject" && op.Value is { Length: > 0 and <= 200 }:
                    documentChanges.Add(op);
                    lines.Add($"文書情報の{(op.Field == "title" ? "タイトル" : op.Field == "author" ? "作成者" : "サブタイトル")}を「{op.Value}」に");
                    break;
                case "info":
                    problems.Add("文書情報は title（タイトル）・author（作成者）・subject を、200文字までの値で指定してください");
                    break;
                case "fill" when !string.IsNullOrWhiteSpace(op.Field) && op.Value is { Length: <= 500 }:
                    var fields = PdfMarkup.FormFields(summaries[0].Path);
                    if (!fields.Contains(op.Field)) { problems.Add(fields.Count == 0 ? "このPDFには入力欄がありません" : $"入力欄「{op.Field}」がありません（ある欄: {string.Join("、", fields.Take(15))}）"); break; }
                    documentChanges.Add(op);
                    lines.Add($"入力欄「{op.Field}」に「{op.Value}」を入力");
                    break;
                case "fill":
                    problems.Add("入力欄の名前と入れる値を指定してください");
                    break;
                default:
                    problems.Add($"使えない操作です（{op.Op}）");
                    break;
            }
        }
        if (documentChanges.Any(op => op.Op == "fill") && ops.Any(op => op.Op is "merge" or "extract" or "delete" or "reorder" or "split"))
            problems.Add("入力欄への入力は、ページの結合・削除・並べ替え・分割と同時にはできません（入力欄が壊れるため）。別々に依頼してください");
        if (split is not null && ops.Any(op => op.Op == "page-numbers")) problems.Add("ページ番号と分割は同時にはできません。分割したあとのファイルで依頼してください");
        var outputs = split is not null ? split.Select(group => (IReadOnlyList<PdfPageRef>)group).ToArray() : [pages];
        if (split is null) lines.Add($"できるファイル: {pages.Count}ページ（{Describe(pages, summaries)}）");
        return new PdfEditPreview(Guid.NewGuid().ToString("N")[..12], summaries.Select(summary => summary.Path).ToArray(), ops, outputs, lines, problems,
            summaries.Select(summary => Hash(summary.Path)).ToArray()) { DocumentChanges = documentChanges };
    }

    /// <summary>"1-3,5" → {1,2,3,5}; error when the text is not a page list or a page is outside 1..count.</summary>
    public static bool ParsePages(string? text, int count, out HashSet<int> pages, out string? error)
    {
        pages = [];
        error = null;
        if (string.IsNullOrWhiteSpace(text)) { error = "ページが指定されていません"; return true; }
        foreach (var part in text.Replace('、', ',').Replace('〜', '-').Replace('~', '-').Replace('ー', '-').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = Regex.Match(part, @"^(\d+)(?:\s*-\s*(\d+))?$");
            if (!match.Success) { error = $"ページの指定が読めません（{part}）"; return true; }
            var first = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var last = match.Groups[2].Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : first;
            if (first < 1 || last > count || first > last) { error = $"{part}ページはありません（全{count}ページ）"; return true; }
            for (var number = first; number <= last; number++) pages.Add(number);
        }
        return true;
    }

    public static string Ranges(IEnumerable<int> numbers)
    {
        var sorted = numbers.Distinct().Order().ToArray();
        var parts = new List<string>();
        for (var start = 0; start < sorted.Length;)
        {
            var end = start;
            while (end + 1 < sorted.Length && sorted[end + 1] == sorted[end] + 1) end++;
            parts.Add(end == start ? sorted[start].ToString(CultureInfo.InvariantCulture) : $"{sorted[start]}-{sorted[end]}");
            start = end + 1;
        }
        return string.Join(",", parts);
    }

    /// <summary>"元1-3, 資料B 1-2" style list of where a result's pages come from.</summary>
    private static string Describe(IReadOnlyList<PdfPageRef> pages, IReadOnlyList<PdfSummary> summaries)
    {
        var parts = new List<string>();
        for (var start = 0; start < pages.Count;)
        {
            var end = start;
            if (pages[start].Blank)
            {
                while (end + 1 < pages.Count && pages[end + 1].Blank) end++;
                parts.Add($"白紙{end - start + 1}ページ");
                start = end + 1;
                continue;
            }
            while (end + 1 < pages.Count && pages[end + 1].File == pages[start].File && pages[end + 1].Page == pages[end].Page + 1) end++;
            var file = summaries.Count > 1 ? Path.GetFileNameWithoutExtension(summaries[pages[start].File].Path) + " " : "";
            parts.Add(file + (end == start ? $"p{pages[start].Page}" : $"p{pages[start].Page}-{pages[end].Page}"));
            start = end + 1;
        }
        var text = string.Join(", ", parts.Take(12)) + (parts.Count > 12 ? " …" : "");
        var rotated = pages.Count(page => page.Rotation != 0);
        return rotated == 0 ? text : text + $"、回転あり{rotated}ページ";
    }

    public static PdfEditResult Apply(PdfEditPreview preview) => ApplyAsync(preview, null).GetAwaiter().GetResult();

    public static async Task<PdfEditResult> ApplyAsync(PdfEditPreview preview, IPdfImaging? imaging, CancellationToken cancellationToken = default)
    {
        if (!preview.CanApply) throw new InvalidOperationException("確認できない操作があるため、ファイルを作りません。");
        for (var index = 0; index < preview.Sources.Count; index++)
            if (Hash(preview.Sources[index]) != preview.SourceSha256[index]) throw new InvalidOperationException("プレビューの後で元のファイルが変わったため、作りません。もう一度依頼してください。");
        if (PdfImagingSteps.NeedsImaging(preview) && imaging is null) throw new InvalidOperationException("この環境では、PDFを画像にする機能と文字の読み取り（OCR）を使えません。");
        if (preview.Ops is [{ Op: "export-text" or "export-docx" } export])
        {
            var texts = PdfDocumentReader.Read(preview.Sources[0]).Pages.Select(page => page.Text).ToArray();
            var exportPath = Path.ChangeExtension(UniqueOutputPath(preview.Sources[0], "_LS編集"), export.Op == "export-text" ? ".txt" : ".docx");
            if (export.Op == "export-text") File.WriteAllText(exportPath, string.Join("\f\n", texts), new UTF8Encoding(true));
            else PdfExport.WriteDocx(exportPath, texts);
            var exportProblems = PdfExport.Verify(exportPath, texts).ToList();
            return new PdfEditResult([exportPath], exportProblems);
        }
        if (preview.Ops.Any(op => op.Op == "to-images"))
        {
            var images = await PdfImagingSteps.WriteImagesAsync(preview, imaging!, cancellationToken);
            var imageProblems = PdfImagingSteps.VerifyImages(preview, images).ToList();
            if (Hash(preview.Sources[0]) != preview.SourceSha256[0]) imageProblems.Add("元のファイルが変わっています");
            return new PdfEditResult(images, imageProblems);
        }
        PdfMarkup.EnsureFont();
        var prepared = imaging is null ? new PdfImagingSteps.Prepared(preview, new Dictionary<(int, int), byte[]>()) : await PdfImagingSteps.PrepareAsync(preview, imaging, cancellationToken);
        preview = prepared.Preview;
        var sourceSizes = preview.Sources.Select(PdfDocumentReader.Read).Select(summary => summary.Pages.Select(page => (page.Width, page.Height)).ToArray()).ToArray();
        var first = preview.Sources[0];
        var kind = preview.Ops.Any(op => op.Op == "split") ? "分割" : preview.Ops.Any(op => op.Op == "merge") ? "結合" : "編集";
        var outputs = new List<string>();
        // Pages kept as they are: the file is opened as a whole (forms, bookmarks and links stay) and saved under a new
        // name. Otherwise the pages are copied into new files.
        Sharp.PdfDocument[] sources = preview.KeepsStructure ? [] : preview.Sources.Select(path => Sharp.IO.PdfReader.Open(path, Sharp.IO.PdfDocumentOpenMode.Import)).ToArray();
        try
        {
            for (var index = 0; index < preview.Outputs.Count; index++)
            {
                var output = UniqueOutputPath(first, preview.Outputs.Count == 1 ? $"_LS{kind}" : $"_LS{kind}_{index + 1:D2}");
                using var document = preview.KeepsStructure ? Sharp.IO.PdfReader.Open(first, Sharp.IO.PdfDocumentOpenMode.Modify) : new Sharp.PdfDocument();
                for (var position = 0; position < preview.Outputs[index].Count; position++)
                {
                    var page = preview.Outputs[index][position];
                    Sharp.PdfPage target;
                    if (prepared.PageImages.TryGetValue((index, position), out var redactedImage))
                    {
                        // Redaction: the page is replaced by its picture with the words painted black.
                        var (width, height) = sourceSizes[page.File][page.Page - 1];
                        if (preview.KeepsStructure)
                        {
                            var old = document.Pages[position];
                            target = PdfImagingSteps.ImagePage(document.Pages.Insert(position), width, height, redactedImage);
                            document.Pages.Remove(old);
                        }
                        else target = PdfImagingSteps.ImagePage(document.AddPage(), width, height, redactedImage);
                    }
                    else if (page.Blank)
                    {
                        target = document.AddPage();
                        target.Width = PdfSharp.Drawing.XUnit.FromPoint(page.BlankWidth);
                        target.Height = PdfSharp.Drawing.XUnit.FromPoint(page.BlankHeight);
                    }
                    else target = preview.KeepsStructure ? document.Pages[position] : document.AddPage(sources[page.File].Pages[page.Page - 1]);
                    if (page.Marks?.FirstOrDefault(mark => mark.Kind == "crop") is { } crop)
                    {
                        var margin = double.Parse(crop.Text, CultureInfo.InvariantCulture);
                        var box = target.MediaBox;
                        target.CropBox = new Sharp.PdfRectangle(new PdfSharp.Drawing.XPoint(box.X1 + margin, box.Y1 + margin), new PdfSharp.Drawing.XPoint(box.X2 - margin, box.Y2 - margin));
                    }
                    target.Rotate = page.Rotation;
                    if (page.Marks is { Count: > 0 } marks) PdfMarkup.Draw(document, target, marks);
                }
                PdfMarkup.ApplyDocumentChanges(document, preview.DocumentChanges);
                try
                {
                    document.Save(output);
                }
                catch
                {
                    // A half-written file is moved aside, never left looking like a result.
                    if (File.Exists(output)) File.Move(output, output + ".failed", overwrite: true);
                    throw;
                }
                outputs.Add(output);
            }
        }
        finally
        {
            foreach (var source in sources) source.Dispose();
        }
        var mismatches = VerifyOutputs(preview, outputs).ToList();
        if (imaging is not null) mismatches.AddRange(await PdfImagingSteps.VerifyRedactionsAsync(preview, outputs, imaging, cancellationToken));
        for (var index = 0; index < preview.Sources.Count; index++)
            if (Hash(preview.Sources[index]) != preview.SourceSha256[index]) mismatches.Add($"元のファイル「{Path.GetFileName(preview.Sources[index])}」が変わっています");
        return new PdfEditResult(outputs, mismatches);
    }

    /// <summary>
    /// Each result page must be the planned source page: same text (or size, for pages without text) and rotation, plus
    /// exactly the drawn text that was planned for it; notes, document information and form values are read back too.
    /// </summary>
    public static IEnumerable<string> VerifyOutputs(PdfEditPreview preview, IReadOnlyList<string> outputs)
    {
        // A protected result: it must not open without the password, and must open with it holding the planned pages.
        if (preview.DocumentChanges.FirstOrDefault(change => change.Op == "password") is { } password)
        {
            for (var index = 0; index < outputs.Count; index++)
            {
                var openWithout = true;
                try { using var plain = Pig.PdfDocument.Open(outputs[index]); _ = plain.NumberOfPages; } catch (Exception) { openWithout = false; }
                int? opened = null;
                try { using var locked = Pig.PdfDocument.Open(outputs[index], new Pig.ParsingOptions { Password = password.Value! }); opened = locked.NumberOfPages; } catch (Exception) { }
                if (openWithout) yield return $"{Path.GetFileName(outputs[index])} がパスワードなしで開けます";
                if (opened != preview.Outputs[index].Count) yield return $"{Path.GetFileName(outputs[index])} をパスワードで開けないか、ページ数が予定と違います";
            }
            yield break;
        }
        var sources = preview.Sources.Select(PdfDocumentReader.Read).ToArray();
        for (var index = 0; index < outputs.Count; index++)
        {
            PdfSummary result;
            string? failure = null;
            try { result = PdfDocumentReader.Read(outputs[index]); }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException) { result = null!; failure = $"{Path.GetFileName(outputs[index])} を読み直せません: {ex.Message}"; }
            if (failure is not null) { yield return failure; continue; }
            var expected = preview.Outputs[index];
            if (result.Pages.Count != expected.Count) { yield return $"{Path.GetFileName(outputs[index])}: ページ数が {result.Pages.Count}（予定 {expected.Count}）"; continue; }
            for (var position = 0; position < expected.Count; position++)
            {
                var planned = expected[position];
                if (planned.Blank)
                {
                    var blankActual = result.Pages[position];
                    // Only what was drawn on it (a page number) may be on a blank page.
                    var drawnOnBlank = (planned.Marks ?? []).Where(mark => mark.Kind is "stamp" or "number" or "watermark").Select(mark => PdfDocumentReader.Fingerprint(mark.Text)).ToArray();
                    var blankText = PdfDocumentReader.Fingerprint(blankActual.Text);
                    if (blankText.Length != drawnOnBlank.Sum(text => text.Length) || !drawnOnBlank.All(text => blankText.Contains(text, StringComparison.Ordinal)) ||
                        Math.Abs(blankActual.Width - (planned.BlankWidth - BlankCut(planned))) > 1 || Math.Abs(blankActual.Height - (planned.BlankHeight - BlankCut(planned))) > 1)
                        yield return $"{Path.GetFileName(outputs[index])} の{position + 1}ページ目が予定の白紙ページになっていません（大きさ {blankActual.Width:0}×{blankActual.Height:0}、予定 {planned.BlankWidth - BlankCut(planned):0}×{planned.BlankHeight - BlankCut(planned):0}、文字「{blankText}」）";
                    continue;
                }
                var source = sources[planned.File].Pages[planned.Page - 1];
                var actual = result.Pages[position];
                var drawn = (planned.Marks ?? []).Where(mark => mark.Kind is not ("note" or "redact" or "crop")).Select(mark => PdfDocumentReader.Fingerprint(mark.Text)).Where(text => text.Length > 0).ToArray();
                var actualText = PdfDocumentReader.Fingerprint(actual.Text);
                // A redacted page is a picture: none of the source text may remain.
                var sourceText = (planned.Marks ?? []).Any(mark => mark.Kind == "redact") ? "" : PdfDocumentReader.Fingerprint(source.Text);
                var sameText = drawn.Length == 0
                    ? actualText == sourceText
                    : actualText.Length == sourceText.Length + drawn.Sum(text => text.Length) && actualText.Contains(sourceText, StringComparison.Ordinal) && drawn.All(text => actualText.Contains(text, StringComparison.Ordinal));
                // A cropped page shows its page less the cut margin on every side.
                var cut = (planned.Marks ?? []).FirstOrDefault(mark => mark.Kind == "crop") is { } cropMark ? 2 * double.Parse(cropMark.Text, CultureInfo.InvariantCulture) : 0;
                var sameSize = Math.Abs(Math.Min(actual.Width, actual.Height) - (Math.Min(source.Width, source.Height) - cut)) < 1 &&
                               Math.Abs(Math.Max(actual.Width, actual.Height) - (Math.Max(source.Width, source.Height) - cut)) < 1;
                if (!sameText || !sameSize) yield return $"{Path.GetFileName(outputs[index])} の{position + 1}ページ目が予定のページ（元の{planned.Page}ページ目）と違います";
                else if (actual.Rotation != planned.Rotation) yield return $"{Path.GetFileName(outputs[index])} の{position + 1}ページ目の回転が {actual.Rotation}°（予定 {planned.Rotation}°）";
            }
            foreach (var problem in PdfMarkup.VerifyExtras(outputs[index], expected, preview.DocumentChanges)) yield return $"{Path.GetFileName(outputs[index])}: {problem}";
        }
    }

    private static double BlankCut(PdfPageRef page) =>
        (page.Marks ?? []).FirstOrDefault(mark => mark.Kind == "crop") is { } crop ? 2 * double.Parse(crop.Text, CultureInfo.InvariantCulture) : 0;

    private static string UniqueOutputPath(string source, string suffix)
    {
        var folder = Path.GetDirectoryName(source)!;
        var name = Path.GetFileNameWithoutExtension(source);
        for (var index = 1; index < 1000; index++)
        {
            var candidate = Path.Combine(folder, index == 1 ? $"{name}{suffix}.pdf" : $"{name}{suffix}({index}).pdf");
            if (!File.Exists(candidate)) return candidate;
        }
        throw new IOException("新しいファイル名を決められませんでした。");
    }

    private static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
