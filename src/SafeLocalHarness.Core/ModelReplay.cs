using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SafeLocalHarness.Core;

/// <summary>
/// One model exchange of a work tool: what LocalStudio sent (request, a hash of the tool-read input, the rejection
/// reasons fed back) and what the model returned. Recorded during real use and drills; replayed in Smoke without a model.
/// </summary>
public sealed record ModelExchange(
    [property: JsonPropertyName("tool")] string Tool,
    [property: JsonPropertyName("request")] string Request,
    [property: JsonPropertyName("inputSha256")] string InputSha256,
    [property: JsonPropertyName("problems")] IReadOnlyList<string>? Problems,
    [property: JsonPropertyName("response")] JsonElement Response,
    [property: JsonPropertyName("time")] DateTimeOffset? Time = null);

/// <summary>Thrown when a replay sees a different exchange than the recording: the harness changed what it sends.</summary>
public sealed class ReplayDivergedException(string message) : InvalidOperationException(message);

/// <summary>
/// Append-only JSONL log of model exchanges (idea taken from DeepSeek Harness's session log and its keyless replay
/// tests, 2026-10-11). Every confident mistake seen in real use can become a fixture that Smoke replays forever, with no
/// model and no memory load. Logs stay in %LOCALAPPDATA%; file contents are never stored, only a hash of the facts.
/// </summary>
public static class ModelExchangeLog
{
    /// <summary>A day's log stops growing past this size, and nothing is written once the folder holds this many bytes.</summary>
    public const long MaxFileBytes = 5L * 1024 * 1024;
    public const long MaxFolderBytes = 20L * 1024 * 1024;

