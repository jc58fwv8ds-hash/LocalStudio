using System.Text.Json;
using System.Text.Json.Nodes;

namespace SafeLocalHarness.Core;

/// <summary>
/// Final shaping of every request body sent to the local model. Qwen3.5 can think before answering; on a small PC
/// that spends the output budget on hidden text and can leak into the JSON the harness parses, so thinking is
/// always turned off for models that support it. Models without thinking get no "think" field (Ollama rejects it).
/// </summary>
public static class ModelRequestPolicy
{
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    public static bool SupportsThinking(string? model) =>
        model is not null && model.StartsWith("qwen3.5:", StringComparison.OrdinalIgnoreCase);

    public static JsonNode Prepare(object body)
    {
        // Same settings PostAsJsonAsync / JsonContent use, so the wire format is unchanged for every other field.
        var node = body as JsonNode ?? JsonSerializer.SerializeToNode(body, WebOptions)
            ?? throw new InvalidOperationException("Empty model request.");
        if (node is JsonObject request && SupportsThinking(request["model"]?.GetValue<string>()))
        {
            request["think"] = false;
            // The published Qwen3.5 defaults carry presence_penalty 1.5 (chat tuning). Code and JSON repeat the same
            // tokens on purpose, so precise output uses 0 unless a caller chose a value.
            if (request["options"] is JsonObject options && !options.ContainsKey("presence_penalty"))
                options["presence_penalty"] = 0;
        }
        return node;
    }
}
