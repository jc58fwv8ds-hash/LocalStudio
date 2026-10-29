using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SafeLocalHarness.Core;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RequirementState
{
    Pending,
    Passed,
    Failed,
    Unknown,
    NotTested,
    NotApplicable
}

public sealed record RequirementEvidence(
    string EvidenceId,
    string Kind,
    string RelativePath,
    string Sha256,
    DateTimeOffset RecordedAt,
    string Summary);

public sealed record RequirementLedgerItem(
    string Id,
    string Text,
    bool Mandatory,
    RequirementState State,
    IReadOnlyList<RequirementEvidence> Evidence,
    string? Notes = null);

public sealed record RequirementLedgerDocument(
    int SchemaVersion,
    string ProjectId,
    string ProjectName,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<RequirementLedgerItem> Items);

public sealed record RequirementLedgerValidation(
    bool Passed,
    bool CompletionAllowed,
    IReadOnlyList<string> Errors);

public static class RequirementLedgerPolicy
{
    private static readonly Regex SafeId = new("^[A-Z][A-Z0-9-]{2,63}$", RegexOptions.CultureInvariant);
    private static readonly Regex Sha256 = new("^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant);

    public static RequirementLedgerValidation Validate(RequirementLedgerDocument ledger)
    {
        var errors = new List<string>();
        if (ledger.SchemaVersion != 1) errors.Add("ledger.schema-version");
        if (!PortableProjectPath.IsSafeIdentifier(ledger.ProjectId)) errors.Add("ledger.project-id");
        if (string.IsNullOrWhiteSpace(ledger.ProjectName) || ledger.ProjectName.Length > 120) errors.Add("ledger.project-name");
        if (ledger.Items.Count is < 1 or > 512) errors.Add("ledger.item-count");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in ledger.Items)
        {
            if (!SafeId.IsMatch(item.Id)) errors.Add("requirement.invalid-id:" + item.Id);
            else if (!ids.Add(item.Id)) errors.Add("requirement.duplicate-id:" + item.Id);
            if (string.IsNullOrWhiteSpace(item.Text) || item.Text.Length > 1000) errors.Add("requirement.invalid-text:" + item.Id);
            if (item.Evidence.Count > 64) errors.Add("requirement.too-much-evidence:" + item.Id);
            foreach (var evidence in item.Evidence)
            {
                if (!PortableProjectPath.IsSafeIdentifier(evidence.EvidenceId)) errors.Add("evidence.invalid-id:" + item.Id);
                if (string.IsNullOrWhiteSpace(evidence.Kind) || evidence.Kind.Length > 80) errors.Add("evidence.invalid-kind:" + item.Id);
                if (!PortableProjectPath.IsSafeRelative(evidence.RelativePath)) errors.Add("evidence.invalid-path:" + item.Id);
                if (!Sha256.IsMatch(evidence.Sha256)) errors.Add("evidence.invalid-sha256:" + item.Id);
                if (string.IsNullOrWhiteSpace(evidence.Summary) || evidence.Summary.Length > 500) errors.Add("evidence.invalid-summary:" + item.Id);
            }
            if (item.State == RequirementState.Passed && item.Evidence.Count == 0)
                errors.Add("requirement.pass-without-evidence:" + item.Id);
        }

        var completionAllowed = errors.Count == 0 && ledger.Items
            .Where(item => item.Mandatory)
            .All(item => item.State == RequirementState.Passed && item.Evidence.Count > 0);
        return new(errors.Count == 0, completionAllowed, errors.Distinct(StringComparer.Ordinal).ToArray());
    }
}

public static class RequirementLedgerStore
{
    public const string RelativePath = ".localstudio/requirement-ledger.json";

    public static Task SaveAtomicAsync(string projectRoot, RequirementLedgerDocument ledger, CancellationToken cancellationToken = default)
    {
        var validation = RequirementLedgerPolicy.Validate(ledger);
        if (!validation.Passed) throw new InvalidDataException("Requirement Ledgerが不正です: " + string.Join(", ", validation.Errors));
        return PortableProjectJson.SaveAtomicAsync(Path.Combine(projectRoot, PortableProjectPath.ToPlatformPath(RelativePath)), ledger, cancellationToken);
    }

    public static async Task<RequirementLedgerDocument> LoadAsync(string projectRoot, CancellationToken cancellationToken = default)
    {
        var ledger = await PortableProjectJson.LoadAsync<RequirementLedgerDocument>(Path.Combine(projectRoot, PortableProjectPath.ToPlatformPath(RelativePath)), cancellationToken);
        var validation = RequirementLedgerPolicy.Validate(ledger);
        if (!validation.Passed) throw new InvalidDataException("Requirement Ledgerが不正です: " + string.Join(", ", validation.Errors));
        return ledger;
    }
}

