using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SafeLocalHarness.Core;

public enum ConversationRole
{
    User,
    Studio
}

public sealed record ConversationAttachment(string RelativePath, string MediaType, long Size, string Sha256);

public sealed record ConversationChoice(string Id, string Label);

public sealed record ConversationMessage(
    string Id,
    DateTimeOffset At,
    ConversationRole Role,
    string Text,
    IReadOnlyList<ConversationAttachment> Attachments,
    IReadOnlyList<ConversationChoice> Choices)
{
    public static ConversationMessage FromUser(string text, IReadOnlyList<ConversationAttachment>? attachments = null) =>
        new(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, ConversationRole.User, text, attachments ?? [], []);

    public static ConversationMessage FromStudio(string text, params ConversationChoice[] choices) =>
        new(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, ConversationRole.Studio, text, [], choices);
}

/// <summary>
/// Append-only conversation record (one JSON message per line). Before a project exists it lives under
/// state\conversations\&lt;session&gt;; after creation it moves into the project's .localstudio folder, which is outside
/// the source fingerprint and is never exported in a cartridge. Damaged lines are skipped on load.
/// </summary>
public sealed class ConversationLog
{
    public const string ProjectRelativePath = ".localstudio/conversation.jsonl";
    public const int MaxMessageCharacters = 8_000;
    private const long MaxLoadBytes = 16L * 1024 * 1024;
    private static readonly JsonSerializerOptions LineOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly List<ConversationMessage> _messages;
    private readonly SemaphoreSlim _write = new(1, 1);

    private ConversationLog(string filePath, List<ConversationMessage> messages, int skippedLines)
    {
        FilePath = filePath;
        _messages = messages;
        SkippedLines = skippedLines;
    }

    public string FilePath { get; private set; }
    public int SkippedLines { get; }

    /// <summary>The folder of the record; attachments live in its "attachments" subfolder and paths are relative to it.</summary>
    public string Folder => Path.GetDirectoryName(FilePath)!;

    public IReadOnlyList<ConversationMessage> Messages
    {
        get { lock (_messages) return _messages.ToArray(); }
    }

    public static ConversationLog CreateSession(AppPaths paths)
    {
        paths.EnsureCreated();
        var session = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}";
        return Open(Path.Combine(paths.StateRoot, "conversations", session, "conversation.jsonl"));
    }

    public static ConversationLog OpenForProject(string projectRoot) =>
        Open(Path.Combine(projectRoot, PortableProjectPath.ToPlatformPath(ProjectRelativePath)));

    public static ConversationLog Open(string filePath)
    {
        filePath = Path.GetFullPath(filePath);
        UserSpaceGuard.EnsureAllowed(filePath);
        var messages = new List<ConversationMessage>();
        var skipped = 0;
        if (File.Exists(filePath))
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var startsMidLine = stream.Length > MaxLoadBytes;
            if (startsMidLine) stream.Seek(-MaxLoadBytes, SeekOrigin.End);
            using var reader = new StreamReader(stream, new UTF8Encoding(false));
            if (startsMidLine) reader.ReadLine();
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0) continue;
                try
                {
                    var message = JsonSerializer.Deserialize<ConversationMessage>(line, LineOptions);
                    if (message is null || string.IsNullOrWhiteSpace(message.Id) || message.Text is null) { skipped++; continue; }
                    messages.Add(message with { Attachments = message.Attachments ?? [], Choices = message.Choices ?? [] });
                }
                catch (JsonException) { skipped++; }
            }
        }
        return new ConversationLog(filePath, messages, skipped);
    }

    public async Task<ConversationMessage> AppendAsync(ConversationMessage message, CancellationToken cancellationToken = default)
    {
        if (message.Text.Length > MaxMessageCharacters)
            message = message with { Text = message.Text[..MaxMessageCharacters] };
        var line = JsonSerializer.Serialize(message, LineOptions) + "\n";
        await _write.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            await using (var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                var bytes = new UTF8Encoding(false).GetBytes(line);
                await stream.WriteAsync(bytes, cancellationToken);
            }
            lock (_messages) _messages.Add(message);
        }
        finally
        {
            _write.Release();
        }
        return message;
    }

    /// <summary>Moves the session record into a newly created project; later messages are appended there.</summary>
    public async Task MoveIntoProjectAsync(string projectRoot, CancellationToken cancellationToken = default)
    {
        var target = Path.GetFullPath(Path.Combine(projectRoot, PortableProjectPath.ToPlatformPath(ProjectRelativePath)));
        UserSpaceGuard.EnsureAllowed(target);
        await _write.WaitAsync(cancellationToken);
        try
        {
            if (string.Equals(target, FilePath, StringComparison.OrdinalIgnoreCase)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var sessionFolder = Path.GetDirectoryName(FilePath)!;
            // Attached images keep their relative paths ("attachments/…") next to the record.
            var sessionAttachments = Path.Combine(sessionFolder, ChatAttachmentPolicy.FolderName);
            if (Directory.Exists(sessionAttachments))
            {
                var targetAttachments = Path.Combine(Path.GetDirectoryName(target)!, ChatAttachmentPolicy.FolderName);
                Directory.CreateDirectory(targetAttachments);
                foreach (var file in Directory.EnumerateFiles(sessionAttachments))
                {
                    var destination = Path.Combine(targetAttachments, Path.GetFileName(file));
                    if (!File.Exists(destination)) File.Copy(file, destination);
                    File.Delete(file);
                }
                Directory.Delete(sessionAttachments);
            }
            if (File.Exists(FilePath))
            {
                File.Copy(FilePath, target, overwrite: false);
                File.Delete(FilePath);
            }
            if (Directory.Exists(sessionFolder) && !Directory.EnumerateFileSystemEntries(sessionFolder).Any()) Directory.Delete(sessionFolder);
            FilePath = target;
        }
        finally
        {
            _write.Release();
        }
    }
}
