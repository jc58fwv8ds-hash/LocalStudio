using System.Security.Cryptography;

namespace SafeLocalHarness.Core;

/// <summary>
/// Images attached to a chat message (decision 6, 2026-10-07: PNG/JPEG, up to 5 MB each, 4 per message). The type is
/// decided from the file's first bytes, never from its extension, and the original file is only read: a copy is kept
/// next to the conversation record (inside the project's .localstudio folder once a project exists), which is
/// outside the source fingerprint and never exported in a cartridge (decision 4).
/// </summary>
public static class ChatAttachmentPolicy
{
    public const long MaxBytes = 5L * 1024 * 1024;
    public const int MaxPerMessage = 4;
    public const string FolderName = "attachments";

    /// <summary>"image/png", "image/jpeg", or null for anything else.</summary>
    public static string? DetectMediaType(ReadOnlySpan<byte> header)
    {
        ReadOnlySpan<byte> png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (header.Length >= png.Length && header[..png.Length].SequenceEqual(png)) return "image/png";
        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF) return "image/jpeg";
        return null;
    }

    /// <summary>Null when the file can be attached; otherwise the reason in plain Japanese.</summary>
    public static string? Check(string sourcePath, int alreadyAttached)
    {
        if (alreadyAttached >= MaxPerMessage) return $"画像は1回に{MaxPerMessage}枚までです。";
        var info = new FileInfo(sourcePath);
        if (!info.Exists) return "ファイルが見つかりません。";
        if (info.Length == 0 || info.Length > MaxBytes) return $"画像は{MaxBytes / 1024 / 1024} MBまでです。";
        Span<byte> header = stackalloc byte[8];
        using (var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var read = stream.Read(header);
            if (DetectMediaType(header[..read]) is null) return "PNGかJPEGの画像だけを添付できます。";
        }
        return null;
    }

    /// <summary>Copies an image into <paramref name="logFolder"/>\attachments, named by its content hash.</summary>
    public static async Task<ConversationAttachment> StoreAsync(string sourcePath, string logFolder, int alreadyAttached, CancellationToken cancellationToken = default)
    {
        if (Check(sourcePath, alreadyAttached) is { } reason) throw new InvalidDataException(reason);
        var bytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken);
        var mediaType = DetectMediaType(bytes) ?? throw new InvalidDataException("PNGかJPEGの画像だけを添付できます。");
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var name = sha[..16] + (mediaType == "image/png" ? ".png" : ".jpg");
        var folder = Path.Combine(logFolder, FolderName);
        UserSpaceGuard.EnsureAllowed(folder);
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, name);
        if (!File.Exists(target)) await File.WriteAllBytesAsync(target, bytes, cancellationToken);
        return new ConversationAttachment(FolderName + "/" + name, mediaType, bytes.Length, sha);
    }
}
