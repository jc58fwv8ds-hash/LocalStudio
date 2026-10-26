namespace SafeLocalHarness.Core;

public sealed record AppPaths(
    string Root,
    string RuntimeRoot,
    string ModelRoot,
    string StateRoot,
    string LogRoot,
    string DownloadRoot,
    string ProjectRoot)
{
    public static AppPaths ForCurrentUser()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
            throw new InvalidOperationException("ユーザー用保存先を取得できませんでした。");

        var root = Path.Combine(local, "LocalStudio");
        return new(root,
            Path.Combine(root, "runtime"),
            Path.Combine(root, "models"),
            Path.Combine(root, "state"),
            Path.Combine(root, "logs"),
            Path.Combine(root, "downloads"),
            Path.Combine(root, "projects"));
    }

    public void EnsureCreated()
    {
        UserSpaceGuard.EnsureAllowed(Root);
        foreach (var path in new[] { Root, RuntimeRoot, ModelRoot, StateRoot, LogRoot, DownloadRoot, ProjectRoot })
            Directory.CreateDirectory(path);
    }

    public IReadOnlyList<string> QuarantineAbandonedProjectStaging(TimeSpan? minimumAge = null)
    {
        EnsureCreated();
        var age = minimumAge ?? TimeSpan.FromHours(24);
        var now = DateTime.UtcNow;
        var quarantineRoot = Path.Combine(StateRoot, "abandoned-staging");
        var moved = new List<string>();
        foreach (var staging in Directory.EnumerateDirectories(ProjectRoot, ".staging-*", SearchOption.TopDirectoryOnly))
        {
            var attributes = File.GetAttributes(staging);
            if (attributes.HasFlag(FileAttributes.ReparsePoint) || now - Directory.GetLastWriteTimeUtc(staging) < age) continue;
            UserSpaceGuard.EnsureAllowed(staging);
            Directory.CreateDirectory(quarantineRoot);
            var destination = Path.Combine(
                quarantineRoot,
                Path.GetFileName(staging) + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"));
            UserSpaceGuard.EnsureAllowed(destination);
            Directory.Move(staging, destination);
            moved.Add(destination);
        }
        return moved;
    }
}