public sealed record ProjectSourceFingerprint(string RelativePath, long Size, string Sha256);
public sealed record ProjectBuildState(string Status, DateTimeOffset RecordedAt, string? ArtifactFingerprint);
public sealed record ProjectTestAssertion(string Operation, string RelativePath, string? Expected = null);
public sealed record ProjectTestSpecification(int SchemaVersion, IReadOnlyList<ProjectTestAssertion> Assertions);
public sealed record ProjectTestDefinition(
    string Id,
    string RequirementId,
    string Kind,
    string Description,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ProjectTestSpecification? Specification = null);
public sealed record ProjectTestResult(string TestId, string Status, string EvidencePath, DateTimeOffset RecordedAt);

public sealed record ProjectCartridge(
    int SchemaVersion,
    string ProjectId,
    string ProjectName,
    string Scope,
    string AppPurpose,
    string OriginalRequirements,
    IReadOnlyList<string> StructuredRequirements,
    RequirementLedgerDocument RequirementLedger,
    string Architecture,
    IReadOnlyList<string> TechStack,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> DirectoryStructure,
    IReadOnlyList<string> BuildInstructions,
    IReadOnlyList<string> ImplementedFeatures,
    IReadOnlyList<string> PendingFeatures,
    IReadOnlyList<string> KnownIssues,
    IReadOnlyList<string> DesignDecisions,
    IReadOnlyList<string> UserDecisions,
    IReadOnlyList<ProjectTestDefinition> TestDefinitions,
    IReadOnlyList<ProjectTestResult> TestResults,
    string VerificationStatus,
    IReadOnlyList<string> UnresolvedItems,
    IReadOnlyList<string> Migrations,
    ProjectBuildState LastKnownWorkingBuild,
    DevelopmentUnitPlan TaskDag,
    string CurrentDevelopmentState,
    IReadOnlyList<ProjectSourceFingerprint> SourceFiles,
    DateTimeOffset UpdatedAt);

public sealed record ProjectCartridgeInspection(
    ProjectCartridge Cartridge,
    bool SchemaValid,
    bool SourceIntegrityValid,
    bool RequirementLedgerValid,
    bool TaskDagValid,
    IReadOnlyList<string> Issues)
{
    public bool CanResume => SchemaValid && SourceIntegrityValid && RequirementLedgerValid && TaskDagValid;
}

public static class ProjectCartridgeStore
{
    public const string RelativePath = ".localstudio/project-cartridge.json";
    private static readonly Regex Sha256 = new("^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant);

    public static async Task SaveAtomicAsync(string projectRoot, ProjectCartridge cartridge, CancellationToken cancellationToken = default)
    {
        var issues = ValidateStructure(cartridge);
        if (issues.Count > 0) throw new InvalidDataException("Project Cartridgeが不正です: " + string.Join(", ", issues));
        await PortableProjectJson.SaveAtomicAsync(Path.Combine(projectRoot, PortableProjectPath.ToPlatformPath(RelativePath)), cartridge, cancellationToken);
    }

    public static async Task<ProjectCartridgeInspection> InspectAsync(string projectRoot, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(projectRoot, PortableProjectPath.ToPlatformPath(RelativePath));
        var cartridge = await PortableProjectJson.LoadAsync<ProjectCartridge>(path, cancellationToken);
        var issues = ValidateStructure(cartridge).ToList();
        var schemaValid = cartridge.SchemaVersion == 1;
        var ledgerValidation = RequirementLedgerPolicy.Validate(cartridge.RequirementLedger);
        var dagValidation = DevelopmentUnitGraph.Validate(cartridge.TaskDag);
        var sourceIntegrityValid = true;
        foreach (var expected in cartridge.SourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!PortableProjectPath.IsSafeRelative(expected.RelativePath))
            {
                sourceIntegrityValid = false;
                continue;
            }
            // Older cartridges listed build outputs; they are regenerated from sources and never opened.
            if (ProjectFilePolicy.IsBuildOutput(expected.RelativePath)) continue;
            var fullPath = Path.GetFullPath(Path.Combine(projectRoot, PortableProjectPath.ToPlatformPath(expected.RelativePath)));
            if (!File.Exists(fullPath))
            {
                issues.Add("source.missing:" + expected.RelativePath);
                sourceIntegrityValid = false;
                continue;
            }
            await using var stream = File.OpenRead(fullPath);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            if (new FileInfo(fullPath).Length != expected.Size || !actual.Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add("source.changed:" + expected.RelativePath);
                sourceIntegrityValid = false;
            }
        }
        return new(cartridge, schemaValid, sourceIntegrityValid, ledgerValidation.Passed, dagValidation.Passed, issues.Distinct(StringComparer.Ordinal).ToArray());
    }

