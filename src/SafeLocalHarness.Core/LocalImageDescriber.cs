using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

namespace SafeLocalHarness.Core;

public sealed record ImageDescription(string Text, string Model, double Seconds);

/// <summary>Thrown when no vision-capable model is installed; the conversation then asks for the image in words.</summary>
public sealed class VisionModelUnavailableException(string message) : InvalidOperationException(message);

public interface IImageDescriber
{
    Task<ImageDescription> DescribeAsync(IReadOnlyList<string> imagePaths, string userText, IProgress<ProjectCreationProgress>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Step one of the two-step image handling (design 3.6, 2026-10-07): a vision-capable local model turns screenshots
/// into a plain-text description, then the code model works from that text. The two models are never loaded
/// together: the code model is released first, and the vision model is asked to unload as soon as it has answered
/// (keep_alive 0), which keeps a 16 GB office PC within its memory budget.
/// </summary>
public sealed class LocalImageDescriber(ILocalModelRuntime runtime, HttpClient? http = null, RuntimeResourcePlan? resources = null) : IImageDescriber, IDisposable
{
    public const int MaxDescriptionCharacters = 1_200;
    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(6) };
    private readonly bool _ownsClient = http is null;

    public async Task<ImageDescription> DescribeAsync(IReadOnlyList<string> imagePaths, string userText, IProgress<ProjectCreationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (imagePaths.Count is 0 or > ChatAttachmentPolicy.MaxPerMessage) throw new ArgumentException("画像は1～4枚です。", nameof(imagePaths));
        if (resources?.Band == RuntimeResourceBand.DeterministicOnly)
            throw new VisionModelUnavailableException("今の空きメモリでは画像を読むAIを動かせません。");
        await runtime.EnsureServerAsync(cancellationToken);
        var installed = await runtime.GetInstalledModelsAsync(cancellationToken);
        var model = installed.FirstOrDefault(name => name.Equals(LocalStudioSettings.VisionModel, StringComparison.OrdinalIgnoreCase))
            ?? throw new VisionModelUnavailableException("画像を読むモデル（" + LocalStudioSettings.VisionModel + "）が準備されていません。");
        var images = new List<string>();
        foreach (var path in imagePaths)
        {
            if (ChatAttachmentPolicy.Check(path, 0) is { } reason) throw new InvalidDataException(reason);
            images.Add(Convert.ToBase64String(await File.ReadAllBytesAsync(path, cancellationToken)));
        }

        progress?.Report(new("vision", "画像を読むため、コード用のAIをいったんメモリから外しています", 0.1));
        await runtime.UnloadModelsAsync(cancellationToken);
        await runtime.EnsureModelFitsAsync(model, cancellationToken);
        progress?.Report(new("vision", $"画像を読んでいます（{images.Count}枚）", 0.3));
        var clock = Stopwatch.StartNew();
        var body = new
        {
            model,
            stream = false,
            keep_alive = 0,
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = """
                        You describe screenshots of apps for a developer who cannot see them. Answer in Japanese, as at most 15 short bullet lines starting with "・".
                        Describe only what is visible: the elements (headings, buttons, inputs, lists, tables) and their order, the text shown (button labels and error messages exactly as written), and anything overlapping, cut off or broken.
                        Do not suggest changes, do not guess colour codes or sizes, do not repeat these instructions.
                        """
                },
                new
                {
                    role = "user",
                    content = "この画面を説明してください。ユーザーが気にしていること（参考）: " + (userText.Length <= 300 ? userText : userText[..300]),
                    images
                }
            },
            options = new { temperature = 0.1, num_predict = 450, num_ctx = 4096 }
        };
        using var response = await _http.PostAsJsonAsync(new Uri(runtime.ApiBaseUri, "api/chat"), ModelRequestPolicy.Prepare(body), cancellationToken);
        response.EnsureSuccessStatusCode();
        using var envelope = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var text = Clean(envelope.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "");
        if (text.Length == 0) throw new InvalidDataException("画像の説明を受け取れませんでした。");
        if (text.Length > MaxDescriptionCharacters) text = text[..MaxDescriptionCharacters];
        return new(text, model, Math.Round(clock.Elapsed.TotalSeconds, 1));
    }

    private static readonly string[] EchoedRules = ["見出し・ボタン・入力欄", "画面に表示されている文字（", "色や大きさで目立つところ", "推測や改善案は書かない"];

    /// <summary>
    /// The 2B vision model sometimes ignores "describe only" (real runs, 2026-10-10): it echoed the rules, appended a
    /// section of suggestions and invented colour codes. Those parts are removed mechanically; what remains is what it
    /// read from the screen.
    /// </summary>
    public static string Clean(string raw)
    {
        var lines = new List<string>();
        foreach (var rawLine in raw.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Replace("**", "").Trim();
            // A suggestions / guesses section ends the useful part.
            if (System.Text.RegularExpressions.Regex.IsMatch(line, @"^(#+\s*)?[^・\-*\d]{0,12}(提案|改善|推測|おすすめ)", System.Text.RegularExpressions.RegexOptions.CultureInvariant)) break;
            if (line.Length == 0 || line.Trim('-', '=', '_', ' ').Length == 0) continue;
            if (EchoedRules.Any(rule => line.Contains(rule, StringComparison.Ordinal))) continue;
            if (line.StartsWith("ご依頼", StringComparison.Ordinal) || line.StartsWith("ご要望", StringComparison.Ordinal)) continue;
            line = System.Text.RegularExpressions.Regex.Replace(line, @"\s*[（(]?#[0-9A-Fa-f]{3,8}[）)]?", "", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            line = System.Text.RegularExpressions.Regex.Replace(line, @"^(#+\s*|[-*]\s+|\d+\.\s+)", "・", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            lines.Add(line);
        }
        return string.Join("\n", lines).Trim();
    }

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }
}
