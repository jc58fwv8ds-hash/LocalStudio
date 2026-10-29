namespace SafeLocalHarness.Core;

public sealed record DevelopmentSessionRequest(
    string SessionRoot,
    string Destination,
    string ProjectId,
    string ProjectName,
    string AppPurpose,
    string SourceRequest,
    IReadOnlyDictionary<string, string> RequirementTexts,
    IReadOnlyList<string> TechStack,
    IReadOnlyList<string> BuildInstructions,
    IReadOnlyList<string> DesignDecisions,
    DevelopmentUnitPlan? Plan = null);

public enum DevelopmentSessionStage
{
    Prepared,
    Paused,
    Blocked,
    AssembledAwaitingRequirementEvidence,
    Complete
}

public sealed record DevelopmentSessionResult(
    DevelopmentSessionStage Stage,
    DevelopmentUnitPlan Plan,
    DevelopmentRunState State,
    string PlanPath,
    string StatePath,
    string? ProjectRoot,
    bool CompletionAllowed,
    IReadOnlyList<string> BlockedUnitIds);

/// <summary>
/// Owns the durable boundary around planning, DAG execution, assembly and resume.
/// It deliberately does not promote Stable or turn a passing DAG into requirement evidence.
/// </summary>
public sealed class DevelopmentSessionService
{
    private const string PlanFileName = "effective-plan.json";
    private const string StateFileName = "development-run.json";
    private readonly IDevelopmentPlanningEngine? _planner;
    private readonly DevelopmentUnitExecutionEngine? _executor;
    private readonly DevelopmentProjectAssembler? _assembler;

    public DevelopmentSessionService(IDevelopmentPlanningEngine? planner) => _planner = planner;

    public DevelopmentSessionService(
        IDevelopmentPlanningEngine? planner,
        DevelopmentUnitExecutionEngine executor,
        DevelopmentUnitCandidateStore store)
    {
        _planner = planner;
        _executor = executor;
        _assembler = new DevelopmentProjectAssembler(store);
    }

    public async Task<DevelopmentSessionResult> PrepareAsync(
        DevelopmentSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        var prepared = await PrepareCoreAsync(request, cancellationToken);
        return new(
            DevelopmentSessionStage.Prepared,
            prepared.Plan,
            prepared.State,
            prepared.PlanPath,
            prepared.StatePath,
            Directory.Exists(prepared.Destination) ? prepared.Destination : null,
            false,
            []);
    }

    public async Task<DevelopmentSessionResult> RunAsync(
        DevelopmentSessionRequest request,
        bool prepareOnly = false,
        IProgress<DevelopmentUnitExecutionProgress>? progress = null,
        int maximumUnitAttempts = int.MaxValue,
        CancellationToken cancellationToken = default)
    {
        if (maximumUnitAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maximumUnitAttempts));
        var prepared = await PrepareCoreAsync(request, cancellationToken);
        var plan = prepared.Plan;
        var state = prepared.State;
        var planPath = prepared.PlanPath;
        var statePath = prepared.StatePath;
        var destination = prepared.Destination;

        if (prepareOnly)
            return new(
                DevelopmentSessionStage.Prepared,
                plan,
                state,
                planPath,
                statePath,
                Directory.Exists(destination) ? destination : null,
                false,
                []);

        if (_executor is null || _assembler is null)
            throw new InvalidOperationException("このDevelopmentSessionServiceは準備専用です。");
        var execution = await _executor.RunAsync(
            plan,
            state,
            statePath,
            request.SourceRequest,
            progress,
            maximumUnitAttempts,
            cancellationToken);

        if (!execution.Completed)
        {
            var stage = execution.BlockedUnitIds.Count > 0
                ? DevelopmentSessionStage.Blocked
                : DevelopmentSessionStage.Paused;
            return new(stage, plan, execution.State, planPath, statePath, null, false, execution.BlockedUnitIds);
        }

        if (Directory.Exists(destination))
            return await InspectExistingProjectAsync(request, plan, execution.State, planPath, statePath, destination, cancellationToken);

