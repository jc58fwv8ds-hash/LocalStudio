namespace SafeLocalHarness.Core;

public sealed record RequirementVerifierRegistration(string Kind, IRequirementVerifier Verifier);

/// <summary>
/// Separates project/DAG evidence from evidence that can prove one requirement. Generic release gates are useful
/// build evidence, but registering one here must never turn every requirement into PASS.
/// </summary>
public static class RequirementTestKindPolicy
{
    private static readonly HashSet<string> NonRequirementEvidenceKinds = new(StringComparer.Ordinal)
    {
        "dag-release-gate",
        "affected-deterministic"
    };

    public static bool CanProduceRequirementEvidence(string kind) =>
        PortableProjectPath.IsSafeIdentifier(kind) &&
        kind.Length <= 80 &&
        !NonRequirementEvidenceKinds.Contains(kind);

    public static void EnsureCanProduceRequirementEvidence(string kind)
    {
        if (!CanProduceRequirementEvidence(kind))
            throw new InvalidDataException("このTest kindは要件固有のPASS証拠には使用できません: " + kind);
    }
}

/// <summary>
/// Exact, fail-closed routing for deterministic requirement verification. There is deliberately no default verifier:
/// an unsupported test kind remains unverified until a host-owned adapter is explicitly registered.
/// </summary>
public sealed class RequirementVerifierRegistry : IRequirementVerifier
{
    private readonly IReadOnlyDictionary<string, IRequirementVerifier> _verifiers;

    public RequirementVerifierRegistry(IEnumerable<RequirementVerifierRegistration> registrations)
    {
        var verifiers = new Dictionary<string, IRequirementVerifier>(StringComparer.Ordinal);
        foreach (var registration in registrations)
        {
            RequirementTestKindPolicy.EnsureCanProduceRequirementEvidence(registration.Kind);
            if (registration.Verifier is null)
                throw new InvalidDataException("Requirement verifierが指定されていません: " + registration.Kind);
            if (!verifiers.TryAdd(registration.Kind, registration.Verifier))
                throw new InvalidDataException("Requirement verifier kindが重複しています: " + registration.Kind);
        }
        if (verifiers.Count is < 1 or > 32)
            throw new InvalidDataException("Requirement verifier registrationは1～32件である必要があります。");
        _verifiers = verifiers;
    }

    public IReadOnlyList<string> SupportedKinds => _verifiers.Keys.Order(StringComparer.Ordinal).ToArray();

    public Task<DeterministicRequirementVerification> VerifyAsync(
        string projectRoot,
        ProjectTestDefinition definition,
        CancellationToken cancellationToken = default)
    {
        if (!_verifiers.TryGetValue(definition.Kind, out var verifier))
            throw new InvalidOperationException("このTest kindに対応する決定論的Verifierは登録されていません: " + definition.Kind);
        return verifier.VerifyAsync(projectRoot, definition, cancellationToken);
    }
}
