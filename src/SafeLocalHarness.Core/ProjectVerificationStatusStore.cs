using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SafeLocalHarness.Core;

public enum ManualFeatureStatus
{
    Pending,
    Confirmed,
    Failed
}

public sealed record FeatureVerificationState(
    string Id,
    string Text,
    string Instruction,
    ManualFeatureStatus Status,
    DateTimeOffset? CheckedAt);

public sealed record ProjectVerificationState(
    bool Applicable,
    string Level,
    string Explanation,
    string SourceFingerprint,
    IReadOnlyList<FeatureVerificationState> Features)
{
    public static ProjectVerificationState NotApplicable(string explanation) =>
        new(false, "not-applicable", explanation, "", []);
}

public interface IProjectVerificationStatusStore
{
    ProjectVerificationState Load(string projectPath);
    Task<ProjectVerificationState> RecordAsync(
        string projectPath,
        string requirementId,
        ManualFeatureStatus status,
        CancellationToken cancellationToken = default);
}

public sealed class ProjectVerificationStatusStore(AppPaths paths) : IProjectVerificationStatusStore
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] WebFingerprintFiles = ["index.html", "styles.css", "app.js", "requirements.json"];
    private static readonly string[] WindowsFingerprintFiles = ["Program.cs", "app.manifest", "requirements.json"];

    public ProjectVerificationState Load(string projectPath)
    {
        var root = ValidateProjectPath(projectPath);
        var requirementsPath = Path.Combine(root, "requirements.json");
        if (!File.Exists(requirementsPath)) return ProjectVerificationState.NotApplicable("検証対象の要件情報がありません。");

        try
        {
            using var requirements = JsonDocument.Parse(File.ReadAllText(requirementsPath, StrictUtf8));
            var requirementsRoot = requirements.RootElement;
            var appType = requirementsRoot.GetProperty("appType").GetString();
            var instructions = LoadInstructions(root);
            FeatureVerificationState[] requested;
            if (appType is "generated-local-web-app" or "generated-local-windows-app")
            {
                requested = requirementsRoot.GetProperty("requestedFeatures").EnumerateArray()
                    .Select(item => CreatePendingFeature(
                        item.GetProperty("id").GetString() ?? "",
                        item.GetProperty("text").GetString() ?? "",
                        instructions))
                    .Where(item => !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.Text))
                    .Take(8)
                    .ToArray();
            }
            else if (appType == "local-static-web-app")
            {
                requested = LoadLegacyFeatures(root);
                if (requested.Length == 0)
                    return ProjectVerificationState.NotApplicable("このプロジェクトは検証済みテンプレートです。追加の操作確認は必要ありません。");
            }
            else
            {
                return ProjectVerificationState.NotApplicable("この種類のプロジェクトにはGUI操作確認を適用できません。");
            }
            if (requested.Length == 0) return ProjectVerificationState.NotApplicable("確認できる機能がありません。");

            var fingerprint = ComputeFingerprint(root, requested);
            var evidencePath = ManualEvidencePath(root);
            if (!File.Exists(evidencePath))
                return BuildState("pending", "実際にアプリを操作し、機能ごとに結果を記録してください。", fingerprint, requested);

            using var evidence = JsonDocument.Parse(File.ReadAllText(evidencePath, StrictUtf8));
            var evidenceRoot = evidence.RootElement;
            var recordedFingerprint = evidenceRoot.GetProperty("sourceFingerprint").GetString() ?? "";
            if (recordedFingerprint != fingerprint && recordedFingerprint != ComputeLegacyFingerprint(root))
                return BuildState("stale", "確認後にコードまたは要件が変わりました。すべての機能をもう一度確認してください。", fingerprint, requested);

            var recorded = evidenceRoot.GetProperty("requirements").EnumerateArray()
                .ToDictionary(
                    item => item.GetProperty("id").GetString() ?? "",
                    item => new
                    {
                        Status = Enum.TryParse<ManualFeatureStatus>(item.GetProperty("status").GetString(), true, out var parsed) ? parsed : ManualFeatureStatus.Pending,
                        CheckedAt = item.TryGetProperty("checkedAt", out var checkedAt) &&
                                    checkedAt.ValueKind == JsonValueKind.String &&
                                    checkedAt.TryGetDateTimeOffset(out var value)
                            ? value
                            : (DateTimeOffset?)null
                    },
                    StringComparer.Ordinal);
            var merged = requested.Select(item => recorded.TryGetValue(item.Id, out var value)
                ? item with { Status = value.Status, CheckedAt = value.CheckedAt }
                : item).ToArray();
            return DeriveState(fingerprint, merged);
        }
        catch (Exception ex) when (ex is IOException or JsonException or DecoderFallbackException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            return ProjectVerificationState.NotApplicable("検証情報を安全に読み取れませんでした。");
        }
    }

    public async Task<ProjectVerificationState> RecordAsync(
        string projectPath,
        string requirementId,
        ManualFeatureStatus status,
        CancellationToken cancellationToken = default)
    {
        if (status == ManualFeatureStatus.Pending) throw new ArgumentOutOfRangeException(nameof(status));
        var root = ValidateProjectPath(projectPath);
        var current = Load(root);
        if (!current.Applicable) throw new InvalidOperationException(current.Explanation);
        if (!current.Features.Any(item => item.Id == requirementId)) throw new InvalidDataException("選択した機能は現在の要件にありません。");

        var now = DateTimeOffset.Now;
        var updated = current.Features.Select(item => item.Id == requirementId
            ? item with { Status = status, CheckedAt = now }
            : item).ToArray();
        var state = DeriveState(current.SourceFingerprint, updated);
        var verificationRoot = Path.Combine(root, ".localstudio", "verification");
        Directory.CreateDirectory(verificationRoot);
        var evidence = new
        {
            schema = 1,
            sourceFingerprint = current.SourceFingerprint,
            updatedAt = now,
            level = state.Level,
            requirements = updated.Select(item => new
            {
                id = item.Id,
                text = item.Text,
                status = item.Status.ToString().ToLowerInvariant(),
                checkedAt = item.CheckedAt
            })
        };
        await WriteAtomicallyAsync(ManualEvidencePath(root), JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
        await UpdateLifecycleAsync(root, state.Level, cancellationToken);
        return state;
    }

    private string ValidateProjectPath(string projectPath)
    {
        var projectRoot = Path.GetFullPath(paths.ProjectRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar);
        if (!candidate.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(candidate))
            throw new InvalidDataException("LocalStudioのプロジェクト領域外は操作できません。");
        if ((new DirectoryInfo(candidate).Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("リンクされたプロジェクト領域は操作できません。");
        UserSpaceGuard.EnsureAllowed(candidate);
        return candidate;
    }

    private static string ComputeFingerprint(string root, IReadOnlyList<FeatureVerificationState> requirements)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendFingerprintFiles(hash, root);
        foreach (var requirement in requirements)
            hash.AppendData(Encoding.UTF8.GetBytes($"{requirement.Id}\n{requirement.Text}\n{requirement.Instruction}\n"));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string ComputeLegacyFingerprint(string root)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendFingerprintFiles(hash, root);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AppendFingerprintFiles(IncrementalHash hash, string root)
    {
        var baseFiles = File.Exists(Path.Combine(root, "index.html"))
            ? WebFingerprintFiles
            : File.Exists(Path.Combine(root, "Program.cs"))
                ? WindowsFingerprintFiles
                : throw new FileNotFoundException("検証対象となる実装ファイルがありません。");
        var files = baseFiles.Concat(File.Exists(Path.Combine(root, "verification-plan.json")) ? ["verification-plan.json"] : []).ToArray();
        foreach (var relative in files)
        {
            var path = Path.Combine(root, relative);
            if (!File.Exists(path)) throw new FileNotFoundException("検証対象ファイルがありません。", relative);
            EnsureRegularFile(path);
            hash.AppendData(Encoding.UTF8.GetBytes(relative + "\n"));
            hash.AppendData(File.ReadAllBytes(path));
        }
    }

    private static FeatureVerificationState CreatePendingFeature(
        string id,
        string text,
        IReadOnlyDictionary<string, string> instructions) =>
        new(id, text, instructions.GetValueOrDefault(id, DefaultInstruction(text)), ManualFeatureStatus.Pending, null);

    private static FeatureVerificationState[] LoadLegacyFeatures(string root)
    {
        var manifestPath = Path.Combine(root, "localstudio.json");
        EnsureRegularFile(manifestPath);
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath, StrictUtf8));
        var manifestRoot = manifest.RootElement;
        if (manifestRoot.GetProperty("projectKind").GetString() != "DesktopOrWebApp") return [];
        var lifecycle = manifestRoot.GetProperty("lifecycle").GetString() ?? "";
        if (lifecycle is not ("generated-project" or "structural-preview" or "user-verified" or "verification-failed")) return [];
        return manifestRoot.GetProperty("blueprint").GetProperty("features").EnumerateArray()
            .Select((item, index) => item.GetString() ?? "")
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Take(8)
            .Select((text, index) => new FeatureVerificationState(
                $"LS-LEGACY-{index + 1:000}",
                text,
                DefaultInstruction(text),
                ManualFeatureStatus.Pending,
                null))
            .ToArray();
    }

    private static string DefaultInstruction(string requirement)
    {
        if (requirement.Contains("背景", StringComparison.Ordinal) || requirement.Contains("色", StringComparison.Ordinal) ||
            requirement.Contains("罫線", StringComparison.Ordinal) || requirement.Contains("フォント", StringComparison.Ordinal) ||
            requirement.Contains("デザイン", StringComparison.Ordinal) || requirement.Contains("表示", StringComparison.Ordinal))
            return $"アプリ画面を見て、「{requirement}」が依頼どおりに反映されていることを確認します。";
        if (requirement.Contains("保存", StringComparison.Ordinal))
            return $"「{requirement}」を操作し、再読込後も内容が復元されることを確認します。";
        if (requirement.Contains("削除", StringComparison.Ordinal) || requirement.Contains("消去", StringComparison.Ordinal))
            return $"「{requirement}」を操作し、再読込後も削除した内容が戻らないことを確認します。";
        return $"アプリで「{requirement}」を実際に操作し、依頼どおりの結果になることを確認します。";
    }

    private static IReadOnlyDictionary<string, string> LoadInstructions(string root)
    {
        var path = Path.Combine(root, "verification-plan.json");
        if (!File.Exists(path)) return new Dictionary<string, string>();
        EnsureRegularFile(path);
        using var plan = JsonDocument.Parse(File.ReadAllText(path, StrictUtf8));
        if (plan.RootElement.GetProperty("scope").GetString() != "guided-manual-runtime-check")
            throw new InvalidDataException("操作確認計画の範囲が不正です。");
        return plan.RootElement.GetProperty("requirements").EnumerateArray()
            .ToDictionary(
                item => item.GetProperty("id").GetString() ?? "",
                item => item.GetProperty("instruction").GetString() ?? "",
                StringComparer.Ordinal);
    }

    private static ProjectVerificationState DeriveState(string fingerprint, IReadOnlyList<FeatureVerificationState> features)
    {
        if (features.Any(item => item.Status == ManualFeatureStatus.Failed))
            return BuildState("failed", "問題がある機能が記録されています。修正後にもう一度確認してください。", fingerprint, features);
        if (features.All(item => item.Status == ManualFeatureStatus.Confirmed))
            return BuildState("user-verified", "すべての機能がこのPCで操作確認済みです。", fingerprint, features);
        if (features.Any(item => item.Status == ManualFeatureStatus.Confirmed))
            return BuildState("partial", "一部の機能を確認済みです。残りの機能も確認してください。", fingerprint, features);
        return BuildState("pending", "実際にアプリを操作し、機能ごとに結果を記録してください。", fingerprint, features);
    }

    private static ProjectVerificationState BuildState(
        string level,
        string explanation,
        string fingerprint,
        IReadOnlyList<FeatureVerificationState> features) =>
        new(true, level, explanation, fingerprint, features);

    private static string ManualEvidencePath(string root) => Path.Combine(root, ".localstudio", "verification", "manual.json");

    private static void EnsureRegularFile(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("リンクされた検証ファイルは操作できません。");
    }

    private static async Task UpdateLifecycleAsync(string root, string level, CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(root, "localstudio.json");
        EnsureRegularFile(manifestPath);
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath, StrictUtf8, cancellationToken))?.AsObject()
            ?? throw new InvalidDataException("プロジェクト情報を読み取れません。");
        manifest["lifecycle"] = level switch
        {
            "user-verified" => "user-verified",
            "failed" => "verification-failed",
            _ => "structural-preview"
        };
        await WriteAtomicallyAsync(manifestPath, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
    }

    private static async Task WriteAtomicallyAsync(string path, string content, CancellationToken cancellationToken)
    {
        if (File.Exists(path)) EnsureRegularFile(path);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary, content + "\n", new UTF8Encoding(false, true), cancellationToken);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