        var assembly = await _assembler.AssembleAsync(
            plan,
            execution.State,
            new DevelopmentProjectAssemblyRequest(
                destination,
                request.ProjectId,
                request.ProjectName,
                request.AppPurpose,
                request.SourceRequest,
                request.RequirementTexts,
                request.TechStack,
                request.BuildInstructions,
                request.DesignDecisions),
            cancellationToken);
        return new(
            assembly.CompletionAllowed ? DevelopmentSessionStage.Complete : DevelopmentSessionStage.AssembledAwaitingRequirementEvidence,
            plan,
            execution.State,
            planPath,
            statePath,
            assembly.ProjectRoot,
            assembly.CompletionAllowed,
            []);
    }

    private async Task<(DevelopmentUnitPlan Plan, DevelopmentRunState State, string PlanPath, string StatePath, string Destination)> PrepareCoreAsync(
        DevelopmentSessionRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        var sessionRoot = Path.GetFullPath(request.SessionRoot);
        var destination = Path.GetFullPath(request.Destination);
        UserSpaceGuard.EnsureAllowed(sessionRoot);
        UserSpaceGuard.EnsureAllowed(destination);
        Directory.CreateDirectory(sessionRoot);
        var planPath = SafeChild(sessionRoot, PlanFileName);
        var statePath = SafeChild(sessionRoot, StateFileName);
        var plan = await ResolvePlanAsync(request, planPath, cancellationToken);
        ValidatePlanRequirements(plan, request.RequirementTexts);
        var state = File.Exists(statePath)
            ? await DevelopmentRunCoordinator.LoadAsync(statePath, cancellationToken)
            : DevelopmentRunCoordinator.Create(plan);
        state = DevelopmentRunCoordinator.Recover(plan, state);
        await DevelopmentRunCoordinator.SaveAtomicAsync(statePath, state, cancellationToken);
        return (plan, state, planPath, statePath, destination);
    }

    private async Task<DevelopmentUnitPlan> ResolvePlanAsync(
        DevelopmentSessionRequest request,
        string planPath,
        CancellationToken cancellationToken)
    {
        if (File.Exists(planPath))
        {
            var saved = await PortableProjectJson.LoadAsync<DevelopmentUnitPlan>(planPath, cancellationToken);
            EnsureValidPlan(saved);
            if (request.Plan is not null &&
                !DevelopmentRunCoordinator.Fingerprint(saved).Equals(
                    DevelopmentRunCoordinator.Fingerprint(request.Plan),
                    StringComparison.Ordinal))
                throw new InvalidDataException("保存済みTask DAGと指定されたTask DAGが一致しません。");
            return saved;
        }

        if (request.Plan is null && _planner is null)
            throw new InvalidOperationException("保存済みTask DAGがなく、現在のリソース状態では新しい計画を作成できません。");
        var plan = request.Plan ?? await _planner!.PlanAsync(
            request.ProjectName,
            BuildPlanningRequest(request),
            cancellationToken: cancellationToken);
        EnsureValidPlan(plan);
        await PortableProjectJson.SaveAtomicAsync(planPath, plan, cancellationToken);
        return plan;
    }

    private static async Task<DevelopmentSessionResult> InspectExistingProjectAsync(
        DevelopmentSessionRequest request,
        DevelopmentUnitPlan plan,
        DevelopmentRunState state,
        string planPath,
        string statePath,
        string destination,
        CancellationToken cancellationToken)
    {
        var inspection = await ProjectCartridgeStore.InspectAsync(destination, cancellationToken);
        if (!inspection.CanResume)
            throw new InvalidDataException("既存の出力プロジェクトを安全に再開できません: " + string.Join(", ", inspection.Issues));
        if (!inspection.Cartridge.ProjectId.Equals(request.ProjectId, StringComparison.Ordinal) ||
            !DevelopmentRunCoordinator.Fingerprint(inspection.Cartridge.TaskDag).Equals(
                DevelopmentRunCoordinator.Fingerprint(plan),
                StringComparison.Ordinal))
            throw new InvalidDataException("既存の出力プロジェクトはこの開発セッションに属していません。");

        var completionAllowed = RequirementLedgerPolicy.Validate(inspection.Cartridge.RequirementLedger).CompletionAllowed;
        return new(
            completionAllowed ? DevelopmentSessionStage.Complete : DevelopmentSessionStage.AssembledAwaitingRequirementEvidence,
            plan,
            state,
            planPath,
            statePath,
            destination,
            completionAllowed,
            []);
    }

    private static void ValidateRequest(DevelopmentSessionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SessionRoot)) throw new ArgumentException("開発セッション保存先が必要です。", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Destination)) throw new ArgumentException("プロジェクト出力先が必要です。", nameof(request));
        if (!PortableProjectPath.IsSafeIdentifier(request.ProjectId)) throw new InvalidDataException("Project IDが不正です。");
        if (string.IsNullOrWhiteSpace(request.ProjectName) || request.ProjectName.Length > 120)
            throw new InvalidDataException("Project名が不正です。");
        if (string.IsNullOrWhiteSpace(request.AppPurpose) || string.IsNullOrWhiteSpace(request.SourceRequest))
            throw new InvalidDataException("目的と元の依頼が必要です。");
        if (request.RequirementTexts.Count == 0 || request.RequirementTexts.Any(pair =>
                !PortableProjectPath.IsSafeIdentifier(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)))
            throw new InvalidDataException("Requirement Ledgerの入力が不正です。");
    }

    private static void ValidatePlanRequirements(
        DevelopmentUnitPlan plan,
        IReadOnlyDictionary<string, string> requirementTexts)
    {
        var referenced = plan.Units.SelectMany(unit => unit.RequirementIds).ToHashSet(StringComparer.Ordinal);
        if (referenced.Count != requirementTexts.Count ||
            referenced.Any(id => !requirementTexts.ContainsKey(id)) ||
            requirementTexts.Keys.Any(id => !referenced.Contains(id)))
            throw new InvalidDataException("Requirement LedgerとTask DAGの要件IDが一致しません。");
    }

    private static void EnsureValidPlan(DevelopmentUnitPlan plan)
    {
        var validation = DevelopmentUnitGraph.Validate(plan);
        if (!validation.Passed)
            throw new InvalidDataException("Task DAGが不正です: " + string.Join(", ", validation.Errors));
    }

    private static string BuildPlanningRequest(DevelopmentSessionRequest request)
    {
        var requirements = string.Join(
            Environment.NewLine,
            request.RequirementTexts.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}: {pair.Value}"));
        return $"PURPOSE:\n{request.AppPurpose.Trim()}\n\nREQUIREMENTS:\n{requirements}\n\nUSER REQUEST:\n{request.SourceRequest.Trim()}";
    }

    private static string SafeChild(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("開発セッションの保存先が許可範囲を外れています。");
        UserSpaceGuard.EnsureAllowed(fullPath);
        return fullPath;
    }
}
