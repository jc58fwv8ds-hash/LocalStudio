using System.Text.RegularExpressions;

namespace SafeLocalHarness.Core;

public sealed record DevelopmentUnit(
    string Id,
    string Title,
    string Outcome,
    string Layer,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> RequirementIds,
    string VerificationKind)
{
    public string BuildSystem { get; init; } = "dotnet";
}

public sealed record DevelopmentUnitPlan(
    int Schema,
    string ProductName,
    IReadOnlyList<DevelopmentUnit> Units);

public sealed record DevelopmentUnitPlanValidation(
    bool Passed,
    IReadOnlyList<string> Errors);

public static class DevelopmentUnitGraph
{
    private static readonly Regex SafeId = new("^[a-z0-9][a-z0-9-]{1,63}$", RegexOptions.CultureInvariant);
    private static readonly IReadOnlySet<string> VerificationKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "contract",
        "unit-test",
        "integration-test",
        "build",
        "manual-spike"
    };

    public static DevelopmentUnitPlanValidation Validate(DevelopmentUnitPlan plan)
    {
        var errors = new List<string>();
        if (plan.Schema != 1) errors.Add("plan.schema");
        if (string.IsNullOrWhiteSpace(plan.ProductName) || plan.ProductName.Length > 120) errors.Add("plan.product-name");
        if (plan.Units.Count is < 2 or > 64) errors.Add("plan.unit-count");

        var unitsById = new Dictionary<string, DevelopmentUnit>(StringComparer.Ordinal);
        foreach (var unit in plan.Units)
        {
            if (!SafeId.IsMatch(unit.Id)) errors.Add("unit.invalid-id:" + unit.Id);
            else if (!unitsById.TryAdd(unit.Id, unit)) errors.Add("unit.duplicate-id:" + unit.Id);
            if (string.IsNullOrWhiteSpace(unit.Title) || string.IsNullOrWhiteSpace(unit.Outcome)) errors.Add("unit.missing-purpose:" + unit.Id);
            if (!ArchitectureRules.AllowedDependencies.ContainsKey(unit.Layer)) errors.Add("unit.invalid-layer:" + unit.Id);
            if (unit.RequirementIds.Count == 0 || unit.RequirementIds.Count > 32) errors.Add("unit.requirements:" + unit.Id);
            if (!VerificationKinds.Contains(unit.VerificationKind)) errors.Add("unit.verification-kind:" + unit.Id);
            if (!DevelopmentStackCatalog.IsKnown(unit.BuildSystem)) errors.Add("unit.build-system:" + unit.Id);
        }

        foreach (var unit in plan.Units)
        {
            foreach (var dependencyId in unit.Dependencies.Distinct(StringComparer.Ordinal))
            {
                if (dependencyId == unit.Id) errors.Add("graph.self-dependency:" + unit.Id);
                if (!unitsById.TryGetValue(dependencyId, out var dependency))
                {
                    errors.Add("graph.missing-dependency:" + unit.Id + ":" + dependencyId);
                    continue;
                }
                if (!unit.Layer.Equals(dependency.Layer, StringComparison.OrdinalIgnoreCase) &&
                    !ArchitectureRules.IsAllowed(unit.Layer, dependency.Layer))
                    errors.Add("graph.layer-violation:" + unit.Id + ":" + dependencyId);
            }
        }

        if (HasCycle(plan.Units, unitsById)) errors.Add("graph.cycle");
        return new(errors.Count == 0, errors.Distinct(StringComparer.Ordinal).ToArray());
    }

    public static IReadOnlyList<string> ReadyFrontier(DevelopmentUnitPlan plan, IReadOnlySet<string> completedUnitIds)
    {
        var validation = Validate(plan);
        if (!validation.Passed) throw new InvalidDataException("開発単位グラフが不正です: " + string.Join(", ", validation.Errors));
        return plan.Units
            .Where(unit => !completedUnitIds.Contains(unit.Id) && unit.Dependencies.All(completedUnitIds.Contains))
            .Select(unit => unit.Id)
            .ToArray();
    }

    private static bool HasCycle(IReadOnlyList<DevelopmentUnit> units, IReadOnlyDictionary<string, DevelopmentUnit> unitsById)
    {
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var unit in units)
            if (Visit(unit.Id)) return true;
        return false;

        bool Visit(string id)
        {
            if (visited.Contains(id)) return false;
            if (!visiting.Add(id)) return true;
            if (unitsById.TryGetValue(id, out var unit))
                foreach (var dependency in unit.Dependencies)
                    if (unitsById.ContainsKey(dependency) && Visit(dependency)) return true;
            visiting.Remove(id);
            visited.Add(id);
            return false;
        }
    }
}
