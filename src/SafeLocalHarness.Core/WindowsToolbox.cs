using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace SafeLocalHarness.Core;

/// <summary>
/// Reviewed helper classes a generated Windows app may use for work a small model cannot write reliably on its own
/// (reading and writing Excel workbooks first: 50 survey workbooks into one, user 2026-10-19). They are tools, not
/// templates: the app itself is still written from the request, and a helper is only added when the request needs it.
/// Helpers are C# 5 sources compiled with the app by the Windows built-in compiler; nothing is downloaded.
/// </summary>
public static class WindowsToolbox
{
    public sealed record Tool(string FileName, string ResourceName, Regex Trigger, string Usage);

    public static IReadOnlyList<Tool> All { get; } =
    [
        new("XlsxLite.cs", "SafeLocalHarness.Core.Toolbox.XlsxLite.cs.txt",
            new Regex(@"エクセル|Excel|xlsx|ワークブック|ブック", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            "XlsxLite (already in the project as XlsxLite.cs; do not write or redefine it) reads and writes the cell values of .xlsx files: " +
            "List<string> XlsxLite.SheetNames(path); List<string[]> XlsxLite.ReadSheet(path) for the first sheet or XlsxLite.ReadSheet(path, sheetName), each row an array of cell texts (\"\" for empty cells); " +
            "XlsxLite.WriteSheet(path, sheetName, IList<string[]> rows) writes a new workbook; XlsxLite.WriteBook(path, IList<KeyValuePair<string, IList<string[]>>> sheets) writes several sheets; " +
            "bool XlsxLite.TryReadDate(cellText, out DateTime date) reads an Excel date serial. " +
            "To combine workbooks so they still look as before (fonts, borders, fills, merged cells, column widths, date formats, pictures), pass the workbook paths themselves (not their rows): " +
            "List<string> notes = XlsxLite.CombineSheets(outputPath, filePaths) where filePaths is a List<string> or string[] of .xlsx paths (each source remains a separate sheet; this is NOT a row-level merge), " +
            "or XlsxLite.CombineSheets(outputPath, filePaths, \"まとめ\", summaryRows) with a POPULATED List<string[]> summaryRows (first row the headings, then actual data rows) to put a values sheet first; never pass a new empty List<string[]>; " +
            "when the request says to read answers/records, add a school/source-name column, and combine them into one table, use XlsxLite.ReadSheet for every file, prepend the source name to every data row, then XlsxLite.WriteSheet for the aggregated rows (optionally also CombineSheets to preserve originals); " +
            "to choose the sheet names, pass a List<KeyValuePair<string, string>> of (sheet name, path) instead of filePaths. " +
            "CombineSheets returns what could not be copied: always assign that return value and show every warning to the user. Prefer CombineSheets over WriteSheet only when the purpose is to preserve separate formatted forms; WriteSheet keeps values only and is correct for a requested combined data table. Let the user pick input files or folders with OpenFileDialog/FolderBrowserDialog (filter \"Excel (*.xlsx)|*.xlsx\") and the output with SaveFileDialog. " +
            "Reading a file can throw InvalidDataException or IOException (for example a file that is not .xlsx, or ~$ lock files): catch them per file, skip that file and list it for the user."),
        // PDF editing the way the user's PDF editor spec asks (2026-10-20): the PDF's own text and rules are changed, never
        // painted over or turned into images, and the original file is never written.
        new("PdfLite.cs", "SafeLocalHarness.Core.Toolbox.PdfLite.cs.txt",
            new Regex(@"PDF", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            "PdfLite (already in the project as PdfLite.cs; do not write or redefine it) edits a PDF's own text and rules: " +
            "PdfLiteDocument doc = PdfLite.Open(path) (read only; throws InvalidDataException for encrypted or unreadable PDFs); doc.PageCount; string doc.PageText(page) (pages start at 1); int doc.CountText(page, text); " +
            "PdfLiteEdit doc.ReplaceText(page, find, replacement, occurrence) changes the occurrence-th match (1 = first) with the page's own font; when its letters are missing it changes nothing and edit.MissingLetters lists them; " +
            "then ask the user and call doc.ReplaceTextWithFont(page, find, replacement, occurrence, \"gothic\" or \"mincho\") to write it with a Windows font (tell the user to check the look); PdfLiteEdit doc.ReplaceAll(find, replacement); " +
            "List<PdfLiteRule> doc.Rules(page) gives each rule's Index, Horizontal, Left, Bottom, Width, Height and Thickness in points; doc.SetRuleThickness(page, index or -1 for all, points); doc.MoveRule(page, index, dx, dy) in points; doc.RotatePage(page, 90) turns a page clockwise (int doc.PageRotation(page)); " +
            "every edit returns PdfLiteEdit with Done, Message, GrowthPoints (how much wider the text became; warn when it is above 0) and UsedSubstituteFont; doc.Undo(), doc.Redo(), doc.CanUndo, doc.CanRedo, List<string> doc.History; " +
            "doc.Save(outputPath) writes a new file and refuses the opened file's own path: let the user choose it with SaveFileDialog (suggest the original name + \"_edited.pdf\"). " +
            "Never draw white boxes over text, never render pages to images and never rebuild the whole PDF to edit it.")
    ];

    /// <summary>Assemblies every generated app is compiled against so the helpers build (all part of the Windows .NET Framework).</summary>
    public static IReadOnlyList<string> References { get; } = ["System.Xml.dll", "System.Xml.Linq.dll", "System.IO.Compression.dll"];

    private static readonly ConcurrentDictionary<string, string> Sources = new(StringComparer.Ordinal);

    /// <summary>The helpers a request needs (by its words).</summary>
    public static IReadOnlyList<Tool> For(string? request) =>
        string.IsNullOrWhiteSpace(request) ? [] : All.Where(tool => tool.Trigger.IsMatch(request)).ToArray();

    public static string Source(Tool tool) => Sources.GetOrAdd(tool.ResourceName, name =>
    {
        using var stream = typeof(WindowsToolbox).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("LocalStudioの道具が見つかりません: " + name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n");
    });

    public static IReadOnlyList<GeneratedTextFile> Files(IEnumerable<Tool> tools) =>
        tools.Select(tool => new GeneratedTextFile(tool.FileName, Source(tool))).ToArray();

    /// <summary>True for a helper file exactly as shipped; the app's own checks look at the app's code, not at reviewed helpers.</summary>
    public static bool IsToolboxFile(string path, string content) =>
        All.Any(tool => Path.GetFileName(path).Equals(tool.FileName, StringComparison.OrdinalIgnoreCase) &&
                        Source(tool).Equals(content.Replace("\r\n", "\n"), StringComparison.Ordinal));

    /// <summary>The app's own C# sources in a project folder (helpers as shipped left out), joined for checking.</summary>
    public static string AppSources(string projectRoot) =>
        string.Join("\n", Directory.EnumerateFiles(projectRoot, "*.cs", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => (path, text: File.ReadAllText(path)))
            .Where(file => !IsToolboxFile(file.path, file.text))
            .Select(file => file.text));
}
