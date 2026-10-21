using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SafeLocalHarness.Core;

public enum DevelopmentUnitStatus
{
    Pending,
    Running,
    VerificationFailed,
    Accepted,
    Blocked
}

public sealed record DevelopmentUnitRun(
    string UnitId,
    DevelopmentUnitStatus Status,
    int Attempt,
    string? ArtifactFingerprint,
    string? LastError,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt)
{
    public string? LastFailureKind { get; init; }
    public string? LastFailureStage { get; init; }
}

public sealed record DevelopmentRunState(
    int Schema,
    string PlanFingerprint,
    string ProductName,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<DevelopmentUnitRun> Units);

public static class DevelopmentRunCoordinator
{
    public const int MaxAttemptsPerUnit = 4;

    public static DevelopmentRunState Create(DevelopmentUnitPlan plan, DateTimeOffset? now = null)
    {
        EnsureValidPlan(plan);
        var at = now ?? DateTimeOffset.UtcNow;
        return new(
            1,
            Fingerprint(plan),
            plan.ProductName,
            at,
            plan.Units.Select(unit => new DevelopmentUnitRun(
                unit.Id,
                DevelopmentUnitStatus.Pending,
                0,
                null,
                null,
                null,
                null)).ToArray());
    }

    public static DevelopmentRunState Recover(
        DevelopmentUnitPlan plan,
        DevelopmentRunState state,
        DateTimeOffset? now = null)
    {
        ValidateState(plan, state);
        var at = now ?? DateTimeOffset.UtcNow;
        var recovered = state.Units.Select(unit => unit.Status == DevelopmentUnitStatus.Running
            ? unit with
            {
                Status = unit.Attempt >= MaxAttemptsPerUnit
                    ? DevelopmentUnitStatus.Blocked
                    : DevelopmentUnitStatus.VerificationFailed,
                LastError = "interrupted-before-verification",
                CompletedAt = at
            }
            : unit).ToArray();
        return state with { UpdatedAt = at, Units = recovered };
    }

    public static IReadOnlyList<string> ReadyUnits(DevelopmentUnitPlan plan, DevelopmentRunState state)
    {
        ValidateState(plan, state);
        var accepted = state.Units
            .Where(unit => unit.Status == DevelopmentUnitStatus.Accepted)
            .Select(unit => unit.UnitId)
            .ToHashSet(StringComparer.Ordinal);
        var runnable = state.Units
            .Where(unit => unit.Status is DevelopmentUnitStatus.Pending or DevelopmentUnitStatus.VerificationFailed)
            .Where(unit => unit.Attempt < MaxAttemptsPerUnit)
            .Select(unit => unit.UnitId)
            .ToHashSet(StringComparer.Ordinal);
        return DevelopmentUnitGraph.ReadyFrontier(plan, accepted)
            .Where(runnable.Contains)
            .ToArray();
    }

    public static DevelopmentRunState StartUnit(
        DevelopmentUnitPlan plan,
        DevelopmentRunState state,
        string unitId,
        DateTimeOffset? now = null)
    {
        if (!ReadyUnits(plan, state).Contains(unitId, StringComparer.Ordinal))
            throw new InvalidOperationException("依存関係を満たしていない開発単位は開始できません: " + unitId);
        var at = now ?? DateTimeOffset.UtcNow;
        return Replace(state, unitId, unit => unit with
        {
            Status = DevelopmentUnitStatus.Running,
            Attempt = unit.Attempt + 1,
            ArtifactFingerprint = null,
            LastError = null,
            LastFailureKind = null,
            LastFailureStage = null,
            StartedAt = at,
            CompletedAt = null
        }, at);
    }