    public static async Task<IReadOnlyList<ProjectSourceFingerprint>> FingerprintSourcesAsync(string projectRoot, CancellationToken cancellationToken = default)
    {
        var result = new List<ProjectSourceFingerprint>();
        foreach (var file in Directory.EnumerateFiles(projectRoot, "*", SearchOption.AllDirectories)
                     .Where(path => !Path.GetRelativePath(projectRoot, path).StartsWith(".localstudio" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                     .Where(path => !ProjectFilePolicy.IsBuildOutput(Path.GetRelativePath(projectRoot, path)))
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var stream = File.OpenRead(file);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            result.Add(new(Path.GetRelativePath(projectRoot, file).Replace('\\', '/'), new FileInfo(file).Length, hash));
        }
        return result;
    }

    internal static IReadOnlyList<string> Validate(ProjectCartridge cartridge) => ValidateStructure(cartridge);

    private static IReadOnlyList<string> ValidateStructure(ProjectCartridge cartridge)
    {
        var issues = new List<string>();
        if (cartridge.SchemaVersion != 1) issues.Add("cartridge.schema-version");
        if (!PortableProjectPath.IsSafeIdentifier(cartridge.ProjectId)) issues.Add("cartridge.project-id");
        if (string.IsNullOrWhiteSpace(cartridge.ProjectName) || cartridge.ProjectName.Length > 120) issues.Add("cartridge.project-name");
        if (!cartridge.Scope.Equals("target-project", StringComparison.Ordinal)) issues.Add("cartridge.scope");
        if (string.IsNullOrWhiteSpace(cartridge.AppPurpose)) issues.Add("cartridge.app-purpose");
        if (cartridge.SourceFiles.Count > 2048) issues.Add("cartridge.source-count");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in cartridge.SourceFiles)
        {
            if (!PortableProjectPath.IsSafeRelative(source.RelativePath)) issues.Add("source.invalid-path:" + source.RelativePath);
            else if (!paths.Add(source.RelativePath)) issues.Add("source.duplicate-path:" + source.RelativePath);
            if (source.Size < 0 || !Sha256.IsMatch(source.Sha256)) issues.Add("source.invalid-fingerprint:" + source.RelativePath);
        }
        var ledgerValidation = RequirementLedgerPolicy.Validate(cartridge.RequirementLedger);
        issues.AddRange(ledgerValidation.Errors);
        var dagValidation = DevelopmentUnitGraph.Validate(cartridge.TaskDag);
        issues.AddRange(dagValidation.Errors);
        var requirementIds = cartridge.RequirementLedger.Items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var testIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in cartridge.TestDefinitions)
        {
            if (!PortableProjectPath.IsSafeIdentifier(definition.Id)) issues.Add("test.invalid-id:" + definition.Id);
            else if (!testIds.Add(definition.Id)) issues.Add("test.duplicate-id:" + definition.Id);
            if (!requirementIds.Contains(definition.RequirementId)) issues.Add("test.unknown-requirement:" + definition.Id);
            if (!PortableProjectPath.IsSafeIdentifier(definition.Kind) || definition.Kind.Length > 80) issues.Add("test.invalid-kind:" + definition.Id);
            if (string.IsNullOrWhiteSpace(definition.Description) || definition.Description.Length > 500) issues.Add("test.invalid-description:" + definition.Id);
            if (definition.Specification is { } specification)
            {
                if (specification.SchemaVersion != 1) issues.Add("test.specification-version:" + definition.Id);
                if (specification.Assertions.Count is < 1 or > 64) issues.Add("test.assertion-count:" + definition.Id);
                foreach (var assertion in specification.Assertions)
                {
                    if (!PortableProjectPath.IsSafeIdentifier(assertion.Operation) || assertion.Operation.Length > 80)
                        issues.Add("test.invalid-operation:" + definition.Id);
                    if (!PortableProjectPath.IsSafeRelative(assertion.RelativePath) ||
                        assertion.RelativePath.Replace('\\', '/').StartsWith(".localstudio/", StringComparison.OrdinalIgnoreCase))
                        issues.Add("test.invalid-assertion-path:" + definition.Id);
                    if (assertion.Expected?.Length > 4096) issues.Add("test.expected-too-long:" + definition.Id);
                }
            }
        }
        return issues.Distinct(StringComparer.Ordinal).ToArray();
    }
}

internal static class PortableProjectPath
{
    private static readonly Regex Identifier = new("^[a-zA-Z0-9][a-zA-Z0-9._-]{0,127}$", RegexOptions.CultureInvariant);

    public static bool IsSafeIdentifier(string value) => !string.IsNullOrWhiteSpace(value) && Identifier.IsMatch(value);

    public static bool IsSafeRelative(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value) || value.Contains(':')) return false;
        var normalized = value.Replace('\\', '/');
        return normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).All(part => part is not "." and not "..") &&
               !normalized.StartsWith("/", StringComparison.Ordinal);
    }

    public static string ToPlatformPath(string value) => value.Replace('/', Path.DirectorySeparatorChar);
}

internal static class PortableProjectJson
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task SaveAtomicAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ?? throw new InvalidOperationException("保存先を確認できません。");
        Directory.CreateDirectory(directory);
        var temporary = fullPath + ".next-" + Guid.NewGuid().ToString("N");
        var backup = fullPath + ".previous";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken);
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

    public static async Task<T> LoadAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(Path.GetFullPath(path));
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("構造化された開発状態を読み取れませんでした。");
    }
}
