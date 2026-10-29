using System.Text.Json;

namespace SafeLocalHarness.Core;

public interface IOutputKindClassifier
{
    Task<OutputKindJudgement> ClassifyAsync(string request, string model, CancellationToken cancellationToken = default);
}

public sealed record OutputKindJudgement(StudioProjectKind? Kind, double Confidence, string Question)
{
    public static OutputKindJudgement Unknown { get; } = new(null, 0, "");

    /// <summary>Parses the classifier's JSON. Anything malformed becomes "unknown", which leads to asking the user.</summary>
    public static OutputKindJudgement Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var kind = root.TryGetProperty("kind", out var kindValue) && kindValue.ValueKind == JsonValueKind.String
                ? FromLabel(kindValue.GetString())
                : null;
            var confidence = root.TryGetProperty("confidence", out var confidenceValue) && confidenceValue.ValueKind == JsonValueKind.Number
                ? Math.Clamp(confidenceValue.GetDouble(), 0, 1)
                : 0;
            var question = root.TryGetProperty("question", out var questionValue) && questionValue.ValueKind == JsonValueKind.String
                ? (questionValue.GetString() ?? "").Trim()
                : "";
            if (question.Length > 180) question = question[..180];
            return new(kind, kind is null ? 0 : confidence, question);
        }
        catch (JsonException)
        {
            return Unknown;
        }
    }

    private static StudioProjectKind? FromLabel(string? label) => label?.Trim().ToLowerInvariant() switch
    {
        "web" => StudioProjectKind.DesktopOrWebApp,
        "windows-app" => StudioProjectKind.WindowsApp,
        "windows-game" => StudioProjectKind.WindowsGame,
        "gas" => StudioProjectKind.GoogleAppsScript,
        "agent" => StudioProjectKind.Agent,
        _ => null
    };
}

public sealed record OutputKindResolution(StudioProjectKind? Kind, string Message, IReadOnlyList<ConversationChoice> Choices)
{
    public bool NeedsAnswer => Kind is null;
}

/// <summary>
/// Decides the output type from keyword signals and the model's judgement. It never guesses silently: when the two
/// disagree or the model is unsure, it asks the user with explicit choices.
/// </summary>
public static class OutputKindResolver
{
    public const double DecideThreshold = 0.7;
    public const double OverrideThreshold = 0.8;

    public static IReadOnlyList<ConversationChoice> KindChoices { get; } =
    [
        new("kind:" + StudioProjectKind.DesktopOrWebApp, "Webアプリ（ブラウザー）"),
        new("kind:" + StudioProjectKind.WindowsApp, "Windowsアプリ（EXE）"),
        new("kind:" + StudioProjectKind.WindowsGame, "Windowsゲーム"),
        new("kind:" + StudioProjectKind.GoogleAppsScript, "Google Apps Script")
    ];

    public static OutputKindResolution Resolve(OutputTargetAdvice keywords, OutputKindJudgement? model)
    {
        if (keywords.RecommendedKind is { } signalled && keywords.IsConfident)
        {
            if (model?.Kind is { } other && other != signalled && model.Confidence >= OverrideThreshold)
                return Ask($"「{Label(signalled)}」と「{Label(other)}」のどちらとしても読み取れます。どちらで作りますか？");
            return new(signalled, $"{Label(signalled)}として作ります。{keywords.Reason}", []);
        }
        if (model?.Kind is { } judged && model.Confidence >= DecideThreshold)
            return new(judged, $"{Label(judged)}として作ります。", []);
        var question = string.IsNullOrWhiteSpace(model?.Question)
            ? "どのように使うアプリにしますか？（ブラウザーで開く、WindowsのEXEとして起動する、Googleスプレッドシート等の中で動かす、など）"
            : model!.Question;
        return Ask(question);
    }

    /// <summary>Reads an answer typed in chat. Returns null when it still does not say which type.</summary>
    public static StudioProjectKind? FromAnswer(string answer)
    {
        if (string.IsNullOrWhiteSpace(answer)) return null;
        var advice = OutputTargetAdvisor.Analyze(answer);
        if (advice.RecommendedKind is { } kind && advice.IsConfident) return kind;
        var text = answer.Trim();
        if (ContainsAny(text, "HTML", "Web", "ウェブ", "ブラウザ")) return StudioProjectKind.DesktopOrWebApp;
        if (ContainsAny(text, "exe", "Windows", "ウィンドウズ", "デスクトップ")) return StudioProjectKind.WindowsApp;
        if (ContainsAny(text, "Google", "グーグル", "シート")) return StudioProjectKind.GoogleAppsScript;
        return null;
    }

    public static string Label(StudioProjectKind kind) => kind switch
    {
        StudioProjectKind.DesktopOrWebApp => "Webアプリ（ブラウザー）",
        StudioProjectKind.WindowsApp => "Windowsアプリ（EXE）",
        StudioProjectKind.WindowsGame => "Windowsゲーム",
        StudioProjectKind.GoogleAppsScript => "Google Apps Script",
        StudioProjectKind.Agent => "エージェント機能（安全な設計のみ）",
        _ => kind.ToString()
    };

    private static OutputKindResolution Ask(string question) => new(null, question, KindChoices);

    private static bool ContainsAny(string value, params string[] signals) =>
        signals.Any(signal => value.Contains(signal, StringComparison.OrdinalIgnoreCase));
}

public enum ConversationStage
{
    Intake,
    Clarifying,
    Proposed,
    Running,
    Completed,
    Failed
}

/// <summary>
/// Drives one conversation: request → (question when the type is unclear) → proposal → creation → result.
/// While creating, new messages become steering instructions applied at the next generation attempt, or, for a type
/// change or on request, cancel the run and redo the proposal with everything the user said.
/// </summary>
public sealed class ConversationOrchestrator
{
    public const string StartChoice = "start";
    public const string ChangeKindChoice = "change-kind";
    public const string CancelAndRedoChoice = "cancel-redo";
    public const int MaxRequestCharacters = 8_000;

    private readonly IPlanningEngine _planner;
    private readonly IOutputKindClassifier _classifier;
    private readonly IProjectScaffolder _scaffolder;
    private readonly Func<string> _model;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private ConversationStage _stage = ConversationStage.Intake;
    private string _request = "";
    private SteeringInbox? _inbox;
    private CancellationTokenSource? _run;
    private bool _redoRequested;
    private StudioProjectKind? _pendingKindChange;
    private readonly IProjectImprover? _improver;
    private readonly IImageDescriber? _imageDescriber;

    /// <summary>Phase 4 work tools; null where they are not offered.</summary>
    public RenameAssistant? Rename { get; set; }

    /// <summary>4-2 Word / Excel; null where it is not offered.</summary>
    public OfficeAssistant? Office { get; set; }

    /// <summary>4-3 PDF; null where it is not offered.</summary>
    public PdfAssistant? Pdf { get; set; }
    private string? _changeRequest;
    private bool _improving;

    public ConversationOrchestrator(
        IPlanningEngine planner,
        IOutputKindClassifier classifier,
        IProjectScaffolder scaffolder,
        ConversationLog log,
        Func<string> model,
        IProjectImprover? improver = null,
        IImageDescriber? imageDescriber = null)
    {
        _imageDescriber = imageDescriber;
        _planner = planner;
        _classifier = classifier;
        _scaffolder = scaffolder;
        Log = log;
        _model = model;
        _improver = improver;
    }

