using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SafeLocalHarness.Core;

/// <summary>
/// Google Apps Script projects are never run or deployed by LocalStudio: the user pastes them into the Apps Script
/// editor. LocalStudio can only check them statically, so the checks focus on what the user must be able to trust:
/// the permissions (OAuth scopes) the script will ask Google for match the services it really uses, nothing reaches
/// the internet unless the user approved it, and no code is built at runtime.
/// </summary>
public static class GasStaticVerifier
{
    public const string ManifestFileName = "appsscript.json";
    public const int MaxFileBytes = 256 * 1024;
    public const int MaxFiles = 40;

    /// <summary>Apps Script services and the scope (prefix) each one needs.</summary>
    private static readonly (string Service, string[] Scopes)[] ServiceScopes =
    [
        ("SpreadsheetApp", ["https://www.googleapis.com/auth/spreadsheets"]),
        ("DocumentApp", ["https://www.googleapis.com/auth/documents"]),
        ("SlidesApp", ["https://www.googleapis.com/auth/presentations"]),
        ("FormApp", ["https://www.googleapis.com/auth/forms"]),
        ("CalendarApp", ["https://www.googleapis.com/auth/calendar"]),
        ("DriveApp", ["https://www.googleapis.com/auth/drive"]),
        ("GmailApp", ["https://mail.google.com/", "https://www.googleapis.com/auth/gmail"]),
        ("MailApp", ["https://www.googleapis.com/auth/script.send_mail"]),
        ("UrlFetchApp", ["https://www.googleapis.com/auth/script.external_request"]),
        ("ScriptApp.newTrigger", ["https://www.googleapis.com/auth/script.scriptapp"]),
        // Menus, alerts, dialogs and sidebars (SpreadsheetApp.getUi() etc.) need this once scopes are listed explicitly.
        (".getUi", ["https://www.googleapis.com/auth/script.container.ui"])
    ];

