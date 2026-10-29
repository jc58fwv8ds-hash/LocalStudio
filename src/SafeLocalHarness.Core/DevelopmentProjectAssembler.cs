using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SafeLocalHarness.Core;

public sealed record DevelopmentProjectAssemblyRequest(
    string Destination,
    string ProjectId,
    string ProjectName,
    string AppPurpose,
    string OriginalRequirements,
    IReadOnlyDictionary<string, string> RequirementTexts,
    IReadOnlyList<string> TechStack,
    IReadOnlyList<string> BuildInstructions,
    IReadOnlyList<string> DesignDecisions);

public sealed record DevelopmentProjectAssemblyResult(
    string ProjectRoot,
    int SourceFileCount,
    string SourceFingerprint,
    bool CompletionAllowed);

public sealed class DevelopmentProjectAssembler
{
    private readonly DevelopmentUnitCandidateStore _store;

    public DevelopmentProjectAssembler(DevelopmentUnitCandidateStore store) => _store = store;

    public async Task<DevelopmentProjectAssemblyResult> AssembleAsync(
        DevelopmentUnitPlan plan,
        DevelopmentRunState state,
        DevelopmentProjectAssemblyRequest request,
        CancellationToken cancellationToken = default)
    {
        var planValidation = DevelopmentUnitGraph.Validate(plan);
        if (!planValidation.Passed) throw new InvalidDataException("開発単位グラフが不正です: " + string.Join(", ", planValidation.Errors));
        EnsureCompleteState(plan, state);
        EnsureReleaseGateCoversPlan(plan);
        ValidateRequest(plan, request);

        var destination = Path.GetFullPath(request.Destination);
        UserSpaceGuard.EnsureAllowed(destination);
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException("既存のプロジェクトは上書きしません: " + destination);
        var parent = Path.GetDirectoryName(destination) ?? throw new InvalidOperationException("プロジェクト保存先を確認できません。");
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, ".staging-assembly-" + Guid.NewGuid().ToString("N"));
        UserSpaceGuard.EnsureAllowed(staging);
        Directory.CreateDirectory(staging);

