using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using P14 = DocumentFormat.OpenXml.Office2010.PowerPoint;

namespace SafeLocalHarness.Core;

/// <summary>One slide as read: its number in the show, title, every paragraph's text in order, and the speaker notes.</summary>
public sealed record SlideSummary(int Number, string Title, IReadOnlyList<string> Texts, string Notes);

/// <summary>
/// PowerPoint (phase D of the Office/Acrobat plan): slides read in show order, and text replaced, the look of chosen
/// words changed, slides added (built from the file's own layout placeholders, so they take the file's design),
/// deleted or moved, and speaker notes written. Sections (PowerPoint 2010+) are kept consistent with the slide list,
/// since a slide missing from them makes PowerPoint repair the file. Each operation is read back from the saved copy
/// against what the original says it should become.
/// </summary>
public static class PowerPointOperations
{
    public static readonly string[] All = ["replace", "format-text", "add-slide", "delete-slide", "move-slide", "notes"];

    // ---------- reading ----------

    public static IReadOnlyList<SlideSummary> ReadSlides(PresentationDocument document)
    {
        var part = document.PresentationPart ?? throw new InvalidDataException("スライドが見つかりません。");
        return SlideParts(part).Select((slide, index) => new SlideSummary(index + 1, TitleOf(slide), ParagraphTexts(slide.Slide!), NotesOf(slide))).ToArray();
    }

    internal static List<SlidePart> SlideParts(PresentationPart part) =>
        (part.Presentation!.SlideIdList?.Elements<P.SlideId>() ?? []).Select(id => id.RelationshipId?.Value is { } rel ? part.GetPartById(rel) as SlidePart : null).OfType<SlidePart>().ToList();

    private static string[] ParagraphTexts(OpenXmlElement root) =>
        root.Descendants<A.Paragraph>().Select(ParagraphText).Where(text => text.Trim().Length > 0).ToArray();

    private static string ParagraphText(A.Paragraph paragraph) => string.Concat(paragraph.Descendants<A.Text>().Select(text => text.Text));

    private static P.PlaceholderShape? Placeholder(P.Shape shape) => shape.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?.PlaceholderShape;

    private static bool IsTitle(P.PlaceholderShape? placeholder) =>
        placeholder?.Type?.Value is { } type && (type == P.PlaceholderValues.Title || type == P.PlaceholderValues.CenteredTitle);

    private static bool IsBody(P.PlaceholderShape? placeholder) =>
        placeholder is not null && !IsTitle(placeholder) &&
        (placeholder.Type is null || placeholder.Type.Value == P.PlaceholderValues.Body || placeholder.Type.Value == P.PlaceholderValues.Object || placeholder.Type.Value == P.PlaceholderValues.SubTitle);

    private static string TitleOf(SlidePart slide) =>
        slide.Slide!.Descendants<P.Shape>().Where(shape => IsTitle(Placeholder(shape))).Select(shape => string.Join(" ", ParagraphTexts(shape))).FirstOrDefault() ?? "";

    private static string NotesOf(SlidePart slide) =>
        slide.NotesSlidePart?.NotesSlide is { } notes
            ? string.Join("\n", notes.Descendants<P.Shape>().Where(shape => Placeholder(shape)?.Type?.Value == P.PlaceholderValues.Body).SelectMany(ParagraphTexts))
            : "";

    // ---------- preview ----------

