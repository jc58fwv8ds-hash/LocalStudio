using System.Security.Cryptography;
using System.Text.Json;

namespace SafeLocalHarness.Core;

public sealed record DeterministicRequirementVerification(
    bool Passed,
    string Kind,
    string Summary,
    ReadOnlyMemory<byte> EvidenceContent,
    RequirementEvidenceClass EvidenceClass = RequirementEvidenceClass.Structural);

public interface IRequirementVerifier
{
    Task<DeterministicRequirementVerification> VerifyAsync(
        string projectRoot,
        ProjectTestDefinition definition,
        CancellationToken cancellationToken = default);
}

public sealed record RequirementVerificationRecord(
    string RequirementId,
    string TestId,
    RequirementState State,
    string EvidenceRelativePath,
    string EvidenceSha256,
    bool CompletionAllowed);

/// <summary>
/// Runs one cartridge-defined deterministic test and records evidence. A caller cannot submit a PASS directly:
/// only the injected host-owned verifier can produce the outcome, and this service owns the evidence hash and ledger update.
/// </summary>
public sealed class RequirementVerificationService(IRequirementVerifier verifier)
{
    private const int MaximumEvidenceBytes = 4 * 1024 * 1024;

    public async Task<RequirementVerificationRecord> VerifyAsync(
        string projectRoot,
        string testId,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(projectRoot);
        UserSpaceGuard.EnsureAllowed(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("検証対象プロジェクトが見つかりません。");
        if (!PortableProjectPath.IsSafeIdentifier(testId)) throw new InvalidDataException("Test IDが不正です。");

        var inspection = await ProjectCartridgeStore.InspectAsync(root, cancellationToken);
        if (!inspection.CanResume)
            throw new InvalidDataException("Project Cartridgeを安全に検証できません: " + string.Join(", ", inspection.Issues));
        var cartridge = inspection.Cartridge;
        var ledger = await RequirementLedgerStore.LoadAsync(root, cancellationToken);
        EnsureLedgerIdentity(cartridge.RequirementLedger, ledger);

        var definition = cartridge.TestDefinitions.SingleOrDefault(item => item.Id.Equals(testId, StringComparison.Ordinal))
            ?? throw new InvalidDataException("Project Cartridgeに登録されていないTest IDです: " + testId);
        var requirement = ledger.Items.SingleOrDefault(item => item.Id.Equals(definition.RequirementId, StringComparison.Ordinal))
            ?? throw new InvalidDataException("TestDefinitionのRequirement IDがLedgerに存在しません: " + definition.RequirementId);
        RequirementTestKindPolicy.EnsureCanProduceRequirementEvidence(definition.Kind);

        var outcome = await verifier.VerifyAsync(root, definition, cancellationToken);
        ValidateOutcome(outcome);
        var relativePath = $".localstudio/verification/requirements/{testId}.evidence";
        var evidencePath = SafeChild(root, PortableProjectPath.ToPlatformPath(relativePath));
        await WriteAtomicAsync(evidencePath, outcome.EvidenceContent, cancellationToken);
        var evidenceHash = Convert.ToHexString(SHA256.HashData(outcome.EvidenceContent.Span)).ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;
        var evidence = new RequirementEvidence(
            testId,
            outcome.Kind.Trim(),
            relativePath,
            evidenceHash,
            now,
            outcome.Summary.Trim(),
            outcome.EvidenceClass);
        var testResults = cartridge.TestResults
            .Where(result => !result.TestId.Equals(testId, StringComparison.Ordinal))
            .Append(new ProjectTestResult(testId, outcome.Passed ? "PASS" : "FAIL", relativePath, now, outcome.EvidenceClass))
            .OrderBy(result => result.TestId, StringComparer.Ordinal)
            .ToArray();
        var state = AggregateRequirementState(cartridge.TestDefinitions, testResults, requirement.Id);
        var updatedItems = ledger.Items.Select(item => !item.Id.Equals(requirement.Id, StringComparison.Ordinal)
            ? item
            : item with
            {
                State = state,
                Evidence = item.Evidence.Where(existing => !existing.EvidenceId.Equals(testId, StringComparison.Ordinal)).Append(evidence).ToArray(),
                Notes = state == RequirementState.Failed
                    ? outcome.Passed ? "1件以上の決定論的検証が失敗しています。" : outcome.Summary.Trim()
                    : null
            }).ToArray();
        var updatedLedger = ledger with { UpdatedAt = now, Items = updatedItems };
        var ledgerValidation = RequirementLedgerPolicy.Validate(updatedLedger);
        if (!ledgerValidation.Passed)
            throw new InvalidDataException("検証後のRequirement Ledgerが不正です: " + string.Join(", ", ledgerValidation.Errors));

        var updatedCartridge = cartridge with
        {
            RequirementLedger = updatedLedger,
            ImplementedFeatures = updatedItems.Where(item => item.State == RequirementState.Passed).Select(item => item.Text).ToArray(),
            PendingFeatures = updatedItems.Where(item => item.State != RequirementState.Passed).Select(item => item.Text).ToArray(),
            TestResults = testResults,
            VerificationStatus = ledgerValidation.CompletionAllowed ? "PASS" : updatedItems.Any(item => item.State == RequirementState.Failed) ? "FAILED" : "NOT_TESTED",
            UnresolvedItems = updatedItems.Where(item => item.Mandatory && item.State != RequirementState.Passed).Select(item => item.Text).ToArray(),
            CurrentDevelopmentState = ledgerValidation.CompletionAllowed ? "verified-complete" : "awaiting-requirement-evidence",
            UpdatedAt = now
        };

        await SaveLedgerAndCartridgeAsync(root, ledger, updatedLedger, updatedCartridge, cancellationToken);
        return new(requirement.Id, testId, state, relativePath, evidenceHash, ledgerValidation.CompletionAllowed);
    }

    private static RequirementState AggregateRequirementState(
        IReadOnlyList<ProjectTestDefinition> definitions,
        IReadOnlyList<ProjectTestResult> results,
        string requirementId)
    {
        var requiredTests = definitions.Where(item => item.RequirementId.Equals(requirementId, StringComparison.Ordinal)).ToArray();
        if (requiredTests.Length == 0)
            throw new InvalidDataException("Requirementに対応するTestDefinitionがありません: " + requirementId);
        var byId = results.GroupBy(item => item.TestId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Single().Status, StringComparer.Ordinal);
        var statuses = requiredTests.Select(test => byId.GetValueOrDefault(test.Id, "NOT_TESTED")).ToArray();
        if (statuses.Any(status => status.Equals("FAIL", StringComparison.Ordinal))) return RequirementState.Failed;
        if (!statuses.All(status => status.Equals("PASS", StringComparison.Ordinal))) return RequirementState.NotTested;
        var resultById = results.GroupBy(item => item.TestId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        var hasCompletionEvidence = requiredTests
            .Select(test => resultById[test.Id].EvidenceClass)
            .Any(evidenceClass => evidenceClass is RequirementEvidenceClass.Behavioral or RequirementEvidenceClass.ManualConfirmed);
        return hasCompletionEvidence ? RequirementState.Passed : RequirementState.NotTested;
    }

    private static async Task SaveLedgerAndCartridgeAsync(
        string projectRoot,
        RequirementLedgerDocument originalLedger,
        RequirementLedgerDocument updatedLedger,
        ProjectCartridge updatedCartridge,
        CancellationToken cancellationToken)
    {
        await RequirementLedgerStore.SaveAtomicAsync(projectRoot, updatedLedger, cancellationToken);
        try
        {
            await ProjectCartridgeStore.SaveAtomicAsync(projectRoot, updatedCartridge, cancellationToken);
        }
        catch
        {
            try
            {
                await RequirementLedgerStore.SaveAtomicAsync(projectRoot, originalLedger, CancellationToken.None);
            }
            catch
            {
                // InspectAsync will fail closed if the durable records cannot be made consistent.
            }
            throw;
        }
    }

    private static void EnsureLedgerIdentity(
        RequirementLedgerDocument embedded,
        RequirementLedgerDocument stored)
    {
        var embeddedJson = JsonSerializer.Serialize(embedded, PortableProjectJson.JsonOptions);
        var storedJson = JsonSerializer.Serialize(stored, PortableProjectJson.JsonOptions);
        if (!embeddedJson.Equals(storedJson, StringComparison.Ordinal))
            throw new InvalidDataException("Project CartridgeとRequirement Ledgerが一致しません。安全のため検証を停止します。");
    }

    private static void ValidateOutcome(DeterministicRequirementVerification outcome)
    {
        if (string.IsNullOrWhiteSpace(outcome.Kind) || outcome.Kind.Length > 80)
            throw new InvalidDataException("Verifierのevidence kindが不正です。");
        if (string.IsNullOrWhiteSpace(outcome.Summary) || outcome.Summary.Length > 500)
            throw new InvalidDataException("Verifierのsummaryが不正です。");
        if (outcome.EvidenceContent.Length is < 1 or > MaximumEvidenceBytes)
            throw new InvalidDataException("Verifierのevidence sizeが不正です。");
        if (!Enum.IsDefined(outcome.EvidenceClass) || outcome.EvidenceClass == RequirementEvidenceClass.Unclassified)
            throw new InvalidDataException("Verifierのevidence classが未分類です。");
    }

    private static async Task WriteAtomicAsync(
        string path,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Evidence保存先を確認できません。");
        Directory.CreateDirectory(directory);
        var temporary = path + ".next-" + Guid.NewGuid().ToString("N");
        var backup = path + ".previous";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            if (File.Exists(path)) File.Replace(temporary, path, backup, true);
            else File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string SafeChild(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Evidence保存先がプロジェクト外です。");
        UserSpaceGuard.EnsureAllowed(fullPath);
        return fullPath;
    }
}
