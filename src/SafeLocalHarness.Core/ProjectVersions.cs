using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SafeLocalHarness.Core;

public sealed record ProjectVersionEntry(
    int Number,
    string Kind,
    DateTimeOffset CreatedAt,
    string? Instruction,
    int? RestoredFrom,
    string SourceSetSha256,
    IReadOnlyList<string> Files,
    int FilesChanged,
    int LinesAdded,
    int LinesRemoved);

public sealed record ProjectVersionIndex(int SchemaVersion, int Current, IReadOnlyList<ProjectVersionEntry> Versions);

/// <summary>
/// Source snapshots of each version of a project, kept under .localstudio/versions (outside the source fingerprint
/// and never exported in a cartridge). Versions are only added: going back to vN creates a new version with vN's
/// contents, so history is never lost.
/// </summary>
public static class ProjectVersionStore
{
    public const string IndexRelativePath = ".localstudio/versions/index.json";

    public static string VersionRoot(string projectRoot, int number) =>
        Path.Combine(projectRoot, ".localstudio", "versions", "v" + number);

    public static async Task<ProjectVersionIndex?> LoadAsync(string projectRoot, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(projectRoot, PortableProjectPath.ToPlatformPath(IndexRelativePath));
        if (!File.Exists(path)) return null;
        var index = await PortableProjectJson.LoadAsync<ProjectVersionIndex>(path, cancellationToken);
        if (index.Versions.Count == 0 || index.Versions.All(version => version.Number != index.Current))
            throw new InvalidDataException("版の記録が壊れています。");
        return index;
    }

    /// <summary>Projects created before versioning start at v1 = their current state.</summary>
    public static async Task<ProjectVersionIndex> EnsureInitialAsync(string projectRoot, CancellationToken cancellationToken = default) =>
        await LoadAsync(projectRoot, cancellationToken) ??
        await AddAsync(projectRoot, null, "created", null, null, cancellationToken);

    internal static async Task<ProjectVersionIndex> AddAsync(
        string projectRoot,
        ProjectVersionIndex? index,
        string kind,
        string? instruction,
        int? restoredFrom,
        CancellationToken cancellationToken)
    {
        var number = (index?.Versions.Max(version => version.Number) ?? 0) + 1;
        var sources = await ProjectCartridgeStore.FingerprintSourcesAsync(projectRoot, cancellationToken);
        var snapshot = VersionRoot(projectRoot, number);
        if (Directory.Exists(snapshot)) Directory.Delete(snapshot, true);
        foreach (var source in sources)
        {
            var target = Path.Combine(snapshot, PortableProjectPath.ToPlatformPath(source.RelativePath));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(projectRoot, PortableProjectPath.ToPlatformPath(source.RelativePath)), target);
        }

