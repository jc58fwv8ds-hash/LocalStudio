using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SafeLocalHarness.Core;

/// <summary>The working copy of one set of chosen files: its versions and the files of the current one.</summary>
public sealed record WorkingSet(string Key, IReadOnlyList<string> Originals, int Version, IReadOnlyList<string> Current)
{
    public bool Edited => Version > 0;
}

/// <summary>
/// Files chosen in a chat are edited on copies kept with the chat (hands-on 2026-10-13, the user's design): the first
/// request copies the originals to version 0, each edit becomes the next version, later requests (questions too) work
/// on the latest version, and only when the user says so the latest version is saved next to the originals as
/// "name_edited.ext" ("name_edited(2).ext" when that exists). Originals are only read; nothing is overwritten.
/// </summary>
/// <remarks>
/// Layout: &lt;root&gt;\&lt;key&gt;\originals.txt, current.txt and v0, v1, … with the files of each version. Earlier versions stay,
/// so "1つ前に戻す" only moves the pointer back.
/// </remarks>
public sealed class WorkingCopies(string root)
{
    public const string EditedSuffix = "_edited";

    public string Root { get; } = Path.GetFullPath(root);

    public static string KeyOf(IReadOnlyList<string> originals) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", originals.Select(path => Path.GetFullPath(path).ToUpperInvariant())))))[..16];

    /// <summary>The working set of these originals, made (version 0) on first use.</summary>
    public WorkingSet Prepare(IReadOnlyList<string> originals)
    {
        if (originals.Count == 0) throw new ArgumentException("ファイルが選ばれていません。", nameof(originals));
        if (Find(KeyOf(originals)) is { } existing && existing.Current.All(File.Exists)) return existing;
        var folder = SetFolder(KeyOf(originals));
        UserSpaceGuard.EnsureAllowed(folder);
        var first = Path.Combine(folder, "v0");
        Directory.CreateDirectory(first);
        foreach (var original in originals)
        {
            // Read even while Word or Excel has the file open (it was refused before: hands-on H12).
            using var source = new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var copy = new FileStream(Path.Combine(first, Path.GetFileName(original)), FileMode.Create, FileAccess.Write, FileShare.None);
            source.CopyTo(copy);
        }
        File.WriteAllLines(Path.Combine(folder, "originals.txt"), originals.Select(Path.GetFullPath), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(folder, "current.txt"), "0");
        return Find(KeyOf(originals))!;
    }

    public WorkingSet? Find(string key)
    {
        var folder = SetFolder(key);
        var list = Path.Combine(folder, "originals.txt");
        if (!File.Exists(list)) return null;
        var version = int.TryParse(File.ReadAllText(Path.Combine(folder, "current.txt")).Trim(), out var number) ? number : 0;
        var versionFolder = Path.Combine(folder, "v" + version);
        var current = Directory.Exists(versionFolder) ? Directory.GetFiles(versionFolder).Order(StringComparer.OrdinalIgnoreCase).ToArray() : [];
        return new WorkingSet(key, File.ReadAllLines(list).Where(line => line.Length > 0).ToArray(), version, current);
    }

    /// <summary>Makes the files an edit wrote (next to the current version) the next version.</summary>
    public WorkingSet Commit(string key, IReadOnlyList<string> outputs)
    {
        var set = Find(key) ?? throw new InvalidOperationException("作業用のコピーが見つかりません。");
        var folder = SetFolder(key);
        var next = Path.Combine(folder, "v" + (set.Version + 1));
        if (Directory.Exists(next)) Directory.Move(next, next + "-old-" + DateTime.Now.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(next);
        foreach (var output in outputs)
        {
            var name = outputs.Count == 1 && set.Originals.Count == 1 && Path.GetExtension(output).Equals(Path.GetExtension(set.Originals[0]), StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileName(set.Originals[0])
                : CleanName(Path.GetFileName(output));
            File.Move(output, Path.Combine(next, name), overwrite: false);
        }
        File.WriteAllText(Path.Combine(folder, "current.txt"), (set.Version + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Find(key)!;
    }

    /// <summary>What made a version (the edit's plan as JSON), so a later "もう少し上に" can redo it adjusted.</summary>
    public void RecordPlan(string key, int version, string json) => File.WriteAllText(Path.Combine(SetFolder(key), $"v{version}.plan.json"), json);

    public string? PlanOf(string key, int version)
    {
        var path = Path.Combine(SetFolder(key), $"v{version}.plan.json");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>Points the set at a version that exists (used to return after a failed adjustment).</summary>
    public void SetVersion(string key, int version)
    {
        if (!Directory.Exists(Path.Combine(SetFolder(key), "v" + version))) throw new InvalidOperationException("その版はありません。");
        File.WriteAllText(Path.Combine(SetFolder(key), "current.txt"), version.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Back to the version before (the later one stays on disk until replaced).</summary>
    public WorkingSet? Undo(string key)
    {
        var set = Find(key);
        if (set is null || set.Version == 0) return set;
        File.WriteAllText(Path.Combine(SetFolder(key), "current.txt"), (set.Version - 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Find(key);
    }

    /// <summary>Copies the current version next to the first original as "name_edited.ext"; returns the new paths.</summary>
    public IReadOnlyList<string> Save(string key)
    {
        var set = Find(key) ?? throw new InvalidOperationException("作業用のコピーが見つかりません。");
        if (!set.Edited) throw new InvalidOperationException("まだ変更していません。");
        var target = Path.GetDirectoryName(set.Originals[0])!;
        var saved = new List<string>();
        foreach (var file in set.Current)
        {
            var path = EditedPath(target, Path.GetFileName(file));
            File.Copy(file, path, overwrite: false);
            saved.Add(path);
        }
        return saved;
    }

    /// <summary>"name_edited.ext" in the folder, or "name_edited(2).ext" and so on when it exists.</summary>
    public static string EditedPath(string folder, string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 1; index < 1000; index++)
        {
            var candidate = Path.Combine(folder, index == 1 ? $"{stem}{EditedSuffix}{extension}" : $"{stem}{EditedSuffix}({index}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
        throw new IOException("保存する名前を決められませんでした。");
    }

    /// <summary>The editors' own output names ("…_LS編集(2).pdf", "…_LS分割_01.pdf") without their marks.</summary>
    public static string CleanName(string fileName)
    {
        var cleaned = Regex.Replace(fileName, @"_LS編集\d*(?:\(\d+\))?", "");
        cleaned = Regex.Replace(cleaned, @"_LS(分割|結合)", "_$1");
        cleaned = Regex.Replace(cleaned, @"\(\d+\)(?=\.[^.]+$)", "");
        return cleaned;
    }

    private string SetFolder(string key) => Path.Combine(Root, key);
}
