using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SafeLocalHarness.App.ViewModels;
using SafeLocalHarness.Core;

namespace SafeLocalHarness.App;

/// <summary>
/// "--render-preview &lt;folder&gt;": lays out the real window off-screen with sample data and saves PNG images, so the
/// screen can be reviewed without opening the app on the user's PC. Nothing here touches the real data folder, the
/// AI runtime or the network; the sample conversation record goes into the output folder.
/// </summary>
internal static class ScreenPreview
{
    public static async Task RenderAsync(string outputFolder)
    {
        outputFolder = Path.GetFullPath(outputFolder);
        UserSpaceGuard.EnsureAllowed(outputFolder);
        Directory.CreateDirectory(outputFolder);
        var logRoot = Path.Combine(outputFolder, "sample-conversation-" + Guid.NewGuid().ToString("N")[..8]);
        var chat = new ChatViewModel(
            () => new ConversationOrchestrator(new SamplePlanner(), new SampleClassifier(), new UnusedScaffolder(),
                ConversationLog.Open(Path.Combine(logRoot, "conversation.jsonl")), () => LocalStudioSettings.LightweightModel),
            _ => throw new InvalidOperationException("preview"),
            new MemoryDrafts());
        var projects = new ProjectsViewModel(new SampleCatalog(), new NoVerification());
        var sampleChat = Path.Combine(logRoot, "sample-pdf-chat", "conversation.jsonl");
        var shell = new ShellViewModel(chat, projects, null!, () => [new ChatSessionInfo(sampleChat, "無効票は全部で何枚あった？", DateTimeOffset.Now.AddHours(-1), 4)]);
        var window = new MainWindow
        {
            DataContext = shell,
            Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false,
            Width = 1180, Height = 780, WindowStartupLocation = WindowStartupLocation.Manual
        };
        window.Show();
        await Settle();
        Save(window, Path.Combine(outputFolder, "chat-empty.png"));

        chat.Input = "ブラウザーで使うメモ帳を作って。保存はこのPCの中だけで。";
        await chat.SendNowAsync();
        await Settle();
        chat.Input = "ボタンをもう少し大きくしたい";
        await Settle();
        Save(window, Path.Combine(outputFolder, "chat-proposal.png"));

        // 4-5: the library shelf with one sample library (the shelf lives in the output folder).
        var sampleFiles = Path.Combine(outputFolder, "sample-library-files");
        Directory.CreateDirectory(sampleFiles);
        var sampleScript = Path.Combine(sampleFiles, "tinybar.js");
        File.WriteAllText(sampleScript, "window.TinyBar = { draw: function (element, values) { element.textContent = values.join(\u0027 | \u0027); } };\n", new System.Text.UTF8Encoding(false));
        var shelf = new LibraryShelf(Path.Combine(outputFolder, "sample-shelf-" + Guid.NewGuid().ToString("N")[..8]));
        shelf.Add(new LibraryDraft("tinybar", "1.0.0", "MIT", "社内配布（見本）", "TinyBar", [sampleScript], "TinyBar.draw(要素, 数値の配列) で値を並べて表示する。"));
        using var http = new System.Net.Http.HttpClient();
        var library = new Views.LibraryWindow(shelf, new NpmLibrarySource(http, Path.Combine(outputFolder, "downloads")), null)
        {
            Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual
        };
        library.Show();
        await Settle();
        Save(library, Path.Combine(outputFolder, "library-shelf.png"));
        library.Close();
        window.Close();
    }

    private static async Task Settle()
    {
        for (var i = 0; i < 5; i++) await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        await Task.Delay(300);
    }

    private static void Save(Window window, string path)
    {
        var root = (FrameworkElement)window.Content;
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        }
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private sealed class SamplePlanner : IPlanningEngine
    {
        public Task<StudioBlueprint> PlanAsync(string request, StudioProjectKind kind, string model, IProgress<PlanningProgress>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new StudioBlueprint("メモ帳", "メモを1行ずつ追加して一覧表示し、このPCのブラウザー内に保存するメモ帳です。", "app",
                ["メモを入力して追加できる", "追加したメモを一覧で表示する", "メモはこのPCのブラウザー内だけに保存する", "メモを1件ずつ削除できる"],
                [new BlueprintStep("画面", "入力欄と一覧を作る")], [], [], ["workspace.read", "workspace.write"]));
    }

    private sealed class SampleClassifier : IOutputKindClassifier
    {
        public Task<OutputKindJudgement> ClassifyAsync(string request, string model, CancellationToken cancellationToken = default) =>
            Task.FromResult(new OutputKindJudgement(StudioProjectKind.DesktopOrWebApp, 0.9, ""));
    }

    private sealed class UnusedScaffolder : IProjectScaffolder
    {
        public Task<string> CreateAsync(StudioBlueprint blueprint, StudioProjectKind kind, CancellationToken cancellationToken = default, IProgress<ProjectCreationProgress>? progress = null, ProjectDerivation? derivation = null, string? sourceRequest = null, IGenerationSteering? steering = null) =>
            throw new InvalidOperationException("preview");
    }

    private sealed class MemoryDrafts : IStudioDraftStore
    {
        public StudioDraft Load() => new("", StudioProjectKind.DesktopOrWebApp, DateTimeOffset.Now);
        public Task SaveAsync(StudioDraft draft, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class SampleCatalog : IProjectCatalog
    {
        public IReadOnlyList<LocalProjectInfo> GetProjects()
        {
            var now = DateTimeOffset.Now;
            return
            [
                new("家計簿", "", "DesktopOrWebApp", "structural-preview", "generated-local-web-app", [], @"C:\preview\kakeibo", now.AddHours(-2), now),
                new("シートの重複を削除", "", "GoogleAppsScript", "structural-preview", "generated-apps-script", [], @"C:\preview\dedupe", now.AddDays(-1), now),
                new("ストップウォッチ", "", "DesktopOrWebApp", "user-verified", "generated-local-web-app", [], @"C:\preview\stopwatch", now.AddDays(-3), now)
            ];
        }
    }

    private sealed class NoVerification : IProjectVerificationStatusStore
    {
        public ProjectVerificationState Load(string projectPath) => new(false, "none", "プレビュー", "", []);
        public Task<ProjectVerificationState> RecordAsync(string projectPath, string requirementId, ManualFeatureStatus status, CancellationToken cancellationToken = default) =>
            Task.FromResult(Load(projectPath));
    }
}
