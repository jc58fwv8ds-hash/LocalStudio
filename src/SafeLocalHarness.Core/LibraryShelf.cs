using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SafeLocalHarness.Core;

public sealed record LibraryFile(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("bytes")] long Bytes);

/// <summary>library.json: what a library is, where it came from and the hash of every file.</summary>
public sealed record LibraryManifest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("license")] string License,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("global")] string? Global,
    [property: JsonPropertyName("files")] IReadOnlyList<LibraryFile> Files,
    [property: JsonPropertyName("addedAt")] DateTimeOffset AddedAt,
    [property: JsonPropertyName("element")] string? Element = null);

public sealed record LibraryEntry(LibraryManifest Manifest, string Folder, string Guide)
{
    public string Name => Manifest.Name;
    public string Key => $"{Manifest.Name}@{Manifest.Version}";
}

/// <summary>A library to put on the shelf: its files (on this PC), facts and the usage guide the user confirmed.</summary>
/// <remarks><paramref name="Element"/>: the element the library draws into, when it needs one (Chart.js: canvas).</remarks>
public sealed record LibraryDraft(string Name, string Version, string License, string Source, string? Global, IReadOnlyList<string> Files, string Guide, string? Element = null);

/// <summary>
/// 4-5: the user's library shelf (%LOCALAPPDATA%\LocalStudio\libraries\&lt;name&gt;@&lt;version&gt;). Libraries the user
/// adds are allowed in generated web apps: their files are copied into the project's vendor folder and loaded from the
/// PC only (the CSP stays 'self'); their hashes are recorded and checked instead of scanning their code, which the
/// user chose and the AI did not write. Each library carries a short usage guide that is given to the AI as data.
/// </summary>
public sealed class LibraryShelf(string root)
{
    // 2 MB: PDF libraries ship single browser files of 0.5–1.1 MB (pdf-lib, the pdf.js worker; approved 2026-10-19).
    public const long MaxFileBytes = 2L * 1024 * 1024;
    public const long MaxLibraryBytes = 2L * 1024 * 1024;
    // Fonts travel as "*.font.js": the font file as base64 in a script, because a web app opened from this PC cannot read
    // a font file next to it, but can load a script (Japanese fonts for PDFs, approved 2026-10-19). 10 MB holds a
    // 6 MB TrueType font with its licence.
    public const long MaxFontFileBytes = 10L * 1024 * 1024;
    public const string FontGlobal = "LocalStudioFonts";
    public const int MaxFiles = 8;
    public const int MaxGuideCharacters = 1_500;
    public const string VendorFolder = "vendor";
    public const string VendorManifest = "vendor/vendor.json";

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public string Root => root;

    public static LibraryShelf ForUser(AppPaths paths) => new(Path.Combine(paths.Root, "libraries"));

