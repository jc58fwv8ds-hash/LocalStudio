using System.IO;
using System.Net.Http;
using System.Windows;
using SafeLocalHarness.App.ViewModels;
using SafeLocalHarness.App.Views;
using SafeLocalHarness.Core;

namespace SafeLocalHarness.App;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    /// <summary>4-5: opens the library shelf.</summary>
    private void OnLibrariesClick(object sender, RoutedEventArgs e)
    {
        if (Application.Current is not App { LibraryShelf: { } shelf, GuideDrafter: { } drafter, LibraryDownloads: { } downloads }) return;
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        new LibraryWindow(shelf, new NpmLibrarySource(http, downloads), drafter) { Owner = this }.ShowDialog();
    }

    private void OnExportChatHtml(object sender, RoutedEventArgs e) => ExportChat(sender, html: true);

    private void OnExportChatText(object sender, RoutedEventArgs e) => ExportChat(sender, html: false);

    /// <summary>Writes the whole chat to a file the user names (the record itself is not changed).</summary>
    private void ExportChat(object sender, bool html)
    {
        if ((sender as FrameworkElement)?.DataContext is not SidebarChatItem item) return;
        var safeName = string.Concat(item.Title.TrimEnd('…').Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)).Trim();
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "チャットを書き出す",
            FileName = $"{(safeName.Length > 0 ? safeName : "チャット")}_{item.At:yyyyMMdd-HHmm}{(html ? ".html" : ".txt")}",
            Filter = html ? "HTML (*.html)|*.html" : "テキスト (*.txt)|*.txt",
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var messages = ShellViewModel.MessagesOf(item);
            File.WriteAllText(dialog.FileName, html ? ChatHistory.ToHtml(item.Title, messages) : ChatHistory.ToText(item.Title, messages), new System.Text.UTF8Encoding(true));
            MessageBox.Show(this, $"{messages.Count}件のメッセージを書き出しました。\n{dialog.FileName}", "LocalStudio", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "書き出せませんでした: " + ex.Message, "LocalStudio", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Moves the chat's folder to the recycle bin after asking (an app's folder holds the app too).</summary>
    private void OnRemoveChat(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SidebarChatItem item || DataContext is not ShellViewModel shell) return;
        if (!shell.CanRemove(item))
        {
            MessageBox.Show(this, "作業中のチャットは移せません。作業が終わってから、もう一度お試しください。", "LocalStudio", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var what = item.Project is not null
            ? $"アプリ「{item.Title}」のフォルダー（作ったアプリとチャット）をごみ箱へ移します。"
            : $"チャット「{item.Title}」をごみ箱へ移します。";
        if (MessageBox.Show(this, what + "\nごみ箱から元に戻せます。よろしいですか？", "LocalStudio", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        if (RecycleBin.MoveFolder(ShellViewModel.FolderOf(item)) is { } reason)
        {
            MessageBox.Show(this, reason, "LocalStudio", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        shell.AfterRemoved(item);
    }
}
