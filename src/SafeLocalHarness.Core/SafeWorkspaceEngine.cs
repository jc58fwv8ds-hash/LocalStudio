using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SafeLocalHarness.Core;

public enum WorkspaceEditStatus { Changed, NoChange }
public sealed record WorkspaceEditResult(WorkspaceEditStatus Status, string RelativePath, string BeforeSha256, string AfterSha256);
public sealed record WorkspaceLedgerItem(DateTimeOffset At, string Action, string Target, string Result, string Detail);

public interface ISafeWorkspaceEngine
{
    Task<SafeWorkspaceSession> StartAsync(string projectRoot, CancellationToken cancellationToken = default);
}

public sealed class SafeWorkspaceEngine(AppPaths paths) : ISafeWorkspaceEngine
{
    public async Task<SafeWorkspaceSession> StartAsync(string projectRoot, CancellationToken cancellationToken = default)
    {
        UserSpaceGuard.EnsureAllowed(projectRoot);
        var allowedRoot = Path.GetFullPath(paths.ProjectRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (!(resolved + Path.DirectorySeparatorChar).StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("編集対象はLocalStudioのプロジェクトに限定されています。");
        if (!File.Exists(Path.Combine(resolved, "localstudio.json")))
            throw new InvalidOperationException("LocalStudioプロジェクトとして確認できませんでした。");
        RejectReparsePoints(resolved, resolved);
        var runId = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N")[..8];
        var runRoot = Path.Combine(resolved, ".localstudio", "runs", runId);
        Directory.CreateDirectory(Path.Combine(runRoot, "files"));
        var session = new SafeWorkspaceSession(resolved, runRoot, runId);
        await session.InitializeAsync(cancellationToken);
        return session;
    }

    internal static void RejectReparsePoints(string root, string path)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var current = File.Exists(path) ? Path.GetDirectoryName(Path.GetFullPath(path))! : Path.GetFullPath(path);
        while (current.Length >= rootFull.Length)
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("リンクや接合点を経由する編集は許可されていません。");
            }
            if (current.Equals(rootFull, StringComparison.OrdinalIgnoreCase)) break;
            var parent = Path.GetDirectoryName(current);
            if (parent is null) break;
            current = parent;
        }
    }
}

public sealed class SafeWorkspaceSession
{
    private const int MaximumFileBytes = 2 * 1024 * 1024;
    private const int MaximumReplacementChars = 6000;
    private readonly string _projectRoot;
    private readonly string _runRoot;
    private readonly string _runId;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, string> _readFingerprints = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _snapshotted = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _created = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _changed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _noChangeCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<WorkspaceLedgerItem> _ledger = [];
    private bool _verified;
    private bool _closed;

    internal SafeWorkspaceSession(string projectRoot, string runRoot, string runId)
    {
        _projectRoot = projectRoot; _runRoot = runRoot; _runId = runId;
    }

    public string RunId => _runId;
    public bool NeedsVerification => _changed.Count > 0 && !_verified;
    public bool IsVerified => _verified;
    public IReadOnlyCollection<string> ChangedFiles => _changed;

