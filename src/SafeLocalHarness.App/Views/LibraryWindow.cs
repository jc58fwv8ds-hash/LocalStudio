using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SafeLocalHarness.Core;

namespace SafeLocalHarness.App.Views;

/// <summary>
/// 4-5: the library shelf. Lists the libraries, adds one from files on this PC or from its official source (npm). For
/// npm, the registry entry (version, licence, address, SHA-512) is shown first and nothing is downloaded until the user
/// presses the button. The usage guide is drafted from the README and must be read and confirmed before saving.
/// </summary>
public sealed class LibraryWindow : Window
{
    private readonly LibraryShelf _shelf;
    private readonly NpmLibrarySource _npm;
    private readonly LibraryGuideDrafter? _drafter;
    private readonly ObservableCollection<string> _items = [];
    private readonly ListBox _list = new() { Height = 150, Margin = new Thickness(0, 6, 0, 10) };
    private readonly TextBox _name = Field(), _version = Field(), _license = Field(), _source = Field(), _global = Field(), _element = Field();
    private readonly TextBox _guide = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 150, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxLength = LibraryShelf.MaxGuideCharacters };
    private readonly ListBox _files = new() { Height = 80, SelectionMode = SelectionMode.Multiple };
    private readonly TextBox _npmName = Field();
    private readonly TextBlock _npmInfo = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
    private readonly Button _npmFetch = new() { Content = "この内容で取得する", IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 4, 10, 4) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private NpmPackageInfo? _pending;
    private string? _readme;

    public LibraryWindow(LibraryShelf shelf, NpmLibrarySource npm, LibraryGuideDrafter? drafter)
    {
        _shelf = shelf;
        _npm = npm;
        _drafter = drafter;
        Title = "ライブラリの棚";
        Width = 720;
        Height = 860;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        if (TryFindResource("CanvasBrush") is Brush canvas) Background = canvas;
        _list.ItemsSource = _items;

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(Heading("棚にあるライブラリ"));
        panel.Children.Add(Note("ここにあるライブラリは、依頼の中で名前を挙げると、作るアプリで使えます（例:「Chart.js で売上のグラフを」）。ファイルはアプリの中にコピーされ、インターネットからは読み込みません。"));
        panel.Children.Add(_list);

        panel.Children.Add(Heading("公式の配布元（npm）から取得"));
        var npmRow = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        var look = new Button { Content = "登録情報を見る", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(look, Dock.Right);
        npmRow.Children.Add(look);
        npmRow.Children.Add(_npmName);
        panel.Children.Add(Labelled("パッケージ名（例: chart.js）", npmRow));
        panel.Children.Add(_npmInfo);
        panel.Children.Add(_npmFetch);
        look.Click += async (_, _) => await LookAsync();
        _npmFetch.Click += async (_, _) => await FetchAsync();

        panel.Children.Add(Heading("追加するライブラリ"));
        var pick = new Button { Content = "手元のファイルを選ぶ（.js / .css）", Padding = new Thickness(10, 4, 10, 4), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 6) };
        pick.Click += (_, _) => PickFiles();
        panel.Children.Add(pick);
        panel.Children.Add(Labelled("使うファイル（選んだものだけを棚に入れます）", _files));
        panel.Children.Add(Labelled("名前（英小文字。例: chart.js）", _name));
        panel.Children.Add(Labelled("版（例: 4.4.1）", _version));
        panel.Children.Add(Labelled("ライセンス（例: MIT）", _license));
        panel.Children.Add(Labelled("入手元（URL や「社内配布」など）", _source));
        panel.Children.Add(Labelled("ブラウザでの呼び名（例: Chart）", _global));
        panel.Children.Add(Labelled("描画先の要素（Chart.js なら canvas。必要なければ空）", _element));
        var draft = new Button { Content = "使い方メモの下書きを作る（AI）", Padding = new Thickness(10, 4, 10, 4), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 4) };
        draft.IsEnabled = drafter is not null;
        draft.Click += async (_, _) => await DraftAsync();
        panel.Children.Add(draft);
        panel.Children.Add(Labelled($"使い方メモ（AI に渡します。{LibraryShelf.MaxGuideCharacters}文字まで。内容を確かめて直してください）", _guide));
        var save = new Button { Content = "確認したので棚に入れる", Padding = new Thickness(12, 6, 12, 6), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        save.Click += (_, _) => Save();
        panel.Children.Add(save);
        panel.Children.Add(_status);
        // Buttons look like the rest of LocalStudio (the chat's choice buttons; the save button as the primary action).
        foreach (var button in new[] { look, _npmFetch, pick, draft }) if (TryFindResource("ChoiceButton") is Style choice) button.Style = choice;
        if (TryFindResource("PrimaryButton") is Style primary) save.Style = primary;
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Background };
        Refresh();
    }

    private void Refresh()
    {
        _items.Clear();
        foreach (var entry in _shelf.List())
            _items.Add($"{entry.Name} {entry.Manifest.Version}（{entry.Manifest.License}）呼び名 {entry.Manifest.Global ?? "なし"} ／ {entry.Manifest.Source}");
        if (_items.Count == 0) _items.Add("（まだありません）");
    }

    private async Task LookAsync()
    {
        _pending = null;
        _npmFetch.IsEnabled = false;
        _npmInfo.Text = "登録情報を読んでいます…（" + NpmLibrarySource.MetadataUrl(_npmName.Text.Trim()) + "）";
        try
        {
            var info = await _npm.ReadAsync(_npmName.Text.Trim());
            _pending = info;
            _npmInfo.Text = $"{info.Name} {info.Version}\nライセンス: {info.License}\n取得元: {info.TarballUrl}\nSHA-512: {info.Integrity}\n" +
                            $"展開後の大きさ: {(info.UnpackedBytes is { } bytes ? $"{bytes / 1024:N0} KB" : "不明")}（{info.FileCount?.ToString() ?? "?"} ファイル）\n" +
                            "この内容でよければ「この内容で取得する」を押してください。取得したファイルは SHA-512 を照合してから使います。";
            _npmFetch.IsEnabled = true;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or InvalidDataException or System.Text.Json.JsonException or TaskCanceledException)
        {
            _npmInfo.Text = "登録情報を読めませんでした: " + ex.Message;
        }
    }

    private async Task FetchAsync()
    {
        if (_pending is not { } info) return;
        _npmFetch.IsEnabled = false;
        _status.Text = "取得して照合しています…";
        try
        {
            var fetched = await _npm.FetchAsync(info);
            _files.Items.Clear();
            foreach (var file in fetched.Candidates) _files.Items.Add(file);
            if (_files.Items.Count > 0) _files.SelectedItems.Add(_files.Items[0]);
            _name.Text = info.Name;
            _version.Text = info.Version;
            _license.Text = info.License;
            _source.Text = info.TarballUrl;
            _global.Text = fetched.DetectedGlobal ?? "";
            // A README that draws into a <canvas> suggests the element (the user can change it).
            _element.Text = fetched.Readme?.Contains("<canvas", StringComparison.OrdinalIgnoreCase) == true ? "canvas" : "";
            _readme = fetched.Readme;
            _status.Text = $"取得しました（{fetched.DownloadedBytes / 1024:N0} KB、SHA-512 一致）。使うファイルを選び、使い方メモの下書きを作って確かめてください。";
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or InvalidDataException or IOException or TaskCanceledException)
        {
            _status.Text = "取得できませんでした: " + ex.Message;
        }
    }

    private void PickFiles()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "ライブラリ (*.js;*.css)|*.js;*.css", Multiselect = true, CheckFileExists = true };
        if (dialog.ShowDialog() != true) return;
        _files.Items.Clear();
        foreach (var file in dialog.FileNames) { _files.Items.Add(file); _files.SelectedItems.Add(file); }
        var readme = dialog.FileNames.Select(file => Path.Combine(Path.GetDirectoryName(file)!, "README.md")).FirstOrDefault(File.Exists);
        _readme = readme is null ? null : File.ReadAllText(readme);
        _source.Text = "手元のファイル";
    }

    private async Task DraftAsync()
    {
        _status.Text = "使い方メモの下書きを作っています（AI）…";
        try
        {
            if (_drafter is null) return;
            _guide.Text = await _drafter.DraftAsync(_name.Text.Trim(), _version.Text.Trim(), _global.Text.Trim().Length == 0 ? null : _global.Text.Trim(), _readme);
            var ungrounded = LibraryGuideDrafter.UngroundedCode(_guide.Text, _readme);
            _status.Text = ungrounded.Count == 0
                ? "下書きを作りました。内容を確かめて、必要なら直してから「確認したので棚に入れる」を押してください。"
                : "下書きを作りました。次の行は README に見当たりません（AI が別の版の書き方を覚えている可能性があります）。公式の説明と照らして確かめてください:\n・" + string.Join("\n・", ungrounded);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or InvalidDataException or TaskCanceledException)
        {
            _status.Text = "下書きを作れませんでした（手で書いても大丈夫です）: " + ex.Message;
        }
    }

    private void Save()
    {
        var files = _files.SelectedItems.Cast<string>().ToArray();
        var draft = new LibraryDraft(_name.Text.Trim(), _version.Text.Trim(), _license.Text.Trim(), _source.Text.Trim(),
            _global.Text.Trim().Length == 0 ? null : _global.Text.Trim(), files, _guide.Text, _element.Text.Trim().Length == 0 ? null : _element.Text.Trim());
        var problems = LibraryShelf.Check(draft);
        if (problems.Count > 0) { _status.Text = "まだ棚に入れられません:\n・" + string.Join("\n・", problems); return; }
        try
        {
            var entry = _shelf.Add(draft);
            _status.Text = $"{entry.Key} を棚に入れました。";
            Refresh();
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _status.Text = "棚に入れられませんでした: " + ex.Message;
        }
    }

    private static TextBox Field() => new() { Padding = new Thickness(4, 2, 4, 2) };

    private static TextBlock Heading(string text) => new() { Text = text, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 2) };

    private static TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };

    private static StackPanel Labelled(string label, UIElement control)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 12, Opacity = 0.8 });
        panel.Children.Add(control);
        return panel;
    }
}
