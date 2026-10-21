using System.Text;
using System.Text.Json;

namespace SafeLocalHarness.Core;

public enum BootstrapStage
{
    AppStarted,
    EnvironmentProbed,
    CoreRuntimeVerified,
    InferenceRuntimeResolved,
    ModelProfileSelected,
    RequiredArtifactsDownloaded,
    ArtifactsVerified,
    SmokePassed,
    Ready,
    Degraded,
    Failed
}

public sealed record BootstrapFailure(
    RuntimeFailureKind Kind,
    string Message,
    string Step,
    DateTimeOffset At,
    bool Retryable);

public sealed record BootstrapState(
    int SchemaVersion,
    string BootstrapId,
    BootstrapStage CurrentStage,
    IReadOnlyList<BootstrapStage> CompletedStages,
    bool DeterministicToolsAvailable,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    int RetryCount,
    BootstrapFailure? LastFailure,
    string? ModelProfileId,
    IReadOnlyList<string> ReadyArtifacts)
{
    public static BootstrapState New() => new(
        1,
        Guid.NewGuid().ToString("N"),
        BootstrapStage.AppStarted,
        [BootstrapStage.AppStarted],
        true,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        0,
        null,
        null,
        []);
}

public sealed class BootstrapStateStore(AppPaths paths)
{
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };
    public string StatePath => Path.Combine(paths.StateRoot, "bootstrap-state.json");

    public async Task<BootstrapState> LoadAsync(CancellationToken cancellationToken = default)
    {
        paths.EnsureCreated();
        if (!File.Exists(StatePath)) return BootstrapState.New();
        await using var stream = File.OpenRead(StatePath);
        return await JsonSerializer.DeserializeAsync<BootstrapState>(stream, _json, cancellationToken)
            ?? BootstrapState.New();
    }

    public async Task SaveAsync(BootstrapState state, CancellationToken cancellationToken = default)
    {
        paths.EnsureCreated();
        var temporary = StatePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(state, _json), new UTF8Encoding(false), cancellationToken);
            File.Move(temporary, StatePath, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

public sealed record BootstrapStepResult(
    bool Passed,
    string Summary,
    RuntimeFailureKind FailureKind = RuntimeFailureKind.None,
    bool Retryable = false,
    string? ReadyArtifact = null,
    string? ModelProfileId = null);

public interface IBootstrapStep
{
    BootstrapStage Stage { get; }
    Task<BootstrapStepResult> ExecuteAsync(BootstrapContext context, CancellationToken cancellationToken = default);
}

public sealed record BootstrapContext(
    AppPaths Paths,
    HardwareSnapshot Hardware,
    RuntimeResourcePlan ResourcePlan);

public sealed class BootstrapCoordinator
{
    private static readonly SemaphoreSlim RunGate = new(1, 1);
    private readonly BootstrapStateStore _store;
    private readonly IReadOnlyList<IBootstrapStep> _steps;
    private readonly bool _resumeCompletedStages;

    public BootstrapCoordinator(BootstrapStateStore store, IEnumerable<IBootstrapStep> steps, bool resumeCompletedStages = true)
    {
        _store = store;
        _resumeCompletedStages = resumeCompletedStages;
        _steps = steps.ToArray();
        var stages = _steps.Select(step => step.Stage).ToArray();
        if (stages.Distinct().Count() != stages.Length)
            throw new ArgumentException("Bootstrap stages must be unique.", nameof(steps));
        if (stages.Any(stage => stage is BootstrapStage.AppStarted or BootstrapStage.Ready or BootstrapStage.Degraded or BootstrapStage.Failed))
            throw new ArgumentException("Terminal and initial stages are controlled by the coordinator.", nameof(steps));
    }

    public async Task<BootstrapState> RunAsync(BootstrapContext context, CancellationToken cancellationToken = default)
    {
        await RunGate.WaitAsync(cancellationToken);
        try { return await RunCoreAsync(context, cancellationToken); }
        finally { RunGate.Release(); }
    }

    private async Task<BootstrapState> RunCoreAsync(BootstrapContext context, CancellationToken cancellationToken)
    {
        if (UserSpaceGuard.IsElevated())
            throw new RuntimeProvisioningException(RuntimeFailureKind.EnvironmentRestriction, "Bootstrap never runs elevated.");
        var state = await _store.LoadAsync(cancellationToken);
        if (state.CurrentStage == BootstrapStage.Ready || !_resumeCompletedStages)
        {
            // A persisted Ready marker is not proof that runtime files still exist or are unchanged.
            // Re-run all deterministic verification stages on an explicit setup request.
            // RetryCount is kept so repeated failures remain visible across runs.
            state = state with
            {
                CurrentStage = BootstrapStage.AppStarted,
                CompletedStages = [BootstrapStage.AppStarted],
                ReadyArtifacts = [],
                ModelProfileId = null,
                LastFailure = null,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await _store.SaveAsync(state, cancellationToken);
        }
        var completed = state.CompletedStages.ToHashSet();
        foreach (var step in _steps)
        {
            if (completed.Contains(step.Stage)) continue;
            BootstrapStepResult result;
            try
            {
                result = await step.ExecuteAsync(context, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                result = new(false, "Bootstrap was interrupted and can resume on next start.", RuntimeFailureKind.Interrupted, true);
            }
            catch (Exception exception)
            {
                result = new(false, exception.Message, RuntimeFailureClassifier.Classify(exception), true);
            }

            if (!result.Passed)
            {
                var degraded = IsDegradable(result.FailureKind);
                state = state with
                {
                    CurrentStage = degraded ? BootstrapStage.Degraded : BootstrapStage.Failed,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    RetryCount = state.RetryCount + 1,
                    LastFailure = new(result.FailureKind, result.Summary, step.Stage.ToString(), DateTimeOffset.UtcNow, result.Retryable),
                    DeterministicToolsAvailable = true
                };
                await _store.SaveAsync(state, CancellationToken.None);
                return state;
            }

            completed.Add(step.Stage);
            var artifacts = state.ReadyArtifacts.ToList();
            if (!string.IsNullOrWhiteSpace(result.ReadyArtifact) && !artifacts.Contains(result.ReadyArtifact, StringComparer.OrdinalIgnoreCase))
                artifacts.Add(result.ReadyArtifact);
            state = state with
            {
                CurrentStage = step.Stage,
                CompletedStages = completed.OrderBy(stage => stage).ToArray(),
                UpdatedAt = DateTimeOffset.UtcNow,
                LastFailure = null,
                ReadyArtifacts = artifacts,
                ModelProfileId = result.ModelProfileId ?? state.ModelProfileId,
                DeterministicToolsAvailable = true
            };
            await _store.SaveAsync(state, cancellationToken);
        }

        completed.Add(BootstrapStage.Ready);
        state = state with
        {
            CurrentStage = BootstrapStage.Ready,
            CompletedStages = completed.OrderBy(stage => stage).ToArray(),
            UpdatedAt = DateTimeOffset.UtcNow,
            LastFailure = null,
            DeterministicToolsAvailable = true
        };
        await _store.SaveAsync(state, cancellationToken);
        return state;
    }

    private static bool IsDegradable(RuntimeFailureKind failure) => failure is
        RuntimeFailureKind.Dns or
        RuntimeFailureKind.Proxy or
        RuntimeFailureKind.Tls or
        RuntimeFailureKind.Http or
        RuntimeFailureKind.InsufficientDisk or
        RuntimeFailureKind.AccessDenied or
        RuntimeFailureKind.Interrupted or
        RuntimeFailureKind.EnvironmentRestriction;
}

public sealed class DelegateBootstrapStep(
    BootstrapStage stage,
    Func<BootstrapContext, CancellationToken, Task<BootstrapStepResult>> execute) : IBootstrapStep
{
    public BootstrapStage Stage { get; } = stage;
    public Task<BootstrapStepResult> ExecuteAsync(BootstrapContext context, CancellationToken cancellationToken = default) =>
        execute(context, cancellationToken);
}
