using System.Net;
using System.Text;

namespace SafeLocalHarness.Core;

/// <summary>A saved conversation that did not become a project (file work, PDF and Office questions, unfinished plans).</summary>
public sealed record ChatSessionInfo(string LogPath, string Title, DateTimeOffset At, int MessageCount)
{
    public string Folder => Path.GetDirectoryName(LogPath)!;
}

/// <summary>
/// Every conversation is written as it happens (ConversationLog), but only those that became a project were listed, so
/// a PDF or Office conversation could not be found again and looked lost (hands-on 2026-10-13, H1). This lists the
/// others and writes any conversation out as HTML or plain text.
/// </summary>
public static class ChatHistory
{
    public const int MaxTitleCharacters = 28;

    public static IReadOnlyList<ChatSessionInfo> ListSessions(AppPaths paths)
    {
        var root = Path.Combine(paths.StateRoot, "conversations");
        if (!Directory.Exists(root)) return [];
        var sessions = new List<ChatSessionInfo>();
        foreach (var folder in Directory.EnumerateDirectories(root))
        {
            var file = Path.Combine(folder, "conversation.jsonl");
            if (!File.Exists(file)) continue;
            try
            {
                var messages = ConversationLog.Open(file).Messages;
                if (messages.Count == 0) continue;
                sessions.Add(new ChatSessionInfo(file, TitleOf(messages), messages[^1].At.ToLocalTime(), messages.Count));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return sessions.OrderByDescending(session => session.At).ToArray();
    }

    /// <summary>The first thing the user asked, on one line, without the file note the chat adds ("（PDF: …）").</summary>
    public static string TitleOf(IReadOnlyList<ConversationMessage> messages)
    {
        var first = messages.FirstOrDefault(message => message.Role == ConversationRole.User)?.Text ?? messages[0].Text;
        var line = System.Text.RegularExpressions.Regex.Replace(first, @"\s*[（(](?:PDF|ファイル|フォルダー)[:：][^）)]*[）)]\s*$", "");
        line = System.Text.RegularExpressions.Regex.Replace(line, @"\s+", " ").Trim();
        if (line.Length == 0) line = "チャット";
        return line.Length <= MaxTitleCharacters ? line : line[..MaxTitleCharacters] + "…";
    }

    public static string ToText(string title, IReadOnlyList<ConversationMessage> messages)
    {
        var text = new StringBuilder();
        text.Append(title).Append('\n').Append(new string('=', Math.Min(40, Math.Max(4, title.Length * 2)))).Append("\n\n");
        foreach (var message in messages)
        {
            text.Append('[').Append(message.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")).Append("] ")
                .Append(message.Role == ConversationRole.User ? "あなた" : "Local Studio").Append('\n')
                .Append(message.Text.Replace("\r\n", "\n")).Append('\n');
            foreach (var attachment in message.Attachments) text.Append("（添付: ").Append(attachment.RelativePath).Append("）\n");
            if (message.Choices.Count > 0) text.Append("（選択肢: ").Append(string.Join(" / ", message.Choices.Select(choice => choice.Label))).Append("）\n");
            text.Append('\n');
        }
        return text.ToString();
    }

    /// <summary>A self-contained page (no scripts, no external files); every text is HTML-encoded.</summary>
    public static string ToHtml(string title, IReadOnlyList<ConversationMessage> messages)
    {
        var html = new StringBuilder();
        html.Append("<!doctype html>\n<html lang=\"ja\"><head><meta charset=\"utf-8\">")
            .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\">")
            .Append("<title>").Append(WebUtility.HtmlEncode(title)).Append("</title><style>")
            .Append("body{font-family:'Yu Gothic UI','Segoe UI',sans-serif;background:#fff8ef;color:#2b2622;max-width:820px;margin:32px auto;padding:0 16px;line-height:1.7}")
            .Append("h1{font-size:20px}.m{margin:14px 0}.u{text-align:right}.u .b{display:inline-block;background:#eee9e3;border-radius:14px;padding:10px 14px;text-align:left}")
            .Append(".t{font-size:11px;color:#8a7f75}.x{white-space:pre-wrap}.c{font-size:12px;color:#8a7f75}</style></head><body>\n")
            .Append("<h1>").Append(WebUtility.HtmlEncode(title)).Append("</h1>\n");
        foreach (var message in messages)
        {
            var user = message.Role == ConversationRole.User;
            html.Append("<div class=\"m").Append(user ? " u" : "").Append("\"><div class=\"t\">")
                .Append(WebUtility.HtmlEncode(message.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))).Append(user ? " あなた" : " Local Studio").Append("</div>")
                .Append(user ? "<div class=\"b x\">" : "<div class=\"x\">").Append(WebUtility.HtmlEncode(message.Text)).Append("</div>");
            foreach (var attachment in message.Attachments) html.Append("<div class=\"c\">添付: ").Append(WebUtility.HtmlEncode(attachment.RelativePath)).Append("</div>");
            if (message.Choices.Count > 0) html.Append("<div class=\"c\">選択肢: ").Append(WebUtility.HtmlEncode(string.Join(" / ", message.Choices.Select(choice => choice.Label)))).Append("</div>");
            html.Append("</div>\n");
        }
        return html.Append("</body></html>\n").ToString();
    }
}
