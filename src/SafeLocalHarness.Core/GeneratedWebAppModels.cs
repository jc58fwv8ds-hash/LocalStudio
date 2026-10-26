namespace SafeLocalHarness.Core;

public sealed record GeneratedRequirement(
    [property: System.Text.Json.Serialization.JsonPropertyName("id")] string Id,
    [property: System.Text.Json.Serialization.JsonPropertyName("text")] string Text);

public sealed record GeneratedWebAppCandidate(
    [property: System.Text.Json.Serialization.JsonPropertyName("body")] string Body,
    [property: System.Text.Json.Serialization.JsonPropertyName("javascript")] string JavaScript,
    [property: System.Text.Json.Serialization.JsonPropertyName("css")] string? Css = null);

public sealed record GeneratedRequirementReview(
    [property: System.Text.Json.Serialization.JsonPropertyName("id")] string Id,
    [property: System.Text.Json.Serialization.JsonPropertyName("passed")] bool Passed,
    [property: System.Text.Json.Serialization.JsonPropertyName("ui_evidence")] string UiEvidence,
    [property: System.Text.Json.Serialization.JsonPropertyName("behavior_evidence")] string BehaviorEvidence);
