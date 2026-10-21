using System.IO;
using System.Runtime.InteropServices;
using SafeLocalHarness.Core;

namespace SafeLocalHarness.App;

/// <summary>
/// Removing a chat moves its folder to the Windows recycle bin (the user can restore it from there); nothing is deleted
/// for good. Only folders inside the user's own space are accepted.
/// </summary>
internal static class RecycleBin
{
    private const uint FoDelete = 0x0003;
    private const ushort FofSilent = 0x0004, FofNoConfirmation = 0x0010, FofAllowUndo = 0x0040, FofNoErrorUi = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr Hwnd;
        public uint Func;
        public string From;
        public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool AnyOperationsAborted;
        public IntPtr NameMappings;
        public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOpStruct operation);

    /// <summary>Returns null when the folder is in the recycle bin, otherwise the reason it is not.</summary>
    public static string? MoveFolder(string folder)
    {
        folder = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
        UserSpaceGuard.EnsureAllowed(folder);
        if (!Directory.Exists(folder)) return "フォルダーが見つかりません。";
        var operation = new ShFileOpStruct
        {
            Func = FoDelete,
            From = folder + "\0\0",
            Flags = (ushort)(FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi)
        };
        var result = SHFileOperation(ref operation);
        if (result != 0 || operation.AnyOperationsAborted) return $"ごみ箱に移せませんでした（コード {result}）。開いているファイルを閉じてから、もう一度お試しください。";
        return Directory.Exists(folder) ? "ごみ箱に移せませんでした。" : null;
    }
}