    public static bool IsGasSource(string relativePath) =>
        Path.GetExtension(relativePath).ToLowerInvariant() is ".gs" or ".html" ||
        Path.GetFileName(relativePath).Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase);

    public static async Task<ProjectVerificationResult> VerifyAsync(string projectRoot, bool externalAccessApproved, CancellationToken cancellationToken = default)
    {
        var passed = new List<string>();
        var errors = new List<string>();
        var sources = Directory.EnumerateFiles(projectRoot, "*", SearchOption.TopDirectoryOnly)
            .Where(path => IsGasSource(Path.GetFileName(path)))
            .ToArray();
        Check(sources.Length is > 0 and <= MaxFiles && sources.All(path => new FileInfo(path).Length <= MaxFileBytes), "gas.files-bounded", passed, errors);
        Check(sources.Any(path => Path.GetExtension(path).Equals(".gs", StringComparison.OrdinalIgnoreCase)), "gas.has-script", passed, errors);

        var code = new StringBuilder();
        foreach (var path in sources.Where(path => !Path.GetFileName(path).Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase)))
            code.Append(await File.ReadAllTextAsync(path, cancellationToken)).Append('\n');
        var script = code.ToString();

        string[] declared = [];
        var manifestPath = Path.Combine(projectRoot, ManifestFileName);
        try
        {
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken));
            var root = manifest.RootElement;
            Check(root.TryGetProperty("runtimeVersion", out var runtime) && runtime.GetString() == "V8", "gas.manifest-v8", passed, errors);
            Check(root.TryGetProperty("timeZone", out var zone) && !string.IsNullOrWhiteSpace(zone.GetString()), "gas.manifest-timezone", passed, errors);
            declared = root.TryGetProperty("oauthScopes", out var scopes) && scopes.ValueKind == JsonValueKind.Array
                ? scopes.EnumerateArray().Select(item => item.GetString() ?? "").ToArray()
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            errors.Add("gas.manifest-invalid");
        }

        var used = UsedServices(script);
        var missing = used.Where(service => !ServiceScopes.Single(item => item.Service == service).Scopes
            .Any(scope => declared.Any(declaredScope => declaredScope.StartsWith(scope, StringComparison.Ordinal)))).ToArray();
        Check(missing.Length == 0, "gas.scopes-cover-used-services" + (missing.Length == 0 ? "" : ":" + string.Join(",", missing)), passed, errors);
        var unused = declared.Where(scope => !used.Any(service => ServiceScopes.Single(item => item.Service == service).Scopes
            .Any(prefix => scope.StartsWith(prefix, StringComparison.Ordinal)))).ToArray();
        Check(unused.Length == 0, "gas.no-unneeded-scopes" + (unused.Length == 0 ? "" : ":" + string.Join(",", unused)), passed, errors);
        Check(!used.Contains("UrlFetchApp") || externalAccessApproved, "gas.external-access-approved", passed, errors);
        Check(!Regex.IsMatch(script, @"(?<![\w$])eval\s*\(|(?<![\w$.])Function\s*\(|\bnew\s+Function\b", RegexOptions.CultureInvariant), "gas.no-dynamic-code", passed, errors);
        var gsCode = new StringBuilder();
        var syntaxErrors = new List<string>();
        foreach (var path in sources.Where(path => Path.GetExtension(path).Equals(".gs", StringComparison.OrdinalIgnoreCase)))
        {
            var text = await File.ReadAllTextAsync(path, cancellationToken);
            gsCode.Append(text).Append('\n');
            if (SyntaxError(text) is { } syntax) syntaxErrors.Add($"gas.syntax:{Path.GetFileName(path)}: {syntax}");
        }
        // A script the editor cannot even save passed every check before (catalogue audit 2026-10-20): parse it as V8 does.
        if (syntaxErrors.Count == 0) passed.Add("gas.syntax-valid"); else errors.AddRange(syntaxErrors);
        Check(TopLevelFunctions(gsCode.ToString()).Count > 0, "gas.has-entry-function", passed, errors);
        var html = new StringBuilder();
        foreach (var path in sources.Where(path => Path.GetExtension(path).Equals(".html", StringComparison.OrdinalIgnoreCase)))
            html.Append(await File.ReadAllTextAsync(path, cancellationToken)).Append('\n');
        // The dialog or sidebar runs in the user's browser; it loads nothing from outside, like LocalStudio web apps.
        Check(!Regex.IsMatch(html.ToString(), @"(src|href)\s*=\s*[""']?\s*(https?:|//)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "gas.html-no-external", passed, errors);
        return new(errors.Count == 0, passed, errors, DateTimeOffset.Now);
    }

    /// <summary>The first syntax error in a script (with its line), or null when it parses as modern JavaScript.</summary>
    public static string? SyntaxError(string script)
    {
        try
        {
            Jint.Engine.PrepareScript(script);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ex.Message;
        }
    }

    /// <summary>Names of functions declared at the start of a line (what the editor lists as runnable).</summary>
    public static IReadOnlyList<string> TopLevelFunctions(string script) =>
        Regex.Matches(script, @"^(?:async\s+)?function\s+([A-Za-z_$][\w$]*)\s*\(", RegexOptions.Multiline | RegexOptions.CultureInvariant)
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// appsscript.json written by LocalStudio, not by the model: the scopes are exactly those of the services the code
    /// uses, so the permissions Google will ask for always match the code (none missing, none extra).
    /// </summary>
    public static string ManifestFor(string script, string timeZone = "Asia/Tokyo")
    {
        var scopes = UsedServices(script)
            .Select(service => ServiceScopes.Single(item => item.Service == service).Scopes[0])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var manifest = new Dictionary<string, object>
        {
            ["timeZone"] = timeZone,
            ["dependencies"] = new Dictionary<string, object>(),
            ["exceptionLogging"] = "STACKDRIVER",
            ["runtimeVersion"] = "V8",
            ["oauthScopes"] = scopes
        };
        return JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    /// <summary>The Apps Script services a script refers to (by name, the way the editor infers scopes).</summary>
    public static IReadOnlyList<string> UsedServices(string script) =>
        ServiceScopes.Where(item => Regex.IsMatch(script,
                item.Service.StartsWith('.') ? $@"{Regex.Escape(item.Service)}\b" : $@"(?<![\w$.]){Regex.Escape(item.Service)}\b",
                RegexOptions.CultureInvariant))
            .Select(item => item.Service)
            .ToArray();

    private static void Check(bool condition, string name, ICollection<string> passed, ICollection<string> errors)
    {
        if (condition) passed.Add(name); else errors.Add(name);
    }
}

/// <summary>
/// Where hand-over zips go and what they are called: one folder per project under the user's Documents, a full zip
/// for every version and, for an improved version, the difference from the version before it.
/// </summary>
public static class GasHandover
{
    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LocalStudio GAS");

    public static async Task<IReadOnlyList<string>> WriteAsync(
        string projectRoot,
        string handoverRoot,
        int version,
        int? previousVersion,
        CancellationToken cancellationToken = default)
    {
        var name = Path.GetFileName(Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar));
        var folder = Path.Combine(handoverRoot, name);
        var written = new List<string>
        {
            await GasPackageBuilder.BuildFullZipAsync(projectRoot, Path.Combine(folder, $"{name}-v{version}-full.zip"), cancellationToken)
        };
        if (previousVersion is { } from && from != version)
            written.Add(await GasPackageBuilder.BuildDiffZipAsync(projectRoot, from, version, Path.Combine(folder, $"{name}-v{from}-to-v{version}-diff.zip"), cancellationToken));
        return written;
    }
}

