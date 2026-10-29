using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SafeLocalHarness.Core;

/// <summary>
/// Host-owned verification for bounded project file invariants. It never starts a process or interprets a shell
/// command. The specification is data in the cartridge, while path resolution, limits and evidence are owned here.
/// </summary>
public sealed class ProjectFileInvariantRequirementVerifier : IRequirementVerifier
{
    public const string Kind = "project-file-invariant";
    private const long MaximumTextBytes = 4L * 1024 * 1024;
    private const long MaximumHashBytes = 64L * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Regex Sha256Pattern = new("^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant);

    public async Task<DeterministicRequirementVerification> VerifyAsync(
        string projectRoot,
        ProjectTestDefinition definition,
        CancellationToken cancellationToken = default)
    {
        if (!definition.Kind.Equals(Kind, StringComparison.Ordinal))
            throw new InvalidOperationException("Project file invariant Verifierへ異なるkindが渡されました: " + definition.Kind);
        var specification = definition.Specification
            ?? throw new InvalidDataException("Project file invariantに構造化Specificationがありません。");
        ValidateSpecification(specification);
        var root = Path.GetFullPath(projectRoot);
        UserSpaceGuard.EnsureAllowed(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("検証対象プロジェクトが見つかりません。");

        var results = new List<object>(specification.Assertions.Count);
        var passedCount = 0;
        foreach (var assertion in specification.Assertions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = SafeTarget(root, assertion.RelativePath);
            SafeWorkspaceEngine.RejectReparsePoints(root, target);
            var result = await EvaluateAsync(assertion, target, cancellationToken);
            if (result.Passed) passedCount++;
            results.Add(new
            {
                operation = assertion.Operation,
                relativePath = assertion.RelativePath.Replace('\\', '/'),
                result = result.Passed ? "PASS" : "FAIL",
                result.Reason,
                result.ActualSha256,
                expectedFingerprint = assertion.Expected is null
                    ? null
                    : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(assertion.Expected))).ToLowerInvariant()
            });
        }

        var passed = passedCount == specification.Assertions.Count;
        var summary = $"Project file invariants: {passedCount}/{specification.Assertions.Count} PASS.";
        var evidence = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            verifier = nameof(ProjectFileInvariantRequirementVerifier),
            testId = definition.Id,
            requirementId = definition.RequirementId,
            result = passed ? "PASS" : "FAIL",
            assertions = results
        }, PortableProjectJson.JsonOptions);
        return new(passed, Kind, summary, evidence, RequirementEvidenceClass.Structural);
    }

    private static void ValidateSpecification(ProjectTestSpecification specification)
    {
        if (specification.SchemaVersion != 1)
            throw new InvalidDataException("Project file invariant specification versionを処理できません。");
        if (specification.Assertions.Count is < 1 or > 64)
            throw new InvalidDataException("Project file invariant assertionは1～64件である必要があります。");
        foreach (var assertion in specification.Assertions)
        {
            var relative = assertion.RelativePath.Replace('\\', '/');
            if (!PortableProjectPath.IsSafeRelative(relative) || relative.StartsWith(".localstudio/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Project file invariantのpathが不正です: " + assertion.RelativePath);
            switch (assertion.Operation)
            {
                case "file-exists" when assertion.Expected is null:
                    break;
                case "utf8-contains-exact" when assertion.Expected is { Length: > 0 and <= 4096 }:
                    break;
                case "sha256-equals" when assertion.Expected is { } expected && Sha256Pattern.IsMatch(expected):
                    break;
                default:
                    throw new InvalidDataException("未対応または不正なProject file invariantです: " + assertion.Operation);
            }
        }
    }

    private static async Task<AssertionResult> EvaluateAsync(
        ProjectTestAssertion assertion,
        string target,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(target)) return new(false, "file-missing", null);
        var info = new FileInfo(target);
        switch (assertion.Operation)
        {
            case "file-exists":
                return new(true, "file-exists", await HashAsync(target, MaximumHashBytes, cancellationToken));
            case "utf8-contains-exact":
            {
                if (info.Length > MaximumTextBytes) return new(false, "text-file-too-large", null);
                byte[] bytes;
                try { bytes = await File.ReadAllBytesAsync(target, cancellationToken); }
                catch (IOException) { return new(false, "file-read-failed", null); }
                string text;
                try { text = StrictUtf8.GetString(bytes); }
                catch (DecoderFallbackException) { return new(false, "invalid-utf8", Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()); }
                return new(
                    text.Contains(assertion.Expected!, StringComparison.Ordinal),
                    text.Contains(assertion.Expected!, StringComparison.Ordinal) ? "exact-text-found" : "exact-text-missing",
                    Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
            }
            case "sha256-equals":
            {
                var actual = await HashAsync(target, MaximumHashBytes, cancellationToken);
                return actual is null
                    ? new(false, "file-too-large", null)
                    : new(actual.Equals(assertion.Expected, StringComparison.OrdinalIgnoreCase),
                        actual.Equals(assertion.Expected, StringComparison.OrdinalIgnoreCase) ? "sha256-matched" : "sha256-mismatch",
                        actual);
            }
            default:
                throw new InvalidOperationException("検証済みでないassertion operationです。");
        }
    }

    private static async Task<string?> HashAsync(string path, long maximumBytes, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (info.Length > maximumBytes) return null;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private static string SafeTarget(string root, string relativePath)
    {
        var rootWithSeparator = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(rootWithSeparator, PortableProjectPath.ToPlatformPath(relativePath)));
        if (!full.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Project file invariantのpathがプロジェクト外です。");
        return full;
    }

    private sealed record AssertionResult(bool Passed, string Reason, string? ActualSha256);
}
