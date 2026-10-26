using System.Collections.ObjectModel;
using System.IO;
using SafeLocalHarness.Core;

namespace SafeLocalHarness.App.ViewModels;

/// <summary>One entry of the sidebar: a project (an app made in the chat) or a saved chat that did not become one.</summary>
public sealed record SidebarChatItem(string Title, DateTimeOffset At, LocalProjectInfo? Project, ChatSessionInfo? Session)
{
    public string Key => Project?.Path ?? Session?.LogPath ?? "";
    public string KindLabel => Project is not null ? "アプリ" : "チャット";
    public string Subtitle => $"{At:MM/dd HH:mm} ・ {KindLabel}";
}

/// <summary>
/// Chat-centred shell (3-4): the sidebar lists every saved chat — projects and the chats about files, PDFs and Office
/// documents (hands-on 2026-10-13: those were saved but not listed, so they looked lost). Choosing one continues it in
/// the chat; a project also gets a small bar of actions above the chat.
/// </summary>
public sealed class ShellViewModel : ViewModelBase
{
    private readonly Func<IReadOnlyList<ChatSessionInfo>> _sessions;
    private ViewModelBase _currentPage;
    private LocalProjectInfo? _sidebarSelection;
    private SidebarChatItem? _selectedChat;
    private bool _refreshQueued;

    public ShellViewModel(ChatViewModel chat, ProjectsViewModel projects, SetupViewModel setup, Func<IReadOnlyList<ChatSessionInfo>>? sessions = null)
    {
        Chat = chat; Projects = projects; Setup = setup; _currentPage = chat;
        _sessions = sessions ?? (() => []);
        ShowChatCommand = new RelayCommand(_ => CurrentPage = Chat);
        NewChatCommand = new RelayCommand(_ => StartNewChat(), _ => Chat.NewChatCommand.CanExecute(null));
        ShowProjectDetailsCommand = new RelayCommand(_ => { Projects.SelectedProject = SidebarSelection; CurrentPage = Projects; }, _ => SidebarSelection is not null);
        ShowSetupCommand = new RelayCommand(_ => CurrentPage = Setup);
        // Kept for the details page and tests: the project list page is still reachable.
        ShowProjectsCommand = new RelayCommand(_ => { Projects.Refresh(); CurrentPage = Projects; });
        Chat.NewChatCommand.CanExecuteChanged += (_, _) => NewChatCommand.Refresh();
        Chat.ProjectChanged += path =>
        {
            Projects.Refresh(path);
            _sidebarSelection = Projects.Projects.FirstOrDefault(project => string.Equals(project.Path, path, StringComparison.OrdinalIgnoreCase));
            RefreshChats(path);
            RaiseSelection();
        };
        // A new chat appears in the list once its first message is saved.
        Chat.SessionSaved += logPath => RefreshChats(logPath);
        // "Improve this app" on the details page continues that project's conversation in the chat.
        Projects.ImprovementRequested += project => SidebarSelection = project;
        // A cartridge read in, or any other change of the project list, shows in the sidebar (once per batch of changes).
        Projects.Projects.CollectionChanged += (_, _) =>
        {
            if (_refreshQueued) return;
            _refreshQueued = true;
            System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeAsync(() => { _refreshQueued = false; RefreshChats(); }, System.Windows.Threading.DispatcherPriority.Background);
        };
        RefreshChats();
    }

    public ChatViewModel Chat { get; }
    public ProjectsViewModel Projects { get; }
    public SetupViewModel Setup { get; }
    public ObservableCollection<SidebarChatItem> Chats { get; } = [];

    public ViewModelBase CurrentPage
    {
        get => _currentPage;
        private set { if (Set(ref _currentPage, value)) Raise(nameof(ShowProjectBar)); }
    }

