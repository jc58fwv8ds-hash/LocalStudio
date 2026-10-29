using System.Text.Json.Nodes;

namespace SafeLocalHarness.Core;

public sealed record FeatureGrowthResult(string Feature, bool Added, string Reason, int Steps);

/// <summary>
/// Large apps are built in parts: the generator makes a working core from the first features, then the agent loop adds
/// each remaining feature to the saved project, checked after every change. A feature that passes becomes the new
/// baseline; one that does not is rolled back from the run snapshot and reported as not added, so the app that is left
/// always passed its checks. Nothing is fixed in advance: the features are the user's own, from the plan.
/// </summary>
public sealed class FeatureGrowth(
    AppPaths paths,
    ILocalModelRuntime runtime,
    Func<string> model,
    RuntimeResourcePlan? resources = null,
    Func<string, string, CancellationToken, Task<WindowsAppBehaviorChecker.Report>>? windowsBehavior = null,
    Func<string, IAgentModel>? agentModel = null,
    Func<string, CancellationToken, Task<IReadOnlyList<string>>>? checkOverride = null)
{
    /// <summary>Plans with more features than this are split; the generator handles this many reliably on a small model.</summary>
    public const int CoreFeatures = 3;
    public int StepsPerFeature { get; init; } = 16;

    public static (StudioBlueprint Core, IReadOnlyList<string> Later) Split(StudioBlueprint blueprint) =>
        blueprint.Features.Count <= CoreFeatures
            ? (blueprint, [])
            : (blueprint with { Features = blueprint.Features.Take(CoreFeatures).ToArray() }, blueprint.Features.Skip(CoreFeatures).ToArray());

    /// <summary>
    /// A split build must not make later requirements disappear from the durable project record. Growth checks are
    /// useful process evidence, but are not automatically requirement-level runtime evidence; new items therefore
    /// remain NotTested. A failed growth attempt is recorded as rolled back instead of being silently omitted.
    /// </summary>
    public static async Task RecordResultsAsync(
        string projectRoot,
        StudioBlueprint completeBlueprint,
        IReadOnlyList<FeatureGrowthResult> results,
        CancellationToken cancellationToken = default)
    {
        if (completeBlueprint.Features.Count <= CoreFeatures && results.Count == 0) return;
        var now = DateTimeOffset.UtcNow;
        var existingLedger = await RequirementLedgerStore.LoadAsync(projectRoot, cancellationToken);
        var existingByText = existingLedger.Items.ToDictionary(item => item.Text, StringComparer.Ordinal);
        var resultByFeature = results.ToDictionary(result => result.Feature, StringComparer.Ordinal);
        var items = completeBlueprint.Features.Select((feature, index) =>
        {
            var id = $"LS-REQ-{index + 1:000}";
            if (existingByText.TryGetValue(feature, out var existing)) return existing with { Id = id };
            resultByFeature.TryGetValue(feature, out var result);
            var note = result is null
                ? "分割生成後の要件です。要件別の検証証拠はまだありません。"
                : result.Added
                    ? "追加変更は検査を通過しましたが、要件別の永続的な動作証拠はまだありません。"
                    : $"追加変更は合格せずrollbackしました。要件は未検証です: {result.Reason}";
            return new RequirementLedgerItem(id, feature, true, RequirementState.NotTested, [], note);
        }).ToArray();
        var ledger = existingLedger with { UpdatedAt = now, Items = items };
        await RequirementLedgerStore.SaveAtomicAsync(projectRoot, ledger, cancellationToken);

        await UpdatePortableRequirementFilesAsync(projectRoot, completeBlueprint, cancellationToken);
        var inspection = await ProjectCartridgeStore.InspectAsync(projectRoot, cancellationToken);
        var cartridge = inspection.Cartridge;
        var requirementIds = items.Select(item => item.Id).ToArray();
        var pending = items.Where(item => item.State != RequirementState.Passed).Select(item => item.Text).ToArray();
        var growthIssues = results.Where(result => !result.Added)
            .Select(result => $"追加要件を検証済みにできませんでした（rollback済み）: {result.Feature} - {result.Reason}")
            .ToArray();
        var taskDag = cartridge.TaskDag with
        {
            Units = cartridge.TaskDag.Units.Select(unit => unit with { RequirementIds = requirementIds }).ToArray()
        };
        var definitions = items.Select(item => new ProjectTestDefinition(
            "TEST-" + item.Id,
            item.Id,
            "affected-deterministic",
            "この要件を対象に、ビルド・静的検査と必要な動作確認を安い順に行う。")).ToArray();
        var sources = await ProjectCartridgeStore.FingerprintSourcesAsync(projectRoot, cancellationToken);
        var updated = cartridge with
        {
            StructuredRequirements = completeBlueprint.Features,
            RequirementLedger = ledger,
            ImplementedFeatures = items.Where(item => item.State == RequirementState.Passed).Select(item => item.Text).ToArray(),
            PendingFeatures = pending,
            KnownIssues = cartridge.KnownIssues.Concat(growthIssues).Distinct(StringComparer.Ordinal).ToArray(),
            TestDefinitions = definitions,
            TestResults = cartridge.TestResults.Where(result => definitions.Any(definition => definition.Id == result.TestId)).ToArray(),
            VerificationStatus = RequirementLedgerPolicy.Validate(ledger).CompletionAllowed ? cartridge.VerificationStatus : "not-tested",
            UnresolvedItems = cartridge.UnresolvedItems.Concat(pending).Distinct(StringComparer.Ordinal).ToArray(),
            TaskDag = taskDag,
            CurrentDevelopmentState = growthIssues.Length == 0 ? "feature-growth-recorded" : "feature-growth-partial",
            SourceFiles = sources,
            UpdatedAt = now
        };
        await ProjectCartridgeStore.SaveAtomicAsync(projectRoot, updated, cancellationToken);
    }

    private static async Task UpdatePortableRequirementFilesAsync(
        string projectRoot,
        StudioBlueprint completeBlueprint,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(projectRoot, "localstudio.json");
        if (File.Exists(manifestPath) && JsonNode.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken)) is JsonObject manifest &&
            manifest["blueprint"] is JsonObject blueprint)
        {
            blueprint["features"] = new JsonArray(completeBlueprint.Features.Select(feature => (JsonNode?)JsonValue.Create(feature)).ToArray());
            await PortableProjectJson.SaveAtomicAsync(manifestPath, manifest, cancellationToken);
        }

        var requirementsPath = Path.Combine(projectRoot, "requirements.json");
        if (File.Exists(requirementsPath) && JsonNode.Parse(await File.ReadAllTextAsync(requirementsPath, cancellationToken)) is JsonObject requirements)
        {
            requirements["requestedFeatures"] = new JsonArray(completeBlueprint.Features.Select((feature, index) =>
                new JsonObject { ["id"] = $"LS-REQ-{index + 1:000}", ["text"] = feature }).ToArray<JsonNode?>());
            await PortableProjectJson.SaveAtomicAsync(requirementsPath, requirements, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<FeatureGrowthResult>> GrowAsync(
        string projectRoot, StudioProjectKind kind, string? request, IReadOnlyList<string> features,
        IProgress<ProjectCreationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var results = new List<FeatureGrowthResult>();
        if (features.Count == 0) return results;
        var name = model();
        await runtime.EnsureServerAsync(cancellationToken);
        await runtime.EnsureModelFitsAsync(name, cancellationToken);
        var check = checkOverride ?? CheckFor(kind, request);
        for (var index = 0; index < features.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var feature = features[index];
            progress?.Report(new("feature-growth", $"機能を1つずつ追加しています（{index + 1}/{features.Count}）: {feature}", 0.9 + 0.09 * index / features.Count));
            var session = await new SafeWorkspaceEngine(paths).StartAsync(projectRoot, cancellationToken);
            // The feature's own mechanism is part of its check: "印刷できる請求書" was reported as added while the code
            // never printed (invoice, catalogue 2026-10-20), because only the build was checked.
            var featureCheck = checkOverride is not null ? check : WithMechanism(check, kind, feature);
            var loop = new AgentLoop(agentModel?.Invoke(name) ?? new OllamaAgentModel(runtime, name, resources), featureCheck, StepsPerFeature);
            // Each step is reported (with the model's reply when it failed), so a feature that could not be added can be understood.
            loop.StepCompleted += step => progress?.Report(new("feature-growth-step", $"{feature}: {step.Index}手目 {step.Action} {step.Path}" + (step.Ok ? "" : " → " + step.Result[..Math.Min(step.Result.Length, 160)] + " / 返答: " + step.Reply[..Math.Min(step.Reply.Length, 200)]), 0.9));
            var goal = "Add this one feature to the existing working app, keeping every existing feature working:\n" + feature +
                       "\n\nThe whole request, for context only:\n" + (request ?? "") +
                       "\n\nFeatures already present: " + string.Join(" / ", features.Take(index).Prepend("(core features)")) +
                       "\nStart by listing and reading the files, then change only what this feature needs.";
            AgentOutcome outcome;
            try { outcome = await loop.RunAsync(session, projectRoot, goal, cancellationToken); }
            catch (OperationCanceledException) { await session.RollbackAsync(CancellationToken.None); throw; }
            if (outcome.Completed && session.ChangedFiles.Count > 0 && session.IsVerified)
            {
                await session.AcceptBaselineAsync(cancellationToken);
                results.Add(new(feature, true, outcome.Reason, outcome.Steps.Count));
            }
            else
            {
                await session.RollbackAsync(cancellationToken);
                results.Add(new(feature, false, outcome.Completed ? "no change was made" : outcome.Reason, outcome.Steps.Count));
            }
            progress?.Report(new("feature-growth-result", (results[^1].Added ? "追加しました: " : "追加できませんでした（元に戻しました）: ") + feature, 0.9 + 0.09 * (index + 1) / features.Count));
        }
        await runtime.UnloadModelsAsync(cancellationToken);
        return results;
    }

    public int RepairSteps { get; init; } = 20;

    /// <summary>
    /// For <see cref="ProjectScaffolder.Repair"/>: the generator's last candidate, already written to the staging
    /// folder, is fixed by the agent loop with exact edits. True when the checks pass; otherwise the edits are undone.
    /// </summary>
    public async Task<bool> RepairAsync(string projectRoot, StudioProjectKind kind, string? request, IReadOnlyList<string> failures,
        IProgress<ProjectCreationProgress>? progress, CancellationToken cancellationToken)
    {
        var name = model();
        await runtime.EnsureServerAsync(cancellationToken);
        await runtime.EnsureModelFitsAsync(name, cancellationToken);
        var session = await new SafeWorkspaceEngine(paths).StartAsync(projectRoot, cancellationToken);
        var loop = new AgentLoop(agentModel?.Invoke(name) ?? new OllamaAgentModel(runtime, name, resources), checkOverride ?? CheckFor(kind, request), RepairSteps);
        var steps = 0;
        loop.StepCompleted += step => progress?.Report(new("agent-repair-step", $"少しずつ直しています（{++steps}/{RepairSteps}手）: {step.Action} {step.Path}" + (step.Ok ? "" : " → " + step.Result[..Math.Min(step.Result.Length, 160)] + " / 返答: " + step.Reply[..Math.Min(step.Reply.Length, 200)]), 0.55));
        var goal = "This app was generated but still fails these checks. Fix them with small exact edits and keep every requested feature:\n- " +
                   string.Join("\n- ", failures.Take(8)) + "\n\nThe request, for context only:\n" + (request ?? "") +
                   "\nRead the lines the problems point to before changing them.";
        AgentOutcome outcome;
        try { outcome = await loop.RunAsync(session, projectRoot, goal, cancellationToken); }
        catch (OperationCanceledException) { await session.RollbackAsync(CancellationToken.None); throw; }
        if (outcome.Completed && session.ChangedFiles.Count > 0 && session.IsVerified)
        {
            await session.CompleteAsync(cancellationToken);
            return true;
        }
        await session.RollbackAsync(cancellationToken);
        return false;
    }

    /// <summary>The project's checks plus "does the code have what this feature needs" (print, save, sound, download, ...).</summary>
    internal static Func<string, CancellationToken, Task<IReadOnlyList<string>>> WithMechanism(
        Func<string, CancellationToken, Task<IReadOnlyList<string>>> check, StudioProjectKind kind, string feature) =>
        async (projectRoot, cancellationToken) =>
        {
            var findings = (await check(projectRoot, cancellationToken)).ToList();
            if (kind is StudioProjectKind.WindowsApp)
                findings.AddRange(LocalWindowsAppGenerationEngine.RequirementsWithoutMechanism([feature], WindowsToolbox.AppSources(projectRoot)));
            else if (kind is StudioProjectKind.DesktopOrWebApp && File.Exists(Path.Combine(projectRoot, "app.js")))
                findings.AddRange(LocalWebAppGenerationEngine.RequirementsWithoutMechanism([new GeneratedRequirement("LS-REQ-NEW", feature)],
                    File.Exists(Path.Combine(projectRoot, "index.html")) ? await File.ReadAllTextAsync(Path.Combine(projectRoot, "index.html"), cancellationToken) : "",
                    await File.ReadAllTextAsync(Path.Combine(projectRoot, "app.js"), cancellationToken)));
            return findings;
        };

    private Func<string, CancellationToken, Task<IReadOnlyList<string>>> CheckFor(StudioProjectKind kind, string? request) => kind switch
    {
        StudioProjectKind.GoogleAppsScript => AgentChecks.AppsScript(false),
        StudioProjectKind.WindowsApp or StudioProjectKind.WindowsGame => AgentChecks.Windows(request ?? "", windowsBehavior),
        _ => AgentChecks.WebAsync
    };

    public static string Describe(IReadOnlyList<FeatureGrowthResult> results)
    {
        if (results.Count == 0) return "";
        var added = results.Where(result => result.Added).Select(result => result.Feature).ToArray();
        var missing = results.Where(result => !result.Added).Select(result => result.Feature).ToArray();
        var text = "\n大きなアプリのため、中心の機能を作ってから残りを1つずつ追加しました。";
        if (added.Length > 0) text += "\n追加できた機能: " + string.Join(" / ", added);
        if (missing.Length > 0) text += "\nまだ入っていない機能（検査に合格しなかったため元に戻しました）: " + string.Join(" / ", missing) + "\n続けて指示すると、その機能だけを改めて追加します。";
        return text;
    }
}
