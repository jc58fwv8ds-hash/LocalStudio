using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using SafeLocalHarness.Core;

namespace SafeLocalHarness.App.ViewModels;

/// <summary>File and confirmation dialogs supplied by the application shell.</summary>
public sealed record CartridgeDialogs(
    Func<string, string?> ChooseExportPath,
    Func<string?> ChooseImportFile,
    Func<string, bool> Confirm);

public sealed class ProjectsViewModel : ViewModelBase
{
    private readonly IProjectCatalog _catalog;
    private readonly IProjectVerificationStatusStore _verificationStore;
    private readonly IProjectCartridgePackages? _cartridges;
    private readonly CartridgeDialogs? _dialogs;
    private bool _cartridgeBusy;
    private LocalProjectInfo? _selectedProject;
    private FeatureVerificationItem? _selectedFeature;
    private string _verificationSummary = "プロジェクトを選択してください。";
    private bool _verificationApplicable;
    private string _status = "このPCに保存されたLocalStudioプロジェクトです。";

    public ProjectsViewModel(
        IProjectCatalog catalog,
        IProjectVerificationStatusStore verificationStore,
        IProjectCartridgePackages? cartridges = null,
        CartridgeDialogs? dialogs = null)
    {
        _catalog = catalog;
        _verificationStore = verificationStore;
        _cartridges = cartridges;
        _dialogs = dialogs;
        ExportCartridgeCommand = new AsyncRelayCommand(ExportCartridgeAsync, CanExportCartridge);
        ImportCartridgeCommand = new AsyncRelayCommand(ImportCartridgeAsync, () => !_cartridgeBusy && _cartridges is not null && _dialogs is not null);
        RefreshCommand = new RelayCommand(_ => Refresh());
        OpenProjectCommand = new RelayCommand(_ => OpenSelectedProject(), _ => SelectedProject is not null);
        OpenAppCommand = new RelayCommand(_ => OpenSelectedApp(), _ => FindLaunchTarget(SelectedProject) is not null);
        ImproveProjectCommand = new RelayCommand(_ => RequestImprovement(), _ => CanImproveSelected);
        ConfirmFeatureCommand = new AsyncRelayCommand(() => RecordSelectedAsync(ManualFeatureStatus.Confirmed), CanConfirmFeature);
        ReportFeatureCommand = new AsyncRelayCommand(() => RecordSelectedAsync(ManualFeatureStatus.Failed), CanReportFeature);
        Refresh();
    }

    public ObservableCollection<LocalProjectInfo> Projects { get; } = [];
    public event Action<LocalProjectInfo>? ImprovementRequested;
    public LocalProjectInfo? SelectedProject
    {
        get => _selectedProject;
        set
        {
            if (!Set(ref _selectedProject, value)) return;
            OpenProjectCommand.Refresh();
            OpenAppCommand.Refresh();
            ImproveProjectCommand.Refresh();
            ExportCartridgeCommand.Refresh();
            Raise(nameof(CanImproveSelected));
            Raise(nameof(LaunchActionLabel));
            LoadVerification();
        }
    }
    public ObservableCollection<FeatureVerificationItem> VerificationFeatures { get; } = [];
    public FeatureVerificationItem? SelectedFeature
    {
        get => _selectedFeature;
        set
        {
            if (!Set(ref _selectedFeature, value)) return;
            Raise(nameof(ConfirmFeatureLabel));
            Raise(nameof(ReportFeatureLabel));
            ConfirmFeatureCommand.Refresh();
            ReportFeatureCommand.Refresh();
        }
    }
    public string VerificationSummary { get => _verificationSummary; private set => Set(ref _verificationSummary, value); }
    public bool VerificationApplicable { get => _verificationApplicable; private set { if (Set(ref _verificationApplicable, value)) Raise(nameof(VerificationNotApplicable)); } }
    public bool VerificationNotApplicable => !VerificationApplicable;
    public bool CanImproveSelected => SelectedProject is { Kind: "DesktopOrWebApp" or "GoogleAppsScript" } &&
                                      SelectedProject.Lifecycle is not ("manifest-error" or "read-error");
    public string LaunchActionLabel => SelectedProject?.Kind switch
    {
        nameof(StudioProjectKind.WindowsApp) => "Windowsアプリを開いて試す",
        nameof(StudioProjectKind.GoogleAppsScript) => "GASのzipを開く",
        _ => "Webアプリを開いて試す"
    };
    public string ConfirmFeatureLabel => SelectedFeature?.Status switch
    {
        ManualFeatureStatus.Confirmed => "確認済み",
        ManualFeatureStatus.Failed => "動作したに変更",
        _ => "動作したと記録"
    };
    public string ReportFeatureLabel => SelectedFeature?.Status switch
    {
        ManualFeatureStatus.Failed => "問題あり",
        ManualFeatureStatus.Confirmed => "問題ありに変更",
        _ => "問題ありとして記録"
    };
    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool HasProjects => Projects.Count > 0;
    public RelayCommand RefreshCommand { get; }
    public RelayCommand OpenProjectCommand { get; }
    public RelayCommand OpenAppCommand { get; }
    public RelayCommand ImproveProjectCommand { get; }
    public AsyncRelayCommand ConfirmFeatureCommand { get; }
    public AsyncRelayCommand ReportFeatureCommand { get; }
    public AsyncRelayCommand ExportCartridgeCommand { get; }
    public AsyncRelayCommand ImportCartridgeCommand { get; }

