using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace SafeLocalHarness.Core;

/// <summary>
/// A built Windows app is started and used before it is accepted, as web apps are (trial 2026-10-14: a memo EXE
/// compiled cleanly, yet its timer wiped what was typed every second and nothing was saved on closing). The app runs
/// minimised inside a job that ends it, and is driven only through window messages to its own controls (no keyboard,
/// no focus), so nothing reaches the user's other windows:
/// it must open without stopping; text typed into an input must still be there a moment later; each button is pressed
/// and must not stop the app or raise an error dialog; captions must not be the code's own names (buttonOpenButton);
/// and when the request asks for keeping data, what was typed must be there when the app is opened again.
/// </summary>
public static class WindowsAppBehaviorChecker
{
    public sealed record Report(IReadOnlyList<string> Problems, IReadOnlyList<string> Notes);

    private static readonly Regex KeepWords = new("保存|残る|残す|閉じても|次に開|再起動|次回", RegexOptions.CultureInvariant);
    private static readonly Regex ExitWords = new("終了|閉じる|やめる|exit|quit|close", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private const string Typed = "LS確認 123";

    public static async Task<Report> CheckAsync(string exePath, string requestText, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(exePath)) return new([], ["Windows の動作確認は行っていません"]);
        var problems = new List<string>();
        var notes = new List<string>();
        var keep = KeepWords.IsMatch(requestText ?? "");
        await using (var first = await RunningApp.StartAsync(exePath, cancellationToken))
        {
            if (first.StartProblem is { } start) return new([start], notes);
            var edits = first.Controls("EDIT").Where(edit => (GetWindowLong(edit, -16) & 0x0800) == 0).ToArray(); // not ES_READONLY
            foreach (var edit in edits) SetText(edit, Typed);
            if (edits.Length > 0)
            {
                await Task.Delay(2500, cancellationToken);
                if (edits.Any(edit => !TextOf(edit).Contains(Typed, StringComparison.Ordinal)))
                    problems.Add("exe.input-overwritten: 入力欄に書いた文字が、数秒のうちに消えたり書き換わったりします");
            }
            foreach (var button in first.Controls("BUTTON"))
            {
                var caption = TextOf(button);
                if (Regex.IsMatch(caption, @"^(?:[a-z]\w*(?:Button|Btn)|button\d*|btn\w*)$"))
                    problems.Add($"exe.internal-caption:{caption}: ボタンに、利用者向けの名前ではなくプログラムの名前が表示されています");
            }
            // Keeping data: a save-like button is pressed, then the app is closed and opened again.
            if (keep && edits.Length > 0)
            {
                foreach (var save in first.Controls("BUTTON").Where(button => Regex.IsMatch(TextOf(button), "保存|save", RegexOptions.IgnoreCase)))
                    await first.PressAsync(save, cancellationToken);
                await first.CloseAsync(cancellationToken);
            }
        }
        if (keep && problems.Count == 0)
        {
            await using var second = await RunningApp.StartAsync(exePath, cancellationToken);
            if (second.StartProblem is null)
            {
                var edits = second.Controls("EDIT").ToArray();
                if (edits.Length > 0 && !edits.Any(edit => TextOf(edit).Contains(Typed, StringComparison.Ordinal)))
                    problems.Add("exe.not-kept: 書いた内容が、アプリを閉じて開き直すと残っていません");
            }
        }
        // Every button is pressed once on a fresh start (closing ones last).
        await using (var run = await RunningApp.StartAsync(exePath, cancellationToken))
        {
            if (run.StartProblem is { } start) return new(problems.Append(start).ToArray(), notes);
            var buttons = run.Controls("BUTTON").OrderBy(button => ExitWords.IsMatch(TextOf(button)) ? 1 : 0).ToArray();
            foreach (var button in buttons)
            {
                var caption = TextOf(button);
                if (run.Exited) break;
                var failure = await run.PressAsync(button, cancellationToken);
                if (failure is not null) problems.Add($"exe.click-error:{caption}: {failure}");
                if (run.Exited && !ExitWords.IsMatch(caption)) { problems.Add($"exe.click-exit:{caption}: 押すとアプリが終了しました"); break; }
            }
            if (buttons.Length == 0 && run.Controls("EDIT").Length == 0) notes.Add("ボタンや入力欄がない画面（描画やキー操作のアプリ）のため、起動のみ確かめました");
        }
        return new(problems, notes);
    }

    /// <summary>One run of the app, minimised, owned by a job that ends it when the run is disposed.</summary>
    private sealed class RunningApp : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly WindowsProcessJob? _job;
        private IntPtr _window;

        private RunningApp(Process process, WindowsProcessJob? job) { _process = process; _job = job; }

        public string? StartProblem { get; private set; }
        public bool Exited => _process.HasExited;

