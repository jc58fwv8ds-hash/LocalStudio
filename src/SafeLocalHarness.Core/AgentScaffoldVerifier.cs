using System.Text.Json;

namespace SafeLocalHarness.Core;

public interface IAgentScaffoldVerifier
{
    Task<ProjectVerificationResult> VerifyAsync(string projectRoot, CancellationToken cancellationToken = default);
}

public sealed class AgentScaffoldVerifier : IAgentScaffoldVerifier
{
    private static readonly HashSet<string> ScaffoldCapabilities = new(StringComparer.Ordinal)
    {
        "workspace.read", "workspace.write"
    };

    public async Task<ProjectVerificationResult> VerifyAsync(string projectRoot, CancellationToken cancellationToken = default)
    {
        var passed = new List<string>();
        var errors = new List<string>();
        var agentPath = Path.Combine(projectRoot, "agent.json");
        var manifestPath = Path.Combine(projectRoot, "localstudio.json");
        var promptPath = Path.Combine(projectRoot, "PROMPT.md");
        var requirementsPath = Path.Combine(projectRoot, "requirements.json");
        Check(File.Exists(agentPath), "file:agent.json", passed, errors);
        Check(File.Exists(manifestPath), "file:localstudio.json", passed, errors);
        Check(File.Exists(promptPath), "file:PROMPT.md", passed, errors);
        Check(File.Exists(requirementsPath), "file:requirements.json", passed, errors);
        if (errors.Count > 0) return new(false, passed, errors, DateTimeOffset.Now);

        using var agent = JsonDocument.Parse(await File.ReadAllTextAsync(agentPath, cancellationToken));
        var root = agent.RootElement;
        Check(root.GetProperty("schema").GetInt32() == 1, "agent.schema", passed, errors);
        Check(root.GetProperty("artifactType").GetString() == "design-scaffold", "agent.truthful-stage", passed, errors);
        Check(!root.GetProperty("executionEnabled").GetBoolean(), "agent.execution-disabled", passed, errors);
        Check(root.GetProperty("grantedPermissions").GetArrayLength() == 0, "agent.no-default-grants", passed, errors);
        Check(root.GetProperty("externalActionsRequireConfirmation").GetBoolean(), "agent.external-confirmation", passed, errors);
        var requested = root.GetProperty("requestedPermissions").EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
        Check(requested.All(ScaffoldCapabilities.Contains), "agent.local-capabilities-only", passed, errors);
        Check(root.GetProperty("maxSteps").GetInt32() is > 0 and <= 20, "agent.step-budget", passed, errors);
        var prompt = await File.ReadAllTextAsync(promptPath, cancellationToken);
        Check(prompt.Contains("実装前", StringComparison.Ordinal) && prompt.Contains("権限は未付与", StringComparison.Ordinal), "agent.prompt-disclosure", passed, errors);
        var requirements = await File.ReadAllTextAsync(requirementsPath, cancellationToken);
        Check(requirements.Contains("no-hidden-screen-capture", StringComparison.Ordinal), "agent.no-hidden-capture-contract", passed, errors);
        Check(requirements.Contains("no-complete-claim-before-tests", StringComparison.Ordinal), "agent.no-premature-completion", passed, errors);
        return new(errors.Count == 0, passed, errors, DateTimeOffset.Now);
    }

    private static void Check(bool condition, string name, ICollection<string> passed, ICollection<string> errors)
    {
        if (condition) passed.Add(name); else errors.Add(name);
    }
}

public static class ProjectKindPolicy
{
    public static StudioBlueprint Apply(StudioBlueprint blueprint, StudioProjectKind kind)
    {
        if (kind != StudioProjectKind.Agent) return blueprint;
        var localOnly = blueprint.Capabilities
            .Where(capability => capability is "workspace.read" or "workspace.write")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return blueprint with
        {
            Capabilities = localOnly,
            SafetyNotes = blueprint.SafetyNotes
                .Append("これは実装前の設計スキャフォールドです。権限付与と自動実行は行いません。")
                .Distinct(StringComparer.Ordinal)
                .Take(5)
                .ToArray()
        };
    }
}