/// <summary>
/// The two hand-over files for the Apps Script editor: the full set, and the difference between two versions with
/// the deleted files and plain step-by-step instructions (the way GAS updates are handed over in conversation).
/// </summary>
public static class GasPackageBuilder
{
    public static async Task<string> BuildFullZipAsync(string projectRoot, string destinationZip, CancellationToken cancellationToken = default)
    {
        var files = Directory.EnumerateFiles(projectRoot, "*", SearchOption.TopDirectoryOnly)
            .Where(path => GasStaticVerifier.IsGasSource(Path.GetFileName(path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(path => Path.GetFileName(path), path => path, StringComparer.OrdinalIgnoreCase);
        var guide = new StringBuilder("# 全ファイル\n\nApps Scriptエディタで、次のファイルをすべて作成（または置き換え）してください。\n\n");
        foreach (var name in files.Keys) guide.Append("- ").Append(name).Append('\n');
        guide.Append("\n保存後、初回実行時にGoogleが表示する権限（appsscript.json の oauthScopes）を確認してから許可してください。\n");
        return await WriteZipAsync(projectRoot, destinationZip, files, [], guide.ToString(), cancellationToken);
    }

    public static async Task<string> BuildDiffZipAsync(string projectRoot, int fromVersion, int toVersion, string destinationZip, CancellationToken cancellationToken = default)
    {
        var before = Snapshot(ProjectVersionStore.VersionRoot(projectRoot, fromVersion));
        var after = Snapshot(ProjectVersionStore.VersionRoot(projectRoot, toVersion));
        var changed = after.Where(item => !before.TryGetValue(item.Key, out var old) || !FilesEqual(old, item.Value))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
        var deleted = before.Keys.Where(name => !after.ContainsKey(name)).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        var guide = new StringBuilder($"# v{fromVersion} → v{toVersion} の変更\n\nApps Scriptエディタで、次の順に操作してください。\n\n");
        var step = 1;
        foreach (var name in changed.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            guide.Append($"{step++}. {(before.ContainsKey(name) ? "置き換え" : "新規作成")}: {name}\n");
        foreach (var name in deleted)
            guide.Append($"{step++}. 削除: {name}\n");
        if (changed.ContainsKey(GasStaticVerifier.ManifestFileName))
            guide.Append("\n※ appsscript.json が変わっています。次の実行時に、Googleが権限の確認を求める場合があります。\n");
        if (changed.Count == 0 && deleted.Length == 0) guide.Append("変更はありません。\n");
        return await WriteZipAsync(projectRoot, destinationZip, changed, deleted, guide.ToString(), cancellationToken);
    }

    private static Dictionary<string, string> Snapshot(string root)
    {
        if (!Directory.Exists(root)) throw new InvalidDataException("指定した版の記録が見つかりません。");
        return Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
            .Where(path => GasStaticVerifier.IsGasSource(Path.GetFileName(path)))
            .ToDictionary(path => Path.GetFileName(path), path => path, StringComparer.OrdinalIgnoreCase);
    }

    private static bool FilesEqual(string left, string right) =>
        File.ReadAllBytes(left).AsSpan().SequenceEqual(File.ReadAllBytes(right));

    private static async Task<string> WriteZipAsync(
        string projectRoot,
        string destinationZip,
        IReadOnlyDictionary<string, string> files,
        IReadOnlyList<string> deleted,
        string guide,
        CancellationToken cancellationToken)
    {
        var destination = Path.GetFullPath(destinationZip);
        var profile = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var project = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!destination.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("保存先は .zip にしてください。");
        if (!destination.StartsWith(profile, StringComparison.OrdinalIgnoreCase) || destination.StartsWith(project, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("保存先は、ユーザーフォルダー内（プロジェクトの外）に限られます。");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                foreach (var (name, path) in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    archive.CreateEntryFromFile(path, name, CompressionLevel.Optimal);
                }
                if (deleted.Count > 0) await WriteEntryAsync(archive, "DELETED.txt", string.Join("\n", deleted) + "\n");
                await WriteEntryAsync(archive, "CHANGES.md", guide);
            }
            File.Move(temporary, destination, overwrite: true);
            return destination;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static async Task WriteEntryAsync(ZipArchive archive, string name, string content)
    {
        await using var stream = archive.CreateEntry(name).Open();
        await stream.WriteAsync(new UTF8Encoding(false).GetBytes(content));
    }
}
