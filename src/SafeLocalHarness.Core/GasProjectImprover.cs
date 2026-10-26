using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SafeLocalHarness.Core;

/// <summary>
/// Versioned improvement of generated Google Apps Script projects, the same way as web apps: the new version is
/// written by the model from the current files, appsscript.json is rewritten by LocalStudio, and the result must pass
/// the static checks in a staging copy before anything changes. Each version can then be handed over as a full zip or
/// as the difference from the version the user already pasted into the editor.
/// </summary>
public sealed class GasProjectImprover(IAppsScriptImprovementEngine engine) : IProjectImprover
{
    public const int MaxChangeRequestCharacters = 2_000;

    private static readonly WebProjectImprover.ImprovementTarget GasTarget = new(
        StudioProjectKind.GoogleAppsScript,
        new ProjectImplementationDecision(ProjectImplementationRoute.GeneratedAppsScript, true, "GASの改善版", "今のコードと依頼をもとにローカルAIが作り、LocalStudioが権限を決めて静的に検査しました。"),
        LocalAppsScriptGenerationEngine.ImplementationMode);

    private static Task<ProjectVerificationResult> Verify(string root, CancellationToken cancellationToken) =>
        GasStaticVerifier.VerifyAsync(root, externalAccessApproved: false, cancellationToken);

    public async Task<string?> CheckImprovableAsync(string projectRoot, CancellationToken cancellationToken = default)
    {
        var manifestPath = Path.Combine(projectRoot, "localstudio.json");
        if (!File.Exists(manifestPath)) return "このプロジェクトには設計情報（localstudio.json）がありません。";
        JsonNode? manifest;
        try { manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken)); }
        catch (JsonException) { return "設計情報（localstudio.json）を読み取れません。"; }
        if (manifest?["projectKind"]?.GetValue<string>() != nameof(StudioProjectKind.GoogleAppsScript)) return "これはGASのプロジェクトではありません。";
        if (manifest["implementationMode"]?.GetValue<string>() != LocalAppsScriptGenerationEngine.ImplementationMode) return "このGASはLocalStudioの生成形式ではないため、まだ改善できません。";
        if (manifest["blueprint"] is null) return "設計情報に設計案が含まれていません。";
        var files = CurrentFiles(projectRoot);
        if (files.Count is 0 or > LocalAppsScriptGenerationEngine.MaxFiles || files.Any(file => file.Content.Length > LocalAppsScriptGenerationEngine.MaxFileCharacters))
            return $"ファイル数（最大{LocalAppsScriptGenerationEngine.MaxFiles}）または1ファイルの文字数（最大{LocalAppsScriptGenerationEngine.MaxFileCharacters:N0}字）の上限を超えているため、今の仕組みでは安全に改善できません。";
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
        var blueprint = manifest["blueprint"].Deserialize<StudioBlueprint>() ?? throw new InvalidDataException("設計案を読み取れません。");
        var sourceRequest = manifest["sourceRequest"]?.GetValue<string>();
        var nextNumber = index.Versions.Max(version => version.Number) + 1;
        var improvedBlueprint = BlueprintValidator.Validate(blueprint.Features.Count < 8
            ? blueprint with { Features = blueprint.Features.Append("変更: " + (changeRequest.Length <= 150 ? changeRequest : changeRequest[..150])).ToArray() }
            : blueprint);
        var currentFiles = CurrentFiles(projectRoot);

        var generated = await engine.ImproveAsync(improvedBlueprint, new CurrentAppsScriptSource(currentFiles), changeRequest, sourceRequest, progress, steering, cancellationToken);
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
        // Script files the new version no longer has are deleted (the diff zip lists them in DELETED.txt).
        var kept = files.Select(file => file.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deletions = currentFiles.Select(file => file.Name).Where(name => !kept.Contains(name)).ToArray();

        progress?.Report(new("verification", "新しい版を静的に検査しています", 0.8));
        var verification = await WebProjectImprover.VerifyInStagingAsync(projectRoot, files, Verify, cancellationToken, deletions: deletions);
        progress?.Report(new("apply", $"v{nextNumber} として保存しています", 0.9));
        var next = await WebProjectImprover.ApplyAsync(projectRoot, index, improvedBlueprint, sourceRequest, verification, "improved", instructionRecord, null, GasTarget,
            async () =>
            {
                foreach (var file in files)
                    await File.WriteAllTextAsync(Path.Combine(projectRoot, file.RelativePath), file.Content, new UTF8Encoding(false, true), cancellationToken);
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
        var verification = await WebProjectImprover.VerifyInStagingAsync(projectRoot, files, Verify, cancellationToken, replaceAll: true);
        var next = await WebProjectImprover.ApplyAsync(projectRoot, index, blueprint, snapshotManifest["sourceRequest"]?.GetValue<string>(), verification, "restored", $"v{version} に戻す", version, GasTarget,
            () => ProjectVersionStore.ApplySnapshotAsync(projectRoot, version, cancellationToken), cancellationToken);
        return new(projectRoot, next.Versions.Single(entry => entry.Number == next.Current), previous);
    }

    /// <summary>The script files the model may change (appsscript.json is always LocalStudio's).</summary>
    private static IReadOnlyList<GasGeneratedFile> CurrentFiles(string projectRoot) =>
        Directory.EnumerateFiles(projectRoot, "*", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetExtension(path).ToLowerInvariant() is ".gs" or ".html")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => new GasGeneratedFile(Path.GetFileName(path), File.ReadAllText(path)))
            .ToArray();
}

/// <summary>
/// One improver for the conversation: picks the web or GAS improver from the project's recorded kind, so a chat can
/// improve whichever kind of project is open.
/// </summary>
public sealed class KindRoutingImprover(IProjectImprover web, IProjectImprover appsScript) : IProjectImprover
{
    private IProjectImprover For(string projectRoot)
    {
        var manifestPath = Path.Combine(projectRoot, "localstudio.json");
        try
        {
            var kind = File.Exists(manifestPath) ? JsonNode.Parse(File.ReadAllText(manifestPath))?["projectKind"]?.GetValue<string>() : null;
            return kind == nameof(StudioProjectKind.GoogleAppsScript) ? appsScript : web;
        }
        catch (JsonException)
        {
            return web;
        }
    }

    public Task<string?> CheckImprovableAsync(string projectRoot, CancellationToken cancellationToken = default) =>
        For(projectRoot).CheckImprovableAsync(projectRoot, cancellationToken);

    public Task<ProjectImprovementResult> ImproveAsync(string projectRoot, string changeRequest, IProgress<ProjectCreationProgress>? progress = null, IGenerationSteering? steering = null, CancellationToken cancellationToken = default) =>
        For(projectRoot).ImproveAsync(projectRoot, changeRequest, progress, steering, cancellationToken);

    public Task<ProjectImprovementResult> RestoreAsync(string projectRoot, int version, CancellationToken cancellationToken = default) =>
        For(projectRoot).RestoreAsync(projectRoot, version, cancellationToken);
}