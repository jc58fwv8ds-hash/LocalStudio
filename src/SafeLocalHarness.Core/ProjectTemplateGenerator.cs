using System.Text.Json;

namespace SafeLocalHarness.Core;

public sealed record GeneratedTextFile(string RelativePath, string Content);

public interface IProjectTemplateGenerator
{
    IReadOnlyList<GeneratedTextFile> Generate(StudioBlueprint blueprint, StudioProjectKind kind);
}

/// <summary>
/// Only the agent design scaffold (a plan with no permissions, never run). No app is made from a fixed template: every app
/// is planned and generated from its request (user decision 2026-10-13; the old templates were moved out of the source).
/// </summary>
public sealed class ProjectTemplateGenerator : IProjectTemplateGenerator
{
    public IReadOnlyList<GeneratedTextFile> Generate(StudioBlueprint blueprint, StudioProjectKind kind)
    {
        var decision = ImplementationSupportPolicy.Evaluate(blueprint, kind);
        return decision.Route switch
        {
            // Fixed app templates are no longer offered (user decision 2026-10-13); only the agent design scaffold remains.
            ProjectImplementationRoute.DesignScaffold when kind == StudioProjectKind.Agent => BuildAgent(blueprint),
            ProjectImplementationRoute.GeneratedLocalWebApp => throw new InvalidOperationException("新規実装にはローカルAI生成器が必要です。"),
            _ => throw new InvalidDataException(decision.Explanation)
        };
    }

    private static IReadOnlyList<GeneratedTextFile> BuildAgent(StudioBlueprint blueprint)
    {
        var agent = new
        {
            schema = 1,
            artifactType = "design-scaffold",
            implementationStatus = "not-started",
            requestedPermissions = blueprint.Capabilities,
            grantedPermissions = Array.Empty<string>(),
            executionEnabled = false,
            externalActionsRequireConfirmation = true,
            maxSteps = 20
        };
        return
        [
            new("agent.json", JsonSerializer.Serialize(agent, new JsonSerializerOptions { WriteIndented = true }) + "\n"),
            new("PROMPT.md", "# Agent instructions\n\nこれは実装前の設計スキャフォールドです。権限は未付与で、自動実行されません。\n確認済みの目的と権限だけをここへ追加します。\n"),
            new("requirements.json", """
                {
                  "schema": 1,
                  "artifactType": "agent-design-contract",
                  "completionRequires": [
                    "working-implementation",
                    "independent-verification",
                    "permission-review",
                    "rollback-test"
                  ],
                  "invariants": [
                    "no-admin-rights",
                    "no-default-permissions",
                    "no-hidden-screen-capture",
                    "no-network-without-confirmation",
                    "no-complete-claim-before-tests"
                  ]
                }
                """ + "\n")
        ];
    }
}