        public static async Task<RunningApp> StartAsync(string exePath, CancellationToken cancellationToken)
        {
            var process = Process.Start(new ProcessStartInfo(exePath)
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Minimized,
                WorkingDirectory = Path.GetDirectoryName(exePath)!
            }) ?? throw new InvalidOperationException("作ったアプリを起動できませんでした。");
            WindowsProcessJob? job = null;
            try { job = WindowsProcessJob.Attach(process); } catch (System.ComponentModel.Win32Exception) { }
            var app = new RunningApp(process, job);
            for (var waited = 0; waited < 10_000 && app._window == IntPtr.Zero; waited += 200)
            {
                await Task.Delay(200, cancellationToken);
                if (process.HasExited) { app.StartProblem = $"exe.start-exit: 起動するとすぐに終了します（終了コード {process.ExitCode}）"; return app; }
                process.Refresh();
                app._window = process.MainWindowHandle;
            }
            if (app._window == IntPtr.Zero) { app.StartProblem = "exe.no-window: 起動しても画面が表示されません"; return app; }
            await Task.Delay(800, cancellationToken);
            if (app.ErrorDialog() is { } error) app.StartProblem = "exe.start-error: 起動するとエラーが表示されます（" + error + "）";
            return app;
        }

        public IntPtr[] Controls(string kind)
        {
            var found = new List<IntPtr>();
            if (_window == IntPtr.Zero || _process.HasExited) return [];
            EnumChildWindows(_window, (child, _) =>
            {
                var name = new StringBuilder(256);
                GetClassName(child, name, name.Capacity);
                if (name.ToString().Contains(kind, StringComparison.OrdinalIgnoreCase) && IsWindowVisible(child)) found.Add(child);
                return true;
            }, IntPtr.Zero);
            return found.ToArray();
        }

        /// <summary>Presses a button; a dialog it opens is read (an error is returned) and closed.</summary>
        public async Task<string?> PressAsync(IntPtr button, CancellationToken cancellationToken)
        {
            PostMessage(button, 0x00F5, IntPtr.Zero, IntPtr.Zero); // BM_CLICK
            await Task.Delay(700, cancellationToken);
            if (_process.HasExited) return null;
            var error = ErrorDialog();
            CloseDialogs();
            await Task.Delay(200, cancellationToken);
            return error;
        }

        public async Task CloseAsync(CancellationToken cancellationToken)
        {
            if (_process.HasExited) return;
            PostMessage(_window, 0x0010, IntPtr.Zero, IntPtr.Zero); // WM_CLOSE
            for (var waited = 0; waited < 5000 && !_process.HasExited; waited += 200) await Task.Delay(200, cancellationToken);
        }

        /// <summary>The text of another top-level window of the app when it reports an exception; null otherwise.</summary>
        private string? ErrorDialog()
        {
            foreach (var dialog in OtherWindows())
            {
                var text = new StringBuilder(TextOf(dialog));
                EnumChildWindows(dialog, (child, _) => { text.Append(' ').Append(TextOf(child)); return true; }, IntPtr.Zero);
                var all = text.ToString();
                if (Regex.IsMatch(all, "例外|Exception|エラーが発生|error occurred|ハンドルされていない", RegexOptions.IgnoreCase))
                    return Regex.Replace(all, @"\s+", " ").Trim() is var compact && compact.Length > 160 ? compact[..160] : compact;
            }
            return null;
        }

        private void CloseDialogs()
        {
            foreach (var dialog in OtherWindows()) PostMessage(dialog, 0x0010, IntPtr.Zero, IntPtr.Zero);
        }

        private List<IntPtr> OtherWindows()
        {
            var windows = new List<IntPtr>();
            var id = (uint)_process.Id;
            EnumWindows((window, _) =>
            {
                GetWindowThreadProcessId(window, out var owner);
                if (owner == id && window != _window && IsWindowVisible(window)) windows.Add(window);
                return true;
            }, IntPtr.Zero);
            return windows;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!_process.HasExited)
                {
                    PostMessage(_window, 0x0010, IntPtr.Zero, IntPtr.Zero);
                    await Task.Delay(500);
                    if (!_process.HasExited) _process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException) { }
            finally
            {
                _job?.Dispose();
                _process.Dispose();
            }
        }
    }

    private static string TextOf(IntPtr window)
    {
        var length = (int)SendMessage(window, 0x000E, IntPtr.Zero, IntPtr.Zero); // WM_GETTEXTLENGTH
        if (length <= 0) return "";
        var text = new StringBuilder(Math.Min(length + 1, 32_000));
        SendMessageText(window, 0x000D, text.Capacity, text); // WM_GETTEXT
        return text.ToString();
    }

    private static void SetText(IntPtr window, string text) => SendMessageSet(window, 0x000C, IntPtr.Zero, text); // WM_SETTEXT

    private delegate bool EnumProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageText(IntPtr window, int message, int capacity, StringBuilder text);
    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageSet(IntPtr window, int message, IntPtr wParam, string text);
}
