using SafeLocalHarness.Core;
using System.Diagnostics;
using System.Windows.Threading;

namespace SafeLocalHarness.App.ViewModels;

public sealed record SetupItemView(
    string Title,
    string Version,
    string Source,
    string Size,
    string Destination,
    string License,
    string Sha256);

public sealed class SetupViewModel : ViewModelBase
{
    private readonly ILocalAiSetupService _setup;
    private readonly ISettingsStore _settingsStore;
    private string _status = "このPCの準備状況を確認しています";
    private string _detail = "";
    private string _progressDetail = "待機中";
    private double _progress;
    private bool _isIndeterminate;
    private bool _isBusy;
    private bool _isReady;
    private SetupDownloadPlan? _plan;
    private IReadOnlyList<SetupItemView> _items = [];
    private string _resourceSummary = "";
    private string _externalOllamaNote = "";
    private string _totalSize = "";
    private CancellationTokenSource? _operation;
    private readonly Stopwatch _clock = new();
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _usageTimer;
    private SetupProgress? _latestProgress;
    private string _usageSummary = "メモリの状況を確認しています";
    private string _pressureSummary = "";
    private ResourceMode _mode;
    private bool _usageRefreshing;

    private readonly Func<string, bool> _confirm;

    public const string ExpertModeWarning =
        "エキスパートモードでは、空いているメモリをほぼすべてAIに使います。\n\n" +
        "・他のアプリが遅くなったり、一時的に応答しにくくなる可能性があります。\n" +
        "・保存していない作業がある場合は、先に保存してください。\n" +
        "・Windowsが低メモリを通知したとき、またはメモリ確保量が95%を超えたときは、安全のためAIを止めます。\n" +
        "・LocalStudioが他のアプリを終了することはありません。\n" +
        "・このモードは今回の起動中のみ有効で、次回起動時は共存モードに戻ります。\n\n" +
        "エキスパートモードに切り替えますか？";

