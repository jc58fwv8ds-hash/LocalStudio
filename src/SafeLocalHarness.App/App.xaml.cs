using System.IO;
using System.Windows;
using System.Windows.Threading;
using SafeLocalHarness.App.ViewModels;
using SafeLocalHarness.Core;

namespace SafeLocalHarness.App;

public partial class App : Application
{
    internal LibraryShelf? LibraryShelf { get; private set; }
    internal LibraryGuideDrafter? GuideDrafter { get; private set; }
    internal string? LibraryDownloads { get; private set; }

    private OllamaManager? _ollama;
    private LocalPlanningEngine? _planner;
    private LocalWebAppGenerationEngine? _implementationGenerator;
    private LocalWindowsAppGenerationEngine? _windowsImplementationGenerator;
    private LocalAppsScriptGenerationEngine? _appsScriptGenerator;
    private LocalImageDescriber? _imageDescriber;
    private LocalRenamePlanner? _renamePlanner;
    private LocalOfficePlanner? _officePlanner;
    private LocalPdfPlanner? _pdfPlanner;
    private ChatViewModel? _chat;
    private SetupViewModel? _setup;
    private OfficialRuntimeCatalog? _runtimeCatalog;
    private OllamaModelBenchmarkRunner? _modelBenchmark;
    private AppPaths? _paths;
    private StudioDraftStore? _draftStore;
    private WindowsMemoryPressureProbe? _pressureProbe;
    private MemoryPressureMonitor? _pressureMonitor;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (UserSpaceGuard.IsElevated())
        {
            MessageBox.Show("LocalStudioは管理者権限では動作しません。通常の方法で起動し直してください。", "LocalStudio", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(2);
            return;
        }
        // Screen images for review without the real data folder, runtime or network (see ScreenPreview).
        var previewIndex = Array.FindIndex(e.Args, arg => arg.Equals("--render-preview", StringComparison.OrdinalIgnoreCase));
        if (previewIndex >= 0 && previewIndex + 1 < e.Args.Length)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RenderPreviewAndExitAsync(e.Args[previewIndex + 1]);
            return;
        }
        var paths = AppPaths.ForCurrentUser();
        // While AI work runs, other apps give back memory they are not using, only as much as needed (user, 2026-10-14).
        MemoryRelief.Enabled = true;
        if (e.Args.Contains("--bootstrap-smoke", StringComparer.OrdinalIgnoreCase))
        {
            var smokeHardware = SystemHardwareProbe.Capture(paths);
            var smokePlan = AdaptiveRuntimePolicy.Create(smokeHardware, LocalStudioSettings.DefaultModel);
            RunOffUiThread(() => SystemHardwareProbe.SaveAsync(paths, smokeHardware, smokePlan), paths, "hardware-profile-save");
            Shutdown(0);
            return;
        }
        var settings = new SettingsStore(paths);
        var hardware = SystemHardwareProbe.Capture(paths);
        var resources = AdaptiveRuntimePolicy.Create(hardware, settings.Load().SelectedModel, settings.Load().ResourceMode, MemoryRelief.EstimateReclaimable());
        RunOffUiThread(() => SystemHardwareProbe.SaveAsync(paths, hardware, resources), paths, "hardware-profile-save");
        _paths = paths;
        // Memory pressure is observed while running (every 5 s). Under any sign no new model work starts;
        // on entering Critical the owned model is released as soon as its current request completes.
        _pressureProbe = new WindowsMemoryPressureProbe();
        var modeState = new ResourceModeState(settings);
        _pressureMonitor = new MemoryPressureMonitor(_pressureProbe, () => modeState.Current, AdaptiveRuntimePolicy.EffectiveInstalledBytes(hardware));
        _ollama = new(paths, resourcePlan: resources, pressureMonitor: _pressureMonitor);
        var ownedRuntime = _ollama;
        _pressureMonitor.EnteredCritical += assessment => { _ = Task.Run(() => ownedRuntime.UnloadModelsAsync()); };
        _pressureMonitor.Start(TimeSpan.FromSeconds(5));
        _planner = new(_ollama, resources: resources);
        var draftStore = new StudioDraftStore(paths);
        _draftStore = draftStore;
        var catalog = new ProjectCatalog(paths);
        // 4-5: libraries the user put on the shelf can be used in generated web apps.
        LibraryShelf = LibraryShelf.ForUser(paths);
        LibraryDownloads = Path.Combine(paths.Root, "library-downloads");
        GuideDrafter = new LibraryGuideDrafter(_ollama, settings, resources: resources);
        // The App Spec route first (trials 2026-10-13: stopwatch, shopping list and bill splitter built correctly from one
        // small-model answer, where free generation failed or passed broken apps); free generation remains the fallback.
        _implementationGenerator = new LocalWebAppGenerationEngine(_ollama, settings, resources: resources) { Libraries = LibraryShelf, PreferAppSpec = true, WriteAcceptance = true };
        // Each built EXE is started minimised and used before it is accepted (trial 2026-10-14).
        _windowsImplementationGenerator = new LocalWindowsAppGenerationEngine(_ollama, settings, paths, resources: resources) { BehaviorCheck = WindowsAppBehaviorChecker.CheckAsync };
        _appsScriptGenerator = new LocalAppsScriptGenerationEngine(_ollama, settings, resources: resources);
        // 3-4: everything happens in the chat. The output kind is decided in the conversation, and Web and GAS projects
        // are improved as new versions of the same project.
        // Large apps in parts: a core first, then the agent loop adds one feature at a time;
        // a generator's last failing candidate is repaired in place by the same loop.
        var growth = new FeatureGrowth(paths, _ollama, () => settings.Load().SelectedModel, resources, WindowsAppBehaviorChecker.CheckAsync);
        var scaffolder = new ProjectScaffolder(
            paths,
            generativeGenerator: _implementationGenerator,
            windowsGenerativeGenerator: _windowsImplementationGenerator,
            appsScriptGenerator: _appsScriptGenerator) { Repair = growth.RepairAsync };
        var improver = new KindRoutingImprover(new WebProjectImprover(_implementationGenerator!), new GasProjectImprover(_appsScriptGenerator!));
        var planner = _planner!;
        // 3-5: screenshots are read by the vision model first, then the code model works from the description.
        _imageDescriber = new LocalImageDescriber(_ollama, resources: resources);
        var describer = _imageDescriber;
        // 4-1: the AI plans renames, LocalStudio previews, applies and undoes them; journals stay in LocalStudio's own state.
        _renamePlanner = new LocalRenamePlanner(_ollama, settings, resources: resources);
        // Each model exchange of the work tools is appended to state\model-exchanges, so a mistake seen in real use can
        // become a replay fixture for Smoke (no file contents are stored, only the request, a hash and the model's plan).
        var exchangeLog = () => ModelExchangeLog.DayFile(paths.StateRoot);
        var rename = new RenameAssistant(new RecordingRenamePlanner(_renamePlanner, exchangeLog), Path.Combine(paths.StateRoot, "work-journals"));
        // 4-2: Word / Excel read by the bundled Open XML SDK; edits always become a new file.
        _officePlanner = new LocalOfficePlanner(_ollama, settings, resources: resources);
        var office = new OfficeAssistant(new RecordingOfficePlanner(_officePlanner, exchangeLog));
        // 4-3: PDFs read by PdfPig and rewritten page by page by PDFsharp into new files.
        _pdfPlanner = new LocalPdfPlanner(_ollama, settings, resources: resources);
        // 4-4b: Windows' own PDF renderer and Japanese OCR (nothing is downloaded or sent anywhere).
        var pdf = new PdfAssistant(new RecordingPdfPlanner(_pdfPlanner, exchangeLog), new SafeLocalHarness.Windows.WindowsPdfImaging());
        var hybridPlanner = new HybridPlanningEngine(planner);
        _chat = new ChatViewModel(
            () => new ConversationOrchestrator(hybridPlanner, planner, scaffolder, ConversationLog.CreateSession(paths), () => settings.Load().SelectedModel, improver, describer) { Rename = rename, Office = office, Pdf = pdf, Growth = growth },
            async projectRoot =>
            {
                var opened = await ConversationOrchestrator.OpenProjectAsync(hybridPlanner, planner, scaffolder, improver, projectRoot, () => settings.Load().SelectedModel, imageDescriber: describer);
                opened.Rename = rename;
                opened.Office = office;
                opened.Pdf = pdf;
                return opened;
            },
            draftStore)
        {
            OpenSession = logPath => new ConversationOrchestrator(hybridPlanner, planner, scaffolder, ConversationLog.Open(logPath), () => settings.Load().SelectedModel, improver, describer) { Rename = rename, Office = office, Pdf = pdf, Growth = growth },
            // The memory mode can be changed from the chat at any time, through the same session-only state as Setup.
            CurrentMode = () => modeState.Current,
            ChangeMode = mode => modeState.SetAsync(mode),
            Confirm = message => MessageBox.Show(message, "LocalStudio", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes
        };
        // Nothing is downloaded at startup. The Setup screen shows the pinned plan and runs it only on an explicit click.
        _runtimeCatalog = new OfficialRuntimeCatalog();
        _modelBenchmark = new OllamaModelBenchmarkRunner(_ollama);
        var bootstrap = new ZeroAdminBootstrapService(
            paths,
            new RuntimeArtifactManager(paths),
            _runtimeCatalog,
            _ollama,
            new ManagedModelManager(paths, _ollama, _modelBenchmark));
        var localAiSetup = new LocalAiSetupService(
            paths,
            _runtimeCatalog,
            bootstrap,
            _ollama.DetectExternalUserInstallation,
            settings,
            _pressureMonitor,
            _ollama,
            modeState);
        _setup = new SetupViewModel(
            localAiSetup,
            settings,
            message => MessageBox.Show(message, "LocalStudio", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes);
        modeState.Changed += mode => Dispatcher.InvokeAsync(() => { _chat?.RefreshMode(); _setup?.ShowMode(mode); });
        const string cartridgeFilter = "LocalStudioカートリッジ (*.lscart)|*.lscart";
        var cartridgeDialogs = new CartridgeDialogs(
            suggestedName =>
            {
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    FileName = suggestedName,
                    DefaultExt = ProjectCartridgePackages.Extension,
                    Filter = cartridgeFilter,
                    AddExtension = true,
                    OverwritePrompt = true,
                    InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                };
                return dialog.ShowDialog() == true ? dialog.FileName : null;
            },
            () =>
            {
                var dialog = new Microsoft.Win32.OpenFileDialog { Filter = cartridgeFilter, CheckFileExists = true, Multiselect = false };
                return dialog.ShowDialog() == true ? dialog.FileName : null;
            },
            message => MessageBox.Show(message, "LocalStudio", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes);
        var projects = new ProjectsViewModel(catalog, new ProjectVerificationStatusStore(paths), new ProjectCartridgePackages(paths), cartridgeDialogs);
        var shell = new ShellViewModel(_chat, projects, _setup, () => ChatHistory.ListSessions(paths));
        if (e.Args.Contains("--setup", StringComparer.OrdinalIgnoreCase)) shell.ShowSetupCommand.Execute(null);
        new MainWindow { DataContext = shell }.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _pressureMonitor?.Dispose();
        _chat?.CancelActiveOperation();
        _setup?.CancelActiveOperation();
        if (_chat is not null && _draftStore is not null && _paths is not null)
        {
            // A failed or slow final draft save must not block application shutdown.
            var draft = _chat.CaptureDraftForExit();
            var draftStore = _draftStore;
            RunOffUiThread(() => draftStore.SaveAsync(draft), _paths, "final-draft-save");
        }
        _planner?.Dispose();
        _implementationGenerator?.Dispose();
        _windowsImplementationGenerator?.Dispose();
        _appsScriptGenerator?.Dispose();
        _imageDescriber?.Dispose();
        _renamePlanner?.Dispose();
        _officePlanner?.Dispose();
        _pdfPlanner?.Dispose();
        GuideDrafter?.Dispose();
        if (_ollama is not null && _paths is not null)
        {
            var ollama = _ollama;
            RunOffUiThread(() => ollama.DisposeAsync().AsTask(), _paths, "owned-runtime-dispose");
        }
        _modelBenchmark?.Dispose();
        _runtimeCatalog?.Dispose();
        _pressureProbe?.Dispose();
        base.OnExit(e);
    }

    private async Task RenderPreviewAndExitAsync(string folder)
    {
        var code = 0;
        try { await ScreenPreview.RenderAsync(folder); }
        catch (Exception ex)
        {
            code = 3;
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "LocalStudio-preview-error.txt"), ex.ToString()); } catch (IOException) { }
        }
        Shutdown(code);
    }

    private static readonly TimeSpan UiBlockingLimit = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Blocking the WPF UI thread on a task that resumes on that thread deadlocks: on 2026-10-06 the window never
    /// appeared and the process waited forever. Work runs on the thread pool and the wait is bounded, so startup
    /// and shutdown always continue. Failures and timeouts are logged, never hidden.
    /// </summary>
    private static bool RunOffUiThread(Func<Task> work, AppPaths paths, string operation)
    {
        try
        {
            if (Task.Run(work).Wait(UiBlockingLimit)) return true;
            LogBackgroundFailure(paths, operation, $"timed out after {UiBlockingLimit.TotalSeconds:0} s");
        }
        catch (AggregateException ex)
        {
            LogBackgroundFailure(paths, operation, ex.InnerException?.ToString() ?? ex.ToString());
        }
        return false;
    }

    private static void LogBackgroundFailure(AppPaths paths, string operation, string detail)
    {
        try
        {
            paths.EnsureCreated();
            File.AppendAllText(Path.Combine(paths.LogRoot, "ui-errors.log"), $"{DateTimeOffset.Now:O}\t{operation}\t{detail}\n\n", System.Text.Encoding.UTF8);
        }
        catch
        {
            // Logging must never block startup or shutdown.
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            var paths = AppPaths.ForCurrentUser();
            paths.EnsureCreated();
            var message = $"{DateTimeOffset.Now:O}\t{e.Exception}\n\n";
            File.AppendAllText(Path.Combine(paths.LogRoot, "ui-errors.log"), message, System.Text.Encoding.UTF8);
        }
        catch
        {
            // Error reporting must never replace the original UI failure.
        }

        MessageBox.Show(
            "操作を安全に停止しました。LocalStudioは終了していません。セットアップ画面を確認してから、もう一度お試しください。",
            "LocalStudio",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        e.Handled = true;
    }
}