    public LocalProjectInfo? SidebarSelection
    {
        get => _sidebarSelection;
        set
        {
            if (!Set(ref _sidebarSelection, value)) return;
            RaiseSelection();
            if (value is null) return;
            Projects.SelectedProject = value;
            CurrentPage = Chat;
            _ = Chat.OpenProjectAsync(value.Path);
            SetSelectedChatQuietly(Chats.FirstOrDefault(item => string.Equals(item.Key, value.Path, StringComparison.OrdinalIgnoreCase)));
        }
    }

    /// <summary>The sidebar's choice: a project opens with its bar, a saved chat opens on its own.</summary>
    public SidebarChatItem? SelectedChat
    {
        get => _selectedChat;
        set
        {
            if (!Set(ref _selectedChat, value) || value is null) return;
            if (value.Project is { } project) { SidebarSelection = project; return; }
            _sidebarSelection = null;
            RaiseSelection();
            CurrentPage = Chat;
            if (value.Session is { } session) _ = Chat.OpenSessionAsync(session.LogPath);
        }
    }

    public bool ShowProjectBar => SidebarSelection is not null && ReferenceEquals(CurrentPage, Chat);
    public string ProjectBarTitle => SidebarSelection?.Name ?? "";
    public RelayCommand ShowChatCommand { get; }
    public RelayCommand NewChatCommand { get; }
    public RelayCommand ShowProjectDetailsCommand { get; }
    public RelayCommand ShowSetupCommand { get; }
    public RelayCommand ShowProjectsCommand { get; }

    /// <summary>Rebuilds the list, newest first, keeping (or moving to) the given chat without reopening it.</summary>
    public void RefreshChats(string? select = null)
    {
        var key = select ?? _selectedChat?.Key;
        // An app shows when it was made: a later file time can come from an update of the folder, not from the user (all apps showed 10/06 15:05).
        var items = Projects.Projects.Select(project => new SidebarChatItem(project.Name, project.CreatedAt, project, null))
            .Concat(_sessions().Select(session => new SidebarChatItem(session.Title, session.At, null, session)))
            .OrderByDescending(item => item.At)
            .ToArray();
        Chats.Clear();
        foreach (var item in items) Chats.Add(item);
        SetSelectedChatQuietly(key is null ? null : Chats.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>The record of a chat in the list (read only), for writing it out.</summary>
    public static IReadOnlyList<ConversationMessage> MessagesOf(SidebarChatItem item) =>
        item.Project is { } project ? ConversationLog.OpenForProject(project.Path).Messages
        : item.Session is { } session ? ConversationLog.Open(session.LogPath).Messages : [];

    /// <summary>Whether the chat can be removed now (not while it is the one being worked on).</summary>
    public bool CanRemove(SidebarChatItem item) => !(Chat.IsBusy && IsOpen(item));

    /// <summary>The folder that holds the chat: the project folder (app and chat) or the chat's own folder.</summary>
    public static string FolderOf(SidebarChatItem item) => item.Project?.Path ?? item.Session!.Folder;

    /// <summary>After the folder was moved to the recycle bin: leave it if it was open and update the list.</summary>
    public void AfterRemoved(SidebarChatItem item)
    {
        if (IsOpen(item)) StartNewChat();
        if (item.Project is not null) Projects.Refresh();
        RefreshChats();
    }

    private bool IsOpen(SidebarChatItem item) =>
        string.Equals(item.Key, Chat.OpenedProject ?? Chat.OpenedSession, StringComparison.OrdinalIgnoreCase);

    private void SetSelectedChatQuietly(SidebarChatItem? item)
    {
        _selectedChat = item;
        Raise(nameof(SelectedChat));
    }

    private void StartNewChat()
    {
        Chat.StartNewChat();
        _sidebarSelection = null;
        SetSelectedChatQuietly(null);
        RaiseSelection();
        CurrentPage = Chat;
    }

    private void RaiseSelection()
    {
        Raise(nameof(SidebarSelection));
        Raise(nameof(ShowProjectBar));
        Raise(nameof(ProjectBarTitle));
        ShowProjectDetailsCommand.Refresh();
    }
}
