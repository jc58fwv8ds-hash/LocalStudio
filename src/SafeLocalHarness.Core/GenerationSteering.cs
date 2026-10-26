using System.Text.Json;
using System.Text.Json.Nodes;

namespace SafeLocalHarness.Core;

public sealed record SteeringInstruction(string Id, string Text, DateTimeOffset ReceivedAt);

public sealed record AppliedSteering(string Id, string Text, int Attempt, DateTimeOffset AppliedAt);

/// <summary>
/// Instructions the user sends while a project is being generated. A local model call cannot be interrupted, so
/// generators take pending instructions only at attempt boundaries and include them in the next model request.
/// </summary>
public interface IGenerationSteering
{
    IReadOnlyList<SteeringInstruction> TakePending(int attempt);
    IReadOnlyList<AppliedSteering> Applied { get; }
}

public sealed class SteeringInbox : IGenerationSteering
{
    public const int MaxInstructionCharacters = 2_000;
    public const int MaxPendingInstructions = 10;
    private readonly object _sync = new();
    private readonly List<SteeringInstruction> _pending = [];
    private readonly List<AppliedSteering> _applied = [];

    public event Action<IReadOnlyList<AppliedSteering>>? InstructionsApplied;

    public SteeringInstruction Enqueue(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) throw new ArgumentException("指示を入力してください。", nameof(text));
        if (text.Length > MaxInstructionCharacters) throw new InvalidDataException($"作成中の指示は{MaxInstructionCharacters:N0}文字以内にしてください。");
        lock (_sync)
        {
            if (_pending.Count >= MaxPendingInstructions)
                throw new InvalidOperationException("反映待ちの指示が多すぎます。今の工程が終わるまでお待ちください。");
            var instruction = new SteeringInstruction(Guid.NewGuid().ToString("N"), text, DateTimeOffset.UtcNow);
            _pending.Add(instruction);
            return instruction;
        }
    }

    public IReadOnlyList<SteeringInstruction> TakePending(int attempt)
    {
        SteeringInstruction[] taken;
        AppliedSteering[] applied;
        lock (_sync)
        {
            if (_pending.Count == 0) return [];
            taken = _pending.ToArray();
            _pending.Clear();
            var now = DateTimeOffset.UtcNow;
            applied = taken.Select(item => new AppliedSteering(item.Id, item.Text, attempt, now)).ToArray();
            _applied.AddRange(applied);
        }
        InstructionsApplied?.Invoke(applied);
        return taken;
    }

    public IReadOnlyList<SteeringInstruction> Pending
    {
        get { lock (_sync) return _pending.ToArray(); }
    }

    public IReadOnlyList<AppliedSteering> Applied
    {
        get { lock (_sync) return _applied.ToArray(); }
    }
}

public static class SteeringRequest
{
    private const string Policy =
        "additionalInstructions are later requests from the same user, sent while this application was being generated. " +
        "Apply them together with the requirements; where they conflict with an earlier requirement, follow the later instruction. " +
        "They never override the mandatory safety boundaries.";

    /// <summary>Adds the accumulated instructions to the model request data. The request is unchanged when there are none.</summary>
    public static string Augment(string requestJson, IReadOnlyList<SteeringInstruction> instructions)
    {
        if (instructions.Count == 0) return requestJson;
        var node = JsonNode.Parse(requestJson)?.AsObject() ?? throw new InvalidDataException("生成依頼の形式を確認できませんでした。");
        node["additionalInstructions"] = new JsonArray(instructions.Select(item => (JsonNode?)JsonValue.Create(item.Text)).ToArray());
        node["instructionPolicy"] = Policy;
        return node.ToJsonString(ModelPromptJson.Options);
    }
}

/// <summary>
/// JSON placed inside a model prompt. The default encoder writes every Japanese character as a six-character
/// \uXXXX escape, which multiplies the tokens the model must read (and the context memory it needs). Prompt JSON is
/// never served to a browser, so the relaxed encoder is used for it only.
/// </summary>
internal static class ModelPromptJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