    /// <summary>Continues the conversation of an existing project: every new message is a change request for its next version.</summary>
    public static async Task<ConversationOrchestrator> OpenProjectAsync(
        IPlanningEngine planner,
        IOutputKindClassifier classifier,
        IProjectScaffolder scaffolder,
        IProjectImprover improver,
        string projectRoot,
        Func<string> model,
        CancellationToken cancellationToken = default,
        IImageDescriber? imageDescriber = null)
    {
        projectRoot = Path.GetFullPath(projectRoot);
        var orchestrator = new ConversationOrchestrator(planner, classifier, scaffolder, ConversationLog.OpenForProject(projectRoot), model, improver, imageDescriber)
        {
            ProjectPath = projectRoot
        };
        using (var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(projectRoot, "localstudio.json"), cancellationToken)))
        {
            if (manifest.RootElement.TryGetProperty("projectKind", out var kind) && Enum.TryParse<StudioProjectKind>(kind.GetString(), out var parsed))
                orchestrator.Kind = parsed;
        }
        orchestrator._stage = ConversationStage.Completed;
        var versions = await ProjectVersionStore.LoadAsync(projectRoot, cancellationToken);
        await orchestrator.AddAsync(ConversationMessage.FromStudio(
            $"「{Path.GetFileName(projectRoot)}」を開きました（今の版: v{versions?.Current ?? 1}）。変えたいことをそのまま送ってください。"), cancellationToken);
        return orchestrator;
    }

    /// <summary>Where GAS hand-over zips are written (the user's Documents by default; a test root in drills).</summary>
    public string GasHandoverRoot { get; set; } = GasHandover.DefaultRoot;

    /// <summary>Builds large plans in parts (core first, then one feature at a time by the agent loop); off when null.</summary>
    public FeatureGrowth? Growth { get; init; }

    public event Action<ConversationMessage>? MessageAdded;
    public event Action<ProjectCreationProgress>? CreationProgress;
    public event Action<ConversationStage>? StageChanged;

    public ConversationLog Log { get; }
    public IReadOnlyList<ConversationMessage> Messages => Log.Messages;
    public StudioProjectKind? Kind { get; private set; }
    public StudioBlueprint? Blueprint { get; private set; }
    public string? ProjectPath { get; private set; }
    public string Request { get { lock (_sync) return _request; } }

    public ConversationStage Stage
    {
        get { lock (_sync) return _stage; }
        private set
        {
            lock (_sync)
            {
                if (_stage == value) return;
                _stage = value;
            }
            StageChanged?.Invoke(value);
        }
    }

    /// <summary>
    /// A user message, optionally with images already stored next to the record (ChatAttachmentPolicy). Images are kept
    /// with the message; reading them is the next step (3-5).
    /// </summary>
    public Task SubmitAsync(string text, CancellationToken cancellationToken = default) => SubmitAsync(text, null, cancellationToken);

    public async Task SubmitAsync(string text, IReadOnlyList<ConversationAttachment>? attachments, CancellationToken cancellationToken = default)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) throw new ArgumentException("メッセージを入力してください。", nameof(text));
        if (text.Length > MaxRequestCharacters) throw new InvalidDataException($"メッセージは{MaxRequestCharacters:N0}文字以内にしてください。");
        if (Stage == ConversationStage.Running)
        {
            await SteerAsync(text, cancellationToken);
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await AddAsync(ConversationMessage.FromUser(text, attachments), cancellationToken);
            if (attachments is { Count: > 0 }) text = await WithImageDescriptionAsync(text, attachments, cancellationToken);
            switch (Stage)
            {
                case ConversationStage.Clarifying:
                    if (OutputKindResolver.FromAnswer(text) is { } answered)
                    {
                        AppendToRequest("回答", text);
                        await DecideKindAndPlanAsync(answered, $"{OutputKindResolver.Label(answered)}として作ります。", cancellationToken);
                    }
                    else if (Kind is { } kept && Blueprint is not null)
                    {
                        // A design already proposed (the user pressed 形式を変える, then wrote what to change): the words are a
                        // change to the design, not an answer about the type (hands-on 2026-10-13: "クラシックにして" was asked
                        // the type again).
                        AppendToRequest("追加の指示", text);
                        var previous = Blueprint;
                        await DecideKindAndPlanAsync(kept, $"形式は{OutputKindResolver.Label(kept)}のままで、いただいた指示を設計案に反映します。", cancellationToken);
                        await NoticeIfUnchangedAsync(previous, cancellationToken);
                    }
                    else
                    {
                        AppendToRequest("回答", text);
                        await AddAsync(ConversationMessage.FromStudio(
                            "形式がまだ決められません。下から選ぶか、使い方をもう少し教えてください。",
                            OutputKindResolver.KindChoices.ToArray()), cancellationToken);
                    }
                    break;
                case ConversationStage.Proposed when _changeRequest is not null:
                    _changeRequest = Limit($"{_changeRequest}\n追加: {text}", WebProjectImprover.MaxChangeRequestCharacters);
                    await ProposeImprovementAsync(cancellationToken);
                    break;
                case ConversationStage.Completed when ProjectPath is not null && _improver is not null:
                    await HandleProjectMessageAsync(text, cancellationToken);
                    break;
                case ConversationStage.Proposed:
                    AppendToRequest("追加の指示", text);
                    var signalled = OutputTargetAdvisor.Analyze(text);
                    if (signalled.RecommendedKind is { } changed && signalled.IsConfident && changed != Kind)
                        await DecideKindAndPlanAsync(changed, $"形式を{OutputKindResolver.Label(changed)}に変えて設計し直します。", cancellationToken);
                    else
                    {
                        var previous = Blueprint;
                        await PlanAsync(cancellationToken);
                        await NoticeIfUnchangedAsync(previous, cancellationToken);
                    }
                    break;
                default:
                    StartNewRequest(text);
                    await ResolveKindAndPlanAsync(cancellationToken);
                    break;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// A design that did not change after an instruction says so, instead of showing the same plan as if it had been
    /// changed.
    /// </summary>
    private async Task NoticeIfUnchangedAsync(StudioBlueprint? previous, CancellationToken cancellationToken)
    {
        if (previous is null || Blueprint is null || previous.Name != Blueprint.Name || !previous.Features.SequenceEqual(Blueprint.Features)) return;
        await AddAsync(ConversationMessage.FromStudio(
            "いただいた指示は、設計案に反映できませんでした（設計案は前と同じです）。別の言い方で、変えたい機能や見た目を具体的に書いてみてください。",
            new ConversationChoice(StartChoice, "このまま作成開始"), new ConversationChoice(ChangeKindChoice, "形式を変える")), cancellationToken);
    }

    /// <summary>
    /// Handles a choice button. Starting creation returns only when the run ends; while it runs, other messages and
    /// the cancel-and-redo choice are still accepted.
    /// </summary>
    public async Task ChooseAsync(string choiceId, CancellationToken cancellationToken = default)
    {
        // Work tools run beside app building and never change its stage.
        if (choiceId.StartsWith(RenamePrefix, StringComparison.Ordinal))
        {
            await HandleRenameChoiceAsync(choiceId, cancellationToken);
            return;
        }
        if (choiceId.StartsWith(OfficePrefix, StringComparison.Ordinal))
        {
            await HandleOfficeChoiceAsync(choiceId, cancellationToken);
            return;
        }
        if (choiceId.StartsWith(PdfPrefix, StringComparison.Ordinal))
        {
            await HandlePdfChoiceAsync(choiceId, cancellationToken);
            return;
        }
        if (choiceId.StartsWith(WorkPrefix, StringComparison.Ordinal))
        {
            await HandleWorkChoiceAsync(choiceId, cancellationToken);
            return;
        }
        if (Stage == ConversationStage.Running)
        {
            if (choiceId == CancelAndRedoChoice) RequestRedo();
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (choiceId.StartsWith("kind:", StringComparison.Ordinal) &&
                Enum.TryParse<StudioProjectKind>(choiceId["kind:".Length..], out var chosen) &&
                Stage is ConversationStage.Clarifying or ConversationStage.Proposed)
            {
                await AddAsync(ConversationMessage.FromUser(OutputKindResolver.Label(chosen)), cancellationToken);
                await DecideKindAndPlanAsync(chosen, $"{OutputKindResolver.Label(chosen)}として作ります。", cancellationToken);
            }
            else if (choiceId == LocalOnlyChoice && Stage == ConversationStage.Proposed && Blueprint is not null && _changeRequest is null)
            {
                await BuildLocalOnlyAsync(cancellationToken);
            }
            else if (choiceId == ChangeKindChoice && Stage == ConversationStage.Proposed)
            {
                Stage = ConversationStage.Clarifying;
                await AddAsync(ConversationMessage.FromStudio("どの形式で作りますか？", OutputKindResolver.KindChoices.ToArray()), cancellationToken);
            }
            else if (choiceId == StartChoice && Stage == ConversationStage.Proposed && _changeRequest is not null)
            {
                await RunImprovementAsync(cancellationToken);
            }
            else if (choiceId == StartChoice && Stage == ConversationStage.Proposed && CanCreate)
            {
                await RunAsync(cancellationToken);
            }
            else if (choiceId.StartsWith(RestoreChoicePrefix, StringComparison.Ordinal) &&
                     int.TryParse(choiceId[RestoreChoicePrefix.Length..], out var restoreTo) &&
                     Stage == ConversationStage.Completed && ProjectPath is not null && _improver is not null)
            {
                await RunRestoreAsync(restoreTo, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool CanCreate => Blueprint is not null && Kind is { } kind && ImplementationSupportPolicy.Evaluate(Blueprint, kind).CanCreate;

    public const string RestoreChoicePrefix = "restore:";
    private static readonly System.Text.RegularExpressions.Regex RestoreRequest = new(
        @"^\s*[vV](?<number>\d{1,4})\s*(?:に|へ)?\s*(?:戻|もど)",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private async Task HandleProjectMessageAsync(string text, CancellationToken cancellationToken)
    {
        var restore = RestoreRequest.Match(text);
        if (restore.Success)
        {
            var number = int.Parse(restore.Groups["number"].Value);
            var versions = await ProjectVersionStore.LoadAsync(ProjectPath!, cancellationToken);
            if (versions is null || versions.Versions.All(version => version.Number != number) || versions.Current == number)
            {
                await AddAsync(ConversationMessage.FromStudio(versions is null
                    ? "まだ版の記録がありません。最初の改善を保存すると v1 と v2 ができます。"
                    : versions.Current == number ? $"v{number} は今の版です。" : $"v{number} は見つかりません（今の版: v{versions.Current}）。"), cancellationToken);
                return;
            }
            await AddAsync(ConversationMessage.FromStudio(
                $"v{number} の内容を、新しい版 v{versions.Versions.Max(version => version.Number) + 1} として復元します。今の版も履歴に残ります。",
                new ConversationChoice(RestoreChoicePrefix + number, $"v{number} に戻す")), cancellationToken);
            return;
        }

        if (await _improver!.CheckImprovableAsync(ProjectPath!, cancellationToken) is { } reason)
        {
            await AddAsync(ConversationMessage.FromStudio(reason), cancellationToken);
            return;
        }
        _changeRequest = Limit(text, WebProjectImprover.MaxChangeRequestCharacters);
        await ProposeImprovementAsync(cancellationToken);
    }

    private async Task ProposeImprovementAsync(CancellationToken cancellationToken)
    {
        var versions = await ProjectVersionStore.LoadAsync(ProjectPath!, cancellationToken);
        var current = versions?.Current ?? 1;
        var next = (versions?.Versions.Max(version => version.Number) ?? 1) + 1;
        Stage = ConversationStage.Proposed;
        await AddAsync(ConversationMessage.FromStudio(
            $"この内容で v{next} を作ります。今の版（v{current}）はそのまま残ります。\n変更内容: {_changeRequest}\n付け足したいことがあれば、そのまま送ってください。",
            new ConversationChoice(StartChoice, "作成開始")), cancellationToken);
    }

    private async Task RunImprovementAsync(CancellationToken cancellationToken)
    {
        var (inbox, allNotices) = CreateInbox();
        using var run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_sync)
        {
            _inbox = inbox;
            _run = run;
            _redoRequested = false;
            _improving = true;
        }
        Stage = ConversationStage.Running;
        await AddAsync(ConversationMessage.FromStudio(
            "今のソースをもとに新しい版を作り始めました。途中で気づいたことはそのまま送ってください。次の工程で反映します。",
            new ConversationChoice(CancelAndRedoChoice, "今すぐ中止して作り直す")), cancellationToken);
        var progress = new Progress<ProjectCreationProgress>(item => CreationProgress?.Invoke(item));
        try
        {
            var result = await _improver!.ImproveAsync(ProjectPath!, _changeRequest!, progress, inbox, run.Token);
            await allNotices();
            _changeRequest = null;
            Stage = ConversationStage.Completed;
            var version = result.Version;
            var message = $"v{version.Number} を保存しました（変更したファイル {version.FilesChanged} 件、追加 {version.LinesAdded} 行・削除 {version.LinesRemoved} 行）。" +
                          $"\n前の版に戻したいときは「v{result.Previous.Number} に戻す」と送ってください。";
            if (Kind == StudioProjectKind.GoogleAppsScript)
                message += await HandOverGasAsync(ProjectPath!, version.Number, result.Previous.Number, cancellationToken);
            if (inbox.Pending.Count > 0)
                message += "\n生成の最後の工程より後に届いたため、次の指示はまだ反映していません。改めて送ってください: " +
                           string.Join(" / ", inbox.Pending.Select(item => item.Text));
            await AddAsync(ConversationMessage.FromStudio(message), cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await allNotices();
            bool redo;
            lock (_sync) redo = _redoRequested;
            Stage = ConversationStage.Proposed;
            if (!redo)
            {
                await AddAsync(ConversationMessage.FromStudio("改善を中止しました。今の版は変わっていません。", new ConversationChoice(StartChoice, "作成開始")), cancellationToken);
                return;
            }
            foreach (var instruction in inbox.Applied.Select(item => item.Text).Concat(inbox.Pending.Select(item => item.Text)))
                _changeRequest = Limit($"{_changeRequest}\n追加: {instruction}", WebProjectImprover.MaxChangeRequestCharacters);
            await AddAsync(ConversationMessage.FromStudio("改善を中止しました。今の版は変わっていません。いただいた指示を含めて提案し直します。"), cancellationToken);
            await ProposeImprovementAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await allNotices();
            Stage = ConversationStage.Proposed;
            await AddAsync(ConversationMessage.FromStudio(
                "新しい版を完成できませんでした。今の版は変わっていません。\n" + ex.Message,
                new ConversationChoice(StartChoice, "もう一度試す")), cancellationToken);
        }
        finally
        {
            lock (_sync)
            {
                _inbox = null;
                _run = null;
                _improving = false;
            }
            if (Stage == ConversationStage.Running) Stage = ConversationStage.Proposed;
        }
    }

    private async Task RunRestoreAsync(int number, CancellationToken cancellationToken)
    {
        await AddAsync(ConversationMessage.FromUser($"v{number} に戻す"), cancellationToken);
        try
        {
            var result = await _improver!.RestoreAsync(ProjectPath!, number, cancellationToken);
            await AddAsync(ConversationMessage.FromStudio(
                $"v{number} の内容を v{result.Version.Number} として復元しました（検証済み）。v{result.Previous.Number} も履歴に残っています。"), cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException)
        {
            await AddAsync(ConversationMessage.FromStudio("復元できませんでした。今の版は変わっていません。\n" + ex.Message), cancellationToken);
        }
    }

    private (SteeringInbox Inbox, Func<Task> AllNotices) CreateInbox()
    {
        var inbox = new SteeringInbox();
        // The generator reports applied instructions from inside its own call; those notices are written without
        // blocking it and awaited before the run's final message so the record stays in order.
        var notices = new List<Task>();
        inbox.InstructionsApplied += applied =>
        {
            var notice = AddAsync(ConversationMessage.FromStudio(
                $"いただいた指示を反映して生成しています（{applied[0].Attempt}回目）: " + string.Join(" / ", applied.Select(item => item.Text))), CancellationToken.None);
            lock (notices) notices.Add(notice);
        };
        return (inbox, () => { lock (notices) return Task.WhenAll(notices.ToArray()); });
    }

    private static string Limit(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];

    private bool _localOnlyChosen;

    private void StartNewRequest(string text)
    {
        lock (_sync) _request = text;
        _localOnlyChosen = false;
        Kind = null;
        Blueprint = null;
        ProjectPath = null;
        _pendingKindChange = null;
    }

    private void AppendToRequest(string label, string text)
    {
        lock (_sync)
        {
            var next = $"{_request}\n\n{label}: {text}";
            _request = next.Length <= MaxRequestCharacters ? next : next[^MaxRequestCharacters..];
        }
    }

    private async Task ResolveKindAndPlanAsync(CancellationToken cancellationToken)
    {
        var keywords = OutputTargetAdvisor.Analyze(Request);
        OutputKindJudgement? judgement = null;
        if (!keywords.IsConfident)
        {
            try { judgement = await _classifier.ClassifyAsync(Request, _model(), cancellationToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await FailAsync("ローカルAIに接続できないため、形式を判断できませんでした。先に「セットアップ」を完了してください。", cancellationToken);
                return;
            }
        }
        var resolution = OutputKindResolver.Resolve(keywords, judgement);
        if (resolution.NeedsAnswer)
        {
            Stage = ConversationStage.Clarifying;
            await AddAsync(ConversationMessage.FromStudio(resolution.Message, resolution.Choices.ToArray()), cancellationToken);
            return;
        }
        await DecideKindAndPlanAsync(resolution.Kind!.Value, resolution.Message, cancellationToken);
    }

    private async Task DecideKindAndPlanAsync(StudioProjectKind kind, string announcement, CancellationToken cancellationToken)
    {
        Kind = kind;
        await AddAsync(ConversationMessage.FromStudio(announcement), cancellationToken);
        await PlanAsync(cancellationToken);
    }

    private async Task PlanAsync(CancellationToken cancellationToken)
    {
        try
        {
            Blueprint = BlueprintPermissionPolicy.Derive(
                await _planner.PlanAsync(Request, Kind!.Value, _model(), null, cancellationToken), Kind.Value, Request);
            if (_localOnlyChosen) Blueprint = WithoutExternalAccess(Blueprint);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Blueprint = null;
            await FailAsync(ex is HttpRequestException
                ? "ローカルAIに接続できません。先に「セットアップ」を完了してください。"
                : "設計案を作れませんでした: " + ex.Message, cancellationToken);
            return;
        }

        await ShowProposalAsync(cancellationToken);
    }

    private static readonly IReadOnlyDictionary<string, string> CapabilityLabels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["workspace.read"] = "このPC内のファイルの読み取り",
        ["workspace.write"] = "このPC内への保存",
        ["network.fetch"] = "インターネット通信",
        ["account.authorize"] = "アカウント連携",
        ["message.send"] = "メッセージの送信",
        ["external.publish"] = "外部への公開"
    };

    private async Task ShowProposalAsync(CancellationToken cancellationToken)
    {
        Stage = ConversationStage.Proposed;
        var decision = ImplementationSupportPolicy.Evaluate(Blueprint!, Kind!.Value);
        var lines = new List<string>
        {
            $"設計案: {Blueprint!.Name}",
            Blueprint.Summary,
            "",
            "含める機能:"
        };
        lines.AddRange(Blueprint.Features.Select(feature => "・" + feature));
        // Saving the project on this PC is what every app does; only access beyond it is worth the user's attention.
        var beyondThisPc = Blueprint.Capabilities.Where(capability => capability is not ("workspace.read" or "workspace.write")).ToArray();
        if (beyondThisPc.Length > 0)
            lines.Add("必要な権限: " + string.Join("、", beyondThisPc.Select(capability => CapabilityLabels.GetValueOrDefault(capability, capability))));
        if (Blueprint.Questions.Count > 0)
        {
            lines.Add("");
            lines.Add("確認したい点（チャットで答えると設計案に反映します）:");
            lines.AddRange(Blueprint.Questions.Select(question => "・" + question));
        }
        lines.Add("");
        // The planning model sometimes asks for external access a local app does not need. Creation stays blocked
        // until the user explicitly chooses to build without it; it is never removed silently.
        var blockedOnlyByExternalAccess = !decision.CanCreate && ImplementationSupportPolicy.RequiresExternalAccess(Blueprint) &&
                                          ImplementationSupportPolicy.Evaluate(WithoutExternalAccess(Blueprint), Kind.Value).CanCreate;
        lines.Add(decision.CanCreate
            ? "よければ「作成開始」を押してください。変えたい点はそのまま入力できます。"
            : blockedOnlyByExternalAccess
                ? "この設計案には外部とのやり取りが含まれているため、このままでは作成できません。外部通信が不要なら「外部通信なしで作る」を選んでください。"
                : decision.Explanation);
        var choices = decision.CanCreate
            ? new[] { new ConversationChoice(StartChoice, "作成開始"), new ConversationChoice(ChangeKindChoice, "形式を変える") }
            : blockedOnlyByExternalAccess
                ? new[] { new ConversationChoice(LocalOnlyChoice, "外部通信なしで作る"), new ConversationChoice(ChangeKindChoice, "形式を変える") }
                : new[] { new ConversationChoice(ChangeKindChoice, "形式を変える") };
        await AddAsync(ConversationMessage.FromStudio(string.Join("\n", lines), choices), cancellationToken);
    }

    public const string LocalOnlyChoice = "local-only";

    public const string OfficePrefix = "office-";
    public const string OfficeApplyPrefix = "office-apply:";
    public const string OfficeCancelPrefix = "office-cancel:";

    /// <summary>
    /// 4-2: a request sent with a Word, Excel or PowerPoint file. Questions are answered from what the tools read. Edits are made on
    /// the chat's working copy, read back, and kept there; the user saves the result next to the original as
    /// "name_edited.ext" with a button (hands-on 2026-10-13). The original is only read.
    /// </summary>
    public async Task SubmitDocumentAsync(string text, string path, CancellationToken cancellationToken = default)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) throw new ArgumentException("何をしたいかを入力してください。", nameof(text));
        await AddAsync(ConversationMessage.FromUser($"{text}\n（ファイル: {Path.GetFileName(path)}）"), cancellationToken);
        if (Office is null)
        {
            await AddAsync(ConversationMessage.FromStudio("この環境ではWord・Excel・PowerPointの操作を使えません。"), cancellationToken);
            return;
        }
        try
        {
            CreationProgress?.Invoke(new("work", "ファイルを読んで考えています", 0.3));
            var set = Work.Prepare([path]);
            var reply = await Office.HandleAsync(text, set.Current[0], cancellationToken);
            if (reply.Preview is null)
            {
                await AddAsync(ConversationMessage.FromStudio(reply.Text + WorkingNote(set)), cancellationToken);
                return;
            }
            if (!reply.Preview.CanApply)
            {
                Office.Discard(reply.Preview.Id);
                await AddAsync(ConversationMessage.FromStudio(WithoutButtonLine(reply.Text)), cancellationToken);
                return;
            }
            CreationProgress?.Invoke(new("work", "作業用のコピーを変更して確かめています", 0.6));
            var result = Office.Apply(reply.Preview.Id);
            await ReportWorkAsync(set, ChangeLines(reply.Text), [result.OutputPath], result.Mismatches, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException or HttpRequestException or DocumentFormat.OpenXml.Packaging.OpenXmlPackageException)
        {
            await AddAsync(ConversationMessage.FromStudio("ファイルを扱えませんでした: " + ex.Message), cancellationToken);
        }
    }
    private async Task HandleOfficeChoiceAsync(string choiceId, CancellationToken cancellationToken)
    {
        if (Office is null) return;
        try
        {
            if (choiceId.StartsWith(OfficeApplyPrefix, StringComparison.Ordinal))
            {
                var result = Office.Apply(choiceId[OfficeApplyPrefix.Length..]);
                await AddAsync(ConversationMessage.FromStudio(result.Succeeded
                    ? $"新しいファイルを作り、読み直して変更を確認しました（元のファイルはそのままです）:\n{result.OutputPath}"
                    : $"新しいファイルを作りましたが、確認できなかった点があります（元のファイルはそのままです）:\n{result.OutputPath}\n" + string.Join("\n", result.Mismatches.Select(item => "・" + item))), cancellationToken);
            }
            else if (choiceId.StartsWith(OfficeCancelPrefix, StringComparison.Ordinal))
            {
                Office.Discard(choiceId[OfficeCancelPrefix.Length..]);
                await AddAsync(ConversationMessage.FromStudio("やめました。ファイルは作っていません。"), cancellationToken);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException or DocumentFormat.OpenXml.Packaging.OpenXmlPackageException)
        {
            await AddAsync(ConversationMessage.FromStudio("新しいファイルを作れませんでした（元のファイルはそのままです）: " + ex.Message), cancellationToken);
        }
    }

    public const string PdfPrefix = "pdf-";
    public const string PdfApplyPrefix = "pdf-apply:";
    public const string PdfCancelPrefix = "pdf-cancel:";

    /// <summary>
    /// 4-3: a request sent with one or more PDFs. Page counts and page text come from the tools. Edits are made on the
    /// chat's working copy (each request builds on the previous result), read back page by page, and saved next to the
    /// originals as "name_edited.pdf" only when the user presses the button. Originals are only read.
    /// </summary>
    public async Task SubmitPdfAsync(string text, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) throw new ArgumentException("何をしたいかを入力してください。", nameof(text));
        // A password is used for the file but never kept in the chat record (or its exports).
        var recorded = System.Text.RegularExpressions.Regex.IsMatch(text, "パスワード") ? System.Text.RegularExpressions.Regex.Replace(text, "「[^」]*」", "「●●●●」") : text;
        await AddAsync(ConversationMessage.FromUser($"{recorded}\n（PDF: {string.Join("、", paths.Select(Path.GetFileName))}）"), cancellationToken);
        if (Pdf is null)
        {
            await AddAsync(ConversationMessage.FromStudio("この環境ではPDFの操作を使えません。"), cancellationToken);
            return;
        }
        try
        {
            CreationProgress?.Invoke(new("work", "PDFを読んで考えています", 0.3));
            var set = Work.Prepare(paths);
            var working = set.Current.Where(file => file.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (working.Length == 0)
            {
                await AddAsync(ConversationMessage.FromStudio("作業用のコピーはPDFではなくなっています（画像にした後など）。元のPDFを選び直してください。"), cancellationToken);
                return;
            }
            // "もう少し上に" / "ハイフンを長く": the last drawing is redone, adjusted, on the version before it.
            if (set.Edited && PdfTextLook.IsAdjustment(text) && Work.PlanOf(set.Key, set.Version) is { } planJson &&
                System.Text.Json.JsonSerializer.Deserialize<PdfOp[]>(planJson) is { } lastOps && PdfTextLook.Adjust(text, lastOps) is { } adjusted)
            {
                var previous = Work.Undo(set.Key)!;
                PdfEditPreview adjustedPreview;
                PdfEditResult? adjustedResult;
                try { (adjustedPreview, adjustedResult) = await Pdf.ApplyOpsAsync(previous.Current.Where(file => file.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)).ToArray(), adjusted, cancellationToken); }
                catch { Work.SetVersion(set.Key, set.Version); throw; }
                if (adjustedResult is null)
                {
                    Work.SetVersion(set.Key, set.Version);
                    await AddAsync(ConversationMessage.FromStudio("その調整はできませんでした（" + string.Join("、", adjustedPreview.Problems) + "）。作業用のコピーは調整前のままです。"), cancellationToken);
                    return;
                }
                WorkingSet? adjustedSet;
                try { adjustedSet = await ReportWorkAsync(previous, "前の変更を調整しました:\n" + string.Join("\n", adjustedPreview.Lines.Select(line => "・" + line)), adjustedResult.OutputPaths, adjustedResult.Mismatches, cancellationToken); }
                catch { Work.SetVersion(set.Key, set.Version); throw; }
                if (adjustedSet is null) Work.SetVersion(set.Key, set.Version);
                else Work.RecordPlan(adjustedSet.Key, adjustedSet.Version, System.Text.Json.JsonSerializer.Serialize(adjusted));
                return;
            }
            var reply = await Pdf.HandleAsync(text, working, cancellationToken);
            if (reply.Preview is null)
            {
                await AddAsync(ConversationMessage.FromStudio(reply.Text + WorkingNote(set)), cancellationToken);
                return;
            }
            if (!reply.Preview.CanApply)
            {
                Pdf.Discard(reply.Preview.Id);
                await AddAsync(ConversationMessage.FromStudio(WithoutButtonLine(reply.Text)), cancellationToken);
                return;
            }
            CreationProgress?.Invoke(new("work", "作業用のコピーを変更して、全ページを読み直しています", 0.6));
            var plan = Pdf.OpsOf(reply.Preview.Id);
            var result = await Pdf.ApplyAsync(reply.Preview.Id, cancellationToken);
            if (await ReportWorkAsync(set, ChangeLines(reply.Text), result.OutputPaths, result.Mismatches, cancellationToken) is { } next && plan is not null)
                Work.RecordPlan(next.Key, next.Version, System.Text.Json.JsonSerializer.Serialize(plan));
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException or HttpRequestException)
        {
            await AddAsync(ConversationMessage.FromStudio("PDFを扱えませんでした: " + ex.Message), cancellationToken);
        }
    }

    /// <summary>
    /// Phase C: images chosen with 📄 become one PDF on the working copy ("PDFにまとめて"); saved with 「保存する」.
    /// </summary>
    public async Task SubmitImagesAsync(string text, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) throw new ArgumentException("何をしたいかを入力してください。", nameof(text));
        // Once made into a PDF, further requests are about that PDF (page numbers, stamps, …) on the same working copy.
        if (Work.Find(WorkingCopies.KeyOf(paths)) is { Edited: true } made && made.Current.All(file => file.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)))
        {
            await SubmitPdfAsync(text, paths, cancellationToken);
            return;
        }
        await AddAsync(ConversationMessage.FromUser($"{text}\n（画像: {string.Join("、", paths.Take(5).Select(Path.GetFileName))}{(paths.Count > 5 ? $" ほか{paths.Count - 5}枚" : "")}）"), cancellationToken);
        if (!System.Text.RegularExpressions.Regex.IsMatch(text, "(?i)PDF|まとめ|結合|1つに|一つに|ひとつに"))
        {
            await AddAsync(ConversationMessage.FromStudio("選んだ画像は、例えば『PDFにまとめて』のように入力すると、1枚1ページのPDFにできます（画像の内容について聞くときは、📎で添付してください）。"), cancellationToken);
            return;
        }
        try
        {
            var set = Work.Prepare(paths);
            var folder = Path.GetDirectoryName(set.Current[0])!;
            var output = Path.Combine(folder, Path.GetFileNameWithoutExtension(paths[0]) + "_LS結合.pdf");
            CreationProgress?.Invoke(new("work", "画像をPDFにまとめて確かめています", 0.5));
            var ordered = set.Current.OrderBy(file => paths.Select(Path.GetFileName).ToList().IndexOf(Path.GetFileName(file))).ToArray();
            var problems = PdfExport.FromImages(ordered, output);
            await ReportWorkAsync(set, $"・画像 {ordered.Length} 枚を、選んだ順に1枚1ページのPDFにまとめる（A4、画像の向きに合わせる）", [output], problems, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            await AddAsync(ConversationMessage.FromStudio("画像をPDFにできませんでした: " + ex.Message), cancellationToken);
        }
    }

    public const string WorkPrefix = "work-";
    public const string WorkSavePrefix = "work-save:";
    public const string WorkOpenPrefix = "work-open:";
    public const string WorkUndoPrefix = "work-undo:";
    private WorkingCopies? _work;

    /// <summary>The working copies of the files chosen in this chat (kept with the chat record).</summary>
    public WorkingCopies Work => _work ??= new WorkingCopies(Path.Combine(Log.Folder, "work"));

    /// <summary>The files of the latest version of a working set (for "開いて確かめる").</summary>
    public IReadOnlyList<string> WorkingFiles(string key) => Work.Find(key)?.Current ?? [];

    private static string WorkingNote(WorkingSet set) =>
        set.Edited ? $"\n\n（作業用のコピーの {set.Version} 回目の変更の後の内容から答えています）" : "";

    /// <summary>The change lines of a preview message ("・…"), without its old button instruction.</summary>
    private static string ChangeLines(string previewText) =>
        string.Join("\n", previewText.Split('\n').Select(line => line.TrimEnd()).Where(line => line.StartsWith('・')));

    private static string WithoutButtonLine(string text) =>
        string.Join("\n", text.Split('\n').Where(line => !line.Contains("を押してください", StringComparison.Ordinal))).TrimEnd();

    /// <summary>
    /// A read-back that passed makes the result the next version and offers to save it; one that did not is set aside
    /// and the working copy stays as it was (no false success).
    /// </summary>
    private async Task<WorkingSet?> ReportWorkAsync(WorkingSet set, string changes, IReadOnlyList<string> outputs, IReadOnlyList<string> mismatches, CancellationToken cancellationToken)
    {
        if (mismatches.Count > 0 || outputs.Count == 0)
        {
            foreach (var output in outputs.Where(File.Exists)) File.Move(output, output + ".failed", overwrite: true);
            await AddAsync(ConversationMessage.FromStudio(
                "変更を読み直したところ、予定どおりになっていない点があったため、作業用のコピーには反映していません（元のファイルもそのままです）:\n" +
                string.Join("\n", mismatches.Take(10).Select(item => "・" + item))), cancellationToken);
            return null;
        }
        var next = Work.Commit(set.Key, outputs);
        var names = string.Join("、", next.Current.Take(5).Select(file => Path.GetFileName(WorkingCopies.EditedPath(Path.GetDirectoryName(next.Originals[0])!, Path.GetFileName(file))))) +
                    (next.Current.Count > 5 ? $" ほか{next.Current.Count - 5}個" : "");
        var text = $"作業用のコピーを変更し、読み直して予定どおりになっていることを確かめました（{next.Version}回目の変更。元のファイルはそのままです）。\n" +
                   (changes.Length > 0 ? changes + "\n" : "") +
                   $"\n「開いて確かめる」で中身を見られます。よければ「保存する」で、元のファイルと同じフォルダーに「{names}」として保存します。\n続けて指示すると、この変更の上に重ねて変更します。";
        await AddAsync(ConversationMessage.FromStudio(text,
            new ConversationChoice(WorkSavePrefix + next.Key, "保存する"),
            new ConversationChoice(WorkOpenPrefix + next.Key, "開いて確かめる"),
            new ConversationChoice(WorkUndoPrefix + next.Key, "1つ前に戻す")), cancellationToken);
        return next;
    }

    private async Task HandleWorkChoiceAsync(string choiceId, CancellationToken cancellationToken)
    {
        try
        {
            if (choiceId.StartsWith(WorkSavePrefix, StringComparison.Ordinal))
            {
                var saved = Work.Save(choiceId[WorkSavePrefix.Length..]);
                await AddAsync(ConversationMessage.FromStudio("保存しました（元のファイルはそのままです）:\n" + string.Join("\n", saved)), cancellationToken);
            }
            else if (choiceId.StartsWith(WorkUndoPrefix, StringComparison.Ordinal))
            {
                var key = choiceId[WorkUndoPrefix.Length..];
                var set = Work.Undo(key) ?? throw new InvalidOperationException("作業用のコピーが見つかりません。");
                await AddAsync(set.Edited
                    ? ConversationMessage.FromStudio($"1つ前（{set.Version}回目の変更の後）に戻しました。",
                        new ConversationChoice(WorkSavePrefix + key, "保存する"), new ConversationChoice(WorkOpenPrefix + key, "開いて確かめる"), new ConversationChoice(WorkUndoPrefix + key, "1つ前に戻す"))
                    : ConversationMessage.FromStudio("変更前の状態に戻しました。"), cancellationToken);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            await AddAsync(ConversationMessage.FromStudio("できませんでした: " + ex.Message), cancellationToken);
        }
    }
    private async Task HandlePdfChoiceAsync(string choiceId, CancellationToken cancellationToken)
    {
        if (Pdf is null) return;
        try
        {
            if (choiceId.StartsWith(PdfApplyPrefix, StringComparison.Ordinal))
            {
                CreationProgress?.Invoke(new("work", "新しいPDFを作って確かめています", 0.5));
                var result = await Pdf.ApplyAsync(choiceId[PdfApplyPrefix.Length..], cancellationToken);
                var files = string.Join("\n", result.OutputPaths.Take(10)) + (result.OutputPaths.Count > 10 ? $"\n…ほか {result.OutputPaths.Count - 10}個" : "");
                await AddAsync(ConversationMessage.FromStudio(result.Succeeded
                    ? (result.OutputPaths.All(path => path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                        ? $"ページを画像にして保存し、すべての画像を確認しました（元のファイルはそのままです）:\n{files}"
                        : $"新しいPDFを作り、全ページが予定どおりのページ・向き・書き込みになっていることを読み直して確認しました（元のファイルはそのままです）:\n{files}")
                    : $"新しいPDFを作りましたが、確認できなかった点があります（元のファイルはそのままです）:\n{files}\n" + string.Join("\n", result.Mismatches.Take(10).Select(item => "・" + item))), cancellationToken);
            }
            else if (choiceId.StartsWith(PdfCancelPrefix, StringComparison.Ordinal))
            {
                Pdf.Discard(choiceId[PdfCancelPrefix.Length..]);
                await AddAsync(ConversationMessage.FromStudio("やめました。ファイルは作っていません。"), cancellationToken);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            await AddAsync(ConversationMessage.FromStudio("新しいPDFを作れませんでした（元のファイルはそのままです）: " + ex.Message), cancellationToken);
        }
    }

    public const string RenamePrefix = "rename-";
    public const string RenameApplyPrefix = "rename-apply:";
    public const string RenameCancelPrefix = "rename-cancel:";
    public const string RenameUndoPrefix = "rename-undo:";

    /// <summary>
    /// 4-1: a request sent with a folder. The AI proposes a plan, LocalStudio shows the full before → after preview,
    /// and nothing changes until the user presses the button.
    /// </summary>
    public async Task SubmitWorkAsync(string text, string folder, CancellationToken cancellationToken = default)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) throw new ArgumentException("どう変えたいかを入力してください。", nameof(text));
        await AddAsync(ConversationMessage.FromUser($"{text}\n（フォルダー: {folder}）"), cancellationToken);
        if (Rename is null)
        {
            await AddAsync(ConversationMessage.FromStudio("この環境ではファイルの操作を使えません。"), cancellationToken);
            return;
        }
        try
        {
            CreationProgress?.Invoke(new("work", "名前の付け方を考えています", 0.3));
            var preview = await Rename.PreviewAsync(text, folder, cancellationToken);
            var choices = preview.CanApply
                ? new[] { new ConversationChoice(RenameApplyPrefix + preview.Id, "名前を変更する"), new ConversationChoice(RenameCancelPrefix + preview.Id, "やめる") }
                : new[] { new ConversationChoice(RenameCancelPrefix + preview.Id, "やめる") };
            await AddAsync(ConversationMessage.FromStudio(RenameAssistant.Describe(preview), choices), cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException or HttpRequestException)
        {
            await AddAsync(ConversationMessage.FromStudio("ファイル名の変更を準備できませんでした: " + ex.Message), cancellationToken);
        }
    }

    private async Task HandleRenameChoiceAsync(string choiceId, CancellationToken cancellationToken)
    {
        if (Rename is null) return;
        try
        {
            if (choiceId.StartsWith(RenameApplyPrefix, StringComparison.Ordinal))
            {
                var result = await Rename.ApplyAsync(choiceId[RenameApplyPrefix.Length..], cancellationToken);
                var text = result.Succeeded
                    ? $"{result.Renamed}件の名前を変更し、フォルダーの中身が予定どおりになっていることを確認しました。"
                    : $"{result.Renamed}件は予定どおりですが、確認できなかったものがあります:\n" + string.Join("\n", result.Mismatches.Take(10).Select(item => "・" + item));
                await AddAsync(ConversationMessage.FromStudio(text, new ConversationChoice(RenameUndoPrefix + result.Journal.Id, "元に戻す")), cancellationToken);
            }
            else if (choiceId.StartsWith(RenameCancelPrefix, StringComparison.Ordinal))
            {
                Rename.Discard(choiceId[RenameCancelPrefix.Length..]);
                await AddAsync(ConversationMessage.FromStudio("名前の変更はやめました。ファイルは変えていません。"), cancellationToken);
            }
            else if (choiceId.StartsWith(RenameUndoPrefix, StringComparison.Ordinal))
            {
                var result = await Rename.UndoAsync(choiceId[RenameUndoPrefix.Length..], cancellationToken);
                await AddAsync(ConversationMessage.FromStudio(result.Succeeded
                    ? $"{result.Renamed}件を元の名前に戻し、確認しました。"
                    : $"{result.Renamed}件を元に戻しました。戻せなかったもの:\n" + string.Join("\n", result.Mismatches.Take(10).Select(item => "・" + item))), cancellationToken);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            await AddAsync(ConversationMessage.FromStudio("ファイル名を変更できませんでした（変更途中のものは元に戻しています）: " + ex.Message), cancellationToken);
        }
    }

    /// <summary>
    /// Step one of 3-5: the screenshots become a text description (shown to the user) that is added to the message as
    /// reference data for the planning and code models. Without a vision model the user is asked to say it in words.
    /// </summary>
    private async Task<string> WithImageDescriptionAsync(string text, IReadOnlyList<ConversationAttachment> attachments, CancellationToken cancellationToken)
    {
        const string askInWords = "画像の中身を文章でも教えてください（どの部分を、どう変えたいか）。";
        if (_imageDescriber is null)
        {
            await AddAsync(ConversationMessage.FromStudio("画像を保存しました。この環境では画像を読めないため、" + askInWords), cancellationToken);
            return text;
        }
        try
        {
            var paths = attachments.Select(item => Path.Combine(Log.Folder, PortableProjectPath.ToPlatformPath(item.RelativePath))).ToArray();
            var progress = new Progress<ProjectCreationProgress>(value => CreationProgress?.Invoke(value));
            var description = await _imageDescriber.DescribeAsync(paths, text, progress, cancellationToken);
            await AddAsync(ConversationMessage.FromStudio($"画像から読み取った内容（{description.Seconds:0}秒）:\n{description.Text}\n\nこの内容を依頼に加えて進めます。違っていたら、そのまま訂正を送ってください。"), cancellationToken);
            // Change requests are limited to 2,000 characters, so the description is shortened to fit after the typed message.
            var room = Math.Max(0, WebProjectImprover.MaxChangeRequestCharacters - text.Length - 60);
            var added = description.Text.Length <= room ? description.Text : description.Text[..room];
            return added.Length == 0 ? text : $"{text}\n\n[添付画像から読み取った内容（参考データ）]\n{added}";
        }
        catch (Exception ex) when (ex is VisionModelUnavailableException or ResourcePressureException or HttpRequestException or InvalidDataException or IOException)
        {
            await AddAsync(ConversationMessage.FromStudio("画像を読めませんでした（" + ex.Message + "）。" + askInWords), cancellationToken);
            return text;
        }
    }

    /// <summary>The zips the user pastes into the Apps Script editor; a failure here never undoes the saved project.</summary>
    private async Task<string> HandOverGasAsync(string projectRoot, int version, int? previous, CancellationToken cancellationToken)
    {
        try
        {
            var files = await GasHandover.WriteAsync(projectRoot, GasHandoverRoot, version, previous, cancellationToken);
            var lines = files.Select(file => (file.EndsWith("-diff.zip", StringComparison.OrdinalIgnoreCase) ? "\n・差分: " : "\n・全ファイル: ") + file);
            return "\nApps Scriptエディタに貼り付けるファイルを書き出しました（中の CHANGES.md に手順があります）:" + string.Concat(lines) +
                   "\nGoogleへの反映と実行はご自身で行ってください。初回実行時に表示される権限が appsscript.json と同じか確認してから許可してください。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            return "\nzipの書き出しに失敗しました（プロジェクトは保存済みです）: " + ex.Message;
        }
    }

    private static StudioBlueprint WithoutExternalAccess(StudioBlueprint blueprint) =>
        blueprint with { Capabilities = blueprint.Capabilities.Where(capability => capability is "workspace.read" or "workspace.write").ToArray() };

    private async Task BuildLocalOnlyAsync(CancellationToken cancellationToken)
    {
        await AddAsync(ConversationMessage.FromUser("外部通信なしで作る"), cancellationToken);
        // Worded without the external-access signals, so later re-plans fall back to local-only permissions.
        AppendToRequest("指示", "このPCの中だけで動かす（外部とのやり取りはしない）");
        _localOnlyChosen = true;
        Blueprint = WithoutExternalAccess(Blueprint!);
        await ShowProposalAsync(cancellationToken);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var (inbox, allNotices) = CreateInbox();
        using var run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_sync)
        {
            _inbox = inbox;
            _run = run;
            _redoRequested = false;
            _pendingKindChange = null;
        }
        Stage = ConversationStage.Running;
        await AddAsync(ConversationMessage.FromStudio(
            "作成を始めました。途中で気づいたことはそのまま送ってください。次の工程で反映します。",
            new ConversationChoice(CancelAndRedoChoice, "今すぐ中止して作り直す")), cancellationToken);
        var progress = new Progress<ProjectCreationProgress>(item => CreationProgress?.Invoke(item));
        try
        {
            // Large plans: a working core first, then the agent loop adds the other features one at a time.
            var (core, later) = Growth is not null && Kind is not StudioProjectKind.Agent ? FeatureGrowth.Split(Blueprint!) : (Blueprint!, []);
            var path = await _scaffolder.CreateAsync(core, Kind!.Value, run.Token, progress, null, Request, inbox);
            IReadOnlyList<FeatureGrowthResult> grown = [];
            if (later.Count > 0)
            {
                // The core is saved and passed its checks; a stop or failure while adding keeps it and says what is missing.
                try { grown = await Growth!.GrowAsync(path, Kind!.Value, Request, later, progress, run.Token); }
                catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or InvalidDataException or IOException or HttpRequestException && !cancellationToken.IsCancellationRequested)
                {
                    grown = later.Select(feature => new FeatureGrowthResult(feature, false, ex.Message, 0)).ToArray();
                }
                await FeatureGrowth.RecordResultsAsync(path, Blueprint!, grown, run.Token);
            }
            await allNotices();
            ProjectPath = path;
            Stage = ConversationStage.Completed;
            var unapplied = inbox.Pending;
            var completion = grown.Any(result => !result.Added)
                ? $"基本部分を保存しましたが、追加要件の検証は完了していません。保存先: {path}" + FeatureGrowth.Describe(grown)
                : $"完成しました。保存先: {path}" + FeatureGrowth.Describe(grown);
            if (Kind == StudioProjectKind.GoogleAppsScript)
                completion += await HandOverGasAsync(path, 1, null, cancellationToken);
            if (unapplied.Count > 0)
                completion += "\n生成の最後の工程より後に届いたため、次の指示はまだ反映していません。改めて送ってください: " +
                              string.Join(" / ", unapplied.Select(item => item.Text));
            await AddAsync(ConversationMessage.FromStudio(completion), cancellationToken);
            await Log.MoveIntoProjectAsync(path, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await allNotices();
            bool redo;
            StudioProjectKind? kindChange;
            lock (_sync)
            {
                redo = _redoRequested;
                kindChange = _pendingKindChange;
            }
            Stage = ConversationStage.Proposed;
            if (!redo)
            {
                await AddAsync(ConversationMessage.FromStudio("作成を中止しました。プロジェクトは保存していません。"), cancellationToken);
                return;
            }
            foreach (var instruction in inbox.Applied.Select(item => item.Text).Concat(inbox.Pending.Select(item => item.Text)))
                AppendToRequest("作成中の指示", instruction);
            if (kindChange is { } changedKind)
                await DecideKindAndPlanAsync(changedKind, $"作成を中止しました。{OutputKindResolver.Label(changedKind)}として設計し直します。", cancellationToken);
            else
            {
                await AddAsync(ConversationMessage.FromStudio("作成を中止しました。いただいた指示を含めて設計し直します。"), cancellationToken);
                await PlanAsync(cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await allNotices();
            Stage = ConversationStage.Proposed;
            await AddAsync(ConversationMessage.FromStudio(
                "作成を完了できませんでした。プロジェクトは保存していません。\n" + ex.Message,
                new ConversationChoice(StartChoice, "もう一度作成"),
                new ConversationChoice(ChangeKindChoice, "形式を変える")), cancellationToken);
        }
        finally
        {
            lock (_sync)
            {
                _inbox = null;
                _run = null;
            }
            // The caller's own cancellation (e.g. application exit) ends the run without a redo.
            if (Stage == ConversationStage.Running) Stage = ConversationStage.Proposed;
        }
    }

    private async Task SteerAsync(string text, CancellationToken cancellationToken)
    {
        SteeringInbox? inbox;
        lock (_sync) inbox = _inbox;
        await AddAsync(ConversationMessage.FromUser(text), cancellationToken);
        if (inbox is null)
        {
            await AddAsync(ConversationMessage.FromStudio("作成が終わったところです。もう一度送ってください。"), cancellationToken);
            return;
        }

        bool improving;
        lock (_sync) improving = _improving;
        var signalled = OutputTargetAdvisor.Analyze(text);
        if (!improving && signalled.RecommendedKind is { } changed && signalled.IsConfident && changed != Kind)
        {
            lock (_sync) _pendingKindChange = changed;
            AppendToRequest("作成中の指示", text);
            await AddAsync(ConversationMessage.FromStudio(
                $"形式を{OutputKindResolver.Label(changed)}に変えるには、作成を中止して作り直す必要があります。",
                new ConversationChoice(CancelAndRedoChoice, "中止して作り直す")), cancellationToken);
            return;
        }

        try
        {
            inbox.Enqueue(text);
            await AddAsync(ConversationMessage.FromStudio(
                "受け取りました。次の工程で反映します。",
                new ConversationChoice(CancelAndRedoChoice, "今すぐ中止して作り直す")), cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException)
        {
            await AddAsync(ConversationMessage.FromStudio(ex.Message), cancellationToken);
        }
    }

    private void RequestRedo()
    {
        CancellationTokenSource? run;
        lock (_sync)
        {
            _redoRequested = true;
            run = _run;
        }
        run?.Cancel();
    }

    private async Task FailAsync(string message, CancellationToken cancellationToken)
    {
        Stage = ConversationStage.Failed;
        await AddAsync(ConversationMessage.FromStudio(message), cancellationToken);
    }

    private async Task AddAsync(ConversationMessage message, CancellationToken cancellationToken)
    {
        var saved = await Log.AppendAsync(message, cancellationToken);
        MessageAdded?.Invoke(saved);
    }
}