    private bool CanExportCartridge() =>
        !_cartridgeBusy && _cartridges is not null && _dialogs is not null &&
        SelectedProject is not null && _cartridges.CanExport(SelectedProject.Path);

    private void SetCartridgeBusy(bool busy)
    {
        _cartridgeBusy = busy;
        ExportCartridgeCommand.Refresh();
        ImportCartridgeCommand.Refresh();
    }

    /// <summary>Exports sources and the key file only; build outputs such as the EXE are neither included nor opened.</summary>
    private async Task ExportCartridgeAsync()
    {
        var project = SelectedProject;
        if (project is null || _cartridges is null || _dialogs is null) return;
        var invalid = Path.GetInvalidFileNameChars();
        var suggested = new string(project.Name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim() + _cartridges.FileExtension;
        var destination = _dialogs.ChooseExportPath(suggested);
        if (destination is null) return;
        SetCartridgeBusy(true);
        Status = "カートリッジを書き出しています…";
        try
        {
            var written = await Task.Run(() => _cartridges.ExportAsync(project.Path, destination));
            Status = $"カートリッジを書き出しました: {written}（EXEなどのビルド成果物は含めていません）";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        {
            Status = "書き出せませんでした: " + ex.Message;
        }
        finally
        {
            SetCartridgeBusy(false);
        }
    }

    /// <summary>
    /// Inspects the package without extracting it, shows what it contains, and imports it as a new project only after
    /// the user confirms. The original .lscart file is never modified.
    /// </summary>
    private async Task ImportCartridgeAsync()
    {
        if (_cartridges is null || _dialogs is null) return;
        var file = _dialogs.ChooseImportFile();
        if (file is null) return;
        SetCartridgeBusy(true);
        try
        {
            Status = "カートリッジの中身を安全に確認しています…";
            var inspection = await Task.Run(() => _cartridges.InspectAsync(file));
            if (!inspection.Valid)
            {
                Status = "このカートリッジは安全に読み込めないため、取り込みませんでした: " + string.Join(", ", inspection.Issues.Take(5));
                return;
            }
            var message =
                $"「{inspection.ProjectName}」を読み込みます。\n\n" +
                $"目的: {inspection.AppPurpose}\n" +
                $"種類: {inspection.ProjectKind ?? "不明"}\n" +
                $"ファイル: {inspection.FileCount}件（{inspection.TotalBytes / 1024.0:0.#} KB、すべてSHA-256照合済み）\n" +
                $"検証状況: {inspection.VerificationStatus}\n" +
                $"書き出し日時: {inspection.ExportedAt?.ToLocalTime():yyyy/MM/dd HH:mm}\n" +
                $"含まれていないもの（EXEなど）: {inspection.ExcludedPaths.Count}件\n\n" +
                "新しいプロジェクトとして追加し、元のファイルは変更しません。\n" +
                "Windowsアプリの場合は、ソースの安全検査に合格したときだけEXEを作り直します。\n\n" +
                "読み込みますか？";
            if (!_dialogs.Confirm(message))
            {
                Status = "読み込みを取りやめました。";
                return;
            }
            Status = "カートリッジを読み込んでいます…";
            var result = await Task.Run(() => _cartridges.ImportAsync(file));
            Refresh(result.ProjectPath);
            Status = $"「{result.ProjectName}」を読み込みました。{result.RebuildSummary}";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        {
            Status = "読み込めませんでした: " + ex.Message;
        }
        finally
        {
            SetCartridgeBusy(false);
        }
    }

    public void Refresh(string? createdPath = null)
    {
        var selectedPath = createdPath ?? SelectedProject?.Path;
        Projects.Clear();
        foreach (var project in _catalog.GetProjects()) Projects.Add(project);
        SelectedProject = Projects.FirstOrDefault(project => string.Equals(project.Path, selectedPath, StringComparison.OrdinalIgnoreCase)) ?? Projects.FirstOrDefault();
        Raise(nameof(HasProjects));
        Status = Projects.Count == 0
            ? "まだプロジェクトはありません。スタジオで設計案を確認してから作成してください。"
            : $"{Projects.Count}件のプロジェクトがあります。新しい順に表示しています。";
    }

    private void OpenSelectedProject()
    {
        var selected = SelectedProject;
        if (selected is null || !Directory.Exists(selected.Path))
        {
            Status = "プロジェクトの保存先が見つかりません。再読み込みしてください。";
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", selected.Path) { UseShellExecute = true });
        Status = $"フォルダーを開きました: {selected.Name}";
    }

    private void OpenSelectedApp()
    {
        var selected = SelectedProject;
        if (selected is null) return;
        var target = FindLaunchTarget(selected);
        if (target is null)
        {
            Status = "このプロジェクトには開いて試せるアプリがありません。";
            return;
        }
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        Status = $"アプリを開きました。機能を試してから結果を記録してください: {selected.Name}";
    }

    private static string? FindLaunchTarget(LocalProjectInfo? project)
    {
        if (project is null || !Directory.Exists(project.Path)) return null;
        if (project.Kind is nameof(StudioProjectKind.WindowsApp) or nameof(StudioProjectKind.WindowsGame))
        {
            var executable = Path.Combine(project.Path, "LocalStudioApp.exe");
            if (File.Exists(executable)) return executable;
            var legacyExecutable = Path.Combine(project.Path, "LocalStudioCharacterCounter.exe");
            return File.Exists(legacyExecutable) ? legacyExecutable : null;
        }
        if (project.Kind == nameof(StudioProjectKind.GoogleAppsScript))
        {
            // The hand-over zips the user pastes into the Apps Script editor.
            var handover = Path.Combine(GasHandover.DefaultRoot, Path.GetFileName(project.Path));
            return Directory.Exists(handover) ? handover : null;
        }
        var index = Path.Combine(project.Path, "index.html");
        return File.Exists(index) ? index : null;
    }

    private void RequestImprovement()
    {
        if (!CanImproveSelected || SelectedProject is null) return;
        ImprovementRequested?.Invoke(SelectedProject);
    }

    private void LoadVerification()
    {
        VerificationFeatures.Clear();
        SelectedFeature = null;
        var selected = SelectedProject;
        if (selected is null)
        {
            VerificationApplicable = false;
            VerificationSummary = "プロジェクトを選択してください。";
            return;
        }
        try
        {
            var state = _verificationStore.Load(selected.Path);
            VerificationApplicable = state.Applicable;
            VerificationSummary = state.Explanation;
            foreach (var feature in state.Features)
                VerificationFeatures.Add(new FeatureVerificationItem(feature.Id, feature.Text, feature.Instruction, feature.Status, feature.CheckedAt));
            SelectedFeature = VerificationFeatures.FirstOrDefault(item => item.Status == ManualFeatureStatus.Pending) ?? VerificationFeatures.FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            VerificationApplicable = false;
            VerificationSummary = "検証情報を安全に読み取れませんでした。";
            Status = ex.Message;
        }
    }

    private bool CanConfirmFeature() => VerificationApplicable && SelectedProject is not null && SelectedFeature is not null && SelectedFeature.Status != ManualFeatureStatus.Confirmed;

    private bool CanReportFeature() => VerificationApplicable && SelectedProject is not null && SelectedFeature is not null && SelectedFeature.Status != ManualFeatureStatus.Failed;

    private async Task RecordSelectedAsync(ManualFeatureStatus result)
    {
        var project = SelectedProject;
        var feature = SelectedFeature;
        if (project is null || feature is null) return;
        try
        {
            var state = await _verificationStore.RecordAsync(project.Path, feature.Id, result);
            Refresh(project.Path);
            SelectedFeature = VerificationFeatures.FirstOrDefault(item => item.Id == feature.Id) ?? SelectedFeature;
            Status = result == ManualFeatureStatus.Confirmed
                ? $"「{feature.Text}」を操作確認済みとして記録しました。{state.Explanation}"
                : $"「{feature.Text}」に問題があることを記録しました。修正前に完成扱いにはしません。";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        {
            Status = ex.Message;
        }
    }
}

public sealed record FeatureVerificationItem(
    string Id,
    string Text,
    string Instruction,
    ManualFeatureStatus Status,
    DateTimeOffset? CheckedAt)
{
    public string StatusLabel => Status switch
    {
        ManualFeatureStatus.Confirmed => "確認済み",
        ManualFeatureStatus.Failed => "問題あり",
        _ => "未確認"
    };
}