        var (changed, added, removed) = index is null
            ? (sources.Count, 0, 0)
            : await CompareAsync(VersionRoot(projectRoot, index.Current), snapshot, cancellationToken);
        var entry = new ProjectVersionEntry(
            number,
            kind,
            DateTimeOffset.UtcNow,
            instruction,
            restoredFrom,
            ProjectCartridgePackages.SetSha256(sources),
            sources.Select(source => source.RelativePath).ToArray(),
            changed,
            added,
            removed);
        var next = new ProjectVersionIndex(1, number, (index?.Versions ?? []).Append(entry).ToArray());
        await PortableProjectJson.SaveAtomicAsync(Path.Combine(projectRoot, PortableProjectPath.ToPlatformPath(IndexRelativePath)), next, cancellationToken);
        return next;
    }

    /// <summary>Makes the project's source files exactly those of a snapshot (the control area is untouched).</summary>
    internal static async Task ApplySnapshotAsync(string projectRoot, int number, CancellationToken cancellationToken)
    {
        var snapshot = VersionRoot(projectRoot, number);
        if (!Directory.Exists(snapshot)) throw new InvalidDataException($"v{number} の記録が見つかりません。");
        var wanted = Directory.EnumerateFiles(snapshot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(snapshot, path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var current in await ProjectCartridgeStore.FingerprintSourcesAsync(projectRoot, cancellationToken))
        {
            var relative = PortableProjectPath.ToPlatformPath(current.RelativePath);
            if (!wanted.Contains(relative)) File.Delete(Path.Combine(projectRoot, relative));
        }
        foreach (var relative in wanted)
        {
            var target = Path.Combine(projectRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(snapshot, relative), target, overwrite: true);
        }
    }

    private static async Task<(int Changed, int Added, int Removed)> CompareAsync(string before, string after, CancellationToken cancellationToken)
    {
        var files = Directory.EnumerateFiles(before, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(before, path))
            .Concat(Directory.EnumerateFiles(after, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(after, path)))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        int changed = 0, added = 0, removed = 0;
        foreach (var relative in files)
        {
            var oldLines = await ReadLinesAsync(Path.Combine(before, relative), cancellationToken);
            var newLines = await ReadLinesAsync(Path.Combine(after, relative), cancellationToken);
            if (oldLines.SequenceEqual(newLines, StringComparer.Ordinal)) continue;
            changed++;
            var remaining = oldLines.GroupBy(line => line, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            foreach (var line in newLines)
            {
                if (remaining.TryGetValue(line, out var count) && count > 0) remaining[line] = count - 1;
                else added++;
            }
            removed += remaining.Values.Sum();
        }
        return (changed, added, removed);
    }

    private static async Task<string[]> ReadLinesAsync(string path, CancellationToken cancellationToken) =>
        File.Exists(path) ? (await File.ReadAllTextAsync(path, cancellationToken)).Replace("\r\n", "\n").Split('\n') : [];
}

public sealed record ProjectImprovementResult(string ProjectPath, ProjectVersionEntry Version, ProjectVersionEntry Previous);

public interface IProjectImprover
{
    /// <summary>Null when the project can be improved; otherwise the reason in plain Japanese.</summary>
    Task<string?> CheckImprovableAsync(string projectRoot, CancellationToken cancellationToken = default);
    Task<ProjectImprovementResult> ImproveAsync(string projectRoot, string changeRequest, IProgress<ProjectCreationProgress>? progress = null, IGenerationSteering? steering = null, CancellationToken cancellationToken = default);
    Task<ProjectImprovementResult> RestoreAsync(string projectRoot, int version, CancellationToken cancellationToken = default);
}

/// <summary>
/// Source-aware improvement of generated web apps inside the same project. The model sees the current source, the
/// requirements and the change request; the result must pass the same independent verification in a staging copy
/// before any project file changes. Any failure leaves the current version exactly as it was.
/// </summary>
public sealed class WebProjectImprover(IWebAppImprovementEngine engine, IStaticWebAppVerifier? verifier = null) : IProjectImprover
{
    public const int MaxChangeRequestCharacters = 2_000;
    private const string ImplementationMode = "generated-local-web-app";
    private readonly IStaticWebAppVerifier _verifier = verifier ?? new StaticWebAppVerifier();

    public async Task<string?> CheckImprovableAsync(string projectRoot, CancellationToken cancellationToken = default)
    {
        var manifestPath = Path.Combine(projectRoot, "localstudio.json");
        if (!File.Exists(manifestPath)) return "このプロジェクトには設計情報（localstudio.json）がありません。";
        JsonNode? manifest;
        try { manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken)); }
        catch (JsonException) { return "設計情報（localstudio.json）を読み取れません。"; }
        if (manifest?["projectKind"]?.GetValue<string>() != nameof(StudioProjectKind.DesktopOrWebApp))
            return "今はWebアプリだけを改善できます。Windowsアプリやゲームの改善は後の段階で対応します。";
        if (manifest["implementationMode"]?.GetValue<string>() != ImplementationMode)
            return "このアプリは固定テンプレートから作られているため、まだ改善できません。";
        if (manifest["blueprint"] is null) return "設計情報に設計案が含まれていません。";
        var indexPath = Path.Combine(projectRoot, "index.html");
        var scriptPath = Path.Combine(projectRoot, "app.js");
        if (!File.Exists(indexPath) || !File.Exists(scriptPath)) return "index.html または app.js が見つかりません。";
        var body = LocalWebAppGenerationEngine.ExtractGeneratedBody(await File.ReadAllTextAsync(indexPath, cancellationToken));
        if (body is null) return "index.html の構成がLocalStudioの生成形式と異なるため、改善できません。";
        if (body.Length > LocalWebAppGenerationEngine.MaxBodyCharacters ||
            (await File.ReadAllTextAsync(scriptPath, cancellationToken)).Length > LocalWebAppGenerationEngine.MaxJavaScriptCharacters)
            return $"画面部分{LocalWebAppGenerationEngine.MaxBodyCharacters:N0}字・JavaScript {LocalWebAppGenerationEngine.MaxJavaScriptCharacters:N0}字の上限を超えているため、今の仕組みでは安全に改善できません。";
        var appCssPath = Path.Combine(projectRoot, AppStylesheetPolicy.FileName);
        if (File.Exists(appCssPath) && AppStylesheetPolicy.Validate(await File.ReadAllTextAsync(appCssPath, cancellationToken)).Count > 0)
            return $"{AppStylesheetPolicy.FileName} が安全規則（{AppStylesheetPolicy.MaxCharacters:N0}字以内、外部読み込みなし）を満たしていないため、改善できません。";
        return null;
    }

    public async Task<ProjectImprovementResult> ImproveAsync(
        string projectRoot,
        string changeRequest,
        IProgress<ProjectCreationProgress>? progress = null,
        IGenerationSteering? steering = null,
        CancellationToken cancellationToken = default)
    {
        projectRoot = Path.GetFullPath(projectRoot);
        UserSpaceGuard.EnsureAllowed(projectRoot);
        changeRequest = (changeRequest ?? "").Trim();
        if (changeRequest.Length == 0) throw new ArgumentException("変えたい内容を入力してください。", nameof(changeRequest));
        if (changeRequest.Length > MaxChangeRequestCharacters) throw new InvalidDataException($"改善の依頼は{MaxChangeRequestCharacters:N0}文字以内にしてください。");
        if (await CheckImprovableAsync(projectRoot, cancellationToken) is { } reason) throw new InvalidOperationException(reason);

        progress?.Report(new("versions", "今の版を記録しています", 0.06));
        var index = await ProjectVersionStore.EnsureInitialAsync(projectRoot, cancellationToken);
        var previous = index.Versions.Single(version => version.Number == index.Current);
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(projectRoot, "localstudio.json"), cancellationToken))!.AsObject();
        var blueprint = manifest["blueprint"].Deserialize<StudioBlueprint>()
            ?? throw new InvalidDataException("設計案を読み取れません。");
        var sourceRequest = manifest["sourceRequest"]?.GetValue<string>();
        var nextNumber = index.Versions.Max(version => version.Number) + 1;
        var improvedBlueprint = BlueprintValidator.Validate(blueprint.Features.Count < 8
            ? blueprint with
            {
                Features = blueprint.Features.Append(
                    (VisualRequirementPolicy.IsVisualChange(changeRequest) ? VisualRequirementPolicy.Prefix : "変更: ") + Limit(changeRequest, 150)).ToArray()
            }
            : blueprint);
        var appCssPath = Path.Combine(projectRoot, AppStylesheetPolicy.FileName);
        var current = new CurrentWebAppSource(
            LocalWebAppGenerationEngine.ExtractGeneratedBody(await File.ReadAllTextAsync(Path.Combine(projectRoot, "index.html"), cancellationToken))!,
            await File.ReadAllTextAsync(Path.Combine(projectRoot, "app.js"), cancellationToken),
            File.Exists(appCssPath) ? await File.ReadAllTextAsync(appCssPath, cancellationToken) : null,
            LibraryShelf.ProjectLibraries(projectRoot));

        var generated = await engine.ImproveAsync(improvedBlueprint, current, changeRequest, sourceRequest, progress, steering, cancellationToken);
        var appliedInstructions = steering?.Applied.Select(item => item.Text).ToArray() ?? [];
        var instructionRecord = appliedInstructions.Length == 0 ? changeRequest : changeRequest + " / " + string.Join(" / ", appliedInstructions);
        manifest["blueprint"] = JsonSerializer.SerializeToNode(improvedBlueprint);
        var history = manifest["improvements"] as JsonArray ?? new JsonArray();
        history.Add(new JsonObject
        {
            ["version"] = nextNumber,
            ["changeRequest"] = changeRequest,
            ["appliedInstructions"] = new JsonArray(appliedInstructions.Select(text => (JsonNode?)JsonValue.Create(text)).ToArray()),
            ["at"] = DateTimeOffset.UtcNow
        });
        manifest["improvements"] = history;
        var files = generated.Files.Append(new GeneratedTextFile("localstudio.json", manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n")).ToArray();

        // A version that no longer needs app-specific styles drops the previous app.css with it.
        string[] deletions = files.Any(file => file.RelativePath == AppStylesheetPolicy.FileName) ? [] : [AppStylesheetPolicy.FileName];

        progress?.Report(new("verification", "新しい版を独立した検証器で確認しています", 0.8));
        var verification = await VerifyInStagingAsync(projectRoot, files, _verifier.VerifyAsync, cancellationToken, deletions: deletions);
        progress?.Report(new("apply", $"v{nextNumber} として保存しています", 0.9));
        var next = await ApplyAsync(projectRoot, index, improvedBlueprint, sourceRequest, verification, "improved", instructionRecord, null, WebTarget,
            async () =>
            {
                foreach (var file in files)
                {
                    // Library files live in vendor/<name>/, which a newly added library has to create.
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(projectRoot, file.RelativePath))!);
                    await File.WriteAllTextAsync(Path.Combine(projectRoot, file.RelativePath), file.Content, new UTF8Encoding(false, true), cancellationToken);
                }
                foreach (var deletion in deletions)
                    File.Delete(Path.Combine(projectRoot, deletion));
            }, cancellationToken);
        progress?.Report(new("complete", $"v{nextNumber} を保存しました", 1));
        return new(projectRoot, next.Versions.Single(version => version.Number == next.Current), previous);
    }

    public async Task<ProjectImprovementResult> RestoreAsync(string projectRoot, int version, CancellationToken cancellationToken = default)
    {
        projectRoot = Path.GetFullPath(projectRoot);
        UserSpaceGuard.EnsureAllowed(projectRoot);
        var index = await ProjectVersionStore.LoadAsync(projectRoot, cancellationToken)
            ?? throw new InvalidOperationException("このプロジェクトにはまだ版の記録がありません。");
        if (index.Versions.All(entry => entry.Number != version)) throw new InvalidOperationException($"v{version} は存在しません。");
        if (version == index.Current) throw new InvalidOperationException($"v{version} は今の版です。");
        var previous = index.Versions.Single(entry => entry.Number == index.Current);
        var snapshot = ProjectVersionStore.VersionRoot(projectRoot, version);
        var snapshotManifest = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(snapshot, "localstudio.json"), cancellationToken))!.AsObject();
        var blueprint = snapshotManifest["blueprint"].Deserialize<StudioBlueprint>() ?? throw new InvalidDataException("設計案を読み取れません。");
        var files = Directory.EnumerateFiles(snapshot, "*", SearchOption.AllDirectories)
            .Select(path => new GeneratedTextFile(Path.GetRelativePath(snapshot, path), File.ReadAllText(path)))
            .ToArray();
        var verification = await VerifyInStagingAsync(projectRoot, files, _verifier.VerifyAsync, cancellationToken, replaceAll: true);
        var next = await ApplyAsync(projectRoot, index, blueprint, snapshotManifest["sourceRequest"]?.GetValue<string>(), verification, "restored", $"v{version} に戻す", version, WebTarget,
            () => ProjectVersionStore.ApplySnapshotAsync(projectRoot, version, cancellationToken), cancellationToken);
        return new(projectRoot, next.Versions.Single(entry => entry.Number == next.Current), previous);
    }

    private static readonly ImprovementTarget WebTarget = new(
        StudioProjectKind.DesktopOrWebApp,
        new ProjectImplementationDecision(ProjectImplementationRoute.GeneratedLocalWebApp, true, "Webアプリの改善版", "今のソースと依頼をもとにローカルAIが作り、LocalStudioが検証しました。"),
        ImplementationMode);

    /// <summary>What a version is recorded as (kind, route and mode written into the development records).</summary>
    internal sealed record ImprovementTarget(StudioProjectKind Kind, ProjectImplementationDecision Decision, string ImplementationMode);

    internal static async Task<ProjectVerificationResult> VerifyInStagingAsync(
        string projectRoot,
        IReadOnlyList<GeneratedTextFile> files,
        Func<string, CancellationToken, Task<ProjectVerificationResult>> verify,
        CancellationToken cancellationToken,
        bool replaceAll = false,
        IReadOnlyList<string>? deletions = null)
    {
        var staging = Path.Combine(Path.GetDirectoryName(projectRoot)!, ".staging-" + Guid.NewGuid().ToString("N"));
        UserSpaceGuard.EnsureAllowed(staging);
        try
        {
            Directory.CreateDirectory(staging);
            if (!replaceAll)
            {
                foreach (var source in await ProjectCartridgeStore.FingerprintSourcesAsync(projectRoot, cancellationToken))
                {
                    var target = Path.Combine(staging, PortableProjectPath.ToPlatformPath(source.RelativePath));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(Path.Combine(projectRoot, PortableProjectPath.ToPlatformPath(source.RelativePath)), target);
                }
                foreach (var deletion in deletions ?? [])
                    File.Delete(Path.Combine(staging, deletion));
            }
            foreach (var file in files)
            {
                var target = Path.Combine(staging, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllTextAsync(target, file.Content, new UTF8Encoding(false, true), cancellationToken);
            }
            var verification = await verify(staging, cancellationToken);
            if (!verification.Passed)
                throw new InvalidDataException("新しい版が独立検証に合格しなかったため、今の版は変更していません: " + string.Join(", ", verification.Errors.Take(5)));
            return verification;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    internal static async Task<ProjectVersionIndex> ApplyAsync(
        string projectRoot,
        ProjectVersionIndex index,
        StudioBlueprint blueprint,
        string? sourceRequest,
        ProjectVerificationResult verification,
        string kind,
        string instruction,
        int? restoredFrom,
        ImprovementTarget target,
        Func<Task> writeSources,
        CancellationToken cancellationToken)
    {
        var controlFiles = new[]
        {
            ProjectCartridgeStore.RelativePath,
            RequirementLedgerStore.RelativePath,
            ".localstudio/verification/initial.json"
        };
        var savedControl = controlFiles
            .Select(relative => Path.Combine(projectRoot, PortableProjectPath.ToPlatformPath(relative)))
            .ToDictionary(path => path, path => File.Exists(path) ? File.ReadAllBytes(path) : null);
        try
        {
            await writeSources();
            await ProjectScaffolder.WriteVerificationEvidenceAsync(projectRoot, verification, structuralPreview: true, cancellationToken);
            await ProjectScaffolder.WriteDevelopmentRecordsAsync(
                projectRoot,
                Path.GetFileName(projectRoot),
                blueprint,
                target.Kind,
                target.Decision,
                target.ImplementationMode,
                verification,
                structuralPreview: true,
                sourceRequest,
                cancellationToken);
            await ProjectScaffolder.CreateAcceptedBaselineAsync(projectRoot, "structural-preview", cancellationToken);
            return await ProjectVersionStore.AddAsync(projectRoot, index, kind, instruction, restoredFrom, cancellationToken);
        }
        catch
        {
            // Put the current version back exactly: its sources, its records and a baseline made from them.
            await ProjectVersionStore.ApplySnapshotAsync(projectRoot, index.Current, CancellationToken.None);
            foreach (var (path, content) in savedControl)
            {
                if (content is null) { if (File.Exists(path)) File.Delete(path); }
                else await File.WriteAllBytesAsync(path, content, CancellationToken.None);
            }
            await ProjectScaffolder.CreateAcceptedBaselineAsync(projectRoot, "structural-preview", CancellationToken.None);
            throw;
        }
    }

    private static string Limit(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];
}