    /// <summary>Names are lower-case letters, digits and . - _ (like npm), e.g. "chart.js".</summary>
    public static bool IsValidName(string name) => Regex.IsMatch(name, @"^[a-z0-9][a-z0-9._-]{0,39}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Characters a library file must not hold: NUL, and the bidirectional controls that make code read differently from
    /// how it runs ("Trojan Source"). Other control characters are allowed here: minified libraries keep lookup tables
    /// as raw characters in their strings (pdf-lib, the pdf.js worker; 2026-10-19), and the files are checked against the
    /// registry's SHA-512. Code the AI writes keeps the stricter rule (HasForbiddenControlCharacters).
    /// </summary>
    public static bool HasUnsafeLibraryCharacters(string text) =>
        text.Any(ch => ch == '\0' || ch is >= '\u202A' and <= '\u202E' || ch is >= '\u2066' and <= '\u2069');

    public static bool IsFontFile(string fileName) => fileName.EndsWith(".font.js", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A font as a shelf script: the licence in full as a comment, then the font bytes as base64 under
    /// globalThis.LocalStudioFonts[family]. Apps turn it back into bytes for pdf-lib (embedFont) or FontFace.
    /// </summary>
    public static string WrapFont(byte[] font, string family, string licenseText)
    {
        if (!Regex.IsMatch(family, @"^[A-Za-z0-9 ._-]{1,60}$", RegexOptions.CultureInvariant)) throw new InvalidDataException("フォント名は英数字と空白・. - _ で60文字までにしてください。");
        var license = licenseText.Replace("\r\n", "\n").Replace("*/", "* /", StringComparison.Ordinal);
        return "/*! " + family + " — font data for LocalStudio apps. Licence:\n" + license.TrimEnd() + "\n*/\n" +
               "(function () { var f = globalThis." + FontGlobal + " = globalThis." + FontGlobal + " || {}; f[\"" + family + "\"] = \"" + Convert.ToBase64String(font) + "\"; })();\n";
    }

    /// <summary>Problems with a draft, in plain Japanese; empty when it can go on the shelf.</summary>
    public static IReadOnlyList<string> Check(LibraryDraft draft)
    {
        var problems = new List<string>();
        if (!IsValidName(draft.Name)) problems.Add("名前は英小文字・数字・. - _ で40文字までにしてください（例: chart.js）");
        if (!Regex.IsMatch(draft.Version, @"^[0-9A-Za-z][0-9A-Za-z.+-]{0,29}$", RegexOptions.CultureInvariant)) problems.Add("版は英数字と . + - で30文字までにしてください（例: 4.4.1）");
        if (string.IsNullOrWhiteSpace(draft.License) || draft.License.Length > 60) problems.Add("ライセンス（例: MIT）を60文字までで入れてください");
        if (string.IsNullOrWhiteSpace(draft.Source) || draft.Source.Length > 300) problems.Add("入手元（URL や「社内配布」など）を300文字までで入れてください");
        if (draft.Global is { } global && !Regex.IsMatch(global, @"^[A-Za-z_$][A-Za-z0-9_$]{0,39}$", RegexOptions.CultureInvariant)) problems.Add("ブラウザでの呼び名は JavaScript の名前にしてください（例: Chart）");
        if (draft.Element is { } element && !Regex.IsMatch(element, "^[a-z][a-z0-9-]{0,19}$", RegexOptions.CultureInvariant)) problems.Add("描画先の要素は英小文字のタグ名にしてください（例: canvas）");
        if (draft.Guide.Trim().Length == 0 || draft.Guide.Length > MaxGuideCharacters) problems.Add($"使い方メモを1～{MaxGuideCharacters}文字で入れてください");
        if (draft.Files.Count is 0 or > MaxFiles) problems.Add($"ファイルは1～{MaxFiles}個にしてください");
        long total = 0;
        foreach (var file in draft.Files)
        {
            var name = Path.GetFileName(file);
            var extension = Path.GetExtension(file).ToLowerInvariant();
            if (extension is not (".js" or ".css")) { problems.Add($"{name}: 使えるのは .js と .css だけです"); continue; }
            if (!ProjectFilePolicy.HasSafeWindowsNames(name)) { problems.Add($"{name}: ファイル名に使えない文字があります"); continue; }
            var info = new FileInfo(file);
            if (!info.Exists) { problems.Add($"{name}: 見つかりません"); continue; }
            var bound = IsFontFile(name) ? MaxFontFileBytes : MaxFileBytes;
            if (info.Length == 0 || info.Length > bound) { problems.Add($"{name}: 1ファイル {bound / 1024} KB までです"); continue; }
            if (!IsFontFile(name)) total += info.Length;
            var bytes = File.ReadAllBytes(file);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) { problems.Add($"{name}: BOM 付きの UTF-8 は扱いません"); continue; }
            try
            {
                var text = StrictUtf8.GetString(bytes);
                if (HasUnsafeLibraryCharacters(text)) problems.Add($"{name}: 文字ではないデータ（NUL）か、表示と中身をずらす制御文字が含まれています");
            }
            catch (DecoderFallbackException) { problems.Add($"{name}: UTF-8 の文字として読めません"); }
        }
        if (draft.Files.Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != draft.Files.Count) problems.Add("同じ名前のファイルが含まれています");
        if (total > MaxLibraryBytes) problems.Add($"ライブラリ全体で {MaxLibraryBytes / 1024 / 1024} MB までです");
        return problems;
    }

    /// <summary>Copies the draft's files onto the shelf with their hashes; nothing is written when the draft has problems.</summary>
    public LibraryEntry Add(LibraryDraft draft)
    {
        if (Check(draft) is { Count: > 0 } problems) throw new InvalidDataException(string.Join("、", problems));
        UserSpaceGuard.EnsureAllowed(root);
        var folder = Path.Combine(root, $"{draft.Name}@{draft.Version}");
        if (Directory.Exists(folder)) throw new InvalidOperationException($"{draft.Name} {draft.Version} はすでに棚にあります。");
        var staging = Path.Combine(root, ".adding-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(staging);
        try
        {
            var files = new List<LibraryFile>();
            foreach (var file in draft.Files)
            {
                var name = Path.GetFileName(file);
                var bytes = File.ReadAllBytes(file);
                File.WriteAllBytes(Path.Combine(staging, name), bytes);
                files.Add(new LibraryFile(name, Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length));
            }
            var manifest = new LibraryManifest(draft.Name, draft.Version, draft.License.Trim(), draft.Source.Trim(), draft.Global, files, DateTimeOffset.Now, draft.Element);
            File.WriteAllText(Path.Combine(staging, "library.json"), JsonSerializer.Serialize(manifest, Json) + "\n", StrictUtf8);
            File.WriteAllText(Path.Combine(staging, "GUIDE.md"), draft.Guide.Trim() + "\n", StrictUtf8);
            Directory.Move(staging, folder);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
        return Load(folder) ?? throw new InvalidDataException("棚に入れたライブラリを読み直せませんでした。");
    }

    /// <summary>Every library on the shelf whose files still match their recorded hashes.</summary>
    public IReadOnlyList<LibraryEntry> List() => !Directory.Exists(root)
        ? []
        : Directory.EnumerateDirectories(root).Where(folder => !Path.GetFileName(folder).StartsWith('.')).Select(Load).OfType<LibraryEntry>()
            .OrderBy(entry => entry.Name, StringComparer.Ordinal).ToArray();

    /// <summary>A library folder; null when it is not a complete, unchanged library (a changed file is never used).</summary>
    public static LibraryEntry? Load(string folder)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<LibraryManifest>(File.ReadAllText(Path.Combine(folder, "library.json"), StrictUtf8));
            if (manifest is null || !IsValidName(manifest.Name) || manifest.Files is not { Count: > 0 }) return null;
            foreach (var file in manifest.Files)
            {
                var path = Path.Combine(folder, file.Path);
                if (Path.GetFileName(file.Path) != file.Path || !File.Exists(path)) return null;
                if (!string.Equals(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), file.Sha256, StringComparison.OrdinalIgnoreCase)) return null;
            }
            var guide = File.ReadAllText(Path.Combine(folder, "GUIDE.md"), StrictUtf8).Trim();
            return guide.Length is 0 or > MaxGuideCharacters ? null : new LibraryEntry(manifest, folder, guide);
        }
        catch (Exception ex) when (ex is IOException or JsonException or DecoderFallbackException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Libraries named in the text ("Chart.js でグラフを" → chart.js), newest version of each.</summary>
    public IReadOnlyList<LibraryEntry> Match(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var lower = text.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        return List().Where(entry =>
            {
                var name = entry.Name;
                var bare = name.EndsWith(".js", StringComparison.Ordinal) ? name[..^3] : name;
                return Regex.IsMatch(lower, $@"(?<![a-z0-9]){Regex.Escape(name)}(?![a-z0-9])") ||
                       (bare.Length >= 4 && Regex.IsMatch(lower, $@"(?<![a-z0-9]){Regex.Escape(bare)}(?![a-z0-9])"));
            })
            .GroupBy(entry => entry.Name).Select(group => group.OrderByDescending(entry => entry.Manifest.AddedAt).First()).ToArray();
    }

    /// <summary>The shelf entries for the given names (a project's libraries when it is improved).</summary>
    public IReadOnlyList<LibraryEntry> Find(IEnumerable<string> keys)
    {
        var all = List();
        return keys.Select(key => all.FirstOrDefault(entry => entry.Key == key)
                                  ?? throw new InvalidOperationException($"このプロジェクトが使うライブラリ {key} が棚にありません（書き換えられたか、消された可能性があります）。"))
            .ToArray();
    }

    /// <summary>Project files for the libraries: vendor/&lt;name&gt;/&lt;file&gt; (unchanged bytes) and vendor/vendor.json with every hash.</summary>
    public static IReadOnlyList<GeneratedTextFile> VendorFiles(IReadOnlyList<LibraryEntry> libraries)
    {
        if (libraries.Count == 0) return [];
        var files = new List<GeneratedTextFile>();
        foreach (var library in libraries)
            foreach (var file in library.Manifest.Files)
                files.Add(new GeneratedTextFile($"{VendorFolder}/{library.Name}/{file.Path}", File.ReadAllText(Path.Combine(library.Folder, file.Path), StrictUtf8)));
        var record = libraries.Select(library => new
        {
            name = library.Manifest.Name,
            version = library.Manifest.Version,
            license = library.Manifest.License,
            source = library.Manifest.Source,
            global = library.Manifest.Global,
            files = library.Manifest.Files.Select(file => new { path = $"{VendorFolder}/{library.Name}/{file.Path}", sha256 = file.Sha256 })
        });
        files.Add(new GeneratedTextFile(VendorManifest, JsonSerializer.Serialize(new { schema = 1, libraries = record }, Json) + "\n"));
        return files;
    }

    /// <summary>The tags that load the libraries before app.js (scripts) and before app.css (stylesheets).</summary>
    public static (string Styles, string Scripts) Tags(IReadOnlyList<LibraryEntry> libraries)
    {
        var styles = new StringBuilder();
        var scripts = new StringBuilder();
        foreach (var library in libraries)
            foreach (var file in library.Manifest.Files)
            {
                var path = $"{VendorFolder}/{library.Name}/{file.Path}";
                if (file.Path.EndsWith(".css", StringComparison.OrdinalIgnoreCase)) styles.Append($"\n  <link rel=\"stylesheet\" href=\"{path}\">");
                else scripts.Append($"\n  <script src=\"{path}\"></script>");
            }
        return (styles.ToString(), scripts.ToString());
    }

    /// <summary>The browser globals of the libraries recorded in a project's vendor/vendor.json.</summary>
    public static IReadOnlyList<string> ProjectGlobals(string projectRoot)
    {
        var path = Path.Combine(projectRoot, PortableProjectPath.ToPlatformPath(VendorManifest));
        if (!File.Exists(path)) return [];
        using var document = JsonDocument.Parse(File.ReadAllText(path, StrictUtf8));
        return document.RootElement.GetProperty("libraries").EnumerateArray()
            .Select(item => item.TryGetProperty("global", out var global) && global.ValueKind == JsonValueKind.String ? global.GetString() : null)
            .OfType<string>().ToArray();
    }

    /// <summary>The library keys (name@version) recorded in a project's vendor/vendor.json; empty when it has none.</summary>
    public static IReadOnlyList<string> ProjectLibraries(string projectRoot)
    {
        var path = Path.Combine(projectRoot, PortableProjectPath.ToPlatformPath(VendorManifest));
        if (!File.Exists(path)) return [];
        using var document = JsonDocument.Parse(File.ReadAllText(path, StrictUtf8));
        return document.RootElement.GetProperty("libraries").EnumerateArray()
            .Select(item => $"{item.GetProperty("name").GetString()}@{item.GetProperty("version").GetString()}").ToArray();
    }

    /// <summary>
    /// A project's vendor files must be exactly the declared ones with their recorded hashes, and the page may load only
    /// its own files and declared vendor files.
    /// </summary>
    public static IReadOnlyList<string> VerifyProject(string projectRoot, string html)
    {
        var problems = new List<string>();
        var declared = new Dictionary<string, string>(StringComparer.Ordinal);
        var manifest = Path.Combine(projectRoot, PortableProjectPath.ToPlatformPath(VendorManifest));
        if (File.Exists(manifest))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(manifest, StrictUtf8));
                foreach (var library in document.RootElement.GetProperty("libraries").EnumerateArray())
                    foreach (var file in library.GetProperty("files").EnumerateArray())
                        declared[file.GetProperty("path").GetString()!] = file.GetProperty("sha256").GetString()!;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException) { problems.Add("vendor.manifest-invalid"); return problems; }
        }
        foreach (var (path, sha) in declared)
        {
            var full = Path.Combine(projectRoot, PortableProjectPath.ToPlatformPath(path));
            if (!path.StartsWith(VendorFolder + "/", StringComparison.Ordinal) || !File.Exists(full)) { problems.Add("vendor.file-missing:" + path); continue; }
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(full))), sha, StringComparison.OrdinalIgnoreCase)) problems.Add("vendor.file-changed:" + path);
        }
        var vendorFolder = Path.Combine(projectRoot, VendorFolder);
        if (Directory.Exists(vendorFolder))
            foreach (var file in Directory.EnumerateFiles(vendorFolder, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(projectRoot, file).Replace('\\', '/');
                if (relative != VendorManifest && !declared.ContainsKey(relative)) problems.Add("vendor.file-undeclared:" + relative);
            }
        var own = new HashSet<string>(StringComparer.Ordinal) { "app.js", "styles.css", AppStylesheetPolicy.FileName };
        foreach (Match match in Regex.Matches(html, @"<(?:script|link)\b[^>]*\b(?:src|href)\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            var reference = match.Groups[1].Value;
            if (!own.Contains(reference) && !declared.ContainsKey(reference)) problems.Add("vendor.reference-undeclared:" + reference);
        }
        return problems;
    }
}
