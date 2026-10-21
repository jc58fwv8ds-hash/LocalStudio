using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using SafeLocalHarness.App.ViewModels;

namespace SafeLocalHarness.App.Views;

public partial class ChatView : UserControl
{
    private ChatViewModel? _viewModel;

    public ChatView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null) _viewModel.Messages.CollectionChanged -= OnMessagesChanged;
            _viewModel = DataContext as ChatViewModel;
            if (_viewModel is not null) _viewModel.Messages.CollectionChanged += OnMessagesChanged;
        };
        Loaded += (_, _) => InputBox.Focus();
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.InvokeAsync(MessageScroller.ScrollToEnd, System.Windows.Threading.DispatcherPriority.Background);

    // Enter sends; Shift+Enter starts a new line (as in common chat tools).
    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || _viewModel is null) return;
        e.Handled = true;
        if (_viewModel.SendCommand.CanExecute(null)) _viewModel.SendCommand.Execute(null);
    }

    private void OnAttachClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "画像 (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg", Multiselect = true, CheckFileExists = true };
        if (dialog.ShowDialog() == true) Add(dialog.FileNames);
    }

    private void OnFolderClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "ファイル名を変えるフォルダーを選んでください", Multiselect = false };
        if (dialog.ShowDialog() != true) return;
        if (_viewModel.SetWorkFolder(dialog.FolderName) is { } reason)
            MessageBox.Show(reason, "LocalStudio", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnDocumentClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        // PDFs can be chosen several at once (joined in the order shown); Word and Excel one at a time.
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Word・Excel・PowerPoint・PDF・画像 (*.docx;*.xlsx;*.pptx;*.pdf;*.png;*.jpg;*.jpeg)|*.docx;*.xlsx;*.pptx;*.pdf;*.png;*.jpg;*.jpeg|PDF (*.pdf)|*.pdf|画像（PDFにまとめる） (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg", CheckFileExists = true, Multiselect = true };
        if (dialog.ShowDialog() != true) return;
        var picked = dialog.FileNames.Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
        // PDFs chosen one after another are added to the ones already chosen, so they can be joined (hands-on H11).
        static bool IsPdf(string path) => Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
        if (picked.All(IsPdf) && _viewModel.Documents.Count > 0 && _viewModel.Documents.All(IsPdf))
            picked = _viewModel.Documents.Concat(picked).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (_viewModel.SetDocuments(picked) is { } reason)
            MessageBox.Show(reason, "LocalStudio", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) Add(files);
    }

    private void OnCanPaste(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = true;
        e.Handled = Clipboard.ContainsImage();
        if (!e.Handled) e.ContinueRouting = true;
    }

    // A pasted screenshot is written once as PNG to the temp folder and attached like a file (only the copy is kept).
    private void OnPaste(object sender, ExecutedRoutedEventArgs e)
    {
        if (!Clipboard.ContainsImage() || Clipboard.GetImage() is not { } image)
        {
            InputBox.Paste();
            return;
        }
        var path = Path.Combine(Path.GetTempPath(), $"LocalStudio-paste-{Guid.NewGuid():N}.png");
        using (var stream = File.Create(path))
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            encoder.Save(stream);
        }
        Add([path]);
        e.Handled = true;
    }

    private void Add(IEnumerable<string> files)
    {
        if (_viewModel is null) return;
        var refused = files.Select(file => (file, reason: _viewModel.AddAttachment(file))).Where(item => item.reason is not null).ToArray();
        if (refused.Length > 0)
            MessageBox.Show(string.Join("\n", refused.Select(item => $"{Path.GetFileName(item.file)}: {item.reason}")), "LocalStudio", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