    public static DevelopmentRunState RecordVerification(
        DevelopmentRunState state,
        string unitId,
        bool passed,
        string? artifactFingerprint,
        string? error,
        DateTimeOffset? now = null,
        string? failureKind = null,
        string? failureStage = null)
    {
        var current = state.Units.SingleOrDefault(unit => unit.UnitId == unitId)
            ?? throw new InvalidOperationException("未知の開発単位です: " + unitId);
        if (current.Status != DevelopmentUnitStatus.Running)
            throw new InvalidOperationException("実行中でない開発単位は検証記録できません: " + unitId);
        if (passed && string.IsNullOrWhiteSpace(artifactFingerprint))
            throw new InvalidOperationException("合格記録には成果物フィンガープリントが必要です。");

        var at = now ?? DateTimeOffset.UtcNow;
        return Replace(state, unitId, unit => unit with
        {
            Status = passed
                ? DevelopmentUnitStatus.Accepted
                : unit.Attempt >= MaxAttemptsPerUnit
                    ? DevelopmentUnitStatus.Blocked
                    : DevelopmentUnitStatus.VerificationFailed,
            ArtifactFingerprint = passed ? artifactFingerprint : null,
            LastError = passed ? null : NormalizeError(error),
            LastFailureKind = passed ? null : NormalizeClassification(failureKind),
            LastFailureStage = passed ? null : NormalizeClassification(failureStage),
            CompletedAt = at
        }, at);
    }

    public static async Task SaveAtomicAsync(
        string path,
        DevelopmentRunState state,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("状態保存先を確認できません。");
        Directory.CreateDirectory(directory);
        var temporary = fullPath + ".next-" + Guid.NewGuid().ToString("N");
        var backup = fullPath + ".previous";
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    state,
                    new JsonSerializerOptions { WriteIndented = true },
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            if (File.Exists(fullPath)) File.Replace(temporary, fullPath, backup, true);
            else File.Move(temporary, fullPath);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static async Task<DevelopmentRunState> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(Path.GetFullPath(path));
        return await JsonSerializer.DeserializeAsync<DevelopmentRunState>(stream, cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("開発状態を読み取れませんでした。");
    }

    public static string Fingerprint(DevelopmentUnitPlan plan)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(plan);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static DevelopmentRunState Replace(
        DevelopmentRunState state,
        string unitId,
        Func<DevelopmentUnitRun, DevelopmentUnitRun> update,
        DateTimeOffset at)
    {
        var found = false;
        var units = state.Units.Select(unit =>
        {
            if (unit.UnitId != unitId) return unit;
            found = true;
            return update(unit);
        }).ToArray();
        if (!found) throw new InvalidOperationException("未知の開発単位です: " + unitId);
        return state with { UpdatedAt = at, Units = units };
    }

    private static void ValidateState(DevelopmentUnitPlan plan, DevelopmentRunState state)
    {
        EnsureValidPlan(plan);
        if (state.Schema != 1 || !state.PlanFingerprint.Equals(Fingerprint(plan), StringComparison.Ordinal))
            throw new InvalidDataException("開発計画と実行状態が一致しません。");
        var expected = plan.Units.Select(unit => unit.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var actual = state.Units.Select(unit => unit.UnitId).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        if (!expected.SequenceEqual(actual, StringComparer.Ordinal) || actual.Distinct(StringComparer.Ordinal).Count() != actual.Length)
            throw new InvalidDataException("開発単位の状態が欠落または重複しています。");
    }

    private static void EnsureValidPlan(DevelopmentUnitPlan plan)
    {
        var validation = DevelopmentUnitGraph.Validate(plan);
        if (!validation.Passed)
            throw new InvalidDataException("開発単位グラフが不正です: " + string.Join(", ", validation.Errors));
    }

    private static string NormalizeError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return "verification-failed";
        var singleLine = string.Join(' ', error.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        return singleLine[..Math.Min(singleLine.Length, 1000)];
    }

    private static string? NormalizeClassification(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = new string(value.Where(character => char.IsLetterOrDigit(character) || character is '-' or '_').ToArray());
        return normalized.Length == 0 ? null : normalized[..Math.Min(normalized.Length, 80)];
    }
}