    public SetupViewModel(ILocalAiSetupService setup, ISettingsStore settingsStore, Func<string, bool>? confirm = null)
    {
        _setup = setup;
        _settingsStore = settingsStore;
        _confirm = confirm ?? (_ => false);
        _mode = setup.Mode;
        SetupCommand = new AsyncRelayCommand(SetupAsync, () => !IsBusy && !IsReady && CanDownload);
        CancelCommand = new RelayCommand(_ => _operation?.Cancel(), _ => IsBusy);
        RecheckCommand = new RelayCommand(_ => Refresh(), _ => !IsBusy);
        UseFocusModeCommand = new AsyncRelayCommand(() => ChangeModeAsync(ResourceMode.Focus), () => !IsBusy && Mode != ResourceMode.Focus);
        UseCoexistenceModeCommand = new AsyncRelayCommand(() => ChangeModeAsync(ResourceMode.Coexistence), () => !IsBusy && Mode != ResourceMode.Coexistence);
        UseExpertModeCommand = new AsyncRelayCommand(
            async () => { if (_confirm(ExpertModeWarning)) await ChangeModeAsync(ResourceMode.Expert); },
            () => !IsBusy && Mode != ResourceMode.Expert);
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateProgressDetail(), Dispatcher.CurrentDispatcher);
        _timer.Stop();
        _usageTimer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, async (_, _) => await RefreshUsageAsync(), Dispatcher.CurrentDispatcher);
        _usageTimer.Start();
        Refresh();
        _ = RefreshUsageAsync();
    }

    public string UsageSummary { get => _usageSummary; private set => Set(ref _usageSummary, value); }
    public string PressureSummary { get => _pressureSummary; private set => Set(ref _pressureSummary, value); }
    public ResourceMode Mode
    {
        get => _mode;
        private set
        {
            if (!Set(ref _mode, value)) return;
            Raise(nameof(ModeLabel));
            UseFocusModeCommand.Refresh();
            UseCoexistenceModeCommand.Refresh();
            UseExpertModeCommand.Refresh();
        }
    }
    public string ModeLabel => Mode switch
    {
        ResourceMode.Expert => "エキスパートモード（あなたが選択・今回の起動中のみ）：空いているメモリをほぼすべて使います。Windowsの低メモリ通知・確保量95%超では安全のため停止します。",
        ResourceMode.Focus => "集中モード（あなたが選択）：AIに使えるメモリを広げます。他のアプリが遅くなる場合があります。",
        _ => "共存モード（標準）：OSと他のアプリのための予備を多めに残します。"
    };
    public AsyncRelayCommand UseFocusModeCommand { get; }
    public AsyncRelayCommand UseCoexistenceModeCommand { get; }
    public AsyncRelayCommand UseExpertModeCommand { get; }

    private async Task ChangeModeAsync(ResourceMode mode)
    {
        try
        {
            await _setup.SetModeAsync(mode);
            Mode = mode;
            Refresh();
            await RefreshUsageAsync();
            Detail += " モードの変更は判定・予算・ダウンロード可否にすぐ反映されます（AIの保持時間などは次回起動から）。";
        }
        catch (Exception ex)
        {
            Detail = "モードを保存できませんでした: " + ex.Message;
        }
    }

    /// <summary>Shows Local Studio's own memory use, the current judgement and its reasons. Sampling runs off the UI thread.</summary>
    private async Task RefreshUsageAsync()
    {
        if (_usageRefreshing) return;
        _usageRefreshing = true;
        try
        {
            var usage = await Task.Run(() => _setup.GetUsage());
            var a = usage.Assessment;
            UsageSummary =
                $"Local Studio 使用中 {FormatBytes(usage.LocalStudioBytes + usage.OwnedRuntimeBytes)}（本体 {FormatBytes(usage.LocalStudioBytes)} / 専用AI {FormatBytes(usage.OwnedRuntimeBytes)}）" +
                $" ・ 空き {FormatBytes(a.AvailableBytes)} ・ 予備 {FormatBytes(a.ReserveBytes)} ・ AIの予算 {FormatBytes(a.BudgetBytes)}";
            var level = a.Level switch
            {
                MemoryPressureLevel.Critical => "逼迫",
                MemoryPressureLevel.Elevated => "注意",
                _ => "通常"
            };
            PressureSummary = $"判定: {level} ・ " + string.Join(" ", a.Reasons);
        }
        catch (Exception ex)
        {
            PressureSummary = "メモリの状況を取得できませんでした: " + ex.Message;
        }
        finally
        {
            _usageRefreshing = false;
        }
    }

    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Detail { get => _detail; private set => Set(ref _detail, value); }
    public string ProgressDetail { get => _progressDetail; private set => Set(ref _progressDetail, value); }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public bool IsIndeterminate { get => _isIndeterminate; private set => Set(ref _isIndeterminate, value); }
    public bool IsBusy { get => _isBusy; private set { if (Set(ref _isBusy, value)) RefreshCommands(); } }
    public bool IsReady { get => _isReady; private set { if (Set(ref _isReady, value)) RefreshCommands(); } }
    public bool CanDownload => _plan?.CanDownload == true;
    public IReadOnlyList<SetupItemView> Items { get => _items; private set => Set(ref _items, value); }
    public bool HasItems => Items.Count > 0;
    public string ResourceSummary { get => _resourceSummary; private set => Set(ref _resourceSummary, value); }
    public string ExternalOllamaNote { get => _externalOllamaNote; private set => Set(ref _externalOllamaNote, value); }
    public string TotalSize { get => _totalSize; private set => Set(ref _totalSize, value); }
    public string StoragePath => _setup.StorageRoot;
    public AsyncRelayCommand SetupCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand RecheckCommand { get; }

    private void RefreshCommands()
    {
        SetupCommand.Refresh();
        CancelCommand.Refresh();
        RecheckCommand.Refresh();
        UseFocusModeCommand?.Refresh();
        UseCoexistenceModeCommand?.Refresh();
        UseExpertModeCommand?.Refresh();
    }

    /// <summary>Measures resources and reads the pinned catalog. Never downloads or contacts the network.</summary>
    private void Refresh()
    {
        try
        {
            _plan = _setup.CreatePlan();
        }
        catch (Exception ex)
        {
            _plan = null;
            Items = [];
            Raise(nameof(HasItems));
            Status = "準備内容を確認できませんでした";
            Detail = ex.Message;
            RefreshCommands();
            return;
        }

        Items = _plan.Items.Select(item => new SetupItemView(
            item.Kind == SetupDownloadPlan.RuntimeKind ? "Ollama 本体（Local Studio 専用）" : $"AIモデル {item.Product}",
            item.Version,
            item.Source,
            (item.SizeIsApproximate ? "約 " : "") + FormatBytes(item.SizeBytes),
            item.Destination,
            item.License,
            item.Sha256)).ToArray();
        Raise(nameof(HasItems));
        TotalSize = _plan.CanDownload ? $"合計 {FormatBytes(_plan.TotalBytes)}（モデルは目安）" : "";
        ResourceSummary = $"段階: {ResourceBandLabel(_plan.Band)} ・ " + string.Join(" ・ ", _plan.Reasons);
        ExternalOllamaNote = _plan.ExternalOllamaPath is null
            ? ""
            : "このPCにインストール済みのOllamaを検出しました。Local Studioはそれを起動・変更・利用しません（表示のみ）。";
        Status = _plan.CanDownload ? "準備する内容を確認してください" : "いまはダウンロードを行いません";
        Detail = _plan.Summary;
        RefreshCommands();
    }

    private static string ResourceBandLabel(RuntimeResourceBand band) => band switch
    {
        RuntimeResourceBand.HighPerformance => "高性能",
        RuntimeResourceBand.Standard => "標準",
        RuntimeResourceBand.Lightweight => "軽量",
        _ => "AIなし（決定論的ツール中心）"
    };

    private async Task SetupAsync()
    {
        if (_plan is not { CanDownload: true } plan) return;
        _operation = new CancellationTokenSource();
        var cancellationToken = _operation.Token;
        IsBusy = true; IsIndeterminate = true; Progress = 0;
        Detail = "処理の段階、転送量、速度、残り時間をここに表示します。";
        _clock.Restart();
        _latestProgress = null;
        _timer.Start();
        UpdateProgressDetail();
        var progress = new Progress<SetupProgress>(p =>
        {
            _latestProgress = p;
            Status = p.Stage switch
            {
                "ollama" => "Ollama 本体を取得・検証しています",
                "model" => "AIモデルを取得・検証しています",
                _ => p.Message
            };
            IsIndeterminate = p.Fraction is null;
            if (p.Fraction is not null) Progress = p.Fraction.Value;
            UpdateProgressDetail();
        });
        try
        {
            // The approval binds exactly the items shown on screen; the bootstrap rejects any mismatch.
            var state = await _setup.RunApprovedAsync(plan.CreateApproval(DateTimeOffset.UtcNow), progress, cancellationToken);
            if (state.CurrentStage == BootstrapStage.Ready)
            {
                await _settingsStore.SaveAsync(_settingsStore.Load() with { SelectedModel = plan.Model! }, CancellationToken.None);
                IsIndeterminate = false; Progress = 1; IsReady = true;
                Status = "LocalStudioの準備ができました";
                Detail = "スタジオ画面から、作りたいものを普段の言葉で伝えられます。";
            }
            else
            {
                IsIndeterminate = false; Progress = 0;
                Status = state.LastFailure?.Kind == RuntimeFailureKind.Interrupted ? "処理を中止しました" : "安全に停止しました";
                Detail = Describe(state.LastFailure?.Kind ?? RuntimeFailureKind.Unknown, state.LastFailure?.Message);
            }
        }
        catch (OperationCanceledException)
        {
            IsIndeterminate = false; Progress = 0;
            Status = "処理を中止しました";
            Detail = Describe(RuntimeFailureKind.Interrupted, null);
        }
        catch (RuntimeProvisioningException ex)
        {
            IsIndeterminate = false; Progress = 0;
            Status = "安全に停止しました";
            Detail = Describe(ex.Kind, ex.Message);
        }
        catch (Exception ex)
        {
            IsIndeterminate = false; Progress = 0;
            Status = "安全に停止しました";
            Detail = Describe(ex is UnauthorizedAccessException ? RuntimeFailureKind.AccessDenied : RuntimeFailureKind.Unknown, ex.Message);
        }
        finally
        {
            _timer.Stop();
            _clock.Stop();
            _operation.Dispose();
            _operation = null;
            IsBusy = false;
            if (IsReady) ProgressDetail = $"完了 ・ 所要時間 {FormatDuration(_clock.Elapsed)}";
        }
    }

    private static string Describe(RuntimeFailureKind kind, string? technical)
    {
        var reason = kind switch
        {
            RuntimeFailureKind.Dns => "取得先の名前を解決できませんでした。社内ネットワークで遮断されている可能性があります。",
            RuntimeFailureKind.Proxy => "プロキシ認証が必要なため取得できませんでした。",
            RuntimeFailureKind.Tls => "安全な接続（TLS）を確認できませんでした。通信を検査する装置がある環境では取得できません。",
            RuntimeFailureKind.Http => "取得先との通信に失敗しました。",
            RuntimeFailureKind.AccessDenied => "端末ポリシーにより保存が拒否されました。昇格や回避は行いません。",
            RuntimeFailureKind.InsufficientDisk => "空きディスクが足りません。",
            RuntimeFailureKind.ChecksumMismatch => "ダウンロードしたファイルのSHA-256が一致しなかったため、導入していません。",
            RuntimeFailureKind.InvalidSource => "確認した内容と取得予定の内容が一致しないか、取得元が許可されていません。「再確認」を押して内容を確認し直してください。",
            RuntimeFailureKind.InvalidArchive => "配布物が安全確認を通りませんでした。",
            RuntimeFailureKind.SmokeFailure => "取得したものの起動確認に失敗しました。",
            RuntimeFailureKind.Interrupted => "途中まで取得したファイルは保持し、次回は続きから再開します。",
            RuntimeFailureKind.EnvironmentRestriction => "この環境の制限（メモリ・権限など）により停止しました。",
            _ => "予期しない理由で停止しました。"
        };
        var scope = kind == RuntimeFailureKind.Interrupted ? "" : " AI機能のみ停止し、他の機能はそのまま使えます。";
        return string.IsNullOrWhiteSpace(technical) ? reason + scope : $"{reason}{scope}（詳細: {technical}）";
    }

    private void UpdateProgressDetail()
    {
        if (!_clock.IsRunning) return;
        var elapsed = FormatDuration(_clock.Elapsed);
        if (_latestProgress is null)
        {
            ProgressDetail = $"準備を開始しています ・ 経過 {elapsed} ・ 残り時間を計算中";
            return;
        }

        var p = _latestProgress;
        var amount = p.CompletedBytes is not null && p.TotalBytes is not null
            ? $"{FormatBytes(p.CompletedBytes.Value)} / {FormatBytes(p.TotalBytes.Value)}"
            : p.Fraction is not null ? $"{p.Fraction.Value:P0}" : "進捗を確認中";
        var speed = p.BytesPerSecond is > 0 ? $" ・ {FormatBytes((long)p.BytesPerSecond.Value)}/秒" : "";
        var remaining = p.EstimatedRemaining is not null
            ? $" ・ 残り目安 {FormatDuration(p.EstimatedRemaining.Value)}"
            : " ・ 残り時間を計算中";
        ProgressDetail = $"{amount}{speed}{remaining} ・ 経過 {elapsed}";
    }

    private static string FormatDuration(TimeSpan value)
    {
        if (value.TotalHours >= 1) return value.ToString(@"h\:mm\:ss");
        return value.ToString(@"m\:ss");
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.0} {units[unit]}";
    }

    public void CancelActiveOperation()
    {
        _usageTimer.Stop();
        _operation?.Cancel();
    }
}
