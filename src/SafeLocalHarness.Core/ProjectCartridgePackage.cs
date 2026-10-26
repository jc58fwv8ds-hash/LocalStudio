using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SafeLocalHarness.Core;

/// <summary>
/// The key file of a <c>.lscart</c> package. It binds the project record to the exact set of included sources:
/// every file is listed with its size and SHA-256, and the whole set has one fingerprint.
/// </summary>
public sealed record CartridgePackageManifest(
    string Format,
    int FormatVersion,
    DateTimeOffset ExportedAt,
    string ExportedBy,
    ProjectCartridge Project,
    IReadOnlyList<ProjectSourceFingerprint> Files,
    string SetSha256,
    IReadOnlyList<string> ExcludedPaths);

public sealed record CartridgeInspection(
    bool Valid,
    IReadOnlyList<string> Issues,
    string? ProjectName,
    string? AppPurpose,
    string? ProjectKind,
    int FileCount,
    long TotalBytes,
    string? VerificationStatus,
    DateTimeOffset? ExportedAt,
    string? SetSha256,
    IReadOnlyList<string> ExcludedPaths);

public sealed record CartridgeImportResult(string ProjectPath, string ProjectName, bool Rebuilt, string RebuildSummary);

public interface IProjectCartridgePackages
{
    string FileExtension { get; }
    bool CanExport(string projectRoot);
    Task<string> ExportAsync(string projectRoot, string destinationFile, CancellationToken cancellationToken = default);
    Task<CartridgeInspection> InspectAsync(string packageFile, CancellationToken cancellationToken = default);
    Task<CartridgeImportResult> ImportAsync(string packageFile, CancellationToken cancellationToken = default);
}

/// <summary>
/// Single-file cartridge (<c>.lscart</c>, a ZIP) holding sources and the key file. Build outputs such as the
/// generated EXE are never included or opened. Import trusts nothing in the package: it is fully inspected without
/// extraction, re-verified while extracting, never executed, and rebuilt only through the existing source verifiers.
/// </summary>
public sealed class ProjectCartridgePackages : IProjectCartridgePackages
{
    public const string Extension = ".lscart";
    public const string FormatName = "localstudio-cartridge";
    public const int CurrentFormatVersion = 1;
    private const string ManifestEntry = "cartridge.json";
    private const string FilesPrefix = "files/";
    private const long MaxManifestBytes = 4L * 1024 * 1024;

    private readonly AppPaths _paths;
    private readonly IWindowsExecutableCompiler _compiler;

    public ProjectCartridgePackages(AppPaths paths, IWindowsExecutableCompiler? compiler = null)
    {
        _paths = paths;
        _compiler = compiler ?? new WindowsExecutableCompiler();
    }

    public string FileExtension => Extension;

    public bool CanExport(string projectRoot) =>
        File.Exists(Path.Combine(projectRoot, PortableProjectPath.ToPlatformPath(ProjectCartridgeStore.RelativePath))) &&
        File.Exists(Path.Combine(projectRoot, "localstudio.json"));