    internal async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await RecordAsync("start", "", "ACTIVE", "run snapshot session started", cancellationToken);
        await SaveStateAsync("active", cancellationToken);
    }

    public async Task<string> ReadTextAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureOpen();
            var (full, relative) = ResolveExistingFile(relativePath);
            var bytes = await File.ReadAllBytesAsync(full, cancellationToken);
            if (bytes.Length > MaximumFileBytes) throw new InvalidOperationException("2 MBを超えるファイルは安全編集の対象外です。");
            var text = DecodeStrictUtf8(bytes);
            EnsureHealthyText(text);
            var fingerprint = Hash(bytes);
            _readFingerprints[relative] = fingerprint;
            await RecordAsync("read", relative, "OK", fingerprint, cancellationToken);
            return text;
        }
        finally { _gate.Release(); }
    }

    public async Task<WorkspaceEditResult> ReplaceTextAsync(string relativePath, string oldText, string newText, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureOpen();
            if (oldText.Length == 0 || oldText.Length > MaximumReplacementChars || newText.Length > MaximumReplacementChars)
                throw new InvalidOperationException("置換文字列は1～6000文字に限定されています。");
            EnsureHealthyText(oldText); EnsureHealthyText(newText);
            var (full, relative) = ResolveExistingFile(relativePath);
            var bytes = await File.ReadAllBytesAsync(full, cancellationToken);
            if (bytes.Length > MaximumFileBytes) throw new InvalidOperationException("2 MBを超えるファイルは安全編集の対象外です。");
            var beforeHash = Hash(bytes);
            if (!_readFingerprints.TryGetValue(relative, out var readHash) || !readHash.Equals(beforeHash, StringComparison.Ordinal))
                throw new InvalidOperationException("変更直前に同じ内容を読み込む必要があります。ファイルが変化した場合は再読込してください。");
            var content = DecodeStrictUtf8(bytes);
            EnsureHealthyText(content);
            var matchCount = CountOccurrences(content, oldText);
            if (matchCount != 1)
            {
                await RecordAsync("replace_text", relative, "REJECTED", $"match_count={matchCount}", cancellationToken);
                throw new InvalidOperationException(matchCount == 0 ? "置換対象が見つかりません。" : "置換対象が複数あるため変更を拒否しました。");
            }
            var updated = content.Replace(oldText, newText, StringComparison.Ordinal);
            EnsureHealthyText(updated);
            if (updated.Equals(content, StringComparison.Ordinal))
            {
                var count = _noChangeCounts.TryGetValue(relative, out var current) ? current + 1 : 1;
                _noChangeCounts[relative] = count;
                await RecordAsync("replace_text", relative, "NO_CHANGE", $"count={count}", cancellationToken);
                if (count >= 2) throw new InvalidOperationException("同じ無変更操作が繰り返されたため、安全に停止しました。");
                return new(WorkspaceEditStatus.NoChange, relative, beforeHash, beforeHash);
            }

            await EnsureSnapshotAsync(full, relative, cancellationToken);
            var output = new UTF8Encoding(false, true).GetBytes(updated);
            var temporary = full + ".localstudio-tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await File.WriteAllBytesAsync(temporary, output, cancellationToken);
                File.Move(temporary, full, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            var afterHash = Hash(output);
            _changed.Add(relative);
            _verified = false;
            _readFingerprints[relative] = afterHash;
            _noChangeCounts.Remove(relative);
            await RecordAsync("replace_text", relative, "CHANGED", $"{beforeHash}->{afterHash}", cancellationToken);
            await SaveStateAsync("active", cancellationToken);
            return new(WorkspaceEditStatus.Changed, relative, beforeHash, afterHash);
        }
        finally { _gate.Release(); }
    }

    public async Task<WorkspaceEditResult> CreateTextFileAsync(string relativePath, string content, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureOpen();
            EnsureHealthyText(content);
            var output = new UTF8Encoding(false, true).GetBytes(content);
            if (output.Length > MaximumFileBytes) throw new InvalidOperationException("2 MBを超えるファイルは安全編集の対象外です。");
            var full = ResolvePath(relativePath, mustExist: false);
            var relative = Path.GetRelativePath(_projectRoot, full);
            if (File.Exists(full) || Directory.Exists(full)) throw new InvalidOperationException("同名のファイルまたはフォルダーが既に存在します。");
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            SafeWorkspaceEngine.RejectReparsePoints(_projectRoot, full);
            var temporary = full + ".localstudio-tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await File.WriteAllBytesAsync(temporary, output, cancellationToken);
                File.Move(temporary, full);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            var afterHash = Hash(output);
            _created.Add(relative);
            _changed.Add(relative);
            _verified = false;
            _readFingerprints[relative] = afterHash;
            await RecordAsync("create_text_file", relative, "CREATED", afterHash, cancellationToken);
            await SaveStateAsync("active", cancellationToken);
            return new(WorkspaceEditStatus.Changed, relative, "", afterHash);
        }
        finally { _gate.Release(); }
    }

    public async Task RecordVerificationAsync(bool passed, string details, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureOpen();
            if (_changed.Count == 0) throw new InvalidOperationException("検証対象の変更がありません。");
            _verified = passed;
            await RecordAsync("verify", "", passed ? "PASS" : "FAIL", Limit(details, 1000), cancellationToken);
            await SaveStateAsync(passed ? "verified" : "verification_failed", cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureOpen();
            foreach (var relative in _snapshotted)
            {
                var source = Path.Combine(_runRoot, "files", relative);
                var target = ResolvePath(relative, mustExist: false);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, true);
            }
            foreach (var relative in _created.OrderByDescending(x => x.Length))
            {
                var target = ResolvePath(relative, mustExist: false);
                if (File.Exists(target)) File.Delete(target);
            }
            _changed.Clear(); _readFingerprints.Clear(); _verified = false;
            await RecordAsync("rollback", "", "RESTORED", $"restored={_snapshotted.Count}; removed={_created.Count}", cancellationToken);
            await SaveStateAsync("restored", cancellationToken);
            _closed = true;
        }
        finally { _gate.Release(); }
    }

    public async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureOpen();
            if (_changed.Count > 0 && !_verified) throw new InvalidOperationException("未検証の変更があるため完了できません。");
            await RecordAsync("complete", "", "COMPLETE", $"changed={_changed.Count}", cancellationToken);
            await SaveStateAsync("complete", cancellationToken);
            _closed = true;
        }
        finally { _gate.Release(); }
    }

    public async Task AcceptBaselineAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureOpen();
            if (_changed.Count == 0 || !_verified) throw new InvalidOperationException("検証に合格した変更だけを正常版として受け入れられます。");
            var baselineRoot = Path.Combine(_projectRoot, ".localstudio", "baseline");
            var filesRoot = Path.Combine(baselineRoot, "files");
            Directory.CreateDirectory(filesRoot);
            foreach (var relative in _changed)
            {
                if (ProjectFilePolicy.IsBuildOutput(relative)) continue;
                var source = ResolvePath(relative, mustExist: true);
                var destination = Path.Combine(filesRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, true);
            }
            var entries = new List<object>();
            foreach (var baselineFile in Directory.EnumerateFiles(filesRoot, "*", SearchOption.AllDirectories)
                         .Where(path => !ProjectFilePolicy.IsBuildOutput(Path.GetRelativePath(filesRoot, path)))
                         .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                await using var stream = File.OpenRead(baselineFile);
                entries.Add(new
                {
                    path = Path.GetRelativePath(filesRoot, baselineFile).Replace('\\', '/'),
                    sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))
                });
            }
            var manifest = new { schema = 1, acceptedAt = DateTimeOffset.Now, status = "accepted", sourceRunId = _runId, files = entries };
            await WriteJsonAtomicAsync(Path.Combine(baselineRoot, "manifest.json"), manifest, cancellationToken);
            await RecordAsync("accept_baseline", "", "ACCEPTED", $"files={_changed.Count}", cancellationToken);
            await SaveStateAsync("accepted", cancellationToken);
            _closed = true;
        }
        finally { _gate.Release(); }
    }

    private async Task EnsureSnapshotAsync(string full, string relative, CancellationToken cancellationToken)
    {
        if (!_snapshotted.Add(relative)) return;
        var snapshot = Path.Combine(_runRoot, "files", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(snapshot)!);
        await using var input = File.OpenRead(full);
        await using var output = File.Create(snapshot);
        await input.CopyToAsync(output, cancellationToken);
    }

    private (string Full, string Relative) ResolveExistingFile(string relativePath)
    {
        var full = ResolvePath(relativePath, mustExist: true);
        return (full, Path.GetRelativePath(_projectRoot, full));
    }

    private string ResolvePath(string relativePath, bool mustExist)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)) throw new InvalidOperationException("相対ファイルパスだけを指定できます。");
        var full = Path.GetFullPath(Path.Combine(_projectRoot, relativePath));
        var prefix = _projectRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("プロジェクト外のファイルは操作できません。");
        var relative = Path.GetRelativePath(_projectRoot, full);
        if (relative.Equals(".localstudio", StringComparison.OrdinalIgnoreCase) || relative.StartsWith(".localstudio" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("LocalStudio制御領域は編集できません。");
        if (mustExist && !File.Exists(full)) throw new FileNotFoundException("対象ファイルが見つかりません。", relative);
        SafeWorkspaceEngine.RejectReparsePoints(_projectRoot, full);
        return full;
    }

    private async Task RecordAsync(string action, string target, string result, string detail, CancellationToken cancellationToken)
    {
        _ledger.Add(new(DateTimeOffset.Now, action, target, result, detail));
        await WriteJsonAtomicAsync(Path.Combine(_runRoot, "ledger.json"), _ledger, cancellationToken);
    }

    private Task SaveStateAsync(string status, CancellationToken cancellationToken) =>
        WriteJsonAtomicAsync(Path.Combine(_runRoot, "run.json"), new { schema = 1, runId = _runId, status, verified = _verified, changedFiles = _changed.OrderBy(x => x).ToArray(), updatedAt = DateTimeOffset.Now }, cancellationToken);

    private static async Task WriteJsonAtomicAsync(string path, object value, CancellationToken cancellationToken)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var json = JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(temporary, json, new UTF8Encoding(false), cancellationToken);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string DecodeStrictUtf8(byte[] bytes) => new UTF8Encoding(false, true).GetString(bytes);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static void EnsureHealthyText(string text)
    {
        if (text.Contains('\0') || text.Contains('�')) throw new InvalidDataException("NULまたはUnicode置換文字を含む内容は拒否されました。");
    }
    private static int CountOccurrences(string text, string value)
    {
        var count = 0; var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0) { count++; index += value.Length; }
        return count;
    }
    private static string Limit(string value, int length) => value.Length <= length ? value : value[..length];
    private void EnsureOpen() { if (_closed) throw new InvalidOperationException("この編集セッションは終了しています。"); }
}