    public static readonly JsonSerializerOptions Options = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly object Sync = new();

    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>Appends one exchange; a full folder or an unwritable file never fails the user's work.</summary>
    public static void Append(string path, ModelExchange exchange)
    {
        try
        {
            UserSpaceGuard.EnsureAllowed(path);
            var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
            lock (Sync)
            {
                Directory.CreateDirectory(folder);
                var file = new FileInfo(path);
                if (file.Exists && file.Length >= MaxFileBytes) return;
                if (new DirectoryInfo(folder).EnumerateFiles("*.jsonl").Sum(item => item.Length) >= MaxFolderBytes) return;
                File.AppendAllText(path, JsonSerializer.Serialize(exchange with { Time = exchange.Time ?? DateTimeOffset.Now }, Options) + "\n", new UTF8Encoding(false));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
    }

    /// <summary>The day file used by the app: state\model-exchanges\yyyy-MM-dd.jsonl.</summary>
    public static string DayFile(string stateRoot) => Path.Combine(stateRoot, "model-exchanges", DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + ".jsonl");

    /// <summary>Exchanges of a JSONL file; lines that are not exchanges (a fixture's header) are skipped.</summary>
    public static IReadOnlyList<ModelExchange> Read(string path) => Parse(File.ReadAllLines(path, Encoding.UTF8));

    public static IReadOnlyList<ModelExchange> Parse(IEnumerable<string> lines) => lines
        .Where(line => line.TrimStart().StartsWith("{\"tool\"", StringComparison.Ordinal))
        .Select(line => JsonSerializer.Deserialize<ModelExchange>(line, Options)!)
        .ToArray();
}

/// <summary>
/// Serves recorded responses in order and fails closed: a different request, a different input, different rejection
/// reasons fed back, or one exchange more than recorded all stop the replay (the log is the source of truth).
/// </summary>
public sealed class ExchangeReplay(IEnumerable<ModelExchange> exchanges)
{
    private readonly Queue<ModelExchange> _queue = new(exchanges);

    public int Served { get; private set; }

    public int Remaining => _queue.Count;

    public JsonElement Next(string tool, string request, string inputSha256, IReadOnlyList<string> problems)
    {
        if (!_queue.TryDequeue(out var recorded))
            throw new ReplayDivergedException($"記録より多くAIに問い合わせました（{tool}、{Served + 1}回目、理由: {string.Join(", ", problems)}）。");
        Served++;
        if (recorded.Tool != tool || recorded.Request != request)
            throw new ReplayDivergedException($"{Served}回目の依頼が記録と違います（{recorded.Tool}:{recorded.Request} → {tool}:{request}）。");
        if (!string.Equals(recorded.InputSha256, inputSha256, StringComparison.OrdinalIgnoreCase))
            throw new ReplayDivergedException($"{Served}回目にAIへ渡した事実が記録と違います（読み取り結果が変わりました。記録 {recorded.InputSha256} / 今回 {inputSha256}）。");
        if (recorded.Problems is { } expected && !expected.SequenceEqual(problems, StringComparer.Ordinal))
            throw new ReplayDivergedException($"{Served}回目に伝えた却下理由が記録と違います（記録: {string.Join(", ", expected)} / 今回: {string.Join(", ", problems)}）。");
        return recorded.Response;
    }
}

public sealed class RecordingOfficePlanner(IOfficePlanner inner, Func<string> logPath) : IOfficePlanner
{
    public async Task<OfficePlan> PlanAsync(string request, string facts, string kind, IReadOnlyList<string> problems, CancellationToken cancellationToken = default)
    {
        var plan = await inner.PlanAsync(request, facts, kind, problems, cancellationToken);
        ModelExchangeLog.Append(logPath(), new ModelExchange("office", request, ModelExchangeLog.Hash(facts), problems.ToArray(),
            JsonSerializer.SerializeToElement(plan, ModelExchangeLog.Options)));
        return plan;
    }
}

public sealed class ReplayOfficePlanner(ExchangeReplay replay) : IOfficePlanner
{
    public Task<OfficePlan> PlanAsync(string request, string facts, string kind, IReadOnlyList<string> problems, CancellationToken cancellationToken = default) =>
        Task.FromResult(replay.Next("office", request, ModelExchangeLog.Hash(facts), problems).Deserialize<OfficePlan>(ModelExchangeLog.Options)
                        ?? throw new InvalidDataException("回答を受け取れませんでした。"));
}

public sealed class RecordingRenamePlanner(IRenamePlanner inner, Func<string> logPath) : IRenamePlanner
{
    public async Task<RenamePlan> PlanAsync(string request, IReadOnlyList<string> sampleNames, IReadOnlyList<string> previousProblems, CancellationToken cancellationToken = default)
    {
        var plan = await inner.PlanAsync(request, sampleNames, previousProblems, cancellationToken);
        ModelExchangeLog.Append(logPath(), new ModelExchange("rename", request, ModelExchangeLog.Hash(string.Join("\n", sampleNames)), previousProblems.ToArray(),
            JsonSerializer.SerializeToElement(plan, ModelExchangeLog.Options)));
        return plan;
    }
}

public sealed class ReplayRenamePlanner(ExchangeReplay replay) : IRenamePlanner
{
    public Task<RenamePlan> PlanAsync(string request, IReadOnlyList<string> sampleNames, IReadOnlyList<string> previousProblems, CancellationToken cancellationToken = default) =>
        Task.FromResult(replay.Next("rename", request, ModelExchangeLog.Hash(string.Join("\n", sampleNames)), previousProblems).Deserialize<RenamePlan>(ModelExchangeLog.Options)
                        ?? throw new InvalidDataException("名前の付け方を受け取れませんでした。"));
}

public sealed class RecordingPdfPlanner(IPdfPlanner inner, Func<string> logPath) : IPdfPlanner
{
    public async Task<PdfPlan> PlanAsync(string request, string facts, int files, IReadOnlyList<string> problems, CancellationToken cancellationToken = default)
    {
        var plan = await inner.PlanAsync(request, facts, files, problems, cancellationToken);
        ModelExchangeLog.Append(logPath(), new ModelExchange("pdf", request, ModelExchangeLog.Hash(facts), problems.ToArray(),
            JsonSerializer.SerializeToElement(plan, ModelExchangeLog.Options)));
        return plan;
    }
}

public sealed class ReplayPdfPlanner(ExchangeReplay replay) : IPdfPlanner
{
    public Task<PdfPlan> PlanAsync(string request, string facts, int files, IReadOnlyList<string> problems, CancellationToken cancellationToken = default) =>
        Task.FromResult(replay.Next("pdf", request, ModelExchangeLog.Hash(facts), problems).Deserialize<PdfPlan>(ModelExchangeLog.Options)
                        ?? throw new InvalidDataException("回答を受け取れませんでした。"));
}