    public async Task<string> ExportAsync(string projectRoot, string destinationFile, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (!CanExport(root))
            throw new InvalidDataException("カートリッジ情報（.localstudio/project-cartridge.json）がないプロジェクトは書き出せません。");
        var destination = Path.GetFullPath(destinationFile);
        if (!destination.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"書き出し先の拡張子は {Extension} にしてください。");
        EnsureUserWritableDestination(root, destination);

        var project = await PortableProjectJson.LoadAsync<ProjectCartridge>(
            Path.Combine(root, PortableProjectPath.ToPlatformPath(ProjectCartridgeStore.RelativePath)), cancellationToken);

        var included = new List<(string Relative, string Full, long Size)>();
        var excluded = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.StartsWith(".localstudio/", StringComparison.OrdinalIgnoreCase)) continue;
            var info = new FileInfo(file);
            // Decided from the path and metadata only: excluded files are never opened.
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                !ProjectFilePolicy.IsCartridgeEligible(relative) ||
                info.Length > ProjectFilePolicy.MaxCartridgeFileBytes)
            {
                excluded.Add(relative);
                continue;
            }
            included.Add((relative, file, info.Length));
        }
        if (included.Count == 0) throw new InvalidDataException("カートリッジに含められるソースがありません。");
        if (included.Count > ProjectFilePolicy.MaxCartridgeFiles) throw new InvalidDataException("ファイル数が上限を超えています。");
        if (included.Sum(item => item.Size) > ProjectFilePolicy.MaxCartridgeTotalBytes) throw new InvalidDataException("合計サイズが上限（50 MB）を超えています。");

        var temporary = destination + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var fingerprints = new List<ProjectSourceFingerprint>();
                foreach (var (relative, full, _) in included)
                {
                    // Hash exactly the bytes written into the package.
                    var bytes = await File.ReadAllBytesAsync(full, cancellationToken);
                    var entry = archive.CreateEntry(FilesPrefix + relative, CompressionLevel.Optimal);
                    await using (var entryStream = entry.Open()) await entryStream.WriteAsync(bytes, cancellationToken);
                    fingerprints.Add(new(relative, bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()));
                }
                var manifest = new CartridgePackageManifest(
                    FormatName,
                    CurrentFormatVersion,
                    DateTimeOffset.UtcNow,
                    "LocalStudio",
                    project with { SourceFiles = fingerprints, UpdatedAt = DateTimeOffset.UtcNow },
                    fingerprints,
                    SetSha256(fingerprints),
                    excluded);
                var manifestEntry = archive.CreateEntry(ManifestEntry, CompressionLevel.Optimal);
                await using var manifestStream = manifestEntry.Open();
                await JsonSerializer.SerializeAsync(manifestStream, manifest, PortableProjectJson.JsonOptions, cancellationToken);
            }
            File.Move(temporary, destination, true);
            return destination;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public async Task<CartridgeInspection> InspectAsync(string packageFile, CancellationToken cancellationToken = default)
    {
        var issues = new List<string>();
        var full = Path.GetFullPath(packageFile);
        CartridgeInspection Fail(params string[] reasons) =>
            new(false, [.. issues, .. reasons], null, null, null, 0, 0, null, null, null, []);

        if (!full.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)) return Fail("package.extension");
        var info = new FileInfo(full);
        if (!info.Exists) return Fail("package.missing");
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return Fail("package.reparse-point");
        if (info.Length > ProjectFilePolicy.MaxCartridgeTotalBytes + MaxManifestBytes) return Fail("package.too-large");

        try
        {
            using var archive = ZipFile.OpenRead(full);
            if (archive.Entries.Count > ProjectFilePolicy.MaxCartridgeFiles + 1) return Fail("package.too-many-entries");
            var manifest = await ReadManifestAsync(archive, cancellationToken);
            if (manifest is null) return Fail("package.manifest-missing-or-invalid");
            if (manifest.Format != FormatName || manifest.FormatVersion != CurrentFormatVersion) return Fail("package.format");

            issues.AddRange(ProjectCartridgeStore.Validate(manifest.Project).Select(issue => "project." + issue));
            var declared = new Dictionary<string, ProjectSourceFingerprint>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var file in manifest.Files)
            {
                if (!PortableProjectPath.IsSafeRelative(file.RelativePath)) { issues.Add("file.unsafe-path:" + file.RelativePath); continue; }
                if (!ProjectFilePolicy.IsCartridgeEligible(file.RelativePath)) { issues.Add("file.disallowed-type:" + file.RelativePath); continue; }
                if (file.Size < 0 || file.Size > ProjectFilePolicy.MaxCartridgeFileBytes) { issues.Add("file.too-large:" + file.RelativePath); continue; }
                if (!declared.TryAdd(file.RelativePath, file)) { issues.Add("file.duplicate:" + file.RelativePath); continue; }
                total += file.Size;
            }
            if (declared.Count > ProjectFilePolicy.MaxCartridgeFiles) issues.Add("package.too-many-files");
            if (total > ProjectFilePolicy.MaxCartridgeTotalBytes) issues.Add("package.total-too-large");
            if (!SetSha256(manifest.Files).Equals(manifest.SetSha256, StringComparison.OrdinalIgnoreCase)) issues.Add("package.set-sha256-mismatch");
            var projectListed = manifest.Project.SourceFiles.Select(file => $"{file.RelativePath}|{file.Size}|{file.Sha256}".ToLowerInvariant()).OrderBy(x => x, StringComparer.Ordinal);
            var filesListed = manifest.Files.Select(file => $"{file.RelativePath}|{file.Size}|{file.Sha256}".ToLowerInvariant()).OrderBy(x => x, StringComparer.Ordinal);
            if (!projectListed.SequenceEqual(filesListed)) issues.Add("package.project-file-list-mismatch");

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.FullName == ManifestEntry) continue;
                if (!entry.FullName.StartsWith(FilesPrefix, StringComparison.Ordinal) || entry.FullName.EndsWith('/'))
                {
                    issues.Add("entry.unexpected:" + entry.FullName);
                    continue;
                }
                var relative = entry.FullName[FilesPrefix.Length..];
                // Unlisted or disallowed entries are reported without being read.
                if (!declared.TryGetValue(relative, out var expected)) { issues.Add("entry.unlisted:" + relative); continue; }
                if (!seen.Add(relative)) { issues.Add("entry.duplicate:" + relative); continue; }
                var actual = await HashEntryAsync(entry, cancellationToken);
                if (actual is null || actual.Value.Size != expected.Size || !actual.Value.Sha256.Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase))
                    issues.Add("entry.sha256-mismatch:" + relative);
            }
            foreach (var missing in declared.Keys.Where(path => !seen.Contains(path)))
                issues.Add("entry.missing:" + missing);

            return new(
                issues.Count == 0,
                issues.Distinct(StringComparer.Ordinal).ToArray(),
                manifest.Project.ProjectName,
                manifest.Project.AppPurpose,
                await ReadProjectKindAsync(archive, declared, cancellationToken),
                declared.Count,
                total,
                manifest.Project.VerificationStatus,
                manifest.ExportedAt,
                manifest.SetSha256,
                manifest.ExcludedPaths);
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or IOException or NotSupportedException)
        {
            return Fail("package.unreadable:" + exception.GetType().Name);
        }
    }

    public async Task<CartridgeImportResult> ImportAsync(string packageFile, CancellationToken cancellationToken = default)
    {
        var inspection = await InspectAsync(packageFile, cancellationToken);
        if (!inspection.Valid)
            throw new InvalidDataException("カートリッジを安全に読み込めません: " + string.Join(", ", inspection.Issues.Take(8)));

        _paths.EnsureCreated();
        var projectId = $"cartridge-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
        var staging = Path.Combine(_paths.ProjectRoot, ".staging-" + Guid.NewGuid().ToString("N"));
        var destination = Path.Combine(_paths.ProjectRoot, projectId);
        UserSpaceGuard.EnsureAllowed(staging);
        UserSpaceGuard.EnsureAllowed(destination);
        Directory.CreateDirectory(staging);
        var stagingPrefix = Path.GetFullPath(staging).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        try
        {
            var full = Path.GetFullPath(packageFile);
            CartridgePackageManifest manifest;
            using (var archive = ZipFile.OpenRead(full))
            {
                manifest = await ReadManifestAsync(archive, cancellationToken)
                    ?? throw new InvalidDataException("カートリッジのキー情報を読み取れません。");
                if (!manifest.SetSha256.Equals(inspection.SetSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("検査後にカートリッジが変更されました。");
                foreach (var file in manifest.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = archive.GetEntry(FilesPrefix + file.RelativePath)
                        ?? throw new InvalidDataException("カートリッジのファイルが見つかりません: " + file.RelativePath);
                    var target = Path.GetFullPath(Path.Combine(staging, PortableProjectPath.ToPlatformPath(file.RelativePath)));
                    if (!target.StartsWith(stagingPrefix, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("カートリッジのパスが不正です: " + file.RelativePath);
                    var bytes = await ReadBoundedAsync(entry, cancellationToken)
                        ?? throw new InvalidDataException("カートリッジのファイルが上限を超えています: " + file.RelativePath);
                    // Re-verify while extracting: the package may have changed since inspection.
                    if (bytes.LongLength != file.Size || !Convert.ToHexString(SHA256.HashData(bytes)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("カートリッジのファイルが照合と一致しません: " + file.RelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await File.WriteAllBytesAsync(target, bytes, cancellationToken);
                }
            }

            var imported = manifest.Project with
            {
                ProjectId = projectId,
                RequirementLedger = manifest.Project.RequirementLedger with { ProjectId = projectId },
                SourceFiles = manifest.Files,
                UpdatedAt = DateTimeOffset.UtcNow,
                Migrations = [.. manifest.Project.Migrations, $"imported {Path.GetFileName(full)} (set {manifest.SetSha256}) at {DateTimeOffset.UtcNow:O}"]
            };
            await ProjectCartridgeStore.SaveAtomicAsync(staging, imported, cancellationToken);
            await RequirementLedgerStore.SaveAtomicAsync(staging, imported.RequirementLedger, cancellationToken);
            await PortableProjectJson.SaveAtomicAsync(
                Path.Combine(staging, ".localstudio", "import.json"),
                new { sourceFile = Path.GetFileName(full), setSha256 = manifest.SetSha256, exportedAt = manifest.ExportedAt, importedAt = DateTimeOffset.UtcNow },
                cancellationToken);

            var (rebuilt, summary) = await RebuildAsync(staging, inspection.ProjectKind, cancellationToken);
            await ProjectScaffolder.CreateAcceptedBaselineAsync(staging, "imported", cancellationToken);
            Directory.Move(staging, destination);
            return new(destination, imported.ProjectName, rebuilt, summary);
        }
        catch
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>Imported sources are untrusted: they pass the existing source verifiers before any EXE is produced.</summary>
    private async Task<(bool Rebuilt, string Summary)> RebuildAsync(string root, string? kind, CancellationToken cancellationToken)
    {
        switch (kind)
        {
            case nameof(StudioProjectKind.WindowsApp):
            case nameof(StudioProjectKind.WindowsGame):
            {
                var verification = kind == nameof(StudioProjectKind.WindowsGame)
                    ? await new WindowsGameSourceVerifier().VerifyAsync(root, cancellationToken)
                    : await new WindowsAppSourceVerifier().VerifyAsync(root, cancellationToken);
                if (!verification.Passed)
                    return (false, "ソースの安全検査に合格しなかったため、EXEは作成していません: " + string.Join(", ", verification.Errors.Take(5)));
                try
                {
                    await _compiler.CompileAsync(root, cancellationToken);
                    return (true, "ソースの安全検査に合格し、EXEを作り直しました。");
                }
                catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or IOException)
                {
                    return (false, "EXEの作成に失敗しました（ソースは取り込み済みです）: " + exception.Message);
                }
            }
            case nameof(StudioProjectKind.DesktopOrWebApp):
            {
                var verification = await new StaticWebAppVerifier().VerifyAsync(root, cancellationToken);
                return verification.Passed
                    ? (true, "Webアプリの安全検査に合格しました（ビルド不要）。")
                    : (false, "Webアプリの安全検査に合格しませんでした: " + string.Join(", ", verification.Errors.Take(5)));
            }
            default:
                return (false, "この種類のプロジェクトは自動の再ビルド対象ではありません。");
        }
    }

    private static async Task<CartridgePackageManifest?> ReadManifestAsync(ZipArchive archive, CancellationToken cancellationToken)
    {
        var entry = archive.GetEntry(ManifestEntry);
        if (entry is null || entry.Length > MaxManifestBytes) return null;
        var bytes = await ReadBoundedAsync(entry, cancellationToken, MaxManifestBytes);
        if (bytes is null) return null;
        try { return JsonSerializer.Deserialize<CartridgePackageManifest>(bytes, PortableProjectJson.JsonOptions); }
        catch (JsonException) { return null; }
    }

    private static async Task<string?> ReadProjectKindAsync(ZipArchive archive, IReadOnlyDictionary<string, ProjectSourceFingerprint> declared, CancellationToken cancellationToken)
    {
        if (!declared.ContainsKey("localstudio.json")) return null;
        var entry = archive.GetEntry(FilesPrefix + "localstudio.json");
        if (entry is null) return null;
        var bytes = await ReadBoundedAsync(entry, cancellationToken);
        if (bytes is null) return null;
        try
        {
            using var json = JsonDocument.Parse(bytes);
            return json.RootElement.TryGetProperty("projectKind", out var kind) ? kind.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static async Task<(long Size, string Sha256)?> HashEntryAsync(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        var bytes = await ReadBoundedAsync(entry, cancellationToken);
        return bytes is null ? null : (bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    /// <summary>Reads at most the limit, regardless of the size the archive header claims (decompression-bomb guard).</summary>
    private static async Task<byte[]?> ReadBoundedAsync(ZipArchiveEntry entry, CancellationToken cancellationToken, long limit = ProjectFilePolicy.MaxCartridgeFileBytes)
    {
        if (entry.Length > limit) return null;
        await using var stream = entry.Open();
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > limit) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    public static string SetSha256(IEnumerable<ProjectSourceFingerprint> files)
    {
        var canonical = string.Join("\n", files
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .Select(file => $"{file.RelativePath}\t{file.Size}\t{file.Sha256.ToLowerInvariant()}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    /// <summary>Exports go only where the user chose inside their own profile, and never into the project itself.</summary>
    private static void EnsureUserWritableDestination(string projectRoot, string destination)
    {
        var profile = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(profile, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("書き出し先は、現在のユーザーのフォルダー内を選んでください。");
        if (destination.StartsWith(projectRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("プロジェクト自身の中には書き出せません。");
    }
}
