using System.Security.Principal;

namespace SafeLocalHarness.Core;

public static class UserSpaceGuard
{
    public static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static void EnsureAllowed(string path)
    {
        var local = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(local, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("安全のため、導入先は現在のユーザー領域に限定されています。");
    }
}