    public static OfficeChange Describe(PresentationDocument document, OfficeEdit edit, int editCount)
    {
        var slides = ReadSlides(document);
        bool SlideNumber(string? value, out int number) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) && number >= 1 && number <= slides.Count;
        if (edit.Op != "replace" && editCount > 1) return new($"「{edit.Op}」は、ほかの変更と一緒にはできません（1つずつ依頼してください）", "invalid");
        switch (edit.Op)
        {
            case "replace":
            {
                if (string.IsNullOrEmpty(edit.Find) || edit.With is null) return new("置き換える文字を指定してください", "invalid");
                var hits = slides.Select(slide => (slide.Number, Count: slide.Texts.Sum(text => Count(text, edit.Find)))).Where(item => item.Count > 0).ToArray();
                return hits.Length == 0
                    ? new($"「{edit.Find}」→「{edit.With}」", "not-found")
                    : new($"「{edit.Find}」→「{edit.With}」（{hits.Sum(item => item.Count)}か所: {string.Join("、", hits.Select(item => item.Number + "枚目"))}）", "ok");
            }
            case "format-text":
            {
                if (string.IsNullOrEmpty(edit.Find)) return new("書式を変える文字を指定してください", "invalid");
                var look = LookText(edit);
                if (look.Length == 0) return new("どんな書式にするか（太字・色・大きさ・フォントなど）を指定してください", "invalid");
                var hits = slides.Where(slide => slide.Texts.Any(text => text.Contains(edit.Find, StringComparison.Ordinal))).Select(slide => slide.Number + "枚目").ToArray();
                return hits.Length == 0 ? new($"「{edit.Find}」を{look}", "not-found") : new($"「{edit.Find}」を{look}（{string.Join("、", hits)}）", "ok");
            }
            case "add-slide":
            {
                if (string.IsNullOrWhiteSpace(edit.Text)) return new("新しいスライドのタイトルを指定してください", "invalid");
                var after = AfterSlide(edit, slides.Count);
                if (after is null) return new($"スライドを入れる位置が分かりません（スライドは{slides.Count}枚です）", "invalid");
                if (ChooseLayout(document.PresentationPart!, null) is null) return new("このファイルにはタイトル付きのレイアウトがないため、スライドを追加できません", "invalid");
                var place = after == 0 ? "最初" : after == slides.Count ? "最後" : $"{after}枚目の後";
                var body = edit.Values is { Count: > 0 } ? $"、本文「{string.Join(" / ", edit.Values)}」" : "";
                return new($"{place}に、タイトル「{edit.Text}」{body}のスライドを追加（{slides.Count}枚 → {slides.Count + 1}枚。デザインはファイルのレイアウトのまま）", "ok");
            }
            case "delete-slide":
                if (!SlideNumber(edit.Rows, out var deleted)) return new($"削除するスライドの番号が正しくありません（スライドは{slides.Count}枚です）", "invalid");
                if (slides.Count == 1) return new("スライドが1枚しかないため削除できません", "invalid");
                return new($"{deleted}枚目のスライド「{Short(slides[deleted - 1].Title, slides[deleted - 1].Texts)}」を削除（{slides.Count}枚 → {slides.Count - 1}枚）", "ok");
            case "move-slide":
                if (!SlideNumber(edit.Rows, out var from) || !SlideNumber(edit.Position, out var to)) return new($"移動するスライドの番号が正しくありません（スライドは{slides.Count}枚です）", "invalid");
                if (from == to) return new($"{from}枚目はもうその位置にあります", "invalid");
                return new($"{from}枚目のスライド「{Short(slides[from - 1].Title, slides[from - 1].Texts)}」を{to}枚目に移動", "ok");
            case "notes":
                if (!SlideNumber(edit.Rows, out var noted)) return new($"ノートを書くスライドの番号が正しくありません（スライドは{slides.Count}枚です）", "invalid");
                if (edit.Text is null) return new("ノートに書く文字を指定してください", "invalid");
                var existing = slides[noted - 1].Notes;
                return new($"{noted}枚目のノートを「{edit.Text}」にする（今のノート: {(existing.Length == 0 ? "なし" : "「" + Short(existing, []) + "」")}）", "ok");
        }
        return new($"PowerPoint では使えない変更です（{edit.Op}）", "invalid");
    }

    private static string Short(string title, IReadOnlyList<string> texts)
    {
        var text = title.Length > 0 ? title : texts.FirstOrDefault() ?? "（文字なし）";
        return text.Length <= 30 ? text : text[..30] + "…";
    }

    /// <summary>The slide number a new slide goes after: rows "0" = first, a number = after it, none = last.</summary>
    private static int? AfterSlide(OfficeEdit edit, int count)
    {
        if (edit.Position is "start" or "first") return 0;
        if (string.IsNullOrEmpty(edit.Rows) || edit.Position is "end" or "last") return count;
        return int.TryParse(edit.Rows, NumberStyles.Integer, CultureInfo.InvariantCulture, out var after) && after >= 0 && after <= count ? after : null;
    }

    private static string LookText(OfficeEdit edit)
    {
        var parts = new List<string>();
        if (edit.Bold == true) parts.Add("太字");
        if (edit.Italic == true) parts.Add("斜体");
        if (edit.Underline == true) parts.Add("下線");
        if (edit.Color is { Length: > 0 } && ExcelFormatting.Argb(edit.Color) is not null) parts.Add($"文字色{edit.Color}");
        if (edit.Size is { } size and > 0 and <= 400) parts.Add($"{size.ToString(CultureInfo.InvariantCulture)}pt");
        if (FontOf(edit.Font) is { Length: > 0 } font) parts.Add(font);
        return parts.Count == 0 ? "" : string.Join("・", parts) + "に";
    }

    private static string FontOf(string? font) => font switch { "mincho" or "明朝" or "明朝体" => "游明朝", "ud" or "UD" or "教科書体" => "UD デジタル 教科書体 N-R", "gothic" or "ゴシック" or "ゴシック体" => "游ゴシック", _ => "" };

    // ---------- apply ----------

    public static void Apply(PresentationDocument document, OfficeEdit edit)
    {
        var part = document.PresentationPart!;
        var presentation = part.Presentation!;
        var slides = SlideParts(part);
        switch (edit.Op)
        {
            case "replace":
                foreach (var slide in slides)
                {
                    foreach (var paragraph in slide.Slide!.Descendants<A.Paragraph>())
                    {
                        if (!ParagraphText(paragraph).Contains(edit.Find!, StringComparison.Ordinal)) continue;
                        // Inside one run the look stays; text split across runs is joined into the first run.
                        foreach (var text in paragraph.Descendants<A.Text>()) text.Text = text.Text.Replace(edit.Find!, edit.With!, StringComparison.Ordinal);
                        if (!ParagraphText(paragraph).Contains(edit.Find!, StringComparison.Ordinal)) continue;
                        var texts = paragraph.Descendants<A.Text>().ToArray();
                        texts[0].Text = string.Concat(texts.Select(text => text.Text)).Replace(edit.Find!, edit.With!, StringComparison.Ordinal);
                        foreach (var text in texts.Skip(1)) text.Text = "";
                    }
                    slide.Slide.Save();
                }
                break;
            case "format-text":
                foreach (var slide in slides)
                {
                    foreach (var paragraph in slide.Slide!.Descendants<A.Paragraph>().ToArray())
                        foreach (var run in IsolateRuns(paragraph, edit.Find!)) SetLook(run, edit);
                    slide.Slide.Save();
                }
                break;
            case "add-slide":
            {
                var after = AfterSlide(edit, slides.Count)!.Value;
                var layout = ChooseLayout(part, after > 0 ? slides[after - 1] : slides.FirstOrDefault())!;
                var slidePart = part.AddNewPart<SlidePart>();
                slidePart.Slide = BuildSlide(layout, edit.Text!, edit.Values ?? []);
                slidePart.AddPart(layout);
                slidePart.Slide.Save();
                var list = presentation.SlideIdList ??= InsertSlideIdList(presentation);
                var id = Math.Max(256u, list.Elements<P.SlideId>().Select(item => item.Id?.Value ?? 0u).DefaultIfEmpty(255u).Max() + 1);
                var slideId = new P.SlideId { Id = id, RelationshipId = part.GetIdOfPart(slidePart) };
                var previous = after > 0 ? list.Elements<P.SlideId>().ElementAt(after - 1) : null;
                if (previous is null) list.PrependChild(slideId); else list.InsertAfter(slideId, previous);
                PlaceInSections(presentation, id, previous?.Id?.Value);
                presentation.Save();
                break;
            }
            case "delete-slide":
            {
                var number = int.Parse(edit.Rows!, CultureInfo.InvariantCulture);
                var slideId = presentation.SlideIdList!.Elements<P.SlideId>().ElementAt(number - 1);
                var slidePart = (SlidePart)part.GetPartById(slideId.RelationshipId!.Value!);
                var id = slideId.Id!.Value;
                slideId.Remove();
                foreach (var entry in presentation.Descendants<P14.SectionSlideIdListEntry>().Where(entry => entry.Id?.Value == id).ToArray()) entry.Remove();
                // A custom show listing the slide would point at nothing.
                foreach (var shown in presentation.CustomShowList?.Descendants<P.SlideListEntry>().Where(entry => entry.Id?.Value == slideId.RelationshipId!.Value).ToArray() ?? []) shown.Remove();
                presentation.Save();
                part.DeletePart(slidePart);
                break;
            }
            case "move-slide":
            {
                var from = int.Parse(edit.Rows!, CultureInfo.InvariantCulture);
                var to = int.Parse(edit.Position!, CultureInfo.InvariantCulture);
                var list = presentation.SlideIdList!;
                var moving = list.Elements<P.SlideId>().ElementAt(from - 1);
                moving.Remove();
                var others = list.Elements<P.SlideId>().ToList();
                var previous = to > 1 ? others[to - 2] : null;
                if (previous is null) list.PrependChild(moving); else list.InsertAfter(moving, previous);
                PlaceInSections(presentation, moving.Id!.Value, previous?.Id?.Value);
                presentation.Save();
                break;
            }
            case "notes":
            {
                var slide = slides[int.Parse(edit.Rows!, CultureInfo.InvariantCulture) - 1];
                var notes = slide.NotesSlidePart ?? CreateNotes(part, slide);
                var body = notes.NotesSlide!.Descendants<P.Shape>().FirstOrDefault(shape => Placeholder(shape)?.Type?.Value == P.PlaceholderValues.Body);
                if (body is null)
                {
                    body = PlaceholderShape(NextShapeId(notes.NotesSlide), "Notes Placeholder", P.PlaceholderValues.Body, 1, []);
                    notes.NotesSlide.CommonSlideData!.ShapeTree!.Append(body);
                }
                body.TextBody ??= new P.TextBody(new A.BodyProperties(), new A.ListStyle());
                foreach (var paragraph in body.TextBody.Elements<A.Paragraph>().ToArray()) paragraph.Remove();
                foreach (var line in edit.Text!.Replace("\r\n", "\n").Split('\n')) body.TextBody.Append(Paragraph(line));
                notes.NotesSlide.Save();
                break;
            }
        }
    }

    private static P.SlideIdList InsertSlideIdList(P.Presentation presentation)
    {
        var list = new P.SlideIdList();
        OpenXmlElement? anchor = (OpenXmlElement?)presentation.HandoutMasterIdList ?? (OpenXmlElement?)presentation.NotesMasterIdList ?? presentation.SlideMasterIdList;
        if (anchor is null) presentation.PrependChild(list); else presentation.InsertAfter(list, anchor);
        return list;
    }

    /// <summary>
    /// Sections list every slide once. A new or moved slide joins the section of the slide now before it (right after
    /// it), or the start of the first section when it comes first.
    /// </summary>
    private static void PlaceInSections(P.Presentation presentation, uint id, uint? previousId)
    {
        var sections = presentation.Descendants<P14.Section>().ToArray();
        if (sections.Length == 0) return;
        foreach (var entry in presentation.Descendants<P14.SectionSlideIdListEntry>().Where(entry => entry.Id?.Value == id).ToArray()) entry.Remove();
        var created = new P14.SectionSlideIdListEntry { Id = id };
        var previous = previousId is null ? null : presentation.Descendants<P14.SectionSlideIdListEntry>().FirstOrDefault(entry => entry.Id?.Value == previousId);
        if (previous is not null) { previous.InsertAfterSelf(created); return; }
        var list = sections[0].SectionSlideIdList ??= new P14.SectionSlideIdList();
        list.PrependChild(created);
    }

    /// <summary>The layout of the slide before (when it has a title), else a "title and content" layout, else any with a title.</summary>
    private static SlideLayoutPart? ChooseLayout(PresentationPart part, SlidePart? near)
    {
        static bool HasTitle(SlideLayoutPart layout) => layout.SlideLayout?.Descendants<P.Shape>().Any(shape => IsTitle(Placeholder(shape))) == true;
        static bool HasBody(SlideLayoutPart layout) => layout.SlideLayout?.Descendants<P.Shape>().Any(shape => IsBody(Placeholder(shape))) == true;
        if (near?.SlideLayoutPart is { } nearLayout && HasTitle(nearLayout) && HasBody(nearLayout) && nearLayout.SlideLayout!.Type?.Value != P.SlideLayoutValues.Title) return nearLayout;
        var layouts = part.SlideMasterParts.SelectMany(master => master.SlideLayoutParts).ToArray();
        return layouts.FirstOrDefault(layout => layout.SlideLayout!.Type?.Value == P.SlideLayoutValues.Object && HasTitle(layout) && HasBody(layout))
            ?? layouts.FirstOrDefault(layout => HasTitle(layout) && HasBody(layout))
            ?? (near?.SlideLayoutPart is { } any && HasTitle(any) ? any : null)
            ?? layouts.FirstOrDefault(HasTitle);
    }

    /// <summary>A slide whose shapes are the layout's title and first body placeholder (positions and looks inherited).</summary>
    private static P.Slide BuildSlide(SlideLayoutPart layout, string title, IReadOnlyList<string> body)
    {
        var tree = new P.ShapeTree(GroupRoot());
        uint id = 2;
        var layoutShapes = layout.SlideLayout!.Descendants<P.Shape>().ToArray();
        var titlePlaceholder = Placeholder(layoutShapes.First(shape => IsTitle(Placeholder(shape))))!;
        tree.Append(PlaceholderShape(id++, "Title", titlePlaceholder.Type!.Value, titlePlaceholder.Index?.Value, [title]));
        if (layoutShapes.Select(Placeholder).FirstOrDefault(IsBody) is { } bodyPlaceholder)
            tree.Append(PlaceholderShape(id, "Content", bodyPlaceholder.Type?.Value, bodyPlaceholder.Index?.Value, body.Count > 0 ? body : [""]));
        else if (body.Count > 0)
            throw new InvalidOperationException("このレイアウトには本文の枠がありません。");
        return new P.Slide(new P.CommonSlideData(tree), new P.ColorMapOverride(new A.MasterColorMapping()));
    }

    private static P.NonVisualGroupShapeProperties GroupRootProperties() =>
        new(new P.NonVisualDrawingProperties { Id = 1u, Name = "" }, new P.NonVisualGroupShapeDrawingProperties(), new P.ApplicationNonVisualDrawingProperties());

    private static OpenXmlElement[] GroupRoot() => [GroupRootProperties(), new P.GroupShapeProperties(new A.TransformGroup())];

    private static P.Shape PlaceholderShape(uint id, string name, P.PlaceholderValues? type, uint? index, IReadOnlyList<string> paragraphs, A.Transform2D? position = null)
    {
        var placeholder = new P.PlaceholderShape();
        if (type is { } value) placeholder.Type = value;
        if (index is { } number) placeholder.Index = number;
        var shape = new P.Shape(
            new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = id, Name = name },
                new P.NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true }),
                new P.ApplicationNonVisualDrawingProperties(placeholder)),
            position is null ? new P.ShapeProperties() : new P.ShapeProperties(position),
            new P.TextBody(new A.BodyProperties(), new A.ListStyle()));
        foreach (var line in paragraphs) shape.TextBody!.Append(Paragraph(line));
        if (!shape.TextBody!.Elements<A.Paragraph>().Any()) shape.TextBody.Append(Paragraph(""));
        return shape;
    }

    private static A.Paragraph Paragraph(string text) =>
        text.Length == 0
            ? new A.Paragraph(new A.EndParagraphRunProperties { Language = "ja-JP" })
            : new A.Paragraph(new A.Run(new A.RunProperties { Language = "ja-JP" }, new A.Text(text)));

    private static uint NextShapeId(OpenXmlElement root) =>
        root.Descendants<P.NonVisualDrawingProperties>().Select(item => item.Id?.Value ?? 0u).DefaultIfEmpty(1u).Max() + 1;

    /// <summary>A notes page for a slide; a notes master is added first when the file has none.</summary>
    private static NotesSlidePart CreateNotes(PresentationPart part, SlidePart slide)
    {
        var master = part.NotesMasterPart ?? CreateNotesMaster(part);
        var notes = slide.AddNewPart<NotesSlidePart>();
        notes.NotesSlide = new P.NotesSlide(
            new P.CommonSlideData(new P.ShapeTree(GroupRoot().Concat<OpenXmlElement>([
                PlaceholderShape(2, "Slide Image Placeholder", P.PlaceholderValues.SlideImage, null, []),
                PlaceholderShape(3, "Notes Placeholder", P.PlaceholderValues.Body, 1, [])]))),
            new P.ColorMapOverride(new A.MasterColorMapping()));
        // A slide image placeholder holds no text.
        notes.NotesSlide.Descendants<P.Shape>().First().TextBody!.Remove();
        notes.AddPart(master);
        notes.AddPart(slide);
        notes.NotesSlide.Save();
        return notes;
    }

    private static NotesMasterPart CreateNotesMaster(PresentationPart part)
    {
        var master = part.AddNewPart<NotesMasterPart>();
        master.NotesMaster = new P.NotesMaster(
            new P.CommonSlideData(new P.ShapeTree(GroupRoot().Concat<OpenXmlElement>([
                PlaceholderShape(2, "Slide Image Placeholder", P.PlaceholderValues.SlideImage, null, [], Box(1143000, 685800, 4572000, 3429000)),
                PlaceholderShape(3, "Notes Placeholder", P.PlaceholderValues.Body, 1, [""], Box(685800, 4343400, 5486400, 4114800))]))),
            NewColorMap());
        master.NotesMaster.Descendants<P.Shape>().First().TextBody!.Remove();
        var theme = master.AddNewPart<ThemePart>();
        theme.Theme = NewTheme();
        var presentation = part.Presentation!;
        var list = new P.NotesMasterIdList(new P.NotesMasterId { Id = part.GetIdOfPart(master) });
        presentation.InsertAfter(list, presentation.SlideMasterIdList!);
        presentation.NotesSize ??= new P.NotesSize { Cx = 6858000, Cy = 9144000 };
        master.NotesMaster.Save();
        return master;
    }

    private static A.Transform2D Box(long x, long y, long width, long height) =>
        new(new A.Offset { X = x, Y = y }, new A.Extents { Cx = width, Cy = height });

    // ---------- the look of chosen words ----------

    /// <summary>
    /// The runs that hold exactly the occurrences of <paramref name="find"/> in a paragraph: runs are split at the
    /// occurrence edges (each part keeps the original run's look), so only the asked words change.
    /// </summary>
    private static List<A.Run> IsolateRuns(A.Paragraph paragraph, string find)
    {
        var result = new List<A.Run>();
        var runs = paragraph.Elements<A.Run>().ToList();
        var text = string.Concat(runs.Select(run => run.Text?.Text ?? ""));
        var ranges = new List<(int Start, int End)>();
        for (var start = text.IndexOf(find, StringComparison.Ordinal); start >= 0; start = text.IndexOf(find, start + find.Length, StringComparison.Ordinal)) ranges.Add((start, start + find.Length));
        if (ranges.Count == 0) return result;
        var offset = 0;
        foreach (var run in runs)
        {
            var value = run.Text?.Text ?? "";
            var cuts = new SortedSet<int> { 0, value.Length };
            foreach (var (start, end) in ranges)
            {
                if (start - offset > 0 && start - offset < value.Length) cuts.Add(start - offset);
                if (end - offset > 0 && end - offset < value.Length) cuts.Add(end - offset);
            }
            var edges = cuts.ToArray();
            OpenXmlElement last = run;
            for (var piece = 0; piece < edges.Length - 1; piece++)
            {
                var part = value[edges[piece]..edges[piece + 1]];
                var target = piece == 0 ? run : (A.Run)run.CloneNode(true);
                target.Text!.Text = part;
                if (piece > 0) { last.InsertAfterSelf(target); last = target; }
                var absolute = offset + edges[piece];
                if (ranges.Any(range => absolute >= range.Start && absolute < range.End)) result.Add(target);
            }
            offset += value.Length;
        }
        return result;
    }

    private static void SetLook(A.Run run, OfficeEdit edit)
    {
        var properties = run.RunProperties ??= new A.RunProperties { Language = "ja-JP" };
        if (edit.Bold == true) properties.Bold = true;
        if (edit.Italic == true) properties.Italic = true;
        if (edit.Underline == true) properties.Underline = A.TextUnderlineValues.Single;
        if (edit.Size is { } size and > 0 and <= 400) properties.FontSize = (int)Math.Round(size * 100);
        if (ExcelFormatting.Argb(edit.Color) is { } colour)
        {
            foreach (var fill in properties.Elements().Where(element => element is A.NoFill or A.SolidFill or A.GradientFill or A.BlipFill or A.PatternFill or A.GroupFill).ToArray()) fill.Remove();
            var solid = new A.SolidFill(new A.RgbColorModelHex { Val = colour[2..] });
            if (properties.GetFirstChild<A.Outline>() is { } outline) outline.InsertAfterSelf(solid); else properties.PrependChild(solid);
        }
        if (FontOf(edit.Font) is { Length: > 0 } font)
        {
            properties.GetFirstChild<A.LatinFont>()?.Remove();
            properties.GetFirstChild<A.EastAsianFont>()?.Remove();
            var latin = new A.LatinFont { Typeface = font };
            var eastAsian = new A.EastAsianFont { Typeface = font };
            var before = properties.Elements().FirstOrDefault(element => element is A.ComplexScriptFont or A.SymbolFont or A.HyperlinkOnClick or A.HyperlinkOnMouseOver or A.RightToLeft or A.ExtensionList);
            if (before is null) { properties.Append(latin); properties.Append(eastAsian); }
            else { before.InsertBeforeSelf(latin); before.InsertBeforeSelf(eastAsian); }
        }
    }

    private static bool HasLook(A.Run run, OfficeEdit edit)
    {
        var properties = run.RunProperties;
        if (properties is null) return false;
        if (edit.Bold == true && properties.Bold?.Value != true) return false;
        if (edit.Italic == true && properties.Italic?.Value != true) return false;
        if (edit.Underline == true && properties.Underline?.Value != A.TextUnderlineValues.Single) return false;
        if (edit.Size is { } size and > 0 and <= 400 && properties.FontSize?.Value != (int)Math.Round(size * 100)) return false;
        if (ExcelFormatting.Argb(edit.Color) is { } colour && properties.GetFirstChild<A.SolidFill>()?.RgbColorModelHex?.Val?.Value != colour[2..]) return false;
        if (FontOf(edit.Font) is { Length: > 0 } font && properties.GetFirstChild<A.EastAsianFont>()?.Typeface?.Value != font) return false;
        return true;
    }

    // ---------- read-back ----------

    public static IEnumerable<string> Verify(string sourcePath, string outputPath, OfficeEdit edit)
    {
        using var beforeStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var before = PresentationDocument.Open(beforeStream, false);
        using var after = PresentationDocument.Open(outputPath, false);
        var oldSlides = ReadSlides(before);
        var newSlides = ReadSlides(after);
        static string Key(SlideSummary slide) => string.Join("\u0001", slide.Texts);
        var problems = new List<string>();
        switch (edit.Op)
        {
            case "replace":
            {
                var all = string.Join("\n", newSlides.SelectMany(slide => slide.Texts));
                if (newSlides.Count != oldSlides.Count) problems.Add("スライドの枚数が変わっています");
                if (!edit.With!.Contains(edit.Find!, StringComparison.Ordinal) && all.Contains(edit.Find!, StringComparison.Ordinal)) problems.Add($"「{edit.Find}」が残っています");
                if (edit.With!.Length > 0 && !all.Contains(edit.With, StringComparison.Ordinal)) problems.Add($"「{edit.With}」が見つかりません");
                // Every other text is as it was.
                var expected = oldSlides.Select(slide => string.Join("\u0001", slide.Texts.Select(text => text.Replace(edit.Find!, edit.With!, StringComparison.Ordinal)).Where(text => text.Trim().Length > 0)));
                if (!expected.SequenceEqual(newSlides.Select(Key))) problems.Add("置き換えたあとの文字が予定と違います");
                break;
            }
            case "format-text":
            {
                if (!oldSlides.Select(Key).SequenceEqual(newSlides.Select(Key))) problems.Add("書式を変えたあとで文字が変わっています");
                foreach (var slide in SlideParts(after.PresentationPart!))
                    foreach (var paragraph in slide.Slide!.Descendants<A.Paragraph>().Where(item => ParagraphText(item).Contains(edit.Find!, StringComparison.Ordinal)))
                    {
                        var runs = paragraph.Elements<A.Run>().ToList();
                        var text = string.Concat(runs.Select(run => run.Text?.Text ?? ""));
                        for (var start = text.IndexOf(edit.Find!, StringComparison.Ordinal); start >= 0; start = text.IndexOf(edit.Find!, start + edit.Find!.Length, StringComparison.Ordinal))
                        {
                            var offset = 0;
                            foreach (var run in runs)
                            {
                                var length = (run.Text?.Text ?? "").Length;
                                if (offset < start + edit.Find!.Length && offset + length > start && !HasLook(run, edit)) { problems.Add($"「{edit.Find}」の書式が予定どおりになっていません"); return problems; }
                                offset += length;
                            }
                        }
                    }
                break;
            }
            case "add-slide":
            {
                var at = AfterSlide(edit, oldSlides.Count)!.Value;
                var expected = oldSlides.Select(Key).ToList();
                var added = string.Join("\u0001", new[] { edit.Text! }.Concat(edit.Values ?? []).Where(text => text.Trim().Length > 0));
                expected.Insert(at, added);
                if (!expected.SequenceEqual(newSlides.Select(Key))) problems.Add("追加したスライドの位置か中身が予定と違います");
                else if (newSlides[at].Title != edit.Text) problems.Add($"追加したスライドのタイトルが「{edit.Text}」になっていません");
                break;
            }
            case "delete-slide":
            {
                var expected = oldSlides.Select(Key).ToList();
                expected.RemoveAt(int.Parse(edit.Rows!, CultureInfo.InvariantCulture) - 1);
                if (!expected.SequenceEqual(newSlides.Select(Key))) problems.Add("スライドの削除が予定どおりになっていません");
                break;
            }
            case "move-slide":
            {
                var expected = oldSlides.Select(Key).ToList();
                var moving = expected[int.Parse(edit.Rows!, CultureInfo.InvariantCulture) - 1];
                expected.RemoveAt(int.Parse(edit.Rows!, CultureInfo.InvariantCulture) - 1);
                expected.Insert(int.Parse(edit.Position!, CultureInfo.InvariantCulture) - 1, moving);
                if (!expected.SequenceEqual(newSlides.Select(Key))) problems.Add("スライドの並びが予定どおりになっていません");
                break;
            }
            case "notes":
            {
                var number = int.Parse(edit.Rows!, CultureInfo.InvariantCulture);
                if (!oldSlides.Select(Key).SequenceEqual(newSlides.Select(Key))) problems.Add("ノートを書いたあとでスライドの文字が変わっています");
                if (newSlides.Count < number || newSlides[number - 1].Notes != edit.Text!.Replace("\r\n", "\n")) problems.Add($"{number}枚目のノートが「{edit.Text}」になっていません");
                if (oldSlides.Where(slide => slide.Number != number).Any(slide => newSlides[slide.Number - 1].Notes != slide.Notes)) problems.Add("ほかのスライドのノートが変わっています");
                break;
            }
        }
        // Sections (when present) list every slide exactly once.
        var presentation = after.PresentationPart!.Presentation!;
        var entries = presentation.Descendants<P14.SectionSlideIdListEntry>().Select(entry => entry.Id?.Value ?? 0).ToArray();
        if (presentation.Descendants<P14.Section>().Any())
        {
            var ids = presentation.SlideIdList?.Elements<P.SlideId>().Select(id => id.Id?.Value ?? 0).ToArray() ?? [];
            if (entries.Length != ids.Length || entries.Except(ids).Any() || ids.Except(entries).Any()) problems.Add("セクションとスライドの対応が崩れています");
        }
        return problems;
    }

    private static int Count(string text, string find)
    {
        var count = 0;
        for (var index = text.IndexOf(find, StringComparison.Ordinal); index >= 0; index = text.IndexOf(find, index + find.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    // ---------- a new presentation (drills and tests) ----------

    /// <summary>
    /// A plain 16:9 presentation with one master, a "title and content" layout, a theme and a notes master, and the
    /// given slides (title, body lines, notes). Used to make drill and test files; it is not a design for users' work.
    /// </summary>
    public static void Create(string path, IReadOnlyList<(string Title, IReadOnlyList<string> Body, string? Notes)> slides)
    {
        using var document = PresentationDocument.Create(path, PresentationDocumentType.Presentation);
        var part = document.AddPresentationPart();
        part.Presentation = new P.Presentation(
            new P.SlideMasterIdList(new P.SlideMasterId { Id = 2147483648u, RelationshipId = "rId1" }),
            new P.SlideIdList(),
            new P.SlideSize { Cx = 12192000, Cy = 6858000 },
            new P.NotesSize { Cx = 6858000, Cy = 9144000 },
            new P.DefaultTextStyle());
        var master = part.AddNewPart<SlideMasterPart>("rId1");
        master.SlideMaster = new P.SlideMaster(
            new P.CommonSlideData(new P.ShapeTree(GroupRoot().Concat<OpenXmlElement>([
                PlaceholderShape(2, "Title Placeholder", P.PlaceholderValues.Title, null, [""], Box(838200, 365125, 10515600, 1325563)),
                PlaceholderShape(3, "Text Placeholder", P.PlaceholderValues.Body, 1, [""], Box(838200, 1825625, 10515600, 4351338))]))),
            NewColorMap(),
            new P.SlideLayoutIdList(new P.SlideLayoutId { Id = 2147483649u, RelationshipId = "rId1" }),
            new P.TextStyles(new P.TitleStyle(), new P.BodyStyle(), new P.OtherStyle()));
        var layout = master.AddNewPart<SlideLayoutPart>("rId1");
        layout.SlideLayout = new P.SlideLayout(
            new P.CommonSlideData(new P.ShapeTree(GroupRoot().Concat<OpenXmlElement>([
                PlaceholderShape(2, "Title 1", P.PlaceholderValues.Title, null, [""]),
                PlaceholderShape(3, "Content Placeholder 2", null, 1, [""])]))) { Name = "Title and Content" },
            new P.ColorMapOverride(new A.MasterColorMapping())) { Type = P.SlideLayoutValues.Object, Preserve = true };
        layout.AddPart(master);
        var theme = master.AddNewPart<ThemePart>("rId2");
        theme.Theme = NewTheme();
        part.AddPart(theme);
        foreach (var (title, body, notes) in slides)
        {
            var slidePart = part.AddNewPart<SlidePart>();
            slidePart.Slide = BuildSlide(layout, title, body);
            slidePart.AddPart(layout);
            var list = part.Presentation.SlideIdList!;
            list.Append(new P.SlideId { Id = (uint)(256 + list.ChildElements.Count), RelationshipId = part.GetIdOfPart(slidePart) });
            if (notes is not null)
            {
                var notesPart = CreateNotes(part, slidePart);
                var shape = notesPart.NotesSlide!.Descendants<P.Shape>().First(item => Placeholder(item)?.Type?.Value == P.PlaceholderValues.Body);
                foreach (var paragraph in shape.TextBody!.Elements<A.Paragraph>().ToArray()) paragraph.Remove();
                shape.TextBody.Append(Paragraph(notes));
            }
        }
        part.Presentation.Save();
    }

    private static P.ColorMap NewColorMap() => new()
    {
        Background1 = A.ColorSchemeIndexValues.Light1, Text1 = A.ColorSchemeIndexValues.Dark1,
        Background2 = A.ColorSchemeIndexValues.Light2, Text2 = A.ColorSchemeIndexValues.Dark2,
        Accent1 = A.ColorSchemeIndexValues.Accent1, Accent2 = A.ColorSchemeIndexValues.Accent2, Accent3 = A.ColorSchemeIndexValues.Accent3,
        Accent4 = A.ColorSchemeIndexValues.Accent4, Accent5 = A.ColorSchemeIndexValues.Accent5, Accent6 = A.ColorSchemeIndexValues.Accent6,
        Hyperlink = A.ColorSchemeIndexValues.Hyperlink, FollowedHyperlink = A.ColorSchemeIndexValues.FollowedHyperlink
    };

    private static A.Theme NewTheme()
    {
        static A.RgbColorModelHex Rgb(string value) => new() { Val = value };
        static A.SolidFill Placeholder() => new(new A.SchemeColor { Val = A.SchemeColorValues.PhColor });
        static T Fonts<T>() where T : A.FontCollectionType, new()
        {
            var fonts = new T();
            fonts.Append(new A.LatinFont { Typeface = "Yu Gothic" }, new A.EastAsianFont { Typeface = "游ゴシック" }, new A.ComplexScriptFont { Typeface = "" });
            return fonts;
        }
        return new A.Theme(
            new A.ThemeElements(
                new A.ColorScheme(
                    new A.Dark1Color(new A.SystemColor { Val = A.SystemColorValues.WindowText, LastColor = "000000" }),
                    new A.Light1Color(new A.SystemColor { Val = A.SystemColorValues.Window, LastColor = "FFFFFF" }),
                    new A.Dark2Color(Rgb("44546A")), new A.Light2Color(Rgb("E7E6E6")),
                    new A.Accent1Color(Rgb("4472C4")), new A.Accent2Color(Rgb("ED7D31")), new A.Accent3Color(Rgb("A5A5A5")),
                    new A.Accent4Color(Rgb("FFC000")), new A.Accent5Color(Rgb("5B9BD5")), new A.Accent6Color(Rgb("70AD47")),
                    new A.Hyperlink(Rgb("0563C1")), new A.FollowedHyperlinkColor(Rgb("954F72"))) { Name = "Office" },
                new A.FontScheme(Fonts<A.MajorFont>(), Fonts<A.MinorFont>()) { Name = "Office" },
                new A.FormatScheme(
                    new A.FillStyleList(Placeholder(), Placeholder(), Placeholder()),
                    new A.LineStyleList(new A.Outline(Placeholder()) { Width = 6350 }, new A.Outline(Placeholder()) { Width = 12700 }, new A.Outline(Placeholder()) { Width = 19050 }),
                    new A.EffectStyleList(new A.EffectStyle(new A.EffectList()), new A.EffectStyle(new A.EffectList()), new A.EffectStyle(new A.EffectList())),
                    new A.BackgroundFillStyleList(Placeholder(), Placeholder(), Placeholder())) { Name = "Office" }),
            new A.ObjectDefaults(),
            new A.ExtraColorSchemeList()) { Name = "Office Theme" };
    }
}
