using System.Text.Json;

namespace SafeLocalHarness.Core;

public enum RunPhase { Idle, Inspecting, Planning, Acting, Verifying, Complete, Restoring, Failed }

public sealed record LedgerEntry(DateTimeOffset At, RunPhase Phase, string Action, string Target, string Fingerprint, string Result);

public sealed class ExecutionLedger
{
    private readonly List<LedgerEntry> _entries = [];
    public IReadOnlyList<LedgerEntry> Entries => _entries;
    public RunPhase Phase { get; private set; } = RunPhase.Idle;

    public void Record(RunPhase phase, string action, string target, string fingerprint, string result)
    {
        Phase = phase;
        _entries.Add(new(DateTimeOffset.Now, phase, action, target, fingerprint, result));
    }

    public bool WouldRepeatUnchanged(string action, string target, string fingerprint, int limit = 2) =>
        _entries.TakeLast(limit).Count(x => x.Action == action && x.Target == target && x.Fingerprint == fingerprint && x.Result == "NO_CHANGE") >= limit;

    public async Task SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, this, new JsonSerializerOptions { WriteIndented = true }, cancellationToken);
    }
}
