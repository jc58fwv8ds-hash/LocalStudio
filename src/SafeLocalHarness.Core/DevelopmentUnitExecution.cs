using System.Text.Json;

namespace SafeLocalHarness.Core;

public sealed record DevelopmentUnitExecutionProgress(
    string UnitId,
    string Stage,
    string Message,
    int Attempt);

public sealed record DevelopmentUnitVerification(
    bool Passed,
    string Summary,
    DevelopmentFailureKind FailureKind = DevelopmentFailureKind.None,
    DevelopmentPipelineStage Stage = DevelopmentPipelineStage.FinalVerification);

public sealed record DevelopmentExecutionResult(
    DevelopmentRunState State,
    bool Completed,
    IReadOnlyList<string> BlockedUnitIds);

public sealed class DevelopmentVerificationWorkspace : IDisposable
{
    public DevelopmentVerificationWorkspace(string root) => Root = root;
    public string Root { get; }
    public void Dispose()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, true);
    }
}

public interface IDevelopmentUnitGenerator
{
    Task<DevelopmentUnitArtifact> GenerateAsync(
        DevelopmentUnitPlan plan,
        DevelopmentRunState state,
        DevelopmentUnit unit,
        string sourceRequest,
        IReadOnlyList<DevelopmentUnitArtifact> dependencyArtifacts,
        string? previousFailure,
        IProgress<DevelopmentUnitExecutionProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IDevelopmentUnitVerifier
{
    Task<DevelopmentUnitVerification> VerifyAsync(
        DevelopmentUnitPlan plan,
        DevelopmentUnit unit,
        string candidateRoot,
        CancellationToken cancellationToken = default);
}

public sealed class DevelopmentUnitCandidateStore
{
    private readonly string _root;

    public DevelopmentUnitCandidateStore(string root)
    {
        _root = Path.GetFullPath(root);
        UserSpaceGuard.EnsureAllowed(_root);
    }

    public async Task<(string CandidateRoot, string Fingerprint)> SaveAsync(
        DevelopmentUnit unit,
        DevelopmentUnitArtifact artifact,
        CancellationToken cancellationToken = default)
    {
        var validation = DevelopmentUnitArtifactPolicy.Validate(unit, artifact);
        if (!validation.Passed)
            throw new InvalidDataException("開発単位の成果物が安全境界を満たしません: " + string.Join(", ", validation.Errors));

        var fingerprint = DevelopmentUnitArtifactPolicy.Fingerprint(artifact);
        var unitRoot = SafeChild(Path.Combine(_root, "candidates"), unit.Id);
        var destination = SafeChild(unitRoot, fingerprint);
        if (Directory.Exists(destination))
        {
            await VerifyStoredArtifactAsync(destination, artifact, fingerprint, cancellationToken);
            return (destination, fingerprint);
        }

        Directory.CreateDirectory(unitRoot);
        var staging = SafeChild(unitRoot, ".staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var file in artifact.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = DevelopmentUnitArtifactPolicy.NormalizeRelativePath(file.Path)
                    ?? throw new InvalidDataException("安全でない成果物パスです: " + file.Path);
                var target = SafeChild(staging, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllTextAsync(target, file.Content, cancellationToken);
            }

            await File.WriteAllTextAsync(
                SafeChild(staging, "artifact.json"),
                JsonSerializer.Serialize(artifact, new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken);
            Directory.Move(staging, destination);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }

        return (destination, fingerprint);
    }

    public async Task<DevelopmentUnitArtifact> LoadAsync(
        string unitId,
        string fingerprint,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(unitId) || string.IsNullOrWhiteSpace(fingerprint))
            throw new ArgumentException("開発単位と成果物指紋が必要です。");
        var candidateRoot = SafeChild(SafeChild(Path.Combine(_root, "candidates"), unitId), fingerprint);
        var metadataPath = SafeChild(candidateRoot, "artifact.json");
        if (!File.Exists(metadataPath)) throw new FileNotFoundException("受理済み成果物が見つかりません。", metadataPath);
        return JsonSerializer.Deserialize<DevelopmentUnitArtifact>(
            await File.ReadAllTextAsync(metadataPath, cancellationToken),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("受理済み成果物を読み取れません。");
    }

    public async Task<DevelopmentVerificationWorkspace> ComposeVerificationWorkspaceAsync(
        DevelopmentUnitArtifact current,
        IReadOnlyList<DevelopmentUnitArtifact> dependencies,
        CancellationToken cancellationToken = default)
    {
        var verificationRoot = SafeChild(
            Path.Combine(_root, "verification"),
            current.UnitId + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(verificationRoot);
        try
        {
            var written = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var artifact in dependencies.Append(current))
            {
                foreach (var file in artifact.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relative = DevelopmentUnitArtifactPolicy.NormalizeRelativePath(file.Path)
                        ?? throw new InvalidDataException("安全でない成果物パスです: " + file.Path);
                    if (written.TryGetValue(relative, out var prior))
                    {
                        if (!prior.Equals(file.Content, StringComparison.Ordinal))
                            throw new InvalidDataException("開発単位間で同じファイルが競合しています: " + relative);
                        continue;
                    }
                    var target = SafeChild(verificationRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await File.WriteAllTextAsync(target, file.Content, cancellationToken);
                    written[relative] = file.Content;
                }
            }
            await File.WriteAllTextAsync(
                SafeChild(verificationRoot, "artifact.json"),
                JsonSerializer.Serialize(current, new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken);
            return new DevelopmentVerificationWorkspace(verificationRoot);
        }
        catch
        {
            if (Directory.Exists(verificationRoot)) Directory.Delete(verificationRoot, true);
            throw;
        }
    }

    private static async Task VerifyStoredArtifactAsync(
        string candidateRoot,
        DevelopmentUnitArtifact expected,
        string expectedFingerprint,
        CancellationToken cancellationToken)
    {
        var metadataPath = SafeChild(candidateRoot, "artifact.json");
        if (!File.Exists(metadataPath))
            throw new InvalidDataException("保存済み成果物の検証情報がありません。");
        var stored = JsonSerializer.Deserialize<DevelopmentUnitArtifact>(
            await File.ReadAllTextAsync(metadataPath, cancellationToken),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("保存済み成果物を読み取れません。");
        if (!DevelopmentUnitArtifactPolicy.Fingerprint(stored).Equals(expectedFingerprint, StringComparison.Ordinal) ||
            !DevelopmentUnitArtifactPolicy.Fingerprint(expected).Equals(expectedFingerprint, StringComparison.Ordinal))
            throw new InvalidDataException("保存済み成果物の指紋が一致しません。");
    }

    private static string SafeChild(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("成果物の保存先が許可範囲を外れています。");
        UserSpaceGuard.EnsureAllowed(fullPath);
        return fullPath;
    }
}

public sealed class DevelopmentUnitExecutionEngine
{
    private readonly IDevelopmentUnitGenerator _generator;
    private readonly IDevelopmentUnitVerifier _verifier;
    private readonly DevelopmentUnitCandidateStore _store;
    private readonly Func<CancellationToken, Task>? _releaseInferenceMemory;

    /// <param name="releaseInferenceMemory">
    /// Called after generation and before build/test verification so the model and the build never hold memory at once.
    /// </param>
    public DevelopmentUnitExecutionEngine(
        IDevelopmentUnitGenerator generator,
        IDevelopmentUnitVerifier verifier,
        DevelopmentUnitCandidateStore store,
        Func<CancellationToken, Task>? releaseInferenceMemory = null)
    {
        _generator = generator;
        _verifier = verifier;
        _store = store;
        _releaseInferenceMemory = releaseInferenceMemory;
    }

    public async Task<DevelopmentExecutionResult> RunAsync(
        DevelopmentUnitPlan plan,
        DevelopmentRunState initialState,
        string statePath,
        string sourceRequest,
        IProgress<DevelopmentUnitExecutionProgress>? progress = null,
        int maximumUnitAttempts = int.MaxValue,
        CancellationToken cancellationToken = default)
    {
        if (maximumUnitAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maximumUnitAttempts));
        var state = DevelopmentRunCoordinator.Recover(plan, initialState);
        await DevelopmentRunCoordinator.SaveAtomicAsync(statePath, state, cancellationToken);
        var executedAttempts = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var blocked = state.Units
                .Where(item => item.Status == DevelopmentUnitStatus.Blocked)
                .Select(item => item.UnitId)
                .ToArray();
            if (blocked.Length > 0) return new(state, false, blocked);

            var ready = DevelopmentRunCoordinator.ReadyUnits(plan, state);
            if (ready.Count == 0)
            {
                var completed = state.Units.All(item => item.Status == DevelopmentUnitStatus.Accepted);
                return new(state, completed, []);
            }

            var unitId = ready[0];
            var unit = plan.Units.Single(item => item.Id == unitId);
            var prior = state.Units.Single(item => item.UnitId == unitId);
            state = DevelopmentRunCoordinator.StartUnit(plan, state, unitId);
            await DevelopmentRunCoordinator.SaveAtomicAsync(statePath, state, cancellationToken);
            var attempt = state.Units.Single(item => item.UnitId == unitId).Attempt;
            progress?.Report(new(unitId, "generation", "開発単位をローカルAIで実装しています", attempt));

            try
            {
                var dependencyArtifacts = new List<DevelopmentUnitArtifact>();
                foreach (var dependencyId in DependencyClosure(plan, unit))
                {
                    var dependency = state.Units.Single(item => item.UnitId == dependencyId);
                    if (dependency.Status != DevelopmentUnitStatus.Accepted || string.IsNullOrWhiteSpace(dependency.ArtifactFingerprint))
                        throw new InvalidOperationException("依存する開発単位が検証済みではありません: " + dependencyId);
                    dependencyArtifacts.Add(await _store.LoadAsync(dependencyId, dependency.ArtifactFingerprint, cancellationToken));
                }
                var artifact = await _generator.GenerateAsync(
                    plan,
                    state,
                    unit,
                    sourceRequest,
                    dependencyArtifacts,
                    prior.LastError,
                    progress,
                    cancellationToken);
                var saved = await _store.SaveAsync(unit, artifact, cancellationToken);
                progress?.Report(new(unitId, "verification", "隔離した成果物を検証しています", attempt));
                using var verificationWorkspace = await _store.ComposeVerificationWorkspaceAsync(
                    artifact,
                    dependencyArtifacts,
                    cancellationToken);
                if (_releaseInferenceMemory is not null) await _releaseInferenceMemory(cancellationToken);
                var verification = await _verifier.VerifyAsync(plan, unit, verificationWorkspace.Root, cancellationToken);
                state = DevelopmentRunCoordinator.RecordVerification(
                    state,
                    unitId,
                    verification.Passed,
                    verification.Passed ? saved.Fingerprint : null,
                    verification.Summary,
                    failureKind: verification.FailureKind == DevelopmentFailureKind.None
                        ? null
                        : verification.FailureKind.ToString(),
                    failureStage: verification.Stage.ToString());
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                state = DevelopmentRunCoordinator.RecordVerification(
                    state,
                    unitId,
                    false,
                    null,
                    ex.Message,
                    failureKind: DevelopmentFailureKind.EnvironmentRestriction.ToString(),
                    failureStage: DevelopmentPipelineStage.FinalVerification.ToString());
            }

            await DevelopmentRunCoordinator.SaveAtomicAsync(statePath, state, cancellationToken);
            var current = state.Units.Single(item => item.UnitId == unitId);
            progress?.Report(new(
                unitId,
                current.Status == DevelopmentUnitStatus.Accepted ? "accepted" : "repair",
                current.Status == DevelopmentUnitStatus.Accepted
                    ? "検証済みの開発単位として受理しました"
                    : "検証結果を次の有限修復へ渡します",
                current.Attempt));
            executedAttempts++;
            if (executedAttempts >= maximumUnitAttempts)
                return new(state, state.Units.All(item => item.Status == DevelopmentUnitStatus.Accepted), []);
        }
    }

    private static IReadOnlyList<string> DependencyClosure(DevelopmentUnitPlan plan, DevelopmentUnit unit)
    {
        var byId = plan.Units.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dependency in unit.Dependencies) Visit(dependency);
        return ordered;

        void Visit(string id)
        {
            if (!seen.Add(id)) return;
            var item = byId[id];
            foreach (var dependency in item.Dependencies) Visit(dependency);
            ordered.Add(id);
        }
    }
}