        try
        {
            var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var unit in plan.Units)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var unitState = state.Units.Single(item => item.UnitId == unit.Id);
                var artifact = await _store.LoadAsync(unit.Id, unitState.ArtifactFingerprint!, cancellationToken);
                var artifactValidation = DevelopmentUnitArtifactPolicy.Validate(unit, artifact);
                if (!artifactValidation.Passed)
                    throw new InvalidDataException("受理済み成果物の安全境界が崩れています: " + string.Join(", ", artifactValidation.Errors));
                foreach (var file in artifact.Files)
                {
                    var relative = DevelopmentUnitArtifactPolicy.NormalizeRelativePath(file.Path)
                        ?? throw new InvalidDataException("安全でない成果物パスです: " + file.Path);
                    if (merged.TryGetValue(relative, out var prior) && !prior.Equals(file.Content, StringComparison.Ordinal))
                        throw new InvalidDataException("開発単位間で同じファイルが競合しています: " + relative);
                    merged[relative] = file.Content;
                }
            }

            foreach (var file in merged.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
            {
                var target = SafeChild(staging, PortableProjectPath.ToPlatformPath(file.Key));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllTextAsync(target, file.Value, new UTF8Encoding(false, true), cancellationToken);
            }

            var evidenceRoot = SafeChild(staging, Path.Combine(".localstudio", "verification"));
            Directory.CreateDirectory(evidenceRoot);
            var evidencePath = SafeChild(evidenceRoot, "development-units.json");
            var evidenceDocument = new
            {
                schemaVersion = 1,
                verifier = "LocalStudio.DevelopmentUnitExecutionEngine",
                // The DAG passing says every unit built and was checked, not that each requested feature works.
                result = "DAG_PASS",
                planFingerprint = state.PlanFingerprint,
                verifiedAt = state.UpdatedAt,
                releaseGate = FindReleaseGate(plan).Id,
                units = state.Units.Select(item => new
                {
                    id = item.UnitId,
                    status = item.Status.ToString(),
                    attempts = item.Attempt,
                    artifactFingerprint = item.ArtifactFingerprint
                }).ToArray()
            };
            await File.WriteAllTextAsync(evidencePath, JsonSerializer.Serialize(evidenceDocument, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false), cancellationToken);
            string evidenceHash;
            await using (var evidenceStream = File.OpenRead(evidencePath))
                evidenceHash = Convert.ToHexString(await SHA256.HashDataAsync(evidenceStream, cancellationToken)).ToLowerInvariant();
            var evidence = new RequirementEvidence(
                "development-release-gate",
                "dag-unit-verification",
                ".localstudio/verification/development-units.json",
                evidenceHash,
                state.UpdatedAt,
                "依存関係を含む全開発単位が有限回の検証ループを通過しました（要件ごとの動作確認ではありません）。");
            var ledger = new RequirementLedgerDocument(
                1,
                request.ProjectId,
                request.ProjectName,
                state.UpdatedAt,
                request.RequirementTexts.Select(pair => new RequirementLedgerItem(
                    pair.Key,
                    pair.Value,
                    true,
                    // A shared DAG record is not evidence that this requirement works (external review 2026-10-26):
                    // each stays NOT_TESTED until a test made for it has run.
                    RequirementState.NotTested,
                    [evidence])).ToArray());
            await RequirementLedgerStore.SaveAtomicAsync(staging, ledger, cancellationToken);

            var sources = await ProjectCartridgeStore.FingerprintSourcesAsync(staging, cancellationToken);
            var sourceFingerprint = ComputeSourceSetFingerprint(sources);
            await WriteBaselineAsync(staging, sources, sourceFingerprint, cancellationToken);
            var tests = request.RequirementTexts.Select(pair => new ProjectTestDefinition(
                "TEST-" + pair.Key,
                pair.Key,
                "dag-release-gate",
                "依存する開発単位を合成し、静的検証とビルドを実行する。")).ToArray();
            var cartridge = new ProjectCartridge(
                1,
                request.ProjectId,
                request.ProjectName,
                "target-project",
                request.AppPurpose,
                request.OriginalRequirements[..Math.Min(request.OriginalRequirements.Length, 24_000)],
                request.RequirementTexts.Values.ToArray(),
                ledger,
                "依存方向を制約したDevelopment Unit DAGを、単一のrelease gateで統合する。",
                request.TechStack,
                [],
                sources.Select(source => Path.GetDirectoryName(PortableProjectPath.ToPlatformPath(source.RelativePath)) ?? ".")
                    .Select(path => string.IsNullOrWhiteSpace(path) ? "." : path.Replace('\\', '/'))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                request.BuildInstructions,
                request.RequirementTexts.Values.ToArray(),
                [],
                [],
                request.DesignDecisions,
                [],
                tests,
                tests.Select(test => new ProjectTestResult(test.Id, "NOT_TESTED", ".localstudio/verification/development-units.json", state.UpdatedAt)).ToArray(),
                "NOT_TESTED",
                [],
                [],
                new ProjectBuildState("verified", state.UpdatedAt, sourceFingerprint),
                plan,
                "verified",
                sources,
                state.UpdatedAt);
            await ProjectCartridgeStore.SaveAtomicAsync(staging, cartridge, cancellationToken);
            Directory.Move(staging, destination);
            return new(destination, sources.Count, sourceFingerprint, RequirementLedgerPolicy.Validate(ledger).CompletionAllowed);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    private static void EnsureCompleteState(DevelopmentUnitPlan plan, DevelopmentRunState state)
    {
        if (!state.PlanFingerprint.Equals(DevelopmentRunCoordinator.Fingerprint(plan), StringComparison.Ordinal))
            throw new InvalidDataException("開発状態とTask DAGが一致しません。");
        if (state.Units.Count != plan.Units.Count || state.Units.Any(item => item.Status != DevelopmentUnitStatus.Accepted || string.IsNullOrWhiteSpace(item.ArtifactFingerprint)))
            throw new InvalidOperationException("未検証または未完了の開発単位があるため、Stableへ昇格できません。");
    }

    private static void EnsureReleaseGateCoversPlan(DevelopmentUnitPlan plan)
    {
        var gate = FindReleaseGate(plan);
        if (gate.VerificationKind is "manual-spike" or "contract")
            throw new InvalidDataException("最終release gateにはビルドまたはテスト検証が必要です。");
        var byId = plan.Units.ToDictionary(unit => unit.Id, StringComparer.Ordinal);
        var covered = new HashSet<string>(StringComparer.Ordinal) { gate.Id };
        Visit(gate);
        if (covered.Count != plan.Units.Count)
            throw new InvalidDataException("最終release gateが全開発単位を包含していません。");

        void Visit(DevelopmentUnit unit)
        {
            foreach (var dependencyId in unit.Dependencies)
                if (covered.Add(dependencyId)) Visit(byId[dependencyId]);
        }
    }

    private static DevelopmentUnit FindReleaseGate(DevelopmentUnitPlan plan)
    {
        var dependedOn = plan.Units.SelectMany(unit => unit.Dependencies).ToHashSet(StringComparer.Ordinal);
        var sinks = plan.Units.Where(unit => !dependedOn.Contains(unit.Id)).ToArray();
        return sinks.Length == 1
            ? sinks[0]
            : throw new InvalidDataException("Stable昇格には単一の最終release gateが必要です。");
    }

    private static void ValidateRequest(DevelopmentUnitPlan plan, DevelopmentProjectAssemblyRequest request)
    {
        if (!PortableProjectPath.IsSafeIdentifier(request.ProjectId)) throw new InvalidDataException("Project IDが不正です。");
        if (string.IsNullOrWhiteSpace(request.ProjectName) || string.IsNullOrWhiteSpace(request.AppPurpose) || string.IsNullOrWhiteSpace(request.OriginalRequirements))
            throw new InvalidDataException("プロジェクトの目的と元要求が必要です。");
        var referenced = plan.Units.SelectMany(unit => unit.RequirementIds).ToHashSet(StringComparer.Ordinal);
        if (request.RequirementTexts.Count == 0 || request.RequirementTexts.Keys.Any(id => !referenced.Contains(id)) || referenced.Any(id => !request.RequirementTexts.ContainsKey(id)))
            throw new InvalidDataException("Requirement LedgerとTask DAGの要件IDが一致しません。");
    }

    private static async Task WriteBaselineAsync(string root, IReadOnlyList<ProjectSourceFingerprint> sources, string fingerprint, CancellationToken cancellationToken)
    {
        var filesRoot = SafeChild(root, Path.Combine(".localstudio", "baseline", "files"));
        Directory.CreateDirectory(filesRoot);
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var from = SafeChild(root, PortableProjectPath.ToPlatformPath(source.RelativePath));
            var to = SafeChild(filesRoot, PortableProjectPath.ToPlatformPath(source.RelativePath));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(from, to, false);
        }
        var manifest = new { schemaVersion = 1, acceptedAt = DateTimeOffset.UtcNow, status = "accepted", sourceFingerprint = fingerprint, files = sources };
        await File.WriteAllTextAsync(SafeChild(root, Path.Combine(".localstudio", "baseline", "manifest.json")), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false), cancellationToken);
    }

    private static string ComputeSourceSetFingerprint(IReadOnlyList<ProjectSourceFingerprint> sources)
    {
        var canonical = string.Join("\n", sources.Select(source => $"{source.RelativePath}\t{source.Size}\t{source.Sha256}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string SafeChild(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("統合先がプロジェクト領域を外れています。");
        return fullPath;
    }
}
