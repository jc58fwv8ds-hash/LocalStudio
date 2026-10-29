using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using SafeLocalHarness.Core;

namespace SafeLocalHarness.App.ViewModels;

public sealed record ChatChoiceItem(string Id, string Label, bool IsPrimary);

public sealed record ChatMessageItem(bool IsUser, string Text, IReadOnlyList<string> ImagePaths, IReadOnlyList<ChatChoiceItem> Choices)
{
    public bool IsStudio => !IsUser;
    public bool HasImages => ImagePaths.Count > 0;
    public bool HasChoices => Choices.Count > 0;
}

public sealed record PendingAttachment(string FullPath)
{
    public string Name => Path.GetFileName(FullPath);
}

/// <summary>
/// The chat screen (3-4): one conversation at a time on top of ConversationOrchestrator. The output kind is decided
/// in the conversation, messages sent while something is being built become instructions for the next step, and
/// images can be attached (kept with the message; reading them comes in 3-5).
/// </summary>
public sealed class ChatViewModel : ViewModelBase
{
    private readonly Func<ConversationOrchestrator> _newConversation;
    private readonly Func<string, Task<ConversationOrchestrator>> _openProject;
    private readonly IStudioDraftStore _draftStore;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _clock;
    private readonly Stopwatch _elapsed = new();
    private ConversationOrchestrator _conversation;
    private CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _draftSave;
    private string _input = "";
    private string _activity = "";
    private bool _isWorking;
    private string? _openedProject;
    private string? _workFolder;
    private IReadOnlyList<string> _documents = [];
    private string? _document => _documents.Count > 0 ? _documents[0] : null;
    private bool IsPdf => _document is not null && System.IO.Path.GetExtension(_document).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
    private bool IsImages => _documents.Count > 0 && _documents.All(path => PdfExport.ImageExtensions.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant()));

    public ChatViewModel(
        Func<ConversationOrchestrator> newConversation,
        Func<string, Task<ConversationOrchestrator>> openProject,
        IStudioDraftStore draftStore)
    {
        _newConversation = newConversation;
        _openProject = openProject;
        _draftStore = draftStore;
        _input = draftStore.Load().Request;
        _conversation = Attach(newConversation());
        SendCommand = new AsyncRelayCommand(SendAsync, () => Input.Trim().Length > 0 || PendingAttachments.Count > 0);
        ChooseCommand = new RelayCommand(parameter => { if (parameter is string id) _ = ChooseAsync(id); }, _ => true);
        NewChatCommand = new RelayCommand(_ => StartNewChat(), _ => !IsRunning && !IsWorking);
        ClearFolderCommand = new RelayCommand(_ => SetWorkFolder(null));
        ClearDocumentCommand = new RelayCommand(_ => SetDocument(null));
        RemoveAttachmentCommand = new RelayCommand(parameter => { if (parameter is PendingAttachment item) PendingAttachments.Remove(item); SendCommand.Refresh(); });
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Raise(nameof(ActivityLine)), _dispatcher);
        SetModeCommand = new RelayCommand(parameter => { if (parameter is string name && Enum.TryParse<ResourceMode>(name, out var mode)) _ = ChangeModeAsync(mode, retry: false); }, _ => ChangeMode is not null);
    }

    /// <summary>The memory mode, changeable from the chat at any time (user, 2026-10-19). Expert asks for confirmation first.</summary>
    public Func<ResourceMode>? CurrentMode { get; init; }
    public Func<ResourceMode, Task>? ChangeMode { get; init; }
    public Func<string, bool>? Confirm { get; init; }
    public RelayCommand SetModeCommand { get; }
    public const string ExpertRetryChoice = "ui:expert-retry";
    public ResourceMode Mode => CurrentMode?.Invoke() ?? ResourceMode.Coexistence;
    public bool IsExpert => Mode == ResourceMode.Expert;
    public string ModeLabel => Mode switch
    {
        ResourceMode.Expert => "メモリ：エキスパート",
        ResourceMode.Focus => "メモリ：集中",
        _ => "メモリ：共存"
    };
    public string ModeToolTip => Mode switch
    {
        ResourceMode.Expert => "エキスパートモード（今回の起動中のみ）：空いているメモリをほぼすべてAIに使います。押すと切り替えられます。",
        ResourceMode.Focus => "集中モード：AIに使えるメモリを広げます。押すと切り替えられます。",
        _ => "共存モード（標準）：他のアプリのための予備を多めに残します。押すと切り替えられます。"
    };

    /// <summary>Called when the mode changed anywhere (also from the Setup screen).</summary>
    public void RefreshMode()
    {
        Raise(nameof(Mode)); Raise(nameof(IsExpert)); Raise(nameof(ModeLabel)); Raise(nameof(ModeToolTip));
    }

    private Func<CancellationToken, Task>? _lastWork;

    private async Task ChangeModeAsync(ResourceMode mode, bool retry)
    {
        if (ChangeMode is null || (mode == Mode && !retry)) return;
        if (mode == ResourceMode.Expert && Mode != ResourceMode.Expert && Confirm is not null && !Confirm(SetupViewModel.ExpertModeWarning)) return;
        try { await ChangeMode(mode); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AddLocalNotice("モードを切り替えられませんでした: " + ex.Message);
            return;
        }
        RefreshMode();
        AddLocalNotice(mode switch
        {
            ResourceMode.Expert => "エキスパートモードにしました（今回の起動中のみ）。",
            ResourceMode.Focus => "集中モードにしました。",
            _ => "共存モードに戻しました。"
        } + (retry && _lastWork is not null ? "止まった処理をもう一度始めます。" : ""));
        if (retry && _lastWork is { } work && !IsBusy) await RunAsync(work);
    }

    /// <summary>A reply in which the memory guard stopped AI work gets a button to switch to Expert and try again.</summary>
    private void OfferExpertIfStopped(string text)
    {
        if (ChangeMode is null || IsExpert || !MemoryGuardMessages.IsRefusal(text)) return;
        Messages.Add(new ChatMessageItem(false, "メモリに余裕がないため止まりました。エキスパートモードにすると、空いているメモリをほぼすべて使って続けられます（他のアプリが遅くなる場合があります）。",
            [], [new ChatChoiceItem(ExpertRetryChoice, "エキスパートモードでもう一度", true)]));
        Raise(nameof(IsEmpty));
    }

    public event Action<string>? ProjectChanged;

    /// <summary>Raised with the record's path when a chat that is not a project saves its first message.</summary>
    public event Action<string>? SessionSaved;

    /// <summary>Opens a saved chat (not a project) to read and continue it.</summary>
    public Func<string, ConversationOrchestrator>? OpenSession { get; init; }

    private string? _openedSession;
    public string? OpenedSession => _openedSession;
    public bool IsBusy => IsRunning || IsWorking;

    public ObservableCollection<ChatMessageItem> Messages { get; } = [];
    public ObservableCollection<PendingAttachment> PendingAttachments { get; } = [];
    public bool IsEmpty => Messages.Count == 0;
    public bool IsRunning => _conversation.Stage == ConversationStage.Running;
    public bool IsWorking { get => _isWorking; private set { if (Set(ref _isWorking, value)) { Raise(nameof(ActivityLine)); Raise(nameof(HasActivity)); NewChatCommand.Refresh(); } } }
    public bool HasActivity => IsWorking || IsRunning;
    public string ActivityLine => HasActivity
        ? $"{(_activity.Length > 0 ? _activity : "考えています")} ・ 経過 {(int)_elapsed.Elapsed.TotalMinutes}:{_elapsed.Elapsed.Seconds:00}" +
          (IsRunning ? "　（作成中に送ったメッセージは、次の工程から反映します）" : "")
        : "";
    public string InputHint => IsImages
        ? $"画像 {_documents.Count}枚。『PDFにまとめて』のように入力してください"
        : IsPdf
        ? _documents.Count > 1
            ? $"PDF {_documents.Count}件。聞きたいことか操作を入力してください（例：結合して／全ページを右に回転して）"
            : "PDFについて聞くか、操作を入力してください（例：3ページ目を削除して／全ページを右に回転して）。📄で続けてPDFを選ぶと追加できます"
        : _document is not null
        ? "このファイルについて聞くか、変えたいことを入力してください（例：売上の合計は？／「案」を「確定」に変えて）"
        : _workFolder is not null
        ? "このフォルダーのファイル名をどう変えますか？（例：先頭に撮影日を付けて、3桁の連番にして）"
        : IsRunning ? "作成中です。方針の変更や追加の指示をここから送れます" : _openedProject is not null ? "変えたいことを話し言葉で入力してください" : "作りたいものを、普段の言葉で伝えてください";
    public string? OpenedProject => _openedProject;
    public string Input
    {
        get => _input;
        set
        {
            if (!Set(ref _input, value)) return;
            SendCommand.Refresh();
            QueueDraftSave();
        }
    }
    public AsyncRelayCommand SendCommand { get; }
    public RelayCommand ChooseCommand { get; }
    public RelayCommand NewChatCommand { get; }
    public RelayCommand RemoveAttachmentCommand { get; }
    public RelayCommand ClearFolderCommand { get; }
    public RelayCommand ClearDocumentCommand { get; }

    /// <summary>4-2: while a Word / Excel file is chosen, messages are about that file (edits become a new file).</summary>
    public string? Document => _document;
    public IReadOnlyList<string> Documents => _documents;
    public bool HasDocument => _document is not null;
    public string DocumentName => _document is null ? "" : System.IO.Path.GetFileName(_document) + (_documents.Count > 1 ? $" ほか{_documents.Count - 1}件" : "");

    public string? SetDocument(string? path) => SetDocuments(path is null ? [] : [path]);

    /// <summary>4-2 / 4-3: one Word or Excel file, or 1–10 PDFs (joined in the chosen order when there are several).</summary>
    public string? SetDocuments(IReadOnlyList<string> paths)
    {
        if (paths.Count > 0)
        {
            var pdf = paths.All(path => System.IO.Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase));
            var images = paths.All(path => PdfExport.ImageExtensions.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant()));
            if (images)
            {
                if (paths.Count > PdfExport.MaxImages) return $"画像は{PdfExport.MaxImages}枚まで選べます。";
                _documents = paths.ToArray();
                SetWorkFolder(null);
                Raise(nameof(Document)); Raise(nameof(HasDocument)); Raise(nameof(DocumentName)); Raise(nameof(InputHint));
                SendCommand.Refresh();
                return null;
            }
            if (!pdf && paths.Count > 1) return "複数まとめて選べるのはPDFと画像だけです。Word・Excel・PowerPointは1つずつ選んでください。";
            if (pdf && paths.Count > PdfDocumentReader.MaxFiles) return $"PDFは{PdfDocumentReader.MaxFiles}個まで選べます。";
            foreach (var path in paths)
                if ((pdf ? PdfDocumentReader.Check(path) : OfficeDocumentReader.Check(path)) is { } reason) return $"{System.IO.Path.GetFileName(path)}: {reason}";
        }
        _documents = paths.ToArray();
        if (paths.Count > 0) SetWorkFolder(null);
        Raise(nameof(Document));
        Raise(nameof(HasDocument));
        Raise(nameof(DocumentName));
        Raise(nameof(InputHint));
        SendCommand.Refresh();
        return null;
    }

    /// <summary>4-1: while a folder is chosen, messages are file tasks for that folder (nothing changes before the preview is approved).</summary>
    public string? WorkFolder => _workFolder;
    public bool HasWorkFolder => _workFolder is not null;
    public string WorkFolderName => _workFolder is null ? "" : System.IO.Path.GetFileName(_workFolder.TrimEnd(System.IO.Path.DirectorySeparatorChar));

    /// <summary>Returns the reason when the folder cannot be used.</summary>
    public string? SetWorkFolder(string? folder)
    {
        if (folder is not null && BulkRenamer.CheckFolder(folder) is { } reason) return reason;
        _workFolder = folder;
        if (folder is not null && _document is not null) SetDocument(null);
        Raise(nameof(WorkFolder));
        Raise(nameof(HasWorkFolder));
        Raise(nameof(WorkFolderName));
        Raise(nameof(InputHint));
        SendCommand.Refresh();
        return null;
    }

    /// <summary>Adds image files (from the button, drag and drop or paste); returns the reason when one is refused.</summary>
    public string? AddAttachment(string path)
    {
        if (PendingAttachments.Any(item => string.Equals(item.FullPath, path, StringComparison.OrdinalIgnoreCase))) return null;
        var reason = ChatAttachmentPolicy.Check(path, PendingAttachments.Count);
        if (reason is null) PendingAttachments.Add(new PendingAttachment(path));
        SendCommand.Refresh();
        return reason;
    }

    public async Task OpenProjectAsync(string projectRoot)
    {
        if (IsRunning || IsWorking || string.Equals(_openedProject, projectRoot, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            var opened = await _openProject(projectRoot);
            Replace(opened);
            _openedProject = projectRoot;
            foreach (var message in opened.Messages) Messages.Add(ToItem(message, opened.Log.Folder));
            RaiseConversation();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            AddLocalNotice("このプロジェクトを開けませんでした: " + ex.Message);
        }
    }

    public void StartNewChat()
    {
        if (IsRunning || IsWorking) return;
        Replace(_newConversation());
        _openedProject = null;
        _openedSession = null;
        RaiseConversation();
    }

    public Task OpenSessionAsync(string logPath)
    {
        if (IsRunning || IsWorking || OpenSession is null || string.Equals(_openedSession, logPath, StringComparison.OrdinalIgnoreCase)) return Task.CompletedTask;
        try
        {
            var opened = OpenSession(logPath);
            Replace(opened);
            _openedProject = null;
            _openedSession = logPath;
            foreach (var message in opened.Messages) Messages.Add(ToItem(message, opened.Log.Folder));
            SetDocuments([]);
            SetWorkFolder(null);
            RaiseConversation();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            AddLocalNotice("このチャットを開けませんでした: " + ex.Message);
        }
        return Task.CompletedTask;
    }

    /// <summary>Sends the current input and waits for the reply (used by the screen preview).</summary>
    internal Task SendNowAsync() => SendAsync();

    private async Task SendAsync()
    {
        var text = Input.Trim();
        var images = PendingAttachments.Select(item => item.FullPath).ToArray();
        if (text.Length == 0 && images.Length == 0) return;
        if (IsImages && images.Length == 0)
        {
            Input = "";
            var chosen = _documents;
            await RunAsync(token => _conversation.SubmitImagesAsync(text, chosen, token));
            return;
        }
        if (IsPdf && images.Length == 0)
        {
            Input = "";
            var pdfs = _documents;
            await RunAsync(token => _conversation.SubmitPdfAsync(text, pdfs, token));
            return;
        }
        if (_document is { } document && images.Length == 0)
        {
            Input = "";
            await RunAsync(token => _conversation.SubmitDocumentAsync(text, document, token));
            return;
        }
        if (_workFolder is { } folder && images.Length == 0)
        {
            Input = "";
            await RunAsync(token => _conversation.SubmitWorkAsync(text, folder, token));
            return;
        }
        if (text.Length == 0) text = "画像を添付しました。";
        Input = "";
        PendingAttachments.Clear();
        await RunAsync(async token =>
        {
            var stored = new List<ConversationAttachment>();
            foreach (var image in images)
                stored.Add(await ChatAttachmentPolicy.StoreAsync(image, _conversation.Log.Folder, stored.Count, token));
            await _conversation.SubmitAsync(text, stored, token);
        });
    }

    private Task ChooseAsync(string choiceId)
    {
        if (choiceId == ExpertRetryChoice) return ChangeModeAsync(ResourceMode.Expert, retry: true);
        // "開いて確かめる": the latest working copy opens in its usual app (several files: their folder).
        if (choiceId.StartsWith(ConversationOrchestrator.WorkOpenPrefix, StringComparison.Ordinal))
        {
            var files = _conversation.WorkingFiles(choiceId[ConversationOrchestrator.WorkOpenPrefix.Length..]);
            if (files.Count == 0) { AddLocalNotice("作業用のコピーが見つかりませんでした。"); return Task.CompletedTask; }
            try { Process.Start(new ProcessStartInfo(files.Count == 1 ? files[0] : System.IO.Path.GetDirectoryName(files[0])!) { UseShellExecute = true }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { AddLocalNotice("開けませんでした: " + ex.Message); }
            return Task.CompletedTask;
        }
        return RunAsync(token => _conversation.ChooseAsync(choiceId, token));
    }

    private async Task RunAsync(Func<CancellationToken, Task> work)
    {
        var running = IsRunning;
        if (!running) BeginActivity();
        _lastWork = work;
        try
        {
            await work(_lifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is HttpRequestException)
        {
            AddLocalNotice("ローカルAIへ接続できません。サイドバーの「セットアップ」で準備を完了してください。");
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            AddLocalNotice(ex.Message);
            OfferExpertIfStopped(ex.Message);
        }
        finally
        {
            if (!running) EndActivity();
        }
    }

    private ConversationOrchestrator Attach(ConversationOrchestrator conversation)
    {
        // A replaced conversation can still finish a step in the background; only the current one reaches the screen.
        conversation.MessageAdded += message => _dispatcher.InvokeAsync(() =>
        {
            if (!ReferenceEquals(conversation, _conversation)) return;
            Messages.Add(ToItem(message, conversation.Log.Folder));
            Raise(nameof(IsEmpty));
            if (message.Role != ConversationRole.User) OfferExpertIfStopped(message.Text);
            if (conversation.ProjectPath is null && _openedProject is null && conversation.Messages.Count == 1)
            {
                _openedSession = conversation.Log.FilePath;
                SessionSaved?.Invoke(conversation.Log.FilePath);
            }
        });
        conversation.CreationProgress += progress => _dispatcher.InvokeAsync(() =>
        {
            if (!ReferenceEquals(conversation, _conversation)) return;
            _activity = progress.Message;
            Raise(nameof(ActivityLine));
        });
        conversation.StageChanged += stage => _dispatcher.InvokeAsync(() =>
        {
            if (!ReferenceEquals(conversation, _conversation)) return;
            RaiseConversation();
            if (stage == ConversationStage.Completed && conversation.ProjectPath is { } path)
            {
                _openedProject = path;
                _openedSession = null;
                ProjectChanged?.Invoke(path);
            }
        });
        return conversation;
    }

    private void Replace(ConversationOrchestrator next)
    {
        _conversation = Attach(next);
        Messages.Clear();
        PendingAttachments.Clear();
    }

    private static ChatMessageItem ToItem(ConversationMessage message, string folder) => new(
        message.Role == ConversationRole.User,
        message.Text,
        message.Attachments.Select(item => Path.Combine(folder, item.RelativePath.Replace('/', Path.DirectorySeparatorChar))).Where(File.Exists).ToArray(),
        message.Choices.Select(choice => new ChatChoiceItem(choice.Id, choice.Label,
            choice.Id == ConversationOrchestrator.StartChoice || choice.Id.StartsWith(ConversationOrchestrator.RenameApplyPrefix, StringComparison.Ordinal) ||
            choice.Id.StartsWith(ConversationOrchestrator.OfficeApplyPrefix, StringComparison.Ordinal) ||
            choice.Id.StartsWith(ConversationOrchestrator.WorkSavePrefix, StringComparison.Ordinal))).ToArray());

    private void AddLocalNotice(string text)
    {
        Messages.Add(new ChatMessageItem(false, text, [], []));
        Raise(nameof(IsEmpty));
    }

    private void BeginActivity()
    {
        _activity = "";
        _elapsed.Restart();
        _clock.Start();
        IsWorking = true;
    }

    private void EndActivity()
    {
        _clock.Stop();
        _elapsed.Stop();
        IsWorking = false;
        RaiseConversation();
    }

    private void RaiseConversation()
    {
        Raise(nameof(IsRunning));
        Raise(nameof(HasActivity));
        Raise(nameof(ActivityLine));
        Raise(nameof(InputHint));
        Raise(nameof(IsEmpty));
        Raise(nameof(OpenedProject));
        Raise(nameof(IsBusy));
        NewChatCommand.Refresh();
    }

    private void QueueDraftSave()
    {
        _draftSave?.Cancel();
        _draftSave?.Dispose();
        _draftSave = new CancellationTokenSource();
        _ = SaveDraftAfterDelayAsync(_draftSave.Token);
    }

    private async Task SaveDraftAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(500, cancellationToken);
            await _draftStore.SaveAsync(new StudioDraft(Input, StudioProjectKind.DesktopOrWebApp, DateTimeOffset.Now), cancellationToken);
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    /// <summary>Reads the unsent text on the UI thread; the caller saves it off the UI thread at shutdown.</summary>
    public StudioDraft CaptureDraftForExit()
    {
        _draftSave?.Cancel();
        return new StudioDraft(Input, StudioProjectKind.DesktopOrWebApp, DateTimeOffset.Now);
    }

    public void CancelActiveOperation() => _lifetime.Cancel();
}
