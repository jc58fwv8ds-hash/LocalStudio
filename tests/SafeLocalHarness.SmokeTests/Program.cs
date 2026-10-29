using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SafeLocalHarness.Core;

var automatedErrorDialogsSuppressed = AutomatedProcessErrorMode.SuppressWindowsErrorDialogs();
var failures = new List<string>();
void Check(bool condition, string name) { if (!condition) failures.Add(name); }
Check(automatedErrorDialogsSuppressed, "automated-smoke-suppresses-windows-crash-dialogs");
void CheckThrows(Action action, string name)
{
    try { action(); failures.Add(name); }
    catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or FileNotFoundException) { }
}
async Task CheckThrowsAsync(Func<Task> action, string name)
{
    try { await action(); failures.Add(name); }
    catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or FileNotFoundException) { }
}
string ComputePre012VerificationFingerprint(string projectRoot)
{
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    var files = new List<string> { "index.html", "styles.css", "app.js", "requirements.json" };
    if (File.Exists(Path.Combine(projectRoot, "verification-plan.json"))) files.Add("verification-plan.json");
    foreach (var relative in files)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(relative + "\n"));
        hash.AppendData(File.ReadAllBytes(Path.Combine(projectRoot, relative)));
    }
    return Convert.ToHexString(hash.GetHashAndReset());
}
string FindRepositoryRoot()
{
    var candidates = new[] { Environment.CurrentDirectory, AppContext.BaseDirectory };
    foreach (var start in candidates)
    {
        var current = new DirectoryInfo(start);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "SafeLocalHarness.slnx"))) return current.FullName;
            current = current.Parent;
        }
    }
    throw new DirectoryNotFoundException("LocalStudioのリポジトリルートを確認できませんでした。");
}

if (args.Contains("--create-demo", StringComparer.OrdinalIgnoreCase))
{
    var demoBlueprint = BlueprintValidator.Validate(new StudioBlueprint(
        "LocalStudio Focus Demo",
        "今日やることを、追加・完了・絞り込みできるシンプルなローカルアプリ",
        "app",
        ["タスクの追加と削除", "完了状態の切替", "表示の絞り込み", "ブラウザへのローカル保存"],
        [new("生成", "依存関係のないWebアプリを作成"), new("検証", "安全性と基本機能を確認")],
        [],
        ["外部通信を行わない"],
        ["workspace.read", "workspace.write"]));
    var demoPath = await new ProjectScaffolder(AppPaths.ForCurrentUser()).CreateAsync(demoBlueprint, StudioProjectKind.DesktopOrWebApp);
    var demoVerification = await new StaticWebAppVerifier().VerifyAsync(demoPath);
    if (!demoVerification.Passed) throw new InvalidOperationException("デモアプリの検証に失敗しました。");
    Console.WriteLine("DEMO_PROJECT=" + demoPath);
    Console.WriteLine("DEMO_CHECKS=" + demoVerification.PassedChecks.Count);
    return 0;
}

if (args.Contains("--create-generated-demo", StringComparer.OrdinalIgnoreCase))
{
    var demoPaths = AppPaths.ForCurrentUser();
    await using var runtime = new OllamaManager(demoPaths);
    var settings = new SettingsStore(demoPaths);
    using var planner = new LocalPlanningEngine(runtime);
    using var generator = new LocalWebAppGenerationEngine(runtime, settings);
    var request = "複数の付箋を作り、色を選び、自由な位置へドラッグして並べられるメモアプリを作って。付箋と位置はこのPC内だけに自動保存し、個別に削除できるようにして。管理者権限とネットワーク通信は使わないで。";
    var planningProgress = new Progress<PlanningProgress>(value => Console.WriteLine($"PLAN {value.Fraction:P0} {value.Message}"));
    var blueprint = await planner.PlanAsync(request, StudioProjectKind.DesktopOrWebApp, settings.Load().SelectedModel, planningProgress);
    var decision = ImplementationSupportPolicy.Evaluate(blueprint, StudioProjectKind.DesktopOrWebApp);
    if (decision.Route != ProjectImplementationRoute.GeneratedLocalWebApp)
        throw new InvalidOperationException("付箋アプリが新規実装として判定されませんでした: " + decision.Explanation);
    var creationProgress = new Progress<ProjectCreationProgress>(value => Console.WriteLine($"BUILD {value.Fraction:P0} {value.Message}"));
    var project = await new ProjectScaffolder(demoPaths, generativeGenerator: generator).CreateAsync(blueprint, StudioProjectKind.DesktopOrWebApp, progress: creationProgress);
    var verification = await new StaticWebAppVerifier().VerifyAsync(project);
    if (!verification.Passed) throw new InvalidOperationException("生成した付箋アプリが検証に失敗しました: " + string.Join(", ", verification.Errors));
    Console.WriteLine("GENERATED_PROJECT=" + project);
    Console.WriteLine("GENERATED_NAME=" + blueprint.Name);
    Console.WriteLine("GENERATED_CHECKS=" + verification.PassedChecks.Count);
    return 0;
}

if (args.Contains("--create-generated-simple-demo", StringComparer.OrdinalIgnoreCase))
{
    var demoPaths = AppPaths.ForCurrentUser();
    await using var runtime = new OllamaManager(demoPaths);
    var settings = new SettingsStore(demoPaths);
    using var generator = new LocalWebAppGenerationEngine(runtime, settings);
    var blueprint = BlueprintValidator.Validate(new StudioBlueprint(
        "ローカル文字数カウンター",
        "入力した文章の文字数と行数をリアルタイム表示し、内容をこのPC内だけに保存できるアプリ",
        "app",
        ["入力した文章の文字数と行数をリアルタイム表示", "文章の自動保存と消去"],
        [new("入力", "文章を入力する"), new("集計", "文字数と行数を表示する"), new("保存", "端末内だけに保存する")],
        [],
        ["管理者権限とネットワーク通信を使わない"],
        ["workspace.read", "workspace.write"]));
    var creationProgress = new Progress<ProjectCreationProgress>(value => Console.WriteLine($"BUILD {value.Fraction:P0} {value.Message}"));
    var project = await new ProjectScaffolder(demoPaths, generativeGenerator: generator).CreateAsync(blueprint, StudioProjectKind.DesktopOrWebApp, progress: creationProgress);
    var verification = await new StaticWebAppVerifier().VerifyAsync(project);
    if (!verification.Passed) throw new InvalidOperationException("生成した文字数カウンターが検証に失敗しました: " + string.Join(", ", verification.Errors));
    Console.WriteLine("GENERATED_PROJECT=" + project);
    Console.WriteLine("GENERATED_NAME=" + blueprint.Name);
    Console.WriteLine("GENERATED_CHECKS=" + verification.PassedChecks.Count);
    return 0;
}

var paths = AppPaths.ForCurrentUser();
Check(paths.Root.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), StringComparison.OrdinalIgnoreCase), "user-space-root");
Check(!paths.Root.Contains("Program Files", StringComparison.OrdinalIgnoreCase), "not-program-files");
Check(paths.Root.EndsWith("LocalStudio", StringComparison.OrdinalIgnoreCase), "localstudio-user-root");
const long GiB = 1024L * 1024 * 1024;
var standardHardware = new HardwareSnapshot(1, DateTimeOffset.UtcNow, 12, 16 * GiB, 8 * GiB, 80 * GiB, ["Intel Iris Xe"], "unknown", [new("ollama", true, "ollama.exe (user-space detected)")]);
var standardResources = AdaptiveRuntimePolicy.Create(standardHardware, "qwen2.5-coder:14b-instruct-q4_K_M");
Check(standardResources.Band == RuntimeResourceBand.Standard && standardResources.RecommendedModel == LocalStudioSettings.DefaultModel, "standard-company-pc-downgrades-to-light-model");
Check(standardResources.HeavyModelConcurrency == 1 && standardResources.TestConcurrency == 1 && !standardResources.AllowBackgroundHeavyVerification, "standard-company-pc-limits-parallel-load");
var constrainedResources = AdaptiveRuntimePolicy.Create(standardHardware with { AvailableMemoryBytes = 2 * GiB, FreeDiskBytes = 4 * GiB }, LocalStudioSettings.DefaultModel);
Check(constrainedResources.Band == RuntimeResourceBand.DeterministicOnly && constrainedResources.RecommendedModel is null, "resource-pressure-degrades-to-deterministic-tools");

// M1: memory is judged by a budget with a reserve and by runtime pressure signals, not by total or start-time free RAM alone.
Check(standardResources.Mode == ResourceMode.Coexistence && standardResources.OllamaKeepAlive == "30s", "coexistence-is-default-and-releases-models-quickly");
var osVisible16 = standardHardware with { InstalledMemoryBytes = (long)(15.6 * GiB) };
Check(AdaptiveRuntimePolicy.Create(osVisible16, LocalStudioSettings.DefaultModel).Band == RuntimeResourceBand.Standard &&
      AdaptiveRuntimePolicy.Create(osVisible16 with { PhysicallyInstalledMemoryBytes = 16 * GiB }, LocalStudioSettings.DefaultModel).Band == RuntimeResourceBand.Standard,
    "os-reported-15-6-gib-keeps-16-gb-tier");
var coexistReserve = MemoryBudgetPolicy.ReserveBytes(16 * GiB, ResourceMode.Coexistence);
Check(coexistReserve >= 3 * GiB / 2 && coexistReserve >= 16 * GiB / 10 && MemoryBudgetPolicy.ReserveBytes(16 * GiB, ResourceMode.Focus) < coexistReserve,
    "reserve-is-larger-of-share-or-floor");
Check(MemoryBudgetPolicy.BudgetBytes(4 * GiB, 16 * GiB, ResourceMode.Focus) > 4 * GiB && MemoryBudgetPolicy.BudgetBytes(4 * GiB, 16 * GiB, ResourceMode.Coexistence) < 4 * GiB,
    "focus-allows-start-budget-above-free-ram-coexistence-keeps-reserve");
var fiveGiBFree = standardHardware with { AvailableMemoryBytes = 5 * GiB };
var coexistAtFive = AdaptiveRuntimePolicy.Create(fiveGiBFree, LocalStudioSettings.DefaultModel);
var focusAtFive = AdaptiveRuntimePolicy.Create(fiveGiBFree, LocalStudioSettings.DefaultModel, ResourceMode.Focus);
// Since 2026-10-09 the 1.5B coder is the last step down before pausing AI (user decision after the bench).
var coexistAtTwo = AdaptiveRuntimePolicy.Create(standardHardware with { AvailableMemoryBytes = 2 * GiB }, LocalStudioSettings.DefaultModel);
Check(coexistAtFive.RecommendedModel == LocalStudioSettings.LightweightModel && coexistAtFive.Reasons.Any(reason => reason.Contains("より小さいモデル", StringComparison.Ordinal)) &&
      coexistAtTwo.Band == RuntimeResourceBand.DeterministicOnly && coexistAtTwo.RecommendedModel is null && coexistAtTwo.Reasons.Any(reason => reason.Contains("一時停止", StringComparison.Ordinal)) &&
      focusAtFive.RecommendedModel == LocalStudioSettings.DefaultModel && focusAtFive.Mode == ResourceMode.Focus,
    "model-runs-only-within-budget-steps-down-to-1-5b-and-focus-widens-it");
var eightGiBPc = AdaptiveRuntimePolicy.Create(standardHardware with { InstalledMemoryBytes = 8 * GiB, AvailableMemoryBytes = 5 * GiB }, LocalStudioSettings.DefaultModel);
Check(eightGiBPc.Band == RuntimeResourceBand.Lightweight && eightGiBPc.RecommendedModel == LocalStudioSettings.LightweightModel,
    "eight-gb-pc-uses-the-1-5b-coder");
var highHardware = standardHardware with { InstalledMemoryBytes = 32 * GiB, AvailableMemoryBytes = 14 * GiB };
var degradedHigh = AdaptiveRuntimePolicy.Create(highHardware, "qwen2.5-coder:14b-instruct-q4_K_M");
Check(degradedHigh.Band == RuntimeResourceBand.HighPerformance && degradedHigh.RecommendedModel == LocalStudioSettings.DefaultModel && degradedHigh.HeavyModelConcurrency == 1,
    "over-budget-model-degrades-to-smaller-model");
MemoryPressureSample Pressure(double availableGiB, int commit = 60, double? hardFaults = 0, long? compressedMiB = 0, bool low = false) =>
    new(DateTimeOffset.UtcNow, (long)(availableGiB * GiB), 16 * GiB, commit, hardFaults, compressedMiB is null ? null : compressedMiB * 1024 * 1024, low);
Check(MemoryBudgetPolicy.Assess([Pressure(8)], 16 * GiB, ResourceMode.Coexistence) is { Level: MemoryPressureLevel.Normal, CanStartHeavyWork: true }, "memory-pressure-normal-allows-heavy-work");
Check(MemoryBudgetPolicy.Assess([Pressure(8, low: true)], 16 * GiB, ResourceMode.Coexistence).Level == MemoryPressureLevel.Critical, "windows-low-memory-notification-is-critical");
Check(MemoryBudgetPolicy.Assess([Pressure(8, commit: 92)], 16 * GiB, ResourceMode.Coexistence).Level == MemoryPressureLevel.Critical, "commit-near-limit-is-critical");
Check(MemoryBudgetPolicy.Assess([Pressure(1.2)], 16 * GiB, ResourceMode.Coexistence) is { Level: MemoryPressureLevel.Critical, CanStartHeavyWork: false }, "free-ram-below-reserve-is-critical");
Check(MemoryBudgetPolicy.Assess(Enumerable.Range(0, 5).Select(_ => Pressure(8, hardFaults: 5000)).ToArray(), 16 * GiB, ResourceMode.Coexistence).Level == MemoryPressureLevel.Normal,
    "hard-faults-alone-are-not-pressure-when-ram-is-free");
Check(MemoryBudgetPolicy.Assess(Enumerable.Range(0, 5).Select(_ => Pressure(2.0, hardFaults: 5000)).ToArray(), 16 * GiB, ResourceMode.Coexistence) is { Level: MemoryPressureLevel.Elevated, CanStartHeavyWork: false },
    "sustained-hard-faults-with-low-ram-stop-expansion");
Check(MemoryBudgetPolicy.Assess([Pressure(2.5, compressedMiB: 500), Pressure(2.5, compressedMiB: 900)], 16 * GiB, ResourceMode.Coexistence).Level == MemoryPressureLevel.Elevated,
    "growing-memory-compression-stops-expansion");
Check(RuntimeFailureClassifier.Classify(new ResourcePressureException("pressure")) == RuntimeFailureKind.EnvironmentRestriction, "memory-pressure-is-environment-restriction");
// Expert mode: nearly all available memory, continues through caution signals, but keeps the last safety line.
Check(MemoryBudgetPolicy.ReserveBytes(16 * GiB, ResourceMode.Expert) == 512L * 1024 * 1024 &&
      MemoryBudgetPolicy.BudgetBytes(4 * GiB, 16 * GiB, ResourceMode.Expert) > MemoryBudgetPolicy.BudgetBytes(4 * GiB, 16 * GiB, ResourceMode.Focus),
    "expert-uses-nearly-all-available-memory");
Check(MemoryBudgetPolicy.Assess([Pressure(4, commit: 88)], 16 * GiB, ResourceMode.Expert) is { Level: MemoryPressureLevel.Elevated, CanStartHeavyWork: true } &&
      !MemoryBudgetPolicy.Assess([Pressure(4, commit: 88)], 16 * GiB, ResourceMode.Coexistence).CanStartHeavyWork,
    "expert-continues-through-caution-signals");
Check(MemoryBudgetPolicy.Assess([Pressure(4, commit: 92)], 16 * GiB, ResourceMode.Expert).Level == MemoryPressureLevel.Elevated &&
      MemoryBudgetPolicy.Assess([Pressure(4, commit: 96)], 16 * GiB, ResourceMode.Expert) is { Level: MemoryPressureLevel.Critical, CanStartHeavyWork: false } &&
      MemoryBudgetPolicy.Assess([Pressure(4, low: true)], 16 * GiB, ResourceMode.Expert).Level == MemoryPressureLevel.Critical &&
      MemoryBudgetPolicy.Assess([Pressure(0.4)], 16 * GiB, ResourceMode.Expert).Level == MemoryPressureLevel.Critical,
    "expert-keeps-last-safety-line");

var ledger = new ExecutionLedger();
ledger.Record(RunPhase.Acting, "replace_text", "app.js", "abc", "NO_CHANGE");
Check(!ledger.WouldRepeatUnchanged("replace_text", "app.js", "abc"), "first-no-change-allowed");
ledger.Record(RunPhase.Acting, "replace_text", "app.js", "abc", "NO_CHANGE");
Check(ledger.WouldRepeatUnchanged("replace_text", "app.js", "abc"), "second-no-change-blocked");

Check(ArchitectureRules.IsAllowed("Presentation", "Application"), "presentation-to-application");
Check(ArchitectureRules.IsAllowed("Verification", "Presentation") && ArchitectureRules.IsAllowed("Verification", "Infrastructure"), "verification-can-join-ui-and-data-layers");
Check(ArchitectureRules.IsAllowed("Host", "Presentation") && ArchitectureRules.IsAllowed("Host", "Infrastructure"), "host-is-explicit-composition-root");
Check(!ArchitectureRules.IsAllowed("Domain", "Infrastructure"), "domain-is-independent");
Check(!ArchitectureRules.IsAllowed("Application", "Presentation"), "no-reverse-ui-dependency");
var developmentPlan = new DevelopmentUnitPlan(1, "Task Creator", [
    new("domain-contracts", "契約", "観測とタスク候補の契約を定義", "Domain", [], ["TC-001"], "contract"),
    new("accessibility-adapter", "観測", "UIAを抽象化して差分を返す", "Infrastructure", ["domain-contracts"], ["TC-002"], "manual-spike"),
    new("candidate-pipeline", "候補化", "差分を絞り構造化候補にする", "Application", ["domain-contracts"], ["TC-003"], "unit-test"),
    new("candidate-ui", "候補画面", "人間が確認・編集・破棄できる", "Presentation", ["candidate-pipeline"], ["TC-004"], "integration-test")
]);
Check(DevelopmentUnitGraph.Validate(developmentPlan).Passed, "development-unit-dag-valid");
Check(DevelopmentUnitGraph.ReadyFrontier(developmentPlan, new HashSet<string>(StringComparer.Ordinal)).SequenceEqual(["domain-contracts"]), "development-unit-frontier-starts-with-prerequisites");
var joinedReleasePlan = developmentPlan with { Units = [
    .. developmentPlan.Units,
    new("release-gate", "Release gate", "Join UI and data verification", "Verification", ["accessibility-adapter", "candidate-ui"], ["TC-001", "TC-002", "TC-003", "TC-004"], "integration-test")
] };
Check(DevelopmentUnitGraph.Validate(joinedReleasePlan).Passed, "verification-release-gate-closes-cross-layer-dag");
var multiStackPlan = developmentPlan with { Units = developmentPlan.Units.Select(unit =>
    unit.Id == "candidate-ui" ? unit with { BuildSystem = "web-static" } : unit).ToArray() };
Check(DevelopmentUnitGraph.Validate(multiStackPlan).Passed, "development-unit-dag-accepts-declared-web-build-system");
var unknownStackPlan = developmentPlan with { Units = [developmentPlan.Units[0] with { BuildSystem = "unknown" }, .. developmentPlan.Units.Skip(1)] };
Check(DevelopmentUnitGraph.Validate(unknownStackPlan).Errors.Contains("unit.build-system:domain-contracts"), "development-unit-dag-rejects-unknown-build-system");
var cyclicPlan = developmentPlan with { Units = [
    developmentPlan.Units[0] with { Dependencies = ["candidate-pipeline"] },
    developmentPlan.Units[2] with { Dependencies = ["domain-contracts"] }
] };
Check(DevelopmentUnitGraph.Validate(cyclicPlan).Errors.Contains("graph.cycle"), "development-unit-cycle-rejected");
var invalidEvidenceLedger = new RequirementLedgerDocument(
    1,
    "portable-project",
    "Portable project",
    DateTimeOffset.UtcNow,
    [new RequirementLedgerItem("REQ-001", "Evidence is mandatory for PASS", true, RequirementState.Passed, [])]);
var invalidEvidenceValidation = RequirementLedgerPolicy.Validate(invalidEvidenceLedger);
Check(!invalidEvidenceValidation.Passed && !invalidEvidenceValidation.CompletionAllowed && invalidEvidenceValidation.Errors.Contains("requirement.pass-without-evidence:REQ-001"), "requirement-ledger-rejects-lucky-pass-without-evidence");
var developmentState = DevelopmentRunCoordinator.Create(developmentPlan, DateTimeOffset.Parse("2026-09-15T00:00:00Z"));
Check(DevelopmentRunCoordinator.ReadyUnits(developmentPlan, developmentState).SequenceEqual(["domain-contracts"]), "development-run-only-starts-ready-unit");
developmentState = DevelopmentRunCoordinator.StartUnit(developmentPlan, developmentState, "domain-contracts", DateTimeOffset.Parse("2026-09-15T00:01:00Z"));
Check(developmentState.Units.Single(unit => unit.UnitId == "domain-contracts").Attempt == 1, "development-run-counts-attempt");
developmentState = DevelopmentRunCoordinator.RecordVerification(developmentState, "domain-contracts", true, "sha256:contracts", null, DateTimeOffset.Parse("2026-09-15T00:02:00Z"));
Check(DevelopmentRunCoordinator.ReadyUnits(developmentPlan, developmentState).OrderBy(id => id).SequenceEqual(["accessibility-adapter", "candidate-pipeline"]), "development-run-unlocks-dependent-frontier");
developmentState = DevelopmentRunCoordinator.StartUnit(developmentPlan, developmentState, "candidate-pipeline");
var recoveredDevelopmentState = DevelopmentRunCoordinator.Recover(developmentPlan, developmentState);
Check(recoveredDevelopmentState.Units.Single(unit => unit.UnitId == "candidate-pipeline").Status == DevelopmentUnitStatus.VerificationFailed, "development-run-recovers-interrupted-unit");
for (var attempt = 1; attempt < DevelopmentRunCoordinator.MaxAttemptsPerUnit; attempt++)
{
    recoveredDevelopmentState = DevelopmentRunCoordinator.StartUnit(developmentPlan, recoveredDevelopmentState, "candidate-pipeline");
    recoveredDevelopmentState = DevelopmentRunCoordinator.RecordVerification(recoveredDevelopmentState, "candidate-pipeline", false, null, "test-failure");
}
Check(recoveredDevelopmentState.Units.Single(unit => unit.UnitId == "candidate-pipeline").Status == DevelopmentUnitStatus.Blocked, "development-run-bounds-repair-attempts");
var classifiedFailureState = DevelopmentRunCoordinator.StartUnit(
    developmentPlan,
    DevelopmentRunCoordinator.Create(developmentPlan),
    "domain-contracts");
classifiedFailureState = DevelopmentRunCoordinator.RecordVerification(
    classifiedFailureState,
    "domain-contracts",
    false,
    null,
    "restore.failed",
    failureKind: DevelopmentFailureKind.RestoreFailure.ToString(),
    failureStage: DevelopmentPipelineStage.Restore.ToString());
var classifiedFailure = classifiedFailureState.Units.Single(unit => unit.UnitId == "domain-contracts");
Check(classifiedFailure.LastFailureKind == "RestoreFailure" && classifiedFailure.LastFailureStage == "Restore", "development-run-persists-structured-failure-classification");
var domainArtifact = new DevelopmentUnitArtifact(1, "domain-contracts", [
    new("src/TaskCreator.Domain/TaskCandidate.cs", "namespace TaskCreator.Domain; public sealed record TaskCandidate(string Title);"),
    new("tests/TaskCreator.Tests/TaskCandidateTests.cs", "namespace TaskCreator.Tests; internal static class TaskCandidateTests { }")
]);
Check(DevelopmentUnitArtifactPolicy.Validate(developmentPlan.Units[0], domainArtifact).Passed, "development-artifact-respects-unit-layer");
var releaseArtifact = new DevelopmentUnitArtifact(1, "release-gate", [
    new("src/TaskCreator.Verification/ReleaseGate.cs", "namespace TaskCreator.Verification; internal static class ReleaseGate { }")
]);
Check(DevelopmentUnitArtifactPolicy.Validate(joinedReleasePlan.Units[^1], releaseArtifact).Passed, "verification-artifact-respects-release-layer");
var webUiUnit = multiStackPlan.Units.Single(unit => unit.Id == "candidate-ui");
var webBuildUnit = webUiUnit with { VerificationKind = "build" };
var webBuildPlan = multiStackPlan with
{
    Units = multiStackPlan.Units.Select(unit => unit.Id == webBuildUnit.Id ? webBuildUnit : unit).ToArray()
};
var webUiArtifact = new DevelopmentUnitArtifact(1, "candidate-ui", [
    new("src/TaskCreator.Presentation/index.html", "<!doctype html><html lang=\"ja\"><title>Task Creator</title><main>候補</main></html>"),
    new("src/TaskCreator.Presentation/styles.css", "main { color: #111; }")
]);
Check(DevelopmentUnitArtifactPolicy.Validate(webUiUnit, webUiArtifact).Passed, "development-artifact-accepts-layer-owned-web-source");
Check(DevelopmentUnitArtifactPolicy.Fingerprint(domainArtifact).Length == 64, "development-artifact-has-stable-fingerprint");
var traversalArtifact = domainArtifact with { Files = [new("../outside.cs", "class Escape { }")] };
Check(DevelopmentUnitArtifactPolicy.Validate(developmentPlan.Units[0], traversalArtifact).Errors.Any(error => error.StartsWith("artifact.unsafe-path", StringComparison.Ordinal)), "development-artifact-rejects-traversal");
var crossLayerArtifact = domainArtifact with { Files = [new("src/TaskCreator.Infrastructure/Leak.cs", "class Leak { }")] };
Check(DevelopmentUnitArtifactPolicy.Validate(developmentPlan.Units[0], crossLayerArtifact).Errors.Any(error => error.StartsWith("artifact.layer-boundary", StringComparison.Ordinal)), "development-artifact-rejects-layer-crossing");
var planEnvelope = JsonSerializer.Serialize(new { message = new { content = JsonSerializer.Serialize(developmentPlan) } });
var planResponses = new SequenceJsonHandler(planEnvelope);
await using (var planRuntime = new FakeLocalModelRuntime(new Uri("http://127.0.0.1:54324/")))
using (var developmentPlanner = new LocalDevelopmentPlanningEngine(
    planRuntime,
    new FakeSettingsStore(LocalStudioSettings.Default),
    new HttpClient(planResponses)))
{
    var generatedPlan = await developmentPlanner.PlanAsync("Task Creator", "詳細なローカルAIアプリを作成する");
    Check(planResponses.RequestCount == 1 && generatedPlan.Units.Count == 4, "local-development-planner-validates-structured-dag");
}
var unitGenerationState = DevelopmentRunCoordinator.StartUnit(
    developmentPlan,
    DevelopmentRunCoordinator.Create(developmentPlan),
    "domain-contracts");
var generatedDomainArtifact = new DevelopmentUnitArtifact(1, "domain-contracts", [
    new("src/TaskCreator.Domain/Observation.cs", "namespace TaskCreator.Domain; public sealed record Observation(string Text);")
]);
var unitResponse = JsonSerializer.Serialize(new
{
    message = new { content = JsonSerializer.Serialize(generatedDomainArtifact) },
    done = true
});
var unitResponses = new SequenceJsonHandler(unitResponse);
await using (var unitRuntime = new FakeLocalModelRuntime(new Uri("http://127.0.0.1:54325/")))
using (var unitGenerator = new LocalDevelopmentUnitGenerationEngine(
    unitRuntime,
    new FakeSettingsStore(LocalStudioSettings.Default),
    new HttpClient(unitResponses)))
{
    var generatedArtifact = await unitGenerator.GenerateAsync(
        developmentPlan,
        unitGenerationState,
        developmentPlan.Units[0],
        "Task Creatorを完全ローカルで作る",
        [],
        null);
    Check(unitResponses.RequestCount == 1 && DevelopmentUnitArtifactPolicy.Validate(developmentPlan.Units[0], generatedArtifact).Passed, "local-development-unit-generator-validates-structured-artifact");
}
Check(!CapabilityPolicy.Evaluate(CapabilityRisk.LocalRead).RequiresConfirmation, "local-read-no-prompt");
Check(CapabilityPolicy.Evaluate(CapabilityRisk.ExternalPublish).RequiresConfirmation, "external-publish-confirmation");

var unsafeBlueprint = new StudioBlueprint("", new string('x', 800), "app", [], [], ["確認事項"], [], ["workspace.read", "admin.elevate"]);
var safeBlueprint = BlueprintValidator.Validate(unsafeBlueprint);
Check(safeBlueprint.Name == "新しいプロジェクト", "blueprint-name-fallback");
Check(safeBlueprint.Summary.Length <= 500, "blueprint-summary-bounded");
Check(safeBlueprint.Features.Count > 0 && safeBlueprint.Steps.Count > 0, "blueprint-required-sections");
Check(safeBlueprint.Capabilities.SequenceEqual(["workspace.read"]), "capability-allowlist");
Check(SettingsStore.IsAllowedModel(LocalStudioSettings.DefaultModel), "default-model-allowed");
Check(!SettingsStore.IsAllowedModel("unknown/model"), "unknown-model-blocked");
Check(ModelSelectionPolicy.FindCompatible("qwen2.5-coder:14b-instruct-q4_K_M", ["qwen2.5-coder:14b"]) == "qwen2.5-coder:14b", "installed-model-variant-compatible");
Check(ModelSelectionPolicy.ResolveForPlanning(LocalStudioSettings.DefaultModel, ["llama3.1:8b", "qwen2.5-coder:14b"]) is null, "resource-policy-never-silently-upgrades-to-larger-model");
Check(ModelSelectionPolicy.ResolveForPlanning("qwen2.5-coder:14b-instruct-q4_K_M", ["qwen2.5-coder:7b"]) == "qwen2.5-coder:7b", "resource-policy-allows-smaller-coder-fallback");
Check(ModelSelectionPolicy.ResolveForPlanning(LocalStudioSettings.DefaultModel, ["llama3.1:8b"]) is null, "non-coder-model-not-selected");

var manifest = Path.Combine(FindRepositoryRoot(), "src", "SafeLocalHarness.App", "app.manifest");
Check(File.Exists(manifest) && File.ReadAllText(manifest).Contains("asInvoker"), "manifest-as-invoker");
var projectFile = Path.Combine(Path.GetDirectoryName(manifest)!, "SafeLocalHarness.App.csproj");
Check(File.ReadAllText(projectFile).Contains("<AssemblyName>LocalStudio</AssemblyName>"), "localstudio-brand");
var setupView = Path.Combine(Path.GetDirectoryName(manifest)!, "Views", "SetupView.xaml");
var setupXaml = File.ReadAllText(setupView);
Check(setupXaml.Contains("Value=\"{Binding Progress, Mode=OneWay}\""), "progress-binding-one-way");
Check(setupXaml.Contains("Maximum=\"1\""), "setup-progress-normalized");
Check(setupXaml.Contains("Text=\"{Binding StoragePath, Mode=OneWay}\""), "storage-path-binding-one-way");
Check(setupXaml.Contains("ProgressDetail"), "setup-progress-detail-visible");
// 3-4: the chat screen replaces the Studio screen (approved mockup and requests of 2026-10-07/09).
var appRoot = Path.GetDirectoryName(manifest)!;
var chatXaml = File.ReadAllText(Path.Combine(appRoot, "Views", "ChatView.xaml"));
Check(!File.Exists(Path.Combine(appRoot, "Views", "StudioView.xaml")) &&
      chatXaml.Contains("さて、何をしましょう？") && chatXaml.Contains("LocalStudio.png") && chatXaml.Contains("AllowDrop=\"True\"") &&
      chatXaml.Contains("SendCommand") && chatXaml.Contains("ChooseCommand") && chatXaml.Contains("ActivityLine") && chatXaml.Contains("InputHint") &&
      chatXaml.Contains("ApplicationCommands.Paste"),
    "chat-screen-has-empty-state-choices-progress-and-attachments");
var allAppMarkup = string.Join("\n", Directory.EnumerateFiles(appRoot, "*.xaml", SearchOption.AllDirectories)
    .Concat(Directory.EnumerateFiles(Path.Combine(appRoot, "ViewModels"), "*.cs"))
    .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
    .Select(File.ReadAllText));
Check(!chatXaml.Contains("<ComboBox") && !allAppMarkup.Contains("プライベート") && !allAppMarkup.Contains("戻せる設計") &&
      !allAppMarkup.Contains("完全ローカル") && !allAppMarkup.Contains("自律実装として作成"),
    "kind-selector-info-panels-and-old-wording-removed");
var paletteXaml = File.ReadAllText(Path.Combine(appRoot, "App.xaml"));
Check(new[] { ("SidebarBrush", "#FEF1E0"), ("CanvasBrush", "#FFF6ED"), ("ChatSurfaceBrush", "#FCFCFA"), ("DividerBrush", "#9F958B"), ("HeadingBrush", "#5F584E"),
              ("PrimaryTextBrush", "#3D3833"), ("SecondaryTextBrush", "#8A8178"), ("SelectedProjectBrush", "#F7DFC2"), ("UserBubbleBrush", "#EFECE7") }
          .All(pair => paletteXaml.Contains($"x:Key=\"{pair.Item1}\" Color=\"{pair.Item2}\"", StringComparison.Ordinal)) &&
      paletteXaml.Contains("Yu Mincho", StringComparison.Ordinal),
    "palette-matches-the-approved-sample");
var projectsXaml = File.ReadAllText(Path.Combine(Path.GetDirectoryName(manifest)!, "Views", "ProjectsView.xaml"));
Check(projectsXaml.Contains("Binding=\"{Binding VerificationApplicable}\"") && projectsXaml.Contains("Value=\"True\""), "verification-actions-only-visible-when-applicable");
Check(projectsXaml.Contains("Property=\"IsSelected\"") && projectsXaml.Contains("#EAF3FF"), "verification-selected-feature-visible");
Check(projectsXaml.Contains("ConfirmFeatureLabel") && projectsXaml.Contains("ReportFeatureLabel"), "verification-action-labels-reflect-current-result");
Check(projectsXaml.Contains("AutomationProperties.LiveSetting=\"Polite\"") && projectsXaml.Contains("AutomationProperties.Name=\"確認する機能\""), "verification-status-and-list-accessible");
Check(projectsXaml.Contains("ImproveProjectCommand") && projectsXaml.Contains("このアプリを改善"), "project-improvement-entrypoint-visible");
var projectsViewModelSource = File.ReadAllText(Path.Combine(Path.GetDirectoryName(manifest)!, "ViewModels", "ProjectsViewModel.cs"));
Check(projectsViewModelSource.Contains("CanConfirmFeature") && projectsViewModelSource.Contains("CanReportFeature"), "verification-same-result-action-disabled");
Check(projectsViewModelSource.Contains("item.Id == feature.Id"), "verification-selection-preserved-after-recording");
var shellViewModelSource = File.ReadAllText(Path.Combine(Path.GetDirectoryName(manifest)!, "ViewModels", "ShellViewModel.cs"));
// "Improve this app" now continues the project's conversation in the chat.
Check(shellViewModelSource.Contains("ImprovementRequested") && shellViewModelSource.Contains("SidebarSelection = project") &&
      shellViewModelSource.Contains("Chat.OpenProjectAsync"), "project-improvement-navigation-wired");
var mainWindowXaml = File.ReadAllText(Path.Combine(Path.GetDirectoryName(manifest)!, "MainWindow.xaml"));
Check(mainWindowXaml.Contains("SelectedChat") && mainWindowXaml.Contains("OnRemoveChat") && mainWindowXaml.Contains("OnExportChatHtml") && mainWindowXaml.Contains("NewChatCommand") && mainWindowXaml.Contains("ShowProjectDetailsCommand") &&
      mainWindowXaml.Contains("Projects.OpenAppCommand") && mainWindowXaml.Contains("LocalStudio.png"), "projects-navigation-enabled");
var appXaml = File.ReadAllText(Path.Combine(Path.GetDirectoryName(manifest)!, "App.xaml"));
Check(appXaml.Contains("DispatcherUnhandledException=\"OnDispatcherUnhandledException\""), "ui-exception-boundary");
var appStartup = File.ReadAllText(Path.Combine(Path.GetDirectoryName(manifest)!, "App.xaml.cs"));
Check(appStartup.Contains("--setup") && appStartup.Contains("ShowSetupCommand.Execute"), "setup-view-test-entrypoint");
var viewModelRoot = Path.Combine(Path.GetDirectoryName(manifest)!, "ViewModels");
var presentationSource = string.Join("\n", Directory.EnumerateFiles(viewModelRoot, "*.cs").Select(File.ReadAllText));
foreach (var forbidden in new[] { "OllamaManager", "AppPaths", "SettingsStore", "ProjectScaffolder", "LocalPlanningEngine" })
    Check(!System.Text.RegularExpressions.Regex.IsMatch(presentationSource, $@"(?<!I)\b{forbidden}\b"), "presentation-no-concrete-" + forbidden);
var coreRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(manifest)!, "..", "SafeLocalHarness.Core"));
var runtimeSource = File.ReadAllText(Path.Combine(coreRoot, "OllamaManager.cs")) + File.ReadAllText(Path.Combine(coreRoot, "LocalPlanningEngine.cs"));
Check(!runtimeSource.Contains("127.0.0.1:11434", StringComparison.Ordinal), "no-shared-default-ollama-port");
Check(runtimeSource.Contains("FindAvailableLoopbackPort", StringComparison.Ordinal), "isolated-dynamic-ollama-port");

// Memory: a smaller attention cache, prompts without escape bloat, and a fit check before any model load.
Check(runtimeSource.Contains("\"OLLAMA_KV_CACHE_TYPE\"", StringComparison.Ordinal) && runtimeSource.Contains("\"OLLAMA_FLASH_ATTENTION\"", StringComparison.Ordinal) &&
      runtimeSource.Contains("EnsureModelFitsAsync(model", StringComparison.Ordinal),
    "owned-runtime-uses-quantized-kv-cache-and-checks-fit-before-loading");
Check(ModelMemoryEstimator.RequiredBytes(LocalStudioSettings.DefaultModel, 8192, "q8_0") < ModelMemoryEstimator.RequiredBytes(LocalStudioSettings.DefaultModel, 8192) &&
      ModelFitPolicy.Fits(4L << 30, 5L << 30) && !ModelFitPolicy.Fits(5L << 30, 4L << 30) && !ModelFitPolicy.Fits(long.MaxValue, long.MaxValue) &&
      ModelFitPolicy.Explain(4L << 30, 3L << 30).Contains("他のアプリ", StringComparison.Ordinal),
    "model-fit-is-decided-before-loading");
var promptJson = JsonSerializer.Serialize(new { text = "メモを1行ずつ追加" }, ModelPromptJson.Options);
Check(promptJson.Contains("メモを1行ずつ追加", StringComparison.Ordinal) && !promptJson.Contains("\\u", StringComparison.Ordinal) &&
      JsonSerializer.Serialize(new { text = "メモ" }).Contains("\\u", StringComparison.Ordinal),
    "prompt-json-keeps-japanese-readable-for-fewer-tokens");

// The user's persistent environment must not change (2026-10-07: the SDK first run had appended 46 entries to the user PATH).
var userPathBeforeSmoke = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User);
var smokeRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalStudio", "smoke-" + Guid.NewGuid().ToString("N"));
var smokePaths = new AppPaths(smokeRoot, Path.Combine(smokeRoot, "runtime"), Path.Combine(smokeRoot, "models"), Path.Combine(smokeRoot, "state"), Path.Combine(smokeRoot, "logs"), Path.Combine(smokeRoot, "downloads"), Path.Combine(smokeRoot, "projects"));
smokePaths.EnsureCreated();
var developmentStatePath = Path.Combine(smokePaths.StateRoot, "development-run.json");
await DevelopmentRunCoordinator.SaveAtomicAsync(developmentStatePath, developmentState);
await DevelopmentRunCoordinator.SaveAtomicAsync(developmentStatePath, recoveredDevelopmentState);
var loadedDevelopmentState = await DevelopmentRunCoordinator.LoadAsync(developmentStatePath);
Check(loadedDevelopmentState.Units.Single(unit => unit.UnitId == "candidate-pipeline").Status == DevelopmentUnitStatus.Blocked, "development-run-state-saves-atomically");
Check(File.Exists(developmentStatePath + ".previous"), "development-run-state-keeps-previous-version");
var executionPlan = new DevelopmentUnitPlan(1, "Execution smoke", [
    new("contracts", "Contracts", "Create domain contracts", "Domain", [], ["EX-001"], "contract"),
    new("pipeline", "Pipeline", "Create application pipeline", "Application", ["contracts"], ["EX-002"], "unit-test")
]);
var executionState = DevelopmentRunCoordinator.Create(executionPlan);
var executionStatePath = Path.Combine(smokePaths.StateRoot, "development-execution.json");
var executionStoreRoot = Path.Combine(smokePaths.StateRoot, "development-artifacts");
var executionGenerator = new SmokeDevelopmentUnitGenerator();
var executionVerifier = new FailFirstDevelopmentUnitVerifier("contracts");
var executionResult = await new DevelopmentUnitExecutionEngine(
    executionGenerator,
    executionVerifier,
    new DevelopmentUnitCandidateStore(executionStoreRoot)).RunAsync(
        executionPlan,
        executionState,
        executionStatePath,
        "Build a bounded local application");
Check(executionResult.Completed && executionResult.BlockedUnitIds.Count == 0, "development-executor-completes-dependency-ordered-units");
Check(executionResult.State.Units.Single(unit => unit.UnitId == "contracts").Attempt == 2, "development-executor-repairs-failed-unit-before-continuing");
Check(executionResult.State.Units.Single(unit => unit.UnitId == "pipeline").Attempt == 1, "development-executor-unlocks-dependent-unit-after-acceptance");
Check(Directory.EnumerateFiles(executionStoreRoot, "artifact.json", SearchOption.AllDirectories).Count() == 2, "development-executor-keeps-only-fingerprinted-candidates");
Check(File.Exists(executionStatePath + ".previous"), "development-executor-persists-recoverable-state");
var assembledDevelopmentProject = Path.Combine(smokePaths.ProjectRoot, "execution-smoke-assembled");
var assemblyResult = await new DevelopmentProjectAssembler(new DevelopmentUnitCandidateStore(executionStoreRoot)).AssembleAsync(
    executionPlan,
    executionResult.State,
    new DevelopmentProjectAssemblyRequest(
        assembledDevelopmentProject,
        "execution-smoke-assembled",
        "Execution smoke",
        "検証済み開発単位を安全に統合する",
        "Build a bounded local application",
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["EX-001"] = "ドメイン契約を定義する",
            ["EX-002"] = "依存契約を使う処理を実装する"
        },
        [".NET"],
        ["dotnet build"],
        ["単一release gateが全開発単位を包含してからStableへ昇格する"]));
// External review 2026-10-26: a passing DAG is recorded, but no requirement is called passed without its own test.
var assembledLedger = await RequirementLedgerStore.LoadAsync(assembledDevelopmentProject);
Check(!assemblyResult.CompletionAllowed && Directory.Exists(assembledDevelopmentProject) &&
      assembledLedger.Items.All(item => item.State == RequirementState.NotTested && item.Evidence.Count > 0),
    "development-assembler-records-the-dag-but-passes-no-untested-requirement");
Check(File.Exists(Path.Combine(assembledDevelopmentProject, ".localstudio", "baseline", "manifest.json")), "development-assembler-creates-rollback-baseline");
var assembledInspection = await ProjectCartridgeStore.InspectAsync(assembledDevelopmentProject);
Check(assembledInspection.CanResume && assembledInspection.Cartridge.VerificationStatus == "NOT_TESTED", "assembled-project-cartridge-can-resume");
var sessionRoot = Path.Combine(smokePaths.StateRoot, "development-session");
var sessionDestination = Path.Combine(smokePaths.ProjectRoot, "session-service-assembled");
var sessionStore = new DevelopmentUnitCandidateStore(Path.Combine(sessionRoot, "artifacts"));
var sessionPlanner = new CountingDevelopmentPlanner(executionPlan);
var sessionService = new DevelopmentSessionService(
    sessionPlanner,
    new DevelopmentUnitExecutionEngine(
        new SmokeDevelopmentUnitGenerator(),
        new FailFirstDevelopmentUnitVerifier("contracts"),
        sessionStore),
    sessionStore);
var sessionRequirements = new Dictionary<string, string>(StringComparer.Ordinal)
{
    ["EX-001"] = "Define the domain contract",
    ["EX-002"] = "Implement the dependency-aware pipeline"
};
var initialSessionRequest = new DevelopmentSessionRequest(
    sessionRoot,
    sessionDestination,
    "session-service-assembled",
    "Session service smoke",
    "Resume a bounded development session without losing evidence.",
    "Build a bounded local application",
    sessionRequirements,
    [".NET"],
    ["dotnet build"],
    ["A passing DAG does not replace requirement-specific evidence."],
    executionPlan);
var pausedSession = await sessionService.RunAsync(initialSessionRequest, maximumUnitAttempts: 1);
Check(pausedSession.Stage == DevelopmentSessionStage.Paused &&
      pausedSession.State.Units.Single(unit => unit.UnitId == "contracts").Status == DevelopmentUnitStatus.VerificationFailed &&
      !Directory.Exists(sessionDestination),
    "development-session-pauses-durably-without-assembling-a-failed-dag");
var resumedSessionRequest = initialSessionRequest with { Plan = null };
var resumedSession = await sessionService.RunAsync(resumedSessionRequest);
Check(resumedSession.Stage == DevelopmentSessionStage.AssembledAwaitingRequirementEvidence &&
      resumedSession.State.Units.All(unit => unit.Status == DevelopmentUnitStatus.Accepted) &&
      sessionPlanner.CallCount == 0 &&
      File.Exists(pausedSession.PlanPath) && File.Exists(pausedSession.StatePath + ".previous"),
    "development-session-resumes-saved-plan-and-state-without-replanning");
var sessionLedger = await RequirementLedgerStore.LoadAsync(sessionDestination);
Check(!resumedSession.CompletionAllowed && sessionLedger.Items.All(item => item.State == RequirementState.NotTested),
    "development-session-does-not-promote-dag-pass-to-requirement-pass");
var reopenedSession = await sessionService.RunAsync(resumedSessionRequest);
Check(reopenedSession.Stage == DevelopmentSessionStage.AssembledAwaitingRequirementEvidence &&
      reopenedSession.ProjectRoot == sessionDestination && sessionPlanner.CallCount == 0,
    "development-session-reopens-its-existing-cartridge-idempotently");
var requirementVerifier = new ScriptedRequirementVerifier(
    ("TEST-EX-001-STATIC", new DeterministicRequirementVerification(true, "deterministic-static", "Domain static checks passed.", Encoding.UTF8.GetBytes("EX-001-STATIC:PASS"))),
    ("TEST-EX-002", new DeterministicRequirementVerification(false, "deterministic-unit", "Pipeline test failed.", Encoding.UTF8.GetBytes("EX-002:FAIL"))),
    ("TEST-EX-002", new DeterministicRequirementVerification(true, "deterministic-unit", "Pipeline test passed after repair.", Encoding.UTF8.GetBytes("EX-002:PASS"))));
await CheckThrowsAsync(
    () => new RequirementVerificationService(requirementVerifier).VerifyAsync(sessionDestination, "TEST-EX-001"),
    "dag-release-gate-cannot-be-used-as-requirement-specific-evidence");
CheckThrows(
    () => _ = new RequirementVerifierRegistry([new("dag-release-gate", requirementVerifier)]),
    "requirement-verifier-registry-rejects-generic-dag-evidence");
var preVerificationInspection = await ProjectCartridgeStore.InspectAsync(sessionDestination);
var specificDefinitions = preVerificationInspection.Cartridge.TestDefinitions
    .Select(definition => definition.RequirementId == "EX-001"
        ? definition with
        {
            Kind = ProjectFileInvariantRequirementVerifier.Kind,
            Specification = new ProjectTestSpecification(1, [
                new("file-exists", "src/Smoke.Domain/contracts.cs"),
                new("utf8-contains-exact", "src/Smoke.Domain/contracts.cs", "internal static class contracts")
            ])
        }
        : definition with { Kind = "smoke-requirement-check" })
    .Append(new ProjectTestDefinition(
        "TEST-EX-001-STATIC",
        "EX-001",
        "smoke-requirement-check",
        "A second independent deterministic check for the same requirement."))
    .ToArray();
var specificResults = specificDefinitions.Select(definition => new ProjectTestResult(
    definition.Id,
    "NOT_TESTED",
    ".localstudio/verification/development-units.json",
    preVerificationInspection.Cartridge.UpdatedAt)).ToArray();
await ProjectCartridgeStore.SaveAtomicAsync(sessionDestination, preVerificationInspection.Cartridge with
{
    TestDefinitions = specificDefinitions,
    TestResults = specificResults
});
var projectFileVerifier = new ProjectFileInvariantRequirementVerifier();
var missingFileOutcome = await projectFileVerifier.VerifyAsync(
    sessionDestination,
    new ProjectTestDefinition(
        "TEST-MISSING-FILE",
        "EX-001",
        ProjectFileInvariantRequirementVerifier.Kind,
        "A missing file must fail without running a process.",
        new ProjectTestSpecification(1, [new("file-exists", "src/Smoke.Domain/missing.cs")])));
Check(!missingFileOutcome.Passed && Encoding.UTF8.GetString(missingFileOutcome.EvidenceContent.Span).Contains("file-missing", StringComparison.Ordinal),
    "project-file-invariant-records-a-deterministic-missing-file-failure");
await CheckThrowsAsync(
    () => projectFileVerifier.VerifyAsync(
        sessionDestination,
        new ProjectTestDefinition(
            "TEST-CONTROL-DATA",
            "EX-001",
            ProjectFileInvariantRequirementVerifier.Kind,
            "Control data is not requirement evidence.",
            new ProjectTestSpecification(1, [new("file-exists", ".localstudio/localstudio.json")]))),
    "project-file-invariant-refuses-localstudio-control-data");
var requirementRegistry = new RequirementVerifierRegistry([
    new(ProjectFileInvariantRequirementVerifier.Kind, projectFileVerifier),
    new("smoke-requirement-check", requirementVerifier)
]);
Check(requirementRegistry.SupportedKinds.SequenceEqual([ProjectFileInvariantRequirementVerifier.Kind, "smoke-requirement-check"]),
    "requirement-verifier-registry-exposes-only-explicit-kinds");
CheckThrows(
    () => _ = new RequirementVerifierRegistry([
        new("smoke-requirement-check", requirementVerifier),
        new("smoke-requirement-check", requirementVerifier)
    ]),
    "requirement-verifier-registry-rejects-duplicate-kinds");
await CheckThrowsAsync(
    () => requirementRegistry.VerifyAsync(
        sessionDestination,
        new ProjectTestDefinition("TEST-UNSUPPORTED", "EX-001", "unknown-verifier", "Must fail closed.")),
    "requirement-verifier-registry-has-no-unsafe-fallback");
var requirementVerification = new RequirementVerificationService(requirementRegistry);
await CheckThrowsAsync(
    () => requirementVerification.VerifyAsync(sessionDestination, "TEST-UNKNOWN"),
    "requirement-verification-refuses-tests-not-defined-by-the-cartridge");
var firstRequirement = await requirementVerification.VerifyAsync(sessionDestination, "TEST-EX-001");
Check(firstRequirement.State == RequirementState.NotTested && !firstRequirement.CompletionAllowed,
    "one-passing-test-does-not-pass-a-requirement-that-has-another-untested-test");
var secondRequirementTest = await requirementVerification.VerifyAsync(sessionDestination, "TEST-EX-001-STATIC");
Check(secondRequirementTest.State == RequirementState.Passed && !secondRequirementTest.CompletionAllowed,
    "a-requirement-passes-only-after-all-of-its-tests-pass");
var failedRequirement = await requirementVerification.VerifyAsync(sessionDestination, "TEST-EX-002");
Check(failedRequirement.State == RequirementState.Failed && !failedRequirement.CompletionAllowed,
    "failed-deterministic-verification-is-recorded-and-does-not-complete");
var repairedRequirement = await requirementVerification.VerifyAsync(sessionDestination, "TEST-EX-002");
var completedLedger = await RequirementLedgerStore.LoadAsync(sessionDestination);
var completedInspection = await ProjectCartridgeStore.InspectAsync(sessionDestination);
var evidenceFullPath = Path.Combine(sessionDestination, repairedRequirement.EvidenceRelativePath.Replace('/', Path.DirectorySeparatorChar));
var evidenceFileHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(evidenceFullPath))).ToLowerInvariant();
Check(repairedRequirement.CompletionAllowed && repairedRequirement.State == RequirementState.Passed &&
      RequirementLedgerPolicy.Validate(completedLedger).CompletionAllowed &&
      RequirementLedgerPolicy.Validate(completedInspection.Cartridge.RequirementLedger).CompletionAllowed &&
      completedInspection.Cartridge.TestResults.All(result => result.Status == "PASS") &&
      evidenceFileHash == repairedRequirement.EvidenceSha256,
    "deterministic-requirement-evidence-completes-only-after-all-mandatory-tests-pass");
var completedSession = await sessionService.RunAsync(resumedSessionRequest);
Check(completedSession.Stage == DevelopmentSessionStage.Complete && completedSession.CompletionAllowed,
    "development-session-observes-requirement-evidence-and-transitions-to-complete");
var contractCandidateRoot = Directory.EnumerateDirectories(Path.Combine(executionStoreRoot, "candidates", "contracts")).Single();
var staticContractVerification = await new DevelopmentUnitStaticVerifier().VerifyAsync(executionPlan, executionPlan.Units[0], contractCandidateRoot);
Check(staticContractVerification.Passed, "development-static-verifier-accepts-layer-safe-contract");
var manualSpikeUnit = executionPlan.Units[0] with { VerificationKind = "manual-spike" };
var manualSpikeVerification = await new DevelopmentUnitStaticVerifier().VerifyAsync(executionPlan, manualSpikeUnit, contractCandidateRoot);
Check(!manualSpikeVerification.Passed && manualSpikeVerification.Summary.Contains("manual-spike-required", StringComparison.Ordinal), "development-static-verifier-does-not-fake-manual-spike-success");
var buildArtifact = new DevelopmentUnitArtifact(1, "contracts", [
    new("src/Smoke.Domain/Smoke.Domain.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><AllowUnsafeBlocks>false</AllowUnsafeBlocks></PropertyGroup></Project>"),
    new("src/Smoke.Domain/Contract.cs", "namespace Smoke.Domain; public sealed record Contract(string Value);")
]);
var buildStore = new DevelopmentUnitCandidateStore(executionStoreRoot);
var savedBuildArtifact = await buildStore.SaveAsync(executionPlan.Units[0], buildArtifact);
using (var buildWorkspace = await buildStore.ComposeVerificationWorkspaceAsync(buildArtifact, []))
{
    var buildVerification = await new DevelopmentUnitBuildVerifier(Environment.ProcessPath!).VerifyAsync(
        executionPlan,
        executionPlan.Units[0],
        buildWorkspace.Root);
    Check(buildVerification.Passed, "development-build-verifier-compiles-safe-sdk-project");
}
var pipelineBuildUnit = executionPlan.Units[1] with { VerificationKind = "build" };
var pipelineBuildPlan = executionPlan with
{
    Units = executionPlan.Units.Select(unit => unit.Id == pipelineBuildUnit.Id ? pipelineBuildUnit : unit).ToArray()
};
var dependentBuildArtifact = new DevelopmentUnitArtifact(1, "pipeline", [
    new("src/Smoke.Application/Smoke.Application.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><AllowUnsafeBlocks>false</AllowUnsafeBlocks></PropertyGroup><ItemGroup><ProjectReference Include=\"..\\Smoke.Domain\\Smoke.Domain.csproj\" /></ItemGroup></Project>"),
    new("src/Smoke.Application/Pipeline.cs", "using Smoke.Domain; namespace Smoke.Application; public sealed class Pipeline { public Contract Create() => new(\"safe\"); }")
]);
await buildStore.SaveAsync(pipelineBuildUnit, dependentBuildArtifact);
using (var dependentBuildWorkspace = await buildStore.ComposeVerificationWorkspaceAsync(dependentBuildArtifact, [buildArtifact]))
{
    var dependentBuildVerification = await new DevelopmentUnitBuildVerifier(Environment.ProcessPath!).VerifyAsync(
        pipelineBuildPlan,
        pipelineBuildUnit,
        dependentBuildWorkspace.Root);
    Check(dependentBuildVerification.Passed, "development-build-verifier-builds-project-reference-dag-once-in-dependency-order");
}
var unsafeProjectPath = Path.Combine(smokePaths.StateRoot, "unsafe.csproj");
await File.WriteAllTextAsync(unsafeProjectPath, "<Project Sdk=\"Microsoft.NET.Sdk\"><Target Name=\"Unsafe\"><Exec Command=\"cmd.exe\" /></Target><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
Check(SafeDevelopmentProjectPolicy.Validate(smokePaths.StateRoot, unsafeProjectPath).Any(), "development-build-verifier-rejects-msbuild-code-execution");
var nodePolicyRoot = Path.Combine(smokePaths.StateRoot, "node-policy");
Directory.CreateDirectory(nodePolicyRoot);
var safePackagePath = Path.Combine(nodePolicyRoot, "package.json");
await File.WriteAllTextAsync(safePackagePath, """
    {"name":"safe-ui","private":true,"version":"1.0.0","scripts":{"build":"vite build"},"dependencies":{"react":"19.1.1"}}
    """);
Check(SafeNodeProjectPolicy.Validate(nodePolicyRoot, safePackagePath).Count == 0, "node-policy-accepts-private-project-with-pinned-dependencies");
await File.WriteAllTextAsync(safePackagePath, """
    {"name":"unsafe-script-name","private":true,"version":"1.0.0","scripts":{"test":"vite build"}}
    """);
Check(SafeNodeProjectPolicy.Validate(nodePolicyRoot, safePackagePath).Any(error => error == "node.script-not-approved:test"), "node-policy-binds-approved-command-to-approved-script-name");
await File.WriteAllTextAsync(safePackagePath, """
    {"name":"unsafe-ui","private":true,"version":"1.0.0","scripts":{"postinstall":"powershell.exe"},"dependencies":{"react":"^19.1.1"}}
    """);
var unsafeNodeErrors = SafeNodeProjectPolicy.Validate(nodePolicyRoot, safePackagePath);
Check(unsafeNodeErrors.Any(error => error.StartsWith("node.lifecycle-script", StringComparison.Ordinal)) &&
      unsafeNodeErrors.Any(error => error.StartsWith("node.dependency-not-pinned", StringComparison.Ordinal)), "node-policy-rejects-lifecycle-script-and-floating-dependency");
var webPolicyRoot = Path.Combine(smokePaths.StateRoot, "web-policy");
Directory.CreateDirectory(webPolicyRoot);
await File.WriteAllTextAsync(Path.Combine(webPolicyRoot, "index.html"), "<script src=\"https://example.invalid/app.js\"></script>");
Check(SafeWebAssetPolicy.Validate(webPolicyRoot).Any(error => error.StartsWith("web.remote-reference", StringComparison.Ordinal)), "web-policy-rejects-remote-runtime-assets");
var webStore = new DevelopmentUnitCandidateStore(Path.Combine(smokePaths.StateRoot, "web-artifacts"));
var savedWebArtifact = await webStore.SaveAsync(webBuildUnit, webUiArtifact);
using (var webWorkspace = await webStore.ComposeVerificationWorkspaceAsync(webUiArtifact, []))
{
    var webVerification = await new DevelopmentUnitBuildVerifier(new DevelopmentBuildToolchain(null, null, null)).VerifyAsync(
        webBuildPlan,
        webBuildUnit,
        webWorkspace.Root);
    Check(webVerification.Passed, "web-static-verifier-does-not-require-dotnet-or-admin-install:" + webVerification.Stage + ":" + webVerification.Summary);
}
var stackRegistry = DevelopmentStackRegistry.CreateDefault(new DevelopmentBuildToolchain(Environment.ProcessPath!, null, null));
var stackCapabilities = stackRegistry.InspectCapabilities();
Check(stackCapabilities.Count == 5 && stackRegistry.Resolve("dotnet").Profile.QualifiedForStablePromotion, "stack-registry-exposes-general-capability-profiles");
Check(!stackRegistry.Resolve("tauri").Profile.QualifiedForStablePromotion, "tauri-remains-experimental-instead-of-being-fixed-as-default");
// A new per-run DOTNET_CLI_HOME must not add a global-tools path, create an ASP.NET development certificate, or
// enable telemetry as a side effect. The certificate message was observed during isolated MSB3374 diagnosis on
// 2026-10-28 when a manual command omitted these product-owned environment guards.
var dotnetEnvironment = DotnetRestoreAdapter.EnvironmentFor(
    new DevelopmentStackContext(pipelineBuildUnit, webPolicyRoot, stackRegistry.Toolchain));
Check(dotnetEnvironment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] == "1" &&
      dotnetEnvironment["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] == "false" &&
      dotnetEnvironment["DOTNET_GENERATE_ASPNET_CERTIFICATE"] == "false" &&
      dotnetEnvironment["DOTNET_CLI_TELEMETRY_OPTOUT"] == "1" &&
      dotnetEnvironment["MSBUILDDISABLENODEREUSE"] == "1" &&
      !string.IsNullOrWhiteSpace(dotnetEnvironment["DOTNET_CLI_HOME"]),
    "dotnet-adapter-disables-first-run-certificate-path-and-telemetry-side-effects");
var missingCargo = await stackRegistry.Resolve("cargo").VerifyAsync(
    new DevelopmentStackContext(webUiUnit with { BuildSystem = "cargo" }, webPolicyRoot, stackRegistry.Toolchain),
    CancellationToken.None);
Check(!missingCargo.Passed && missingCargo.FailureKind == DevelopmentFailureKind.ToolchainMissing && missingCargo.Stage == DevelopmentPipelineStage.Capability, "stack-pipeline-classifies-missing-toolchain");
// External review 2026-10-26: Node ran Tauri's stages (so it asked for Cargo.toml), and a unit that asked for tests
// passed with none. A Node package with nothing to install or run never starts the executable named here.
var nodeOnlyRoot = Path.Combine(smokePaths.StateRoot, "node-only");
Directory.CreateDirectory(nodeOnlyRoot);
await File.WriteAllTextAsync(Path.Combine(nodeOnlyRoot, "package.json"), "{\"name\":\"node-only\",\"version\":\"1.0.0\",\"private\":true}");
var nodeRegistry = DevelopmentStackRegistry.CreateDefault(new DevelopmentBuildToolchain(null, Environment.ProcessPath!, null));
var nodeBuilt = await nodeRegistry.Resolve("node").VerifyAsync(new DevelopmentStackContext(webUiUnit with { BuildSystem = "node", VerificationKind = "build" }, nodeOnlyRoot, nodeRegistry.Toolchain), CancellationToken.None);
var nodeUntested = await nodeRegistry.Resolve("node").VerifyAsync(new DevelopmentStackContext(webUiUnit with { BuildSystem = "node", VerificationKind = "unit-test" }, nodeOnlyRoot, nodeRegistry.Toolchain), CancellationToken.None);
Check(nodeBuilt.Passed && !nodeUntested.Passed && nodeUntested.Stage == DevelopmentPipelineStage.Test && nodeUntested.Summary.StartsWith("node.test-script-missing", StringComparison.Ordinal),
    "node-stack-runs-node-stages-and-asked-for-tests-are-required:" + nodeBuilt.Summary + " / " + nodeUntested.Summary);
var scriptedWebArtifact = webUiArtifact with
{
    Files = [.. webUiArtifact.Files, new("src/TaskCreator.Presentation/app.js", "const message = 'safe'; console.log(message);")]
};
using (var scriptedWebWorkspace = await webStore.ComposeVerificationWorkspaceAsync(scriptedWebArtifact, []))
{
    var missingNode = await new DevelopmentUnitBuildVerifier(new DevelopmentBuildToolchain(null, null, null)).VerifyAsync(
        webBuildPlan,
        webBuildUnit,
        scriptedWebWorkspace.Root);
    Check(!missingNode.Passed && missingNode.FailureKind == DevelopmentFailureKind.ToolchainMissing && missingNode.Stage == DevelopmentPipelineStage.Test, "stack-pipeline-classifies-conditional-toolchain-requirement:" + missingNode.Stage + ":" + missingNode.FailureKind + ":" + missingNode.Summary);
}
var abandonedStaging = Path.Combine(smokePaths.ProjectRoot, ".staging-abandoned-smoke");
Directory.CreateDirectory(abandonedStaging);
await File.WriteAllTextAsync(Path.Combine(abandonedStaging, "partial.txt"), "recoverable partial artifact");
Directory.SetLastWriteTimeUtc(abandonedStaging, DateTime.UtcNow.AddDays(-2));
var quarantinedStaging = smokePaths.QuarantineAbandonedProjectStaging();
Check(!Directory.Exists(abandonedStaging) && quarantinedStaging.Count == 1 && File.Exists(Path.Combine(quarantinedStaging[0], "partial.txt")), "abandoned-staging-is-quarantined-not-deleted");
try
{
    var machinePathBefore = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine);
    var userPathBefore = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User);
    var phase1Root = Path.Combine(smokeRoot, "phase1-runtime-tests");
    var phase1Paths = new AppPaths(
        phase1Root,
        Path.Combine(phase1Root, "runtime"),
        Path.Combine(phase1Root, "models"),
        Path.Combine(phase1Root, "state"),
        Path.Combine(phase1Root, "logs"),
        Path.Combine(phase1Root, "downloads"),
        Path.Combine(phase1Root, "projects"));
    phase1Paths.EnsureCreated();
    var source = new Uri("https://github.com/ollama/ollama/releases/download/v0.0.0/runtime.zip");
    var archiveV1 = RuntimeTestArchive.Create("version-one");
    var archiveV2 = RuntimeTestArchive.Create("version-two");
    var artifactV1 = RuntimeTestArtifact.Create("runtime-test", "1.0.0", source, archiveV1);
    var artifactV2 = RuntimeTestArtifact.Create("runtime-test", "2.0.0", source, archiveV2);
    var verifier = new FakeRuntimeSmokeVerifier();
    var managerV1 = new RuntimeArtifactManager(phase1Paths, downloader: new ScriptedRuntimeDownloader(archiveV1));
    var promotedV1 = await managerV1.ProvisionAsync(artifactV1, verifier);
    Check(promotedV1.Passed && promotedV1.Receipt.ExpectedSha256 == promotedV1.Receipt.ActualSha256, "runtime-candidate-hash-verified-before-promotion");
    var managerV2 = new RuntimeArtifactManager(phase1Paths, downloader: new ScriptedRuntimeDownloader(archiveV2));
    var promotedV2 = await managerV2.ProvisionAsync(artifactV2, verifier);
    Check(promotedV2.Passed && promotedV2.Receipt.RollbackPath is not null, "runtime-update-preserves-rollback");
    Check(await managerV2.RollbackAsync("runtime-test"), "runtime-rollback-succeeds");
    var restoredManifest = JsonSerializer.Deserialize<RuntimeArtifactReceipt>(await File.ReadAllTextAsync(
        Path.Combine(phase1Paths.RuntimeRoot, "runtime-test", "stable", "runtime-artifact.json")));
    Check(restoredManifest?.Version == "1.0.0", "runtime-rollback-restores-prior-stable");
    Check(!Directory.Exists(Path.Combine(phase1Paths.RuntimeRoot, ".candidates")), "promotion-leaves-no-empty-candidates-folder");

    var wrongHash = artifactV2 with { Version = "3.0.0", ExpectedSha256 = new string('0', 64) };
    var mismatch = await new RuntimeArtifactManager(phase1Paths, downloader: new ScriptedRuntimeDownloader(archiveV2))
        .ProvisionAsync(wrongHash, verifier);
    Check(!mismatch.Passed && mismatch.FailureKind == RuntimeFailureKind.ChecksumMismatch, "runtime-checksum-mismatch-fails-closed");
    restoredManifest = JsonSerializer.Deserialize<RuntimeArtifactReceipt>(await File.ReadAllTextAsync(
        Path.Combine(phase1Paths.RuntimeRoot, "runtime-test", "stable", "runtime-artifact.json")));
    Check(restoredManifest?.Version == "1.0.0", "failed-runtime-update-leaves-stable-unchanged");

    var resumeArchive = RuntimeTestArchive.Create("resume-safe");
    var resumeArtifact = RuntimeTestArtifact.Create("resume-test", "1.0.0", source, resumeArchive);
    var resumeDownloader = new ScriptedRuntimeDownloader(resumeArchive, interruptFirst: true);
    var resumeManager = new RuntimeArtifactManager(phase1Paths, downloader: resumeDownloader);
    var interrupted = await resumeManager.ProvisionAsync(resumeArtifact, verifier);
    var resumed = await resumeManager.ProvisionAsync(resumeArtifact, verifier);
    Check(!interrupted.Passed && interrupted.FailureKind == RuntimeFailureKind.Interrupted && resumed.Passed && resumeDownloader.Resumed,
        "runtime-download-interruption-resumes-safely");

    // M3: a dropped connection keeps the received bytes and the next attempt resumes; integrity failures discard them.
    var dropArchive = RuntimeTestArchive.Create("network-drop");
    var dropArtifact = RuntimeTestArtifact.Create("drop-test", "1.0.0", source, dropArchive);
    var dropPartial = Path.Combine(phase1Paths.DownloadRoot, "drop-test", RuntimeArtifactManager.PartialFileName(dropArtifact));
    var dropDownloader = new ScriptedRuntimeDownloader(dropArchive, failFirstAfterHalf: new HttpIOException(HttpRequestError.ResponseEnded, "connection dropped"));
    var dropManager = new RuntimeArtifactManager(phase1Paths, downloader: dropDownloader);
    var dropped = await dropManager.ProvisionAsync(dropArtifact, verifier);
    var keptBytes = File.Exists(dropPartial) ? new FileInfo(dropPartial).Length : 0;
    var droppedThenResumed = await dropManager.ProvisionAsync(dropArtifact, verifier);
    Check(!dropped.Passed && dropped.FailureKind == RuntimeFailureKind.Http && keptBytes == dropArchive.Length / 2 &&
          droppedThenResumed.Passed && dropDownloader.Resumed && !File.Exists(dropPartial),
        "runtime-network-drop-keeps-partial-and-resumes:" + dropped.FailureKind + ":" + keptBytes);
    Check(RuntimeFailureClassifier.Classify(new HttpIOException(HttpRequestError.ResponseEnded)) == RuntimeFailureKind.Http &&
          RuntimeFailureClassifier.Classify(new IOException("reset", new System.Net.Sockets.SocketException(10054))) == RuntimeFailureKind.Http,
        "runtime-body-read-network-errors-are-classified-as-network");

    var corruptArchive = RuntimeTestArchive.Create("corrupt-midway");
    var corruptArtifact = RuntimeTestArtifact.Create("corrupt-test", "1.0.0", source, corruptArchive);
    var corruptPartial = Path.Combine(phase1Paths.DownloadRoot, "corrupt-test", RuntimeArtifactManager.PartialFileName(corruptArtifact));
    var corrupt = await new RuntimeArtifactManager(phase1Paths, downloader: new ScriptedRuntimeDownloader(corruptArchive, failFirstAfterHalf: new InvalidDataException("corrupt stream")))
        .ProvisionAsync(corruptArtifact, verifier);
    Check(!corrupt.Passed && corrupt.FailureKind == RuntimeFailureKind.InvalidArchive && !File.Exists(corruptPartial),
        "runtime-integrity-failure-discards-partial");

    var nextVersion = dropArtifact with { Version = "1.0.1", ExpectedSha256 = Convert.ToHexString(SHA256.HashData(archiveV2)).ToLowerInvariant(), ExpectedSizeBytes = archiveV2.LongLength };
    var olderPartial = Path.Combine(phase1Paths.DownloadRoot, "drop-test", RuntimeArtifactManager.PartialFileName(dropArtifact));
    await File.WriteAllBytesAsync(olderPartial, dropArchive[..(dropArchive.Length / 2)]);
    var nextVersionResult = await new RuntimeArtifactManager(phase1Paths, downloader: new ScriptedRuntimeDownloader(archiveV2)).ProvisionAsync(nextVersion, verifier);
    Check(RuntimeArtifactManager.PartialFileName(nextVersion) != RuntimeArtifactManager.PartialFileName(dropArtifact) &&
          nextVersionResult.Passed && !File.Exists(olderPartial),
        "runtime-partial-is-bound-to-pinned-version-and-hash");

    var diskFailure = await new RuntimeArtifactManager(
        phase1Paths,
        downloader: new ScriptedRuntimeDownloader(archiveV1),
        storage: new FixedRuntimeStorageProbe(0)).ProvisionAsync(
            RuntimeTestArtifact.Create("disk-test", "1.0.0", source, archiveV1), verifier);
    Check(!diskFailure.Passed && diskFailure.FailureKind == RuntimeFailureKind.InsufficientDisk, "runtime-disk-shortage-is-structured");
    Check(RuntimeFailureClassifier.Classify(new HttpRequestException("proxy denied", null, HttpStatusCode.ProxyAuthenticationRequired)) == RuntimeFailureKind.Proxy,
        "runtime-proxy-failure-is-structured");
    Check(RuntimeFailureClassifier.Classify(new HttpRequestException("network unavailable")) == RuntimeFailureKind.Http,
        "runtime-network-failure-is-structured");

    var linkArchive = RuntimeTestArchive.Create("unsafe", linkEntry: true);
    var linkFailure = await new RuntimeArtifactManager(phase1Paths, downloader: new ScriptedRuntimeDownloader(linkArchive))
        .ProvisionAsync(RuntimeTestArtifact.Create("link-test", "1.0.0", source, linkArchive), verifier);
    Check(!linkFailure.Passed && linkFailure.FailureKind == RuntimeFailureKind.InvalidArchive, "runtime-archive-links-are-rejected");

    var stateStore = new RuntimeArtifactStateStore(phase1Paths);
    var concurrentA = artifactV1 with { Product = "state-a" };
    var concurrentB = artifactV1 with { Product = "state-b" };
    await Task.WhenAll(
        stateStore.SaveAsync(new RuntimeArtifactReceipt(1, concurrentA.Product, concurrentA.Kind, concurrentA.Version, concurrentA.Architecture, concurrentA.OfficialSource.AbsoluteUri, concurrentA.ExpectedSha256, concurrentA.ExpectedSha256, concurrentA.License, "a", RuntimeArtifactStatus.Ready, DateTimeOffset.UtcNow, null, null)),
        stateStore.SaveAsync(new RuntimeArtifactReceipt(1, concurrentB.Product, concurrentB.Kind, concurrentB.Version, concurrentB.Architecture, concurrentB.OfficialSource.AbsoluteUri, concurrentB.ExpectedSha256, concurrentB.ExpectedSha256, concurrentB.License, "b", RuntimeArtifactStatus.Ready, DateTimeOffset.UtcNow, null, null)));
    var concurrentState = await stateStore.LoadAsync();
    Check(concurrentState.Any(item => item.Product == "state-a") && concurrentState.Any(item => item.Product == "state-b"),
        "runtime-state-concurrent-updates-are-not-lost");

    var bootstrapRuns = 0;
    var bootstrap = new BootstrapCoordinator(
        new BootstrapStateStore(phase1Paths),
        [new DelegateBootstrapStep(BootstrapStage.EnvironmentProbed, (_, _) =>
        {
            bootstrapRuns++;
            return Task.FromResult(new BootstrapStepResult(true, "verified"));
        })]);
    var bootstrapContext = new BootstrapContext(phase1Paths, standardHardware, standardResources);
    Check((await bootstrap.RunAsync(bootstrapContext)).CurrentStage == BootstrapStage.Ready, "bootstrap-first-run-reaches-ready");
    Check((await bootstrap.RunAsync(bootstrapContext)).CurrentStage == BootstrapStage.Ready && bootstrapRuns == 2,
        "bootstrap-ready-state-is-reverified-not-trusted");
    Check(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) == machinePathBefore &&
          Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) == userPathBefore,
        "runtime-provisioning-does-not-change-machine-or-user-path");

    // Toolchain failures are classified by evidence, not by the stage that happened to run the tool.
    const int accessViolationExit = unchecked((int)0xC0000005);
    Check(DevelopmentProcessRunner.Classify(new DevelopmentProcessResult(accessViolationExit, "", false), DevelopmentFailureKind.CompileFailure) == DevelopmentFailureKind.ToolchainFailure,
        "toolchain-crash-without-diagnostics-is-not-compile-failure");
    Check(DevelopmentProcessRunner.Classify(new DevelopmentProcessResult(1, "Program.cs(3,1): error CS1002: ; expected", false), DevelopmentFailureKind.CompileFailure) == DevelopmentFailureKind.CompileFailure,
        "compiler-diagnostics-remain-compile-failure");
    Check(DevelopmentProcessRunner.Classify(new DevelopmentProcessResult(1, "error NU1100: Unable to resolve 'Microsoft.NETCore.App.Runtime.win-x64 (= 10.0.0)' for 'net10.0'.", false), DevelopmentFailureKind.RestoreFailure) == DevelopmentFailureKind.PackageResolutionFailure,
        "nu1100-is-package-resolution-failure");
    Check(DevelopmentProcessRunner.Classify(new DevelopmentProcessResult(1, "error NETSDK1004: Assets file 'project.assets.json' not found.", false), DevelopmentFailureKind.CompileFailure) == DevelopmentFailureKind.RestoreFailure,
        "netsdk1004-is-restore-failure");
    Check(DevelopmentProcessRunner.Classify(new DevelopmentProcessResult(-1, "", true), DevelopmentFailureKind.CompileFailure) == DevelopmentFailureKind.ToolchainFailure,
        "toolchain-timeout-is-not-compile-failure");
    Check(DevelopmentProcessRunner.Failure(DevelopmentPipelineStage.Build, DevelopmentFailureKind.CompileFailure, "compile.failed", new DevelopmentProcessResult(accessViolationExit, "", false))
            .Summary.StartsWith("toolchain.process-crashed(0xC0000005)", StringComparison.Ordinal),
        "toolchain-crash-summary-names-exception-code");

    // Timeout and cancellation stop the whole process tree (cmd.exe -> loopback ping), not only the direct child.
    var systemCmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
    string[] longRunningCommand = ["/d", "/s", "/c", "ping -n 30 127.0.0.1 >nul"];
    var processTreeTestStarted = DateTime.Now;
    var timeoutClock = System.Diagnostics.Stopwatch.StartNew();
    var timedOutRun = await DevelopmentProcessRunner.RunAsync(systemCmd, phase1Root, longRunningCommand, null, CancellationToken.None, TimeSpan.FromSeconds(1));
    timeoutClock.Stop();
    var cancelledRun = false;
    using (var cancelSource = new CancellationTokenSource(TimeSpan.FromSeconds(1)))
    {
        try { await DevelopmentProcessRunner.RunAsync(systemCmd, phase1Root, longRunningCommand, null, cancelSource.Token); }
        catch (OperationCanceledException) { cancelledRun = true; }
    }
    await Task.Delay(500);
    var orphanedPing = System.Diagnostics.Process.GetProcessesByName("PING").Count(process =>
    {
        try { return process.StartTime >= processTreeTestStarted; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    });
    Check(timedOutRun.TimedOut && timeoutClock.Elapsed < TimeSpan.FromSeconds(20), "toolchain-timeout-is-enforced:" + timeoutClock.Elapsed.TotalSeconds.ToString("F1"));
    Check(cancelledRun, "toolchain-cancellation-is-propagated");
    Check(orphanedPing == 0, "toolchain-timeout-and-cancel-leave-no-descendants:" + orphanedPing);

    // Runtime smoke checks pass explicit environment overrides to the child process.
    var environmentProbe = RuntimeTestArtifact.Create("environment-probe", "1.0.0", source, archiveV1) with { ExecutableRelativePath = "cmd.exe" };
    string[] environmentProbeCommand = ["/d", "/s", "/c", "if %OLLAMA_HOST%==127.0.0.1:1 (echo isolated) else (exit /b 7)"];
    var overridePassed = "not-run";
    try
    {
        await new ExecutableRuntimeSmokeVerifier(environmentProbeCommand, environment: new Dictionary<string, string?> { ["OLLAMA_HOST"] = "127.0.0.1:1" })
            .VerifyAsync(environmentProbe, Environment.SystemDirectory);
        overridePassed = "passed";
    }
    catch (Exception exception) { overridePassed = exception.GetType().Name + ":" + exception.Message; }
    var missingOverride = "accepted";
    try { await new ExecutableRuntimeSmokeVerifier(environmentProbeCommand).VerifyAsync(environmentProbe, Environment.SystemDirectory); }
    catch (RuntimeProvisioningException exception) when (exception.Kind == RuntimeFailureKind.SmokeFailure) { missingOverride = "rejected"; }
    catch (Exception exception) { missingOverride = exception.GetType().Name + ":" + exception.Message; }
    Check(overridePassed == "passed" && missingOverride == "rejected",
        "runtime-smoke-passes-environment-overrides-to-child:" + overridePassed + ":" + missingOverride);

    // A version check that exits immediately must never fail on sampling an exited process (clean-user run, 2026-10-06).
    var fastExitFailures = new List<string>();
    for (var run = 0; run < 25; run++)
    {
        try { await new ExecutableRuntimeSmokeVerifier(["/d", "/c", "echo", "ok"]).VerifyAsync(environmentProbe, Environment.SystemDirectory); }
        catch (Exception exception) { fastExitFailures.Add(exception.GetType().Name + ":" + exception.Message); }
    }
    Check(fastExitFailures.Count == 0, "runtime-smoke-tolerates-immediately-exiting-process:" + string.Join("|", fastExitFailures.Distinct()));

    // `ollama --version` must never contact the user's own Ollama on the default port.
    var isolatedOllamaHost = OllamaManager.CreateIsolatedVersionVerifier().EnvironmentOverrides["OLLAMA_HOST"];
    Check(isolatedOllamaHost is not null &&
          System.Text.RegularExpressions.Regex.IsMatch(isolatedOllamaHost, @"^127\.0\.0\.1:\d+$") &&
          !isolatedOllamaHost.EndsWith(":11434", StringComparison.Ordinal),
        "ollama-version-check-never-targets-user-default-port");
    var ollamaSmokeCallSites = File.ReadAllText(Path.Combine(coreRoot, "OllamaManager.cs")) + File.ReadAllText(Path.Combine(coreRoot, "ZeroAdminBootstrapService.cs"));
    Check(!ollamaSmokeCallSites.Contains("new ExecutableRuntimeSmokeVerifier([\"--version\"]", StringComparison.Ordinal) &&
          System.Text.RegularExpressions.Regex.Matches(ollamaSmokeCallSites, @"CreateIsolatedVersionVerifier\(").Count >= 2,
        "ollama-version-checks-use-isolated-verifier");

    var draftStore = new StudioDraftStore(smokePaths);
    var savedDraft = new StudioDraft("話し言葉の入力を保存", StudioProjectKind.Agent, DateTimeOffset.Now);
    await draftStore.SaveAsync(savedDraft);
    var loadedDraft = draftStore.Load();
    Check(loadedDraft.Request == savedDraft.Request && loadedDraft.Kind == StudioProjectKind.Agent, "studio-draft-roundtrip");

    var externalExecutable = Path.Combine(smokeRoot, "official-user-install", "ollama.exe");
    Directory.CreateDirectory(Path.GetDirectoryName(externalExecutable)!);
    await File.WriteAllTextAsync(externalExecutable, "test");
    await using (var externalRuntime = new OllamaManager(smokePaths, officialUserExecutable: externalExecutable))
    {
        Check(externalRuntime.FindInstalledExecutable() is null, "external-user-ollama-never-treated-as-managed");
        Check(externalRuntime.DetectExternalUserInstallation() == Path.GetFullPath(externalExecutable), "external-user-ollama-detected-read-only");
    }

    Directory.CreateDirectory(smokePaths.RuntimeRoot);
    var managedExecutable = Path.Combine(smokePaths.RuntimeRoot, "ollama", "stable", "content", "ollama.exe");
    Directory.CreateDirectory(Path.GetDirectoryName(managedExecutable)!);
    await File.WriteAllTextAsync(managedExecutable, "managed");
    await using (var managedRuntime = new OllamaManager(smokePaths, officialUserExecutable: externalExecutable))
        Check(managedRuntime.FindInstalledExecutable() == managedExecutable, "only-promoted-managed-ollama-is-executable");

    File.Delete(managedExecutable);
    var candidateExecutable = Path.Combine(smokePaths.RuntimeRoot, ".candidates", "ollama-test", "content", "ollama.exe");
    Directory.CreateDirectory(Path.GetDirectoryName(candidateExecutable)!);
    await File.WriteAllTextAsync(candidateExecutable, "candidate");
    var blockedNetwork = new SequenceJsonHandler();
    using (var blockedHttp = new HttpClient(blockedNetwork))
    await using (var unconfiguredRuntime = new OllamaManager(smokePaths, blockedHttp, externalExecutable))
    {
        Check(unconfiguredRuntime.FindInstalledExecutable() is null, "candidate-runtime-is-never-treated-as-stable");
        var emptyCatalog = new OfficialRuntimeCatalog(new Dictionary<string, RuntimeArtifact>(StringComparer.OrdinalIgnoreCase));
        var unconfiguredPlan = LocalAiSetupService.BuildPlan(smokePaths, emptyCatalog, standardHardware, standardResources, ManagedModelManager.DefaultCandidates, null);
        Check(!unconfiguredPlan.CanDownload && unconfiguredPlan.Items.Count == 0 && blockedNetwork.RequestCount == 0, "unconfigured-runtime-catalog-fails-before-network");
        var bootstrapWithoutCatalog = new ZeroAdminBootstrapService(
            smokePaths,
            new RuntimeArtifactManager(smokePaths, downloader: new ScriptedRuntimeDownloader(archiveV1)),
            emptyCatalog,
            unconfiguredRuntime,
            new ManagedModelManager(smokePaths, unconfiguredRuntime, new OllamaModelBenchmarkRunner(unconfiguredRuntime, blockedHttp)));
        var forgedApproval = new BootstrapDownloadApproval(
            "ollama", "1.0.0", "https://github.com/ollama/ollama/releases/download/v1.0.0/ollama-windows-amd64.zip", new string('a', 64),
            ZeroAdminBootstrapService.ManagedRuntimeDestination(smokePaths), LocalStudioSettings.DefaultModel, long.MaxValue, DateTimeOffset.UtcNow);
        var bootstrapFailedClosed = false;
        try { await bootstrapWithoutCatalog.RunUserInitiatedAsync(standardHardware, standardResources, forgedApproval); }
        catch (RuntimeProvisioningException exception) when (exception.Kind == RuntimeFailureKind.InvalidSource) { bootstrapFailedClosed = true; }
        Check(bootstrapFailedClosed && blockedNetwork.RequestCount == 0, "approval-without-pinned-catalog-fails-before-network");
        var modelFailedClosed = false;
        try { await unconfiguredRuntime.PullModelAsync("qwen2.5-coder:14b-instruct-q4_K_M"); }
        catch (RuntimeProvisioningException exception) when (exception.Kind == RuntimeFailureKind.InvalidSource) { modelFailedClosed = true; }
        Check(modelFailedClosed && blockedNetwork.RequestCount == 0, "unpinned-model-fails-before-network");
    }

    // The shipped table holds exactly the values the user reviewed on 2026-10-06, and nothing else.
    var shippedRuntime = new OfficialRuntimeCatalog().GetOllamaStatus().Artifact;
    Check(shippedRuntime is not null &&
          shippedRuntime.Version == "0.35.1" &&
          shippedRuntime.OfficialSource.AbsoluteUri == "https://github.com/ollama/ollama/releases/download/v0.35.1/ollama-windows-amd64.zip" &&
          shippedRuntime.ExpectedSha256 == "dc50b9ca7f9023c86525012632cd1615b093d0407987444a7f62ecab617e8e93" &&
          shippedRuntime.ExpectedSizeBytes == 1_471_094_402 && shippedRuntime.License == "MIT",
        "shipped-runtime-pin-matches-reviewed-values");
    var shippedModel = ManagedModelManager.GetPinnedCandidate(LocalStudioSettings.DefaultModel);
    Check(shippedModel is { ExpectedManifestSha256: "748645786a08b8f96c5e5723b85e5858c2a8b28334c8c54b19325705847af993", ApproximateDownloadBytes: 3_492_382_409, License: "Apache-2.0" } &&
          ManagedModelManager.GetPinnedCandidate("qwen2.5-coder:14b-instruct-q4_K_M") is null,
        "shipped-model-pin-matches-reviewed-values-and-unreviewed-model-stays-unpinned");
    // Smaller candidates for this PC (values reviewed and approved 2026-10-09).
    var smallPins = new (string Model, string Sha, long Bytes)[]
    {
        ("qwen3.5:2b-q4_K_M", "1e2d21a4f03ab9e4a9c1976a4b7e88f21de61d65f2c5485d8fde8942be73f1e7", 1_945_781_358),
        ("qwen3.5:4b-q4_K_M", "d8b0f5e9760cd1682034f292d7ef72ec46f432149be0df7574bf2d6e92e38c04", 3_324_173_934),
        ("qwen2.5-coder:1.5b-instruct-q4_K_M", "d7372fd828518a4d38b1eb196c673c31a85f2ed302b3d1e406c4c2d1b64a0668", 986_062_089)
    };
    Check(smallPins.All(pin => SettingsStore.IsAllowedModel(pin.Model) &&
                               ManagedModelManager.GetPinnedCandidate(pin.Model) is { } candidate &&
                               candidate.ExpectedManifestSha256 == pin.Sha && candidate.ApproximateDownloadBytes == pin.Bytes && candidate.License == "Apache-2.0" &&
                               ModelMemoryEstimator.RequiredBytes(pin.Model, 8192, "q8_0") < ModelMemoryEstimator.RequiredBytes(LocalStudioSettings.DefaultModel, 8192, "q8_0")),
        "small-model-pins-match-reviewed-values-and-are-lighter");
    Check(ModelSelectionPolicy.ResolveForPlanning("qwen3.5:4b-q4_K_M", ["qwen3.5:2b-q4_K_M", LocalStudioSettings.DefaultModel]) == "qwen3.5:2b-q4_K_M" &&
          ModelSelectionPolicy.ResolveForPlanning(LocalStudioSettings.DefaultModel, ["qwen2.5-coder:1.5b-instruct-q4_K_M", "qwen3.5:2b-q4_K_M"]) == "qwen2.5-coder:1.5b-instruct-q4_K_M" &&
          ModelSelectionPolicy.ResolveForPlanning("qwen3.5:2b-q4_K_M", [LocalStudioSettings.DefaultModel]) is null,
        "model-stand-ins-stay-within-family-and-never-grow");
    Check(AdaptiveRuntimePolicy.Create(standardHardware, "qwen3.5:2b-q4_K_M").RecommendedModel == "qwen3.5:2b-q4_K_M" &&
          AdaptiveRuntimePolicy.Create(standardHardware, "qwen2.5-coder:1.5b-instruct-q4_K_M").RecommendedModel == "qwen2.5-coder:1.5b-instruct-q4_K_M",
        "a-chosen-lighter-model-is-honoured");
    var thinkingRequest = ModelRequestPolicy.Prepare(new { model = "qwen3.5:2b-q4_K_M", stream = false, keep_alive = "30s", options = new { num_ctx = 4096 } }).AsObject();
    var coderRequest = ModelRequestPolicy.Prepare(new { model = LocalStudioSettings.DefaultModel, stream = false }).AsObject();
    var modelSendSites = Directory.GetFiles(coreRoot, "*.cs").Select(File.ReadAllText)
        .Where(source => source.Contains("\"api/chat\"", StringComparison.Ordinal) || source.Contains("Return exactly the word READY", StringComparison.Ordinal))
        .ToArray();
    Check(thinkingRequest["think"]?.GetValue<bool>() == false && thinkingRequest["keep_alive"]?.GetValue<string>() == "30s" &&
          thinkingRequest["options"]?["num_ctx"]?.GetValue<int>() == 4096 && thinkingRequest["options"]?["presence_penalty"]?.GetValue<int>() == 0 &&
          !coderRequest.ContainsKey("think") &&
          modelSendSites.Length >= 6 && modelSendSites.All(source => source.Contains("ModelRequestPolicy.Prepare(", StringComparison.Ordinal)),
        "thinking-is-off-for-qwen35-and-every-model-request-goes-through-the-policy");
    try { new OfficialRuntimeSourcePolicy().EnsureAllowed(shippedRuntime!, shippedRuntime!.OfficialSource); Check(true, "shipped-runtime-source-is-allowlisted"); }
    catch (RuntimeProvisioningException) { Check(false, "shipped-runtime-source-is-allowlisted"); }
    var shippedPlan = LocalAiSetupService.BuildPlan(phase1Paths, new OfficialRuntimeCatalog(), standardHardware, standardResources, ManagedModelManager.DefaultCandidates, null);
    Check(shippedPlan.CanDownload && shippedPlan.TotalBytes == 1_471_094_402L + 3_492_382_409L && shippedPlan.Model == LocalStudioSettings.DefaultModel,
        "shipped-plan-offers-only-reviewed-download-on-a-standard-pc:" + shippedPlan.Summary);

    // Setup plan: shown before any download, binds the approval, and never offers unsuitable choices.
    var pinnedRuntime = RuntimeTestArtifact.Create("ollama", "9.9.9", new Uri("https://github.com/ollama/ollama/releases/download/v9.9.9/ollama-windows-amd64.zip"), archiveV1);
    var pinnedCatalog = new OfficialRuntimeCatalog(new Dictionary<string, RuntimeArtifact>(StringComparer.OrdinalIgnoreCase) { ["ollama"] = pinnedRuntime });
    var pinnedModels = ManagedModelManager.DefaultCandidates.Select(candidate => candidate with { ExpectedManifestSha256 = new string('b', 64) }).ToArray();
    var offeredPlan = LocalAiSetupService.BuildPlan(phase1Paths, pinnedCatalog, standardHardware, standardResources, pinnedModels, externalExecutable);
    var offeredRuntime = offeredPlan.Items.SingleOrDefault(item => item.Kind == SetupDownloadPlan.RuntimeKind);
    var offeredModel = offeredPlan.Items.SingleOrDefault(item => item.Kind == SetupDownloadPlan.ModelKind);
    Check(offeredPlan.CanDownload && offeredRuntime is not null && offeredModel is not null &&
          offeredRuntime.Source == pinnedRuntime.OfficialSource.AbsoluteUri &&
          offeredRuntime.SizeBytes == archiveV1.LongLength &&
          offeredRuntime.Sha256 == pinnedRuntime.ExpectedSha256 &&
          offeredRuntime.Destination == ZeroAdminBootstrapService.ManagedRuntimeDestination(phase1Paths) &&
          offeredModel.Destination == phase1Paths.ModelRoot && offeredModel.SizeIsApproximate &&
          offeredPlan.TotalBytes == archiveV1.LongLength + offeredModel.SizeBytes,
        "setup-plan-shows-source-size-destination-and-sha256-before-download");
    Check(offeredPlan.ExternalOllamaPath == externalExecutable, "setup-plan-reports-external-ollama-display-only");
    var shownApproval = offeredPlan.CreateApproval(DateTimeOffset.UtcNow);
    Check(offeredRuntime is not null &&
          shownApproval.RuntimeSource == offeredRuntime.Source && shownApproval.RuntimeSha256 == offeredRuntime.Sha256 &&
          shownApproval.RuntimeDestination == offeredRuntime.Destination && shownApproval.Model == offeredPlan.Model &&
          shownApproval.MaximumDownloadBytes == offeredPlan.TotalBytes,
        "setup-approval-binds-exactly-the-shown-plan");
    var aboveBandResources = standardResources with { RecommendedModel = "qwen2.5-coder:14b-instruct-q4_K_M" };
    var aboveBandPlan = LocalAiSetupService.BuildPlan(phase1Paths, pinnedCatalog, standardHardware, aboveBandResources, pinnedModels, null);
    Check(!aboveBandPlan.CanDownload && aboveBandPlan.Items.Count == 0, "setup-plan-does-not-offer-models-above-resource-band");
    var lowMemoryHardware = standardHardware with { AvailableMemoryBytes = 5 * GiB };
    var lowMemoryPlan = LocalAiSetupService.BuildPlan(phase1Paths, pinnedCatalog, lowMemoryHardware, standardResources, pinnedModels, null);
    Check(!lowMemoryPlan.CanDownload && lowMemoryPlan.Items.Count == 0, "setup-plan-checks-ram-before-offering-download");
    var exhaustedHardware = standardHardware with { AvailableMemoryBytes = 2 * GiB };
    var exhaustedPlan = LocalAiSetupService.BuildPlan(phase1Paths, pinnedCatalog, exhaustedHardware, AdaptiveRuntimePolicy.Create(exhaustedHardware, LocalStudioSettings.DefaultModel), pinnedModels, null);
    Check(!exhaustedPlan.CanDownload && exhaustedPlan.Items.Count == 0 && exhaustedPlan.Band == RuntimeResourceBand.DeterministicOnly, "setup-plan-offers-nothing-when-ai-is-disabled");
    var smallDiskHardware = standardHardware with { FreeDiskBytes = archiveV1.LongLength };
    Check(!LocalAiSetupService.BuildPlan(phase1Paths, pinnedCatalog, smallDiskHardware, standardResources, pinnedModels, null).CanDownload, "setup-plan-checks-disk-before-offering-download");
    var unsizedCatalog = new OfficialRuntimeCatalog(new Dictionary<string, RuntimeArtifact>(StringComparer.OrdinalIgnoreCase) { ["ollama"] = pinnedRuntime with { ExpectedSizeBytes = null } });
    Check(!LocalAiSetupService.BuildPlan(phase1Paths, unsizedCatalog, standardHardware, standardResources, pinnedModels, null).CanDownload, "setup-plan-requires-known-download-size");
    var blockedPlanApprovable = true;
    try { lowMemoryPlan.CreateApproval(DateTimeOffset.UtcNow); }
    catch (InvalidOperationException) { blockedPlanApprovable = false; }
    Check(!blockedPlanApprovable, "blocked-setup-plan-cannot-be-approved");

    // M5: a single download path. Runtime installation exists only behind the approved plan.
    Check(typeof(ILocalModelRuntime).GetMethod("InstallAsync") is null && typeof(OllamaManager).GetMethod("InstallAsync") is null,
        "single-download-path-runtime-install-method-removed");
    var setupViewModelSource = File.ReadAllText(Path.Combine(Path.GetDirectoryName(manifest)!, "ViewModels", "SetupViewModel.cs"));
    var workbenchSource = File.ReadAllText(Path.Combine(coreRoot, "..", "SafeLocalHarness.Workbench", "Program.cs"));
    // The memory mode can be switched in the chat at any time; a stop by the memory guard offers Expert there.
    var chatModeXaml = File.ReadAllText(Path.Combine(Path.GetDirectoryName(manifest)!, "Views", "ChatView.xaml"));
    var chatViewModelSource = File.ReadAllText(Path.Combine(Path.GetDirectoryName(manifest)!, "ViewModels", "ChatViewModel.cs"));
    var modeEvents = new List<ResourceMode>();
    var modeStateForChat = new ResourceModeState(new FakeSettingsStore(new LocalStudioSettings(LocalStudioSettings.DefaultModel, ResourceMode.Focus)));
    modeStateForChat.Changed += modeEvents.Add;
    await modeStateForChat.SetAsync(ResourceMode.Expert);
    Check(MemoryGuardMessages.IsRefusal("設計案を作れませんでした: ほかのアプリの未使用メモリを空けても余裕がないため、AIの処理を開始しません。メモリの確保量が上限の86%です。") &&
          MemoryGuardMessages.IsRefusal("今の空きメモリではAIを動かせません。") && !MemoryGuardMessages.IsRefusal("完成しました。") &&
          modeStateForChat.Current == ResourceMode.Expert && modeEvents.SequenceEqual([ResourceMode.Expert]) &&
          chatModeXaml.Contains("CommandParameter=\"Expert\"", StringComparison.Ordinal) && chatModeXaml.Contains("{Binding ModeLabel}", StringComparison.Ordinal) &&
          chatViewModelSource.Contains("ExpertModeWarning", StringComparison.Ordinal) && chatViewModelSource.Contains("OfferExpertIfStopped", StringComparison.Ordinal),
        "chat-offers-memory-mode-switch-and-expert-after-a-guard-stop");
    Check(!setupViewModelSource.Contains("PullModelAsync", StringComparison.Ordinal) &&
          !workbenchSource.Contains("PullModelAsync", StringComparison.Ordinal) &&
          setupViewModelSource.Contains("CreateApproval", StringComparison.Ordinal),
        "single-download-path-setup-and-workbench-never-pull-directly");
    Check(workbenchSource.Contains("new DevelopmentSessionService", StringComparison.Ordinal) &&
          workbenchSource.Contains("assembled-awaiting-evidence", StringComparison.Ordinal) &&
          !workbenchSource.Contains("new DevelopmentProjectAssembler(store)", StringComparison.Ordinal) &&
          !workbenchSource.Contains("await executor.RunAsync(", StringComparison.Ordinal),
        "workbench-uses-the-durable-session-service-and-does-not-call-assembly-directly");

    // An explicit setup run re-checks every stage, so a resumed run never lacks evidence from earlier stages.
    var restartPaths = phase1Paths with { StateRoot = Path.Combine(phase1Root, "restart-state") };
    var firstStageRuns = 0;
    var secondStageCalls = 0;
    IBootstrapStep[] restartSteps =
    [
        new DelegateBootstrapStep(BootstrapStage.EnvironmentProbed, (_, _) =>
        {
            firstStageRuns++;
            return Task.FromResult(new BootstrapStepResult(true, "probed"));
        }),
        new DelegateBootstrapStep(BootstrapStage.InferenceRuntimeResolved, (_, _) => Task.FromResult(++secondStageCalls == 1
            ? new BootstrapStepResult(false, "interrupted", RuntimeFailureKind.Interrupted, true)
            : new BootstrapStepResult(true, "resolved")))
    ];
    var restartCoordinator = new BootstrapCoordinator(new BootstrapStateStore(restartPaths), restartSteps, resumeCompletedStages: false);
    var interruptedState = await restartCoordinator.RunAsync(new BootstrapContext(restartPaths, standardHardware, standardResources));
    var restartedState = await restartCoordinator.RunAsync(new BootstrapContext(restartPaths, standardHardware, standardResources));
    Check(interruptedState.CurrentStage == BootstrapStage.Degraded && restartedState.CurrentStage == BootstrapStage.Ready &&
          firstStageRuns == 2 && restartedState.RetryCount == 1,
        "explicit-setup-rechecks-all-stages-after-interruption");

    // Startup/shutdown must never block the UI thread on a task that resumes on it (the 2026-10-06 startup hang).
    var presentationRoot = Path.GetDirectoryName(manifest)!;
    var blockingWaitSources = new[] { Path.Combine(presentationRoot, "App.xaml.cs") }
        .Concat(Directory.EnumerateFiles(Path.Combine(presentationRoot, "ViewModels"), "*.cs"))
        .Where(path => File.ReadAllText(path).Contains(".GetAwaiter().GetResult()", StringComparison.Ordinal))
        .Select(Path.GetFileName)
        .ToArray();
    Check(blockingWaitSources.Length == 0, "ui-thread-never-blocks-on-async-work:" + string.Join("|", blockingWaitSources));
    Check(appStartup.Contains("RunOffUiThread(", StringComparison.Ordinal) && File.ReadAllText(Path.Combine(presentationRoot, "App.xaml.cs")).Contains("Task.Run(work).Wait(UiBlockingLimit)", StringComparison.Ordinal),
        "ui-startup-and-shutdown-waits-are-off-thread-and-bounded");
    var hardwareSaveCompleted = BlockedUiContext.Completes(() => SystemHardwareProbe.SaveAsync(phase1Paths, standardHardware, standardResources), TimeSpan.FromSeconds(10));
    var draftSaveCompleted = BlockedUiContext.Completes(() => new StudioDraftStore(phase1Paths).SaveAsync(new StudioDraft("終了時の下書き", StudioProjectKind.Agent, DateTimeOffset.Now)), TimeSpan.FromSeconds(10));
    Check(hardwareSaveCompleted && draftSaveCompleted && File.Exists(Path.Combine(phase1Paths.StateRoot, "hardware-profile.json")),
        "startup-and-shutdown-saves-complete-without-ui-context:" + hardwareSaveCompleted + ":" + draftSaveCompleted);

    // M1 (environment): real user-level signals, the model-start gate, release before builds, and the explicit mode setting.
    using (var realProbe = new WindowsMemoryPressureProbe())
    {
        var realSample = realProbe.Sample();
        Check(realSample.AvailableBytes > 0 && realSample.TotalVisibleBytes >= realSample.AvailableBytes && realSample.CommitPercent is > 0 and <= 100,
            "windows-pressure-probe-reads-user-level-signals:" + realSample.CommitPercent);
    }
    var capturedHardware = SystemHardwareProbe.Capture(phase1Paths);
    Check(capturedHardware.PhysicallyInstalledMemoryBytes is null || capturedHardware.PhysicallyInstalledMemoryBytes >= capturedHardware.InstalledMemoryBytes,
        "physically-installed-memory-is-not-below-os-visible-memory");

    var criticalSample = new MemoryPressureSample(DateTimeOffset.UtcNow, 1 * GiB, 16 * GiB, 95, 0, 0, true);
    using var criticalMonitor = new MemoryPressureMonitor(new ScriptedPressureProbe(criticalSample), () => ResourceMode.Coexistence, 16 * GiB);
    var criticalEvents = 0;
    criticalMonitor.EnteredCritical += _ => criticalEvents++;
    var refusedUnderPressure = false;
    await using (var pressuredRuntime = new OllamaManager(smokePaths, officialUserExecutable: externalExecutable, pressureMonitor: criticalMonitor))
    {
        try { await pressuredRuntime.EnsureServerAsync(); }
        catch (ResourcePressureException) { refusedUnderPressure = true; }
    }
    criticalMonitor.SampleAndAssess();
    Check(refusedUnderPressure && criticalEvents == 1, "memory-pressure-blocks-model-start-and-signals-release-once:" + refusedUnderPressure + ":" + criticalEvents);

    var orderEvents = new List<string>();
    var orderEngine = new DevelopmentUnitExecutionEngine(
        new SmokeDevelopmentUnitGenerator(),
        new OrderRecordingVerifier(orderEvents),
        new DevelopmentUnitCandidateStore(Path.Combine(phase1Root, "order-artifacts")),
        _ => { orderEvents.Add("release"); return Task.CompletedTask; });
    var orderResult = await orderEngine.RunAsync(executionPlan, DevelopmentRunCoordinator.Create(executionPlan), Path.Combine(phase1Root, "order-state.json"), "Build a bounded local application");
    Check(orderResult.Completed && orderEvents.Count(item => item == "verify") == executionPlan.Units.Count &&
          Enumerable.Range(0, orderEvents.Count).All(index => orderEvents[index] != "verify" || (index > 0 && orderEvents[index - 1] == "release")),
        "model-memory-is-released-before-each-build-verification:" + string.Join(">", orderEvents));
    var windowsEngineSource = File.ReadAllText(Path.Combine(coreRoot, "LocalWindowsAppGenerationEngine.cs"));
    var unloadIndex = windowsEngineSource.IndexOf("_runtime.UnloadModelsAsync(cancellationToken)", StringComparison.Ordinal);
    var compileIndex = windowsEngineSource.IndexOf("_compiler.CompileAsync(root, cancellationToken)", StringComparison.Ordinal);
    Check(unloadIndex >= 0 && compileIndex > unloadIndex, "windows-generation-releases-model-before-compiling");

    // Owned memory is totalled over every process in the owned job (the model runner is a child of the server).
    // The second ping starts after the job is attached, so it is reliably a job member.
    var jobProbeStart = new System.Diagnostics.ProcessStartInfo(systemCmd)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        ArgumentList = { "/d", "/s", "/c", "ping -n 2 127.0.0.1 >nul & ping -n 30 127.0.0.1 >nul" }
    };
    using var jobProbeProcess = System.Diagnostics.Process.Start(jobProbeStart)!;
    var jobProbe = WindowsProcessJob.Attach(jobProbeProcess);
    IReadOnlyList<int> jobMembers = [];
    for (var attempt = 0; attempt < 40 && jobMembers.Count < 2; attempt++)
    {
        await Task.Delay(100);
        var listed = jobProbe?.ProcessIds();
        if (listed is not null) jobMembers = listed;
    }
    var jobMetrics = OwnedProcessMetrics.Measure(jobMembers);
    jobProbe?.Dispose();
    var jobEnded = jobProbeProcess.WaitForExit(5000);
    Check(jobMembers.Contains(jobProbeProcess.Id) && jobMembers.Count >= 2 && jobMetrics.WorkingSetBytes > 0 && jobEnded,
        "owned-job-lists-child-processes-for-memory-totals:" + jobMembers.Count + ":" + jobEnded);

    var modeSettings = new SettingsStore(phase1Paths);
    await modeSettings.SaveAsync(LocalStudioSettings.Default with { ResourceMode = ResourceMode.Focus });
    var savedFocus = modeSettings.Load().ResourceMode;
    await File.WriteAllTextAsync(Path.Combine(phase1Paths.StateRoot, "settings.json"), "{\"SelectedModel\":\"qwen2.5-coder:7b-instruct-q3_K_S\"}");
    Check(savedFocus == ResourceMode.Focus && modeSettings.Load().ResourceMode == ResourceMode.Coexistence && LocalStudioSettings.Default.ResourceMode == ResourceMode.Coexistence,
        "focus-mode-persists-only-when-chosen-and-defaults-to-coexistence");

    await modeSettings.SaveAsync(LocalStudioSettings.Default with { ResourceMode = ResourceMode.Focus });
    var sessionModes = new ResourceModeState(modeSettings);
    await sessionModes.SetAsync(ResourceMode.Expert);
    var expertDuringSession = sessionModes.Current;
    var modeAtNextLaunch = new ResourceModeState(modeSettings).Current;
    var expertSaveRejected = false;
    try { await modeSettings.SaveAsync(LocalStudioSettings.Default with { ResourceMode = ResourceMode.Expert }); }
    catch (InvalidDataException) { expertSaveRejected = true; }
    await File.WriteAllTextAsync(Path.Combine(phase1Paths.StateRoot, "settings.json"), "{\"SelectedModel\":\"qwen2.5-coder:7b-instruct-q3_K_S\",\"ResourceMode\":2}");
    var tamperedExpert = modeSettings.Load().ResourceMode;
    Check(expertDuringSession == ResourceMode.Expert && modeAtNextLaunch == ResourceMode.Coexistence && expertSaveRejected && tamperedExpert == ResourceMode.Coexistence,
        "expert-mode-is-session-only-and-never-persisted");

    // Fixed templates are gone (user decision 2026-10-13): the sample project is an app as the generation route makes it,
    // compiled from an App Spec so no model is needed.
    var appBlueprint = BlueprintValidator.Validate(new StudioBlueprint("Daily Focus", "今日やることを安全に整理するアプリ", "app", ["タスクを1行ずつ追加", "タスクの一覧表示", "ブラウザ内での保存"], [new("生成", "動くアプリを作成"), new("検証", "要件を確認")], [], [], ["workspace.read", "workspace.write"]));
    GeneratedProjectSource SpecSource(StudioBlueprint specBlueprint, AppSpec spec)
    {
        var specRequirements = LocalWebAppGenerationEngine.RequirementsFor(specBlueprint);
        var compiledSpec = AppSpecCompiler.Compile(spec, specRequirements);
        return new(LocalWebAppGenerationEngine.PackageFiles(specBlueprint, compiledSpec.Candidate, specRequirements, compiledSpec.Evidence), "generated-local-web-app");
    }
    var taskSource = SpecSource(appBlueprint, new AppSpec(
        [new("draft", "text"), new("tasks", "list", Persist: true)],
        [new("task-input", "text-input", Label: "タスク", Bind: "draft"), new("add", "button", Label: "追加", Action: "addTask"),
         new("task-list", "list", Source: "tasks", ItemAction: new("完了", "completeTask")), new("total", "text", Template: "{tasks.count}件")],
        [new("addTask", [new("append", "tasks", "$draft"), new("set", "draft", "")]), new("completeTask", [new("removeAt", "tasks")])],
        [new("LS-REQ-001", "add", "addTask"), new("LS-REQ-002", "task-list", "addTask"), new("LS-REQ-003", null, null)]));
    var appProject = await new ProjectScaffolder(smokePaths, generativeGenerator: new FixedGenerativeProjectGenerator(taskSource)).CreateAsync(appBlueprint, StudioProjectKind.DesktopOrWebApp);
    Check(File.Exists(Path.Combine(appProject, ".localstudio", "requirement-ledger.json")), "project-requirement-ledger-created");
    Check(File.Exists(Path.Combine(appProject, ".localstudio", "project-cartridge.json")), "project-cartridge-created");
    var appLedger = await RequirementLedgerStore.LoadAsync(appProject);
    // A generated app is complete only after the user confirms each feature (no evidence is recorded before that).
    Check(appLedger.Items.Count == appBlueprint.Features.Count && !RequirementLedgerPolicy.Validate(appLedger).CompletionAllowed,
        "generated-requirements-have-evidence-and-wait-for-confirmation:" + appLedger.Items.Count + "/" + appLedger.Items.Count(item => item.Evidence.Count > 0) + "/" + RequirementLedgerPolicy.Validate(appLedger).CompletionAllowed);
    var appCartridgeInspection = await ProjectCartridgeStore.InspectAsync(appProject);
    Check(appCartridgeInspection.CanResume && appCartridgeInspection.Cartridge.Scope == "target-project", "project-cartridge-validates-for-resume");
    Check(appCartridgeInspection.Cartridge.TaskDag.Units.Count == 3 && appCartridgeInspection.Cartridge.SourceFiles.Count >= 4, "project-cartridge-captures-task-dag-and-source-integrity");
    var completeGrowthBlueprint = appBlueprint with
    {
        Name = appBlueprint.Name + " Growth",
        Features = appBlueprint.Features.Concat(["気づきを入力して保存", "過去の日報を一覧表示"]).ToArray()
    };
    var (growthCore, growthLater) = FeatureGrowth.Split(completeGrowthBlueprint);
    var growthSource = new GeneratedProjectSource(
        taskSource.Files.Select(file => new GeneratedTextFile(file.RelativePath, file.Content.Replace(appBlueprint.Name, completeGrowthBlueprint.Name, StringComparison.Ordinal))).ToArray(),
        taskSource.ImplementationMode);
    var growthRecordProject = await new ProjectScaffolder(smokePaths, generativeGenerator: new FixedGenerativeProjectGenerator(growthSource))
        .CreateAsync(growthCore, StudioProjectKind.DesktopOrWebApp);
    await FeatureGrowth.RecordResultsAsync(growthRecordProject, completeGrowthBlueprint,
    [
        new(growthLater[0], true, "deterministic checks passed", 2),
        new(growthLater[1], false, "bounded repair exhausted", 8)
    ]);
    var growthLedger = await RequirementLedgerStore.LoadAsync(growthRecordProject);
    var growthInspection = await ProjectCartridgeStore.InspectAsync(growthRecordProject);
    var growthRequirements = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(growthRecordProject, "requirements.json")));
    var growthManifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(growthRecordProject, "localstudio.json")));
    Check(growthLedger.Items.Count == completeGrowthBlueprint.Features.Count &&
          growthLedger.Items.Skip(FeatureGrowth.CoreFeatures).All(item => item.State == RequirementState.NotTested && item.Evidence.Count == 0) &&
          !RequirementLedgerPolicy.Validate(growthLedger).CompletionAllowed &&
          growthInspection.CanResume && growthInspection.Cartridge.StructuredRequirements.SequenceEqual(completeGrowthBlueprint.Features) &&
          growthInspection.Cartridge.PendingFeatures.Contains(growthLater[1]) &&
          growthInspection.Cartridge.KnownIssues.Any(issue => issue.Contains(growthLater[1], StringComparison.Ordinal)) &&
          growthRequirements.RootElement.GetProperty("requestedFeatures").GetArrayLength() == completeGrowthBlueprint.Features.Count &&
          growthManifest.RootElement.GetProperty("blueprint").GetProperty("features").GetArrayLength() == completeGrowthBlueprint.Features.Count,
        "split-generation-keeps-unverified-later-requirements-in-ledger-cartridge-and-portable-metadata");
    var appCartridgeJson = File.ReadAllText(Path.Combine(appProject, ".localstudio", "project-cartridge.json"));
    Check(!appCartridgeJson.Contains(appProject, StringComparison.OrdinalIgnoreCase) && appCartridgeJson.Contains("\"schemaVersion\": 1"), "project-cartridge-is-portable-and-versioned");
    var catalogProjects = new ProjectCatalog(smokePaths).GetProjects();
    var focusCatalogProject = catalogProjects.Single(project => project.Name == "Daily Focus");
    Check(focusCatalogProject.Path == appProject, "project-catalog-loads-created-project");
    Check(focusCatalogProject.ImplementationMode != "verified-template" && focusCatalogProject.Features.SequenceEqual(appBlueprint.Features), "project-catalog-loads-improvement-context:" + focusCatalogProject.ImplementationMode);
    var improvementRequest = ProjectImprovementRequestBuilder.Build(focusCatalogProject, "完了した項目に日時を表示して");
    Check(improvementRequest.Contains("Daily Focus") && improvementRequest.Contains(appBlueprint.Features[0]) && improvementRequest.Contains("完了した項目に日時を表示して"), "improvement-request-preserves-source-context");
    Check(!improvementRequest.Contains(appProject, StringComparison.OrdinalIgnoreCase), "improvement-request-does-not-disclose-project-path");
    var originalVersionScript = File.ReadAllText(Path.Combine(appProject, "app.js"));
    var separateVersionProject = await new ProjectScaffolder(smokePaths, generativeGenerator: new FixedGenerativeProjectGenerator(taskSource)).CreateAsync(
        appBlueprint,
        StudioProjectKind.DesktopOrWebApp,
        derivation: ProjectDerivation.Improvement(focusCatalogProject, "完了した項目に日時を表示して"));
    Check(separateVersionProject != appProject && Directory.Exists(separateVersionProject), "improvement-version-uses-separate-project");
    Check(File.ReadAllText(Path.Combine(appProject, "app.js")) == originalVersionScript, "improvement-version-leaves-source-unchanged");
    var separateVersionManifest = File.ReadAllText(Path.Combine(separateVersionProject, "localstudio.json"));
    Check(separateVersionManifest.Contains("\"Type\": \"improved-version\"") && separateVersionManifest.Contains(Path.GetFileName(appProject)) && !separateVersionManifest.Contains(appProject, StringComparison.OrdinalIgnoreCase), "improvement-version-lineage-without-absolute-path");
    Check(File.ReadAllText(Path.Combine(separateVersionProject, ".localstudio", "baseline", "files", "localstudio.json")) == separateVersionManifest, "improvement-lineage-in-accepted-baseline");
    Check(File.Exists(Path.Combine(appProject, "index.html")), "web-app-index-created");
    Check(File.Exists(Path.Combine(appProject, "styles.css")), "web-app-styles-created");
    Check(File.Exists(Path.Combine(appProject, "app.js")), "web-app-script-created");
    Check(File.Exists(Path.Combine(appProject, "requirements.json")), "web-app-requirements-created");
    var initialVerification = File.ReadAllText(Path.Combine(appProject, ".localstudio", "verification", "initial.json"));
    Check(initialVerification.Contains("\"result\": \"STRUCTURAL_PASS\""), "web-app-initial-verification-evidence:" + System.Text.RegularExpressions.Regex.Match(initialVerification, "\"result\": \"[^\"]*\"").Value);

    // Phase 2-1: single-file cartridge (.lscart). Sources and the key file only; build outputs are never included or opened.
    static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var directory in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, directory)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
    }
    var cartridgeRoot = Path.Combine(smokeRoot, "cartridge-tests");
    var cartridgePaths = new AppPaths(cartridgeRoot, Path.Combine(cartridgeRoot, "runtime"), Path.Combine(cartridgeRoot, "models"),
        Path.Combine(cartridgeRoot, "state"), Path.Combine(cartridgeRoot, "logs"), Path.Combine(cartridgeRoot, "downloads"), Path.Combine(cartridgeRoot, "projects"));
    cartridgePaths.EnsureCreated();
    var countingCompiler = new CountingWindowsCompiler();
    var cartridges = new ProjectCartridgePackages(cartridgePaths, countingCompiler);
    var cartridgeWork = Path.Combine(cartridgeRoot, "work");
    Directory.CreateDirectory(cartridgeWork);

    var exportSource = Path.Combine(cartridgeWork, "export-source");
    CopyTree(appProject, exportSource);
    var placeholderExe = Path.Combine(exportSource, "LocalStudioApp.exe");
    await File.WriteAllTextAsync(placeholderExe, "placeholder build output for tests (not an executable)");
    Directory.CreateDirectory(Path.Combine(exportSource, "bin"));
    await File.WriteAllTextAsync(Path.Combine(exportSource, "bin", "notes.json"), "{}");
    var cartridgeFile = Path.Combine(cartridgeWork, "daily-focus.lscart");
    string exportedCartridge = "";
    var buildOutputOpened = "none";
    // While the placeholder EXE is held exclusively, any attempt to open it fails, so passing proves it was never opened.
    using (File.Open(placeholderExe, FileMode.Open, FileAccess.Read, FileShare.None))
    {
        try
        {
            exportedCartridge = await cartridges.ExportAsync(exportSource, cartridgeFile);
            var fingerprintsWithLockedExe = await ProjectCartridgeStore.FingerprintSourcesAsync(exportSource);
            if (fingerprintsWithLockedExe.Any(file => file.RelativePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || file.RelativePath.StartsWith("bin/", StringComparison.OrdinalIgnoreCase)))
                buildOutputOpened = "listed-in-fingerprints";
            await ProjectScaffolder.CreateAcceptedBaselineAsync(exportSource, "test", CancellationToken.None);
            if (File.Exists(Path.Combine(exportSource, ".localstudio", "baseline", "files", "LocalStudioApp.exe")))
                buildOutputOpened = "copied-into-baseline";
        }
        catch (IOException exception) { buildOutputOpened = "opened:" + exception.Message; }
    }
    Check(buildOutputOpened == "none", "build-outputs-are-never-opened-by-cartridge-fingerprint-or-baseline:" + buildOutputOpened);
    var exportInspection = await cartridges.InspectAsync(exportedCartridge);
    Check(exportInspection.Valid && exportInspection.FileCount > 0 &&
          exportInspection.ExcludedPaths.Contains("LocalStudioApp.exe") && exportInspection.ExcludedPaths.Contains("bin/notes.json"),
        "cartridge-export-excludes-build-outputs:" + string.Join("|", exportInspection.Issues));

    var importResult = await cartridges.ImportAsync(exportedCartridge);
    var importedInspection = await ProjectCartridgeStore.InspectAsync(importResult.ProjectPath);
    var importedListed = new ProjectCatalog(cartridgePaths).GetProjects().Any(project => project.Path == importResult.ProjectPath);
    Check(importedInspection.CanResume && importedListed && importResult.Rebuilt &&
          File.ReadAllText(Path.Combine(importResult.ProjectPath, "app.js")) == File.ReadAllText(Path.Combine(appProject, "app.js")) &&
          File.Exists(Path.Combine(importResult.ProjectPath, ".localstudio", "requirement-ledger.json")) &&
          File.Exists(Path.Combine(importResult.ProjectPath, ".localstudio", "import.json")) &&
          !File.Exists(Path.Combine(importResult.ProjectPath, "LocalStudioApp.exe")) &&
          File.Exists(cartridgeFile),
        "cartridge-roundtrip-imports-as-new-verified-project:" + string.Join("|", importedInspection.Issues) + ":" + importResult.RebuildSummary);

    // Tampered packages are rejected before anything is extracted.
    async Task<CartridgeInspection> InspectTamperedAsync(string name, Func<ZipArchive, CartridgePackageManifest, CartridgePackageManifest?> mutate)
    {
        var copy = Path.Combine(cartridgeWork, name + ".lscart");
        File.Copy(exportedCartridge, copy, true);
        using (var archive = ZipFile.Open(copy, ZipArchiveMode.Update))
        {
            var manifestEntry = archive.GetEntry("cartridge.json")!;
            CartridgePackageManifest manifest;
            using (var reader = manifestEntry.Open()) manifest = JsonSerializer.Deserialize<CartridgePackageManifest>(reader, PortableProjectJson.JsonOptions)!;
            var changed = mutate(archive, manifest);
            if (changed is not null)
            {
                manifestEntry.Delete();
                using var writer = archive.CreateEntry("cartridge.json").Open();
                JsonSerializer.Serialize(writer, changed, PortableProjectJson.JsonOptions);
            }
        }
        return await cartridges.InspectAsync(copy);
    }
    static void AddEntry(ZipArchive archive, string name, byte[] content)
    {
        using var stream = archive.CreateEntry(name).Open();
        stream.Write(content);
    }
    static CartridgePackageManifest Declare(CartridgePackageManifest manifest, string path, byte[] content)
    {
        var files = manifest.Files.Append(new ProjectSourceFingerprint(path, content.LongLength, Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant())).ToArray();
        return manifest with { Files = files, Project = manifest.Project with { SourceFiles = files }, SetSha256 = ProjectCartridgePackages.SetSha256(files) };
    }
    var payloadBytes = Encoding.UTF8.GetBytes("payload");
    var shaMismatch = await InspectTamperedAsync("sha-mismatch", (archive, _) =>
    {
        archive.GetEntry("files/app.js")!.Delete();
        AddEntry(archive, "files/app.js", Encoding.UTF8.GetBytes("console.log('tampered');"));
        return null;
    });
    Check(!shaMismatch.Valid && shaMismatch.Issues.Contains("entry.sha256-mismatch:app.js"), "cartridge-rejects-changed-file:" + string.Join("|", shaMismatch.Issues));
    var unlisted = await InspectTamperedAsync("unlisted", (archive, _) => { AddEntry(archive, "files/extra.txt", payloadBytes); return null; });
    Check(!unlisted.Valid && unlisted.Issues.Contains("entry.unlisted:extra.txt"), "cartridge-rejects-unlisted-file");
    var withExe = await InspectTamperedAsync("with-exe", (archive, manifest) => { AddEntry(archive, "files/payload.exe", payloadBytes); return Declare(manifest, "payload.exe", payloadBytes); });
    Check(!withExe.Valid && withExe.Issues.Contains("file.disallowed-type:payload.exe"), "cartridge-rejects-executable-even-if-declared");
    var traversal = await InspectTamperedAsync("traversal", (archive, manifest) => { AddEntry(archive, "files/../evil.txt", payloadBytes); return Declare(manifest, "../evil.txt", payloadBytes); });
    Check(!traversal.Valid && traversal.Issues.Any(issue => issue.StartsWith("file.unsafe-path:", StringComparison.Ordinal)), "cartridge-rejects-path-traversal");
    var reserved = await InspectTamperedAsync("reserved", (archive, manifest) => { AddEntry(archive, "files/CON.txt", payloadBytes); return Declare(manifest, "CON.txt", payloadBytes); });
    Check(!reserved.Valid && reserved.Issues.Contains("file.disallowed-type:CON.txt"), "cartridge-rejects-windows-reserved-names");
    var bombBytes = new byte[3 * 1024 * 1024];
    var bomb = await InspectTamperedAsync("bomb", (archive, manifest) =>
    {
        AddEntry(archive, "files/bomb.txt", bombBytes);
        var claimed = manifest.Files.Append(new ProjectSourceFingerprint("bomb.txt", 10, new string('0', 64))).ToArray();
        return manifest with { Files = claimed, Project = manifest.Project with { SourceFiles = claimed }, SetSha256 = ProjectCartridgePackages.SetSha256(claimed) };
    });
    Check(!bomb.Valid && bomb.Issues.Contains("entry.sha256-mismatch:bomb.txt"), "cartridge-bounds-reads-against-oversized-content:" + string.Join("|", bomb.Issues));
    var setMismatch = await InspectTamperedAsync("set-mismatch", (_, manifest) => manifest with { SetSha256 = new string('0', 64) });
    Check(!setMismatch.Valid && setMismatch.Issues.Contains("package.set-sha256-mismatch"), "cartridge-rejects-set-fingerprint-mismatch");

    var projectsBeforeRejected = Directory.GetDirectories(cartridgePaths.ProjectRoot).Length;
    var rejectedImport = false;
    try { await cartridges.ImportAsync(Path.Combine(cartridgeWork, "with-exe.lscart")); }
    catch (InvalidDataException) { rejectedImport = true; }
    Check(rejectedImport && Directory.GetDirectories(cartridgePaths.ProjectRoot).Length == projectsBeforeRejected, "rejected-cartridge-creates-no-project");

    var noCartridgeProject = Path.Combine(cartridgeWork, "no-cartridge");
    Directory.CreateDirectory(noCartridgeProject);
    await File.WriteAllTextAsync(Path.Combine(noCartridgeProject, "index.html"), "<!doctype html><title>old</title>");
    var noCartridgeRefused = false;
    try { await cartridges.ExportAsync(noCartridgeProject, Path.Combine(cartridgeWork, "old.lscart")); }
    catch (InvalidDataException) { noCartridgeRefused = true; }
    Check(noCartridgeRefused && !cartridges.CanExport(noCartridgeProject), "project-without-cartridge-info-is-not-exported");
    var intoProjectRefused = false;
    try { await cartridges.ExportAsync(exportSource, Path.Combine(exportSource, "self.lscart")); }
    catch (InvalidOperationException) { intoProjectRefused = true; }
    var outsideProfileRefused = false;
    try { await cartridges.ExportAsync(exportSource, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp", "outside.lscart")); }
    catch (InvalidOperationException) { outsideProfileRefused = true; }
    var wrongExtensionRefused = false;
    try { await cartridges.ExportAsync(exportSource, Path.Combine(cartridgeWork, "wrong.zip")); }
    catch (InvalidDataException) { wrongExtensionRefused = true; }
    Check(intoProjectRefused && outsideProfileRefused && wrongExtensionRefused, "cartridge-export-destination-is-restricted");

    // Imported Windows sources are untrusted: no EXE is produced unless the source verifier passes.
    var importedUnsafeWindowsRoot = Path.Combine(cartridgeWork, "unsafe-windows");
    CopyTree(appProject, importedUnsafeWindowsRoot);
    await File.WriteAllTextAsync(Path.Combine(importedUnsafeWindowsRoot, "Program.cs"), "using System.Net; class Program { static void Main() { new System.Net.WebClient(); } }");
    var windowsManifestPath = Path.Combine(importedUnsafeWindowsRoot, "localstudio.json");
    var windowsManifest = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(windowsManifestPath))!;
    windowsManifest["projectKind"] = "WindowsApp";
    await File.WriteAllTextAsync(windowsManifestPath, windowsManifest.ToJsonString());
    var unsafeWindowsCartridge = await cartridges.ExportAsync(importedUnsafeWindowsRoot,Path.Combine(cartridgeWork, "unsafe-windows.lscart"));
    var unsafeWindowsImport = await cartridges.ImportAsync(unsafeWindowsCartridge);
    Check(!unsafeWindowsImport.Rebuilt && countingCompiler.Calls == 0 && !File.Exists(Path.Combine(unsafeWindowsImport.ProjectPath, "LocalStudioApp.exe")),
        "imported-windows-source-is-verified-before-any-exe-is-built:" + unsafeWindowsImport.RebuildSummary);
    var webVerification = await new StaticWebAppVerifier().VerifyAsync(appProject);
    Check(webVerification.Passed && webVerification.PassedChecks.Count >= 12, "web-app-independent-verification");
    var webScript = File.ReadAllText(Path.Combine(appProject, "app.js"));
    Check(webScript.Contains("function addTask") && webScript.Contains("localStorage.setItem"), "web-app-functional-script");
    Check(!webScript.Contains("innerHTML") && !webScript.Contains("eval("), "web-app-dangerous-js-absent");
    Check(LocalWebAppGenerationEngine.FindForbiddenScriptApis(
            "button.addEventListener('click', function() { const retrieval = (x) => x; setInterval(function () {}, 1000); });").Count == 0,
        "ordinary-anonymous-functions-are-not-dynamic-code");
    var dangerousFound = LocalWebAppGenerationEngine.FindForbiddenScriptApis(
        "list.innerHTML = s; window.eval(a); const f = new Function('x'); Function('y'); import('./m.js'); fetch(u);");
    Check(new[] { "innerHTML", "eval", "new Function", "Function", "import()", "fetch(" }.All(dangerousFound.Contains),
        "forbidden-script-apis-are-named:" + string.Join(",", dangerousFound));
    var legacyVerificationStore = new ProjectVerificationStatusStore(smokePaths);
    Check(legacyVerificationStore.Load(appProject).Applicable, "generated-app-asks-the-user-to-confirm-its-features");
    var legacyProject = Path.Combine(smokePaths.ProjectRoot, "legacy-generated-project");
    Directory.CreateDirectory(legacyProject);
    foreach (var fileName in new[] { "index.html", "styles.css", "app.js", "requirements.json", "localstudio.json" })
        File.Copy(Path.Combine(appProject, fileName), Path.Combine(legacyProject, fileName));
    var legacyManifestPath = Path.Combine(legacyProject, "localstudio.json");
    await File.WriteAllTextAsync(legacyManifestPath, File.ReadAllText(legacyManifestPath).Replace("\"lifecycle\": \"verified-project\"", "\"lifecycle\": \"generated-project\"", StringComparison.Ordinal));
    var legacyPending = legacyVerificationStore.Load(legacyProject);
    Check(legacyPending.Applicable && legacyPending.Features.Count == appBlueprint.Features.Count, "legacy-generated-project-features-derived:" + string.Join(",", legacyPending.Features.Select(item => item.Id)));
    var legacyPartial = await legacyVerificationStore.RecordAsync(legacyProject, legacyPending.Features[0].Id, ManualFeatureStatus.Confirmed);
    Check(legacyPartial.Level == "partial", "legacy-generated-project-confirmation-recorded");
    foreach (var feature in legacyPartial.Features.Where(item => item.Status == ManualFeatureStatus.Pending))
        legacyPartial = await legacyVerificationStore.RecordAsync(legacyProject, feature.Id, ManualFeatureStatus.Confirmed);
    Check(legacyPartial.Level == "user-verified" && File.Exists(Path.Combine(legacyProject, ".localstudio", "verification", "manual.json")), "legacy-generated-project-can-be-fully-verified");
    var webStylesPath = Path.Combine(appProject, "styles.css");
    var originalWebStyles = File.ReadAllText(webStylesPath);
    await File.AppendAllTextAsync(webStylesPath, "\n/* https://external.invalid must fail */\n");
    var rejectedWebVerification = await new StaticWebAppVerifier().VerifyAsync(appProject);
    Check(!rejectedWebVerification.Passed && rejectedWebVerification.Errors.Contains("security.no-external-resources"), "web-app-external-resource-rejected");
    await File.WriteAllTextAsync(webStylesPath, originalWebStyles);
    await CheckThrowsAsync(() => new ProjectScaffolder(smokePaths, generativeGenerator: new FixedGenerativeProjectGenerator(new GeneratedProjectSource([new("..\\escaped.txt", "blocked")], "generated-local-web-app")))
        .CreateAsync(appBlueprint, StudioProjectKind.DesktopOrWebApp), "generated-path-escape-blocked");

    var calculatorBlueprint = BlueprintValidator.Validate(new StudioBlueprint(
        "価格計算アプリ",
        "価格、割引率、消費税率から最終支払額を計算し、履歴をローカル保存するアプリ",
        "app",
        ["価格、割引率、消費税率の入力", "最終支払額の計算", "計算履歴の保存と削除"],
        [new("計算", "割引後に消費税を適用"), new("履歴", "このPC内に保存")],
        [],
        ["ネットワーク通信を行わない"],
        ["workspace.read", "workspace.write"]));
    // A price calculator (once a fixed template) is now made for its request like any other app.
    Check(ImplementationSupportPolicy.Evaluate(calculatorBlueprint, StudioProjectKind.DesktopOrWebApp).Route == ProjectImplementationRoute.GeneratedLocalWebApp, "price-calculator-is-generated-not-a-fixed-template");
    await CheckThrowsAsync(() => new ProjectScaffolder(smokePaths).CreateAsync(calculatorBlueprint, StudioProjectKind.DesktopOrWebApp), "price-calculator-needs-the-generator");

    var unsupportedBlueprint = BlueprintValidator.Validate(new StudioBlueprint("付箋メモ", "複数の付箋を自由配置するアプリ", "app", ["付箋の自由配置"], [new("実装", "付箋を作る")], [], [], ["workspace.read", "workspace.write"]));
    await CheckThrowsAsync(() => new ProjectScaffolder(smokePaths).CreateAsync(unsupportedBlueprint, StudioProjectKind.DesktopOrWebApp), "unsupported-app-does-not-masquerade-as-todo");

    var generatedDecision = ImplementationSupportPolicy.Evaluate(unsupportedBlueprint, StudioProjectKind.DesktopOrWebApp);
    Check(generatedDecision.CanCreate && generatedDecision.Route == ProjectImplementationRoute.GeneratedLocalWebApp, "unsupported-template-routes-to-bounded-generation");
    var generatedBody = """
        <section><h2>付箋</h2><textarea id="note-input" placeholder="付箋の内容"></textarea>
        <button id="add-note" type="button" aria-label="新しい付箋を追加">追加</button><ul id="notes"></ul></section>
        """;
    var rejectedGeneratedScript = "// LS-REQ-001\nfetch('blocked');\ndocument.getElementById('add-note').addEventListener('click', () => {});";
    var acceptedGeneratedScript = """
        'use strict';
        const input = document.getElementById('note-input');
        const list = document.getElementById('notes');
        const status = document.getElementById('ls-status');
        // LS-REQ-001: 付箋を個別の要素として追加する。
        document.getElementById('add-note').addEventListener('click', () => {
          const value = input.value.trim();
          if (!value) return;
          const item = document.createElement('li');
          item.textContent = value;
          list.append(item);
          input.value = '';
          status.textContent = '付箋を追加しました。';
        });
        """;
    var responses = new SequenceJsonHandler(
        TestData.CandidateEnvelope(generatedBody, rejectedGeneratedScript),
        TestData.CandidateEnvelope(generatedBody, acceptedGeneratedScript),
        TestData.ReviewEnvelope("LS-REQ-001", "notes", "list.append"));
    await using var generatedRuntime = new FakeLocalModelRuntime(new Uri("http://127.0.0.1:54321/"));
    using var generatedEngine = new LocalWebAppGenerationEngine(
        generatedRuntime,
        new FakeSettingsStore(LocalStudioSettings.Default),
        new HttpClient(responses));
    var generatedSource = await generatedEngine.GenerateAsync(unsupportedBlueprint, StudioProjectKind.DesktopOrWebApp);
    Check(responses.RequestCount == 3 && generatedSource.ImplementationMode == "generated-local-web-app", "bounded-generation-repairs-and-reviews-candidate");
    var generatedProject = await new ProjectScaffolder(
        smokePaths,
        generativeGenerator: new FixedGenerativeProjectGenerator(generatedSource)).CreateAsync(
            unsupportedBlueprint,
            StudioProjectKind.DesktopOrWebApp,
            sourceRequest: "付箋を色分けし、検索と期限表示を含めてください");
    var generatedVerification = await new StaticWebAppVerifier().VerifyAsync(generatedProject);
    Check(generatedVerification.Passed && generatedVerification.PassedChecks.Contains("generated.behavior-evidence-grounded"), "generated-app-independent-verification");
    using (var generatedManifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(generatedProject, "localstudio.json"))))
        Check(generatedManifest.RootElement.GetProperty("sourceRequest").GetString()?.Contains("付箋を色分けし、検索と期限表示", StringComparison.Ordinal) == true, "original-request-is-retained-for-long-horizon-generation");
    var generatedHtml = File.ReadAllText(Path.Combine(generatedProject, "index.html"));
    var generatedRequirements = File.ReadAllText(Path.Combine(generatedProject, "requirements.json"));
    Check(!generatedHtml.Contains("ls-feature-contract") && !generatedHtml.Contains("実装対象の機能") && !generatedHtml.Contains("data-ls-requirement"), "generated-internal-contract-not-visible");
    Check(generatedRequirements.Contains("generated.no-visible-internal-contract"), "generated-hidden-contract-rule-declared");
    Check(File.ReadAllText(Path.Combine(generatedProject, "localstudio.json")).Contains("\"implementationMode\": \"generated-local-web-app\""), "generated-app-mode-recorded");
    Check(File.ReadAllText(Path.Combine(generatedProject, "localstudio.json")).Contains("\"lifecycle\": \"structural-preview\""), "generated-app-not-overclaimed-as-verified");
    Check(File.ReadAllText(Path.Combine(generatedProject, ".localstudio", "verification", "initial.json")).Contains("\"result\": \"STRUCTURAL_PASS\""), "generated-app-verification-scope-recorded");
    Check(File.ReadAllText(Path.Combine(generatedProject, ".localstudio", "baseline", "manifest.json")).Contains("\"status\": \"structural-preview\""), "generated-app-baseline-is-preview");
    var generatedLedger = await RequirementLedgerStore.LoadAsync(generatedProject);
    Check(!RequirementLedgerPolicy.Validate(generatedLedger).CompletionAllowed && generatedLedger.Items.All(item => item.State == RequirementState.NotTested), "structural-preview-cannot-be-completed-without-runtime-evidence");
    Check((await ProjectCartridgeStore.InspectAsync(generatedProject)).CanResume, "generated-project-cartridge-can-resume-before-edits");

    // Phase 3-1: conversation core — output type without a selector, questions instead of guesses, steering while running.
    var webKeywords = OutputTargetAdvisor.Analyze("ブラウザーで使うWebアプリにして");
    Check(OutputKindResolver.Resolve(webKeywords, null).Kind == StudioProjectKind.DesktopOrWebApp, "output-kind-keyword-decides");
    Check(OutputKindResolver.Resolve(webKeywords, new(StudioProjectKind.GoogleAppsScript, 0.9, "")).NeedsAnswer, "output-kind-conflicting-judgement-asks");
    var vagueKeywords = OutputTargetAdvisor.Analyze("家計簿を作りたい");
    Check(OutputKindResolver.Resolve(vagueKeywords, new(StudioProjectKind.WindowsApp, 0.85, "")).Kind == StudioProjectKind.WindowsApp, "output-kind-confident-model-decides");
    var unsureKind = OutputKindResolver.Resolve(vagueKeywords, new(StudioProjectKind.DesktopOrWebApp, 0.4, "どこで使いますか？"));
    Check(unsureKind.NeedsAnswer && unsureKind.Message == "どこで使いますか？" && unsureKind.Choices.Count == 4, "output-kind-low-confidence-asks-with-choices");
    Check(OutputKindJudgement.Parse("{bad").Kind is null &&
          OutputKindJudgement.Parse("{\"kind\":\"gas\",\"confidence\":7}") is { Kind: StudioProjectKind.GoogleAppsScript, Confidence: 1 } &&
          OutputKindJudgement.Parse("{\"kind\":\"rocket\",\"confidence\":0.99}").Confidence == 0,
        "output-kind-judgement-is-parsed-defensively");
    Check(OutputKindResolver.FromAnswer("ブラウザで開きたい") == StudioProjectKind.DesktopOrWebApp && OutputKindResolver.FromAnswer("まだ決めていない") is null, "output-kind-answer-is-read-from-chat");
    var classifierResponses = new SequenceJsonHandler(JsonSerializer.Serialize(new { message = new { content = "{\"kind\":\"windows-app\",\"confidence\":0.82,\"question\":\"\"}" } }));
    using (var classifierEngine = new LocalPlanningEngine(generatedRuntime, new HttpClient(classifierResponses)))
    {
        var judged = await classifierEngine.ClassifyAsync("家計簿を作りたい", LocalStudioSettings.DefaultModel);
        Check(classifierResponses.RequestCount == 1 && judged.Kind == StudioProjectKind.WindowsApp && judged.Confidence > 0.8, "local-classifier-returns-structured-judgement");
    }
    // qwen2.5-coder:1.5b once repeated itself inside "questions" until the output limit (2026-10-09): the planner
    // bounds every field with a schema and tries once more when the answer is still broken.
    var plannerResponses = new CapturingJsonHandler(
        JsonSerializer.Serialize(new { message = new { content = "{\"name\":\"サイコロ\",\"questions\":[\"何面ですか何面ですか何面" } }),
        JsonSerializer.Serialize(new { message = new { content = JsonSerializer.Serialize(new { name = "サイコロ", summary = "サイコロを振る", kind = "app", features = new[] { "振る" }, steps = new[] { new { title = "作成", outcome = "振れる" } }, questions = Array.Empty<string>(), safety_notes = Array.Empty<string>(), capabilities = Array.Empty<string>() }) } }));
    using (var retryingPlanner = new LocalPlanningEngine(generatedRuntime, new HttpClient(plannerResponses)))
    {
        var retried = await retryingPlanner.PlanAsync("サイコロを作って", StudioProjectKind.DesktopOrWebApp, LocalStudioSettings.DefaultModel);
        using var sentPlan = JsonDocument.Parse(plannerResponses.Bodies[0]);
        Check(plannerResponses.Bodies.Count == 2 && retried.Name == "サイコロ" &&
              sentPlan.RootElement.GetProperty("format").GetProperty("properties").GetProperty("questions").GetProperty("items").GetProperty("maxLength").GetInt32() == 180,
            "planner-bounds-fields-and-retries-a-broken-answer-once");
    }

    var steeringInbox = new SteeringInbox();
    steeringInbox.Enqueue("ボタンの文字をもっと大きくして");
    var steeringCapture = new CapturingJsonHandler(
        TestData.CandidateEnvelope(generatedBody, acceptedGeneratedScript),
        TestData.ReviewEnvelope("LS-REQ-001", "notes", "list.append"));
    using (var steeredEngine = new LocalWebAppGenerationEngine(generatedRuntime, new FakeSettingsStore(LocalStudioSettings.Default), new HttpClient(steeringCapture)))
        await steeredEngine.GenerateAsync(unsupportedBlueprint, StudioProjectKind.DesktopOrWebApp, steering: steeringInbox);
    Check(steeringCapture.Bodies.Count == 2 && steeringCapture.Bodies.All(body => body.Contains("additionalInstructions", StringComparison.Ordinal)) &&
          steeringInbox.Applied is [{ Attempt: 1 }] && steeringInbox.Pending.Count == 0,
        "steering-instruction-reaches-generation-and-review");
    // Test first, end to end: examples written from the request, a wrong app sent back by them, the right one kept with them.
    var firstCounterBlueprint = BlueprintValidator.Validate(new StudioBlueprint("カウンター", "数を数える", "app", ["ボタンを押すと数を1ずつ増やす"], [new("実装", "作る")], [], [], ["workspace.read", "workspace.write"]));
    var counterExamples = JsonSerializer.Serialize(new { message = new { content = JsonSerializer.Serialize(new { scenarios = new[] {
        new { requirement = "LS-REQ-001", title = "2回押すと2", steps = new object[] { new { press = "増やす" }, new { press = "増やす" } }, expect = new[] { "2回" } } } }) } });
    const string counterBody = "<p id=\"count-view\">0回</p><button id=\"plus\" type=\"button\">増やす</button>";
    var counterCapture = new CapturingJsonHandler(
        counterExamples,
        TestData.CandidateEnvelope(counterBody, "document.getElementById('plus').addEventListener('click', () => { document.getElementById('count-view').textContent = '1回'; });"),
        TestData.CandidateEnvelope(counterBody, "let count = 0; document.getElementById('plus').addEventListener('click', () => { count += 1; document.getElementById('count-view').textContent = count + '回'; });"),
        TestData.ReviewEnvelope("LS-REQ-001", "plus", "textContent"));
    GeneratedProjectSource counterSource;
    using (var counterEngine = new LocalWebAppGenerationEngine(generatedRuntime, new FakeSettingsStore(LocalStudioSettings.Default), new HttpClient(counterCapture)) { WriteAcceptance = true })
        counterSource = await counterEngine.GenerateAsync(firstCounterBlueprint, StudioProjectKind.DesktopOrWebApp);
    Check(counterCapture.Bodies.Count == 4 && counterCapture.Bodies[1].Contains("acceptanceChecks", StringComparison.Ordinal) &&
          counterCapture.Bodies[2].Contains("acceptance.failed", StringComparison.Ordinal) &&
          counterSource.Files.Any(file => file.RelativePath == AcceptanceScenario.FileName && file.Content.Contains("2回", StringComparison.Ordinal)) &&
          counterSource.Files.Single(file => file.RelativePath == "app.js").Content.Contains("count += 1", StringComparison.Ordinal),
        "acceptance-examples-written-first-send-back-a-wrong-app-and-keep-the-right-one");
    var recordedInbox = new SteeringInbox();
    recordedInbox.Enqueue("付箋を黄色にして");
    var steeredProject = await new ProjectScaffolder(smokePaths, generativeGenerator: new SteeringAwareGenerator(generatedSource))
        .CreateAsync(unsupportedBlueprint, StudioProjectKind.DesktopOrWebApp, steering: recordedInbox);
    using (var steeredManifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(steeredProject, "localstudio.json"))))
        Check(steeredManifest.RootElement.GetProperty("appliedInstructions")[0].GetProperty("Text").GetString() == "付箋を黄色にして", "applied-instructions-are-recorded-in-project");
    var limitInbox = new SteeringInbox();
    for (var index = 0; index < SteeringInbox.MaxPendingInstructions; index++) limitInbox.Enqueue("指示" + index);
    var overflowRejected = false;
    try { limitInbox.Enqueue("多すぎる指示"); } catch (InvalidOperationException) { overflowRejected = true; }
    var tooLongRejected = false;
    try { new SteeringInbox().Enqueue(new string('あ', SteeringInbox.MaxInstructionCharacters + 1)); } catch (InvalidDataException) { tooLongRejected = true; }
    Check(overflowRejected && tooLongRejected && SteeringRequest.Augment("{\"a\":1}", []) == "{\"a\":1}", "steering-is-bounded-and-optional");

    // 4-5: libraries the user adds — shelf with hashes, copied into the app, guide given to the model, checked by the verifier.
    var libraryShelf = new LibraryShelf(Path.Combine(smokeRoot, "libraries"));
    var librarySource = Path.Combine(Path.GetTempPath(), "ls-lib-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(librarySource);
    var tinyBarFile = Path.Combine(librarySource, "tinybar.js");
    await File.WriteAllTextAsync(tinyBarFile, "window.TinyBar = { draw: function (element, values) { element.textContent = values.join(' | '); } };\n", new UTF8Encoding(false));
    var badFile = Path.Combine(librarySource, "tool.exe");
    await File.WriteAllTextAsync(badFile, "x");
    var tinyBarGuide = "TinyBar はブラウザの window.TinyBar として読み込み済み。TinyBar.draw(要素, 数値の配列) で要素に値を並べて表示する。";
    var badDraft = new LibraryDraft("Tiny Bar", "1.0", "", "社内配布", "Tiny-Bar", [badFile], "");
    var tinyBar = libraryShelf.Add(new LibraryDraft("tinybar", "1.0.0", "MIT", "社内配布（Smoke）", "TinyBar", [tinyBarFile], tinyBarGuide));
    Check(LibraryShelf.Check(badDraft).Count >= 5 && libraryShelf.List().Single().Key == "tinybar@1.0.0" &&
          libraryShelf.Match("TinyBar で付箋の数をグラフにして").Single().Name == "tinybar" && libraryShelf.Match("付箋アプリ").Count == 0,
        "library-shelf-accepts-checked-files-and-finds-libraries-named-in-requests:" + string.Join("|", LibraryShelf.Check(badDraft)));
    var libraryScript = acceptedGeneratedScript + "\nTinyBar.draw(document.getElementById('ls-status'), [list.children.length]);\n";
    var libraryCapture = new CapturingJsonHandler(
        TestData.CandidateEnvelope(generatedBody, libraryScript),
        TestData.ReviewEnvelope("LS-REQ-001", "notes", "list.append"));
    GeneratedProjectSource librarySourceFiles;
    using (var libraryEngine = new LocalWebAppGenerationEngine(generatedRuntime, new FakeSettingsStore(LocalStudioSettings.Default), new HttpClient(libraryCapture)) { Libraries = libraryShelf })
        librarySourceFiles = await libraryEngine.GenerateAsync(unsupportedBlueprint, StudioProjectKind.DesktopOrWebApp, sourceRequest: "付箋の数を tinybar で表示して");
    var libraryIndex = librarySourceFiles.Files.Single(file => file.RelativePath == "index.html").Content;
    Check(libraryCapture.Bodies.Count == 2 && libraryCapture.Bodies[0].Contains("TinyBar.draw", StringComparison.Ordinal) &&
          libraryCapture.Bodies[0].Contains("The only libraries are those in REQUEST.libraries", StringComparison.Ordinal) &&
          !steeringCapture.Bodies[0].Contains("REQUEST.libraries", StringComparison.Ordinal) &&
          librarySourceFiles.Files.Any(file => file.RelativePath == "vendor/tinybar/tinybar.js") && librarySourceFiles.Files.Any(file => file.RelativePath == LibraryShelf.VendorManifest) &&
          libraryIndex.IndexOf("vendor/tinybar/tinybar.js", StringComparison.Ordinal) is > 0 and var vendorAt && vendorAt < libraryIndex.IndexOf("src=\"app.js\"", StringComparison.Ordinal) &&
          libraryIndex.Contains("script-src 'self'", StringComparison.Ordinal),
        "library-is-loaded-before-app-js-from-the-project-and-its-guide-reaches-the-model");
    // A candidate that ignores the requested library (draws by hand) is sent back once with library.unused.
    var unusedCapture = new CapturingJsonHandler(
        TestData.CandidateEnvelope(generatedBody, acceptedGeneratedScript),
        TestData.CandidateEnvelope(generatedBody, libraryScript),
        TestData.ReviewEnvelope("LS-REQ-001", "notes", "list.append"));
    using (var unusedEngine = new LocalWebAppGenerationEngine(generatedRuntime, new FakeSettingsStore(LocalStudioSettings.Default), new HttpClient(unusedCapture)) { Libraries = libraryShelf })
        await unusedEngine.GenerateAsync(unsupportedBlueprint, StudioProjectKind.DesktopOrWebApp, sourceRequest: "付箋の数を tinybar で表示して");
    Check(unusedCapture.Bodies.Count == 3 && unusedCapture.Bodies[1].Contains("library.unused:tinybar", StringComparison.Ordinal),
        "a-candidate-that-ignores-the-requested-library-is-sent-back");
    // Drill 2026-10-12: the model added <script src="https://cdn…/chart.js"> to the body out of habit. A tag for a shelf
    // library is removed (LocalStudio loads it); a tag for anything else still fails validation.
    var cdnCapture = new CapturingJsonHandler(
        TestData.CandidateEnvelope(generatedBody + "\n<script src=\"https://cdn.example.com/npm/tinybar@1/tinybar.min.js\"></script>", libraryScript),
        TestData.ReviewEnvelope("LS-REQ-001", "notes", "list.append"));
    GeneratedProjectSource cdnSource;
    using (var cdnEngine = new LocalWebAppGenerationEngine(generatedRuntime, new FakeSettingsStore(LocalStudioSettings.Default), new HttpClient(cdnCapture)) { Libraries = libraryShelf })
        cdnSource = await cdnEngine.GenerateAsync(unsupportedBlueprint, StudioProjectKind.DesktopOrWebApp, sourceRequest: "付箋の数を tinybar で表示して");
    Check(cdnCapture.Bodies.Count == 2 && !cdnSource.Files.Single(file => file.RelativePath == "index.html").Content.Contains("cdn.example.com", StringComparison.Ordinal),
        "a-cdn-tag-for-a-shelf-library-is-removed");
    // Drill 2026-10-12: a Chart.js app was reported complete with <div id="sales-chart" …></canvas> and a bar added on
    // every keystroke. Both are now sent back before anything is saved.
    var brokenBody = generatedBody.Replace("<ul id=\"notes\"></ul>", "<div id=\"notes\"></canvas>", StringComparison.Ordinal);
    var keystrokeScript = acceptedGeneratedScript + "\nconst items = [];\ninput.addEventListener('input', () => { const typed = input.value; items.push(typed); });\n";
    var structureCapture = new CapturingJsonHandler(
        TestData.CandidateEnvelope(brokenBody, keystrokeScript),
        TestData.CandidateEnvelope(generatedBody, acceptedGeneratedScript),
        TestData.ReviewEnvelope("LS-REQ-001", "notes", "list.append"));
    using (var structureEngine = new LocalWebAppGenerationEngine(generatedRuntime, new FakeSettingsStore(LocalStudioSettings.Default), new HttpClient(structureCapture)))
        await structureEngine.GenerateAsync(unsupportedBlueprint, StudioProjectKind.DesktopOrWebApp);
    Check(structureCapture.Bodies.Count == 3 && structureCapture.Bodies[1].Contains("structure.tags-unbalanced", StringComparison.Ordinal) &&
          structureCapture.Bodies[1].Contains("behavior.append-on-input", StringComparison.Ordinal) &&
          BlueprintValidator.Validate(unsupportedBlueprint with { Features = ["同じ", "同じ", "同じ"] }).Features.Count == 1,
        "mismatched-tags-and-adding-on-every-keystroke-are-sent-back-and-repeated-features-are-kept-once");
    // A script that looks up an element the body does not have stops with a null error when the page opens.
    var missingCapture = new CapturingJsonHandler(
        TestData.CandidateEnvelope(generatedBody, acceptedGeneratedScript + "\ndocument.getElementById('note-count').textContent = '0';\nconst made = document.createElement('p'); made.id = 'made-here'; document.getElementById('made-here');\n"),
        TestData.CandidateEnvelope(generatedBody, acceptedGeneratedScript),
        TestData.ReviewEnvelope("LS-REQ-001", "notes", "list.append"));
    using (var missingEngine = new LocalWebAppGenerationEngine(generatedRuntime, new FakeSettingsStore(LocalStudioSettings.Default), new HttpClient(missingCapture)))
        await missingEngine.GenerateAsync(unsupportedBlueprint, StudioProjectKind.DesktopOrWebApp);
    Check(missingCapture.Bodies.Count == 3 && missingCapture.Bodies[1].Contains("behavior.missing-element:note-count", StringComparison.Ordinal) &&
          // The repair also carries the previous source (which mentions made-here); only the findings must not.
          !System.Text.RegularExpressions.Regex.IsMatch(missingCapture.Bodies[1], "missing-element:[^ ]*made-here"),
        "an-element-the-script-looks-up-must-exist");
    // Drill 2026-10-12: apps that passed every static check stopped when a button was pressed. The candidate is run.
    var runCapture = new CapturingJsonHandler(
        TestData.CandidateEnvelope(generatedBody, acceptedGeneratedScript.Replace("document.getElementById('add-note').addEventListener('click', () => {", "document.getElementById('add-note').addEventListener('click', () => { const saved = null; saved.push(1);", StringComparison.Ordinal)),
        TestData.CandidateEnvelope(generatedBody, acceptedGeneratedScript),
        TestData.ReviewEnvelope("LS-REQ-001", "notes", "list.append"));
    using (var runEngine = new LocalWebAppGenerationEngine(generatedRuntime, new FakeSettingsStore(LocalStudioSettings.Default), new HttpClient(runCapture)))
        await runEngine.GenerateAsync(unsupportedBlueprint, StudioProjectKind.DesktopOrWebApp);
    Check(runCapture.Bodies.Count == 3 && runCapture.Bodies[1].Contains("Pressing a button stopped the app with an error (add-note: click: TypeError", StringComparison.Ordinal) &&
          !runCapture.Bodies[1].Contains("Pressing the buttons changed nothing", StringComparison.Ordinal),
        "a-candidate-that-fails-when-its-button-is-pressed-is-sent-back:" + runCapture.Bodies.Count + ":" + (runCapture.Bodies.Count > 1 && runCapture.Bodies[1].IndexOf("rejected", StringComparison.Ordinal) is var at and >= 0 ? runCapture.Bodies[1].Substring(at, Math.Min(500, runCapture.Bodies[1].Length - at)) : ""));
    static string Page(string body) => $"<!doctype html><html><head><meta charset=\"utf-8\"></head><body><main id=\"app\">{body}<p id=\"ls-status\"></p></main><script src=\"app.js\"></script></body></html>";
    var stopwatchRun = await WebAppBehaviorChecker.CheckPageAsync(Page("<p id=\"t\">0.0</p><button id=\"go\">開始</button>"),
        "let start = 0; document.getElementById('go').addEventListener('click', () => { start = Date.now(); setInterval(() => { document.getElementById('t').textContent = ((Date.now() - start) / 1000).toFixed(1); }, 100); });", []);
    var silentRun = await WebAppBehaviorChecker.CheckPageAsync(Page("<p id=\"n\">0</p><button id=\"reset\">リセット</button>"),
        "document.getElementById('reset').addEventListener('click', () => { document.getElementById('n').textContent = '0'; });", []);
    // Hands-on 2026-10-13 (H14): the time is shown only when stopped.
    var silentTimerRun = await WebAppBehaviorChecker.CheckPageAsync(Page("<p id=\"t\">0</p><button id=\"go\">開始</button><button id=\"halt\">停止</button>"),
        "let start = 0, id = 0; document.getElementById('go').addEventListener('click', () => { start = Date.now(); id = setInterval(() => {}, 100); }); document.getElementById('halt').addEventListener('click', () => { clearInterval(id); document.getElementById('t').textContent = String(Date.now() - start); });", []);
    var noTimerRun = await WebAppBehaviorChecker.CheckPageAsync(Page("<p id=\"t\">00:00</p><button id=\"go\">開始</button>"),
        "document.getElementById('go').addEventListener('click', () => { document.getElementById('t').textContent = new Date().toISOString(); });", [], expectsTimer: true);
    var missingRun = await WebAppBehaviorChecker.CheckPageAsync(Page("<button id=\"a\">a</button>"), "document.getElementById('clear').addEventListener('click', () => {});", []);
    var loopRun = await WebAppBehaviorChecker.CheckPageAsync(Page("<button id=\"a\">a</button>"), "document.getElementById('a').addEventListener('click', () => { while (true) {} });", []);
    var chartRun = await WebAppBehaviorChecker.CheckPageAsync(Page("<canvas id=\"c\"></canvas><button id=\"draw\">描く</button>"),
        "const chart = new Chart(document.getElementById('c'), { type: 'bar', data: { labels: [], datasets: [{ data: [] }] } }); document.getElementById('draw').addEventListener('click', () => { chart.data.labels.push('a'); chart.update(); });", ["Chart"]);
    var redrawRun = await WebAppBehaviorChecker.CheckPageAsync(Page("<div id=\"box\"><input id=\"memo\" aria-label=\"メモ\"><button id=\"save\">保存</button><button id=\"clear\">消す</button></div>"),
        "document.getElementById('save').addEventListener('click', () => { document.getElementById('box').replaceChildren('保存しました'); }); document.getElementById('clear').addEventListener('click', () => {});", []);
    Check(stopwatchRun.Passed && stopwatchRun.Steps.Single().Changed &&
          !silentRun.Passed && silentRun.Problems.Any(problem => problem.StartsWith("behavior.nothing-changes", StringComparison.Ordinal)) &&
          !silentTimerRun.Passed && silentTimerRun.Problems.Any(problem => problem.StartsWith("behavior.timer-silent:go", StringComparison.Ordinal)) &&
          !noTimerRun.Passed && noTimerRun.Problems.Any(problem => problem.StartsWith("behavior.timer-missing", StringComparison.Ordinal)) &&
          !missingRun.Passed && missingRun.Problems.Any(problem => problem.StartsWith("behavior.load-error", StringComparison.Ordinal) && problem.Contains("addEventListener", StringComparison.Ordinal)) &&
          !loopRun.Passed && loopRun.Problems.Any(problem => problem.Contains("止まらない処理", StringComparison.Ordinal)) &&
          chartRun.Passed && chartRun.Notes.Any(note => note.Contains("Chart", StringComparison.Ordinal)) &&
          redrawRun.Passed && redrawRun.Notes.Any(note => note.Contains("#clear", StringComparison.Ordinal)),
        "the-behaviour-check-runs-apps-and-tells-working-from-broken:" + string.Join("|", stopwatchRun.Problems.Concat(silentRun.Problems).Concat(chartRun.Problems).Concat(redrawRun.Problems).Concat(loopRun.Problems)));
    var libraryProject = await new ProjectScaffolder(smokePaths, generativeGenerator: new FixedGenerativeProjectGenerator(librarySourceFiles))
        .CreateAsync(unsupportedBlueprint, StudioProjectKind.DesktopOrWebApp, sourceRequest: "付箋の数を tinybar で表示して");
    var libraryVerification = await new StaticWebAppVerifier().VerifyAsync(libraryProject);
    var vendorCopy = Path.Combine(libraryProject, "vendor", "tinybar", "tinybar.js");
    var libraryCartridge = await ProjectCartridgeStore.InspectAsync(libraryProject);
    await File.AppendAllTextAsync(vendorCopy, "window.extra = 1;\n");
    var tamperedVerification = await new StaticWebAppVerifier().VerifyAsync(libraryProject);
    Check(libraryVerification.Passed && libraryVerification.PassedChecks.Contains("security.vendor-libraries") && libraryCartridge.CanResume &&
          LibraryShelf.ProjectLibraries(libraryProject).SequenceEqual(["tinybar@1.0.0"]) &&
          !tamperedVerification.Passed && tamperedVerification.Errors.Contains("vendor.file-changed:vendor/tinybar/tinybar.js") &&
          LibraryShelf.VerifyProject(libraryProject, "<script src=\"vendor/other/x.js\"></script>").Contains("vendor.reference-undeclared:vendor/other/x.js"),
        "vendor-libraries-pass-only-with-their-recorded-hashes:" + string.Join("|", libraryVerification.Errors));
    // A changed file on the shelf is never used again.
    await File.AppendAllTextAsync(Path.Combine(tinyBar.Folder, "tinybar.js"), "// changed\n");
    Check(libraryShelf.List().Count == 0 && libraryShelf.Match("tinybar").Count == 0, "a-changed-library-on-the-shelf-is-not-used");
    CheckThrows(() => libraryShelf.Find(["tinybar@1.0.0"]), "an-app-whose-library-changed-is-not-improved-with-it");

    // 4-5 (b): official source (npm), with a fake registry — the archive must match the registry's SHA-512.
    byte[] Tarball(params (string Path, string Text)[] entries)
    {
        using var output = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new System.Formats.Tar.TarWriter(gzip))
            foreach (var (entryPath, text) in entries)
                tar.WriteEntry(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, entryPath) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)) });
        return output.ToArray();
    }
    const string umd = "!function(t,e){\"object\"==typeof exports?module.exports=e():(t=\"undefined\"!=typeof globalThis?globalThis:t||self).TinyChart=e()}(this,function(){return{draw:function(){}}});\n";
    var goodTarball = Tarball(("package/dist/tinychart.umd.js", umd), ("package/README.md", "# TinyChart\n```js\nconst c = TinyChart.draw(el);\n```\n<script src=\"https://cdn.example/tinychart.js\"></script>\n"),
        ("package/LICENSE", "MIT License"), ("package/../evil.js", "x"), ("package/src/index.ts", "export {}"));
    var goodIntegrity = "sha512-" + Convert.ToBase64String(SHA512.HashData(goodTarball));
    string Metadata(string integrity, string tarball) => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["dist-tags"] = new { latest = "2.0.0" },
        ["versions"] = new Dictionary<string, object> { ["2.0.0"] = new { license = "MIT", jsdelivr = "dist/tinychart.umd.js", dist = new { tarball, integrity, unpackedSize = 2048, fileCount = 4 } } }
    });
    var tarballUrl = "https://registry.npmjs.org/tinychart/-/tinychart-2.0.0.tgz";
    var npmRoot = Path.Combine(smokeRoot, "library-downloads");
    using (var npmHttp = new HttpClient(new FakeNpmHandler(new() { ["https://registry.npmjs.org/tinychart"] = Encoding.UTF8.GetBytes(Metadata(goodIntegrity, tarballUrl)), [tarballUrl] = goodTarball })))
    {
        var npm = new NpmLibrarySource(npmHttp, npmRoot);
        var npmInfo = await npm.ReadAsync("tinychart");
        var fetched = await npm.FetchAsync(npmInfo);
        Check(npmInfo is { Version: "2.0.0", License: "MIT", BrowserFile: "dist/tinychart.umd.js", UnpackedBytes: 2048 } && npmInfo.Integrity == goodIntegrity &&
              fetched.Candidates.Select(Path.GetFileName).SequenceEqual(["tinychart.umd.js"]) && fetched.DetectedGlobal == "TinyChart" &&
              fetched.Readme!.Contains("TinyChart.draw", StringComparison.Ordinal) && fetched.LicenseText == "MIT License" &&
              !Directory.EnumerateFiles(npmRoot, "evil.js", SearchOption.AllDirectories).Any() && !File.Exists(Path.Combine(smokeRoot, "evil.js")),
            "npm-package-is-read-then-fetched-checked-and-only-usable-files-unpacked:" + string.Join(",", fetched.Candidates.Select(Path.GetFileName)));
        var guide = LibraryGuideDrafter.Fallback("tinychart", "2.0.0", fetched.DetectedGlobal, fetched.Readme);
        Check(guide.Contains("TinyChart.draw(el)", StringComparison.Ordinal) && !guide.Contains("https://", StringComparison.Ordinal) &&
              LibraryGuideDrafter.Clean("使い方\n<script src=\"x.js\"></script>\nimport Chart from 'chart.js'\nnpm install chart.js\nnew Chart(el, {});") == "使い方\nnew Chart(el, {});" &&
              // Drill 2026-10-12: a Chart.js 4 draft used the version 2 option yAxes, which the README does not contain.
              LibraryGuideDrafter.UngroundedCode("```js\nconst c = TinyChart.draw(el);\nscales: { yAxes: [{ ticks: {} }] },\n```", fetched.Readme).SequenceEqual(["scales: { yAxes: [{ ticks: {} }] },"]),
            "library-guide-drafts-keep-examples-and-drop-anything-that-loads-code:" + guide.Replace("\n", " / "));
    }
    var tampered = (byte[])goodTarball.Clone();
    tampered[^10] ^= 0xFF;
    using (var badHttp = new HttpClient(new FakeNpmHandler(new() { ["https://registry.npmjs.org/tinychart"] = Encoding.UTF8.GetBytes(Metadata(goodIntegrity, tarballUrl)), [tarballUrl] = tampered })))
        await CheckThrowsAsync(async () => await new NpmLibrarySource(badHttp, npmRoot).FetchAsync(await new NpmLibrarySource(badHttp, npmRoot).ReadAsync("tinychart")), "npm-archive-with-another-sha512-is-refused");
    using (var elsewhereHttp = new HttpClient(new FakeNpmHandler(new() { ["https://registry.npmjs.org/tinychart"] = Encoding.UTF8.GetBytes(Metadata(goodIntegrity, "https://elsewhere.example/tinychart.tgz")) })))
        await CheckThrowsAsync(() => new NpmLibrarySource(elsewhereHttp, npmRoot).ReadAsync("tinychart"), "npm-archive-outside-the-registry-is-refused");

    var conversationRoot = Path.Combine(smokeRoot, "conversation-tests");
    var conversationPaths = new AppPaths(conversationRoot, Path.Combine(conversationRoot, "runtime"), Path.Combine(conversationRoot, "models"),
        Path.Combine(conversationRoot, "state"), Path.Combine(conversationRoot, "logs"), Path.Combine(conversationRoot, "downloads"), Path.Combine(conversationRoot, "projects"));
    conversationPaths.EnsureCreated();
    var damagedLogPath = Path.Combine(conversationRoot, "damaged", "conversation.jsonl");
    var damagedLog = ConversationLog.Open(damagedLogPath);
    await damagedLog.AppendAsync(ConversationMessage.FromUser("最初の依頼"));
    await File.AppendAllTextAsync(damagedLogPath, "{broken json\n");
    await damagedLog.AppendAsync(ConversationMessage.FromStudio("返答", new ConversationChoice("start", "作成開始")));
    var reloadedLog = ConversationLog.Open(damagedLogPath);
    Check(reloadedLog.Messages.Count == 2 && reloadedLog.SkippedLines == 1 && reloadedLog.Messages[1].Choices[0].Label == "作成開始", "conversation-record-skips-damaged-lines");

    var askingClassifier = new ScriptedClassifier(new OutputKindJudgement(null, 0.2, ""));
    var conversationPlanner = new RecordingPlanner(unsupportedBlueprint);
    var probeScaffolder = new ProbeScaffolder(conversationPaths.ProjectRoot);
    var conversation = new ConversationOrchestrator(conversationPlanner, askingClassifier, probeScaffolder, ConversationLog.CreateSession(conversationPaths), () => LocalStudioSettings.DefaultModel);
    await conversation.SubmitAsync("家計簿を作りたい");
    Check(conversation.Stage == ConversationStage.Clarifying && askingClassifier.Calls == 1 && conversationPlanner.CallCount == 0 && conversation.Messages[^1].Choices.Count == 4,
        "unclear-request-asks-before-planning");
    await conversation.ChooseAsync("kind:" + StudioProjectKind.DesktopOrWebApp);
    Check(conversation.Stage == ConversationStage.Proposed && conversation.Kind == StudioProjectKind.DesktopOrWebApp && conversationPlanner.CallCount == 1 &&
          conversation.Messages[^1].Choices.Any(choice => choice.Id == ConversationOrchestrator.StartChoice && choice.Label == "作成開始"),
        "answered-type-leads-to-proposal-with-start");
    var conversationRun = conversation.ChooseAsync(ConversationOrchestrator.StartChoice);
    await probeScaffolder.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
    await conversation.SubmitAsync("ボタンを大きくして").WaitAsync(TimeSpan.FromSeconds(10));
    Check(conversation.Stage == ConversationStage.Running, "message-during-run-does-not-block-or-restart");
    probeScaffolder.Release.TrySetResult();
    await conversationRun.WaitAsync(TimeSpan.FromSeconds(10));
    Check(conversation.Stage == ConversationStage.Completed && probeScaffolder.Taken.Select(item => item.Text).SequenceEqual(["ボタンを大きくして"]) &&
          probeScaffolder.LastRequest?.Contains("家計簿", StringComparison.Ordinal) == true,
        "steering-during-run-is-applied-at-next-attempt");
    var projectConversation = ConversationLog.OpenForProject(conversation.ProjectPath!);
    Check(string.Equals(conversation.Log.FilePath, projectConversation.FilePath, StringComparison.OrdinalIgnoreCase) &&
          projectConversation.Messages.Count == conversation.Messages.Count &&
          projectConversation.Messages.Any(message => message.Text.StartsWith("いただいた指示を反映して", StringComparison.Ordinal)) &&
          !Directory.EnumerateFiles(Path.Combine(conversationPaths.StateRoot, "conversations"), "*", SearchOption.AllDirectories).Any(),
        "conversation-record-moves-into-project");

    var keywordClassifier = new ScriptedClassifier(OutputKindJudgement.Unknown);
    var redoPlanner = new RecordingPlanner(unsupportedBlueprint);
    var redoScaffolder = new ProbeScaffolder(conversationPaths.ProjectRoot);
    var redo = new ConversationOrchestrator(redoPlanner, keywordClassifier, redoScaffolder, ConversationLog.CreateSession(conversationPaths), () => LocalStudioSettings.DefaultModel);
    await redo.SubmitAsync("ブラウザーで使える家計簿のWebアプリを作って");
    Check(keywordClassifier.Calls == 0 && redo.Kind == StudioProjectKind.DesktopOrWebApp && redo.Stage == ConversationStage.Proposed, "clear-request-is-decided-without-asking");
    var redoRun = redo.ChooseAsync(ConversationOrchestrator.StartChoice);
    await redoScaffolder.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
    await redo.SubmitAsync("やっぱりWindowsアプリ（EXE）にして");
    var offeredRedo = redo.Messages[^1].Choices.Any(choice => choice.Id == ConversationOrchestrator.CancelAndRedoChoice);
    await redo.ChooseAsync(ConversationOrchestrator.CancelAndRedoChoice);
    await redoRun.WaitAsync(TimeSpan.FromSeconds(10));
    Check(offeredRedo && redo.Stage == ConversationStage.Proposed && redo.Kind == StudioProjectKind.WindowsApp && redoPlanner.CallCount == 2 &&
          redo.Request.Contains("Windowsアプリ（EXE）", StringComparison.Ordinal) && redo.ProjectPath is null && redoScaffolder.Completed == 0,
        "type-change-during-run-cancels-and-replans");

    // 3-4: images in the chat (PNG/JPEG by content, 5 MB, 4 per message), kept with the message and moved with the record.
    var imageRoot = Path.Combine(conversationRoot, "images");
    Directory.CreateDirectory(imageRoot);
    var pngPath = Path.Combine(imageRoot, "screen.png");
    await File.WriteAllBytesAsync(pngPath, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4]);
    var jpegNamedPng = Path.Combine(imageRoot, "photo.png");
    await File.WriteAllBytesAsync(jpegNamedPng, [0xFF, 0xD8, 0xFF, 0xE0, 5, 6, 7]);
    var textNamedPng = Path.Combine(imageRoot, "fake.png");
    await File.WriteAllTextAsync(textNamedPng, "not an image");
    Check(ChatAttachmentPolicy.Check(pngPath, 0) is null && ChatAttachmentPolicy.Check(jpegNamedPng, 0) is null &&
          ChatAttachmentPolicy.Check(textNamedPng, 0) is not null && ChatAttachmentPolicy.Check(pngPath, ChatAttachmentPolicy.MaxPerMessage) is not null,
        "chat-images-are-checked-by-content-and-count");
    var imageChat = new ConversationOrchestrator(new RecordingPlanner(unsupportedBlueprint), new ScriptedClassifier(OutputKindJudgement.Unknown),
        new ProbeScaffolder(conversationPaths.ProjectRoot), ConversationLog.CreateSession(conversationPaths), () => LocalStudioSettings.DefaultModel);
    var storedImage = await ChatAttachmentPolicy.StoreAsync(pngPath, imageChat.Log.Folder, 0);
    await imageChat.SubmitAsync("この画面のようなものを作りたい", [storedImage]);
    var sessionFolder = imageChat.Log.Folder;
    var imageProject = Path.Combine(conversationPaths.ProjectRoot, "image-project");
    Directory.CreateDirectory(imageProject);
    await imageChat.Log.MoveIntoProjectAsync(imageProject);
    var reopened = ConversationLog.OpenForProject(imageProject);
    Check(storedImage.MediaType == "image/png" && storedImage.RelativePath.StartsWith("attachments/", StringComparison.Ordinal) &&
          reopened.Messages[0].Attachments.Count == 1 &&
          File.Exists(Path.Combine(reopened.Folder, storedImage.RelativePath.Replace('/', Path.DirectorySeparatorChar))) &&
          !Directory.Exists(sessionFolder) && File.Exists(pngPath),
        "chat-images-stay-with-their-message-and-move-into-the-project");

    // 3-5: two-step image handling. The vision model reads the screenshot after the code model is released, unloads
    // right after answering, and its description reaches the request as reference data.
    var visionRuntime = new FakeVisionRuntime(new Uri("http://127.0.0.1:54321/"));
    var visionResponses = new CapturingJsonHandler(JsonSerializer.Serialize(new { message = new { content = "・見出し「メモ帳」\n・追加ボタンが入力欄に重なっている" } }));
    using (var describer = new LocalImageDescriber(visionRuntime, new HttpClient(visionResponses)))
    {
        var visionChat = new ConversationOrchestrator(new RecordingPlanner(unsupportedBlueprint), new ScriptedClassifier(new(StudioProjectKind.DesktopOrWebApp, 0.9, "")),
            new ProbeScaffolder(conversationPaths.ProjectRoot), ConversationLog.CreateSession(conversationPaths), () => LocalStudioSettings.DefaultModel, imageDescriber: describer);
        var shot = await ChatAttachmentPolicy.StoreAsync(pngPath, visionChat.Log.Folder, 0);
        await visionChat.SubmitAsync("ブラウザーで使うメモ帳で、この画面の崩れを直したい", [shot]);
        using var visionRequest = JsonDocument.Parse(visionResponses.Bodies[0]);
        var root = visionRequest.RootElement;
        Check(root.GetProperty("model").GetString() == LocalStudioSettings.VisionModel && root.GetProperty("keep_alive").GetInt32() == 0 &&
              root.GetProperty("think").GetBoolean() == false &&
              root.GetProperty("messages")[0].GetProperty("role").GetString() == "system" &&
              root.GetProperty("messages")[1].GetProperty("images")[0].GetString()!.StartsWith("iVBORw0KGgo", StringComparison.Ordinal) &&
              visionRuntime.Unloads == 1 &&
              visionChat.Messages.Any(message => message.Text.StartsWith("画像から読み取った内容", StringComparison.Ordinal)) &&
              visionChat.Request.Contains("[添付画像から読み取った内容（参考データ）]", StringComparison.Ordinal) &&
              visionChat.Request.Contains("重なっている", StringComparison.Ordinal),
            "screenshot-is-described-by-the-vision-model-alone-and-added-as-reference-data");
    }
    // Shapes of real qwen3.5:2b answers (2026-10-10): echoed rules, markdown, invented colour codes, a suggestions section.
    var cleaned = LocalImageDescriber.Clean(
        "ご依頼の内容を説明します。\n\n---\n\n- 見出し・ボタン・入力欄・一覧・表などの要素と、その並び\n**左サイドバー**\n* **見出し**：「Local Studio」\n" +
        "*   **背景色**：淡いベージュ色（#F5F5F0）\n1.  **送信ボタン**：「送信」\n\n---\n\n**目立たせるための提案（推測のみ）**\n* 送信ボタンを青にする");
    Check(cleaned.Contains("「Local Studio」", StringComparison.Ordinal) && cleaned.Contains("「送信」", StringComparison.Ordinal) &&
          !cleaned.Contains("#F5F5F0", StringComparison.Ordinal) && !cleaned.Contains("提案", StringComparison.Ordinal) &&
          !cleaned.Contains("青にする", StringComparison.Ordinal) && !cleaned.Contains("見出し・ボタン・入力欄", StringComparison.Ordinal) &&
          !cleaned.Contains("**", StringComparison.Ordinal) && !cleaned.Contains("ご依頼", StringComparison.Ordinal),
        "vision-answer-keeps-what-was-read-and-drops-echoes-guesses-and-suggestions:" + cleaned.Replace("\n", " | "));
    await using (var noVisionRuntime = new FakeLocalModelRuntime(new Uri("http://127.0.0.1:54321/")))
    using (var noVision = new LocalImageDescriber(noVisionRuntime, new HttpClient(new SequenceJsonHandler())))
    {
        var wordsChat = new ConversationOrchestrator(new RecordingPlanner(unsupportedBlueprint), new ScriptedClassifier(new(StudioProjectKind.DesktopOrWebApp, 0.9, "")),
            new ProbeScaffolder(conversationPaths.ProjectRoot), ConversationLog.CreateSession(conversationPaths), () => LocalStudioSettings.DefaultModel, imageDescriber: noVision);
        var shot = await ChatAttachmentPolicy.StoreAsync(pngPath, wordsChat.Log.Folder, 0);
        await wordsChat.SubmitAsync("ブラウザーで使うメモ帳で、この画面の崩れを直したい", [shot]);
        Check(wordsChat.Messages.Any(message => message.Text.Contains("文章でも教えてください", StringComparison.Ordinal)) &&
              wordsChat.Stage == ConversationStage.Proposed,
            "without-a-vision-model-the-chat-asks-for-words-and-continues");
    }

    // 4-1: bulk rename. The AI only proposes a plan; LocalStudio previews, refuses unsafe names, renames in two
    // phases, checks the folder itself afterwards and undoes in one step.
    var renameFolder = Path.Combine(Path.GetTempPath(), "ls-rename-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(renameFolder);
    foreach (var name in new[] { "IMG_0010.JPG", "IMG_0002.JPG", "IMG_0001.JPG", "memo.txt", "Readme.TXT" })
        await File.WriteAllTextAsync(Path.Combine(renameFolder, name), name);
    var journalRoot = Path.Combine(conversationPaths.StateRoot, "work-journals");
    Check(BulkRenamer.CheckFolder(renameFolder) is null &&
          BulkRenamer.CheckFolder(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) is not null &&
          BulkRenamer.CheckFolder(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)) is not null &&
          BulkRenamer.CheckFolder(Path.GetPathRoot(renameFolder)!) is not null,
        "rename-folder-must-be-a-chosen-user-folder:" + BulkRenamer.CheckFolder(renameFolder));
    var photoPlan = new RenamePlan("jpg", null, "name", [new RenameStep("replace", Find: "IMG_", With: "photo-"), new RenameStep("case", To: "lower"),
        new RenameStep("number", Start: 1, Digits: 3, Position: "suffix", Text: "_")]);
    var photoPreview = BulkRenamer.Preview(renameFolder, photoPlan);
    var applied = await BulkRenamer.ApplyAsync(photoPreview, journalRoot);
    var afterApply = Directory.EnumerateFiles(renameFolder).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
    var undone = await BulkRenamer.UndoAsync(journalRoot, applied.Journal.Id);
    var afterUndo = Directory.EnumerateFiles(renameFolder).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
    Check(photoPreview.Changing == 3 && photoPreview.Problems == 0 &&
          photoPreview.Items.Select(item => item.NewName).SequenceEqual(["photo-0001_001.JPG", "photo-0002_002.JPG", "photo-0010_003.JPG"]) &&
          applied.Succeeded && afterApply.SequenceEqual(["Readme.TXT", "memo.txt", "photo-0001_001.JPG", "photo-0002_002.JPG", "photo-0010_003.JPG"]) &&
          undone.Succeeded && afterUndo.SequenceEqual(["IMG_0001.JPG", "IMG_0002.JPG", "IMG_0010.JPG", "Readme.TXT", "memo.txt"]),
        "rename-previews-applies-verifies-and-undoes:" + string.Join(",", afterApply!));
    await File.WriteAllTextAsync(Path.Combine(renameFolder, "0001.JPG"), "existing");
    var clash = BulkRenamer.Preview(renameFolder, new RenamePlan("jpg", null, null, [new RenameStep("remove", Find: "IMG_")]));
    var badChar = BulkRenamer.Preview(renameFolder, new RenamePlan("txt", null, null, [new RenameStep("suffix", Text: ":v2")]));
    var caseOnly = BulkRenamer.Preview(renameFolder, new RenamePlan("txt", "Readme", null, [new RenameStep("case", To: "lower")]));
    var caseApplied = await BulkRenamer.ApplyAsync(caseOnly, journalRoot);
    Check(!clash.CanApply && clash.Items.Any(item => item.OldName == "IMG_0001.JPG" && item.Status == RenameStatus.Conflict) &&
          !badChar.CanApply && badChar.Items.All(item => item.Status == RenameStatus.Invalid) &&
          caseApplied.Succeeded && Directory.EnumerateFiles(renameFolder).Select(Path.GetFileName).Contains("readme.TXT") &&
          BulkRenamer.Validate(new RenamePlan(null, null, null, [new RenameStep("delete")])).Count > 0,
        "rename-refuses-clashes-and-bad-names-and-handles-case-only-changes");
    File.Move(Path.Combine(renameFolder, "0001.JPG"), Path.Combine(renameFolder, "existing-0001.txt"));

    // The plans the 1.5B model actually returned in the first drill (2026-10-10): valid in form, not what was asked.
    var literalWordPlan = new RenamePlan(null, null, null, [new RenameStep("prefix", Text: "更新日_"), new RenameStep("number", Start: 1, Digits: 3, Position: "prefix")]);
    var quoteMarkPlan = new RenamePlan(null, null, null, [new RenameStep("replace", Find: "「", With: ""), new RenameStep("replace", Find: "」", With: "")]);
    var datedRequest = "jpgの写真の名前を「更新日_3桁の連番」にして";
    var finalRequest = "ファイル名についている「 final」を消して";
    var datedProblems = RenameRequestRules.Mismatches(datedRequest, literalWordPlan);
    Check(datedProblems.Any(problem => problem.StartsWith("rename.request-date-missing", StringComparison.Ordinal)) &&
          datedProblems.Any(problem => problem.StartsWith("rename.request-extension-missing", StringComparison.Ordinal)) &&
          datedProblems.Any(problem => problem.StartsWith("rename.request-word-copied:更新日", StringComparison.Ordinal)) &&
          RenameRequestRules.Mismatches(finalRequest, quoteMarkPlan).Any(problem => problem.StartsWith("rename.request-text-unused: final", StringComparison.Ordinal)) &&
          RenameRequestRules.Mismatches(datedRequest, new RenamePlan("jpg", null, "modified", [new RenameStep("clear"),
              new RenameStep("date", Format: "yyyyMMdd", Position: "prefix", Text: "_"), new RenameStep("number", Start: 1, Digits: 3, Position: "suffix", Text: "_")])).Count == 0,
        "rename-plans-are-checked-against-the-request-not-only-their-form:" + string.Join("|", datedProblems));
    // Drill 2 (2026-10-10): "名前を…連番にして" kept the old name; the whole name was asked for.
    var keptOldName = new RenamePlan(null, null, null, [new RenameStep("date", Format: "yyyyMMdd", Position: "prefix"), new RenameStep("number", Start: 1, Digits: 3, Position: "suffix")]);
    Check(RenameRequestRules.Mismatches("写真の名前を撮影日（更新日）と3桁の連番にして", keptOldName).Any(problem => problem.StartsWith("rename.request-whole-name", StringComparison.Ordinal)) &&
          !RenameRequestRules.Mismatches("写真の名前の先頭に更新日を付けて", new RenamePlan(null, null, null, [new RenameStep("date", Position: "prefix")])).Any(problem => problem.StartsWith("rename.request-whole-name", StringComparison.Ordinal)),
        "rename-whole-name-requests-must-drop-the-old-name");
    Check(RenameRequestRules.Normalize(new RenamePlan(null, null, null, [new RenameStep("date"), new RenameStep("number"), new RenameStep("clear")])).Steps.Select(step => step.Op)
              .SequenceEqual(["clear", "date", "number"]),
        "rename-clear-is-moved-to-the-front");
    Check(RenameRequestRules.TryRules(finalRequest) is { Steps: [{ Op: "remove", Find: " final" }] } &&
          RenameRequestRules.TryRules("全部のファイルの先頭に「2026-10_」を付けて") is { Steps: [{ Op: "prefix", Text: "2026-10_" }] } &&
          RenameRequestRules.TryRules("pdfの「案」を「確定」に変えて") is { Extension: "pdf", Steps: [{ Op: "replace", Find: "案", With: "確定" }] } &&
          RenameRequestRules.TryRules(datedRequest) is null,
        "common-rename-requests-are-planned-by-rules");
    var clearedFolder = Path.Combine(Path.GetTempPath(), "ls-rename-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(clearedFolder);
    await File.WriteAllTextAsync(Path.Combine(clearedFolder, "IMG_9.jpg"), "x");
    File.SetLastWriteTime(Path.Combine(clearedFolder, "IMG_9.jpg"), new DateTime(2026, 10, 2, 9, 0, 0));
    Check(BulkRenamer.Preview(clearedFolder, new RenamePlan("jpg", null, "modified", [new RenameStep("clear"),
              new RenameStep("date", Format: "yyyyMMdd", Position: "prefix", Text: "_"), new RenameStep("number", Start: 1, Digits: 3, Position: "suffix", Text: "_")]))
          .Items.Single().NewName == "20261002_001.jpg",
        "rename-clear-builds-a-name-from-date-and-number");

    // In the chat: preview first, nothing changes until the button, and the result offers undo.
    var scriptedRename = new ScriptedRenamePlanner(new RenamePlan("jpg", null, "name", [new RenameStep("prefix", Text: "2026_")]));
    var workChat = new ConversationOrchestrator(new RecordingPlanner(unsupportedBlueprint), new ScriptedClassifier(OutputKindJudgement.Unknown),
        new ProbeScaffolder(conversationPaths.ProjectRoot), ConversationLog.CreateSession(conversationPaths), () => LocalStudioSettings.DefaultModel)
    { Rename = new RenameAssistant(scriptedRename, journalRoot) };
    await workChat.SubmitWorkAsync("写真の先頭に2026_を付けて", renameFolder);
    var namesBeforeButton = Directory.EnumerateFiles(renameFolder).Select(Path.GetFileName).ToArray();
    var applyChoice = workChat.Messages[^1].Choices.First(choice => choice.Id.StartsWith(ConversationOrchestrator.RenameApplyPrefix, StringComparison.Ordinal));
    await workChat.ChooseAsync(applyChoice.Id);
    var renamedInChat = File.Exists(Path.Combine(renameFolder, "2026_IMG_0001.JPG"));
    var undoChoice = workChat.Messages[^1].Choices.Single(choice => choice.Id.StartsWith(ConversationOrchestrator.RenameUndoPrefix, StringComparison.Ordinal));
    await workChat.ChooseAsync(undoChoice.Id);
    Check(!namesBeforeButton.Any(name => name!.StartsWith("2026_", StringComparison.Ordinal)) && renamedInChat &&
          workChat.Messages[^2].Text.Contains("確認しました", StringComparison.Ordinal) &&
          File.Exists(Path.Combine(renameFolder, "IMG_0001.JPG")) && workChat.Stage == ConversationStage.Intake,
        "rename-in-chat-waits-for-the-button-and-offers-undo:" + string.Join(" | ", workChat.Messages.Skip(Math.Max(0, workChat.Messages.Count - 3)).Select(message => message.Text.Split('\n')[0])));

    // 4-2: Word / Excel with the bundled Open XML SDK. Tools read and compute; edits become a new file that is read back.
    var officeFolder = Path.Combine(Path.GetTempPath(), "ls-office-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(officeFolder);
    var docPath = Path.Combine(officeFolder, "見積書.docx");
    using (var word = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Create(docPath, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
    {
        var main = word.AddMainDocumentPart();
        main.Document = new DocumentFormat.OpenXml.Wordprocessing.Document(new DocumentFormat.OpenXml.Wordprocessing.Body(
            new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                new DocumentFormat.OpenXml.Wordprocessing.ParagraphProperties(new DocumentFormat.OpenXml.Wordprocessing.ParagraphStyleId { Val = "Heading1" }),
                new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text("お見積り"))),
            // "見積案" split across two runs, as Word often stores edited text.
            new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text("この見積")),
                new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text("案は10月末まで有効です。"))),
            new DocumentFormat.OpenXml.Wordprocessing.Paragraph(new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text("担当: 営業部")))));
    }
    var xlsxPath = Path.Combine(officeFolder, "売上.xlsx");
    using (var excel = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Create(xlsxPath, DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook))
    {
        var book = excel.AddWorkbookPart();
        book.Workbook = new DocumentFormat.OpenXml.Spreadsheet.Workbook();
        var sheetPart = book.AddNewPart<DocumentFormat.OpenXml.Packaging.WorksheetPart>();
        DocumentFormat.OpenXml.Spreadsheet.Cell Text(string reference, string value) => new() { CellReference = reference, DataType = DocumentFormat.OpenXml.Spreadsheet.CellValues.InlineString, InlineString = new DocumentFormat.OpenXml.Spreadsheet.InlineString(new DocumentFormat.OpenXml.Spreadsheet.Text(value)) };
        DocumentFormat.OpenXml.Spreadsheet.Cell Number(string reference, string value) => new() { CellReference = reference, CellValue = new DocumentFormat.OpenXml.Spreadsheet.CellValue(value) };
        sheetPart.Worksheet = new DocumentFormat.OpenXml.Spreadsheet.Worksheet(new DocumentFormat.OpenXml.Spreadsheet.SheetData(
            new DocumentFormat.OpenXml.Spreadsheet.Row(Text("A1", "品名"), Text("B1", "数量"), Text("C1", "単価"), Text("D1", "金額")) { RowIndex = 1 },
            new DocumentFormat.OpenXml.Spreadsheet.Row(Text("A2", "りんご"), Number("B2", "3"), Number("C2", "150"),
                new DocumentFormat.OpenXml.Spreadsheet.Cell { CellReference = "D2", CellFormula = new DocumentFormat.OpenXml.Spreadsheet.CellFormula("B2*C2"), CellValue = new DocumentFormat.OpenXml.Spreadsheet.CellValue("450") }) { RowIndex = 2 },
            new DocumentFormat.OpenXml.Spreadsheet.Row(Text("A3", "みかん"), Number("B3", "5"), Number("C3", "80"), Number("D3", "400")) { RowIndex = 3 }));
        book.Workbook.AppendChild(new DocumentFormat.OpenXml.Spreadsheet.Sheets(new DocumentFormat.OpenXml.Spreadsheet.Sheet { Id = book.GetIdOfPart(sheetPart), SheetId = 1, Name = "売上" }));
    }
    var docSummary = OfficeDocumentReader.Read(docPath);
    var xlsxSummary = OfficeDocumentReader.Read(xlsxPath);
    var quantity = xlsxSummary.Sheets[0].Numeric.Single(stats => stats.Header == "数量");
    var macroPath = Path.Combine(officeFolder, "マクロ.xlsm");
    File.Copy(xlsxPath, macroPath);
    var notOffice = Path.Combine(officeFolder, "壊れた.docx");
    await File.WriteAllTextAsync(notOffice, "not a zip");
    Check(docSummary.Headings.SequenceEqual(["お見積り"]) && docSummary.Paragraphs.Contains("この見積案は10月末まで有効です。") &&
          quantity is { Numbers: 2, Sum: 8 } && xlsxSummary.ToFacts().Contains("合計 8", StringComparison.Ordinal) &&
          OfficeDocumentReader.Check(macroPath) is not null && OfficeDocumentReader.Check(notOffice) is not null,
        "office-files-are-read-with-computed-facts-and-unsafe-files-are-refused");

    // The Excel helper given to generated EXEs (WindowsToolbox): reads workbooks as Excel writes them (shared strings,
    // phonetic guides, gaps, formulas) and writes ones that read back the same.
    var surveyPath = Path.Combine(officeFolder, "第一小.xlsx");
    using (var survey = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Create(surveyPath, DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook))
    {
        var book = survey.AddWorkbookPart();
        book.Workbook = new DocumentFormat.OpenXml.Spreadsheet.Workbook();
        var strings = book.AddNewPart<DocumentFormat.OpenXml.Packaging.SharedStringTablePart>();
        strings.SharedStringTable = new DocumentFormat.OpenXml.Spreadsheet.SharedStringTable(
            new DocumentFormat.OpenXml.Spreadsheet.SharedStringItem(new DocumentFormat.OpenXml.Spreadsheet.Text("学年")),
            new DocumentFormat.OpenXml.Spreadsheet.SharedStringItem(new DocumentFormat.OpenXml.Spreadsheet.Text("満足")),
            new DocumentFormat.OpenXml.Spreadsheet.SharedStringItem(
                new DocumentFormat.OpenXml.Spreadsheet.Run(new DocumentFormat.OpenXml.Spreadsheet.Text("とても")),
                new DocumentFormat.OpenXml.Spreadsheet.Run(new DocumentFormat.OpenXml.Spreadsheet.Text("良い")),
                new DocumentFormat.OpenXml.Spreadsheet.PhoneticRun(new DocumentFormat.OpenXml.Spreadsheet.Text("ヨイ")) { BaseTextStartIndex = 3, EndingBaseIndex = 5 }));
        var other = book.AddNewPart<DocumentFormat.OpenXml.Packaging.WorksheetPart>();
        other.Worksheet = new DocumentFormat.OpenXml.Spreadsheet.Worksheet(new DocumentFormat.OpenXml.Spreadsheet.SheetData());
        var answers = book.AddNewPart<DocumentFormat.OpenXml.Packaging.WorksheetPart>();
        DocumentFormat.OpenXml.Spreadsheet.Cell Shared(string reference, int index) => new() { CellReference = reference, DataType = DocumentFormat.OpenXml.Spreadsheet.CellValues.SharedString, CellValue = new DocumentFormat.OpenXml.Spreadsheet.CellValue(index.ToString(System.Globalization.CultureInfo.InvariantCulture)) };
        answers.Worksheet = new DocumentFormat.OpenXml.Spreadsheet.Worksheet(new DocumentFormat.OpenXml.Spreadsheet.SheetData(
            new DocumentFormat.OpenXml.Spreadsheet.Row(Shared("A1", 0), Shared("C1", 1)) { RowIndex = 1 },
            new DocumentFormat.OpenXml.Spreadsheet.Row(new DocumentFormat.OpenXml.Spreadsheet.Cell { CellReference = "A3", CellValue = new DocumentFormat.OpenXml.Spreadsheet.CellValue("4") }, Shared("C3", 2)) { RowIndex = 3 }));
        book.Workbook.AppendChild(new DocumentFormat.OpenXml.Spreadsheet.Sheets(
            new DocumentFormat.OpenXml.Spreadsheet.Sheet { Id = book.GetIdOfPart(other), SheetId = 1, Name = "説明" },
            new DocumentFormat.OpenXml.Spreadsheet.Sheet { Id = book.GetIdOfPart(answers), SheetId = 2, Name = "回答" }));
    }
    var surveyRows = XlsxLite.ReadSheet(surveyPath, "回答");
    var salesRows = XlsxLite.ReadSheet(xlsxPath);
    var mergedPath = Path.Combine(officeFolder, "まとめ.xlsx");
    XlsxLite.WriteSheet(mergedPath, "集計:全校", [["学校名", "学年", "回答"], ["第一小", "4", "とても良い"], ["第二小", "007", ""]]);
    var mergedRows = XlsxLite.ReadSheet(mergedPath);
    var mergedFacts = OfficeDocumentReader.Read(mergedPath);
    Check(XlsxLite.SheetNames(surveyPath).SequenceEqual(["説明", "回答"]) &&
          surveyRows.Count == 3 && surveyRows[0].SequenceEqual(["学年", "", "満足"]) && surveyRows[1].Length == 0 && surveyRows[2].SequenceEqual(["4", "", "とても良い"]) &&
          salesRows[1].SequenceEqual(["りんご", "3", "150", "450"]) &&
          XlsxLite.SheetNames(mergedPath).SequenceEqual(["集計全校"]) && mergedRows[2].SequenceEqual(["第二小", "007"]) && mergedRows[1][1] == "4" &&
          mergedFacts.Sheets[0].Numeric.Any(stats => stats.Header == "学年") &&
          WindowsToolbox.For("50個のエクセルファイルを1つにまとめる").Any(tool => tool.FileName == "XlsxLite.cs") && WindowsToolbox.For("タイマー").Count == 0 &&
          WindowsToolbox.IsToolboxFile("XlsxLite.cs", WindowsToolbox.Source(WindowsToolbox.All[0])) && !WindowsToolbox.IsToolboxFile("XlsxLite.cs", "class X {}"),
        "excel-helper-reads-and-writes-workbooks-for-generated-apps");
    // An app that calls the helper builds with the Windows built-in C# 5 compiler, and the helper itself is not judged
    // as app code (its schema URLs and file streams belong to the reviewed helper).
    var toolboxApp = Path.Combine(officeFolder, "toolbox-app");
    Directory.CreateDirectory(toolboxApp);
    await File.WriteAllTextAsync(Path.Combine(toolboxApp, "Program.cs"), """
        using System;
        using System.Collections.Generic;
        using System.Windows.Forms;
        public class MergeForm : Form
        {
            private Button merge = new Button();
            public MergeForm()
            {
                merge.Text = "まとめる";
                merge.Click += OnMerge;
                Controls.Add(merge);
            }
            private void OnMerge(object sender, EventArgs e)
            {
                var folder = new FolderBrowserDialog();
                if (folder.ShowDialog() != DialogResult.OK) return;
                var rows = new List<string[]>();
                foreach (var path in System.IO.Directory.GetFiles(folder.SelectedPath, "*.xlsx"))
                    rows.AddRange(XlsxLite.ReadSheet(path));
                var save = new SaveFileDialog();
                if (save.ShowDialog() == DialogResult.OK) XlsxLite.WriteSheet(save.FileName, "まとめ", rows);
            }
            [STAThread]
            static void Main() { Application.EnableVisualStyles(); Application.Run(new MergeForm()); }
        }
        """);
    await File.WriteAllTextAsync(Path.Combine(toolboxApp, "app.manifest"), WindowsApplicationManifest.Content);
    foreach (var helper in WindowsToolbox.Files(WindowsToolbox.For("エクセル")))
        await File.WriteAllTextAsync(Path.Combine(toolboxApp, helper.RelativePath), helper.Content);
    string? toolboxBuild = null;
    try { await new WindowsExecutableCompiler().CompileAsync(toolboxApp); }
    catch (Exception ex) when (ex is InvalidDataException or WindowsBuildEnvironmentException) { toolboxBuild = ex.Message; }
    Check(toolboxBuild is null && !WindowsToolbox.AppSources(toolboxApp).Contains("XlsxLite.ReadSheet(string path)", StringComparison.Ordinal) &&
          WindowsToolbox.AppSources(toolboxApp).Contains("class MergeForm", StringComparison.Ordinal) &&
          LocalWindowsAppGenerationEngine.ValidateSource(WindowsToolbox.AppSources(toolboxApp)).Count == 0,
        "generated-app-builds-with-the-excel-helper-using-the-windows-compiler:" + toolboxBuild);

    var docHashBefore = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(docPath)));
    var docPreview = OfficeEditor.Preview(docPath, [new OfficeEdit("replace", Find: "見積案", With: "確定版")]);
    var docResult = OfficeEditor.Apply(docPreview);
    var docAfter = OfficeDocumentReader.Read(docResult.OutputPath);
    Check(docPreview.CanApply && docResult.Succeeded && docResult.OutputPath.EndsWith("見積書_LS編集.docx", StringComparison.Ordinal) &&
          docAfter.Paragraphs.Contains("この確定版は10月末まで有効です。") &&
          Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(docPath))) == docHashBefore,
        "word-replace-across-runs-makes-a-checked-new-file-and-keeps-the-original:" + string.Join("|", docResult.Mismatches));
    var formulaPreview = OfficeEditor.Preview(xlsxPath, [new OfficeEdit("set-cell", Sheet: "売上", Cell: "D2", Value: "999")]);
    var xlsxPreview = OfficeEditor.Preview(xlsxPath, [new OfficeEdit("set-cell", Sheet: "売上", Cell: "B2", Value: "10"), new OfficeEdit("append-row", Sheet: "売上", Values: ["ぶどう", "2", "300"])]);
    var xlsxResult = OfficeEditor.Apply(xlsxPreview);
    var xlsxAfter = OfficeDocumentReader.Read(xlsxResult.OutputPath);
    Check(!formulaPreview.CanApply && formulaPreview.Changes.Single().Status == "formula" &&
          xlsxResult.Succeeded && xlsxAfter.Sheets[0].Rows == 4 && xlsxAfter.Sheets[0].Numeric.Single(stats => stats.Header == "数量").Sum == 17,
        "excel-cells-and-rows-are-edited-in-a-new-file-but-formula-cells-are-protected:" + string.Join("|", xlsxResult.Mismatches));

    // The two confident mistakes of the real drill (2026-10-11): a wrong row named as the largest, and a cell edit on another row.
    var salesFacts = xlsxSummary.ToFacts();
    var wrongRowEdit = OfficeAssistant.EditMismatches("みかんの単価を90に変えて", [new OfficeEdit("set-cell", Sheet: "売上", Cell: "C2", Value: "90")], xlsxSummary);
    Check(salesFacts.Contains("最大 450（りんご）", StringComparison.Ordinal) && salesFacts.Contains("行3 | A:みかん", StringComparison.Ordinal) &&
          OfficeAssistant.SuperlativeMismatches("金額が一番大きいのはどの品名？", "みかん", xlsxSummary).Count == 1 &&
          OfficeAssistant.SuperlativeMismatches("金額が一番大きいのはどの品名？", "りんごです", xlsxSummary).Count == 0 &&
          wrongRowEdit.Any(problem => problem.Contains("みかんは行3", StringComparison.Ordinal)) &&
          OfficeAssistant.EditMismatches("みかんの単価を90に変えて", [new OfficeEdit("set-cell", Sheet: "売上", Cell: "C3", Value: "90")], xlsxSummary).Count == 0 &&
          OfficeAssistant.EditMismatches("C2を90にして", [new OfficeEdit("set-cell", Sheet: "売上", Cell: "C2", Value: "90")], xlsxSummary).Count == 0 &&
          OfficeEditor.Preview(xlsxPath, [new OfficeEdit("set-cell", Sheet: "売上", Cell: "C3", Value: "90")]).Changes.Single().Description.Contains("C3（みかん の 単価）", StringComparison.Ordinal),
        "office-checks-catch-a-wrong-largest-row-and-an-edit-on-another-row:" + string.Join("|", wrongRowEdit));
    var wrongRowPlanner = new ScriptedOfficePlanner(new OfficePlan("edit", null, [new OfficeEdit("set-cell", Sheet: "売上", Cell: "C2", Value: "90")]));
    var refusedEdit = await new OfficeAssistant(wrongRowPlanner).HandleAsync("みかんの単価を90に変えて", xlsxPath);
    // Totals and largest/smallest are computed by the tool; the model is not asked at all.
    var largestPlanner = new ScriptedOfficePlanner(new OfficePlan("answer", "みかん", null));
    var largest = await new OfficeAssistant(largestPlanner).HandleAsync("金額が一番大きいのはどの品名？", xlsxPath);
    // A question answered with an edit (drill: 「単価が一番安いのは？」 became "set C2 to 80") is never offered as an edit.
    var editForQuestion = new ScriptedOfficePlanner(new OfficePlan("edit", null, [new OfficeEdit("set-cell", Sheet: "売上", Cell: "C2", Value: "80")]));
    var questionReply = await new OfficeAssistant(editForQuestion).HandleAsync("この表について教えて", xlsxPath);
    Check(refusedEdit.Preview is null && refusedEdit.Text.Contains("ファイルは作りません", StringComparison.Ordinal) && wrongRowPlanner.Calls == 2 &&
          largestPlanner.Calls == 0 && largest.Text.Contains("りんご", StringComparison.Ordinal) && largest.Text.Contains("450", StringComparison.Ordinal) &&
          OfficeAssistant.IntentOf("単価が一番安いのは？") == "question" && OfficeAssistant.IntentOf("みかんの単価を90に変えて") == "edit" &&
          questionReply.Preview is null && editForQuestion.Calls == 2,
        "office-wrong-place-edits-and-edits-for-questions-are-refused-and-superlatives-are-computed");
    Check(OfficeAssistant.UnsupportedNumbers("合計は 1,250 円、平均は 999 円です。", "合計 1,250") is ["999"] &&
          OfficeAssistant.UnsupportedNumbers("数量の合計は 8 個です。", xlsxSummary.ToFacts()).Count == 0,
        "office-answers-may-only-use-numbers-found-in-the-file");
    var inventedAnswer = new ScriptedOfficePlanner(new OfficePlan("answer", "数量の合計は 12 個です。", null));
    var answerReply = await new OfficeAssistant(inventedAnswer).HandleAsync("数量について教えて", xlsxPath);
    Check(inventedAnswer.Calls == 2 && answerReply.Text.Contains("見つかりませんでした", StringComparison.Ordinal) && answerReply.Text.Contains("12", StringComparison.Ordinal),
        "an-invented-number-is-retried-once-then-flagged-to-the-user");
    var officeChat = new ConversationOrchestrator(new RecordingPlanner(unsupportedBlueprint), new ScriptedClassifier(OutputKindJudgement.Unknown),
        new ProbeScaffolder(conversationPaths.ProjectRoot), ConversationLog.CreateSession(conversationPaths), () => LocalStudioSettings.DefaultModel)
    { Office = new OfficeAssistant(new ScriptedOfficePlanner(new OfficePlan("edit", null, [new OfficeEdit("replace", Find: "営業部", With: "営業一課")]))) };
    // Hands-on 2026-10-13 (the user's design): the edit is made on the chat's working copy and read back; nothing is
    // written next to the original until "保存する", which saves "name_edited.ext".
    var officeFilesBefore = Directory.GetFiles(officeFolder).Length;
    await officeChat.SubmitDocumentAsync("担当を営業一課に変えて", docPath);
    var officeEdited = officeChat.Messages[^1];
    var officeSave = officeEdited.Choices.First(choice => choice.Id.StartsWith(ConversationOrchestrator.WorkSavePrefix, StringComparison.Ordinal));
    var officeUntouched = Directory.GetFiles(officeFolder).Length == officeFilesBefore;
    await officeChat.ChooseAsync(officeSave.Id);
    var officeSaved = Path.Combine(officeFolder, "見積書_edited.docx");
    Check(officeEdited.Text.Contains("読み直して予定どおり", StringComparison.Ordinal) && officeEdited.Text.Contains("営業一課", StringComparison.Ordinal) && officeUntouched &&
          File.Exists(officeSaved) && OfficeDocumentReader.Read(officeSaved).ToFacts().Contains("営業一課", StringComparison.Ordinal) &&
          Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(docPath))) == docHashBefore,
        "office-edits-go-to-a-working-copy-and-are-saved-as-edited-only-on-request:" + officeEdited.Text.Split('\n')[0]);
    // Drill 2026-10-13: the rows of an unquoted request are found by the longest part of it in the workbook, and
    // "何人含まれている？" is counted by the tool with the rows as evidence.
    var unquotedRule = OfficeRequestRules.TryRules("りんごに関係する部分を黄色で塗りつぶし、文字を太文字にして赤く文字色も変更して", xlsxSummary, xlsxPath);
    var countAnswer = OfficeAssistant.CountAnswer("みかんは何件含まれている？", xlsxSummary, xlsxPath);
    Check(unquotedRule is [{ Op: "format-rows", Contains: "りんご", Fill: "黄色", Bold: true, Color: "赤" }] &&
          countAnswer is { } counted && counted.Contains("1 行", StringComparison.Ordinal) && counted.Contains("「みかん」", StringComparison.Ordinal),
        "excel-rows-by-the-requests-own-words-and-counts-by-the-tool:" + countAnswer);
    // Hands-on 2026-10-13 (H5): fill, bold, text colour and new sheets; a cell keeps the format it already had.
    var formatRule = OfficeRequestRules.TryRules("「りんご」を含む行を黄色で塗りつぶして、太字にして赤い文字にして", xlsxSummary);
    var sheetRule = OfficeRequestRules.TryRules("「テスト追加」というシートを追加して", xlsxSummary);
    var firstFormat = OfficeEditor.Apply(OfficeEditor.Preview(xlsxPath, [new OfficeEdit("format-cells", Sheet: "売上", Range: "C2", Fill: "水色")]));
    var formatPreview = OfficeEditor.Preview(firstFormat.OutputPath, [.. formatRule!, .. sheetRule!]);
    var formatted = OfficeEditor.Apply(formatPreview);
    string[] priceStyle;
    using (var book = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Open(formatted.OutputPath, false))
    {
        var styles = book.WorkbookPart!.WorkbookStylesPart!.Stylesheet!;
        var priceCell = book.WorkbookPart.WorksheetParts.First().Worksheet!.Descendants<DocumentFormat.OpenXml.Spreadsheet.Cell>().First(cell => cell.CellReference == "C2");
        var xf = styles.CellFormats!.Elements<DocumentFormat.OpenXml.Spreadsheet.CellFormat>().ElementAt((int)priceCell.StyleIndex!.Value);
        var font = styles.Fonts!.Elements<DocumentFormat.OpenXml.Spreadsheet.Font>().ElementAt((int)xf.FontId!.Value);
        priceStyle = [styles.Fills!.Elements<DocumentFormat.OpenXml.Spreadsheet.Fill>().ElementAt((int)xf.FillId!.Value).PatternFill!.ForegroundColor!.Rgb!.Value!, font.Bold is null ? "" : "bold", font.Color?.Rgb?.Value ?? ""];
    }
    Check(formatRule is [{ Op: "format-rows", Contains: "りんご", Fill: "黄色", Bold: true, Color: "赤" }] && sheetRule is [{ Op: "add-sheet", Name: "テスト追加" }] &&
          firstFormat.Succeeded && formatPreview.CanApply && formatted.Succeeded &&
          OfficeDocumentReader.Read(formatted.OutputPath).Sheets.Any(sheet => sheet.Name == "テスト追加") &&
          priceStyle.SequenceEqual(["FFFFFF00", "bold", "FFFF0000"]) &&
          OfficeAssistant.EditMismatches("「りんご」の行を黄色で塗って", [new OfficeEdit("format-rows", Contains: "りんご", Fill: "青")], xlsxSummary).Any(problem => problem.StartsWith("office.fill-not-requested", StringComparison.Ordinal)) &&
          OfficeAssistant.EditMismatches("〇〇市△区の人の行を黄色にして", [new OfficeEdit("format-rows", Contains: "△区役所", Fill: "黄色")], xlsxSummary).Any(problem => problem.StartsWith("office.format-rows-contains", StringComparison.Ordinal)),
        "excel-rows-are-filled-made-bold-and-coloured-and-sheets-added-and-read-back:" + string.Join("|", formatted.Mismatches) + "|" + string.Join(",", priceStyle));
    // Phase A (user: "Office でできる操作はチャットから"): common wordings become operations; each result is read back
    // against what the original says it should become, and formulas follow moved rows.
    string RuleOf(string request) => OfficeRequestRules.TryOperation(request, xlsxSummary, xlsxPath) is { } op ? $"{op.Op}:{op.Column}{op.Cell}{op.Range}{op.Rows}:{op.Order}{op.Format}{op.Formula}{op.Width}{op.Operator}{op.Value}{op.Fill}" : "null";
    var phaseARules = new[]
    {
        RuleOf("単価の高い順に並べ替えて"), RuleOf("見出し行を固定して"), RuleOf("単価を桁区切りにして"), RuleOf("数量の合計を一番下に入れて"),
        RuleOf("金額が400以上のセルを黄色で塗って"), RuleOf("A列の幅を20にして"), RuleOf("表全体に罫線を引いて"), RuleOf("2行目を削除して"), RuleOf("CSVで書き出して")
    };
    Check(phaseARules.SequenceEqual(["sort:C:desc", "freeze:A2:", "number-format:C2:C3:桁区切り", "formula:B4:SUM(B2:B3)", "conditional-format:D2:D3:以上400黄色",
              "column-width:A:20", "borders:A1:D3:thin", "delete-rows:2:", "csv::"]),
        "excel-phase-a-wordings-become-operations:" + string.Join(" | ", phaseARules));
    var sortedBook = OfficeEditor.Apply(OfficeEditor.Preview(xlsxPath, [new OfficeEdit("sort", Sheet: "売上", Column: "単価", Order: "desc")]));
    var totalBook = OfficeEditor.Apply(OfficeEditor.Preview(sortedBook.OutputPath, [new OfficeEdit("formula", Sheet: "売上", Cell: "D4", Formula: "SUM(D2:D3)"),
        new OfficeEdit("number-format", Sheet: "売上", Range: "D2:D4", Format: "円"), new OfficeEdit("freeze", Sheet: "売上", Cell: "A2"),
        new OfficeEdit("borders", Sheet: "売上", Range: "A1:D4"), new OfficeEdit("conditional-format", Sheet: "売上", Range: "D2:D3", Operator: "以上", Value: "420", Fill: "黄色")]));
    var trimmedBook = OfficeEditor.Apply(OfficeEditor.Preview(totalBook.OutputPath, [new OfficeEdit("delete-rows", Sheet: "売上", Rows: "3")]));
    string totalFormula;
    using (var book = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Open(trimmedBook.OutputPath, false))
        totalFormula = book.WorkbookPart!.WorksheetParts.First().Worksheet!.Descendants<DocumentFormat.OpenXml.Spreadsheet.CellFormula>().Select(formula => formula.Text).Last() ?? "";
    // Phase B: Word wordings become operations, run on a document with a table and tracked changes, and read back.
    string WordRuleOf(string request) => OfficeRequestRules.TryWordOperation(request) is { } op ? $"{op.Op}:{op.Find}:{op.Text}{op.Value}:{op.Bold}{op.Color}{op.Highlight}:{op.Level}{op.Rows}{op.Column}" : "null";
    var phaseBRules = new[] { WordRuleOf("「重要」を太字の赤にして"), WordRuleOf("「はじめに」を見出し1にして"), WordRuleOf("表の2行目の3列目を「済」にして"), WordRuleOf("「締め切り」に「日付を確認」とコメントして"), WordRuleOf("変更履歴をすべて承諾して") };
    Check(phaseBRules.SequenceEqual(["format-text:重要::True赤:", "heading:はじめに:::1", "table-set-cell::済::23", "comment:締め切り:日付を確認::", "accept-changes::::"]),
        "word-phase-b-wordings-become-operations:" + string.Join(" | ", phaseBRules));
    var wordDrill = Path.Combine(officeFolder, "表と履歴.docx");
    using (var wordCreated = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Create(wordDrill, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
    {
        DocumentFormat.OpenXml.Wordprocessing.Paragraph Para(string text) => new(new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text(text)));
        DocumentFormat.OpenXml.Wordprocessing.TableRow TRow(params string[] cells) => new(cells.Select(cell => new DocumentFormat.OpenXml.Wordprocessing.TableCell(Para(cell))));
        wordCreated.AddMainDocumentPart().Document = new DocumentFormat.OpenXml.Wordprocessing.Document(new DocumentFormat.OpenXml.Wordprocessing.Body(
            Para("はじめに"), Para("重要な点は締め切りです。"),
            new DocumentFormat.OpenXml.Wordprocessing.Table(TRow("番号", "状況"), TRow("1", "未"), TRow("2", "未")),
            new DocumentFormat.OpenXml.Wordprocessing.Paragraph(new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text("提出先は")),
                new DocumentFormat.OpenXml.Wordprocessing.DeletedRun(new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.DeletedText("総務課"))) { Id = "1", Author = "t" },
                new DocumentFormat.OpenXml.Wordprocessing.InsertedRun(new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text("選挙課"))) { Id = "2", Author = "t" }),
            new DocumentFormat.OpenXml.Wordprocessing.SectionProperties()));
    }
    var wordSteps = new[]
    {
        new OfficeEdit("format-text", Find: "重要", Bold: true, Color: "赤"), new OfficeEdit("heading", Find: "はじめに", Level: 1), new OfficeEdit("table-set-cell", Table: 1, Rows: "2", Column: "2", Value: "済"),
        new OfficeEdit("table-add-row", Table: 1, Values: ["3", "未"]), new OfficeEdit("table-delete-row", Table: 1, Rows: "3"), new OfficeEdit("add-paragraph", Find: "はじめに", Text: "追加の段落"),
        new OfficeEdit("header-text", Text: "社外秘", Position: "right"), new OfficeEdit("page-numbers", Format: "{n} / {total}"), new OfficeEdit("comment", Find: "締め切り", Text: "日付を確認"),
        new OfficeEdit("accept-changes"), new OfficeEdit("delete-paragraph", Find: "追加の段落")
    };
    var wordPath = wordDrill;
    var wordProblems = new List<string>();
    foreach (var step in wordSteps)
    {
        var stepResult = OfficeEditor.Apply(OfficeEditor.Preview(wordPath, [step]));
        wordProblems.AddRange(stepResult.Mismatches.Select(problem => step.Op + ":" + problem));
        wordPath = stepResult.OutputPath;
    }
    var finalWord = OfficeDocumentReader.Read(wordPath).ToFacts();
    var mixedWord = OfficeEditor.Preview(wordDrill, [new OfficeEdit("heading", Find: "はじめに", Level: 1), new OfficeEdit("accept-changes")]);
    Check(wordProblems.Count == 0 && finalWord.Contains("選挙課", StringComparison.Ordinal) && !finalWord.Contains("総務課", StringComparison.Ordinal) && !mixedWord.CanApply,
        "word-phase-b-operations-are-made-and-read-back:" + string.Join(" | ", wordProblems));
    // Phase D: PowerPoint wordings become operations; slides are added, deleted, moved and noted, read back, and the
    // files stay valid (sections kept in step with the slides, a notes master made when the file has none).
    string SlideRuleOf(string request) => OfficeRequestRules.TryPowerPointOperation(request, 5) is { } op ? $"{op.Op}:{op.Find}{op.With}:{op.Text}:{op.Rows}:{op.Position}:{string.Join("/", op.Values ?? [])}{op.Bold}{op.Color}" : "null";
    var phaseDRules = new[] { SlideRuleOf("3枚目のスライドを削除して"), SlideRuleOf("最後に「まとめ」というスライドを追加して"), SlideRuleOf("2枚目の後に「課題」というスライドを追加して、本文は「A、B」"),
        SlideRuleOf("最後のスライドを最初に移動して"), SlideRuleOf("2枚目のノートに「短く話す」と書いて"), SlideRuleOf("「旧」を「新」に置き換えて"), SlideRuleOf("「52%」を赤の太字にして") };
    Check(phaseDRules.SequenceEqual(["delete-slide:::3::", "add-slide::まとめ:::","add-slide::課題:2::A/B", "move-slide:::5:1:", "notes::短く話す:2::", "replace:旧新::::", "format-text:52%::::True赤"]),
        "powerpoint-phase-d-wordings-become-operations:" + string.Join(" | ", phaseDRules));
    static int PresentationErrors(string path)
    {
        using var shown = DocumentFormat.OpenXml.Packaging.PresentationDocument.Open(path, false);
        return new DocumentFormat.OpenXml.Validation.OpenXmlValidator().Validate(shown).Count();
    }
    var slidesPath = Path.Combine(officeFolder, "発表.pptx");
    PowerPointOperations.Create(slidesPath, [("表紙", ["2026年"], null), ("目的", ["投票率を上げる"], "強調する"), ("現状", ["52%"], null), ("不要", ["消す"], null)]);
    // Sections as PowerPoint 2010+ writes them: every slide listed once.
    using (var sectioned = DocumentFormat.OpenXml.Packaging.PresentationDocument.Open(slidesPath, true))
    {
        var ids = sectioned.PresentationPart!.Presentation!.SlideIdList!.Elements<DocumentFormat.OpenXml.Presentation.SlideId>().Select(id => id.Id!.Value).ToArray();
        DocumentFormat.OpenXml.Office2010.PowerPoint.Section Section(string name, IEnumerable<uint> members) =>
            new(new DocumentFormat.OpenXml.Office2010.PowerPoint.SectionSlideIdList(members.Select(id => new DocumentFormat.OpenXml.Office2010.PowerPoint.SectionSlideIdListEntry { Id = id })))
            { Name = name, Id = "{" + Guid.NewGuid().ToString().ToUpperInvariant() + "}" };
        sectioned.PresentationPart.Presentation.Append(new DocumentFormat.OpenXml.Presentation.PresentationExtensionList(
            new DocumentFormat.OpenXml.Presentation.PresentationExtension(new DocumentFormat.OpenXml.Office2010.PowerPoint.SectionList(Section("前半", ids.Take(2)), Section("後半", ids.Skip(2)))) { Uri = "{521415D9-36F7-43E2-AB2F-B90AF26B5E84}" }));
        sectioned.PresentationPart.Presentation.Save();
    }
    var slideErrorsBefore = PresentationErrors(slidesPath);
    var slideSteps = new[]
    {
        new OfficeEdit("replace", Find: "52%", With: "55%"), new OfficeEdit("format-text", Find: "55%", Bold: true, Color: "赤", Size: 28, Font: "gothic"),
        new OfficeEdit("delete-slide", Rows: "4"), new OfficeEdit("add-slide", Text: "課題", Values: ["若者", "情報"], Rows: "2"), new OfficeEdit("add-slide", Text: "最初", Rows: "0"),
        new OfficeEdit("add-slide", Text: "最後"), new OfficeEdit("move-slide", Rows: "6", Position: "2"), new OfficeEdit("notes", Rows: "1", Text: "自己紹介"), new OfficeEdit("notes", Rows: "4", Text: "書き換え")
    };
    var slidePath = slidesPath;
    var slideProblems = new List<string>();
    foreach (var step in slideSteps)
    {
        var stepResult = OfficeEditor.Apply(OfficeEditor.Preview(slidePath, [step]));
        slideProblems.AddRange(stepResult.Mismatches.Select(problem => step.Op + ":" + problem));
        slidePath = stepResult.OutputPath;
    }
    var finalSlides = OfficeDocumentReader.Read(slidePath).Slides!;
    var plainSlides = Path.Combine(officeFolder, "ノートなし.pptx");
    PowerPointOperations.Create(plainSlides, [("一枚目", [], null)]);
    var notedPlain = OfficeEditor.Apply(OfficeEditor.Preview(plainSlides, [new OfficeEdit("notes", Rows: "1", Text: "初めてのノート")]));
    var lastSlideRefused = !OfficeEditor.Preview(plainSlides, [new OfficeEdit("delete-slide", Rows: "1")]).CanApply;
    Check(slideProblems.Count == 0 && slideErrorsBefore == 0 && PresentationErrors(slidePath) == 0 && notedPlain.Succeeded && PresentationErrors(notedPlain.OutputPath) == 0 && lastSlideRefused &&
          string.Join(",", finalSlides.Select(slide => slide.Title)) == "最初,最後,表紙,目的,課題,現状" && finalSlides[0].Notes == "自己紹介" && finalSlides[3].Notes == "書き換え",
        "powerpoint-phase-d-operations-are-made-and-read-back:" + string.Join(" | ", slideProblems) + "|" + slideErrorsBefore + "|" + PresentationErrors(slidePath) + "|" +
        string.Join(",", finalSlides.Select(slide => slide.Title)) + "|" + string.Join(" | ", notedPlain.Mismatches));
    // Windows sources written as if a designer file existed, with C# 6/7 syntax, are fixed mechanically (trial 2026-10-13).
    var designerStyle = WindowsSourceAutoFix.Apply("""
        using System;
        using System.Windows.Forms;
        namespace TimerApp
        {
            public partial class MainForm : Form
            {
                private Label timerLabel = new Label();
                private int left;
                public MainForm() { InitializeComponent(); }
                private void Show(string text)
                {
                    if (int.TryParse(text, out int minutes)) { left = minutes * 60; }
                    timerLabel.Text = $"残り時間: {left} 秒 ({left / 60.0:F1} 分) {{ok}}";
                }
            }
        }
        """);
    Check(designerStyle.Fixes.SequenceEqual(["interpolated-strings", "out-variables", "designer-call", "partial-class", "entry-point", "error-message"]) &&
          designerStyle.Source.Contains("string.Format(\"残り時間: {0} 秒 ({1:F1} 分) {{ok}}\", left, left / 60.0)", StringComparison.Ordinal) &&
          designerStyle.Source.Contains("int minutes;", StringComparison.Ordinal) && designerStyle.Source.Contains("out minutes)", StringComparison.Ordinal) &&
          !designerStyle.Source.Contains("InitializeComponent", StringComparison.Ordinal) && !designerStyle.Source.Contains("partial", StringComparison.Ordinal) &&
          designerStyle.Source.Contains("Application.Run(new MainForm())", StringComparison.Ordinal) && designerStyle.Source.Contains("[STAThread]", StringComparison.Ordinal),
        "windows-designer-style-and-csharp6-syntax-are-fixed-mechanically:" + string.Join(",", designerStyle.Fixes) + "\n" + designerStyle.Source);
    // A request beyond the spec language goes straight to free generation, so nothing is narrowed to fit it.
    Check(AppSpecCompiler.BeyondVocabulary("三目並べのゲームを作って") == "ゲーム" && AppSpecCompiler.BeyondVocabulary("棒グラフで表示して") == "グラフ" &&
          AppSpecCompiler.BeyondVocabulary("買い物リストを作って。品名を追加できるように") is null,
        "requests-beyond-the-spec-language-are-built-freely");
    // Designer-style controls are completed from how the code uses them (trials 2026-10-13/14): a board used as
    // grid[i, j], buttons named button1/button3 (never matched to textBox1), captions from what each handler does, a save
    // into a folder made first, and a form that ran Application.Run(this) itself.
    var designed = WindowsSourceAutoFix.Apply("""
        using System;
        using System.IO;
        using System.Windows.Forms;
        namespace Memo
        {
            public class MainForm : Form
            {
                private const int BoardSize = 3;
                private TextBox textBox1;
                public MainForm() { Application.Run(this); }
                private void Fill() { for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) cells[i, j].Text = ""; }
                private void button1_Click(object sender, EventArgs e)
                {
                    string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MemoApp", "memo.txt");
                    File.WriteAllText(path, textBox1.Text);
                }
                private void button3_Click(object sender, EventArgs e)
                {
                    Application.Exit();
                }
                private void cells_Click(object sender, EventArgs e) { }
            }
        }
        """).Source;
    Check(designed.Contains("private Button[,] cells;", StringComparison.Ordinal) && designed.Contains("cells = new Button[3, 3];", StringComparison.Ordinal) &&
          designed.Contains("localStudioCell.Click += cells_Click;", StringComparison.Ordinal) &&
          designed.Contains("button1Button.Text = \"保存\";", StringComparison.Ordinal) && designed.Contains("button3Button.Text = \"閉じる\";", StringComparison.Ordinal) &&
          designed.Contains("button1Button.Click += button1_Click;", StringComparison.Ordinal) && !designed.Contains("textBox1.Click", StringComparison.Ordinal) &&
          designed.Contains("Directory.CreateDirectory(Path.GetDirectoryName(path));", StringComparison.Ordinal) &&
          !designed.Contains("Application.Run(this)", StringComparison.Ordinal) && designed.Contains("Application.Run(new MainForm())", StringComparison.Ordinal),
        "windows-designer-controls-boards-captions-and-folders-are-completed:\n" + designed);
    var mixedStructural = OfficeEditor.Preview(xlsxPath, [new OfficeEdit("sort", Sheet: "売上", Column: "単価"), new OfficeEdit("freeze", Sheet: "売上", Cell: "A2")]);
    var csvResult = OfficeEditor.Apply(OfficeEditor.Preview(xlsxPath, [new OfficeEdit("csv", Sheet: "売上")]));
    Check(sortedBook.Succeeded && totalBook.Succeeded && trimmedBook.Succeeded && totalFormula == "SUM(D2:D2)" && !mixedStructural.CanApply &&
          csvResult.Succeeded && csvResult.OutputPath.EndsWith(".csv", StringComparison.Ordinal) && File.ReadAllText(csvResult.OutputPath).Contains("品名,数量,単価,金額", StringComparison.Ordinal),
        "excel-phase-a-operations-are-made-and-read-back:" + string.Join("|", sortedBook.Mismatches.Concat(totalBook.Mismatches).Concat(trimmedBook.Mismatches).Concat(csvResult.Mismatches)) + "|" + totalFormula);

    // Recorded model exchanges replayed without a model (after DeepSeek Harness's keyless replay tests). Each fixture is
    // a mistake seen in a real drill; the replay fails closed when the harness sends anything the recording did not see.
    var replayFolder = Path.Combine(Path.GetTempPath(), "ls-replay-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(replayFolder);
    var replayJpg = Path.Combine(replayFolder, "jpg");
    var replayDocx = Path.Combine(replayFolder, "docx");
    Directory.CreateDirectory(replayJpg);
    Directory.CreateDirectory(replayDocx);
    foreach (var name in new[] { "IMG_0001.jpg", "IMG_0002.jpg", "IMG_0003.jpg", "IMG_0004.jpg", "memo.txt" })
        await File.WriteAllTextAsync(Path.Combine(replayJpg, name), name);
    foreach (var name in new[] { "会議資料 final.docx", "議事録 final.docx" })
        await File.WriteAllTextAsync(Path.Combine(replayDocx, name), name);
    var fixtureNames = typeof(ScriptedOfficePlanner).Assembly.GetManifestResourceNames().Where(name => name.StartsWith("replay/", StringComparison.Ordinal) && !name.StartsWith("replay/pdf-", StringComparison.Ordinal)).Order().ToArray();
    var replayResults = new List<string>();
    foreach (var fixtureName in fixtureNames)
    {
        using var fixtureReader = new StreamReader(typeof(ScriptedOfficePlanner).Assembly.GetManifestResourceStream(fixtureName)!, Encoding.UTF8);
        var fixtureLines = (await fixtureReader.ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using var header = JsonDocument.Parse(fixtureLines[0]);
        var input = header.RootElement.GetProperty("input").GetString();
        var request = header.RootElement.GetProperty("request").GetString()!;
        var expect = header.RootElement.GetProperty("expect");
        var replay = new ExchangeReplay(ModelExchangeLog.Parse(fixtureLines));
        string text;
        bool hasPreview;
        try
        {
            if (input == "sales-xlsx")
            {
                var reply = await new OfficeAssistant(new ReplayOfficePlanner(replay)).HandleAsync(request, xlsxPath);
                (text, hasPreview) = (reply.Text, reply.Preview is not null);
            }
            else
            {
                var preview = await new RenameAssistant(new ReplayRenamePlanner(replay), journalRoot).PreviewAsync(request, input == "jpg-folder" ? replayJpg : replayDocx);
                (text, hasPreview) = (RenameAssistant.Describe(preview), true);
            }
        }
        catch (InvalidDataException ex) { (text, hasPreview) = (ex.Message, false); }
        catch (ReplayDivergedException ex) { (text, hasPreview) = ("diverged: " + ex.Message, false); }
        var passed = hasPreview == expect.GetProperty("preview").GetBoolean() &&
                     replay.Served == expect.GetProperty("served").GetInt32() && replay.Remaining == 0 &&
                     (!expect.TryGetProperty("textContains", out var contains) || text.Contains(contains.GetString()!, StringComparison.Ordinal)) &&
                     (!expect.TryGetProperty("error", out var error) || text.Contains(error.GetString()!, StringComparison.Ordinal));
        if (!passed) replayResults.Add($"{fixtureName} served={replay.Served} remaining={replay.Remaining} text={text.Split('\n')[0]}");
    }
    Check(fixtureNames.Length >= 5 && replayResults.Count == 0, "recorded-model-mistakes-replay-without-a-model:" + string.Join(" | ", replayResults));

    // The replay is strict: a changed request, input or rejection reason is reported, never silently answered.
    var strictReplay = new ExchangeReplay([new ModelExchange("office", "質問", ModelExchangeLog.Hash("事実"), [], JsonSerializer.SerializeToElement(new OfficePlan("answer", "答え", null)))]);
    var sameAnswer = strictReplay.Next("office", "質問", ModelExchangeLog.Hash("事実"), []).GetProperty("answer").GetString();
    CheckThrows(() => strictReplay.Next("office", "質問", ModelExchangeLog.Hash("事実"), ["office.empty-answer"]), "replay-refuses-an-unrecorded-extra-exchange");
    CheckThrows(() => new ExchangeReplay([new ModelExchange("office", "質問", ModelExchangeLog.Hash("事実"), [], default)]).Next("office", "質問", ModelExchangeLog.Hash("別の事実"), []), "replay-refuses-changed-facts");
    CheckThrows(() => new ExchangeReplay([new ModelExchange("office", "質問", ModelExchangeLog.Hash("事実"), ["a"], default)]).Next("office", "質問", ModelExchangeLog.Hash("事実"), ["b"]), "replay-refuses-changed-rejection-reasons");
    // Recording keeps the request, a hash of the facts and the plan, but not the file's contents.
    var exchangeFile = Path.Combine(smokePaths.StateRoot, "model-exchanges", "smoke.jsonl");
    await new OfficeAssistant(new RecordingOfficePlanner(new ScriptedOfficePlanner(new OfficePlan("answer", "見積の担当は営業部です（「営業部」）。", null)), () => exchangeFile)).HandleAsync("担当は誰？", docPath);
    var recordedExchanges = ModelExchangeLog.Read(exchangeFile);
    Check(sameAnswer == "答え" && recordedExchanges.Count == 1 && recordedExchanges[0].Request == "担当は誰？" &&
          recordedExchanges[0].InputSha256 == ModelExchangeLog.Hash(docSummary.ToFacts()) &&
          !(await File.ReadAllTextAsync(exchangeFile)).Contains("10月末", StringComparison.Ordinal),
        "model-exchanges-are-recorded-without-file-contents");

    // 4-3: PDFs read by PdfPig, rewritten page by page by PDFsharp into new files that are read back page by page.
    var pdfFolder = Path.Combine(Path.GetTempPath(), "ls-pdf-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(pdfFolder);
    string MakePdf(string name, params string[] pageTexts)
    {
        var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        foreach (var pageText in pageTexts)
            builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4).AddText(pageText, 14, new UglyToad.PdfPig.Core.PdfPoint(60, 700), font);
        var pdfPath = Path.Combine(pdfFolder, name);
        File.WriteAllBytes(pdfPath, builder.Build());
        return pdfPath;
    }
    var reportPdf = MakePdf("report.pdf", "PAGE-ONE", "PAGE-TWO total 1250", "PAGE-THREE", "PAGE-FOUR", "PAGE-FIVE");
    var appendixPdf = MakePdf("appendix.pdf", "APPX-A", "APPX-B");
    var notPdf = Path.Combine(pdfFolder, "fake.pdf");
    await File.WriteAllTextAsync(notPdf, "hello");
    var reportHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(reportPdf)));
    var reportSummary = PdfDocumentReader.Read(reportPdf);
    Check(reportSummary.Pages.Count == 5 && reportSummary.Pages[2].Text.Contains("PAGE-THREE", StringComparison.Ordinal) &&
          PdfDocumentReader.SizeName(reportSummary.Pages[0]) == "A4 縦" && reportSummary.ToFacts().Contains("[3ページ目 A4 縦] PAGE-THREE", StringComparison.Ordinal) &&
          PdfDocumentReader.Check(notPdf) is not null && PdfDocumentReader.Check(Path.Combine(pdfFolder, "x.docx")) is not null,
        "pdf-files-are-read-page-by-page-and-non-pdfs-are-refused");

    string Rules(string request, int files = 1, int pages = 5) =>
        PdfRequestRules.TryRules(request, files, pages) is { } ruledOps ? string.Join(";", ruledOps.Select(op => $"{op.Op}:{op.Pages}:{op.Degrees}:{(op.Order is null ? "" : string.Join(",", op.Order))}")) : "null";
    var ruleResults = new[]
    {
        Rules("3ページ目を削除して"), Rules("３ページ目を削除して"), Rules("2-4ページを抜き出して"), Rules("2ページから4ページだけにして"),
        Rules("全ページを右に回転して"), Rules("1ページ目を左に回転"), Rules("1ページずつ分割して"), Rules("2ページごとに分割して"),
        Rules("2ページ目と4ページ目を入れ替えて"), Rules("結合して", files: 2, pages: 7), Rules("この2つを結合して6ページ目を削除", files: 2, pages: 7),
        Rules("1,3,5ページを削除して"), Rules("1ページ目と2ページ目を逆にして"), Rules("3枚目のページはいらないので消して、残りを左に回して"), Rules("偶数ページだけを残して"), Rules("最後のページを削除して"),
        Rules("この2つを1つのPDFにして", files: 2, pages: 7), Rules("表紙以外を1つずつ別のファイルにして"), Rules("2ページ目から4ページ目までを別のPDFにして"), Rules("3ページ目以外を削除"),
        Rules("3ページ目を消して、いい感じに整えて"), Rules("いい感じに整えて")
    };
    Check(ruleResults.SequenceEqual([
            "delete:3::", "delete:3::", "extract:2-4::", "extract:2-4::",
            "rotate::90:", "rotate:1:270:", "split:::", "split:1-2,3-4,5-5::",
            "reorder:::1,4,3,2,5", "merge:::", "merge:::;delete:6::",
            "delete:1,3,5::", "reorder:::2,1,3,4,5", "delete:3::;rotate::270:", "extract:2,4::", "delete:5::",
            "merge:::", "extract:2-5::;split:::", "extract:2-4::", "delete:1-2,4-5::", "null", "null"]),
        "pdf-common-requests-are-planned-by-rules:" + string.Join(" | ", ruleResults));
    Check(PdfRequestRules.Mismatches("3ページ目を削除して", [new PdfOp("delete", "2")], 1).Any(problem => problem.StartsWith("pdf.pages", StringComparison.Ordinal)) &&
          PdfRequestRules.Mismatches("3ページ目を削除して", [new PdfOp("extract", "3")], 1).Count == 2 &&
          PdfRequestRules.Mismatches("3ページ目を削除して", [new PdfOp("delete", "3")], 1).Count == 0 &&
          PdfRequestRules.Mismatches("この2つを1つのPDFにして", [new PdfOp("merge", "appendix.pdf,report.pdf")], 2).Count == 0 &&
          PdfRequestRules.Mismatches("2ページ目を削除して", [new PdfOp("delete", "appendix.pdf")], 1).Any(problem => problem.StartsWith("pdf.pages-invalid", StringComparison.Ordinal)) &&
          PdfRequestRules.Mismatches("最後のページを消しておいて", [new PdfOp("delete", "4")], 1, pages: 5).Any(problem => problem.StartsWith("pdf.pages", StringComparison.Ordinal)) &&
          PdfRequestRules.IntentOf("何ページありますか？") == "question" && PdfRequestRules.IntentOf("3ページ目を削除して") == "edit",
        "pdf-ai-plans-must-match-the-requested-operations-and-pages");

    var deletePreview = PdfEditor.Preview([reportPdf], [new PdfOp("delete", "3")]);
    var deleteResult = PdfEditor.Apply(deletePreview);
    var afterDelete = PdfDocumentReader.Read(deleteResult.OutputPaths.Single());
    Check(deletePreview.CanApply && deleteResult.Succeeded && deleteResult.OutputPaths.Single().EndsWith("report_LS編集.pdf", StringComparison.Ordinal) &&
          afterDelete.Pages.Select(page => page.Text).SequenceEqual(["PAGE-ONE", "PAGE-TWO total 1250", "PAGE-FOUR", "PAGE-FIVE"]) &&
          Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(reportPdf))) == reportHash,
        "pdf-delete-makes-a-checked-new-file-and-keeps-the-original:" + string.Join("|", deleteResult.Mismatches));
    var mergePreview = PdfEditor.Preview([reportPdf, appendixPdf], [new PdfOp("merge"), new PdfOp("rotate", "6", 90), new PdfOp("reorder", Order: [7, 6, 1, 2, 3, 4, 5])]);
    var mergeResult = PdfEditor.Apply(mergePreview);
    var merged = PdfDocumentReader.Read(mergeResult.OutputPaths.Single());
    var splitResult = PdfEditor.Apply(PdfEditor.Preview([reportPdf], [new PdfOp("split", "1-2,3-4,5")]));
    Check(mergeResult.Succeeded && merged.Pages.Count == 7 && merged.Pages[0].Text == "APPX-B" && merged.Pages[1].Text == "APPX-A" && merged.Pages[1].Rotation == 90 &&
          mergeResult.OutputPaths.Single().EndsWith("report_LS結合.pdf", StringComparison.Ordinal) &&
          splitResult.Succeeded && splitResult.OutputPaths.Count == 3 && PdfDocumentReader.Read(splitResult.OutputPaths[2]).Pages.Single().Text == "PAGE-FIVE",
        "pdf-merge-rotate-reorder-and-split-follow-the-preview:" + string.Join("|", mergeResult.Mismatches.Concat(splitResult.Mismatches)));
    // The read-back is a real check: a result whose pages are not the planned ones is reported, not called a success.
    var wrongExpectation = deletePreview with { Outputs = [[new PdfPageRef(0, 1, 0), new PdfPageRef(0, 3, 0), new PdfPageRef(0, 4, 0), new PdfPageRef(0, 5, 0)]] };
    var outOfRange = PdfEditor.Preview([reportPdf], [new PdfOp("delete", "9")]);
    Check(PdfEditor.VerifyOutputs(wrongExpectation, deleteResult.OutputPaths).Any(problem => problem.Contains("2ページ目が予定のページ", StringComparison.Ordinal)) &&
          !outOfRange.CanApply && outOfRange.Problems.Single().Contains("全5ページ", StringComparison.Ordinal) &&
          !PdfEditor.Preview([reportPdf, appendixPdf], [new PdfOp("delete", "1")]).CanApply,
        "pdf-read-back-catches-wrong-pages-and-impossible-plans-are-not-offered");

    var countPlanner = new ScriptedPdfPlanner(new PdfPlan("answer", "5ページです", null));
    var countReply = await new PdfAssistant(countPlanner).HandleAsync("このPDFは何ページ？", [reportPdf]);
    var pageReply = await new PdfAssistant(countPlanner).HandleAsync("3ページ目には何が書いてある？", [reportPdf]);
    var inventedPdf = new ScriptedPdfPlanner(new PdfPlan("answer", "合計は 9999 です。", null));
    var inventedReply = await new PdfAssistant(inventedPdf).HandleAsync("このPDFの要約を教えて", [reportPdf]);
    var wrongPagePlanner = new ScriptedPdfPlanner(new PdfPlan("edit", null, [new PdfOp("delete", "2")]));
    var wrongPageReply = await new PdfAssistant(wrongPagePlanner).HandleAsync("3ページ目と4ページ目を削除して2ページ目を右に回転", [reportPdf]);
    var ruledReply = await new PdfAssistant(wrongPagePlanner).HandleAsync("3ページ目を削除して", [reportPdf]);
    Check(countPlanner.Calls == 0 && countReply.Text.Contains("5ページ", StringComparison.Ordinal) && pageReply.Text.Contains("PAGE-THREE", StringComparison.Ordinal) &&
          inventedPdf.Calls == 3 && inventedReply.Text.Contains("見つかりませんでした", StringComparison.Ordinal) &&
          wrongPagePlanner.Calls == 3 && wrongPageReply.Preview is null && wrongPageReply.Text.Contains("ファイルは作りません", StringComparison.Ordinal) &&
          ruledReply.Preview is { CanApply: true },
        "pdf-counts-and-page-text-are-computed-and-ai-plans-on-other-pages-are-refused:" + wrongPageReply.Text.Split('\n')[0]);
    // A form with one text field, written by hand as the smallest valid PDF.
    var formPdf = Path.Combine(pdfFolder, "form.pdf");
    {
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Annots [4 0 R] /Contents 5 0 R >>",
            "<< /FT /Tx /T (name) /V () /Rect [100 700 300 720] /Subtype /Widget /Type /Annot /P 3 0 R >>",
            "<< /Length 0 >>\nstream\n\nendstream"
        };
        var builder = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(builder.ToString()));
            builder.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        var xref = Encoding.ASCII.GetByteCount(builder.ToString());
        builder.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) builder.Append($"{offset:D10} 00000 n \n");
        builder.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        await File.WriteAllTextAsync(formPdf, builder.ToString(), Encoding.ASCII);
    }
    // Recorded PDF exchanges from the real drills (2026-10-11), replayed without a model.
    var pdfReplayResults = new List<string>();
    var pdfFixtures = typeof(ScriptedOfficePlanner).Assembly.GetManifestResourceNames().Where(name => name.StartsWith("replay/pdf-", StringComparison.Ordinal)).Order().ToArray();
    foreach (var fixtureName in pdfFixtures)
    {
        using var fixtureReader = new StreamReader(typeof(ScriptedOfficePlanner).Assembly.GetManifestResourceStream(fixtureName)!, Encoding.UTF8);
        var fixtureLines = (await fixtureReader.ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using var header = JsonDocument.Parse(fixtureLines[0]);
        var expect = header.RootElement.GetProperty("expect");
        var replay = new ExchangeReplay(ModelExchangeLog.Parse(fixtureLines));
        string[] inputs = header.RootElement.GetProperty("input").GetString() switch { "appendix-report-pdf" => [appendixPdf, reportPdf], "form-pdf" => [formPdf], _ => [reportPdf] };
        PdfReply reply;
        try { reply = await new PdfAssistant(new ReplayPdfPlanner(replay)).HandleAsync(header.RootElement.GetProperty("request").GetString()!, inputs); }
        catch (ReplayDivergedException ex) { reply = new PdfReply("diverged: " + ex.Message, null); }
        var passed = (reply.Preview is { CanApply: true }) == expect.GetProperty("preview").GetBoolean() &&
                     replay.Served == expect.GetProperty("served").GetInt32() && replay.Remaining == 0 &&
                     reply.Text.Contains(expect.GetProperty("textContains").GetString()!, StringComparison.Ordinal);
        if (!passed) pdfReplayResults.Add($"{fixtureName} served={replay.Served} remaining={replay.Remaining} text={reply.Text.Replace("\n", " / ")}");
    }
    Check(pdfFixtures.Length >= 4 && pdfReplayResults.Count == 0, "recorded-pdf-model-mistakes-replay-without-a-model:" + string.Join(" | ", pdfReplayResults));

    var pdfChat = new ConversationOrchestrator(new RecordingPlanner(unsupportedBlueprint), new ScriptedClassifier(OutputKindJudgement.Unknown),
        new ProbeScaffolder(conversationPaths.ProjectRoot), ConversationLog.CreateSession(conversationPaths), () => LocalStudioSettings.DefaultModel)
    { Pdf = new PdfAssistant(new ScriptedPdfPlanner(new PdfPlan("answer", "", null))) };
    // Each request builds on the previous result (hands-on H8: every instruction used to start from the original again).
    var pdfFilesBefore = Directory.GetFiles(pdfFolder).Length;
    await pdfChat.SubmitPdfAsync("1ページ目を削除して", [reportPdf]);
    await pdfChat.SubmitPdfAsync("1ページ目を削除して", [reportPdf]);
    var pdfSecond = pdfChat.Messages[^1];
    var pdfKey = pdfSecond.Choices.First(choice => choice.Id.StartsWith(ConversationOrchestrator.WorkSavePrefix, StringComparison.Ordinal)).Id[ConversationOrchestrator.WorkSavePrefix.Length..];
    var pdfWorking = pdfChat.WorkingFiles(pdfKey);
    var pdfUntouched = Directory.GetFiles(pdfFolder).Length == pdfFilesBefore;
    await pdfChat.ChooseAsync(ConversationOrchestrator.WorkUndoPrefix + pdfKey);
    var pdfAfterUndo = PdfDocumentReader.Read(pdfChat.WorkingFiles(pdfKey).Single()).Pages.Count;
    await pdfChat.ChooseAsync(ConversationOrchestrator.WorkSavePrefix + pdfKey);
    await pdfChat.ChooseAsync(ConversationOrchestrator.WorkSavePrefix + pdfKey);
    var pdfSaved = Path.Combine(pdfFolder, Path.GetFileNameWithoutExtension(reportPdf) + "_edited.pdf");
    var pdfSavedAgain = Path.Combine(pdfFolder, Path.GetFileNameWithoutExtension(reportPdf) + "_edited(2).pdf");
    Check(pdfSecond.Text.Contains("2回目の変更", StringComparison.Ordinal) && PdfDocumentReader.Read(pdfWorking.Single()).Pages.Count == 3 && pdfUntouched &&
          pdfAfterUndo == 4 && File.Exists(pdfSaved) && PdfDocumentReader.Read(pdfSaved).Pages.Count == 4 && File.Exists(pdfSavedAgain) &&
          Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(reportPdf))) == reportHash,
        "pdf-edits-build-on-the-working-copy-can-be-undone-and-are-saved-as-edited:" + pdfSecond.Text.Split('\n')[0]);

    // 4-4: text drawn on pages (Japanese, embedded font), watermark, page numbers, notes, document information and form values.
    string Markup(string request, int files = 1, int pages = 5) =>
        PdfRequestRules.TryRules(request, files, pages) is { } markupOps ? string.Join(";", markupOps.Select(op => $"{op.Op}:{op.Pages}:{op.Text}:{op.Position}:{op.Field}:{op.Value}")) : "null";
    var markupRules = new[]
    {
        Markup("右上に「社外秘」と入れて"), Markup("全ページに「DRAFT」の透かしを入れて"), Markup("ページ番号を下中央に付けて"), Markup("ページ番号を1/10の形で入れて"),
        Markup("2ページ目に「要確認」とコメントを付けて"), Markup("タイトルを「2026年度 報告書」にして"), Markup("入力欄「name」に「山田太郎」を入力して"),
        Markup("3ページ目の左下に「控え」と書いて"), Markup("「社外秘」と入れて、2ページ目を削除"), Markup("「3ページ」と右上に入れて"), Markup("「極秘」を斜めに大きく入れて"), Markup("表紙に「回覧」のはんこを押して"), Markup("「社外秘」と右上に入れて、ページ番号も付けて"), Markup("右上に「A、B」と入れて")
    };
    Check(markupRules.SequenceEqual([
            "stamp::社外秘:top-right::", "watermark::DRAFT:::", "page-numbers::{n}:bottom::", "page-numbers::{n} / {total}:::",
            "note:2:要確認:::", "info::::title:2026年度 報告書", "fill::::name:山田太郎",
            "stamp:3:控え:bottom-left::", "null", "stamp::3ページ:top-right::", "watermark::極秘:::", "stamp:1:回覧:::", "stamp::社外秘:top-right::;page-numbers::{n}:::", "stamp::A、B:top-right::"]),
        "pdf-markup-requests-are-planned-by-rules:" + string.Join(" | ", markupRules));
    var invented = PdfRequestRules.Mismatches("右上に「社外秘」と入れて", [new PdfOp("stamp", Text: "社外秘です", Position: "top-right")], 1);
    Check(invented.Any(problem => problem.StartsWith("pdf.text-not-in-request", StringComparison.Ordinal)) &&
          invented.Any(problem => problem.StartsWith("pdf.request-text-unused", StringComparison.Ordinal)) &&
          PdfRequestRules.Mismatches("右上に「社外秘」と入れて", [new PdfOp("stamp", Text: "社外秘", Position: "top-right")], 1).Count == 0 &&
          PdfRequestRules.IntentOf("右上に「社外秘」と入れて") == "edit" && PdfRequestRules.IntentOf("このPDFのタイトルは何？") == "question",
        "pdf-ai-markup-must-use-the-quoted-text-exactly:" + string.Join("|", invented));
    // Hands-on 2026-10-13 (H3): "全頁をOCRして右に回転" became an OCR alone and was reported as done. The rotation is kept, and
    // a plan without it is found by the request check (which now also holds the rules' own plans).
    var ocrRotate = Markup("全頁をOCRして右に回転");
    Check(ocrRotate == "ocr:::::;rotate:::::" && PdfRequestRules.TryRules("全頁をOCRして右に回転", 1, 5)!.Last().Degrees == 90 &&
          PdfRequestRules.Mismatches("全頁をOCRして右に回転", [new PdfOp("ocr")], 1, 5).Contains("pdf.request-op-missing:rotate") &&
          PdfRequestRules.Mismatches("「社外秘」と入れて全ページを左に回して", [new PdfOp("stamp", Text: "社外秘")], 1, 5).Contains("pdf.request-op-missing:rotate") &&
          Markup("2ページ目をOCRして右に回転") == "null",
        "pdf-a-rotation-next-to-ocr-or-a-stamp-is-not-dropped:" + ocrRotate);
    // Hands-on 2026-10-13 (H4, H6, H7): questions stay questions, "何枚" about the content is not the page count, and an
    // answer must show the file's own words as its evidence.
    var reportSummaries = new[] { PdfDocumentReader.Read(reportPdf) };
    Check(OfficeAssistant.IntentOf("係名に追加して欲しいものは指定されていたか") == "question" &&
          PdfRequestRules.IntentOf("無効票は全部で何枚あった？") == "question" &&
          PdfAssistant.TryComputedAnswer("無効票は全部で何枚あった？", reportSummaries) is null &&
          PdfAssistant.TryComputedAnswer("全部で何枚？", reportSummaries) is { } pageCountAnswer && pageCountAnswer.Contains("5ページ", StringComparison.Ordinal) &&
          PdfRequestRules.IntentOf("全頁を右に回転させたうえで、1ページ目の使用者の欄に「テスト 太郎」と入力して欲しい。") == "edit" &&
          OfficeAssistant.IntentOf("みかんの単価を90に変えて") == "edit" &&
          AnswerEvidence.Problems("574", "開票結果 無効  574 票").Count == 1 &&
          AnswerEvidence.Problems("無効票は574票です（「無効 574」1ページ目）。", "開票結果 無効  574 票").Count == 0 &&
          AnswerEvidence.Problems("記載はファイルの中に見つかりませんでした。", "x").Count == 0 &&
          AnswerEvidence.Note("無効票は600票です（「無効 600」）。", "開票結果 無効  574 票") is { } wrongQuote && wrongQuote.Contains("無効 600", StringComparison.Ordinal) &&
          AnswerEvidence.Problems(string.Concat(Enumerable.Repeat("電子投票を活用して行われた「市議選」が投開票され、開票にかかった時間は1時間7分だった。", 4)),
              "記事 " + string.Concat(Enumerable.Repeat("電子投票を活用して行われた「市議選」が投開票され、開票にかかった時間は1時間7分だった。", 4))).Single().StartsWith("answer.copied-facts", StringComparison.Ordinal),
        "questions-stay-questions-and-answers-carry-the-files-own-words");
    // Drill 2026-10-13 (run by LocalStudio itself): questions answered by searching the text, with the article heading
    // rather than an article the sentence refers to, and the number written right after the question's words.
    var lawSummary = new[] { new PdfSummary("law.pdf", [new PdfPageInfo(1, 595, 842, 0,
        "（投票区の廃止又は変更の告示） 第九条の二 市町村の選挙管理委員会は、法第十七条第二項の規定により設けた投票区を廃止し、又は変更したときは、直ちにその旨を告示しなければならない。 （指定投票区の指定等） 第二十六条 市町村の選挙管理委員会は、前二項の規定により指定投票区を指定し、通知しなければならない。")], null, false) };
    var newsSummary = new[] { new PdfSummary("news.pdf", [new PdfPageInfo(1, 595, 842, 0, "投票率は46.81%。無効票574票のうち、判別不能などによるものは3票のみ。")], null, false) };
    var whereAbolish = PdfAssistant.TryLocateAnswer("投票区を廃止したり変更したりした時について参考にすべき条文はどこ", lawSummary);
    var whereDesignate = PdfAssistant.TryLocateAnswer("指定投票区を指定する際に参考にすべき条文はどこ？", lawSummary);
    var howMany = PdfAssistant.TryLocateAnswer("無効票は全部で何枚あった？", newsSummary);
    Check(whereAbolish is { } a1 && a1.Contains("第九条の二", StringComparison.Ordinal) && !a1.Contains("、第十七条", StringComparison.Ordinal) &&
          whereDesignate is { } a2 && a2.Contains("第二十六条", StringComparison.Ordinal) &&
          howMany is { } a3 && a3.Contains("574票", StringComparison.Ordinal) && a3.Contains("根拠: 「無効票574票", StringComparison.Ordinal),
        "pdf-questions-are-answered-by-searching-with-the-article-heading-and-the-number:" + whereAbolish + "|" + howMany);
    // 2026-10-13 (user: "あらゆる変更を可能に"): the look of drawn text from the words, adjustments of the last drawing,
    // and the font and size read back from the written letters.
    var lookOps = PdfRequestRules.TryRules("ページ番号を明朝体の10ptで、下から20ptの位置に1-30の形で入れて", 1, 5);
    var lookNumber = lookOps?.SingleOrDefault();
    var raised = lookNumber is null ? null : PdfTextLook.Adjust("もう少し上にして", [lookNumber])?.Single();
    var longerDash = raised is null ? null : PdfTextLook.Adjust("ハイフンをちょっとだけ長くして", [raised])?.Single();
    var smaller = longerDash is null ? null : PdfTextLook.Adjust("もっと小さく、9ptくらいにして", [longerDash])?.Single();
    var blueStamp = PdfTextLook.Adjust("文字を青にして", [new PdfOp("stamp", Text: "社外秘", Position: "top-right", Size: 16, Bold: true)])?.Single();
    Check(lookNumber is { Op: "page-numbers", Font: "mincho", Size: 10, Dy: -8, Text: "{n}-{total}" } &&
          raised is { Dy: null } && longerDash is { Text: "{n}－{total}" } && smaller is { Size: 9, Font: "mincho" } &&
          blueStamp is { Color: "青", Size: 16, Bold: true } &&
          PdfTextLook.IsAdjustment("もう少し上にして") && PdfTextLook.IsAdjustment("文字を青にして") && !PdfTextLook.IsAdjustment("3ページ目を削除して") &&
          PdfTextLook.NumberFormat("1/30の形で") == "{n} / {total}",
        "pdf-text-look-is-read-from-words-and-adjusted:" + lookNumber + " | " + smaller);
    var styledResult = PdfEditor.Apply(PdfEditor.Preview([reportPdf], [new PdfOp("page-numbers", Text: "{n}－{total}", Font: "mincho", Size: 9, Dy: -8),
        new PdfOp("stamp", Text: "社外秘", Position: "top-right", Font: "gothic", Size: 16, Bold: true, Color: "青")]));
    var wrongStyle = PdfEditor.VerifyOutputs(PdfEditor.Preview([reportPdf], [new PdfOp("page-numbers", Text: "{n}－{total}", Font: "mincho", Size: 12, Dy: -8),
        new PdfOp("stamp", Text: "社外秘", Position: "top-right", Font: "gothic", Size: 16, Bold: true, Color: "青")]), styledResult.OutputPaths).ToArray();
    Check(styledResult.Succeeded && wrongStyle.Any(problem => problem.Contains("指定の書式", StringComparison.Ordinal)),
        "pdf-font-and-size-of-drawn-text-are-read-back:" + string.Join("|", styledResult.Mismatches.Concat(wrongStyle).Take(3)));
    // Phase C: blank pages, bookmarks, crop, password, header / footer, text and Word export, comparison, images → PDF.
    string PhaseC(string request) => PdfRequestRules.TryRules(request, 1, 5) is [var op] ? $"{op.Op}:{op.Pages}:{op.Value}:{op.Text}:{op.Position}" : "null";
    var phaseCRules = new[]
    {
        PhaseC("2ページ目の後に白紙を2枚入れて"), PhaseC("最初に白紙を入れて"), PhaseC("しおり「本文」を3ページ目に付けて"), PhaseC("パスワード「abcd1234」を付けて"),
        PhaseC("余白を10mm切り取って"), PhaseC("ヘッダーに「回覧」を入れて"), PhaseC("テキストに書き出して"), PhaseC("Wordに書き出して")
    };
    Check(phaseCRules.SequenceEqual(["insert-blank:2:2::", "insert-blank:0:1::", "bookmark:3::本文:", "password::abcd1234::", "crop::28.3::", "stamp:::回覧:top", "export-text::::", "export-docx::::"]) &&
          PdfRequestRules.Mismatches("2ページ目の後に白紙を2枚入れて", [new PdfOp("insert-blank", "2", Value: "2")], 1, 5).Count == 0 &&
          PdfRequestRules.Mismatches("余白を10mm切り取って", [new PdfOp("crop", Value: "28.3")], 1, 5).Count == 0,
        "pdf-phase-c-wordings-become-operations:" + string.Join(" | ", phaseCRules));
    var phaseCResult = PdfEditor.Apply(PdfEditor.Preview([reportPdf], [new PdfOp("insert-blank", "2", Value: "2"), new PdfOp("bookmark", "3", Text: "本文"), new PdfOp("crop", Value: "20"), new PdfOp("page-numbers")]));
    var lockedResult = PdfEditor.Apply(PdfEditor.Preview([reportPdf], [new PdfOp("password", Value: "abcd1234")]));
    var lockedOpensPlain = true;
    try { using var plain = UglyToad.PdfPig.PdfDocument.Open(lockedResult.OutputPaths.Single()); _ = plain.NumberOfPages; } catch (Exception) { lockedOpensPlain = false; }
    var textResult = PdfEditor.Apply(PdfEditor.Preview([reportPdf], [new PdfOp("export-text")]));
    var wordResult = PdfEditor.Apply(PdfEditor.Preview([reportPdf], [new PdfOp("export-docx")]));
    var sameCompare = PdfExport.Compare(PdfDocumentReader.Read(reportPdf), PdfDocumentReader.Read(reportPdf));
    var tinyPng = Path.Combine(pdfFolder, "tiny.png");
    await File.WriteAllBytesAsync(tinyPng, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="));
    var imagesPdf = Path.Combine(pdfFolder, "images.pdf");
    var imageProblems = PdfExport.FromImages([tinyPng, tinyPng], imagesPdf);
    Check(phaseCResult.Succeeded && PdfDocumentReader.Read(phaseCResult.OutputPaths.Single()).Pages.Count == 7 &&
          lockedResult.Succeeded && !lockedOpensPlain && textResult.Succeeded && textResult.OutputPaths.Single().EndsWith(".txt", StringComparison.Ordinal) &&
          wordResult.Succeeded && wordResult.OutputPaths.Single().EndsWith(".docx", StringComparison.Ordinal) &&
          sameCompare.Contains("同じ", StringComparison.Ordinal) && imageProblems.Count == 0,
        "pdf-phase-c-operations-are-made-and-read-back:" + string.Join("|", phaseCResult.Mismatches.Concat(lockedResult.Mismatches).Concat(textResult.Mismatches).Concat(wordResult.Mismatches).Concat(imageProblems)));
    // Hands-on 2026-10-13 (H9, H10): four drawing requests in one message (one without 「」), and clear guidance instead of
    // a refusal for a scanned form or a redaction by kind.
    const string fourMarks = "「社外秘」と上に赤文字で追記して。ページ番号を下に追記して、DRAFTの透かしを追加して、「これはテストです」とコメントを追加して";
    var fourOps = PdfRequestRules.TryRules(fourMarks, 1, 5);
    Check(fourOps is { Count: 4 } && fourOps.Select(op => op.Op).SequenceEqual(["stamp", "page-numbers", "watermark", "note"]) &&
          fourOps[0].Text == "社外秘" && fourOps[2].Text == "DRAFT" && fourOps[3].Text == "これはテストです" &&
          PdfRequestRules.Mismatches(fourMarks, fourOps, 1, 5).Count == 0 &&
          PdfAssistant.GuidanceFor("1ページ目の使用者の欄に「テスト 太郎」と入力して", reportSummaries) is { } noFields && noFields.Contains("入力欄", StringComparison.Ordinal) && noFields.Contains("のように入力してみてください", StringComparison.Ordinal) &&
          PdfAssistant.GuidanceFor("個人情報に当たる欄を黒塗りして", reportSummaries) is { } byKind && byKind.Contains("「」で囲んで", StringComparison.Ordinal) &&
          PdfAssistant.Examples.Contains("のように", StringComparison.Ordinal),
        "pdf-several-marks-in-one-message-and-guidance-for-impossible-requests:" + (fourOps is null ? "null" : string.Join(";", fourOps.Select(op => $"{op.Op}:{op.Text}:{op.Position}"))));
    // Hands-on 2026-10-13 (H1, H2): a chat about a file is listed again after it is saved, and can be written out.
    var savedChat = ConversationLog.CreateSession(smokePaths);
    await savedChat.AppendAsync(ConversationMessage.FromUser("<b>無効票</b>は全部で何枚あった？ （PDF: 開票.pdf）"));
    await savedChat.AppendAsync(ConversationMessage.FromStudio("無効票は 574 枚です（1ページ目「無効 574」）。"));
    var listedChat = ChatHistory.ListSessions(smokePaths).FirstOrDefault(session => session.LogPath == savedChat.FilePath);
    var chatHtml = ChatHistory.ToHtml(listedChat?.Title ?? "", savedChat.Messages);
    var chatText = ChatHistory.ToText(listedChat?.Title ?? "", savedChat.Messages);
    Check(listedChat is { Title: "<b>無効票</b>は全部で何枚あった？", MessageCount: 2 } &&
          chatHtml.Contains("&lt;b&gt;無効票&lt;/b&gt;", StringComparison.Ordinal) && !chatHtml.Contains("<b>", StringComparison.Ordinal) && !chatHtml.Contains("<script", StringComparison.OrdinalIgnoreCase) &&
          chatText.Contains("あなた", StringComparison.Ordinal) && chatText.Contains("無効票は 574 枚です", StringComparison.Ordinal),
        "saved-chats-are-listed-and-written-out-as-html-and-text:" + listedChat?.Title);

    var stampPreview = PdfEditor.Preview([reportPdf], [new PdfOp("stamp", Text: "社外秘", Position: "top-right"), new PdfOp("page-numbers", Text: "{n} / {total}")]);
    var stampResult = PdfEditor.Apply(stampPreview);
    var stamped = PdfDocumentReader.Read(stampResult.OutputPaths.Single());
    var watermarkResult = PdfEditor.Apply(PdfEditor.Preview([reportPdf], [new PdfOp("delete", "5"), new PdfOp("watermark", "1", Text: "DRAFT")]));
    var noteResult = PdfEditor.Apply(PdfEditor.Preview([reportPdf], [new PdfOp("note", "2", Text: "要確認"), new PdfOp("info", Field: "title", Value: "2026年度 報告書")]));
    Check(stampPreview.CanApply && stampResult.Succeeded && stamped.Pages.Count == 5 &&
          stamped.Pages[0].Text.Contains("社外秘", StringComparison.Ordinal) && PdfDocumentReader.Fingerprint(stamped.Pages[4].Text).Contains("5/5", StringComparison.Ordinal) &&
          watermarkResult.Succeeded && PdfDocumentReader.Read(watermarkResult.OutputPaths.Single()).Pages.Count == 4 &&
          noteResult.Succeeded && PdfDocumentReader.Read(noteResult.OutputPaths.Single()).Title == "2026年度 報告書" &&
          Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(reportPdf))) == reportHash,
        "pdf-japanese-stamps-numbers-watermarks-notes-and-info-are-written-and-read-back:" +
        string.Join("|", stampResult.Mismatches.Concat(watermarkResult.Mismatches).Concat(noteResult.Mismatches)) + "|" + stamped.Pages[0].Text);
    // The read-back checks the drawn text too: a result without the planned stamp is not a success.
    var expectedOther = stampPreview with { Outputs = [stampPreview.Outputs[0].Select(page => page with { Marks = [new PdfMark("stamp", "極秘", "top-right")] }).ToArray()] };
    Check(PdfEditor.VerifyOutputs(expectedOther, stampResult.OutputPaths).Any() &&
          !PdfEditor.Preview([reportPdf], [new PdfOp("stamp", Text: "")]).CanApply &&
          !PdfEditor.Preview([reportPdf], [new PdfOp("fill", Field: "name", Value: "x")]).CanApply,
        "pdf-markup-read-back-catches-missing-text-and-impossible-plans-are-not-offered");

    var formSummary = PdfDocumentReader.Read(formPdf);
    var fillPreview = PdfEditor.Preview([formPdf], [new PdfOp("fill", Field: "name", Value: "山田太郎")]);
    var fillResult = PdfEditor.Apply(fillPreview);
    Check(formSummary.Fields.SequenceEqual(["name"]) && formSummary.ToFacts().Contains("入力欄: 「name」", StringComparison.Ordinal) &&
          fillPreview.CanApply && fillResult.Succeeded && PdfMarkup.FormFields(fillResult.OutputPaths.Single()).Contains("name") &&
          !PdfEditor.Preview([formPdf], [new PdfOp("fill", Field: "age", Value: "3")]).CanApply &&
          !PdfEditor.Preview([formPdf, reportPdf], [new PdfOp("merge"), new PdfOp("fill", Field: "name", Value: "x")]).CanApply,
        "pdf-form-fields-are-filled-in-a-new-file-and-read-back:" + string.Join("|", fillResult.Mismatches));
    Check(PdfMarkup.NormalizePosition("右下", "top") == "bottom-right" && PdfMarkup.NormalizePosition("下中央", "top") == "bottom" &&
          PdfMarkup.NormalizePosition("bottom_left", "top") == "bottom-left" && PdfMarkup.NormalizePosition("斜め", "top") is null &&
          PdfMarkup.WindowsJapaneseFontResolver.EmbeddingAllowed(PdfMarkup.WindowsJapaneseFontResolver.FirstFace(
              await File.ReadAllBytesAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "YuGothR.ttc")))),
        "pdf-positions-are-understood-and-the-font-allows-embedding");

    // 4-4b: Windows' own PDF renderer and Japanese OCR — scanned pages read, OCR text layers, redaction checked by OCR, pages as PNG.
    string Imaging(string request, int files = 1, int pages = 5) =>
        PdfRequestRules.TryRules(request, files, pages) is { } imagingOps ? string.Join(";", imagingOps.Select(op => $"{op.Op}:{op.Pages}:{op.Text}")) : "null";
    var imagingRules = new[] { Imaging("OCRして"), Imaging("3ページ目を検索できるようにして"), Imaging("「山田」を黒塗りして"), Imaging("2ページ目の「1250」を黒塗りにして"), Imaging("全ページを画像にして"), Imaging("PNGで保存して") };
    Check(imagingRules.SequenceEqual(["ocr::", "ocr:3:", "redact::山田", "redact:2:1250", "to-images::", "to-images::"]),
        "pdf-ocr-redaction-and-image-requests-are-planned-by-rules:" + string.Join(" | ", imagingRules));
    var imaging = new SafeLocalHarness.Windows.WindowsPdfImaging();
    // A "scanned" PDF: the stamped page 1 (社外秘 in Japanese) as a picture only, without any text.
    var scanPdf = Path.Combine(pdfFolder, "scan.pdf");
    using (var scanDocument = new PdfSharp.Pdf.PdfDocument())
    {
        var stampedSize = PdfDocumentReader.Read(stampResult.OutputPaths.Single()).Pages[0];
        PdfImagingSteps.ImagePage(scanDocument.AddPage(), stampedSize.Width, stampedSize.Height, await imaging.RenderPageAsync(stampResult.OutputPaths.Single(), 1, 4)); // 288 dpi, like a 300 dpi office scan
        scanDocument.Save(scanPdf);
    }
    var scanSummary = PdfDocumentReader.Read(scanPdf);
    var scanReply = await new PdfAssistant(new ScriptedPdfPlanner(new PdfPlan("answer", "", null)), imaging).HandleAsync("1ページ目には何が書いてある？", [scanPdf]);
    var ocrResult = await PdfEditor.ApplyAsync(PdfEditor.Preview([scanPdf], [new PdfOp("ocr")]), imaging);
    var ocrText = PdfDocumentReader.Read(ocrResult.OutputPaths.Single()).Pages[0].Text;
    Check(SafeLocalHarness.Windows.WindowsPdfImaging.JapaneseOcrAvailable && scanSummary.Pages[0].Text.Trim().Length == 0 &&
          scanReply.Text.Contains("OCR", StringComparison.Ordinal) && scanReply.Text.Contains("社外秘", StringComparison.Ordinal) &&
          ocrResult.Succeeded && ocrText.Contains("社外秘", StringComparison.Ordinal) &&
          !PdfEditor.Preview([reportPdf], [new PdfOp("ocr")]).CanApply,
        "pdf-scanned-pages-are-read-by-windows-ocr-and-get-a-searchable-text-layer:" + scanReply.Text.Replace("\n", " / ") + "|" + string.Join("|", ocrResult.Mismatches) + "|" + ocrText);
    var redactPreview = PdfEditor.Preview([reportPdf], [new PdfOp("redact", "2", Text: "1250")]);
    var redactResult = await PdfEditor.ApplyAsync(redactPreview, imaging);
    var redacted = PdfDocumentReader.Read(redactResult.OutputPaths.Single());
    var scanRedact = await PdfEditor.ApplyAsync(PdfEditor.Preview([scanPdf], [new PdfOp("redact", Text: "社外秘")]), imaging);
    Check(redactPreview.CanApply && redactResult.Succeeded && redacted.Pages.Count == 5 && redacted.Pages[1].Text.Trim().Length == 0 &&
          redacted.Pages[0].Text.Contains("PAGE-ONE", StringComparison.Ordinal) &&
          scanRedact.Succeeded &&
          !PdfEditor.Preview([reportPdf], [new PdfOp("redact", Text: "存在しない語")]).CanApply &&
          Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(reportPdf))) == reportHash,
        "pdf-redaction-turns-the-page-into-a-picture-and-ocr-confirms-the-words-are-gone:" + string.Join("|", redactResult.Mismatches.Concat(scanRedact.Mismatches)));
    // Drill 2026-10-12: boxes landed 25% off because Windows gives page sizes in 1/96 inch, and the check missed it because
    // OCR read "1250" as "凵250". Pictures are now 2 × points, and the check allows misreadings.
    var renderedPage = await imaging.RenderPageAsync(reportPdf, 2, 2);
    var redactedPageRead = (await imaging.RecognizeAsync(await imaging.RenderPageAsync(redactResult.OutputPaths.Single(), 2, 2))).Text;
    Check(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(renderedPage.AsSpan(16)) == 1190 &&
          PdfImagingSteps.Similar(PdfDocumentReader.Fingerprint("PAGE-TWO tota凵250"), "1250").Count > 0 &&
          PdfImagingSteps.Similar("PAGE-TWOtotal", "1250").Count == 0 &&
          PdfDocumentReader.Fingerprint(redactedPageRead).Contains("PAGE-TWO", StringComparison.Ordinal) && PdfImagingSteps.Similar(PdfDocumentReader.Fingerprint(redactedPageRead), "1250").Count == 0,
        "pdf-pages-render-at-true-size-and-redaction-hides-only-the-word-even-for-misreading-ocr:" + redactedPageRead);
    // The redaction check is real: an unredacted file is reported, not called a success.
    var notRedacted = await PdfImagingSteps.VerifyRedactionsAsync(redactPreview, [reportPdf], imaging, CancellationToken.None);
    var imagesResult = await PdfEditor.ApplyAsync(PdfEditor.Preview([reportPdf], [new PdfOp("to-images", "1-2")]), imaging);
    Check(notRedacted.Any(problem => problem.Contains("1250", StringComparison.Ordinal)) &&
          imagesResult.Succeeded && imagesResult.OutputPaths.Count == 2 && imagesResult.OutputPaths.All(path => path.EndsWith(".png", StringComparison.Ordinal)) &&
          PdfImagingSteps.VerifyImages(PdfEditor.Preview([reportPdf], [new PdfOp("to-images", "1-3")]), imagesResult.OutputPaths).Count > 0 &&
          !PdfEditor.Preview([reportPdf], [new PdfOp("to-images"), new PdfOp("delete", "1")]).CanApply,
        "pdf-redaction-check-catches-readable-words-and-pages-become-checked-png-files:" + string.Join("|", imagesResult.Mismatches));
    await CheckThrowsAsync(() => PdfEditor.ApplyAsync(PdfEditor.Preview([scanPdf], [new PdfOp("ocr")]), null), "pdf-ocr-without-windows-imaging-is-refused");

    // Phase 3-2: source-aware improvement as versions of the same project.
    var improvedBody = generatedBody.Trim() + "\n<p id=\"note-count\">0件</p>";
    var improvedScript = acceptedGeneratedScript + """

        function countNotes() {
          document.getElementById('note-count').textContent = list.children.length + '件';
        }
        document.getElementById('add-note').addEventListener('click', countNotes);
        """;
    // Each improvement adds its change request as one more requirement, so the fake review answers every id.
    string ImprovedReview(int requirementCount = 2) => JsonSerializer.Serialize(new
    {
        message = new
        {
            content = JsonSerializer.Serialize(new
            {
                requirements = Enumerable.Range(1, requirementCount).Select(index => index == 1
                    ? new { id = "LS-REQ-001", passed = true, ui_evidence = "notes", behavior_evidence = "list.append" }
                    : new { id = $"LS-REQ-{index:000}", passed = true, ui_evidence = "note-count", behavior_evidence = "countNotes" })
                    .ToArray()
            })
        }
    });
    WebProjectImprover ImproverWith(IStaticWebAppVerifier? verifier, params string[] responses) =>
        new(new LocalWebAppGenerationEngine(generatedRuntime, new FakeSettingsStore(LocalStudioSettings.Default), new HttpClient(new SequenceJsonHandler(responses))), verifier);
    var improveTarget = Path.Combine(conversationPaths.ProjectRoot, "improve-target");
    CopyTree(generatedProject, improveTarget);
    var originalScript = File.ReadAllText(Path.Combine(improveTarget, "app.js"));
    var improveCapture = new CapturingJsonHandler(TestData.CandidateEnvelope(improvedBody, improvedScript), ImprovedReview());
    var capturingImprover = new WebProjectImprover(new LocalWebAppGenerationEngine(generatedRuntime, new FakeSettingsStore(LocalStudioSettings.Default), new HttpClient(improveCapture)));
    Check(await capturingImprover.CheckImprovableAsync(improveTarget) is null, "generated-web-app-is-improvable");
    var improvement = await capturingImprover.ImproveAsync(improveTarget, "付箋の件数を表示して");
    var versionIndex = await ProjectVersionStore.LoadAsync(improveTarget);
    Check(improvement.Version.Number == 2 && improvement.Previous.Number == 1 && versionIndex?.Current == 2 &&
          Directory.Exists(ProjectVersionStore.VersionRoot(improveTarget, 1)) && Directory.Exists(ProjectVersionStore.VersionRoot(improveTarget, 2)) &&
          File.ReadAllText(Path.Combine(improveTarget, "app.js")).Contains("countNotes", StringComparison.Ordinal) &&
          File.ReadAllText(Path.Combine(ProjectVersionStore.VersionRoot(improveTarget, 1), "app.js")) == originalScript &&
          improvement.Version.FilesChanged > 0 && improvement.Version.LinesAdded > 0,
        "improvement-becomes-next-version-and-keeps-previous");
    Check(improveCapture.Bodies.Count == 2 && improveCapture.Bodies[0].Contains("currentJavaScript", StringComparison.Ordinal) &&
          improveCapture.Bodies[0].Contains("changeRequest", StringComparison.Ordinal),
        "improvement-sends-current-source-and-change-request");
    using (var improvedManifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(improveTarget, "localstudio.json"))))
        Check(improvedManifest.RootElement.GetProperty("improvements")[0].GetProperty("version").GetInt32() == 2 &&
              improvedManifest.RootElement.GetProperty("blueprint").GetProperty("features").GetArrayLength() == 2,
            "improvement-is-recorded-in-project-manifest");
    var improvedInspection = await ProjectCartridgeStore.InspectAsync(improveTarget);
    Check(improvedInspection.CanResume && (await new StaticWebAppVerifier().VerifyAsync(improveTarget)).Passed &&
          File.ReadAllText(Path.Combine(improveTarget, ".localstudio", "baseline", "manifest.json")).Contains("structural-preview", StringComparison.Ordinal),
        "improved-version-is-verified-and-resumable:" + string.Join("|", improvedInspection.Issues));

    var scriptAfterV2 = File.ReadAllText(Path.Combine(improveTarget, "app.js"));
    var cartridgeAfterV2 = File.ReadAllText(Path.Combine(improveTarget, ".localstudio", "project-cartridge.json"));
    var rejectedImprovement = false;
    try
    {
        await ImproverWith(null,
            // Five attempts; an unchanged repair starts over, so the same answer is given each time.
            TestData.CandidateEnvelope(generatedBody, rejectedGeneratedScript),
            TestData.CandidateEnvelope(generatedBody, rejectedGeneratedScript),
            TestData.CandidateEnvelope(generatedBody, rejectedGeneratedScript),
            TestData.CandidateEnvelope(generatedBody, rejectedGeneratedScript),
            TestData.CandidateEnvelope(generatedBody, rejectedGeneratedScript)).ImproveAsync(improveTarget, "通信で同期して");
    }
    catch (InvalidDataException) { rejectedImprovement = true; }
    var failedVerification = false;
    try
    {
        await ImproverWith(new RejectingWebVerifier(), TestData.CandidateEnvelope(improvedBody, improvedScript), ImprovedReview(3))
            .ImproveAsync(improveTarget, "もう一度件数を表示して");
    }
    catch (InvalidDataException) { failedVerification = true; }
    Check(rejectedImprovement && failedVerification &&
          File.ReadAllText(Path.Combine(improveTarget, "app.js")) == scriptAfterV2 &&
          File.ReadAllText(Path.Combine(improveTarget, ".localstudio", "project-cartridge.json")) == cartridgeAfterV2 &&
          (await ProjectVersionStore.LoadAsync(improveTarget))?.Current == 2 &&
          !Directory.EnumerateDirectories(conversationPaths.ProjectRoot, ".staging-*").Any(),
        "failed-improvement-leaves-current-version-unchanged");

    var restored = await ImproverWith(null).RestoreAsync(improveTarget, 1);
    var afterRestore = await ProjectVersionStore.LoadAsync(improveTarget);
    Check(restored.Version is { Number: 3, Kind: "restored", RestoredFrom: 1 } && afterRestore?.Versions.Count == 3 &&
          File.ReadAllText(Path.Combine(improveTarget, "app.js")) == originalScript &&
          Directory.Exists(ProjectVersionStore.VersionRoot(improveTarget, 2)) &&
          (await ProjectCartridgeStore.InspectAsync(improveTarget)).CanResume,
        "restoring-an-old-version-adds-a-new-version");

    var templateCopy = Path.Combine(conversationPaths.ProjectRoot, "template-copy");
    CopyTree(generatedProject, templateCopy);
    var templateManifest = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(templateCopy, "localstudio.json")))!;
    templateManifest["implementationMode"] = "verified-template";
    File.WriteAllText(Path.Combine(templateCopy, "localstudio.json"), templateManifest.ToJsonString());
    var oversizeCopy = Path.Combine(conversationPaths.ProjectRoot, "oversize-copy");
    CopyTree(generatedProject, oversizeCopy);
    File.WriteAllText(Path.Combine(oversizeCopy, "app.js"), "'use strict';\n" + new string('/', LocalWebAppGenerationEngine.MaxJavaScriptCharacters + 10));
    var improvableCheck = ImproverWith(null);
    Check(await improvableCheck.CheckImprovableAsync(templateCopy) is { } templateReason && templateReason.Contains("テンプレート", StringComparison.Ordinal) &&
          await improvableCheck.CheckImprovableAsync(oversizeCopy) is { } sizeReason && sizeReason.Contains("上限", StringComparison.Ordinal) &&
          await improvableCheck.CheckImprovableAsync(steeredProject) is null,
        "unsupported-projects-are-refused-with-a-reason");

    var projectChat = await ConversationOrchestrator.OpenProjectAsync(
        new RecordingPlanner(unsupportedBlueprint), new ScriptedClassifier(OutputKindJudgement.Unknown), new ProbeScaffolder(conversationPaths.ProjectRoot),
        ImproverWith(null, TestData.CandidateEnvelope(improvedBody, improvedScript), ImprovedReview()),
        improveTarget, () => LocalStudioSettings.DefaultModel);
    var openedAtCompleted = projectChat.Stage == ConversationStage.Completed && projectChat.Kind == StudioProjectKind.DesktopOrWebApp;
    await projectChat.SubmitAsync("付箋の件数を表示して");
    var proposedV4 = projectChat.Stage == ConversationStage.Proposed && projectChat.Messages[^1].Text.Contains("v4", StringComparison.Ordinal) &&
                     projectChat.Messages[^1].Choices.Any(choice => choice.Id == ConversationOrchestrator.StartChoice);
    await projectChat.ChooseAsync(ConversationOrchestrator.StartChoice);
    var savedV4 = projectChat.Stage == ConversationStage.Completed && projectChat.Messages[^1].Text.Contains("v4 を保存しました", StringComparison.Ordinal);
    await projectChat.SubmitAsync("v1に戻して");
    var offeredRestore = projectChat.Messages[^1].Choices.Any(choice => choice.Id == ConversationOrchestrator.RestoreChoicePrefix + "1");
    await projectChat.ChooseAsync(ConversationOrchestrator.RestoreChoicePrefix + "1");
    Check(openedAtCompleted && proposedV4 && savedV4 && offeredRestore &&
          projectChat.Messages[^1].Text.Contains("v5 として復元しました", StringComparison.Ordinal) &&
          (await ProjectVersionStore.LoadAsync(improveTarget))?.Current == 5 &&
          ConversationLog.OpenForProject(improveTarget).Messages.Count == projectChat.Messages.Count,
        "project-chat-improves-and-restores-by-conversation");

    // A plan that asks for external access a local app does not need: shown in plain words, removed only on request.
    var overReachingPlan = unsupportedBlueprint with { Capabilities = ["workspace.write", "network.fetch"] };
    var localOnlyChat = new ConversationOrchestrator(new RecordingPlanner(overReachingPlan), new ScriptedClassifier(OutputKindJudgement.Unknown),
        new ProbeScaffolder(conversationPaths.ProjectRoot), ConversationLog.CreateSession(conversationPaths), () => LocalStudioSettings.DefaultModel);
    await localOnlyChat.SubmitAsync("ブラウザーで使うWebアプリで、天気APIの結果を付箋に貼れるようにして");
    var blockedProposal = localOnlyChat.Messages[^1];
    var offeredLocalOnly = blockedProposal.Text.Contains("インターネット通信", StringComparison.Ordinal) &&
                           blockedProposal.Choices.Any(choice => choice.Id == ConversationOrchestrator.LocalOnlyChoice) &&
                           blockedProposal.Choices.All(choice => choice.Id != ConversationOrchestrator.StartChoice) &&
                           localOnlyChat.Blueprint!.Capabilities.Contains("network.fetch");
    await localOnlyChat.ChooseAsync(ConversationOrchestrator.LocalOnlyChoice);
    Check(offeredLocalOnly && localOnlyChat.Stage == ConversationStage.Proposed &&
          !localOnlyChat.Blueprint!.Capabilities.Contains("network.fetch") &&
          localOnlyChat.Messages[^1].Choices.Any(choice => choice.Id == ConversationOrchestrator.StartChoice) &&
          localOnlyChat.Request.Contains("このPCの中だけで動かす", StringComparison.Ordinal),
        "external-access-is-explained-and-removed-only-on-request");
    var plainChat = new ConversationOrchestrator(new RecordingPlanner(overReachingPlan), new ScriptedClassifier(OutputKindJudgement.Unknown),
        new ProbeScaffolder(conversationPaths.ProjectRoot), ConversationLog.CreateSession(conversationPaths), () => LocalStudioSettings.DefaultModel);
    await plainChat.SubmitAsync("ブラウザーで使うWebアプリで付箋を貼れるようにして。保存はこのPCの中だけ。");
    Check(plainChat.Blueprint!.Capabilities.SequenceEqual(BlueprintPermissionPolicy.LocalOnly) &&
          plainChat.Messages[^1].Choices.Any(choice => choice.Id == ConversationOrchestrator.StartChoice) &&
          !plainChat.Messages[^1].Text.Contains("インターネット通信", StringComparison.Ordinal),
        "permissions-are-decided-by-localstudio-not-the-model");
    Check(BlueprintPermissionPolicy.AsksForExternalAccess("天気APIから取得して") && !BlueprintPermissionPolicy.AsksForExternalAccess("メモを1行ずつ追加して保存"),
        "external-access-signals-come-from-the-users-words");

    // Visual changes: an optional, rule-bound app.css next to the shared theme.
    Check(AppStylesheetPolicy.Validate("#add-note { font-size: 1.4rem; }\n.panel > p { color: #0a5; }").Count == 0 &&
          AppStylesheetPolicy.Validate("body { background: url(x.png) }").Contains("url") &&
          AppStylesheetPolicy.Validate("@import 'a.css';").Contains("import") &&
          AppStylesheetPolicy.Validate("p { content: \"\\75 rl\" }").Contains("escape") &&
          AppStylesheetPolicy.Validate("p { color: red; }</style><script>").Contains("markup") &&
          AppStylesheetPolicy.Validate("p { color: red; ").Contains("unbalanced") &&
          AppStylesheetPolicy.Validate(new string('a', AppStylesheetPolicy.MaxCharacters + 1)).Contains("too-large"),
        "app-css-policy-blocks-loading-and-escapes");
    string CssCandidate(string css) => JsonSerializer.Serialize(new
    {
        message = new { content = JsonSerializer.Serialize(new { body = improvedBody, javascript = improvedScript, css }) }
    });
    var cssTarget = Path.Combine(conversationPaths.ProjectRoot, "css-target");
    CopyTree(generatedProject, cssTarget);
    await ImproverWith(null, CssCandidate("#add-note { font-size: 1.4rem; padding: 14px 24px; }"), ImprovedReview(2))
        .ImproveAsync(cssTarget, "追加ボタンを大きくして");
    var cssHtml = File.ReadAllText(Path.Combine(cssTarget, "index.html"));
    Check(File.ReadAllText(Path.Combine(cssTarget, AppStylesheetPolicy.FileName)).Contains("font-size: 1.4rem", StringComparison.Ordinal) &&
          cssHtml.Contains("href=\"app.css\"", StringComparison.Ordinal) &&
          LocalWebAppGenerationEngine.ExtractGeneratedBody(cssHtml) is not null &&
          (await new StaticWebAppVerifier().VerifyAsync(cssTarget)).Passed &&
          (await ProjectCartridgeStore.InspectAsync(cssTarget)).CanResume,
        "visual-change-is-applied-through-verified-app-css");
    var unsafeCssRejected = false;
    try
    {
        await ImproverWith(null,
            CssCandidate("body { background: url(https://example.invalid/x.png) }"),
            CssCandidate("@import 'theme.css';"),
            CssCandidate("p { content: \"\\75 rl(x)\" }"),
            CssCandidate("body { background: url(https://example.invalid/y.png) }"),
            CssCandidate("@import 'other.css';")).ImproveAsync(cssTarget, "背景に画像を入れて");
    }
    catch (InvalidDataException) { unsafeCssRejected = true; }
    Check(unsafeCssRejected && File.ReadAllText(Path.Combine(cssTarget, AppStylesheetPolicy.FileName)).Contains("font-size: 1.4rem", StringComparison.Ordinal),
        "unsafe-app-css-is-never-saved");
    // A project without visual requirements: a later version that needs no app.css drops the old one.
    // (Where a visual requirement exists, dropping its CSS is rejected as review.style-evidence instead.)
    var cssRemovalTarget = Path.Combine(conversationPaths.ProjectRoot, "css-removal-target");
    CopyTree(generatedProject, cssRemovalTarget);
    await ImproverWith(null, CssCandidate("#note-count { font-weight: bold; }"), ImprovedReview(2)).ImproveAsync(cssRemovalTarget, "付箋の件数を表示して");
    var hadAppCss = File.Exists(Path.Combine(cssRemovalTarget, AppStylesheetPolicy.FileName));
    await ImproverWith(null, TestData.CandidateEnvelope(improvedBody, improvedScript), ImprovedReview(3)).ImproveAsync(cssRemovalTarget, "付箋の件数をもう一度表示して");
    Check(hadAppCss && !File.Exists(Path.Combine(cssRemovalTarget, AppStylesheetPolicy.FileName)) &&
          !File.ReadAllText(Path.Combine(cssRemovalTarget, "index.html")).Contains("app.css", StringComparison.Ordinal) &&
          (await new StaticWebAppVerifier().VerifyAsync(cssRemovalTarget)).Passed,
        "version-without-app-css-removes-the-old-stylesheet");
    var tamperedCss = Path.Combine(conversationPaths.ProjectRoot, "tampered-css");
    CopyTree(generatedProject, tamperedCss);
    File.WriteAllText(Path.Combine(tamperedCss, AppStylesheetPolicy.FileName), "body { background: url(x.png) }");
    var tamperedResult = await new StaticWebAppVerifier().VerifyAsync(tamperedCss);
    Check(!tamperedResult.Passed && tamperedResult.Errors.Contains("security.app-css"), "verifier-rejects-unlinked-or-unsafe-app-css");

    // Repair loop (2026-10-09 real-device findings): a wrong review costs only a review, and earlier findings stay in view.
    var reviewOnlyCapture = new CapturingJsonHandler(
        TestData.CandidateEnvelope(generatedBody, acceptedGeneratedScript),
        TestData.ReviewEnvelope("LS-REQ-001", "notes", "addMemoToList"),
        TestData.ReviewEnvelope("LS-REQ-001", "notes", "list.append"));
    using (var reviewRetryEngine = new LocalWebAppGenerationEngine(generatedRuntime, new FakeSettingsStore(LocalStudioSettings.Default), new HttpClient(reviewOnlyCapture)))
        await reviewRetryEngine.GenerateAsync(unsupportedBlueprint, StudioProjectKind.DesktopOrWebApp);
    Check(reviewOnlyCapture.Bodies.Count == 3 &&
          reviewOnlyCapture.Bodies[2].Contains("Valid behavior_evidence candidates", StringComparison.Ordinal) &&
          reviewOnlyCapture.Bodies[2].Contains("list.append", StringComparison.Ordinal),
        "wrong-review-evidence-is-retried-without-regenerating-code");
    var stickyCapture = new CapturingJsonHandler(
        TestData.CandidateEnvelope(generatedBody, rejectedGeneratedScript),
        TestData.CandidateEnvelope(generatedBody, acceptedGeneratedScript),
        TestData.ReviewEnvelope("LS-REQ-001", "notes", "missingOne"),
        TestData.ReviewEnvelope("LS-REQ-001", "notes", "missingTwo"),
        TestData.CandidateEnvelope(generatedBody, acceptedGeneratedScript),
        TestData.ReviewEnvelope("LS-REQ-001", "notes", "list.append"));
    using (var stickyEngine = new LocalWebAppGenerationEngine(generatedRuntime, new FakeSettingsStore(LocalStudioSettings.Default), new HttpClient(stickyCapture)))
        await stickyEngine.GenerateAsync(unsupportedBlueprint, StudioProjectKind.DesktopOrWebApp);
    Check(stickyCapture.Bodies.Count == 6 &&
          stickyCapture.Bodies[4].Contains("must stay fixed", StringComparison.Ordinal) &&
          stickyCapture.Bodies[4].Contains("fetch(", StringComparison.Ordinal),
        "earlier-security-findings-stay-in-later-repairs");

    // Visual requirements are proven by CSS, behaviour by JavaScript (2026-10-09 real-device finding).
    Check(VisualRequirementPolicy.IsVisualChange("追加ボタンをもっと大きくして、目立つ色にしてください") &&
          !VisualRequirementPolicy.IsVisualChange("付箋の件数を表示して") &&
          !VisualRequirementPolicy.IsVisualChange("ボタンを大きくして、保存もできるようにして") &&
          VisualRequirementPolicy.HasStyleEvidence("#add-note { font-size: 2rem; }", "<button id=\"add-note\">追加</button>") &&
          !VisualRequirementPolicy.HasStyleEvidence("#missing { color: red; }", "<button id=\"add-note\">追加</button>") &&
          !VisualRequirementPolicy.HasStyleEvidence("", "<button id=\"add-note\">追加</button>"),
        "visual-requirements-are-typed-and-checked-mechanically");
    var visualTarget = Path.Combine(conversationPaths.ProjectRoot, "visual-target");
    CopyTree(generatedProject, visualTarget);
    var visualReviewWithoutBehaviour = JsonSerializer.Serialize(new
    {
        message = new
        {
            content = JsonSerializer.Serialize(new
            {
                requirements = new[]
                {
                    new { id = "LS-REQ-001", passed = true, ui_evidence = "notes", behavior_evidence = "list.append" },
                    new { id = "LS-REQ-002", passed = true, ui_evidence = "add-note", behavior_evidence = "" }
                }
            })
        }
    });
    await ImproverWith(null, CssCandidate("#add-note { font-size: 1.5rem; background: #e4572e; color: #fff; }"), visualReviewWithoutBehaviour)
        .ImproveAsync(visualTarget, "追加ボタンをもっと大きくして、目立つ色にしてください");
    using (var visualManifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(visualTarget, "localstudio.json"))))
        Check(visualManifest.RootElement.GetProperty("blueprint").GetProperty("features")[1].GetString()!.StartsWith(VisualRequirementPolicy.Prefix, StringComparison.Ordinal) &&
              (await new StaticWebAppVerifier().VerifyAsync(visualTarget)).Passed,
            "visual-change-passes-without-javascript-evidence");
    var visualWithoutCssRejected = false;
    try
    {
        await ImproverWith(null,
            TestData.CandidateEnvelope(improvedBody, improvedScript), visualReviewWithoutBehaviour,
            TestData.CandidateEnvelope(improvedBody, improvedScript), visualReviewWithoutBehaviour,
            TestData.CandidateEnvelope(improvedBody, improvedScript), visualReviewWithoutBehaviour,
            TestData.CandidateEnvelope(improvedBody, improvedScript), visualReviewWithoutBehaviour,
            TestData.CandidateEnvelope(improvedBody, improvedScript), visualReviewWithoutBehaviour)
            .ImproveAsync(visualTarget, "見出しの文字を大きくして");
    }
    catch (InvalidDataException exception) { visualWithoutCssRejected = exception.Message.Contains("review.style-evidence", StringComparison.Ordinal); }
    Check(visualWithoutCssRejected, "visual-change-without-css-is-not-accepted");

    // Mechanical repair of the most common forbidden pattern before asking the model again.
    var autoFixed = GeneratedScriptAutoFix.Apply("list.innerHTML = '';\nstatus.innerHTML = message;\nif (a.innerHTML == b) {}\n");
    var markupKept = GeneratedScriptAutoFix.Apply("const html = '<li>' + text + '</li>';\nlist.innerHTML = html;\n");
    Check(autoFixed.Fixes == 2 && autoFixed.JavaScript.Contains("list.replaceChildren();", StringComparison.Ordinal) &&
          autoFixed.JavaScript.Contains("status.textContent = message;", StringComparison.Ordinal) &&
          autoFixed.JavaScript.Contains("a.innerHTML == b", StringComparison.Ordinal) &&
          markupKept.Fixes == 0,
        "auto-fix-only-applies-strictly-safer-rewrites");
    var rowFixed = GeneratedScriptAutoFix.Apply("row.innerHTML = `<td>${e.date}</td><td>${e.amount}円</td>`;\n");
    var attrKept = GeneratedScriptAutoFix.Apply("row.innerHTML = `<td class=\"x\">${e.date}</td>`;\n");
    Check(rowFixed.Fixes == 1 && rowFixed.JavaScript.Contains("row.replaceChildren(...[[\"td\", `${e.date}`], [\"td\", `${e.amount}円`]]", StringComparison.Ordinal) &&
          !rowFixed.JavaScript.Contains("innerHTML", StringComparison.Ordinal) && attrKept.Fixes == 0,
        "auto-fix-turns-simple-template-rows-into-elements");
    var newlineFixed = GeneratedScriptAutoFix.Apply("const lines = text.split('\n');\nconst ok = \"\n\";");
    Check(newlineFixed.JavaScript == "const lines = text.split('\\n');\nconst ok = \"\\n\";" && newlineFixed.Fixes == 2, "auto-fix-mends-a-quoted-line-break");
    var constFixed = GeneratedScriptAutoFix.Apply("const entries = load();\nconst total = 0;\nconst f = x => x;\nclear.addEventListener('click', () => { entries = []; if (total == 0) {} });");
    Check(constFixed.JavaScript.Contains("let entries = load();", StringComparison.Ordinal) && constFixed.JavaScript.Contains("const total = 0;", StringComparison.Ordinal) && constFixed.JavaScript.Contains("const f = x => x;", StringComparison.Ordinal),
        "auto-fix-turns-a-reassigned-const-into-let");
    var pdfFixed = GeneratedScriptAutoFix.Apply("const a = await pdfjsLib.getDocument({ data: bytes }).promise;\nconst b = await pdfjsLib.getDocument(buffer).promise;\nconst c = await pdfjsLib.getDocument({ data: x, isEvalSupported: false }).promise;\n").JavaScript;
    Check(pdfFixed.Contains("pdfjsLib.getDocument({ isEvalSupported: false, data: bytes })", StringComparison.Ordinal) &&
          pdfFixed.Contains("pdfjsLib.getDocument({ data: buffer, isEvalSupported: false })", StringComparison.Ordinal) &&
          pdfFixed.Contains("pdfjsLib.getDocument({ data: x, isEvalSupported: false })", StringComparison.Ordinal),
        "auto-fix-opens-pdfjs-documents-without-script-evaluation:" + pdfFixed);
    const string wholeDocument = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>t</title><style>#out { color: red; }</style></head>" +
        "<body><button id=\"go\">押す</button><p id=\"out\"></p><script>document.getElementById('go').addEventListener('click', () => {});</script></body></html>";
    var split = GeneratedDocumentSplitter.Apply(new GeneratedWebAppCandidate(wholeDocument, ""));
    var sameScriptTwice = GeneratedDocumentSplitter.Apply(new GeneratedWebAppCandidate(wholeDocument, "document.getElementById('go').addEventListener('click', () => {});"));
    var twoScripts = GeneratedDocumentSplitter.Apply(new GeneratedWebAppCandidate(wholeDocument, "console.log(1);"));
    var cdnKept = GeneratedDocumentSplitter.Apply(new GeneratedWebAppCandidate("<html><head><script src=\"https://cdn.example/x.js\"></script></head><body><button id=\"a\">a</button></body></html>", ""));
    var fragmentKept = GeneratedDocumentSplitter.Apply(new GeneratedWebAppCandidate("<button id=\"a\">a</button>", "x();"));
    var cdnStyleDropped = GeneratedDocumentSplitter.Apply(new GeneratedWebAppCandidate(
        "<html><head><link rel=\"stylesheet\" href=\"https://cdn.example/bootstrap.css\"></head><body><button id=\"a\">a</button></body></html>", "x();"));
    Check(cdnStyleDropped.Split && cdnStyleDropped.Candidate.Body == "<button id=\"a\">a</button>" &&
          !cdnStyleDropped.Candidate.Body.Contains("http", StringComparison.OrdinalIgnoreCase),
        "external-stylesheet-links-are-dropped-but-nothing-is-loaded-from-outside");
    var ownScriptTag = GeneratedDocumentSplitter.Apply(new GeneratedWebAppCandidate(
        "<!DOCTYPE html><html><body><button id=\"a\">a</button><script src=\"script.js\"></script></body></html>", "a.addEventListener('click', () => {});"));
    Check(ownScriptTag.Split && ownScriptTag.Candidate.Body == "<button id=\"a\">a</button>" &&
          ownScriptTag.Candidate.JavaScript == "a.addEventListener('click', () => {});" && twoScripts.Skipped == "two-different-scripts" &&
          cdnKept.Split && cdnKept.Candidate.Body == "<button id=\"a\">a</button>" && !cdnKept.Candidate.Body.Contains("cdn.example", StringComparison.Ordinal),
        "own-and-external-script-tags-are-dropped-and-skips-are-explained");
    Check(split.Split && split.Candidate.Body == "<button id=\"go\">押す</button><p id=\"out\"></p>" &&
          split.Candidate.JavaScript.Contains("addEventListener", StringComparison.Ordinal) && split.Candidate.Css == "#out { color: red; }" &&
          sameScriptTwice.Split && sameScriptTwice.Candidate.JavaScript == "document.getElementById('go').addEventListener('click', () => {});" &&
          !twoScripts.Split && !fragmentKept.Split,
        "whole-html-answers-are-split-mechanically-but-conflicting-scripts-are-left-for-rejection");
    var autoFixCapture = new CapturingJsonHandler(
        TestData.CandidateEnvelope(generatedBody, acceptedGeneratedScript.Replace("list.append(item);", "list.innerHTML = '';\n  list.append(item);", StringComparison.Ordinal)),
        TestData.ReviewEnvelope("LS-REQ-001", "notes", "list.append"));
    using (var autoFixEngine = new LocalWebAppGenerationEngine(generatedRuntime, new FakeSettingsStore(LocalStudioSettings.Default), new HttpClient(autoFixCapture)))
    {
        var autoFixedSource = await autoFixEngine.GenerateAsync(unsupportedBlueprint, StudioProjectKind.DesktopOrWebApp);
        Check(autoFixCapture.Bodies.Count == 2 &&
              autoFixedSource.Files.Single(file => file.RelativePath == "app.js").Content.Contains("list.replaceChildren();", StringComparison.Ordinal),
            "innerhtml-clearing-is-fixed-without-another-model-call");
    }

    // Background requirements (saving) have no screen element: UI evidence is not demanded for them (run 6 finding).
    var saveCandidate = new GeneratedWebAppCandidate(generatedBody, acceptedGeneratedScript + "\nlocalStorage.setItem('notes', '[]');\nlocalStorage.getItem('notes');");
    var saveRequirements = new[] { new GeneratedRequirement("LS-REQ-001", "付箋の自由配置"), new GeneratedRequirement("LS-REQ-002", "ブラウザ内での保存") };
    var saveReview = new[]
    {
        new GeneratedRequirementReview("LS-REQ-001", true, "notes", "list.append"),
        new GeneratedRequirementReview("LS-REQ-002", true, "", "localStorage.setItem")
    };
    Check(LocalWebAppGenerationEngine.ValidateReview(saveReview, saveCandidate, saveRequirements).Count == 0 &&
          LocalWebAppGenerationEngine.ValidateReview([saveReview[0], saveReview[0] with { Id = "LS-REQ-002", UiEvidence = "" }], saveCandidate,
              [saveRequirements[0], new GeneratedRequirement("LS-REQ-002", "付箋の削除")]).Contains("review.ui-evidence:LS-REQ-002") &&
          !RequirementEvidencePolicy.NeedsUiEvidence("ブラウザ内での保存") && RequirementEvidencePolicy.NeedsUiEvidence("メモの一覧表示"),
        "background-requirements-need-no-screen-element-evidence");

    // App Spec (step 4 draft): each golden app described in the small language, compiled deterministically, and held to
    // the same candidate rules, mechanical evidence, scaffolder verification and golden checks as model-written code.
    async Task<string?> CompileGoldenAsync(string caseId, string name, string[] features, AppSpec spec)
    {
        var blueprint = BlueprintValidator.Validate(new StudioBlueprint(name, name + "（App Spec）", "app", features, [new("実装", "作る")], [], [], ["workspace.read", "workspace.write"]));
        var requirements = LocalWebAppGenerationEngine.RequirementsFor(blueprint);
        var specErrors = AppSpecCompiler.Validate(spec, requirements);
        if (specErrors.Count > 0) return caseId + ":spec:" + string.Join("|", specErrors);
        var compiled = AppSpecCompiler.Compile(spec, requirements);
        var candidateErrors = LocalWebAppGenerationEngine.ValidateCandidate(compiled.Candidate, blueprint, requirements)
            .Concat(LocalWebAppGenerationEngine.ValidateReview(compiled.Evidence, compiled.Candidate, requirements)).ToArray();
        if (candidateErrors.Length > 0) return caseId + ":candidate:" + string.Join("|", candidateErrors);
        var source = new GeneratedProjectSource(LocalWebAppGenerationEngine.PackageFiles(blueprint, compiled.Candidate, requirements, compiled.Evidence), "generated-local-web-app");
        if (caseId == "counter")
        {
            // A generator that ran out of attempts hands its last candidate to the repair, which works in staging.
            var broken = source with { Files = source.Files.Select(file => file.RelativePath == "app.js" ? file with { Content = file.Content + "\n(" } : file).ToArray() };
            var repairs = 0;
            var repairing = new ProjectScaffolder(conversationPaths, generativeGenerator: new ExhaustedGenerator(broken))
            {
                Repair = async (staging, _, _, findings, _, token) =>
                {
                    repairs++;
                    var js = Path.Combine(staging, "app.js");
                    await File.WriteAllTextAsync(js, (await File.ReadAllTextAsync(js, token)).TrimEnd('(', '\n') + "\n", token);
                    return File.Exists(Path.Combine(staging, "localstudio.json")) && findings.SequenceEqual(["broken script"]);
                }
            };
            var repaired = await repairing.CreateAsync(blueprint, StudioProjectKind.DesktopOrWebApp);
            var refused = false;
            try { await new ProjectScaffolder(conversationPaths, generativeGenerator: new ExhaustedGenerator(broken)) { Repair = (_, _, _, _, _, _) => Task.FromResult(false) }.CreateAsync(blueprint, StudioProjectKind.DesktopOrWebApp); }
            catch (InvalidDataException failed) when (failed.InnerException is GenerationExhaustedException) { refused = true; }
            Check(repairs == 1 && refused && File.ReadAllText(Path.Combine(repaired, "localstudio.json")).Contains("\"repairedByAgent\": true", StringComparison.Ordinal) &&
                  !File.ReadAllText(Path.Combine(repaired, "app.js")).TrimEnd().EndsWith('('),
                "exhausted-candidate-is-repaired-in-staging-or-dropped");
        }
        string project;
        try { project = await new ProjectScaffolder(conversationPaths, generativeGenerator: new FixedGenerativeProjectGenerator(source)).CreateAsync(blueprint, StudioProjectKind.DesktopOrWebApp); }
        catch (InvalidDataException exception) { return caseId + ":verifier:" + exception.Message; }
        var misses = LocalStudioBenchmark.CheckGolden(LocalStudioBenchmark.WebCases.Single(item => item.Id == caseId), project);
        return misses.Count == 0 ? null : caseId + ":golden:" + string.Join("|", misses);
    }
    var specFailures = new List<string?>
    {
        await CompileGoldenAsync("memo", "メモ帳", ["メモを1行ずつ追加", "メモの一覧表示", "ブラウザ内での保存"], new AppSpec(
            [new("draft", "text"), new("memos", "list", Persist: true)],
            [new("memo-input", "text-input", Label: "メモ", Bind: "draft"), new("add", "button", Label: "追加", Action: "addMemo"),
             new("memo-list", "list", Source: "memos", ItemAction: new("削除", "removeMemo")), new("total", "text", Template: "{memos.count}件")],
            [new("addMemo", [new("append", "memos", "$draft"), new("set", "draft", "")]), new("removeMemo", [new("removeAt", "memos")])],
            [new("LS-REQ-001", "add", "addMemo"), new("LS-REQ-002", "memo-list", "addMemo"), new("LS-REQ-003", null, null)],
            [new("add", Size: "large", Color: "accent")])),
        await CompileGoldenAsync("counter", "カウンター", ["ボタンで数を1ずつ増やす", "リセット"], new AppSpec(
            [new("count", "number")],
            [new("count-view", "text", Template: "{count}"), new("plus", "button", Label: "+1", Action: "increment"), new("reset", "button", Label: "リセット", Action: "resetCount")],
            [new("increment", [new("add", "count", "1")]), new("resetCount", [new("set", "count", "0")])],
            [new("LS-REQ-001", "plus", "increment"), new("LS-REQ-002", "reset", "resetCount")])),
        await CompileGoldenAsync("character-count", "文字数カウンター", ["入力した文章の文字数をリアルタイム表示", "文章を空に戻すボタン"], new AppSpec(
            [new("text", "text")],
            [new("text-area", "textarea", Label: "文章", Bind: "text"), new("count-view", "text", Template: "{text.length}文字"), new("empty", "button", Label: "空にする", Action: "emptyText")],
            [new("emptyText", [new("set", "text", "")])],
            [new("LS-REQ-001", "count-view", "render"), new("LS-REQ-002", "empty", "emptyText")])),
        await CompileGoldenAsync("dice", "サイコロ", ["ボタンでサイコロを振る", "1から6の目をランダムに表示"], new AppSpec(
            [new("face", "number")],
            [new("roll", "button", Label: "振る", Action: "roll"), new("face-view", "text", Template: "出た目: {face}")],
            [new("roll", [new("random", "face", Min: 1, Max: 6)])],
            [new("LS-REQ-001", "roll", "roll"), new("LS-REQ-002", "face-view", "roll")])),
        await CompileGoldenAsync("stopwatch", "ストップウォッチ", ["スタート", "ストップ", "リセット"], new AppSpec(
            [new("seconds", "number")],
            [new("time-view", "text", Template: "{seconds} 秒"), new("start", "button", Label: "スタート", Action: "startWatch"),
             new("stop", "button", Label: "ストップ", Action: "stopWatch"), new("reset", "button", Label: "リセット", Action: "resetWatch")],
            [new("startWatch", [new("startTimer", "seconds")]), new("stopWatch", [new("stopTimer", "seconds")]),
             new("resetWatch", [new("stopTimer", "seconds"), new("set", "seconds", "0")])],
            [new("LS-REQ-001", "start", "startWatch"), new("LS-REQ-002", "stop", "stopWatch"), new("LS-REQ-003", "reset", "resetWatch")])),
        await CompileGoldenAsync("bmi", "BMI計算", ["身長と体重の入力", "BMIの計算と表示"], new AppSpec(
            [new("height", "number"), new("weight", "number"), new("bmi", "number")],
            [new("height-input", "number-input", Label: "身長(cm)", Bind: "height"), new("weight-input", "number-input", Label: "体重(kg)", Bind: "weight"),
             new("calc", "button", Label: "計算", Action: "calculate"), new("result", "text", Template: "BMI: {bmi}")],
            [new("calculate", [new("compute", "bmi", Expression: "round(weight / ((height / 100) * (height / 100)), 1)")])],
            [new("LS-REQ-001", "height-input", "calculate"), new("LS-REQ-002", "result", "calculate")]))
    }.Where(failure => failure is not null).ToArray();
    Check(specFailures.Length == 0, "app-spec-compiles-all-golden-apps-through-the-independent-verifier:" + string.Join(" / ", specFailures));
    // Spec-first generation route: one small model answer is compiled; a spec that cannot be compiled falls back to free generation.
    var counterBlueprint = BlueprintValidator.Validate(new StudioBlueprint("カウンター", "数を数える", "app", ["ボタンで数を1ずつ増やす", "リセット"], [new("実装", "作る")], [], [], ["workspace.read", "workspace.write"]));
    var counterSpecJson = JsonSerializer.Serialize(new AppSpec(
        [new("count", "number")],
        [new("count-view", "text", Template: "{count}"), new("plus", "button", Label: "+1", Action: "increment"), new("reset", "button", Label: "リセット", Action: "resetCount")],
        [new("increment", [new("add", "count", "1")]), new("resetCount", [new("set", "count", "0")])],
        [new("LS-REQ-001", "plus", "increment"), new("LS-REQ-002", "reset", "resetCount")]));
    string Envelope(string content) => JsonSerializer.Serialize(new { message = new { content } });
    var specRoute = new SequenceJsonHandler(Envelope(counterSpecJson));
    using (var specEngine = new LocalWebAppGenerationEngine(generatedRuntime, new FakeSettingsStore(LocalStudioSettings.Default), new HttpClient(specRoute)) { PreferAppSpec = true })
    {
        var fromSpec = await specEngine.GenerateAsync(counterBlueprint, StudioProjectKind.DesktopOrWebApp);
        Check(specRoute.RequestCount == 1 && fromSpec.Files.Any(file => file.RelativePath == LocalWebAppGenerationEngine.AppSpecFileName) &&
              fromSpec.Files.Single(file => file.RelativePath == "app.js").Content.Contains("function increment(", StringComparison.Ordinal),
            "spec-route-builds-an-app-from-one-small-model-answer");
    }
    var fallbackRoute = new SequenceJsonHandler(
        Envelope("{\"state\":[],\"ui\":[],\"actions\":[],\"requirements\":[]}"),
        Envelope("not json"),
        Envelope("not json either"),
        TestData.CandidateEnvelope(generatedBody, acceptedGeneratedScript),
        TestData.ReviewEnvelope("LS-REQ-001", "notes", "list.append"));
    using (var fallbackEngine = new LocalWebAppGenerationEngine(generatedRuntime, new FakeSettingsStore(LocalStudioSettings.Default), new HttpClient(fallbackRoute)) { PreferAppSpec = true })
    {
        var fallback = await fallbackEngine.GenerateAsync(unsupportedBlueprint, StudioProjectKind.DesktopOrWebApp);
        Check(fallbackRoute.RequestCount == 5 && fallback.Files.All(file => file.RelativePath != LocalWebAppGenerationEngine.AppSpecFileName),
            "spec-route-falls-back-to-free-generation");
    }

    // Small model slips seen on the real device (2026-10-09): JS-style expressions and mappings that name the button.
    var sloppySpec = new AppSpec(
        [new("count", "number")],
        [new("count-view", "text", Template: "{count}"), new("plus", "button", Label: "+1", Action: "incrementCounter"), new("reset", "button", Label: "リセット", Action: "resetCount")],
        [new("incrementCounter", [new("compute", "count", Expression: "state.count++;")]), new("resetCount", [new("reset", "count")])],
        [new("LS-REQ-001", "plus", null), new("LS-REQ-002", null, "resetCount")]);
    var sloppyRequirements = new[] { new GeneratedRequirement("LS-REQ-001", "ボタンで数を1ずつ増やす"), new GeneratedRequirement("LS-REQ-002", "リセット") };
    var normalizedSpec = AppSpecCompiler.Normalize(sloppySpec);
    Check(AppSpecCompiler.Validate(sloppySpec, sloppyRequirements).Any(error => error.StartsWith("spec.expression:incrementCounter(", StringComparison.Ordinal)) &&
          AppSpecCompiler.Validate(normalizedSpec, sloppyRequirements).Count == 0 &&
          normalizedSpec.Actions[0].Steps[0].Expression == "count + 1" && normalizedSpec.Requirements[1].Ui == "reset" &&
          AppSpecCompiler.Compile(normalizedSpec, sloppyRequirements).Candidate.JavaScript.Contains("Number(state.count) + 1", StringComparison.Ordinal),
        "app-spec-normalizes-small-model-slips:" + string.Join("|", AppSpecCompiler.Validate(normalizedSpec, sloppyRequirements)));
    // Slips seen with qwen2.5-coder:1.5b (2026-10-09): camelCase ids, an action named "save", "$<element id>" values,
    // and a list declared as text.
    var tinySpec = new AppSpec(
        [new("memos", "text", true), new("memoText", "text")],
        [new("memoInput", "text-input", Label: "メモ", Bind: "memoText"), new("addMemoButton", "button", Label: "追加", Action: "addMemo"),
         new("memoList", "list", Source: "memos"), new("saveButton", "button", Label: "保存", Action: "save")],
        [new("addMemo", [new("append", "memos", "$memoInput")]), new("save", [new("clear", "memoText")])],
        [new("LS-REQ-001", "addMemoButton", "addMemo"), new("LS-REQ-002", "saveButton", "save")],
        [new("memoList", "large")]);
    var tinyRequirements = new[] { new GeneratedRequirement("LS-REQ-001", "メモを追加できる"), new GeneratedRequirement("LS-REQ-002", "保存ボタンで入力欄を空にする") };
    var tinyNormalized = AppSpecCompiler.Normalize(tinySpec);
    var tinyBefore = AppSpecCompiler.Validate(tinySpec, tinyRequirements);
    Check(tinyBefore.Contains("spec.ui:memoInput") && tinyBefore.Contains("spec.action:save") &&
          AppSpecCompiler.Validate(tinyNormalized, tinyRequirements).Count == 0 &&
          tinyNormalized.Ui[0].Id == "memo-input" && tinyNormalized.Ui[3].Action == "saveAction" && tinyNormalized.Requirements[1].Action == "saveAction" &&
          tinyNormalized.Actions[0].Steps[0].Value == "$memoText" && tinyNormalized.State[0].Type == "list" && tinyNormalized.State[1].Type == "text" &&
          tinyNormalized.Style![0].Target == "memo-list",
        "app-spec-normalizes-tiny-model-names:" + string.Join("|", AppSpecCompiler.Validate(tinyNormalized, tinyRequirements)));
    // A character counter reacts while typing and needs no button; "text.length" is a valid expression.
    var typingSpec = new AppSpec(
        [new("text", "text"), new("chars", "number")],
        [new("body-text", "textarea", Label: "文章", Bind: "text"), new("chars-view", "text", Template: "{text.length}文字")],
        [new("measure", [new("compute", "chars", Expression: "text.length")])],
        [new("LS-REQ-001", "chars-view", "render")]);
    var typingRequirements = new[] { new GeneratedRequirement("LS-REQ-001", "入力した文字数を表示する") };
    Check(AppSpecCompiler.Validate(typingSpec, typingRequirements).Count == 0 &&
          AppSpecCompiler.Compile(typingSpec, typingRequirements).Candidate.JavaScript.Contains("[...String(state.text)].length", StringComparison.Ordinal),
        "app-spec-allows-typing-only-apps-and-length-expressions:" + string.Join("|", AppSpecCompiler.Validate(typingSpec, typingRequirements)));

    var unsafeSpec = new AppSpec(
        [new("count", "number")],
        [new("go", "button", Label: "実行", Action: "run")],
        [new("open", [new("add", "count")]), new("run", [new("compute", "count", Expression: "alert(1)")])],
        [new("LS-REQ-001", "go", "run")]);
    var unsafeSpecErrors = AppSpecCompiler.Validate(unsafeSpec, [new GeneratedRequirement("LS-REQ-001", "実行する")]);
    Check(unsafeSpecErrors.Contains("spec.action:open") && unsafeSpecErrors.Any(error => error.StartsWith("spec.expression:run(", StringComparison.Ordinal)),
        "app-spec-rejects-browser-globals-and-free-expressions:" + string.Join("|", unsafeSpecErrors));

    // Phase 3-3 groundwork: GAS static checks (scopes match the services used) and the full / diff hand-over zips.
    var gasRoot = Path.Combine(conversationPaths.ProjectRoot, "gas-app");
    Directory.CreateDirectory(gasRoot);
    async Task<ProjectVerificationResult> VerifyGasAsync(string code, string[] scopes, bool approved = false)
    {
        await File.WriteAllTextAsync(Path.Combine(gasRoot, "Code.gs"), code);
        await File.WriteAllTextAsync(Path.Combine(gasRoot, GasStaticVerifier.ManifestFileName),
            JsonSerializer.Serialize(new { timeZone = "Asia/Tokyo", runtimeVersion = "V8", oauthScopes = scopes }));
        return await GasStaticVerifier.VerifyAsync(gasRoot, approved);
    }
    const string sheetScope = "https://www.googleapis.com/auth/spreadsheets";
    const string mailScope = "https://www.googleapis.com/auth/script.send_mail";
    var gasOk = await VerifyGasAsync("function notify() { const sheet = SpreadsheetApp.getActiveSheet(); MailApp.sendEmail('a@example.com', 's', String(sheet.getLastRow())); }", [sheetScope, mailScope]);
    var gasMissing = await VerifyGasAsync("function run() { GmailApp.getInboxThreads(); SpreadsheetApp.getActiveSheet(); }", [sheetScope]);
    var gasUnneeded = await VerifyGasAsync("function run() { SpreadsheetApp.getActiveSheet(); }", [sheetScope, "https://www.googleapis.com/auth/drive"]);
    var gasNetwork = await VerifyGasAsync("function run() { UrlFetchApp.fetch('https://example.invalid'); }", ["https://www.googleapis.com/auth/script.external_request"]);
    var gasDynamic = await VerifyGasAsync("function run() { eval('1'); SpreadsheetApp.getActiveSheet(); }", [sheetScope]);
    Check(gasOk.Passed && gasMissing.Errors.Contains("gas.scopes-cover-used-services:GmailApp") &&
          gasUnneeded.Errors.Any(error => error.StartsWith("gas.no-unneeded-scopes", StringComparison.Ordinal)) &&
          gasNetwork.Errors.Contains("gas.external-access-approved") &&
          (await VerifyGasAsync("function run() { UrlFetchApp.fetch('https://example.invalid'); }", ["https://www.googleapis.com/auth/script.external_request"], approved: true)).Passed &&
          gasDynamic.Errors.Contains("gas.no-dynamic-code"),
        "gas-static-check-matches-scopes-to-services:" + string.Join("|", gasOk.Errors));
    var gasBroken = await VerifyGasAsync("function run() { const sheet = SpreadsheetApp.getActiveSheet(; }", [sheetScope]);
    Check(!gasBroken.Passed && gasBroken.Errors.Any(error => error.StartsWith("gas.syntax:Code.gs:", StringComparison.Ordinal)) &&
          gasOk.PassedChecks.Contains("gas.syntax-valid"),
        "gas-script-that-does-not-parse-is-refused:" + string.Join("|", gasBroken.Errors));

    await File.WriteAllTextAsync(Path.Combine(gasRoot, "Code.gs"), "function main() { SpreadsheetApp.getActiveSheet(); }\n");
    await File.WriteAllTextAsync(Path.Combine(gasRoot, "Util.gs"), "function helper() { return 1; }\n");
    var gasV1 = await ProjectVersionStore.EnsureInitialAsync(gasRoot);
    await File.WriteAllTextAsync(Path.Combine(gasRoot, "Code.gs"), "function main() { SpreadsheetApp.getActiveSheet().appendRow([1]); }\n");
    File.Delete(Path.Combine(gasRoot, "Util.gs"));
    await File.WriteAllTextAsync(Path.Combine(gasRoot, "Page.html"), "<p>hello</p>\n");
    await ProjectVersionStore.AddAsync(gasRoot, gasV1, "improved", "test", null, CancellationToken.None);
    var handover = Path.Combine(conversationRoot, "handover");
    var fullZip = await GasPackageBuilder.BuildFullZipAsync(gasRoot, Path.Combine(handover, "gas-full.zip"));
    var diffZip = await GasPackageBuilder.BuildDiffZipAsync(gasRoot, 1, 2, Path.Combine(handover, "gas-v1-to-v2.zip"));
    string[] fullEntries, diffEntries;
    string changesText;
    using (var archive = ZipFile.OpenRead(fullZip)) fullEntries = archive.Entries.Select(entry => entry.FullName).OrderBy(name => name, StringComparer.Ordinal).ToArray();
    using (var archive = ZipFile.OpenRead(diffZip))
    {
        diffEntries = archive.Entries.Select(entry => entry.FullName).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        using var reader = new StreamReader(archive.GetEntry("CHANGES.md")!.Open());
        changesText = reader.ReadToEnd();
    }
    var intoProjectBlocked = false;
    try { await GasPackageBuilder.BuildFullZipAsync(gasRoot, Path.Combine(gasRoot, "self.zip")); } catch (InvalidOperationException) { intoProjectBlocked = true; }
    Check(fullEntries.SequenceEqual(["CHANGES.md", "Code.gs", "Page.html", "appsscript.json"]) &&
          diffEntries.SequenceEqual(["CHANGES.md", "Code.gs", "DELETED.txt", "Page.html"]) &&
          changesText.Contains("置き換え: Code.gs", StringComparison.Ordinal) && changesText.Contains("新規作成: Page.html", StringComparison.Ordinal) &&
          changesText.Contains("削除: Util.gs", StringComparison.Ordinal) && intoProjectBlocked,
        "gas-full-and-diff-zips-carry-step-by-step-instructions:" + string.Join(",", diffEntries));

    // Phase 3-3: GAS is created by the local model (only .gs/.html), LocalStudio writes appsscript.json from the services
    // the code uses, checks it statically, versions improvements and hands over full and diff zips.
    StudioBlueprint GasBlueprint(params string[] capabilities) => new("Dedupe", "重複行を削除する", "app", ["メニューから重複行を削除する"],
        [new BlueprintStep("作成", "削除できる")], [], [], capabilities);
    Check(ImplementationSupportPolicy.Evaluate(GasBlueprint("account.authorize"), StudioProjectKind.GoogleAppsScript) is { Route: ProjectImplementationRoute.GeneratedAppsScript, CanCreate: true } &&
          !ImplementationSupportPolicy.Evaluate(GasBlueprint("network.fetch"), StudioProjectKind.GoogleAppsScript).CanCreate &&
          !BlueprintPermissionPolicy.Derive(GasBlueprint("account.authorize", "network.fetch"), StudioProjectKind.GoogleAppsScript, "シートの重複を消して").Capabilities.Contains("network.fetch") &&
          BlueprintPermissionPolicy.Derive(GasBlueprint("network.fetch"), StudioProjectKind.GoogleAppsScript, "天気APIから取得してシートに書く").Capabilities.Contains("network.fetch") &&
          ProjectFilePolicy.IsCartridgeEligible("Code.gs"),
        "gas-route-is-creatable-for-google-only-work-and-gs-is-a-source-file");

    const string dedupeCode = "function onOpen() {\n  SpreadsheetApp.getUi().createMenu('整理').addItem('重複を削除', 'removeDuplicates').addToUi();\n}\n\n" +
        "function removeDuplicates() {\n  const sheet = SpreadsheetApp.getActiveSheet();\n  const values = sheet.getDataRange().getValues();\n  const seen = new Set();\n" +
        "  for (let row = values.length; row >= 1; row--) {\n    const key = String(values[row - 1][0]);\n    if (seen.has(key)) sheet.deleteRow(row); else seen.add(key);\n  }\n}\n";
    string GasEnvelope(string code, string function, params (string Name, string Content)[] extra) => JsonSerializer.Serialize(new
    {
        message = new
        {
            content = JsonSerializer.Serialize(new
            {
                files = new[] { new { name = "Code.gs", content = code } }.Concat(extra.Select(item => new { name = item.Name, content = item.Content })).ToArray(),
                // An improvement adds "変更: …" as the next requirement, so both ids are mapped.
                requirements = new[] { new { id = "LS-REQ-001", function }, new { id = "LS-REQ-002", function } }
            })
        }
    });
    var gasRequirements = new[] { new GeneratedRequirement("LS-REQ-001", "メニューから重複行を削除する") };
    GasCandidate Candidate(string code, string function, params GasGeneratedFile[] extra) =>
        new([new GasGeneratedFile("Code.gs", code), .. extra], [new GasRequirementEvidence("LS-REQ-001", function)]);
    Check(LocalAppsScriptGenerationEngine.Validate(Candidate(dedupeCode, "removeDuplicates"), gasRequirements).Count == 0 &&
          LocalAppsScriptGenerationEngine.Validate(Candidate(dedupeCode, "missing"), gasRequirements).Contains("gas.requirement-function:LS-REQ-001") &&
          LocalAppsScriptGenerationEngine.Validate(Candidate("function run() { UrlFetchApp.fetch('https://example.invalid'); }\n", "run"), gasRequirements).Contains("gas.external-access") &&
          LocalAppsScriptGenerationEngine.Validate(Candidate("function run() { if (true) {\n}\n", "run"), gasRequirements).Contains("gas.unbalanced-braces") &&
          LocalAppsScriptGenerationEngine.Validate(Candidate("function run() { HtmlService.createHtmlOutputFromFile('Page'); }\n", "run", new GasGeneratedFile("Page.html", "<script src=\"https://cdn.example/x.js\"></script>")), gasRequirements).Contains("gas.html-no-external") &&
          LocalAppsScriptGenerationEngine.Validate(Candidate("function run() {}\n", "run", new GasGeneratedFile("Page.html", "<p>x</p>")), gasRequirements).Contains("gas.html-unused") &&
          LocalAppsScriptGenerationEngine.Validate(Candidate("function run() {}\n", "run", new GasGeneratedFile("appsscript.json", "{}")), gasRequirements).Contains("gas.file-name"),
        "gas-candidate-checks-requirements-network-structure-and-html");
    // 1.5B slips (2026-10-09): appsscript.json returned, unused HTML attached, only the first requirement mapped.
    var sixRequirements = Enumerable.Range(1, 6).Select(index => new GeneratedRequirement($"LS-REQ-{index:000}", "機能" + index)).ToArray();
    var withHelper = "function onOpen() {\n  SpreadsheetApp.getUi().createMenu('整理').addItem('実行', 'removeDuplicates').addToUi();\n}\n\n" +
                     "function removeDuplicates() {\n  const rows = readRows();\n  return rows.length;\n}\n\nfunction readRows() {\n  return SpreadsheetApp.getActiveSheet().getDataRange().getValues();\n}\n";
    var sloppyGas = new GasCandidate(
        [new GasGeneratedFile("Code.gs", withHelper), new GasGeneratedFile("appsscript.json", "{}"), new GasGeneratedFile("Sidebar.html", "<p>x</p>")],
        [new GasRequirementEvidence("LS-REQ-001", "removeDuplicates()")]);
    var normalizedGas = LocalAppsScriptGenerationEngine.Normalize(sloppyGas, sixRequirements);
    var twoEntries = LocalAppsScriptGenerationEngine.Normalize(
        new GasCandidate([new GasGeneratedFile("Code.gs", "function a() {}\nfunction b() {}\n")], []), sixRequirements);
    Check(normalizedGas.Candidate.Files.Count == 1 && normalizedGas.Candidate.Requirements.All(item => item.Function == "removeDuplicates") &&
          normalizedGas.Candidate.Requirements.Count == 6 && LocalAppsScriptGenerationEngine.Validate(normalizedGas.Candidate, sixRequirements).Count == 0 &&
          twoEntries.Candidate.Requirements.Count == 0,
        "gas-normalize-fixes-only-unambiguous-slips:" + string.Join("|", LocalAppsScriptGenerationEngine.Validate(normalizedGas.Candidate, sixRequirements)));
    const string forwardDelete = "function run() {\n  const sheet = SpreadsheetApp.getActiveSheet();\n  const data = sheet.getDataRange().getValues();\n  for (var i = 1; i < data.length; i++) {\n    if (data[i][0] === '') { sheet.deleteRow(i); }\n  }\n}\n";
    Check(LocalAppsScriptGenerationEngine.DeletesRowsWhileWalkingForward(forwardDelete) &&
          !LocalAppsScriptGenerationEngine.DeletesRowsWhileWalkingForward(dedupeCode) &&
          LocalAppsScriptGenerationEngine.Validate(Candidate(forwardDelete, "run"), gasRequirements).Contains("gas.delete-rows-forward"),
        "gas-deleting-rows-in-an-upward-loop-is-rejected");
    // The attached Google app comes from the user's words (a 1.5B model wrote spreadsheet code for a Docs request 4/4 times).
    var docContainer = LocalAppsScriptGenerationEngine.ContainerFor("Googleドキュメントの末尾に今日の日付の見出しを追加するGAS");
    Check(docContainer?.Service == "DocumentApp" &&
          LocalAppsScriptGenerationEngine.ContainerFor("Googleカレンダーの予定を、スプレッドシートに書き出す")?.Service == "SpreadsheetApp" &&
          LocalAppsScriptGenerationEngine.ContainerFor("毎朝Gmailを整理する") is null &&
          LocalAppsScriptGenerationEngine.Validate(Candidate(dedupeCode, "removeDuplicates"), gasRequirements, docContainer).Contains("gas.container:DocumentApp") &&
          LocalAppsScriptGenerationEngine.Validate(Candidate("function addHeading() {\n  DocumentApp.getActiveDocument().getBody().appendParagraph('見出し');\n}\n", "addHeading"), gasRequirements, docContainer).Count == 0,
        "gas-attached-app-is-decided-from-the-request-and-enforced");
    // 1.5B planner put permission names into features in all 8 GAS runs (2026-10-09).
    var permissionFeatures = LocalPlanningEngine.WithBehaviourFeatures(
        GasBlueprint() with { Features = ["workspace.read", "network.fetch", "account.authorize"] },
        "Googleドキュメントの末尾に今日の日付の見出しを追加するGAS。箇条書きも付けて。");
    var mixedFeatures = LocalPlanningEngine.WithBehaviourFeatures(GasBlueprint() with { Features = ["workspace.write", "見出しを追加する"] }, "x");
    Check(permissionFeatures.Features.SequenceEqual(["Googleドキュメントの末尾に今日の日付の見出しを追加するGAS", "箇条書きも付けて"]) &&
          mixedFeatures.Features.SequenceEqual(["見出しを追加する"]),
        "planner-features-are-behaviour-not-permission-names:" + string.Join("|", permissionFeatures.Features));
    var derivedManifest = GasStaticVerifier.ManifestFor(dedupeCode + "\nfunction later() { CalendarApp.getDefaultCalendar(); }\n");
    Check(derivedManifest.Contains("\"https://www.googleapis.com/auth/spreadsheets\"", StringComparison.Ordinal) &&
          derivedManifest.Contains("\"https://www.googleapis.com/auth/script.container.ui\"", StringComparison.Ordinal) &&
          derivedManifest.Contains("\"https://www.googleapis.com/auth/calendar\"", StringComparison.Ordinal) &&
          !derivedManifest.Contains("auth/drive", StringComparison.Ordinal) && derivedManifest.Contains("\"V8\"", StringComparison.Ordinal),
        "gas-manifest-scopes-are-derived-from-the-code");

    await using (var gasRuntime = new FakeLocalModelRuntime(new Uri("http://127.0.0.1:54321/")))
    {
        // The first answer reaches the internet (not fixable mechanically), so one repair round is needed.
        var gasResponses = new CapturingJsonHandler(
            GasEnvelope(dedupeCode + "\nfunction ping() { UrlFetchApp.fetch('https://example.invalid'); }\n", "removeDuplicates"),
            GasEnvelope(dedupeCode, "removeDuplicates"));
        using var gasEngine = new LocalAppsScriptGenerationEngine(gasRuntime, new FakeSettingsStore(LocalStudioSettings.Default), new HttpClient(gasResponses));
        var gasScaffolder = new ProjectScaffolder(conversationPaths, appsScriptGenerator: gasEngine);
        var gasProject = await gasScaffolder.CreateAsync(GasBlueprint("account.authorize"), StudioProjectKind.GoogleAppsScript, sourceRequest: "シートの重複行を消すGAS");
        var gasManifest = await File.ReadAllTextAsync(Path.Combine(gasProject, GasStaticVerifier.ManifestFileName));
        var gasCreated = await GasStaticVerifier.VerifyAsync(gasProject, externalAccessApproved: false);
        Check(gasResponses.Bodies.Count == 2 && gasResponses.Bodies[1].Contains("gas.external-access", StringComparison.Ordinal) &&
              File.Exists(Path.Combine(gasProject, "Code.gs")) && gasCreated.Passed &&
              gasManifest.Contains("auth/spreadsheets", StringComparison.Ordinal) && gasManifest.Contains("auth/script.container.ui", StringComparison.Ordinal) &&
              (await File.ReadAllTextAsync(Path.Combine(gasProject, "localstudio.json"))).Contains(LocalAppsScriptGenerationEngine.ImplementationMode, StringComparison.Ordinal),
            "gas-project-is-created-repaired-once-and-passes-static-checks:" + string.Join("|", gasCreated.Errors));

        const string renamedMenu = "function onOpen() {\n  SpreadsheetApp.getUi().createMenu('データ整理').addItem('重複を削除', 'removeDuplicates').addToUi();\n}\n\n";
        var improvedCode = renamedMenu + dedupeCode[dedupeCode.IndexOf("function removeDuplicates", StringComparison.Ordinal)..];
        using var improveEngine = new LocalAppsScriptGenerationEngine(gasRuntime, new FakeSettingsStore(LocalStudioSettings.Default),
            new HttpClient(new SequenceJsonHandler(GasEnvelope(improvedCode, "removeDuplicates"))));
        var router = new KindRoutingImprover(new WebProjectImprover(new LocalWebAppGenerationEngine(gasRuntime, new FakeSettingsStore(LocalStudioSettings.Default))), new GasProjectImprover(improveEngine));
        var improvable = await router.CheckImprovableAsync(gasProject);
        var gasV2 = await router.ImproveAsync(gasProject, "メニュー名を「データ整理」にして");
        var handoverRoot = Path.Combine(conversationRoot, "gas-handover");
        var handed = await GasHandover.WriteAsync(gasProject, handoverRoot, gasV2.Version.Number, gasV2.Previous.Number);
        string[] gasDiffEntries;
        using (var archive = ZipFile.OpenRead(handed[1])) gasDiffEntries = archive.Entries.Select(entry => entry.FullName).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        Check(improvable is null && gasV2.Version.Number == 2 && gasV2.Previous.Number == 1 &&
              (await File.ReadAllTextAsync(Path.Combine(gasProject, "Code.gs"))).Contains("データ整理", StringComparison.Ordinal) &&
              handed.Count == 2 && handed[0].EndsWith("-v2-full.zip", StringComparison.Ordinal) && handed[1].EndsWith("-v1-to-v2-diff.zip", StringComparison.Ordinal) &&
              gasDiffEntries.SequenceEqual(["CHANGES.md", "Code.gs"]),
            "gas-improvement-is-versioned-and-handed-over-as-full-and-diff-zips:" + string.Join(",", gasDiffEntries));
    }

    // Local Studio benchmark: golden expectations are checked mechanically and runs are scored for this PC's priorities.
    var goldenProject = Path.Combine(conversationPaths.ProjectRoot, "golden-dice");
    Directory.CreateDirectory(goldenProject);
    File.WriteAllText(Path.Combine(goldenProject, "index.html"), "<main><button id=\"roll\">振る</button><p id=\"face\"></p></main>");
    File.WriteAllText(Path.Combine(goldenProject, "app.js"), "document.getElementById('roll').addEventListener('click', () => { face.textContent = 1 + Math.floor(Math.random() * 6); });");
    var dice = LocalStudioBenchmark.WebCases.Single(item => item.Id == "dice");
    var stopwatch = LocalStudioBenchmark.WebCases.Single(item => item.Id == "stopwatch");
    Check(LocalStudioBenchmark.CheckGolden(dice, goldenProject).Count == 0 &&
          LocalStudioBenchmark.CheckGolden(stopwatch, goldenProject).Any(miss => miss.Contains("setInterval", StringComparison.Ordinal)) &&
          LocalStudioBenchmark.WebCases.Count >= 6 &&
          LocalStudioBenchmark.WebCases.All(item => !BlueprintPermissionPolicy.AsksForExternalAccess(item.Request)),
        "golden-cases-are-checked-mechanically-and-stay-local");
    var perfect = new BenchmarkRun("a", true, true, [], 1, 0, 100, 3L << 30, 3L << 30, null);
    var failed = new BenchmarkRun("b", false, false, ["not-created"], 3, 0, 1000, 4L << 30, 400L << 20, "x");
    Check(LocalStudioBenchmark.Score([perfect]).Total == 100 && LocalStudioBenchmark.Score([failed]).Total == 0 &&
          LocalStudioBenchmark.Score([perfect, failed]).Total == 50,
        "benchmark-score-weights-quality-time-memory-repairs");
    // A requirement that needs a mechanism (sound, print, download, file input, chart, saving) is not met by its words
    // alone: the silent metronome of catalogue 2026-10-19 is refused, the one that makes a sound passes.
    var metronomeRequirements = new[] { new GeneratedRequirement("LS-REQ-002", "拍に合わせて音を鳴らす"), new GeneratedRequirement("LS-REQ-003", "テンポを表示する") };
    var silentMetronome = "setInterval(() => { beat.textContent = '拍'; }, 500);";
    var soundingMetronome = "start.addEventListener('click', () => { const ctx = new AudioContext(); setInterval(() => { const o = ctx.createOscillator(); o.connect(ctx.destination); o.start(); o.stop(ctx.currentTime + 0.05); }, 500); });";
    Check(LocalWebAppGenerationEngine.RequirementsWithoutMechanism(metronomeRequirements, "<p id=\"beat\"></p>", silentMetronome).SingleOrDefault()?.StartsWith("requirement.mechanism-missing:LS-REQ-002:sound", StringComparison.Ordinal) == true &&
          LocalWebAppGenerationEngine.RequirementsWithoutMechanism(metronomeRequirements, "<p id=\"beat\"></p>", soundingMetronome).Count == 0 &&
          LocalWebAppGenerationEngine.RequirementsWithoutMechanism([new GeneratedRequirement("LS-REQ-001", "請求書を印刷できる")], "<button id=\"p\">印刷</button>", "").Count == 1 &&
          LocalWebAppGenerationEngine.RequirementsWithoutMechanism([new GeneratedRequirement("LS-REQ-001", "請求書を印刷できる")], "", "p.addEventListener('click', () => window.print());").Count == 0,
        "requirements-needing-a-mechanism-are-not-met-by-words-alone");
    // "文字の削除" and "別名で保存" in a PDF editor are not about data kept in the browser.
    var pdfEditorContracts = LocalWebAppGenerationEngine.ValidateKnownBehaviorContracts("const pdf = await PDFLib.PDFDocument.load(bytes); link.download = 'edited.pdf';",
        [new GeneratedRequirement("LS-REQ-001", "文字の削除・書き換え"), new GeneratedRequirement("LS-REQ-002", "別名で保存")]);
    var memoContracts = LocalWebAppGenerationEngine.ValidateKnownBehaviorContracts("localStorage.setItem('m', JSON.stringify(memos)); memos = JSON.parse(localStorage.getItem('m'));",
        [new GeneratedRequirement("LS-REQ-001", "メモの保存"), new GeneratedRequirement("LS-REQ-002", "メモの削除")]);
    var memoFilterDelete = LocalWebAppGenerationEngine.ValidateKnownBehaviorContracts("memos = memos.filter((m, i) => i !== index); localStorage.setItem('m', JSON.stringify(memos)); localStorage.getItem('m');",
        [new GeneratedRequirement("LS-REQ-001", "メモの保存"), new GeneratedRequirement("LS-REQ-002", "メモの削除")]);
    Check(!pdfEditorContracts.Any(error => error.StartsWith("behavior.local", StringComparison.Ordinal)) && memoContracts.Contains("behavior.local-delete") && !memoFilterDelete.Contains("behavior.local-delete"),
        "browser-storage-contracts-apply-only-to-browser-storage");
    var soundReport = await WebAppBehaviorChecker.CheckPageAsync("<button id=\"start\">スタート</button><p id=\"beat\"></p>", "document.getElementById('start').addEventListener('click', () => { const ctx = new AudioContext(); const o = ctx.createOscillator(); o.connect(ctx.destination); o.start(); o.stop(ctx.currentTime + 0.05); new Audio('a.wav').play(); document.getElementById('beat').textContent = '拍'; });", []);
    Check(soundReport.LoadError is null && !soundReport.Problems.Any(problem => problem.Contains("not defined", StringComparison.Ordinal)), "behavior-check-accepts-sound-without-playing-it:" + string.Join("|", soundReport.Problems));
    var downloadReport = await WebAppBehaviorChecker.CheckPageAsync("<button id=\"save\">CSVを保存</button>", "document.getElementById('save').addEventListener('click', () => { const blob = new Blob(['a,b\\n1,2'], { type: 'text/csv' }); const link = document.createElement('a'); link.href = URL.createObjectURL(blob); link.download = 'clean.csv'; link.click(); URL.revokeObjectURL(link.href); });", []);
    Check(downloadReport.LoadError is null && !downloadReport.Problems.Any(problem => problem.Contains("constructor", StringComparison.OrdinalIgnoreCase) || problem.Contains("not defined", StringComparison.Ordinal)), "behavior-check-accepts-downloads-without-writing-files:" + string.Join("|", downloadReport.Problems));
    // Acceptance scenarios catch apps that run but do the wrong thing (catalogue 2026-10-20: a converter whose result was
    // its input, a metronome that sounded once).
    var converterScenario = new AcceptanceScenario("LS-REQ-001", "1kmをmに変換", [
        new AcceptanceStep(Set: "長さ", Value: "1"), new AcceptanceStep(Choose: "変換元", Option: "km"), new AcceptanceStep(Choose: "変換先", Option: "m"), new AcceptanceStep(Press: "変換")], ["1000"]);
    const string converterBody = "<label for=\"len\">長さ</label><input id=\"len\" type=\"number\"><label for=\"from\">変換元</label><select id=\"from\"><option>mm</option><option>m</option><option>km</option></select>" +
                                 "<label for=\"to\">変換先</label><select id=\"to\"><option>mm</option><option>m</option><option>km</option></select><button id=\"go\">変換</button><p id=\"out\"></p>";
    const string rates = "const r = { mm: 1, m: 1000, km: 1000000 };";
    var converterPage = $"<!doctype html><html><body><main id=\"app\">{converterBody}</main></body></html>";
    var sameUnit = await WebAppBehaviorChecker.CheckScenariosAsync(converterPage, rates + " document.getElementById('go').addEventListener('click', () => { const v = parseFloat(document.getElementById('len').value); const f = document.getElementById('from').value; const t = document.getElementById('from').value; document.getElementById('out').textContent = (v * r[f] / r[t]) + ' ' + t; });", [], [converterScenario]);
    var rightUnit = await WebAppBehaviorChecker.CheckScenariosAsync(converterPage, rates + " document.getElementById('go').addEventListener('click', () => { const v = parseFloat(document.getElementById('len').value); const f = document.getElementById('from').value; const t = document.getElementById('to').value; document.getElementById('out').textContent = (v * r[f] / r[t]) + ' ' + t; });", [], [converterScenario]);
    var noField = await WebAppBehaviorChecker.CheckScenariosAsync("<!doctype html><html><body><main id=\"app\"><button id=\"go\">変換</button></main></body></html>", "", [], [converterScenario]);
    var keptScenario = new AcceptanceScenario("LS-REQ-002", "開き直しても残る", [new AcceptanceStep(Set: "メモ", Value: "買い物"), new AcceptanceStep(Press: "追加"), new AcceptanceStep(Reload: true)], ["買い物"]);
    var memoPage = "<!doctype html><html><body><main id=\"app\"><input id=\"memo\" placeholder=\"メモ\"><button id=\"add\">追加</button><ul id=\"list\"></ul></main></body></html>";
    const string memoShow = "const show = () => { const l = document.getElementById('list'); l.replaceChildren(); items.forEach(t => { const li = document.createElement('li'); li.textContent = t; l.append(li); }); };";
    var notKept = await WebAppBehaviorChecker.CheckScenariosAsync(memoPage, "const items = []; " + memoShow + " document.getElementById('add').addEventListener('click', () => { items.push(document.getElementById('memo').value); show(); });", [], [keptScenario]);
    var kept = await WebAppBehaviorChecker.CheckScenariosAsync(memoPage, "const items = JSON.parse(localStorage.getItem('m') || '[]'); " + memoShow + " show(); document.getElementById('add').addEventListener('click', () => { items.push(document.getElementById('memo').value); localStorage.setItem('m', JSON.stringify(items)); show(); });", [], [keptScenario]);
    var oneBeat = await WebAppBehaviorChecker.CheckPageAsync("<!doctype html><html><body><main id=\"app\"><button id=\"start\">スタート</button><p id=\"beat\"></p></main></body></html>",
        "const ctx = new AudioContext(); const osc = ctx.createOscillator(); osc.connect(ctx.destination); document.getElementById('start').addEventListener('click', () => { setInterval(() => { osc.start(); osc.stop(ctx.currentTime + 0.05); document.getElementById('beat').textContent = '拍'; }, 500); });", []);
    Check(sameUnit.Count == 1 && sameUnit[0].StartsWith("acceptance.failed:1kmをmに変換", StringComparison.Ordinal) && rightUnit.Count == 0 &&
          noField.Count == 1 && noField[0].StartsWith("acceptance.control-missing", StringComparison.Ordinal) &&
          notKept.Count == 1 && kept.Count == 0 &&
          oneBeat.Problems.Any(problem => problem.Contains("start() can only be called once", StringComparison.Ordinal)),
        "acceptance-scenarios-catch-apps-that-run-but-do-the-wrong-thing:" + string.Join(" | ", sameUnit.Concat(rightUnit).Concat(noField).Concat(notKept).Concat(kept)) + " || " + string.Join("|", oneBeat.Problems));
    var pdfBlueprint = BlueprintValidator.Validate(new StudioBlueprint("PDFビューア", "PDFを表示", "app", ["PDFを表示する"], [new("実装", "作る")], [], [], []));
    var pdfRequirements = LocalWebAppGenerationEngine.RequirementsFor(pdfBlueprint);
    GeneratedWebAppCandidate PdfCandidate(string open) => new("<main><input id=\"file\" type=\"file\" aria-label=\"PDFを表示する\"><canvas id=\"page\"></canvas></main>",
        "document.getElementById('file').addEventListener('change', async (e) => { const bytes = new Uint8Array(await e.target.files[0].arrayBuffer()); const pdf = await " + open + ".promise; });");
    Check(LocalWebAppGenerationEngine.ValidateCandidate(PdfCandidate("pdfjsLib.getDocument({ data: bytes })"), pdfBlueprint, pdfRequirements).Any(error => error.StartsWith("library.pdfjs-unsafe", StringComparison.Ordinal)) &&
          !LocalWebAppGenerationEngine.ValidateCandidate(PdfCandidate("pdfjsLib.getDocument({ data: bytes, isEvalSupported: false })"), pdfBlueprint, pdfRequirements).Any(error => error.StartsWith("library.pdfjs-unsafe", StringComparison.Ordinal)),
        "pdfjs-documents-are-opened-without-script-evaluation");
    Check(!LibraryShelf.HasUnsafeLibraryCharacters("var t='\u0001\u0017\uFFFD';") && LibraryShelf.HasUnsafeLibraryCharacters("a\0b") && LibraryShelf.HasUnsafeLibraryCharacters("if (x) { /* \u202E */ }"),
        "shelf-libraries-may-hold-raw-tables-but-not-nul-or-bidi-controls");
    // Fonts go on the shelf as *.font.js (base64 under globalThis.LocalStudioFonts, licence in full as a comment).
    var fontScript = LibraryShelf.WrapFont([0, 1, 2, 250], "BIZ UDGothic", "SIL Open Font License 1.1 */ end");
    var fontFolder = Path.Combine(Path.GetTempPath(), "ls-font-shelf-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(fontFolder);
    var fontPath = Path.Combine(fontFolder, "BIZUDGothic-Regular.font.js");
    await File.WriteAllTextAsync(fontPath, fontScript);
    Check(fontScript.Contains("f[\"BIZ UDGothic\"] = \"AAEC+g==\"", StringComparison.Ordinal) && fontScript.Contains("SIL Open Font License 1.1 * / end", StringComparison.Ordinal) &&
          LibraryShelf.IsFontFile("x.font.js") && !LibraryShelf.IsFontFile("x.js") &&
          LibraryShelf.Check(new LibraryDraft("biz-udgothic", "gf-dd215603", "OFL-1.1", "google/fonts", LibraryShelf.FontGlobal, [fontPath], "使い方")).Count == 0 &&
          NpmLibrarySource.IsValidPackageName("@pdf-lib/fontkit") && NpmLibrarySource.ShelfName("@pdf-lib/fontkit") == "pdf-lib-fontkit" && !NpmLibrarySource.IsValidPackageName("@x/../y"),
        "fonts-and-scoped-packages-go-on-the-shelf");
    var catalogExcel = RequirementsCatalog.Cases.Single(item => item.Id == "excel-merge-50");
    var catalogScores = RequirementsCatalog.Score([
        new CatalogRun("a", "x", "w", "s", true, true, [], 1, 1, null), new CatalogRun("b", "x", "w", "s", false, false, ["not-created"], 1, 1, null),
        new CatalogRun("c", "y", "w", "s", true, true, [], 1, 1, null)]);
    Check(RequirementsCatalog.Cases.Select(item => item.Id).Distinct().Count() == RequirementsCatalog.Cases.Count &&
          RequirementsCatalog.Cases.Select(item => item.Category).Distinct().Count() >= 8 &&
          RequirementsCatalog.Cases.Any(item => item.Id == "desktop-suggest") && RequirementsCatalog.Cases.Any(item => item.Id == "pdf-editor") &&
          RequirementsCatalog.Gaps(catalogExcel).Count == 0 && RequirementsCatalog.Gaps(RequirementsCatalog.Cases.Single(item => item.Id == "pdf-editor")).Count == 0 &&
          RequirementsCatalog.Select("kind:WindowsApp").All(item => item.Kind == StudioProjectKind.WindowsApp) &&
          RequirementsCatalog.Select("size:Large").Count == 3 &&
          catalogScores[0].Category == "x" && catalogScores[0].PassRate == 50 && catalogScores[1].PassRate == 100,
        "requirements-catalogue-covers-office-and-other-apps-with-per-category-rates");
    // The catalogue's verdict is honest: a converter that converts is verified, one that echoes its input is wrong,
    // and an EXE that only compiled is unverified, never passed (audit 2026-10-20).
    var unitCase = RequirementsCatalog.Cases.Single(item => item.Id == "unit-convert");
    var judgeRoot = Path.Combine(conversationPaths.ProjectRoot, "catalog-judge");
    async Task<CatalogVerdict> JudgeWebAsync(string folder, string conversion)
    {
        var project = Path.Combine(judgeRoot, folder);
        Directory.CreateDirectory(project);
        const string units = "<option>mm</option><option>cm</option><option>m</option><option>km</option><option>g</option><option>kg</option>";
        await File.WriteAllTextAsync(Path.Combine(project, "index.html"),
            $"<!doctype html><html><body><div id=\"app\"><label for=\"v\">値</label><input id=\"v\"><label for=\"from-unit\">変換元</label><select id=\"from-unit\">{units}</select>" +
            $"<label for=\"to-unit\">変換先</label><select id=\"to-unit\">{units}</select><button id=\"go\">変換</button><p id=\"out\"></p></div><script src=\"app.js\"></script></body></html>");
        await File.WriteAllTextAsync(Path.Combine(project, "app.js"),
            "const f = { mm: 0.001, cm: 0.01, m: 1, km: 1000, g: 1, kg: 1000 };\n" +
            "document.getElementById('go').addEventListener('click', () => {\n const v = Number(document.getElementById('v').value);\n" +
            " const from = document.getElementById('from-unit').value;\n const to = document.getElementById('to-unit').value;\n" +
            $" document.getElementById('out').textContent = ({conversion}) + ' ' + to;\n}});\n");
        return await RequirementsCatalog.JudgeAsync(unitCase, project);
    }
    var convertsRight = await JudgeWebAsync("right", "v * f[from] / f[to]");
    var echoesInput = await JudgeWebAsync("echo", "v");
    var exeOnly = Path.Combine(judgeRoot, "exe");
    Directory.CreateDirectory(exeOnly);
    await File.WriteAllTextAsync(Path.Combine(exeOnly, "Program.cs"), "class F : Form { Button go = new Button(); F() { Controls.Add(go); go.Click += (s, e) => Convert(); } void Convert() { } }");
    var exeVerdict = await RequirementsCatalog.JudgeAsync(unitCase, exeOnly);
    var honestScores = RequirementsCatalog.Score([
        new CatalogRun("a", "x", "w", "s", true, true, [], 1, 1, null, CatalogVerdict.Verified), new CatalogRun("b", "x", "w", "s", true, false, [], 1, 1, null, CatalogVerdict.Unverified),
        new CatalogRun("c", "x", "w", "s", true, false, ["m"], 1, 1, null, CatalogVerdict.Wrong)]);
    Check(convertsRight.Verdict == CatalogVerdict.Verified && echoesInput.Verdict == CatalogVerdict.Wrong &&
          echoesInput.Misses.Any(miss => miss.StartsWith("acceptance.failed:", StringComparison.Ordinal)) &&
          exeVerdict.Verdict == CatalogVerdict.Unverified &&
          honestScores[0].Passed == 1 && honestScores[0].Unverified == 1 && honestScores[0].Wrong == 1 && Math.Abs(honestScores[0].PassRate - 33.3) < 0.01,
        "catalogue-verdict-is-verified-only-when-seen-working:" + convertsRight.Verdict + "/" + string.Join("|", convertsRight.Misses) + "/" + echoesInput.Verdict + "/" + exeVerdict.Verdict);
    // Days really pass for a streak: a tracker that counts days is verified, one that counts checked habits is wrong.
    var habitCase = RequirementsCatalog.Cases.Single(item => item.Id == "habit");
    async Task<CatalogVerdict> JudgeHabitAsync(string folder, string onDone, string shown)
    {
        var project = Path.Combine(judgeRoot, folder);
        Directory.CreateDirectory(project);
        await File.WriteAllTextAsync(Path.Combine(project, "index.html"),
            "<!doctype html><html><body><div id=\"app\"><label for=\"h\">習慣</label><input id=\"h\"><button id=\"add\">追加</button><ul id=\"list\"></ul></div><script src=\"app.js\"></script></body></html>");
        await File.WriteAllTextAsync(Path.Combine(project, "app.js"),
            "const list = document.getElementById('list');\nconst habits = JSON.parse(localStorage.getItem('habits') || '[]');\n" +
            "const today = () => new Date(Date.now()).toISOString().slice(0, 10);\n" +
            "function streak(days) { let n = 0; let d = new Date(today()); while (days.includes(d.toISOString().slice(0, 10))) { n++; d = new Date(d.getTime() - 86400000); } return n; }\n" +
            "function save() { localStorage.setItem('habits', JSON.stringify(habits)); }\n" +
            "function render() { list.replaceChildren(); habits.forEach(h => { const li = document.createElement('li'); const s = document.createElement('span'); s.textContent = " + shown + "; const b = document.createElement('button'); b.textContent = 'できた';\n" +
            " b.addEventListener('click', () => { " + onDone + " save(); render(); }); li.append(s, b); list.append(li); }); }\n" +
            "document.getElementById('add').addEventListener('click', () => { habits.push({ name: document.getElementById('h').value, days: [], done: false }); save(); render(); });\nrender();\n");
        return await RequirementsCatalog.JudgeAsync(habitCase, project);
    }
    // A chosen file and the file made for download are played too: a cleaner that keeps duplicates is wrong.
    var csvCase = RequirementsCatalog.Cases.Single(item => item.Id == "csv-clean");
    async Task<CatalogVerdict> JudgeCsvAsync(string folder, string read, string keep)
    {
        var project = Path.Combine(judgeRoot, folder);
        Directory.CreateDirectory(project);
        await File.WriteAllTextAsync(Path.Combine(project, "index.html"),
            "<!doctype html><html><body><div id=\"app\"><label for=\"csv\">CSVファイル</label><input type=\"file\" id=\"csv\"><button id=\"dl\">ダウンロード</button><pre id=\"out\"></pre></div><script src=\"app.js\"></script></body></html>");
        await File.WriteAllTextAsync(Path.Combine(project, "app.js"),
            "let cleaned = '';\nfunction clean(text) { const seen = new Set(); const rows = [];\n" +
            " for (const raw of text.split(/\\r?\\n/)) { const line = raw.split(',').map(c => c.trim()).join(','); if (!line.replace(/,/g, '').trim()) continue; if (" + keep + ") continue; seen.add(line); rows.push(line); }\n" +
            " cleaned = rows.join('\\n'); document.getElementById('out').textContent = cleaned; }\n" +
            read + "\n" +
            "document.getElementById('dl').addEventListener('click', () => { const blob = new Blob([cleaned], { type: 'text/csv' }); const a = document.createElement('a'); a.href = URL.createObjectURL(blob); a.download = 'clean.csv'; a.click(); });\n");
        return await RequirementsCatalog.JudgeAsync(csvCase, project);
    }
    const string readWithText = "document.getElementById('csv').addEventListener('change', async (e) => { clean(await e.target.files[0].text()); });";
    const string readWithReader = "document.getElementById('csv').addEventListener('change', (e) => { const reader = new FileReader(); reader.onload = () => clean(reader.result); reader.readAsText(e.target.files[0]); });";
    var csvRight = await JudgeCsvAsync("csv-right", readWithText, "seen.has(line)");
    var csvReader = await JudgeCsvAsync("csv-reader", readWithReader, "seen.has(line)");
    var csvKeepsDuplicates = await JudgeCsvAsync("csv-dup", readWithText, "false");
    Check(csvRight.Verdict == CatalogVerdict.Verified && csvReader.Verdict == CatalogVerdict.Verified && csvKeepsDuplicates.Verdict == CatalogVerdict.Wrong &&
          csvKeepsDuplicates.Misses.Any(miss => miss.Contains("more than once", StringComparison.Ordinal)),
        "chosen-file-and-downloaded-file-are-played:" + csvRight.Verdict + "/" + string.Join("|", csvRight.Misses) + "/" + csvReader.Verdict + "/" + string.Join("|", csvReader.Misses) + "/" + csvKeepsDuplicates.Verdict);
    // PDF editing in place (指示書 phase 0): text changed inside its show command with the font's own letters, a rule's
    // thickness changed under a scaled drawing, and a white box over the text is not taken for a deletion.
    PdfMarkup.EnsureFont();
    var inPlaceRoot = Path.Combine(judgeRoot, "ls-pdf-inplace");
    Directory.CreateDirectory(inPlaceRoot);
    var inPlaceSource = Path.Combine(inPlaceRoot, "source.pdf");
    using (var made = new PdfSharp.Pdf.PdfDocument())
    {
        var madePage = made.AddPage();
        using var graphics = PdfSharp.Drawing.XGraphics.FromPdfPage(madePage);
        var font = new PdfSharp.Drawing.XFont("LocalStudio Gothic", 12);
        graphics.DrawString("開会挨拶と閉会", font, PdfSharp.Drawing.XBrushes.Black, 50, 100);
        graphics.DrawString("研修日程", font, PdfSharp.Drawing.XBrushes.Black, 50, 140);
        graphics.DrawString("摘　要", font, PdfSharp.Drawing.XBrushes.Black, 50, 180); // a heading with a space drawn between its letters
        graphics.ScaleTransform(0.25);
        graphics.DrawRectangle(PdfSharp.Drawing.XBrushes.Black, 200, 800, 1200, 2); // a 0.5 pt rule drawn at a quarter scale
        made.Save(inPlaceSource);
    }
    var replaced = Path.Combine(inPlaceRoot, "replaced.pdf");
    var replaceResult = PdfInPlaceEdit.ReplaceText(inPlaceSource, replaced, 1, "閉会", "開会");
    var replaceCheck = replaceResult.Done ? PdfInPlaceEdit.Verify(inPlaceSource, replaced, 1, expected: "開会", removed: "閉会", find: "閉会") : new PdfEditVerification(false, ["not done"]);
    var missingLetter = PdfInPlaceEdit.ReplaceText(inPlaceSource, Path.Combine(inPlaceRoot, "missing.pdf"), 1, "閉会", "閉幕");
    var covered = Path.Combine(inPlaceRoot, "covered.pdf");
    using (var cover = PdfSharp.Pdf.IO.PdfReader.Open(inPlaceSource, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify))
    {
        using (var graphics = PdfSharp.Drawing.XGraphics.FromPdfPage(cover.Pages[0]))
        {
            graphics.DrawRectangle(PdfSharp.Drawing.XBrushes.White, 120, 85, 40, 20);
            graphics.DrawString("開会", new PdfSharp.Drawing.XFont("LocalStudio Gothic", 12), PdfSharp.Drawing.XBrushes.Black, 122, 100);
        }
        cover.Save(covered);
    }
    var coverCheck = PdfInPlaceEdit.Verify(inPlaceSource, covered, 1, expected: "開会", removed: "閉会", find: "閉会");
    var ruled = Path.Combine(inPlaceRoot, "ruled.pdf");
    var ruleResult = PdfInPlaceEdit.SetRuleThickness(inPlaceSource, ruled, 1, 1.0);
    var ruleCheck = ruleResult.Done ? PdfInPlaceEdit.Verify(inPlaceSource, ruled, 1, ruleThickness: 1.0) : new PdfEditVerification(false, ["not done"]);
    var unchangedRuleCheck = PdfInPlaceEdit.Verify(inPlaceSource, inPlaceSource, 1, ruleThickness: 1.0);
    Check(replaceResult.Done && replaceCheck.Passed && !missingLetter.Done && missingLetter.Detail.Contains("「幕」", StringComparison.Ordinal) &&
          !coverCheck.Passed && coverCheck.Problems.Any(problem => problem.Contains("still in", StringComparison.Ordinal)) &&
          ruleResult.Done && ruleCheck.Passed && !unchangedRuleCheck.Passed &&
          Math.Abs(PdfInPlaceEdit.RuleThicknesses(inPlaceSource, 1).Single() - 0.5) < 0.01 && Math.Abs(PdfInPlaceEdit.RuleThicknesses(ruled, 1, 1.5).Single() - 1.0) < 0.01,
        "pdf-edits-in-place-are-verified-and-a-white-box-is-not-a-deletion:" + replaceResult.Detail + "/" + string.Join("|", replaceCheck.Problems) + "/" + missingLetter.Detail + "/" +
        string.Join("|", coverCheck.Problems) + "/" + ruleResult.Detail + "/" + string.Join("|", ruleCheck.Problems));
    // PdfLite, the C# 5 helper generated PDF editors get: the same edits from inside an app, saved as an incremental
    // update after the original bytes, read back with PdfPig.
    var lite = PdfLite.Open(inPlaceSource);
    var liteText = lite.PageText(1);
    var liteSame = lite.ReplaceText(1, "閉会", "開会", 1);
    var liteMissing = lite.ReplaceText(1, "研修日程", "研修予定", 1);
    var liteSubstitute = lite.ReplaceTextWithFont(1, "研修日程", "研修予定", 1, "gothic");
    var liteRules = lite.Rules(1);
    var liteRule = lite.SetRuleThickness(1, -1, 1.0);
    var liteUndo = lite.Undo() && lite.History.Count == 2 && lite.Redo() && lite.History.Count == 3;
    var liteSpaced = lite.ReplaceText(1, "摘要", "要摘", 1); // found without the space, saved with it
    var liteRotated = lite.RotatePage(1, 90).Done && lite.PageRotation(1) == 90 && !lite.RotatePage(1, 45).Done;
    var liteSaved = Path.Combine(inPlaceRoot, "lite-saved.pdf");
    lite.Save(liteSaved);
    var liteOverwrite = false;
    try { lite.Save(inPlaceSource); } catch (IOException) { liteOverwrite = true; }
    var liteOriginal = await File.ReadAllBytesAsync(inPlaceSource);
    var liteBytes = await File.ReadAllBytesAsync(liteSaved);
    string liteReadBack;
    var liteFonts = new Dictionary<string, string>();
    using (var check = UglyToad.PdfPig.PdfDocument.Open(liteSaved))
    {
        liteReadBack = string.Concat(check.GetPage(1).Text.Where(ch => !char.IsWhiteSpace(ch)));
        foreach (var letter in check.GetPage(1).Letters) liteFonts[letter.Value] = letter.FontName ?? "";
    }
    // Only the letters that changed are in the Windows font; 「研修」, shared by both texts, keeps the embedded font.
    var liteOnlyChanged = liteFonts.TryGetValue("研", out var keptFont) && liteFonts.TryGetValue("予", out var addedFont) &&
                          !keptFont.Contains("MS-Gothic", StringComparison.Ordinal) && addedFont.Contains("MS-Gothic", StringComparison.Ordinal);
    var liteVerify = PdfInPlaceEdit.Verify(inPlaceSource, liteSaved, 1, ruleThickness: 1.0);
    var liteReopened = PdfLite.Open(liteSaved);
    Check(liteText.Contains("開会挨拶と閉会", StringComparison.Ordinal) && liteSame.Done && !liteMissing.Done && liteMissing.MissingLetters == "予定" &&
          liteSubstitute.Done && liteSubstitute.UsedSubstituteFont && liteOnlyChanged && liteRules.Count == 1 && Math.Abs(liteRules[0].Thickness - 0.5) < 0.01 && liteRule.Done && liteUndo && liteOverwrite &&
          liteBytes.Length > liteOriginal.Length && liteBytes.AsSpan(0, liteOriginal.Length).SequenceEqual(liteOriginal) &&
          liteReadBack.Contains("開会挨拶と開会", StringComparison.Ordinal) && liteReadBack.Contains("研修予定", StringComparison.Ordinal) && !liteReadBack.Contains("閉会", StringComparison.Ordinal) &&
          liteVerify.Problems.All(problem => problem.Contains("text changed", StringComparison.Ordinal) || problem.Contains("overlap", StringComparison.Ordinal)) &&
          !liteVerify.Problems.Any(problem => problem.Contains("rule", StringComparison.Ordinal)) &&
          liteReopened.PageText(1).Contains("研修予定", StringComparison.Ordinal) && liteSpaced.Done && liteReopened.PageText(1).Contains("要　摘", StringComparison.Ordinal) && liteRotated && liteReopened.PageRotation(1) == 90 && Math.Abs(liteReopened.Rules(1)[0].Thickness - 1.0) < 0.01,
        "pdflite-edits-text-and-rules-in-place-and-appends-to-the-original:" + liteSame.Message + "/" + liteMissing.Message + "/" + liteSubstitute.Message + "/" + liteRule.Message + "/" +
        liteReadBack + "/" + string.Join("|", liteVerify.Problems) + "/" + string.Join(",", liteFonts.Where(pair => "研修予定".Contains(pair.Key, StringComparison.Ordinal)).Select(pair => pair.Key + "=" + pair.Value)));
    var pdfApp = Path.Combine(inPlaceRoot, "pdf-app");
    Directory.CreateDirectory(pdfApp);
    await File.WriteAllTextAsync(Path.Combine(pdfApp, "Program.cs"), """
        using System;
        using System.Windows.Forms;
        public class EditForm : Form
        {
            private Button open = new Button();
            public EditForm()
            {
                open.Text = "開く";
                open.Click += OnOpen;
                Controls.Add(open);
            }
            private void OnOpen(object sender, EventArgs e)
            {
                var dialog = new OpenFileDialog();
                if (dialog.ShowDialog() != DialogResult.OK) return;
                var doc = PdfLite.Open(dialog.FileName);
                var edit = doc.ReplaceText(1, "挨拶", "開会挨拶", 1);
                if (!edit.Done && edit.MissingLetters.Length > 0) edit = doc.ReplaceTextWithFont(1, "挨拶", "開会挨拶", 1, "gothic");
                doc.SetRuleThickness(1, -1, 1.0);
                var save = new SaveFileDialog();
                if (save.ShowDialog() == DialogResult.OK) doc.Save(save.FileName);
                MessageBox.Show(edit.Message);
            }
            [STAThread]
            static void Main() { Application.EnableVisualStyles(); Application.Run(new EditForm()); }
        }
        """);
    await File.WriteAllTextAsync(Path.Combine(pdfApp, "app.manifest"), WindowsApplicationManifest.Content);
    foreach (var helper in WindowsToolbox.Files(WindowsToolbox.For("PDFの文字を書き換えるアプリ")))
        await File.WriteAllTextAsync(Path.Combine(pdfApp, helper.RelativePath), helper.Content);
    string? pdfAppBuild = null;
    try { await new WindowsExecutableCompiler().CompileAsync(pdfApp); }
    catch (Exception ex) when (ex is InvalidDataException or WindowsBuildEnvironmentException) { pdfAppBuild = ex.Message; }
    Check(pdfAppBuild is null && WindowsToolbox.For("PDFを編集").Any(tool => tool.FileName == "PdfLite.cs") &&
          LocalWindowsAppGenerationEngine.ValidateSource(WindowsToolbox.AppSources(pdfApp)).Count == 0,
        "generated-pdf-app-builds-with-pdflite-using-the-windows-compiler:" + pdfAppBuild);
    // The catalogue's give-ups of 2026-10-20, each turned into a check or a standard fix.
    var brokenExe = string.Join("\n",
        "using System;", "using System.Collections.Generic;", "using System.Drawing;", "using System.Windows.Forms;",
        "public class GameForm : Form",
        "{",
        "    private Label ball = new Label();",
        "    private List<Rectangle> blocks = new List<Rectangle> { new Rectangle(0, 0, 10, 10) };",
        "    public GameForm() { Controls.Add(ball); ball.Click += Move; }",
        "    private void Move(object sender, EventArgs e)",
        "    {",
        "        ball.Location.X += 5;",
        "        blocks[0].Y = 20;",
        "        List<string> cells = new string[] { \"a\", \"b\" };",
        "        string name = Microsoft.VisualBasic.Interaction.InputBox(\"名前\", \"入力\", \"\");",
        "        Text = name + cells.Count;",
        "    }",
        "    [STAThread] static void Main() { Application.Run(new GameForm()); }",
        "}");
    var brokenApp = Path.Combine(inPlaceRoot, "compile-fix-app");
    Directory.CreateDirectory(brokenApp);
    await File.WriteAllTextAsync(Path.Combine(brokenApp, "app.manifest"), WindowsApplicationManifest.Content);
    await File.WriteAllTextAsync(Path.Combine(brokenApp, "Program.cs"), brokenExe);
    string? firstBuild = null, fixedBuild = null;
    try { await new WindowsExecutableCompiler().CompileAsync(brokenApp); } catch (InvalidDataException ex) { firstBuild = ex.Message; }
    var compileFixed = firstBuild is null ? null : CompileErrorFixes.Apply(brokenExe, firstBuild);
    if (compileFixed is not null)
    {
        await File.WriteAllTextAsync(Path.Combine(brokenApp, "Program.cs"), LocalWindowsAppGenerationEngine.AddSafeFrameworkUsings(compileFixed));
        try { await new WindowsExecutableCompiler().CompileAsync(brokenApp); } catch (InvalidDataException ex) { fixedBuild = ex.Message; }
    }
    Check(firstBuild is not null && firstBuild.Contains("CS0029", StringComparison.Ordinal) && !firstBuild.Contains("VisualBasic", StringComparison.Ordinal) &&
          compileFixed is not null && compileFixed.Contains("var lsCopy = ball.Location; lsCopy.X += 5; ball.Location = lsCopy;", StringComparison.Ordinal) &&
          compileFixed.Contains("var lsCopy = blocks[0]; lsCopy.Y = 20; blocks[0] = lsCopy;", StringComparison.Ordinal) &&
          compileFixed.Contains("new string[] { \"a\", \"b\" }).ToList();", StringComparison.Ordinal) && fixedBuild is null,
        "standard-compile-errors-are-fixed-and-visual-basic-input-box-builds:" + firstBuild?[..Math.Min(400, firstBuild.Length)] + "/" + fixedBuild);
    var invoicePlan = new StudioBlueprint("請求書", "", "app", ["品名・数量・単価を入力する", "合計を計算する"], [], [], [], []);
    var printless = "class F : Form { void Calc() { total.Text = (price * count).ToString(); } }";
    var csvRequirement = new[] { new GeneratedRequirement("LS-REQ-002", "空白行と重複行を取り除く") };
    var gasBrokenCandidate = new GasCandidate([new GasGeneratedFile("Code.gs", "function summarize() {\n  const sheet = SpreadsheetApp.getActiveSheet(;\n}\n")], [new GasRequirementEvidence("LS-REQ-001", "summarize")]);
    var gasReadOnly = new GasCandidate([new GasGeneratedFile("Code.gs", "function summarize() {\n  const values = SpreadsheetApp.getActiveSheet().getDataRange().getValues();\n  Logger.log(values.length);\n}\n")], [new GasRequirementEvidence("LS-REQ-001", "summarize")]);
    var gasSummary = new[] { new GeneratedRequirement("LS-REQ-001", "質問ごとの回答の件数を新しいシートに集計する") };
    var gasNewline = LocalAppsScriptGenerationEngine.Normalize(new GasCandidate([new GasGeneratedFile("Code.gs", "function a() {\n  const text = rows.join('\n');\n  return text;\n}\n")], []), []);
    var cleanerBody = "<button id=\"go\">整える</button><p id=\"out\"></p>";
    var cleanerScript = "document.getElementById('go').addEventListener('click', () => { document.getElementById('out').textContent = 'ok'; });";
    var cleanerPlan = new StudioBlueprint("CSV", "", "app", [], [], [], [], []);
    Check(LocalWindowsAppGenerationEngine.ValidateRequirementGrounding(printless, invoicePlan, StudioProjectKind.WindowsApp, "請求書を入力して印刷できるアプリ").Any(error => error.StartsWith("requirement.mechanism-missing:print", StringComparison.Ordinal)) &&
          !LocalWindowsAppGenerationEngine.ValidateRequirementGrounding(printless, invoicePlan, StudioProjectKind.WindowsApp).Any(error => error.Contains(":print", StringComparison.Ordinal)) &&
          LocalWindowsAppGenerationEngine.RequirementsWithoutMechanism(["月ごとの合計を表示する"], "total += amount;").Count == 1 &&
          LocalWindowsAppGenerationEngine.RequirementsWithoutMechanism(["月ごとの合計を表示する"], "var key = date.ToString(\"yyyy/MM\");").Count == 0 &&
          LocalWebAppGenerationEngine.RequirementsWithoutMechanism([new GeneratedRequirement("LS-REQ-001", "出勤日をチェックで入力する")], "<input id=\"d\">", "").Count == 1 &&
          LocalWebAppGenerationEngine.RequirementsWithoutMechanism([new GeneratedRequirement("LS-REQ-001", "出勤日をチェックで入力する")], "<input type=\"checkbox\">", "").Count == 0 &&
          LocalWebAppGenerationEngine.RequirementsWithoutMechanism([new GeneratedRequirement("LS-REQ-001", "マウスでなぞって絵を描ける")], "", "ctx.fillRect(x, y, 1, 1);").Count == 1 &&
          LocalWebAppGenerationEngine.RequirementsWithoutMechanism([new GeneratedRequirement("LS-REQ-001", "結果をファイルに保存できる")], "", "a.download = 'x.csv'; new Blob([t]);").Count == 0 &&
          LocalAppsScriptGenerationEngine.Validate(gasBrokenCandidate, [new GeneratedRequirement("LS-REQ-001", "集計する")]).Any(error => error.StartsWith("gas.syntax:Code.gs", StringComparison.Ordinal)) &&
          LocalAppsScriptGenerationEngine.Validate(gasReadOnly, gasSummary).Any(error => error.StartsWith("requirement.mechanism-missing:sheet-write", StringComparison.Ordinal)) &&
          gasNewline.Fixes > 0 && GasStaticVerifier.SyntaxError(gasNewline.Candidate.Files[0].Content) is null &&
          LocalWebAppGenerationEngine.ValidateCandidate(new GeneratedWebAppCandidate(cleanerBody, cleanerScript), cleanerPlan, csvRequirement).Any(error => error.StartsWith("requirement.not-visible:LS-REQ-002", StringComparison.Ordinal)) &&
          !LocalWebAppGenerationEngine.ValidateCandidate(new GeneratedWebAppCandidate(cleanerBody, cleanerScript), cleanerPlan, csvRequirement,
              acceptance: [new AcceptanceScenario("LS-REQ-002", "空白行", [new AcceptanceStep(Press: "整える")], ["ok"])]).Any(error => error.StartsWith("requirement.not-visible", StringComparison.Ordinal)),
        "request-words-are-checked-and-give-ups-get-standard-fixes");
    // Second re-measure (2026-10-20 evening): a helper carrying a raw U+FFFD made every PDF EXE unsaveable; matched memory
    // cards were taken for a dead button; a GAS shift table without tick boxes and with "C2,D2" ranges got through.
    var memoryCards = "class F : Form { Button start = new Button(); F() { Controls.Add(start); } void Card_Click(object sender, EventArgs e) { Button button = sender as Button; foreach (Button other in cards) { if (other.Tag == button.Tag) { other.Enabled = false; button.Enabled = false; } } } }";
    var gasShift = new GasCandidate([new GasGeneratedFile("Code.gs", "function count() {\n  var sheet = SpreadsheetApp.getActiveSheet();\n  for (var i = 0; i < 5; i++) sheet.getRange('C' + (i + 2) + ',D' + (i + 2)).setValues([[1, 2]]);\n}\n")], [new GasRequirementEvidence("LS-REQ-001", "count")]);
    var gasShiftErrors = LocalAppsScriptGenerationEngine.Validate(gasShift, [new GeneratedRequirement("LS-REQ-001", "出勤人数を集計する")], sourceRequest: "スタッフ名と出勤日をチェックで入力すると集計するシフト表");
    Check(WindowsToolbox.All.All(tool => !WindowsToolbox.Source(tool).Contains('�') && !LocalWebAppGenerationEngine.HasForbiddenControlCharacters(WindowsToolbox.Source(tool))) &&
          !LocalWindowsAppGenerationEngine.FalseSuccessShapes(memoryCards).Any(found => found.StartsWith("exe.never-enabled", StringComparison.Ordinal)) &&
          gasShiftErrors.Any(error => error.StartsWith("gas.range-address", StringComparison.Ordinal)) && gasShiftErrors.Any(error => error.StartsWith("requirement.mechanism-missing:checkbox", StringComparison.Ordinal)) &&
          !LocalWebAppGenerationEngine.ValidateCandidate(new GeneratedWebAppCandidate(cleanerBody, cleanerScript), cleanerPlan,
              [.. csvRequirement, new GeneratedRequirement("LS-REQ-003", "前後の空白を消去する")],
              acceptance: [new AcceptanceScenario("LS-REQ-002,LS-REQ-003", "整える", [new AcceptanceStep(Press: "整える")], ["ok"])]).Any(error => error.StartsWith("requirement.not-visible", StringComparison.Ordinal)),
        "helpers-are-clean-per-item-disabling-is-fine-gas-ranges-and-request-words-are-checked:" + string.Join("|", gasShiftErrors));
    // Hands-on 2026-10-21: the daily report and the renamer the user tried, in the shapes they had, and usable versions.
    var unlabeledReport = "class F : Form { TextBox dateBox, doneBox, nextBox; DateTimePicker picker; Button save;\n F() {\n dateBox = new TextBox(); doneBox = new TextBox(); nextBox = new TextBox(); picker = new DateTimePicker();\n save = new Button(); save.Text = \"保存\";\n Controls.Add(dateBox); Controls.Add(doneBox); Controls.Add(nextBox); Controls.Add(picker); Controls.Add(save); } }";
    var labeledReport = "class F : Form { TextBox doneBox; DateTimePicker picker; Button save;\n F() {\n var dateLabel = new Label(); dateLabel.Text = \"日付\"; dateLabel.Location = new Point(0, 0); var doneLabel = new Label(); doneLabel.Text = \"やったこと\"; doneLabel.Location = new Point(0, 30);\n doneBox = new TextBox(); picker = new DateTimePicker(); save = new Button(); save.Text = \"保存\";\n Controls.Add(dateLabel); Controls.Add(picker); Controls.Add(doneLabel); Controls.Add(doneBox); Controls.Add(save); } void Save_Click(object s, EventArgs e) { var line = picker.Value.ToString() + doneBox.Text; File.WriteAllText(path, line); } }";
    var randomRenamer = "class F : Form { Button pick, rename; ListBox list; List<string> olds = new List<string>(); List<string> news = new List<string>();\n F() {\n pick = new Button(); pick.Text = \"フォルダーを選択\"; rename = new Button(); rename.Text = \"ファイル名を変更\";\n list = new ListBox(); Controls.Add(pick); Controls.Add(rename); Controls.Add(list); }\n" +
                         " void Pick_Click(object s, EventArgs e) { foreach (string f in Directory.GetFiles(folder)) { string n = DateTime.Now.ToString(\"yyyyMMdd_\") + Path.GetRandomFileName(); olds.Add(f); news.Add(n); list.Items.Add(string.Format(\"Original: {0}, New: {1}\", f, n)); } }\n" +
                         " void Rename_Click(object s, EventArgs e) { for (int i = 0; i < olds.Count; i++) File.Move(olds[i], news[i]); MessageBox.Show(\"完了しました\"); } }";
    var usableRenamer = "class F : Form { Button pick, rename; ListBox list; TextBox prefix; Label prefixLabel; List<string> olds = new List<string>(); List<string> news = new List<string>();\n F() {\n prefixLabel = new Label(); prefixLabel.Text = \"先頭に付ける文字\"; prefixLabel.Location = new Point(0, 0); prefix = new TextBox();\n pick = new Button(); pick.Text = \"フォルダーを選択\"; pick.AutoSize = true; rename = new Button(); rename.Text = \"名前を変える\"; rename.Width = 120;\n list = new ListBox(); Controls.Add(prefixLabel); Controls.Add(prefix); Controls.Add(pick); Controls.Add(rename); Controls.Add(list); }\n" +
                          " void Pick_Click(object s, EventArgs e) { int n = 1; foreach (string f in Directory.GetFiles(folder)) { string name = prefix.Text + n.ToString(\"D3\") + \"_\" + Path.GetFileName(f); n++; olds.Add(f); news.Add(name); list.Items.Add(Path.GetFileName(f) + \" → \" + name); } }\n" +
                          " void Rename_Click(object s, EventArgs e) { foreach (string target in news) if (File.Exists(target)) return; var moved = new List<int>(); try { for (int i = 0; i < olds.Count; i++) { File.Move(olds[i], news[i]); moved.Add(i); } } catch { for (int j = moved.Count - 1; j >= 0; j--) File.Move(news[moved[j]], olds[moved[j]]); throw; } olds.Clear(); news.Clear(); rename.Enabled = false; MessageBox.Show(\"名前を変えました\"); } }";
    var guardApp = Path.Combine(inPlaceRoot, "error-message-app");
    Directory.CreateDirectory(guardApp);
    await File.WriteAllTextAsync(Path.Combine(guardApp, "app.manifest"), WindowsApplicationManifest.Content);
    await File.WriteAllTextAsync(Path.Combine(guardApp, "Program.cs"), WindowsSourceAutoFix.Apply("using System;\nusing System.Windows.Forms;\nclass F : Form\n{\n    [STAThread]\n    static void Main()\n    {\n        Application.Run(new F());\n    }\n}\n").Source);
    string? guardBuild = null;
    try { await new WindowsExecutableCompiler().CompileAsync(guardApp); } catch (InvalidDataException ex) { guardBuild = ex.Message; }
    var reportFindings = LocalWindowsAppGenerationEngine.UsabilityShapes(unlabeledReport);
    var renamerFindings = LocalWindowsAppGenerationEngine.UsabilityShapes(randomRenamer);
    var guardedMain = WindowsSourceAutoFix.Apply("using System;\nusing System.Windows.Forms;\nclass F : Form\n{\n    [STAThread]\n    static void Main()\n    {\n        Application.Run(new F());\n    }\n}\n").Source;
    Check(reportFindings.Any(found => found.StartsWith("exe.unlabeled-inputs", StringComparison.Ordinal)) && LocalWindowsAppGenerationEngine.UsabilityShapes(labeledReport).Count == 0 &&
          renamerFindings.Any(found => found.StartsWith("exe.button-text-cut:pick", StringComparison.Ordinal)) && renamerFindings.Any(found => found.StartsWith("exe.english-ui", StringComparison.Ordinal)) &&
          renamerFindings.Any(found => found.StartsWith("exe.random-names", StringComparison.Ordinal)) && renamerFindings.Any(found => found.StartsWith("exe.repeat-apply:Rename_Click", StringComparison.Ordinal)) &&
          LocalWindowsAppGenerationEngine.UsabilityShapes(usableRenamer).Count == 0 &&
          LocalWindowsAppGenerationEngine.RequirementsWithoutMechanism(["ファイル名の先頭に連番を付けて一括で名前を変える"], randomRenamer).Any(found => found.Contains(":rename-rule", StringComparison.Ordinal)) &&
          LocalWindowsAppGenerationEngine.RequirementsWithoutMechanism(["ファイル名の先頭に連番を付けて一括で名前を変える"], usableRenamer).Count == 0 &&
          LocalWindowsAppGenerationEngine.RequirementsWithoutMechanism(["結果を名前を付けて保存する"], "var d = new SaveFileDialog(); File.WriteAllText(d.FileName, text);").Count == 0 &&
          guardedMain.IndexOf("ThreadException", StringComparison.Ordinal) is > 0 and var guardAt && guardAt < guardedMain.IndexOf("Application.Run", StringComparison.Ordinal) &&
          WindowsSourceAutoFix.Apply(guardedMain).Source == guardedMain && guardBuild is null,
        "unusable-exe-shapes-from-hands-on-are-refused-and-usable-versions-pass:" + string.Join(" | ", reportFindings.Concat(renamerFindings).Select(found => found.Split(':')[0])) + " / usable: " + string.Join("|", LocalWindowsAppGenerationEngine.UsabilityShapes(usableRenamer).Concat(LocalWindowsAppGenerationEngine.UsabilityShapes(labeledReport))));
    // Regeneration 2026-10-26: the renamer that passed gave every file "prefix_now.ext" and its preview read the new
    // names before the rename had made them.
    var sameNameRenamer = "class F : Form { List<string> originalNames = new List<string>(); List<string> newNames = new List<string>();\n" +
                          " void Pick_Click(object s, EventArgs e) { foreach (string file in Directory.GetFiles(folder)) originalNames.Add(Path.GetFileName(file)); }\n" +
                          " void Rename_Click(object s, EventArgs e) { string folderPath = dialog.SelectedPath; for (int i = 0; i < list.Items.Count; i++) { string oldName = originalNames[i];\n" +
                          "  string newName = box.Text + \"_\" + DateTime.Now.ToString(\"yyyyMMdd_HHmmss\") + Path.GetExtension(oldName); string oldFilePath = Path.Combine(folderPath, oldName); string newFilePath = Path.Combine(folderPath, newName);\n" +
                          "  File.Move(oldFilePath, newFilePath); newNames.Add(newName); } button.Enabled = false; }\n" +
                          " void View_Click(object s, EventArgs e) { list.Items.Clear(); for (int i = 0; i < originalNames.Count; i++) { list.Items.Add(originalNames[i] + \" → \" + newNames[i]); } button.Enabled = true; } }";
    var sameNameFindings = LocalWindowsAppGenerationEngine.UsabilityShapes(sameNameRenamer);
    var numberedRenamer = sameNameRenamer.Replace("box.Text + \"_\" + DateTime", "box.Text + (i + 1).ToString(\"000\") + \"_\" + DateTime")
        .Replace("for (int i = 0; i < originalNames.Count; i++) { list.Items.Add(originalNames[i] + \" → \" + newNames[i]); }", "foreach (string name in originalNames) list.Items.Add(name);");
    Check(sameNameFindings.Any(found => found.StartsWith("exe.same-new-name", StringComparison.Ordinal)) &&
          sameNameFindings.Any(found => found.StartsWith("exe.read-before-filled:View_Click", StringComparison.Ordinal)) &&
          !LocalWindowsAppGenerationEngine.UsabilityShapes(numberedRenamer).Any(found => found.StartsWith("exe.same-new-name", StringComparison.Ordinal) || found.StartsWith("exe.read-before-filled", StringComparison.Ordinal)) &&
          !LocalWindowsAppGenerationEngine.UsabilityShapes(usableRenamer).Any(found => found.StartsWith("exe.same-new-name", StringComparison.Ordinal)) &&
          !LocalWindowsAppGenerationEngine.UsabilityShapes("class F { void Sort_Click(object s, EventArgs e) { foreach (var file in Directory.GetFiles(folder)) { var target = Path.Combine(folder, Path.GetExtension(file).TrimStart('.'), Path.GetFileName(file)); File.Move(file, target); } } }").Any(found => found.StartsWith("exe.same-new-name", StringComparison.Ordinal)),
        "renames-to-one-name-and-previews-of-unfilled-lists-are-refused:" + string.Join(" | ", sameNameFindings.Select(found => found.Split(':')[0])) + " / numbered: " + string.Join("|", LocalWindowsAppGenerationEngine.UsabilityShapes(numberedRenamer)));
    // Run 19 (2026-10-27): the model satisfied the old checks by placing two labels at (0,0), ignored the starting-number
    // control, and used a details ListView without columns. Its Excel app copied separate sheets and created an empty
    // summary while discarding CombineSheets warnings.
    var disconnectedRenamer = "class F : Form { NumericUpDown start; TextBox prefix; Label a, b; ListView preview; F() { a = new Label(); b = new Label(); Controls.Add(a); Controls.Add(b); start = new NumericUpDown(); start.Value = 1; prefix = new TextBox(); preview = new ListView(); preview.View = View.Details; Controls.Add(start); Controls.Add(prefix); Controls.Add(preview); } string Name(int i) { return prefix.Text + (i + 1).ToString(); } }";
    var disconnectedFindings = LocalWindowsAppGenerationEngine.UsabilityShapes(disconnectedRenamer);
    var falseExcelMerge = "var files = Directory.GetFiles(folder, \"*.xlsx\"); var notes = new List<string>(); XlsxLite.CombineSheets(output, pairs, \"まとめ\", new List<string[]>()); if (notes.Count == 0) status.Text = \"完了\";";
    var falseExcelFindings = LocalWindowsAppGenerationEngine.FalseSuccessShapes(falseExcelMerge);
    var rowAggregateRequest = new[] { "学校名の列を付けて回答を1つの表に統合する" };
    var duplicateDateInputs = "class F : Form { TextBox dateTextBox; DateTimePicker datePicker; F() { dateTextBox = new TextBox(); datePicker = new DateTimePicker(); Controls.Add(dateTextBox); Controls.Add(datePicker); var a = dateTextBox.Text; var b = datePicker.Value; } }";
    var distinctDateInputs = "class F : Form { DateTimePicker startDatePicker; DateTimePicker endDatePicker; F() { startDatePicker = new DateTimePicker(); endDatePicker = new DateTimePicker(); Controls.Add(startDatePicker); Controls.Add(endDatePicker); var a = startDatePicker.Value; var b = endDatePicker.Value; } }";
    var duplicateDateFindings = LocalWindowsAppGenerationEngine.UsabilityShapes(duplicateDateInputs);
    var duplicateDateWithEmptyLayout = """
        class F : Form
        {
            private TextBox dateTextBox;
            private DateTimePicker datePicker;
            private FlowLayoutPanel obsoleteDateRow;
            F()
            {
                dateTextBox = new TextBox();
                obsoleteDateRow = new FlowLayoutPanel();
                obsoleteDateRow.Dock = DockStyle.Top;
                obsoleteDateRow.Controls.Add(dateTextBox);
                Controls.Add(obsoleteDateRow);
                datePicker = new DateTimePicker();
                Controls.Add(datePicker);
                var savedDate = datePicker.Value;
            }
        }
        """;
    var repairedDuplicateDate = LocalWindowsAppGenerationEngine.RemoveUnusedDuplicateInputControls(duplicateDateWithEmptyLayout);
    var promotionSource = Path.Combine(smokeRoot, "promotion-source");
    var promotionDestination = Path.Combine(smokeRoot, "promotion-destination");
    Directory.CreateDirectory(promotionSource);
    await File.WriteAllTextAsync(Path.Combine(promotionSource, "evidence.txt"), "verified");
    var promotionAttempts = 0;
    await DirectoryPromotion.MoveWithTransientRetryAsync(
        promotionSource,
        promotionDestination,
        CancellationToken.None,
        (source, destination) =>
        {
            promotionAttempts++;
            if (promotionAttempts < 3) throw new UnauthorizedAccessException("simulated transient scanner lock");
            Directory.Move(source, destination);
        },
        retryDelay: _ => TimeSpan.Zero);
    Check(disconnectedFindings.Count(found => found.StartsWith("exe.label-default-position", StringComparison.Ordinal)) == 2 &&
          disconnectedFindings.Any(found => found.StartsWith("exe.input-value-unused:start", StringComparison.Ordinal)) &&
          disconnectedFindings.Any(found => found.StartsWith("exe.details-without-columns:preview", StringComparison.Ordinal)) &&
          duplicateDateFindings.Any(found => found.StartsWith("exe.duplicate-input-concept:date", StringComparison.Ordinal)) &&
          !LocalWindowsAppGenerationEngine.UsabilityShapes(distinctDateInputs).Any(found => found.StartsWith("exe.duplicate-input-concept", StringComparison.Ordinal)) &&
          !repairedDuplicateDate.Contains("dateTextBox", StringComparison.Ordinal) && repairedDuplicateDate.Contains("datePicker", StringComparison.Ordinal) &&
          !repairedDuplicateDate.Contains("obsoleteDateRow", StringComparison.Ordinal) &&
          !LocalWindowsAppGenerationEngine.UsabilityShapes(repairedDuplicateDate).Any(found => found.StartsWith("exe.duplicate-input-concept", StringComparison.Ordinal) || found.StartsWith("exe.input-value-unused:dateTextBox", StringComparison.Ordinal)) &&
          LocalWindowsAppGenerationEngine.RemoveUnusedDuplicateInputControls(duplicateDateInputs) == duplicateDateInputs &&
          promotionAttempts == 3 && File.ReadAllText(Path.Combine(promotionDestination, "evidence.txt")) == "verified" && !Directory.Exists(promotionSource) &&
          falseExcelFindings.Any(found => found.StartsWith("excel.combine-result-ignored", StringComparison.Ordinal)) &&
          falseExcelFindings.Any(found => found.StartsWith("excel.empty-summary", StringComparison.Ordinal)) &&
          LocalWindowsAppGenerationEngine.RequirementsWithoutMechanism(rowAggregateRequest, falseExcelMerge).Any(found => found.Contains(":excel-row-aggregation", StringComparison.Ordinal)) &&
          LocalWindowsAppGenerationEngine.RequirementsWithoutMechanism(rowAggregateRequest, "var rows = new List<string[]>(); foreach (var file in files) { foreach (var row in XlsxLite.ReadSheet(file)) rows.Add(row); } XlsxLite.WriteSheet(output, rows);").Count == 0 &&
          LocalWindowsAppGenerationEngine.RequirementsWithoutMechanism(["ファイル名を連番付きに変える"], "File.Move(oldPath, newPath);").Any(found => found.Contains(":rename-safety", StringComparison.Ordinal)) &&
          LocalWindowsAppGenerationEngine.RequirementsWithoutMechanism(["ファイル名を連番付きに変える"], usableRenamer).Count == 0,
        "run19-formal-passes-are-refused:" + string.Join(" | ", disconnectedFindings.Concat(falseExcelFindings).Select(found => found.Split(':')[0])));
    // Hands-on 2026-10-21: an Excel merge lost the forms' look. CombineSheets keeps each school's sheet as it was, from
    // workbooks whose formats differ, and puts a values sheet first.
    void MakeForm(string file, string school, double titleSize, bool reversed)
    {
        using var document = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Create(file, DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook);
        var book = document.AddWorkbookPart();
        book.Workbook = new DocumentFormat.OpenXml.Spreadsheet.Workbook();
        var stylePart = book.AddNewPart<DocumentFormat.OpenXml.Packaging.WorkbookStylesPart>();
        var plainFont = new DocumentFormat.OpenXml.Spreadsheet.Font(new DocumentFormat.OpenXml.Spreadsheet.FontSize { Val = 11 });
        var titleFont = new DocumentFormat.OpenXml.Spreadsheet.Font(new DocumentFormat.OpenXml.Spreadsheet.Bold(), new DocumentFormat.OpenXml.Spreadsheet.FontSize { Val = titleSize });
        var dateCode = reversed ? "ggge\"年\"m\"月\"d\"日\"" : "yyyy\"年\"m\"月\"d\"日\"";
        stylePart.Stylesheet = new DocumentFormat.OpenXml.Spreadsheet.Stylesheet(
            new DocumentFormat.OpenXml.Spreadsheet.NumberingFormats(new DocumentFormat.OpenXml.Spreadsheet.NumberingFormat { NumberFormatId = 164, FormatCode = dateCode }) { Count = 1 },
            new DocumentFormat.OpenXml.Spreadsheet.Fonts(reversed ? new[] { titleFont, plainFont } : new[] { plainFont, titleFont }) { Count = 2 },
            new DocumentFormat.OpenXml.Spreadsheet.Fills(
                new DocumentFormat.OpenXml.Spreadsheet.Fill(new DocumentFormat.OpenXml.Spreadsheet.PatternFill { PatternType = DocumentFormat.OpenXml.Spreadsheet.PatternValues.None }),
                new DocumentFormat.OpenXml.Spreadsheet.Fill(new DocumentFormat.OpenXml.Spreadsheet.PatternFill { PatternType = DocumentFormat.OpenXml.Spreadsheet.PatternValues.Gray125 }),
                new DocumentFormat.OpenXml.Spreadsheet.Fill(new DocumentFormat.OpenXml.Spreadsheet.PatternFill(new DocumentFormat.OpenXml.Spreadsheet.ForegroundColor { Rgb = "FFFFFF00" }) { PatternType = DocumentFormat.OpenXml.Spreadsheet.PatternValues.Solid })) { Count = 3 },
            new DocumentFormat.OpenXml.Spreadsheet.Borders(new DocumentFormat.OpenXml.Spreadsheet.Border(),
                new DocumentFormat.OpenXml.Spreadsheet.Border(new DocumentFormat.OpenXml.Spreadsheet.LeftBorder { Style = DocumentFormat.OpenXml.Spreadsheet.BorderStyleValues.Thin },
                    new DocumentFormat.OpenXml.Spreadsheet.RightBorder { Style = DocumentFormat.OpenXml.Spreadsheet.BorderStyleValues.Thin }, new DocumentFormat.OpenXml.Spreadsheet.TopBorder { Style = DocumentFormat.OpenXml.Spreadsheet.BorderStyleValues.Thin },
                    new DocumentFormat.OpenXml.Spreadsheet.BottomBorder { Style = DocumentFormat.OpenXml.Spreadsheet.BorderStyleValues.Thin }, new DocumentFormat.OpenXml.Spreadsheet.DiagonalBorder())) { Count = 2 },
            new DocumentFormat.OpenXml.Spreadsheet.CellStyleFormats(new DocumentFormat.OpenXml.Spreadsheet.CellFormat()) { Count = 1 },
            new DocumentFormat.OpenXml.Spreadsheet.CellFormats(new DocumentFormat.OpenXml.Spreadsheet.CellFormat(),
                new DocumentFormat.OpenXml.Spreadsheet.CellFormat { FontId = reversed ? 0u : 1u, FillId = 2, BorderId = 1, ApplyFont = true, ApplyFill = true, ApplyBorder = true },
                new DocumentFormat.OpenXml.Spreadsheet.CellFormat { NumberFormatId = 164, FontId = reversed ? 1u : 0u, ApplyNumberFormat = true }) { Count = 3 });
        var strings = book.AddNewPart<DocumentFormat.OpenXml.Packaging.SharedStringTablePart>();
        strings.SharedStringTable = new DocumentFormat.OpenXml.Spreadsheet.SharedStringTable(
            new DocumentFormat.OpenXml.Spreadsheet.SharedStringItem(new DocumentFormat.OpenXml.Spreadsheet.Text(school)),
            new DocumentFormat.OpenXml.Spreadsheet.SharedStringItem(new DocumentFormat.OpenXml.Spreadsheet.Text("使用する")));
        var sheetPart = book.AddNewPart<DocumentFormat.OpenXml.Packaging.WorksheetPart>();
        sheetPart.Worksheet = new DocumentFormat.OpenXml.Spreadsheet.Worksheet(
            new DocumentFormat.OpenXml.Spreadsheet.Columns(new DocumentFormat.OpenXml.Spreadsheet.Column { Min = 1, Max = 1, Width = 30, CustomWidth = true }),
            new DocumentFormat.OpenXml.Spreadsheet.SheetData(
                new DocumentFormat.OpenXml.Spreadsheet.Row(new DocumentFormat.OpenXml.Spreadsheet.Cell { CellReference = "A1", DataType = DocumentFormat.OpenXml.Spreadsheet.CellValues.SharedString, CellValue = new DocumentFormat.OpenXml.Spreadsheet.CellValue("0"), StyleIndex = 1 }) { RowIndex = 1 },
                new DocumentFormat.OpenXml.Spreadsheet.Row(new DocumentFormat.OpenXml.Spreadsheet.Cell { CellReference = "A2", CellValue = new DocumentFormat.OpenXml.Spreadsheet.CellValue("46000"), StyleIndex = 2 },
                    new DocumentFormat.OpenXml.Spreadsheet.Cell { CellReference = "B2", DataType = DocumentFormat.OpenXml.Spreadsheet.CellValues.SharedString, CellValue = new DocumentFormat.OpenXml.Spreadsheet.CellValue("1") }) { RowIndex = 2 }),
            new DocumentFormat.OpenXml.Spreadsheet.MergeCells(new DocumentFormat.OpenXml.Spreadsheet.MergeCell { Reference = "A1:B1" }) { Count = 1 });
        book.Workbook.AppendChild(new DocumentFormat.OpenXml.Spreadsheet.Sheets(new DocumentFormat.OpenXml.Spreadsheet.Sheet { Id = book.GetIdOfPart(sheetPart), SheetId = 1, Name = "回答" }));
    }
    var formFolder = Path.Combine(inPlaceRoot, "forms");
    Directory.CreateDirectory(formFolder);
    var firstForm = Path.Combine(formFolder, "第一小.xlsx");
    var secondForm = Path.Combine(formFolder, "第二小.xlsx");
    MakeForm(firstForm, "第一小学校", 14, false);
    MakeForm(secondForm, "第二小学校", 16, true);
    var combined = Path.Combine(formFolder, "まとめ.xlsx");
    var combineNotes = XlsxLite.CombineSheets(combined, new List<KeyValuePair<string, string>> { new("第一小", firstForm), new("第二小", secondForm) }, "一覧", [["学校名", "回答"], ["第一小", "使用する"], ["第二小", "使用する"]]);
    var combineChecks = new List<string>();
    using (var result = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Open(combined, false))
    {
        var validation = new DocumentFormat.OpenXml.Validation.OpenXmlValidator().Validate(result).Take(3).Select(error => error.Description).ToList();
        if (validation.Count > 0) combineChecks.Add("invalid: " + string.Join(" / ", validation));
        var bookPart = result.WorkbookPart!;
        var sheetNames = bookPart.Workbook!.Sheets!.Elements<DocumentFormat.OpenXml.Spreadsheet.Sheet>().Select(sheet => sheet.Name!.Value).ToList();
        if (!sheetNames.SequenceEqual(["一覧", "第一小", "第二小"])) combineChecks.Add("sheets " + string.Join(",", sheetNames));
        var formats = bookPart.WorkbookStylesPart!.Stylesheet!;
        foreach (var (sheetName, school, size, dateCode) in new[] { ("第一小", "第一小学校", 14.0, "yyyy\"年\"m\"月\"d\"日\""), ("第二小", "第二小学校", 16.0, "ggge\"年\"m\"月\"d\"日\"") })
        {
            var sheetRef = bookPart.Workbook!.Sheets!.Elements<DocumentFormat.OpenXml.Spreadsheet.Sheet>().First(sheet => sheet.Name == sheetName);
            var worksheet = ((DocumentFormat.OpenXml.Packaging.WorksheetPart)bookPart.GetPartById(sheetRef.Id!)).Worksheet!;
            var cells = worksheet.Descendants<DocumentFormat.OpenXml.Spreadsheet.Cell>().ToDictionary(cell => cell.CellReference!.Value!);
            var title = (DocumentFormat.OpenXml.Spreadsheet.CellFormat)formats.CellFormats!.ElementAt((int)cells["A1"].StyleIndex!.Value);
            var titleFontFound = (DocumentFormat.OpenXml.Spreadsheet.Font)formats.Fonts!.ElementAt((int)title.FontId!.Value);
            var titleFill = (DocumentFormat.OpenXml.Spreadsheet.Fill)formats.Fills!.ElementAt((int)title.FillId!.Value);
            var dateFormat = (DocumentFormat.OpenXml.Spreadsheet.CellFormat)formats.CellFormats!.ElementAt((int)cells["A2"].StyleIndex!.Value);
            var code = formats.NumberingFormats!.Elements<DocumentFormat.OpenXml.Spreadsheet.NumberingFormat>().FirstOrDefault(format => format.NumberFormatId!.Value == dateFormat.NumberFormatId!.Value)?.FormatCode?.Value;
            if (cells["A1"].InnerText != school) combineChecks.Add(sheetName + " text " + cells["A1"].InnerText);
            if (titleFontFound.Bold is null || titleFontFound.FontSize?.Val?.Value != size) combineChecks.Add(sheetName + " font");
            if (titleFill.PatternFill?.ForegroundColor?.Rgb?.Value != "FFFFFF00") combineChecks.Add(sheetName + " fill");
            if (code != dateCode) combineChecks.Add(sheetName + " date " + code);
            if (worksheet.Descendants<DocumentFormat.OpenXml.Spreadsheet.MergeCell>().SingleOrDefault()?.Reference?.Value != "A1:B1") combineChecks.Add(sheetName + " merge");
            if (worksheet.Descendants<DocumentFormat.OpenXml.Spreadsheet.Column>().SingleOrDefault()?.Width?.Value != 30) combineChecks.Add(sheetName + " width");
        }
    }
    // The plain list of paths a small model could write (2026-10-26): sheets are named after the files.
    var byPaths = Path.Combine(formFolder, "まとめ2.xlsx");
    var byPathsNotes = XlsxLite.CombineSheets(byPaths, new List<string> { firstForm, secondForm });
    if (byPathsNotes.Count > 0 || !XlsxLite.SheetNames(byPaths).SequenceEqual(["第一小", "第二小"])) combineChecks.Add("by paths " + string.Join(",", XlsxLite.SheetNames(byPaths)));
    Check(combineNotes.Count == 0 && combineChecks.Count == 0 && XlsxLite.ReadSheet(combined, "第二小")[1][1] == "使用する",
        "excel-forms-are-combined-with-their-look-kept:" + string.Join(" | ", combineChecks.Concat(combineNotes)));
    var countsDays = await JudgeHabitAsync("habit-days", "if (!h.days.includes(today())) h.days.push(today());", "h.name + ' 連続' + streak(h.days) + '日'");
    var countsChecks = await JudgeHabitAsync("habit-checks", "h.done = !h.done;", "h.name + ' 連続' + (h.done ? 1 : 0) + '日'");
    Check(countsDays.Verdict == CatalogVerdict.Verified && countsChecks.Verdict == CatalogVerdict.Wrong &&
          LocalWebAppGenerationEngine.FalseSuccessShapes("", "const update = () => {\n const fromUnit = lengthUnit.value;\n const toUnit = lengthUnit.value;\n};").SingleOrDefault()?.StartsWith("behavior.same-field-twice:lengthUnit", StringComparison.Ordinal) == true,
        "next-day-streaks-and-same-field-through-a-variable:" + countsDays.Verdict + "/" + string.Join("|", countsDays.Misses) + "/" + countsChecks.Verdict);
    var verificationStatusStore = new ProjectVerificationStatusStore(smokePaths);
    var pendingVerification = verificationStatusStore.Load(generatedProject);
    Check(pendingVerification.Applicable && pendingVerification.Level == "pending" && pendingVerification.Features.Count == 1 && !string.IsNullOrWhiteSpace(pendingVerification.Features[0].Instruction), "generated-app-manual-verification-starts-pending");
    Check(File.Exists(Path.Combine(generatedProject, "verification-plan.json")), "generated-app-guided-verification-plan-created");
    await CheckThrowsAsync(() => verificationStatusStore.RecordAsync(smokeRoot, "LS-REQ-001", ManualFeatureStatus.Confirmed), "manual-verification-project-root-boundary");
    var reportedProblem = await verificationStatusStore.RecordAsync(generatedProject, "LS-REQ-001", ManualFeatureStatus.Failed);
    Check(reportedProblem.Level == "failed" && reportedProblem.Features[0].Status == ManualFeatureStatus.Failed, "generated-app-problem-result-recorded");
    var userVerified = await verificationStatusStore.RecordAsync(generatedProject, "LS-REQ-001", ManualFeatureStatus.Confirmed);
    Check(userVerified.Level == "user-verified" && File.Exists(Path.Combine(generatedProject, ".localstudio", "verification", "manual.json")), "generated-app-user-verification-recorded");
    Check(File.ReadAllText(Path.Combine(generatedProject, "localstudio.json")).Contains("\"lifecycle\": \"user-verified\""), "generated-app-lifecycle-promoted-after-all-confirmed");
    File.Delete(Path.Combine(generatedProject, "verification-plan.json"));
    var manualEvidencePath = Path.Combine(generatedProject, ".localstudio", "verification", "manual.json");
    await File.WriteAllTextAsync(manualEvidencePath, File.ReadAllText(manualEvidencePath).Replace(userVerified.SourceFingerprint, ComputePre012VerificationFingerprint(generatedProject), StringComparison.Ordinal));
    var prePlanCompatibility = verificationStatusStore.Load(generatedProject);
    Check(prePlanCompatibility.Applicable && prePlanCompatibility.Level == "user-verified" && prePlanCompatibility.Features[0].Status == ManualFeatureStatus.Confirmed, "generated-pre-verification-plan-record-is-preserved");
    var generatedScriptPath = Path.Combine(generatedProject, "app.js");
    await File.AppendAllTextAsync(generatedScriptPath, "\n// changed after manual verification\n");
    var staleVerification = verificationStatusStore.Load(generatedProject);
    Check(staleVerification.Level == "stale" && staleVerification.Features.All(item => item.Status == ManualFeatureStatus.Pending), "generated-app-code-change-invalidates-manual-verification");
    Check(new ProjectCatalog(smokePaths).GetProjects().Single(item => item.Path == generatedProject).Lifecycle == "stale", "project-catalog-shows-effective-stale-state");

    var counterRequirements = new[]
    {
        new GeneratedRequirement("LS-REQ-001", "入力した文章の文字数と行数をリアルタイム表示"),
        new GeneratedRequirement("LS-REQ-002", "文章の自動保存と消去")
    };
    var brokenCounterScript = "const text = input.value; const count = text.trim().split(/\b/).length;";
    Check(LocalWebAppGenerationEngine.HasForbiddenControlCharacters(brokenCounterScript), "generated-control-character-detected");
    var incompleteCounterScript = "input.addEventListener('input', updateStats); function updateStats(){ const text=input.value; const count=text.length; const lines=text.split('\\n').length; } function saveText(){ localStorage.setItem('text', input.value); } function loadText(){ localStorage.getItem('text'); } function clearText(){ localStorage.removeItem('text'); }";
    Check(LocalWebAppGenerationEngine.ValidateKnownBehaviorContracts(incompleteCounterScript, counterRequirements).Contains("behavior.auto-save"), "generated-false-auto-save-rejected");
    var validCounterScript = "input.addEventListener('input', updateStats); function updateStats(){ const text=input.value; const count=Array.from(text).length; const lines=text.split('\\n').length; localStorage.setItem('text', text); } function loadText(){ localStorage.getItem('text'); } function clearText(){ localStorage.removeItem('text'); }";
    Check(LocalWebAppGenerationEngine.ValidateKnownBehaviorContracts(validCounterScript, counterRequirements).Count == 0, "generated-known-counter-contract-accepted");
    var stopwatchRequirements = new[] { new GeneratedRequirement("LS-REQ-001", "ストップウォッチで経過時間を測る") };
    Check(LocalWebAppGenerationEngine.ValidateKnownBehaviorContracts("start.addEventListener('click', () => { shown.textContent = '0:00'; });", stopwatchRequirements).Contains("behavior.timer") &&
          LocalWebAppGenerationEngine.ValidateKnownBehaviorContracts("start.addEventListener('click', () => { id = setInterval(tick, 100); });", stopwatchRequirements).Count == 0,
        "stopwatch-without-a-timer-is-rejected");
    var plannerWordedStopwatch = new[] { new GeneratedRequirement("LS-REQ-001", "スタートボタンで開始"), new GeneratedRequirement("LS-REQ-002", "ストップボタンで停止"), new GeneratedRequirement("LS-REQ-003", "リセットボタンで0に戻す") };
    Check(LocalWebAppGenerationEngine.ValidateKnownBehaviorContracts("start.addEventListener('click', () => {});", plannerWordedStopwatch).Contains("behavior.timer") &&
          !LocalWebAppGenerationEngine.ValidateKnownBehaviorContracts("go.addEventListener('click', () => {});", [new GeneratedRequirement("LS-REQ-001", "BMIを計算して表示する")]).Contains("behavior.timer"),
        "timer-contract-recognises-planner-wording-but-not-unrelated-apps");

    Check(OutputTargetAdvisor.Analyze("ブラウザーを使わずWindowsのEXEにして").RecommendedKind == StudioProjectKind.WindowsApp, "output-advisor-recommends-windows-exe");
    Check(OutputTargetAdvisor.Analyze("ブラウザーで使える簡単な計算フォーム").RecommendedKind == StudioProjectKind.DesktopOrWebApp, "output-advisor-recommends-web-app");
    Check(OutputTargetAdvisor.Analyze("テトリス").RecommendedKind == StudioProjectKind.WindowsGame, "output-advisor-recognizes-game-without-windows-keyword");
    Check(OutputTargetAdvisor.Analyze("スプレッドシートの期限を確認するGAS").RecommendedKind == StudioProjectKind.GoogleAppsScript, "output-advisor-recognizes-gas");
    Check(OutputTargetAdvisor.Analyze("複数ツールを使って調査するエージェント").RecommendedKind == StudioProjectKind.Agent, "output-advisor-recognizes-agent");
    Check(OutputTargetAdvisor.Analyze("便利なメモを作りたい").RecommendedKind is null, "output-advisor-does-not-default-ambiguous-request-to-web");
    Check(OutputTargetAdvisor.Describe("テトリス", StudioProjectKind.WindowsApp).Contains("ローカルAIが自律実装"), "output-advisor-discloses-autonomous-generation-route");
    var nativeRequest = "WindowsのEXEとして動く文字数と行数のカウンター。文章を自動保存して消去できるようにして";
    // The character counter and the pinball game were fixed templates; both are now planned and built from the request.
    var nativeCounterBlueprint = BlueprintValidator.Validate(new StudioBlueprint("文字数カウンター", nativeRequest, "windows-app", ["文字数と行数の表示"], [new("実装", "数える")], [], [], ["workspace.read", "workspace.write"]));
    var nativeCounterPlanner = new RecordingPlanner(nativeCounterBlueprint);
    var plannedCounter = await new HybridPlanningEngine(nativeCounterPlanner).PlanAsync(nativeRequest, StudioProjectKind.WindowsApp, LocalStudioSettings.DefaultModel);
    var pinballPlanner = new RecordingPlanner(BlueprintValidator.Validate(new StudioBlueprint("クラシックピンボール", "実物のようなピンボール", "windows-game", ["フリッパー"], [new("実装", "作る")], [], [], ["workspace.read", "workspace.write"])));
    var plannedPinball = await new HybridPlanningEngine(pinballPlanner).PlanAsync("ネオンではなくクラシックな実物っぽいピンボールを作って", StudioProjectKind.WindowsGame, LocalStudioSettings.DefaultModel);
    Check(nativeCounterPlanner.CallCount == 1 && plannedCounter.Name == "文字数カウンター" &&
          ImplementationSupportPolicy.Evaluate(plannedCounter, StudioProjectKind.WindowsApp).Route == ProjectImplementationRoute.GeneratedWindowsExecutable &&
          pinballPlanner.CallCount == 1 && plannedPinball.Name == "クラシックピンボール" &&
          ImplementationSupportPolicy.Evaluate(plannedPinball, StudioProjectKind.WindowsGame).Route == ProjectImplementationRoute.GeneratedWindowsExecutable,
        "no-app-is-made-from-a-fixed-template");

    var mazeBlueprint = BlueprintValidator.Validate(new StudioBlueprint("迷路ゲーム", "迷路を探索するWindowsゲーム", "windows-game", ["迷路の自動生成"], [new("実装", "迷路を作る")], [], [], ["workspace.read", "workspace.write"]));
    var mazeDecision = ImplementationSupportPolicy.Evaluate(mazeBlueprint, StudioProjectKind.WindowsGame);
    Check(mazeDecision.CanCreate && mazeDecision.Route == ProjectImplementationRoute.GeneratedWindowsExecutable, "unknown-game-routes-to-autonomous-generation");
    await CheckThrowsAsync(() => new ProjectScaffolder(smokePaths).CreateAsync(mazeBlueprint, StudioProjectKind.WindowsGame), "autonomous-generation-requires-local-engine");
    var recordingPlanner = new RecordingPlanner(mazeBlueprint);
    var delegatedGame = await new HybridPlanningEngine(recordingPlanner).PlanAsync("迷路ゲームを作って", StudioProjectKind.WindowsGame, LocalStudioSettings.DefaultModel);
    Check(recordingPlanner.CallCount == 1 && delegatedGame.Name == "迷路ゲーム", "non-pinball-game-request-not-rewritten");

    var tetrisBlueprint = BlueprintValidator.Validate(new StudioBlueprint(
        "テトリス",
        "キーボードで操作するオフラインのWindows落ち物ゲーム",
        "windows-game",
        ["ブロックの落下と左右操作", "ライン消去とスコア", "ゲームオーバーと再スタート"],
        [new("実装", "ゲームを作る"), new("検証", "EXEを作る")],
        [],
        ["ネットワーク通信を行わない"],
        ["workspace.read", "workspace.write"]));
    var unsafeWindowsSource = "using System.Diagnostics; class Broken { static void Main(){ Process.Start(\"cmd.exe\"); } }";
    var safeTetrisSource = """
        using System;
        using System.Drawing;
        using System.Windows.Forms;
        namespace LocalStudioGeneratedApp {
          internal static class Program {
            [STAThread] private static void Main() {
              Application.EnableVisualStyles();
              Application.SetCompatibleTextRenderingDefault(false);
              Application.Run(new TetrisForm());
            }
          }
          internal sealed class TetrisForm : Form {
            private readonly Timer gameTimer = new Timer();
            private readonly int[,] board = new int[10, 20];
            private int blockX = 4;
            private int blockY;
            private int rotation;
            private int score;
            private bool gameOver;
            internal TetrisForm() {
              Text = "LocalStudio テトリス";
              ClientSize = new Size(420, 620);
              BackColor = Color.FromArgb(22, 24, 32);
              DoubleBuffered = true;
              KeyPreview = true;
              KeyDown += HandleKey;
              gameTimer.Interval = 350;
              gameTimer.Tick += TickGame;
              gameTimer.Start();
            }
            private void HandleKey(object sender, KeyEventArgs e) {
              if (e.KeyCode == Keys.R) { Restart(); return; }
              if (gameOver) return;
              if (e.KeyCode == Keys.Left) blockX = Math.Max(0, blockX - 1);
              if (e.KeyCode == Keys.Right) blockX = Math.Min(8, blockX + 1);
              if (e.KeyCode == Keys.Down) blockY++;
              if (e.KeyCode == Keys.Up) RotatePiece();
              Invalidate();
            }
            private void TickGame(object sender, EventArgs e) {
              if (gameOver) return;
              blockY++;
              if (blockY > 18) { board[blockX, 19] = 1; ClearLines(); score += 100; blockY = 0; blockX = 4; }
              if (score >= 1000) { gameOver = true; gameTimer.Stop(); }
              Invalidate();
            }
            private void RotatePiece() { rotation = (rotation + 1) % 4; }
            private void ClearLines() {
              for (var y = 0; y < 20; y++) {
                var full = true;
                for (var x = 0; x < 10; x++) if (board[x, y] == 0) full = false;
                if (!full) continue;
                for (var row = y; row > 0; row--) for (var x = 0; x < 10; x++) board[x, row] = board[x, row - 1];
                score += 1000;
              }
            }
            private void Restart() { score = 0; blockX = 4; blockY = 0; gameOver = false; gameTimer.Start(); Invalidate(); }
            protected override void OnPaint(PaintEventArgs e) {
              base.OnPaint(e);
              e.Graphics.FillRectangle(Brushes.DeepSkyBlue, 30 + blockX * 36, 60 + blockY * 26, 68, 48);
              e.Graphics.DrawString("SCORE " + score, Font, Brushes.White, 18, 18);
              if (gameOver) e.Graphics.DrawString("GAME OVER  Rで再スタート", new Font(Font.FontFamily, 18), Brushes.White, 45, 290);
            }
          }
        }
        """;
    var windowsResponses = new SequenceJsonHandler(
        TestData.WindowsCandidateEnvelope(unsafeWindowsSource),
        TestData.WindowsPatchStream(unsafeWindowsSource, safeTetrisSource));
    var repairedFrameworkUsings = LocalWindowsAppGenerationEngine.AddSafeFrameworkUsings(
        "class UsesSafeCollection { private List<int> values = new List<int>(); }");
    Check(repairedFrameworkUsings.StartsWith("using System.Collections.Generic;", StringComparison.Ordinal), "windows-safe-framework-using-is-repaired-deterministically");
    Check(LocalWindowsAppGenerationEngine.UnwrapWholeSourceFence("```csharp\nclass App { }\n```") == "class App { }", "windows-whole-source-fence-is-unwrapped");
    // Since 2026-10-19 the program is taken out of an explained reply (it still has to compile and pass the behaviour
    // check); text without any fenced class is left as it is, for the source check to refuse.
    Check(LocalWindowsAppGenerationEngine.UnwrapWholeSourceFence("explanation\n```csharp\nclass App { }\n```") == "class App { }" &&
          LocalWindowsAppGenerationEngine.UnwrapWholeSourceFence("explanation\n```\nrun csc\n```").StartsWith("explanation", StringComparison.Ordinal),
        "windows-program-is-taken-out-of-an-explained-reply-only-when-a-class-is-fenced");
    Check(LocalWindowsAppGenerationEngine.ValidateSource("var text = $\"score {score}\";").Contains("source.csharp5-compatibility"), "windows-csharp5-interpolation-is-rejected-before-compile");
    Check(!LocalWindowsAppGenerationEngine.ValidateSource(new string('a', 15_000)).Contains("source.missing-or-too-large"), "windows-source-bound-allows-bounded-complex-app");
    Check(LocalWindowsAppGenerationEngine.ValidateSource(new string('a', 16_001)).Contains("source.missing-or-too-large"), "windows-source-bound-remains-enforced");
    var pickedFiles = "var dialog = new FolderBrowserDialog(); foreach (var f in Directory.GetFiles(dialog.SelectedPath)) File.Move(f, f + \".bak\");";
    Check(!LocalWindowsAppGenerationEngine.ValidateSource(pickedFiles).Contains("security.user-storage-only") &&
          LocalWindowsAppGenerationEngine.ValidateSource("var p = \"x\"; File.ReadAllText(p);").Contains("security.user-storage-only") &&
          LocalWindowsAppGenerationEngine.ValidateSource(pickedFiles + " File.Delete(f);").Contains("security.no-delete") &&
          LocalWindowsAppGenerationEngine.ValidateSource(pickedFiles + " new DirectoryInfo(d).Delete(true);").Contains("security.no-delete"),
        "windows-apps-use-user-picked-files-but-never-delete");
    Check(!LocalWindowsAppGenerationEngine.ValidateSource("void ProcessFiles(string folder) { } // processed").Any(error => error.StartsWith("security.forbidden-api", StringComparison.Ordinal)) &&
          LocalWindowsAppGenerationEngine.ValidateSource("System.Diagnostics.Process.Start(\"notepad\");").Contains("security.forbidden-api:Process.Start") &&
          LocalWindowsAppGenerationEngine.ValidateSource("var u = \"https://example.com\";").Any(error => error.StartsWith("security.forbidden-api:https://", StringComparison.Ordinal)),
        "windows-forbidden-apis-are-matched-as-api-names-and-named");
    var usingHelper = LocalWindowsAppGenerationEngine.AddSafeFrameworkUsings("using System;\nusing XlsxLite;\nclass A { void M() { var rows = XlsxLite.ReadSheet(\"a.xlsx\"); } }");
    Check(!usingHelper.Contains("using XlsxLite", StringComparison.Ordinal) && usingHelper.Contains("XlsxLite.ReadSheet", StringComparison.Ordinal),
        "using-a-helper-class-as-a-namespace-is-removed");
    Check(LocalWindowsAppGenerationEngine.ExplainFailure("compile.failed: Program.cs(38,84): error CS1913: Member 'Click' cannot be initialized.").Contains("button.Click += OnClick", StringComparison.Ordinal) &&
          LocalWindowsAppGenerationEngine.ExplainFailure("compile.failed: Program.cs(15,18): error CS1031: Type expected").Contains("KeyValuePair<string, int>", StringComparison.Ordinal) &&
          LocalWindowsAppGenerationEngine.ExplainFailure("compile.failed: Program.cs(21,49): error CS0102: The type 'A.MainForm' already contains a definition for 'items'").Contains("declares 'items' twice", StringComparison.Ordinal) &&
          LocalWindowsAppGenerationEngine.ExplainFailure("compile.failed: Program.cs(65,20): error CS1061: 'System.Random' does not contain a definition for 'Shuffle'").Contains("random.Next(i + 1)", StringComparison.Ordinal),
        "csharp5-compile-errors-come-with-the-way-to-write-it");
    var nestedFixed = WindowsSourceAutoFix.RewriteNestedInitializerMembers("var b = new Button { Text = \"表\", FlatAppearance.BorderSize = 0, Size = new Size(80, 80), Font.Bold = x };");
    Check(nestedFixed == "var b = new Button { Text = \"表\", FlatAppearance = { BorderSize = 0 }, Size = new Size(80, 80), Font = { Bold = x } };" &&
          WindowsSourceAutoFix.RewriteNestedInitializerMembers("var l = new List<int> { a.B };") == "var l = new List<int> { a.B };",
        "nested-initializer-members-are-rewritten:" + nestedFixed);
    var strayFixed = WindowsSourceAutoFix.Apply("using System;\n```csharp\nclass A { string s = \"a\u0007\"; }\n```\n").Source;
    Check(!strayFixed.Contains("```", StringComparison.Ordinal) && !strayFixed.Contains('\u0007') && strayFixed.Contains("class A", StringComparison.Ordinal) &&
          LocalWindowsAppGenerationEngine.ValidateSource("class A { }\n```").Contains("source.invalid-format-or-unicode:markdown-fence") &&
          LocalWindowsAppGenerationEngine.ExplainFailure("source.invalid-format-or-unicode:replacement-character").Contains("U+FFFD", StringComparison.Ordinal),
        "stray-fences-and-control-characters-are-removed-and-named");
    var renamedIds = LocalWebAppGenerationEngine.RenameShellIds(new GeneratedWebAppCandidate("<p id=\"ls-status\"></p><div id=\"ls-status-area\"></div><span id=\"x\"></span><span id=\"x\"></span>", "document.getElementById('ls-status').textContent = 'ok';"));
    Check(System.Text.RegularExpressions.Regex.Matches(renamedIds.Body, "id=\"ls-status-area\"").Count == 1 && renamedIds.Body.Contains("id=\"ls-status-area-2\"", StringComparison.Ordinal) &&
          renamedIds.JavaScript.Contains("getElementById('ls-status-area-2')", StringComparison.Ordinal) && renamedIds.Body.Contains("id=\"x-2\"", StringComparison.Ordinal) &&
          LocalWindowsAppGenerationEngine.ExplainFailure("compile.failed: Program.cs(146,5): error CS1519: Invalid token '{' in class, struct, or interface member declaration").Contains("outside any method", StringComparison.Ordinal),
        "shell-ids-and-duplicate-ids-stay-unique:" + renamedIds.Body);
    Check(LocalWindowsAppGenerationEngine.IsInteractive("card.MouseClick += OnCardClick;") && LocalWindowsAppGenerationEngine.IsInteractive("timer.Tick += OnTick;") &&
          LocalWindowsAppGenerationEngine.IsInteractive("protected override void OnMouseDown(MouseEventArgs e) { }") && !LocalWindowsAppGenerationEngine.IsInteractive("int total = 0; total += 1;") &&
          !LocalWindowsAppGenerationEngine.AddSafeFrameworkUsings("using System;\nusing DocumentFormat.OpenXml.Packaging;\nclass A { }").Contains("DocumentFormat", StringComparison.Ordinal) &&
          LocalWindowsAppGenerationEngine.AddSafeFrameworkUsings("using System.Drawing.Printing;\nclass A { }").Contains("using System.Drawing.Printing;", StringComparison.Ordinal),
        "windows-interactivity-counts-any-event-and-outside-usings-go");
    var explained = "Here is the game:\n```csharp\nusing System;\npublic class GameForm : Form { }\n```\nRun it with csc.\n```\ncsc Program.cs\n```";
    Check(LocalWindowsAppGenerationEngine.UnwrapWholeSourceFence(explained) == "using System;\npublic class GameForm : Form { }" &&
          LocalWindowsAppGenerationEngine.UnwrapWholeSourceFence("class A { }") == "class A { }",
        "fenced-program-is-taken-out-of-an-explained-reply");
    Check(LocalWindowsAppGenerationEngine.RequirementsWithoutMechanism(["デスクトップを監視して締め切りを探す"], "class A { void M() { label.Text = \"x\"; } }").Count == 2 &&
          LocalWindowsAppGenerationEngine.RequirementsWithoutMechanism(["デスクトップを監視して締め切りを探す"], "var t = new Timer(); var d = DateTime.Today;").Count == 0 &&
          LocalWindowsAppGenerationEngine.RequirementsWithoutMechanism(["50個のエクセルを1つにまとめる"], "var rows = XlsxLite.ReadSheet(p);").Count == 0 &&
          LocalWindowsAppGenerationEngine.RequirementsWithoutMechanism(["拡張子ごとのフォルダーに振り分ける"], "listBox.Items.Add(f);").Count == 1 &&
          LocalWindowsAppGenerationEngine.RequirementsWithoutMechanism(["拡張子ごとのフォルダーに振り分ける"], "File.Move(f, Path.Combine(dir, name));").Count == 0 &&
          OutputTargetAdvisor.Analyze("PDFの文字を書き換えたりページを回転したりできるアプリ").RecommendedKind == StudioProjectKind.WindowsApp &&
          OutputTargetAdvisor.Analyze("PDFのページを並べ替えたり結合したりするアプリ").RecommendedKind == StudioProjectKind.DesktopOrWebApp &&
          LocalWindowsAppGenerationEngine.RequirementsWithoutMechanism(["PDFの文字を書き換える"], "MessageBox.Show(\"書き換えました\");").Count == 1 &&
          LocalWindowsAppGenerationEngine.RequirementsWithoutMechanism(["PDFの文字を書き換える"], "var doc = PdfLite.Open(path);").Count == 0 &&
          OutputTargetAdvisor.Analyze("50個のエクセルファイルを1つにまとめるアプリ").RecommendedKind == StudioProjectKind.WindowsApp &&
          OutputTargetAdvisor.Analyze("EXEで、PDFのページを回転するアプリ").RecommendedKind == StudioProjectKind.WindowsApp,
        "windows-mechanisms-and-part-based-form-choice");
    // The shapes of the false "passes" in the 2026-10-20 audit are refused; the working versions of the same apps are not.
    var hiddenInput = "class F : Form { TextBox box; Button go; F() { box = new TextBox(); go = new Button(); Controls.Add(go); /* Controls.Add(box); */ } }";
    var placedInput = "class F : Form { TextBox box; Button go; F() { box = new TextBox(); go = new Button(); Controls.AddRange(new Control[] { box, go }); } }";
    var lockedButton = "class F : Form { Button run = new Button(); F() { run.Enabled = false; Controls.Add(run); } }";
    var unlockedButton = "class F : Form { Button run = new Button(); F() { run.Enabled = false; Controls.Add(run); } void Pick() { run.Enabled = files.Count > 0; } }";
    var renameOnPick = "class F : Form { void pick_Click(object s, EventArgs e) { var d = new FolderBrowserDialog(); if (d.ShowDialog() == DialogResult.OK) foreach (var f in Directory.GetFiles(d.SelectedPath)) File.Move(f, f + \".bak\"); } }";
    var renameOnConfirm = "class F : Form { void pick_Click(object s, EventArgs e) { var d = new FolderBrowserDialog(); if (d.ShowDialog() == DialogResult.OK) { folder = d.SelectedPath; Preview(); } } void go_Click(object s, EventArgs e) { foreach (var f in Directory.GetFiles(folder)) File.Move(f, f + \".bak\"); } }";
    var stubButton = "class F : Form { void merge_Click(object s, EventArgs e) { MessageBox.Show(\"まとめました\"); } }";
    var dotPen = "class F : Form { void Canvas_MouseMove(object s, MouseEventArgs e) { g.DrawLine(pen, e.X, e.Y, e.X, e.Y); } }";
    var linePen = "class F : Form { void Canvas_MouseMove(object s, MouseEventArgs e) { g.DrawLine(pen, last.X, last.Y, e.X, e.Y); last = e.Location; } }";
    // These shapes are judged on their own; usability findings (labels, button widths, …) are checked separately below.
    IReadOnlyList<string> CoreShapes(string source) => LocalWindowsAppGenerationEngine.FalseSuccessShapes(source).Except(LocalWindowsAppGenerationEngine.UsabilityShapes(source)).ToArray();
    Check(CoreShapes(hiddenInput).SingleOrDefault()?.StartsWith("exe.control-not-on-screen:box", StringComparison.Ordinal) == true &&
          CoreShapes(placedInput).Count == 0 &&
          CoreShapes(lockedButton).SingleOrDefault()?.StartsWith("exe.never-enabled:run", StringComparison.Ordinal) == true &&
          CoreShapes(unlockedButton).Count == 0 &&
          CoreShapes(renameOnPick).SingleOrDefault()?.StartsWith("exe.changes-before-confirm:pick_Click", StringComparison.Ordinal) == true &&
          CoreShapes(renameOnConfirm).Count == 0 &&
          CoreShapes(stubButton).SingleOrDefault()?.StartsWith("exe.stub-handler:merge_Click", StringComparison.Ordinal) == true &&
          CoreShapes(dotPen).SingleOrDefault()?.StartsWith("exe.draw-dots", StringComparison.Ordinal) == true &&
          CoreShapes(linePen).Count == 0,
        "exe-false-success-shapes-are-refused-and-working-versions-pass");
    // The Excel merge of 2026-10-20: 50 workbooks read, only the first written, the school names gathered and dropped.
    var firstOnlyMerge = "class F : Form { void go_Click(object s, EventArgs e) { var schoolNames = new List<string>(); var allData = new List<IList<string[]>>();\n" +
                         " foreach (var file in Directory.GetFiles(folder, \"*.xlsx\")) { var data = XlsxLite.ReadSheet(file, \"Sheet1\"); schoolNames.Add(Path.GetFileNameWithoutExtension(file)); allData.Add(data); }\n" +
                         " var combined = new List<string[]>(); combined.AddRange(allData[0]); XlsxLite.WriteSheet(output, \"結果\", combined); } }";
    var wholeMerge = "class F : Form { void go_Click(object s, EventArgs e) { var combined = new List<string[]>();\n" +
                     " foreach (var file in Directory.GetFiles(folder, \"*.xlsx\")) { var school = Path.GetFileNameWithoutExtension(file); foreach (var row in XlsxLite.ReadSheet(file, \"Sheet1\")) { var line = new List<string> { school }; line.AddRange(row); combined.Add(line.ToArray()); } }\n" +
                     " XlsxLite.WriteSheet(output, \"結果\", combined); } }";
    var firstOnlyFindings = LocalWindowsAppGenerationEngine.FalseSuccessShapes(firstOnlyMerge);
    Check(firstOnlyFindings.Any(found => found.StartsWith("exe.collected-unused:schoolNames", StringComparison.Ordinal)) &&
          firstOnlyFindings.Any(found => found.StartsWith("exe.only-first:allData", StringComparison.Ordinal)) &&
          LocalWindowsAppGenerationEngine.FalseSuccessShapes(wholeMerge).Count == 0 &&
          LocalWebAppGenerationEngine.FalseSuccessShapes("", "const rows = [];\nfor (const f of files) rows.push(await f.text());\nout.textContent = rows[0];").SingleOrDefault()?.StartsWith("behavior.only-first:rows", StringComparison.Ordinal) == true &&
          LocalWebAppGenerationEngine.FalseSuccessShapes("", "const names = [];\nfor (const f of files) names.push(f.name);\nout.textContent = 'done';").SingleOrDefault()?.StartsWith("behavior.collected-unused:names", StringComparison.Ordinal) == true &&
          LocalWebAppGenerationEngine.FalseSuccessShapes("", "const rows = [];\nfor (const f of files) rows.push(await f.text());\nout.textContent = rows.join('\\n');").Count == 0,
        "gathered-then-dropped-data-is-refused:" + string.Join("|", firstOnlyFindings.Select(found => found.Split(':')[0] + ":" + found.Split(':')[1])));
    Check(LocalWebAppGenerationEngine.FalseSuccessShapes("<button id=\"run\" disabled>変換</button>", "run.addEventListener('click', convert);").SingleOrDefault()?.StartsWith("behavior.never-enabled", StringComparison.Ordinal) == true &&
          LocalWebAppGenerationEngine.FalseSuccessShapes("<button id=\"run\" disabled>変換</button>", "input.addEventListener('input', () => { run.disabled = input.value === ''; });").Count == 0 &&
          LocalWebAppGenerationEngine.FalseSuccessShapes("", "function convert() {\n const from = document.getElementById('unit').value;\n const to = document.getElementById('unit').value;\n}").SingleOrDefault()?.StartsWith("behavior.same-field-twice:unit", StringComparison.Ordinal) == true &&
          LocalWebAppGenerationEngine.FalseSuccessShapes("", "function convert() {\n const from = document.getElementById('from-unit').value;\n const to = document.getElementById('to-unit').value;\n}").Count == 0 &&
          LocalWebAppGenerationEngine.FalseSuccessShapes("", "merge.addEventListener('click', () => { alert('まとめました'); });").SingleOrDefault()?.StartsWith("behavior.stub-handler", StringComparison.Ordinal) == true &&
          LocalWebAppGenerationEngine.FalseSuccessShapes("", "merge.addEventListener('click', () => { status.textContent = `${rows.length}行をまとめました`; });").Count == 0,
        "web-false-success-shapes-are-refused-and-working-versions-pass");
    var growthCheckFolder = Path.Combine(Path.GetTempPath(), "ls-growth-check-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(growthCheckFolder);
    await File.WriteAllTextAsync(Path.Combine(growthCheckFolder, "Program.cs"), "class InvoiceForm { void Show() { totalLabel.Text = total.ToString(); } }");
    var printCheck = FeatureGrowth.WithMechanism((_, _) => Task.FromResult<IReadOnlyList<string>>([]), StudioProjectKind.WindowsApp, "印刷できる請求書の形で表示できます");
    var withoutPrint = await printCheck(growthCheckFolder, CancellationToken.None);
    await File.WriteAllTextAsync(Path.Combine(growthCheckFolder, "Program.cs"), "class InvoiceForm { PrintDocument doc = new PrintDocument(); }");
    var withPrint = await printCheck(growthCheckFolder, CancellationToken.None);
    Check(withoutPrint.Count == 1 && withoutPrint[0].Contains("mechanism-missing:print", StringComparison.Ordinal) && withPrint.Count == 0,
        "an-added-feature-must-bring-its-mechanism");
    Check(LocalWindowsAppGenerationEngine.CollapseExcessBlankLines("\nclass App {\n\n\n    int score;   \n\n\n}\n\n") ==
          "class App {\n\n    int score;\n\n}\n",
        "windows-generated-source-collapses-excess-blank-lines");
    Check(LocalWindowsAppGenerationEngine.ApplyExactPatch(
        "class App { int score = 0; }",
        new WindowsSourcePatchCandidate([new("int score = 0;", "int score = 100;")])) ==
        "class App { int score = 100; }", "windows-exact-repair-patch-applies-once");
    Check(LocalWindowsAppGenerationEngine.ApplyExactPatch(
        "class App { int score = 0; }",
        new WindowsSourcePatchCandidate([
            new("class App", "class App"),
            new("int score = 0;", "int score = 100;")])) ==
        "class App { int score = 100; }", "windows-mixed-repair-skips-no-change-and-applies-effective-replacement");
    CheckThrows(() => LocalWindowsAppGenerationEngine.ApplyExactPatch(
        "class App { }",
        new WindowsSourcePatchCandidate([new("class App", "class App")])), "windows-all-no-change-repair-is-rejected");
    var duplicateFieldSource = "class App {\n    private bool gameOver = false;\n    private int score;\n    private bool gameOver = false;\n}";
    var deduplicatedFieldSource = LocalWindowsAppGenerationEngine.RemoveDuplicateExactPrivateFieldDeclarations(duplicateFieldSource);
    Check(deduplicatedFieldSource.Split("private bool gameOver = false;", StringSplitOptions.None).Length == 2 &&
          deduplicatedFieldSource.Contains("private int score;", StringComparison.Ordinal),
        "windows-identical-private-field-declaration-is-deduplicated-deterministically");
    var distinctFieldSource = LocalWindowsAppGenerationEngine.RemoveDuplicateExactPrivateFieldDeclarations(
        "class App {\n    private bool first = false;\n    private bool second = false;\n}");
    Check(distinctFieldSource.Contains("private bool first", StringComparison.Ordinal) &&
          distinctFieldSource.Contains("private bool second", StringComparison.Ordinal),
        "windows-distinct-private-fields-are-preserved");
    Check(LocalWindowsAppGenerationEngine.RepairRedundantTetrominoConstruction(
        "Tetromino next = new Tetromino(Tetromino.RandomShape());") ==
        "Tetromino next = Tetromino.RandomShape();",
        "windows-redundant-tetromino-wrapper-is-repaired-deterministically");
    var repairedTetrominoColors = LocalWindowsAppGenerationEngine.RepairTetrominoColorIndexing(
        "public static readonly KnownColor[] Colors = { KnownColor.Cyan, KnownColor.Blue, KnownColor.Orange, KnownColor.Yellow, KnownColor.Green, KnownColor.Purple, KnownColor.Red };\nColor = Colors[Array.IndexOf(Shapes, shape)];");
    Check(repairedTetrominoColors.Contains("KnownColor.Gray, KnownColor.Cyan", StringComparison.Ordinal) &&
          repairedTetrominoColors.Contains("Color = Array.IndexOf(Shapes, shape) + 1;", StringComparison.Ordinal) &&
          !repairedTetrominoColors.Contains("Color = Colors[", StringComparison.Ordinal),
        "windows-tetromino-color-index-reserves-zero-for-empty-cells");
    var repairedKnownColorBrush = LocalWindowsAppGenerationEngine.RepairUnsupportedKnownColorBrushes(
        "class App {\n    private void DrawBoard(Graphics g) { g.FillRectangle(Brushes.FromKnownColor(KnownColor.Red), 0, 0, 1, 1); }\n}");
    Check(!repairedKnownColorBrush.Contains("Brushes.FromKnownColor", StringComparison.Ordinal) &&
          repairedKnownColorBrush.Contains("private static Brush GetKnownColorBrush", StringComparison.Ordinal) &&
          repairedKnownColorBrush.Contains("case KnownColor.Red: return Brushes.Red;", StringComparison.Ordinal),
        "windows-unsupported-known-color-brush-is-repaired-with-shared-brush-map");
    var repairedSolidKnownColorBrush = LocalWindowsAppGenerationEngine.RepairUnsupportedKnownColorBrushes(
        "class App {\n    private void DrawBoard(Graphics g) { g.FillRectangle(new SolidBrush(Tetromino.Colors[currentPiece.Color]), 0, 0, 1, 1); }\n}");
    Check(!repairedSolidKnownColorBrush.Contains("new SolidBrush(Tetromino.Colors", StringComparison.Ordinal) &&
          repairedSolidKnownColorBrush.Contains("GetKnownColorBrush(Tetromino.Colors[currentPiece.Color])", StringComparison.Ordinal) &&
          repairedSolidKnownColorBrush.Contains("private static Brush GetKnownColorBrush", StringComparison.Ordinal),
        "windows-solid-known-color-brush-is-repaired-with-shared-brush-map");
    var repairedIntegerColorHelper = LocalWindowsAppGenerationEngine.RepairUnsupportedKnownColorBrushes(
        "private static Brush GetKnownColorBrush(int color) { switch (color) { case 0: return Brushes.Cyan; case 1: return Brushes.Blue; case 2: return Brushes.Orange; case 3: return Brushes.Yellow; case 4: return Brushes.Green; case 5: return Brushes.Purple; case 6: return Brushes.Red; default: return Brushes.Gray; } }");
    Check(repairedIntegerColorHelper.Contains("GetKnownColorBrush(KnownColor color)", StringComparison.Ordinal) &&
          repairedIntegerColorHelper.Contains("case KnownColor.Cyan: return Brushes.Cyan;", StringComparison.Ordinal) &&
          !repairedIntegerColorHelper.Contains("case 0:", StringComparison.Ordinal),
        "windows-integer-known-color-helper-is-repaired-consistently");
    var repairedLeakingColorHelper = LocalWindowsAppGenerationEngine.RepairUnsupportedKnownColorBrushes(
        "private static Brush GetKnownColorBrush(KnownColor color) { return new SolidBrush(color); }");
    Check(!repairedLeakingColorHelper.Contains("new SolidBrush(color)", StringComparison.Ordinal) &&
          repairedLeakingColorHelper.Contains("case KnownColor.Red: return Brushes.Red;", StringComparison.Ordinal),
        "windows-known-color-helper-reuses-shared-brushes");
    Check(LocalWindowsAppGenerationEngine.ApplyExactPatch(
        "class App {\r\n    int score = 0;\r\n}",
        new WindowsSourcePatchCandidate([new("class App {\n  int score = 0;\n}", "class App {\n    int score = 100;\n}")])) ==
        "class App {\n    int score = 100;\n}", "windows-repair-patch-tolerates-only-unique-whitespace-differences");
    CheckThrows(() => LocalWindowsAppGenerationEngine.ApplyExactPatch(
        "class App { int score; int otherScore; }",
        new WindowsSourcePatchCandidate([new("int", "long")])), "windows-ambiguous-repair-patch-is-rejected");
    CheckThrows(() => LocalWindowsAppGenerationEngine.ApplyExactPatch(
        "class App { }",
        new WindowsSourcePatchCandidate(null!)), "windows-null-repair-list-is-rejected-without-crashing");
    var alternativeLifecycleSource = "class TetrisForm { int[,] board; int points; bool gameEnded; void ResetGame(){} void RotatePiece(){} void ClearLines(){} void Render(){ DrawString(\"Score\"); var message=\"Game Over\"; } void KeysTest(){ var a=Keys.Left; var b=Keys.Right; var c=Keys.Down; var r=Keys.R; } }";
    var alternativeLifecycleErrors = LocalWindowsAppGenerationEngine.ValidateRequirementGrounding(alternativeLifecycleSource, tetrisBlueprint, StudioProjectKind.WindowsGame);
    Check(!alternativeLifecycleErrors.Any(error => error.StartsWith("requirements.tetris-score", StringComparison.Ordinal) || error.StartsWith("requirements.tetris-game-over", StringComparison.Ordinal) || error.StartsWith("requirements.tetris-restart", StringComparison.Ordinal)), "tetris-equivalent-lifecycle-names-are-accepted");
    var missingRestartSource = alternativeLifecycleSource.Replace("var r=Keys.R;", "", StringComparison.Ordinal);
    Check(LocalWindowsAppGenerationEngine.ValidateRequirementGrounding(missingRestartSource, tetrisBlueprint, StudioProjectKind.WindowsGame).Contains("requirements.tetris-restart"), "tetris-restart-key-is-required");
    var nextPieceBlueprint = tetrisBlueprint with { Features = tetrisBlueprint.Features.Append("次のピースを表示する").ToArray() };
    Check(LocalWindowsAppGenerationEngine.ValidateRequirementGrounding(safeTetrisSource, nextPieceBlueprint, StudioProjectKind.WindowsGame).Contains("requirements.tetris-next-piece"), "tetris-next-piece-request-is-not-satisfied-by-score-only");
    var nextPieceSource = safeTetrisSource + " class PreviewSupport { Tetromino nextPiece; void DrawNextPiece(){ var label=\"NEXT\"; } } class Tetromino { }";
    Check(!LocalWindowsAppGenerationEngine.ValidateRequirementGrounding(nextPieceSource, nextPieceBlueprint, StudioProjectKind.WindowsGame).Contains("requirements.tetris-next-piece"), "tetris-next-piece-requires-state-and-visible-preview");
    var wrongGenreErrors = LocalWindowsAppGenerationEngine.ValidateRequirementGrounding(
        "class Shooter { private Bullet bullet; private Enemy enemy; }",
        tetrisBlueprint,
        StudioProjectKind.WindowsGame);
    Check(wrongGenreErrors.Contains("requirements.tetris-not-another-game"), "windows-game-genre-drift-is-rejected");
    await using var windowsRuntime = new FakeLocalModelRuntime(new Uri("http://127.0.0.1:54322/"));
    using var windowsEngine = new LocalWindowsAppGenerationEngine(
        windowsRuntime,
        new FakeSettingsStore(LocalStudioSettings.Default),
        smokePaths,
        new HttpClient(windowsResponses));
    var generatedWindowsSource = await windowsEngine.GenerateAsync(tetrisBlueprint, StudioProjectKind.WindowsGame);
    Check(windowsResponses.RequestCount == 2 && generatedWindowsSource.ImplementationMode == "generated-local-windows-executable", "windows-generation-repairs-rejected-candidate");
    var transientWindowsResponses = new TransientRepairJsonHandler(
        TestData.WindowsCandidateEnvelope(unsafeWindowsSource),
        TestData.WindowsPatchStream(unsafeWindowsSource, safeTetrisSource));
    using var transientWindowsEngine = new LocalWindowsAppGenerationEngine(
        windowsRuntime,
        new FakeSettingsStore(LocalStudioSettings.Default),
        smokePaths,
        new HttpClient(transientWindowsResponses));
    var transientRecoveredSource = await transientWindowsEngine.GenerateAsync(tetrisBlueprint, StudioProjectKind.WindowsGame);
    Check(transientWindowsResponses.RequestCount == 3 && transientRecoveredSource.ImplementationMode == "generated-local-windows-executable", "windows-generation-recovers-from-one-transient-model-response-failure");
    var checkpointBlueprint = tetrisBlueprint with { Name = tetrisBlueprint.Name + " checkpoint" };
    var failedCheckpointResponses = new SequenceJsonHandler(
        TestData.WindowsCandidateEnvelope(unsafeWindowsSource),
        "{bad",
        "{bad");
    using (var failedCheckpointEngine = new LocalWindowsAppGenerationEngine(
        windowsRuntime,
        new FakeSettingsStore(LocalStudioSettings.Default),
        smokePaths,
        new HttpClient(failedCheckpointResponses)))
    {
        await CheckThrowsAsync(
            () => failedCheckpointEngine.GenerateAsync(checkpointBlueprint, StudioProjectKind.WindowsGame),
            "windows-rejected-candidate-run-remains-bounded");
    }
    var resumedCheckpointResponses = new SequenceJsonHandler(TestData.WindowsPatchStream(unsafeWindowsSource, safeTetrisSource));
    using (var resumedCheckpointEngine = new LocalWindowsAppGenerationEngine(
        windowsRuntime,
        new FakeSettingsStore(LocalStudioSettings.Default),
        smokePaths,
        new HttpClient(resumedCheckpointResponses)))
    {
        var resumedCheckpointSource = await resumedCheckpointEngine.GenerateAsync(checkpointBlueprint, StudioProjectKind.WindowsGame);
        Check(resumedCheckpointResponses.RequestCount == 1 && resumedCheckpointSource.ImplementationMode == "generated-local-windows-executable", "windows-rejected-candidate-resumes-from-private-checkpoint");
    }
    generatedWindowsSource = generatedWindowsSource with
    {
        Files = generatedWindowsSource.Files.Append(new GeneratedTextFile(
            "Support.cs",
            "internal static class SupportMarker { public const int Version = 1; }\n")).ToArray()
    };
    var tetrisProject = await new ProjectScaffolder(
        smokePaths,
        windowsGenerativeGenerator: new FixedGenerativeProjectGenerator(generatedWindowsSource)).CreateAsync(
            tetrisBlueprint,
            StudioProjectKind.WindowsGame);
    var tetrisExe = Path.Combine(tetrisProject, "LocalStudioApp.exe");
    var tetrisBytes = await File.ReadAllBytesAsync(tetrisExe);
    Check(tetrisBytes.Length > 2 && tetrisBytes[0] == (byte)'M' && tetrisBytes[1] == (byte)'Z', "generated-tetris-is-real-windows-executable");
    Check(File.Exists(Path.Combine(tetrisProject, "Support.cs")), "generated-windows-project-compiles-multiple-source-files");
    Check(File.ReadAllText(Path.Combine(tetrisProject, "localstudio.json")).Contains("generated-local-windows-executable"), "generated-tetris-mode-recorded");
    var tetrisVerification = new ProjectVerificationStatusStore(smokePaths).Load(tetrisProject);
    Check(tetrisVerification.Applicable && tetrisVerification.Level == "pending" && tetrisVerification.Features.Count == 3, "generated-tetris-awaits-guided-runtime-verification");

    var environmentFailureResponses = new SequenceJsonHandler(TestData.WindowsCandidateEnvelope(safeTetrisSource));
    await using var environmentFailureRuntime = new FakeLocalModelRuntime(new Uri("http://127.0.0.1:54323/"));
    using var environmentFailureEngine = new LocalWindowsAppGenerationEngine(
        environmentFailureRuntime,
        new FakeSettingsStore(LocalStudioSettings.Default),
        smokePaths,
        new HttpClient(environmentFailureResponses),
        new UnavailableWindowsCompiler());
    await CheckThrowsAsync(
        () => environmentFailureEngine.GenerateAsync(tetrisBlueprint, StudioProjectKind.WindowsGame),
        "windows-build-environment-failure-stops-without-model-loop");
    Check(environmentFailureResponses.RequestCount == 1, "windows-build-environment-failure-is-not-retried");

    var gasBlueprint = BlueprintValidator.Validate(new StudioBlueprint("通知GAS", "予定を確認して通知するGAS", "gas", ["予定の確認"], [new("実装", "GASを作る")], [], [], ["workspace.read"]));
    // Since 3-3 GAS is written by the local model and checked (never a fixed template); without that generator nothing is saved.
    Check(ImplementationSupportPolicy.Evaluate(gasBlueprint, StudioProjectKind.GoogleAppsScript).Route == ProjectImplementationRoute.GeneratedAppsScript, "gas-stub-not-treated-as-complete");
    await CheckThrowsAsync(() => new ProjectScaffolder(smokePaths).CreateAsync(gasBlueprint, StudioProjectKind.GoogleAppsScript), "gas-empty-stub-not-saved");


    var scaffoldBlueprint = BlueprintValidator.Validate(new StudioBlueprint("AgentSmoke", "安全な雛形", "agent", ["確認する"], [new("作成", "雛形")], [], [], ["workspace.read", "external.publish"]));
    var created = await new ProjectScaffolder(smokePaths).CreateAsync(scaffoldBlueprint, StudioProjectKind.Agent);
    var scaffoldBaseline = File.ReadAllText(Path.Combine(created, ".localstudio", "baseline", "manifest.json"));
    Check(scaffoldBaseline.Contains("\"status\": \"safe-scaffold\""), "safe-scaffold-baseline-created");
    var agentJson = File.ReadAllText(Path.Combine(created, "agent.json"));
    Check(agentJson.Contains("\"executionEnabled\": false"), "agent-disabled-by-default");
    Check(agentJson.Contains("\"grantedPermissions\": []"), "agent-no-default-grants");
    Check(agentJson.Contains("\"artifactType\": \"design-scaffold\""), "agent-truthful-stage");
    Check(!agentJson.Contains("external.publish"), "agent-external-capability-stripped");
    var agentVerification = File.ReadAllText(Path.Combine(created, ".localstudio", "verification", "initial.json"));
    Check(agentVerification.Contains("\"result\": \"PASS\""), "agent-scaffold-verification-evidence");

    var promptPath = Path.Combine(created, "PROMPT.md");
    var originalPrompt = File.ReadAllText(promptPath);
    var workspaceEngine = new SafeWorkspaceEngine(smokePaths);
    var rollbackSession = await workspaceEngine.StartAsync(created);
    await CheckThrowsAsync(() => rollbackSession.ReplaceTextAsync("PROMPT.md", "確認済み", "検証済み"), "replace-requires-read");
    await CheckThrowsAsync(() => rollbackSession.ReadTextAsync("..\\outside.txt"), "path-traversal-blocked");
    await CheckThrowsAsync(() => rollbackSession.ReadTextAsync(".localstudio\\ledger.json"), "control-area-blocked");
    await rollbackSession.ReadTextAsync("PROMPT.md");
    var edit = await rollbackSession.ReplaceTextAsync("PROMPT.md", "確認済み", "検証済み");
    Check(edit.Status == WorkspaceEditStatus.Changed && rollbackSession.NeedsVerification, "safe-replace-needs-verification");
    await CheckThrowsAsync(() => rollbackSession.CompleteAsync(), "unverified-completion-blocked");
    await rollbackSession.RollbackAsync();
    Check(File.ReadAllText(promptPath) == originalPrompt, "run-snapshot-rollback");

    var acceptSession = await workspaceEngine.StartAsync(created);
    await acceptSession.ReadTextAsync("PROMPT.md");
    await acceptSession.ReplaceTextAsync("PROMPT.md", "確認済み", "検証済み");
    await acceptSession.RecordVerificationAsync(true, "smoke test passed");
    Check(acceptSession.IsVerified && !acceptSession.NeedsVerification, "verification-gate-pass");
    await acceptSession.AcceptBaselineAsync();
    var baselinePrompt = File.ReadAllText(Path.Combine(created, ".localstudio", "baseline", "files", "PROMPT.md"));
    Check(baselinePrompt.Contains("検証済み"), "accepted-baseline-updated");

    var noChangeSession = await workspaceEngine.StartAsync(created);
    var currentPrompt = await noChangeSession.ReadTextAsync("PROMPT.md");
    var uniqueLine = "検証済みの目的と権限だけをここへ追加します。";
    var noChange = await noChangeSession.ReplaceTextAsync("PROMPT.md", uniqueLine, uniqueLine);
    Check(noChange.Status == WorkspaceEditStatus.NoChange, "first-no-change-reported");
    await CheckThrowsAsync(() => noChangeSession.ReplaceTextAsync("PROMPT.md", uniqueLine, uniqueLine), "second-no-change-blocked-engine");

    var ambiguousSession = await workspaceEngine.StartAsync(created);
    await ambiguousSession.ReadTextAsync("PROMPT.md");
    await ambiguousSession.ReplaceTextAsync("PROMPT.md", "検証済み", "権限 権限");
    await ambiguousSession.ReadTextAsync("PROMPT.md");
    await CheckThrowsAsync(() => ambiguousSession.ReplaceTextAsync("PROMPT.md", "権限", "許可"), "ambiguous-replace-blocked");
    await CheckThrowsAsync(() => ambiguousSession.ReplaceTextAsync("PROMPT.md", "権限 権限", "破損\uFFFD"), "unicode-replacement-blocked");
    await ambiguousSession.RollbackAsync();

    var staleSession = await workspaceEngine.StartAsync(created);
    await staleSession.ReadTextAsync("PROMPT.md");
    var beforeExternalChange = File.ReadAllText(promptPath);
    await File.AppendAllTextAsync(promptPath, "外部変更");
    await CheckThrowsAsync(() => staleSession.ReplaceTextAsync("PROMPT.md", "検証済み", "確認済み"), "stale-read-blocked");
    await File.WriteAllTextAsync(promptPath, beforeExternalChange);
    await staleSession.CompleteAsync();

    // The agent loop: looks, edits, is checked after every change, may finish only after a passing check.
    var agentCheck = new Func<string, CancellationToken, Task<IReadOnlyList<string>>>(async (root, token) =>
        (await File.ReadAllTextAsync(Path.Combine(root, "PROMPT.md"), token)).Contains("AGENT-OK", StringComparison.Ordinal) ? [] : ["PROMPT.md must contain AGENT-OK"]);
    var agentModel = new ScriptedAgentModel(
        "{\"action\":\"finish\"}",
        "{\"action\":\"search\",\"query\":\"検証済み\"}",
        "{\"action\":\"read\",\"path\":\"..\\\\outside.txt\"}",
        "{\"action\":\"read\",\"path\":\"PROMPT.md\",\"start_line\":1,\"end_line\":3}",
        "not json",
        "{\"action\":\"replace\",\"path\":\"PROMPT.md\",\"old_text\":\"" + uniqueLine + "\",\"new_text\":\"" + uniqueLine + " AGENT-OK\"}",
        "{\"action\":\"finish\",\"note\":\"done\"}");
    var agentSession = await workspaceEngine.StartAsync(created);
    var agentOutcome = await new AgentLoop(agentModel, agentCheck).RunAsync(agentSession, created, "PROMPT.md に AGENT-OK を書く");
    Check(agentOutcome.Completed && agentOutcome.Reason == "done" && agentOutcome.Checks == 2 &&
          agentOutcome.Steps[0] is { Action: "finish", Ok: false } && agentOutcome.Steps[2] is { Ok: false } && agentOutcome.Steps[4] is { Action: "invalid", Ok: false } &&
          agentModel.Seen[0].Contains("PROMPT.md (", StringComparison.Ordinal) && agentModel.Seen[0].Contains("must contain AGENT-OK", StringComparison.Ordinal) &&
          agentModel.Seen[2].Contains("PROMPT.md:", StringComparison.Ordinal) && agentModel.Seen[4].Contains("1: ", StringComparison.Ordinal) &&
          agentModel.Seen[6].Contains("All checks passed", StringComparison.Ordinal) &&
          File.ReadAllText(promptPath).Contains("AGENT-OK", StringComparison.Ordinal) && agentSession.IsVerified,
        "agent-loop-reads-edits-checks-and-finishes-only-after-passing");
    await agentSession.RollbackAsync();
    // After each effective edit the next turn receives the current source, not the initial stale source. Otherwise a
    // small model copies an old line into old_text repeatedly and cannot make a second coherent replacement.
    var refreshedModel = new ScriptedAgentModel(
        "{\"action\":\"replace\",\"path\":\"PROMPT.md\",\"old_text\":\"" + uniqueLine + "\",\"new_text\":\"" + uniqueLine + " AGENT-FIRST\"}",
        "{\"action\":\"replace\",\"path\":\"PROMPT.md\",\"old_text\":\"" + uniqueLine + " AGENT-FIRST\",\"new_text\":\"" + uniqueLine + " AGENT-OK\"}",
        "{\"action\":\"finish\",\"note\":\"fresh\"}");
    var refreshedSession = await workspaceEngine.StartAsync(created);
    var refreshedOutcome = await new AgentLoop(refreshedModel, agentCheck).RunAsync(refreshedSession, created, "PROMPT.mdを段階修正する");
    Check(refreshedOutcome.Completed && refreshedOutcome.Checks == 3 &&
          refreshedModel.FullSeen[1].Contains("AGENT-FIRST", StringComparison.Ordinal) &&
          !refreshedModel.FullSeen[0].Contains("AGENT-FIRST", StringComparison.Ordinal) &&
          File.ReadAllText(promptPath).Contains("AGENT-OK", StringComparison.Ordinal),
        "agent-loop-refreshes-current-source-after-each-effective-edit");
    await refreshedSession.RollbackAsync();
    var stuckModel = new ScriptedAgentModel(Enumerable.Repeat("{\"action\":\"read\",\"path\":\"missing.txt\"}", 10).ToArray());
    var stuckSession = await workspaceEngine.StartAsync(created);
    var stuckOutcome = await new AgentLoop(stuckModel, agentCheck).RunAsync(stuckSession, created, "x");
    Check(!stuckOutcome.Completed && stuckOutcome.Steps.Count == 3 &&
          stuckOutcome.Reason == "stopped: the same rejected action and result were repeated three times" &&
          File.ReadAllText(promptPath) == beforeExternalChange,
        "agent-loop-stops-identical-rejected-action-after-three-and-rolls-back");
    await stuckSession.RollbackAsync();
    // A different occurrence/new_text is a distinct repair attempt even when the action, path and old_text match.
    // The loop must not stop those progressive attempts as if the model had repeated one rejected operation.
    var distinctRepairModel = new ScriptedAgentModel(
        "{\"action\":\"replace\",\"path\":\"PROMPT.md\",\"old_text\":\"missing\",\"new_text\":\"first\"}",
        "{\"action\":\"replace\",\"path\":\"PROMPT.md\",\"old_text\":\"missing\",\"new_text\":\"second\",\"occurrence\":2}",
        "{\"action\":\"replace\",\"path\":\"PROMPT.md\",\"old_text\":\"missing\",\"new_text\":\"third\",\"occurrence\":3}");
    var distinctRepairSession = await workspaceEngine.StartAsync(created);
    var distinctRepairOutcome = await new AgentLoop(distinctRepairModel, agentCheck, 3).RunAsync(distinctRepairSession, created, "x");
    Check(!distinctRepairOutcome.Completed && distinctRepairOutcome.Steps.Count == 3 && distinctRepairOutcome.Reason == "step limit reached",
        "agent-loop-does-not-collapse-distinct-repair-attempts");
    await distinctRepairSession.RollbackAsync();
    // A refused replacement says where: how many times and on which lines, or the most similar line.
    var whereModel = new ScriptedAgentModel(
        "{\"action\":\"replace\",\"path\":\"PROMPT.md\",\"old_text\":\"\\n\",\"new_text\":\"\\n\\n\"}",
        "{\"action\":\"replace\",\"path\":\"PROMPT.md\",\"old_text\":\"" + uniqueLine.Replace("目的", "目標") + "\",\"new_text\":\"x\"}");
    var whereSession = await workspaceEngine.StartAsync(created);
    await new AgentLoop(whereModel, agentCheck, 4).RunAsync(whereSession, created, "x");
    Check(whereModel.Seen[1].Contains("old_text occurs", StringComparison.Ordinal) && whereModel.Seen[1].Contains("(lines 1, ", StringComparison.Ordinal) &&
          whereModel.Seen[2].Contains("The most similar line is", StringComparison.Ordinal) && whereModel.Seen[2].Contains(uniqueLine, StringComparison.Ordinal),
        "agent-loop-refusals-say-where-the-text-is");
    await whereSession.RollbackAsync();
    // The same text several times on one line: "occurrence" picks one.
    var occurrenceModel = new ScriptedAgentModel(
        "{\"action\":\"create\",\"path\":\"dup.js\",\"content\":\"x = 1; x = 1; x = 1;\"}",
        "{\"action\":\"replace\",\"path\":\"dup.js\",\"old_text\":\"x = 1;\",\"new_text\":\"x = 2;\"}",
        "{\"action\":\"replace\",\"path\":\"dup.js\",\"old_text\":\"x = 1;\",\"new_text\":\"x = 2;\",\"occurrence\":2}",
        "{\"action\":\"create\",\"path\":\"loose.js\",\"content\":\"if (ok) {\\n    total = a + b;\\n}\"}",
        "{\"action\":\"replace\",\"path\":\"loose.js\",\"old_text\":\"if (ok) { total = a + b; }\",\"new_text\":\"if (ok) { total = a + b + c; }\"}",
        "{\"action\":\"append\",\"path\":\"loose.js\",\"content\":\"function added() { return 1; }\"}");
    var occurrenceSession = await workspaceEngine.StartAsync(created);
    await new AgentLoop(occurrenceModel, agentCheck, 7).RunAsync(occurrenceSession, created, "x");
    var dupText = File.ReadAllText(Path.Combine(created, "dup.js"));
    Check(occurrenceModel.Seen[2].Contains("\"occurrence\": n (1 = the first, on line 1)", StringComparison.Ordinal) && dupText == "x = 1; x = 2; x = 1;",
        "agent-loop-replaces-the-chosen-occurrence-on-one-line:" + dupText);
    Check(File.ReadAllText(Path.Combine(created, "loose.js")) == "if (ok) { total = a + b + c; }\n\nfunction added() { return 1; }\n", "agent-loop-forgives-spacing-and-appends-new-code:" + File.ReadAllText(Path.Combine(created, "loose.js")));
    await occurrenceSession.RollbackAsync();
    var checkTwiceModel = new ScriptedAgentModel("{\"action\":\"check\"}", "{\"action\":\"check\"}");
    var checkTwiceSession = await workspaceEngine.StartAsync(created);
    await new AgentLoop(checkTwiceModel, agentCheck, 3).RunAsync(checkTwiceSession, created, "x");
    Check(!checkTwiceModel.Seen[1].Contains("Nothing has changed", StringComparison.Ordinal) && checkTwiceModel.Seen[2].Contains("Nothing has changed", StringComparison.Ordinal),
        "agent-loop-says-a-repeated-check-tells-nothing-new");
    await checkTwiceSession.RollbackAsync();

    // Large plans in parts: the core is generated, then each later feature is added by the loop or rolled back.
    var bigPlan = new StudioBlueprint("Big", "", "app", ["a", "b", "c", "d", "e"], [], [], [], []);
    var (bigCore, bigLater) = FeatureGrowth.Split(bigPlan);
    var growthModel = new ScriptedAgentModel(
        "{\"action\":\"replace\",\"path\":\"PROMPT.md\",\"old_text\":\"" + uniqueLine + "\",\"new_text\":\"" + uniqueLine + " AGENT-OK\"}",
        "{\"action\":\"finish\",\"note\":\"added\"}",
        "{\"action\":\"read\",\"path\":\"missing.txt\"}", "{\"action\":\"read\",\"path\":\"missing.txt\"}", "{\"action\":\"read\",\"path\":\"missing.txt\"}");
    var growthFeatureTwo = new Func<string, CancellationToken, Task<IReadOnlyList<string>>>(async (root, token) =>
        growthModel.Seen.Count > 2 && !(await File.ReadAllTextAsync(Path.Combine(root, "PROMPT.md"), token)).Contains("FEATURE-E", StringComparison.Ordinal) ? ["needs FEATURE-E"] : await agentCheck(root, token));
    var growth = new FeatureGrowth(smokePaths, new FakeLocalModelRuntime(new Uri("http://127.0.0.1:9/")), () => "qwen2.5-coder:7b-instruct-q3_K_S",
        agentModel: _ => growthModel, checkOverride: growthFeatureTwo);
    var grownFeatures = await growth.GrowAsync(created, StudioProjectKind.Agent, "big request", bigLater);
    var grownText = FeatureGrowth.Describe(grownFeatures);
    Check(bigCore.Features.Count == FeatureGrowth.CoreFeatures && bigLater.SequenceEqual(["d", "e"]) && FeatureGrowth.Split(bigCore).Later.Count == 0 &&
          grownFeatures.Count == 2 && grownFeatures[0].Added && !grownFeatures[1].Added &&
          File.ReadAllText(promptPath).Contains("AGENT-OK", StringComparison.Ordinal) &&
          File.ReadAllText(Path.Combine(created, ".localstudio", "baseline", "files", "PROMPT.md")).Contains("AGENT-OK", StringComparison.Ordinal) &&
          grownText.Contains("追加できた機能: d", StringComparison.Ordinal) && grownText.Contains("まだ入っていない機能", StringComparison.Ordinal) && grownText.Contains("e", StringComparison.Ordinal),
        "large-plans-grow-feature-by-feature-keeping-only-passing-features");
    await File.WriteAllTextAsync(promptPath, beforeExternalChange);

    var createRollbackSession = await workspaceEngine.StartAsync(created);
    var createdText = await createRollbackSession.CreateTextFileAsync("notes\\run-note.txt", "安全に作成\n");
    Check(createdText.Status == WorkspaceEditStatus.Changed && File.Exists(Path.Combine(created, "notes", "run-note.txt")), "safe-create-file");
    await CheckThrowsAsync(() => createRollbackSession.CreateTextFileAsync("notes\\run-note.txt", "重複"), "safe-create-existing-rejected");
    await CheckThrowsAsync(() => createRollbackSession.CreateTextFileAsync("..\\escaped.txt", "拒否"), "safe-create-traversal-rejected");
    await createRollbackSession.RollbackAsync();
    Check(!File.Exists(Path.Combine(created, "notes", "run-note.txt")), "safe-created-file-rollback");

    var createAcceptSession = await workspaceEngine.StartAsync(created);
    await createAcceptSession.CreateTextFileAsync("notes\\accepted.txt", "検証済み\n");
    await createAcceptSession.RecordVerificationAsync(true, "created file verified");
    await createAcceptSession.AcceptBaselineAsync();
    Check(File.Exists(Path.Combine(created, ".localstudio", "baseline", "files", "notes", "accepted.txt")), "safe-created-file-baseline");
}
finally
{
    if (Directory.Exists(smokeRoot)) Directory.Delete(smokeRoot, true);
}
Check(Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) == userPathBeforeSmoke, "user-path-is-not-modified");

if (failures.Count > 0)
{
    Console.Error.WriteLine("FAILED: " + string.Join(", ", failures));
    return 1;
}
Console.WriteLine("PASS: LocalStudio user-space guard, architecture boundaries, bounded blueprints, and no-change loop gate");
return 0;

sealed class ExplodingPlanner : IPlanningEngine
{
    public Task<StudioBlueprint> PlanAsync(string request, StudioProjectKind kind, string model, IProgress<PlanningProgress>? progress = null, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("fallback planner must not run for Windows games");
}

static class TestData
{
    public static string WindowsCandidateEnvelope(string programCs)
    {
        var candidate = JsonSerializer.Serialize(new { program_cs = programCs });
        return JsonSerializer.Serialize(new { message = new { content = candidate } });
    }

    public static string WindowsCandidateStream(string programCs)
    {
        var candidate = JsonSerializer.Serialize(new { program_cs = programCs });
        var middle = candidate.Length / 2;
        return JsonSerializer.Serialize(new { message = new { content = candidate[..middle] }, done = false }) + "\n" +
               JsonSerializer.Serialize(new { message = new { content = candidate[middle..] }, done = true });
    }

    public static string WindowsPatchStream(string oldText, string newText)
    {
        var patch = JsonSerializer.Serialize(new
        {
            replacements = new[] { new { old_text = oldText, new_text = newText } }
        });
        var middle = patch.Length / 2;
        return JsonSerializer.Serialize(new { message = new { content = patch[..middle] }, done = false }) + "\n" +
               JsonSerializer.Serialize(new { message = new { content = patch[middle..] }, done = true });
    }

    public static string CandidateEnvelope(string body, string javascript)
    {
        var candidate = JsonSerializer.Serialize(new { body, javascript });
        return JsonSerializer.Serialize(new { message = new { content = candidate } });
    }

    public static string ReviewEnvelope(string id, string uiEvidence, string behaviorEvidence)
    {
        var review = JsonSerializer.Serialize(new
        {
            requirements = new[] { new { id, passed = true, ui_evidence = uiEvidence, behavior_evidence = behaviorEvidence } }
        });
        return JsonSerializer.Serialize(new { message = new { content = review } });
    }
}

/// <summary>A registry stand-in: fixed bytes per URL, 404 for anything else (no network).</summary>
sealed class FakeNpmHandler(Dictionary<string, byte[]> responses) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(responses.TryGetValue(request.RequestUri!.ToString(), out var bytes)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
            : new HttpResponseMessage(HttpStatusCode.NotFound));
}

sealed class SequenceJsonHandler(params string[] responses) : HttpMessageHandler
{
    private readonly Queue<string> _responses = new(responses);
    public int RequestCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        if (_responses.Count == 0) throw new InvalidOperationException("No fake response remains.");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(_responses.Dequeue(), Encoding.UTF8, "application/json")
        });
    }
}

/// <summary>
/// Models a blocked WPF UI thread: work posted back to this context never runs. A task that needs the
/// context to finish can then only complete if it never resumes on it (ConfigureAwait(false)).
/// </summary>
sealed class ScriptedPressureProbe(params MemoryPressureSample[] samples) : IMemoryPressureProbe
{
    private int _next;
    public MemoryPressureSample Sample() => samples[Math.Min(_next++, samples.Length - 1)];
}

sealed class OrderRecordingVerifier(List<string> events) : IDevelopmentUnitVerifier
{
    public Task<DevelopmentUnitVerification> VerifyAsync(
        DevelopmentUnitPlan plan,
        DevelopmentUnit unit,
        string candidateRoot,
        CancellationToken cancellationToken = default)
    {
        events.Add("verify");
        return Task.FromResult(new DevelopmentUnitVerification(true, "verified"));
    }
}

static class BlockedUiContext
{
    public static bool Completes(Func<Task> start, TimeSpan timeout)
    {
        var completed = false;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new NeverRunsPostedWorkContext());
            try { completed = start().Wait(timeout); }
            catch (AggregateException) { completed = false; }
        });
        thread.IsBackground = true;
        thread.Start();
        return thread.Join(timeout + TimeSpan.FromSeconds(5)) && completed;
    }

    private sealed class NeverRunsPostedWorkContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) { }
        public override void Send(SendOrPostCallback d, object? state) =>
            throw new InvalidOperationException("The blocked UI thread cannot run synchronous work.");
    }
}

static class RuntimeTestArchive
{
    public static byte[] Create(string content, bool linkEntry = false)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("ollama.exe");
            if (linkEntry) entry.ExternalAttributes = unchecked((int)0xA0000000);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
        return output.ToArray();
    }
}

static class RuntimeTestArtifact
{
    public static RuntimeArtifact Create(string product, string version, Uri source, byte[] payload) => new(
        product,
        RuntimeArtifactKind.Ollama,
        version,
        "x64",
        source,
        Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
        "MIT",
        RuntimeArtifactFormat.Zip,
        "runtime.zip",
        "ollama.exe",
        payload.LongLength);
}

sealed class ScriptedRuntimeDownloader(byte[] payload, bool interruptFirst = false, Exception? failFirstAfterHalf = null) : IRuntimeArtifactDownloader
{
    private int _calls;
    public bool Resumed { get; private set; }

    public async Task<RuntimeDownloadResult> DownloadAsync(
        RuntimeArtifact artifact,
        string partialPath,
        IProgress<RuntimeDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        _calls++;
        Directory.CreateDirectory(Path.GetDirectoryName(partialPath)!);
        if (interruptFirst && _calls == 1)
        {
            await File.WriteAllBytesAsync(partialPath, payload[..(payload.Length / 2)], cancellationToken);
            throw new OperationCanceledException("simulated interruption", cancellationToken);
        }
        if (failFirstAfterHalf is not null && _calls == 1)
        {
            await File.WriteAllBytesAsync(partialPath, payload[..(payload.Length / 2)], cancellationToken);
            throw failFirstAfterHalf;
        }

        var existing = File.Exists(partialPath) ? checked((int)new FileInfo(partialPath).Length) : 0;
        Resumed = existing > 0;
        if (existing > payload.Length) { File.Delete(partialPath); existing = 0; Resumed = false; }
        await using var stream = new FileStream(partialPath, existing > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None);
        await stream.WriteAsync(payload.AsMemory(existing), cancellationToken);
        progress?.Report(new(payload.LongLength, payload.LongLength, null, TimeSpan.Zero, Resumed));
        return new(payload.LongLength, Resumed, artifact.OfficialSource);
    }
}

sealed class FixedRuntimeStorageProbe(long availableBytes) : IRuntimeStorageProbe
{
    public long GetAvailableBytes(string path) => availableBytes;
}

sealed class CountingWindowsCompiler : IWindowsExecutableCompiler
{
    public int Calls { get; private set; }
    public Task<string> CompileAsync(string projectRoot, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(Path.Combine(projectRoot, "LocalStudioApp.exe"));
    }
}

sealed class FakeRuntimeSmokeVerifier : IRuntimeSmokeVerifier
{
    public Task<RuntimeProcessTelemetry> VerifyAsync(
        RuntimeArtifact artifact,
        string candidateContentRoot,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new RuntimeProcessTelemetry(
            Path.Combine(candidateContentRoot, artifact.ExecutableRelativePath ?? artifact.FileName),
            DateTimeOffset.UtcNow,
            0,
            1,
            1,
            0,
            true,
            TimeSpan.Zero));
}

sealed class TransientRepairJsonHandler(string firstResponse, string recoveredResponse) : HttpMessageHandler
{
    public int RequestCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        if (RequestCount == 2) throw new HttpRequestException("simulated local model stream interruption");
        var response = RequestCount == 1 ? firstResponse : recoveredResponse;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(response, Encoding.UTF8, "application/json")
        });
    }
}

sealed class FakeLocalModelRuntime(Uri apiBaseUri) : ILocalModelRuntime
{
    public string StorageRoot => Path.GetTempPath();
    public Uri ApiBaseUri => apiBaseUri;
    public string? FindInstalledExecutable() => "fake-ollama.exe";
    public Task<IReadOnlyList<string>> GetInstalledModelsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([LocalStudioSettings.DefaultModel]);
    public Task EnsureServerAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PullModelAsync(string model, IProgress<SetupProgress>? progress = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

sealed class ScriptedOfficePlanner(OfficePlan plan) : IOfficePlanner
{
    public int Calls { get; private set; }

    public Task<OfficePlan> PlanAsync(string request, string facts, string kind, IReadOnlyList<string> problems, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(plan);
    }
}

sealed class ScriptedPdfPlanner(PdfPlan plan) : IPdfPlanner
{
    public int Calls { get; private set; }

    public Task<PdfPlan> PlanAsync(string request, string facts, int files, IReadOnlyList<string> problems, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(plan);
    }
}

sealed class ScriptedRenamePlanner(RenamePlan plan) : IRenamePlanner
{
    public Task<RenamePlan> PlanAsync(string request, IReadOnlyList<string> sampleNames, IReadOnlyList<string> previousProblems, CancellationToken cancellationToken = default) =>
        Task.FromResult(plan);
}

/// <summary>A runtime with the vision model installed that counts how often loaded models were released.</summary>
sealed class FakeVisionRuntime(Uri apiBaseUri) : ILocalModelRuntime
{
    public int Unloads { get; private set; }
    public string StorageRoot => Path.GetTempPath();
    public Uri ApiBaseUri => apiBaseUri;
    public string? FindInstalledExecutable() => "fake-ollama.exe";
    public Task<IReadOnlyList<string>> GetInstalledModelsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([LocalStudioSettings.DefaultModel, LocalStudioSettings.VisionModel]);
    public Task EnsureServerAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PullModelAsync(string model, IProgress<SetupProgress>? progress = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task UnloadModelsAsync(CancellationToken cancellationToken = default) { Unloads++; return Task.CompletedTask; }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

sealed class FakeSettingsStore(LocalStudioSettings settings) : ISettingsStore
{
    public LocalStudioSettings Load() => settings;
    public Task SaveAsync(LocalStudioSettings value, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

sealed class ExhaustedGenerator(GeneratedProjectSource last) : IGenerativeProjectGenerator
{
    public Task<GeneratedProjectSource> GenerateAsync(StudioBlueprint blueprint, StudioProjectKind kind, IProgress<ProjectCreationProgress>? progress = null, CancellationToken cancellationToken = default, string? sourceRequest = null, IGenerationSteering? steering = null) =>
        throw new InvalidDataException("attempts used up", new GenerationExhaustedException("attempts used up", last, ["broken script"]));
}

/// <summary>Replays fixed agent actions and records what the loop showed it.</summary>
sealed class ScriptedAgentModel(params string[] replies) : IAgentModel
{
    private int _next;
    public List<string> Seen { get; } = [];
    public List<string> FullSeen { get; } = [];
    public Task<string> NextAsync(string system, IReadOnlyList<(string Role, string Content)> turns, CancellationToken cancellationToken)
    {
        Seen.Add(turns[^1].Content);
        FullSeen.Add(string.Join("\n", turns.Select(turn => turn.Role + ": " + turn.Content)));
        return Task.FromResult(_next < replies.Length ? replies[_next++] : "{\"action\":\"finish\"}");
    }
}

sealed class FixedGenerativeProjectGenerator(GeneratedProjectSource source) : IGenerativeProjectGenerator
{
    public Task<GeneratedProjectSource> GenerateAsync(StudioBlueprint blueprint, StudioProjectKind kind, IProgress<ProjectCreationProgress>? progress = null, CancellationToken cancellationToken = default, string? sourceRequest = null, IGenerationSteering? steering = null) =>
        Task.FromResult(source);
}

sealed class SteeringAwareGenerator(GeneratedProjectSource source) : IGenerativeProjectGenerator
{
    public Task<GeneratedProjectSource> GenerateAsync(StudioBlueprint blueprint, StudioProjectKind kind, IProgress<ProjectCreationProgress>? progress = null, CancellationToken cancellationToken = default, string? sourceRequest = null, IGenerationSteering? steering = null)
    {
        steering?.TakePending(1);
        return Task.FromResult(source);
    }
}

sealed class RejectingWebVerifier : IStaticWebAppVerifier
{
    public Task<ProjectVerificationResult> VerifyAsync(string projectRoot, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ProjectVerificationResult(false, [], ["test.rejected"], DateTimeOffset.UtcNow));
}

sealed class ScriptedClassifier(OutputKindJudgement judgement) : IOutputKindClassifier
{
    public int Calls { get; private set; }

    public Task<OutputKindJudgement> ClassifyAsync(string request, string model, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(judgement);
    }
}

/// <summary>Holds a creation run open until released, then takes pending steering as the next attempt would.</summary>
sealed class ProbeScaffolder(string projectRoot) : IProjectScaffolder
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public IReadOnlyList<SteeringInstruction> Taken { get; private set; } = [];
    public string? LastRequest { get; private set; }
    public int Completed { get; private set; }

    public async Task<string> CreateAsync(StudioBlueprint blueprint, StudioProjectKind kind, CancellationToken cancellationToken = default, IProgress<ProjectCreationProgress>? progress = null, ProjectDerivation? derivation = null, string? sourceRequest = null, IGenerationSteering? steering = null)
    {
        LastRequest = sourceRequest;
        Started.TrySetResult();
        await Release.Task.WaitAsync(cancellationToken);
        Taken = steering?.TakePending(2) ?? [];
        var path = Path.Combine(projectRoot, "probe-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);
        Completed++;
        return path;
    }
}

sealed class CapturingJsonHandler(params string[] responses) : HttpMessageHandler
{
    private readonly Queue<string> _responses = new(responses);
    public List<string> Bodies { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
        if (_responses.Count == 0) throw new InvalidOperationException("No fake response remains.");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_responses.Dequeue(), Encoding.UTF8, "application/json") };
    }
}

sealed class UnavailableWindowsCompiler : IWindowsExecutableCompiler
{
    public Task<string> CompileAsync(string projectRoot, CancellationToken cancellationToken = default) =>
        throw new WindowsBuildEnvironmentException("compiler unavailable");
}

sealed class SmokeDevelopmentUnitGenerator : IDevelopmentUnitGenerator
{
    public Task<DevelopmentUnitArtifact> GenerateAsync(
        DevelopmentUnitPlan plan,
        DevelopmentRunState state,
        DevelopmentUnit unit,
        string sourceRequest,
        IReadOnlyList<DevelopmentUnitArtifact> dependencyArtifacts,
        string? previousFailure,
        IProgress<DevelopmentUnitExecutionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var layer = unit.Layer;
        var content = $"namespace Smoke.{layer}; internal static class {unit.Id.Replace("-", "", StringComparison.Ordinal)} {{ }}";
        return Task.FromResult(new DevelopmentUnitArtifact(
            1,
            unit.Id,
            [new($"src/Smoke.{layer}/{unit.Id}.cs", content)]));
    }
}

sealed class FailFirstDevelopmentUnitVerifier(string unitToFailOnce) : IDevelopmentUnitVerifier
{
    private readonly Dictionary<string, int> _calls = new(StringComparer.Ordinal);

    public Task<DevelopmentUnitVerification> VerifyAsync(
        DevelopmentUnitPlan plan,
        DevelopmentUnit unit,
        string candidateRoot,
        CancellationToken cancellationToken = default)
    {
        _calls[unit.Id] = _calls.GetValueOrDefault(unit.Id) + 1;
        var hasSource = Directory.EnumerateFiles(candidateRoot, "*.cs", SearchOption.AllDirectories).Any();
        var fail = unit.Id == unitToFailOnce && _calls[unit.Id] == 1;
        return Task.FromResult(new DevelopmentUnitVerification(
            hasSource && !fail,
            fail ? "synthetic-verification-failure" : hasSource ? "verified" : "missing-source"));
    }
}

sealed class ScriptedRequirementVerifier(params (string TestId, DeterministicRequirementVerification Outcome)[] script) : IRequirementVerifier
{
    private readonly Queue<(string TestId, DeterministicRequirementVerification Outcome)> _script = new(script);

    public Task<DeterministicRequirementVerification> VerifyAsync(
        string projectRoot,
        ProjectTestDefinition definition,
        CancellationToken cancellationToken = default)
    {
        if (_script.Count == 0) throw new InvalidOperationException("No scripted requirement verification remains.");
        var next = _script.Dequeue();
        if (!next.TestId.Equals(definition.Id, StringComparison.Ordinal))
            throw new InvalidOperationException($"Expected {next.TestId}, received {definition.Id}.");
        return Task.FromResult(next.Outcome);
    }
}

sealed class CountingDevelopmentPlanner(DevelopmentUnitPlan result) : IDevelopmentPlanningEngine
{
    public int CallCount { get; private set; }

    public Task<DevelopmentUnitPlan> PlanAsync(
        string productName,
        string sourceRequest,
        IProgress<PlanningProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        return Task.FromResult(result);
    }
}

sealed class RecordingPlanner(StudioBlueprint result) : IPlanningEngine
{
    public int CallCount { get; private set; }

    public Task<StudioBlueprint> PlanAsync(string request, StudioProjectKind kind, string model, IProgress<PlanningProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        CallCount++;
        return Task.FromResult(result);
    }
}

static class AutomatedProcessErrorMode
{
    private const uint SemFailCriticalErrors = 0x0001;
    private const uint SemNoGpFaultErrorBox = 0x0002;
    private const uint SemNoOpenFileErrorBox = 0x8000;

    public static bool SuppressWindowsErrorDialogs()
    {
        if (!OperatingSystem.IsWindows()) return true;
        _ = SetErrorMode(SemFailCriticalErrors | SemNoGpFaultErrorBox | SemNoOpenFileErrorBox);
        return (GetErrorMode() & SemNoGpFaultErrorBox) != 0;
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);

    [DllImport("kernel32.dll")]
    private static extern uint GetErrorMode();
}
