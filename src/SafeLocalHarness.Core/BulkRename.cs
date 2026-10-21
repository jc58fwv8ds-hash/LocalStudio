using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SafeLocalHarness.Core;

/// <summary>
/// One step of a rename plan, applied to the name without its extension (the extension never changes).
/// op: replace (find → with) | prefix (text) | suffix (text) | number (start, digits, position prefix|suffix, text = separator)
///   | date (format, position, text = separator; the file's last-modified date) | case (to lower|upper) | trim | remove (find)
///   | clear (drop the original name, e.g. before date + number)
/// </summary>
public sealed record RenameStep(
    [property: JsonPropertyName("op")] string Op,
    [property: JsonPropertyName("find")] string? Find = null,
    [property: JsonPropertyName("with")] string? With = null,
    [property: JsonPropertyName("text")] string? Text = null,
    [property: JsonPropertyName("start")] int? Start = null,
    [property: JsonPropertyName("digits")] int? Digits = null,
    [property: JsonPropertyName("position")] string? Position = null,
    [property: JsonPropertyName("format")] string? Format = null,
    [property: JsonPropertyName("to")] string? To = null);

/// <summary>Which files (top level of the chosen folder) and in what order, then the steps.</summary>
public sealed record RenamePlan(
    [property: JsonPropertyName("extension")] string? Extension,
    [property: JsonPropertyName("nameContains")] string? NameContains,
    [property: JsonPropertyName("order")] string? Order,
    [property: JsonPropertyName("steps")] IReadOnlyList<RenameStep> Steps);

public sealed record RenameItem(string OldName, string NewName, string Status)
{
    public bool Changes => Status == RenameStatus.Ok;
}

public static class RenameStatus
{
    public const string Ok = "ok";
    public const string Unchanged = "unchanged";
    public const string Conflict = "conflict";
    public const string Invalid = "invalid";
}

public sealed record RenamePreview(string Id, string Folder, RenamePlan Plan, IReadOnlyList<RenameItem> Items)
{
    public int Changing => Items.Count(item => item.Changes);
    public int Problems => Items.Count(item => item.Status is RenameStatus.Conflict or RenameStatus.Invalid);
    public bool CanApply => Changing > 0 && Problems == 0;
}

public sealed record RenamePair(string From, string To);

public sealed record RenameJournal(string Id, string Folder, DateTimeOffset At, string State, IReadOnlyList<RenamePair> Pairs);

public sealed record RenameResult(RenameJournal Journal, int Renamed, IReadOnlyList<string> Mismatches)
{
    public bool Succeeded => Mismatches.Count == 0;
}

/// <summary>
/// Bulk rename (4-1): deterministic, previewed and undoable. The AI only proposes a <see cref="RenamePlan"/>; this
/// class computes every new name, refuses anything unsafe, renames in two phases (so swaps and chains work), keeps
/// a journal for one-step undo, and then checks the folder itself: success is reported only when every file really
/// has its new name, never just because no error was thrown.
/// </summary>
public static class BulkRenamer
{
    public const int MaxFiles = 1_000;
    public const int MaxSteps = 8;
    private const string TemporaryPrefix = "~ls";

    /// <summary>Null when the folder may be used; otherwise the reason in plain Japanese.</summary>
    public static string? CheckFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return "フォルダーを選んでください。";
        var full = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Directory.Exists(full)) return "フォルダーが見つかりません。";
        if (Path.GetPathRoot(full)!.Equals(full, StringComparison.OrdinalIgnoreCase)) return "ドライブ全体は対象にできません。フォルダーを選んでください。";
        var forbidden = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        }.Where(path => !string.IsNullOrEmpty(path)).Select(path => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
        if (forbidden.Any(root => full.StartsWith(root, StringComparison.OrdinalIgnoreCase)))
            return "Windowsやアプリのフォルダーは対象にできません。";
        var profile = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (full.Equals(profile, StringComparison.OrdinalIgnoreCase)) return "ユーザーフォルダーそのものは対象にできません。中のフォルダーを選んでください。";
        return null;
    }

    /// <summary>Null when the plan can be previewed; otherwise what is wrong with it.</summary>
    public static IReadOnlyList<string> Validate(RenamePlan plan)
    {
        var errors = new List<string>();
        if (plan.Steps is null || plan.Steps.Count is 0 or > MaxSteps) { errors.Add("rename.steps"); return errors; }
        foreach (var step in plan.Steps)
        {
            switch (step?.Op)
            {
                case "replace" when !string.IsNullOrEmpty(step.Find) && step.With is not null:
                case "remove" when !string.IsNullOrEmpty(step.Find):
                case "prefix" or "suffix" when !string.IsNullOrEmpty(step.Text):
                case "case" when step.To is "lower" or "upper":
                case "trim":
                case "clear":
                    break;
                case "number" when step.Start is null or >= 0 and <= 1_000_000 && step.Digits is null or >= 1 and <= 6 && step.Position is null or "prefix" or "suffix":
                    break;
                case "date" when step.Position is null or "prefix" or "suffix" && IsDateFormat(step.Format ?? "yyyyMMdd"):
                    break;
                default:
                    errors.Add("rename.step:" + step?.Op);
                    break;
            }
        }
        if (plan.Order is not (null or "name" or "modified")) errors.Add("rename.order");
        return errors;
    }

    public static RenamePreview Preview(string folder, RenamePlan plan)
    {
        if (CheckFolder(folder) is { } reason) throw new InvalidOperationException(reason);
        if (Validate(plan) is { Count: > 0 } errors) throw new InvalidDataException("名前の付け方を確認できませんでした: " + string.Join(", ", errors));
        folder = Path.GetFullPath(folder);
        var extension = string.IsNullOrWhiteSpace(plan.Extension) ? null : "." + plan.Extension.Trim().TrimStart('.').ToLowerInvariant();
        var files = new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.TopDirectoryOnly)
            .Where(file => (file.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
            .Where(file => extension is null || file.Extension.Equals(extension, StringComparison.OrdinalIgnoreCase))
            .Where(file => string.IsNullOrEmpty(plan.NameContains) || file.Name.Contains(plan.NameContains, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (files.Count > MaxFiles) throw new InvalidOperationException($"一度に変えられるのは{MaxFiles:N0}件までです（対象 {files.Count:N0}件）。条件を絞ってください。");
        files = plan.Order == "modified"
            ? files.OrderBy(file => file.LastWriteTime).ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase).ToList()
            : files.OrderBy(file => file.Name, NaturalNameComparer.Instance).ToList();

        var proposed = files.Select((file, index) => (file.Name, New: NewName(file, index, plan.Steps))).ToList();
        var targets = proposed.GroupBy(item => item.New, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var renamedAway = proposed.Where(item => !item.Name.Equals(item.New, StringComparison.Ordinal)).Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var others = new DirectoryInfo(folder).EnumerateFileSystemInfos().Select(entry => entry.Name)
            .Where(name => !files.Any(file => file.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var items = proposed.Select(item =>
        {
            if (item.New.Equals(item.Name, StringComparison.Ordinal)) return new RenameItem(item.Name, item.New, RenameStatus.Unchanged);
            if (!IsValidName(item.New)) return new RenameItem(item.Name, item.New, RenameStatus.Invalid);
            var clash = targets[item.New] > 1 || others.Contains(item.New) ||
                        (files.Any(file => file.Name.Equals(item.New, StringComparison.OrdinalIgnoreCase)) && !renamedAway.Contains(item.New));
            return new RenameItem(item.Name, item.New, clash ? RenameStatus.Conflict : RenameStatus.Ok);
        }).ToArray();
        return new RenamePreview(Guid.NewGuid().ToString("N")[..12], folder, plan, items);
    }

    public static async Task<RenameResult> ApplyAsync(RenamePreview preview, string journalRoot, CancellationToken cancellationToken = default)
    {
        if (!preview.CanApply) throw new InvalidOperationException("問題のある名前があるため、変更しません。");
        if (CheckFolder(preview.Folder) is { } reason) throw new InvalidOperationException(reason);
        var pairs = preview.Items.Where(item => item.Changes).Select(item => new RenamePair(item.OldName, item.NewName)).ToArray();
        // The folder must still look like the preview: every source present, no new file at a target name.
        foreach (var pair in pairs)
        {
            if (!File.Exists(Path.Combine(preview.Folder, pair.From)))
                throw new InvalidOperationException($"プレビューの後で「{pair.From}」が見つからなくなったため、変更しません。もう一度プレビューしてください。");
            if (File.Exists(Path.Combine(preview.Folder, pair.To)) && !pairs.Any(other => other.From.Equals(pair.To, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"プレビューの後で「{pair.To}」ができたため、変更しません。もう一度プレビューしてください。");
        }
        var journal = new RenameJournal(preview.Id, preview.Folder, DateTimeOffset.Now, "applying", pairs);
        await SaveJournalAsync(journalRoot, journal, cancellationToken);
        var moved = new List<(string Current, string Original)>();
        try
        {
            MoveAll(preview.Folder, pairs, moved, cancellationToken);
        }
        catch
        {
            // Put every file back to its original name before reporting the failure.
            foreach (var (current, original) in Enumerable.Reverse(moved))
            {
                var from = Path.Combine(preview.Folder, current);
                var to = Path.Combine(preview.Folder, original);
                if (File.Exists(from) && !File.Exists(to)) File.Move(from, to);
            }
            await SaveJournalAsync(journalRoot, journal with { State = "rolled-back" }, CancellationToken.None);
            throw;
        }
        var mismatches = Verify(preview.Folder, pairs, after: true);
        var finalJournal = journal with { State = mismatches.Count == 0 ? "applied" : "applied-with-mismatches" };
        await SaveJournalAsync(journalRoot, finalJournal, CancellationToken.None);
        return new RenameResult(finalJournal, pairs.Length - mismatches.Count, mismatches);
    }

    /// <summary>Puts the names back (one step). Files renamed or removed since then are left alone and listed.</summary>
    public static async Task<RenameResult> UndoAsync(string journalRoot, string journalId, CancellationToken cancellationToken = default)
    {
        var journal = await LoadJournalAsync(journalRoot, journalId, cancellationToken);
        if (journal.State is not ("applied" or "applied-with-mismatches")) throw new InvalidOperationException("この変更はすでに元に戻したか、適用されていません。");
        var skipped = new List<string>();
        var restored = new List<RenamePair>();
        var currentNames = journal.Pairs.Select(pair => pair.To).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in journal.Pairs)
        {
            if (!File.Exists(Path.Combine(journal.Folder, pair.To))) { skipped.Add(pair.To + "（見つかりません）"); continue; }
            // Someone made a file with the old name since (and it is not one being renamed back): keep both, leave this one.
            if (File.Exists(Path.Combine(journal.Folder, pair.From)) && !currentNames.Contains(pair.From)) { skipped.Add(pair.From + "（同じ名前がすでにあります）"); continue; }
            restored.Add(pair);
        }
        var moved = new List<(string Current, string Original)>();
        MoveAll(journal.Folder, restored.Select(pair => new RenamePair(pair.To, pair.From)).ToArray(), moved, cancellationToken);
        var mismatches = Verify(journal.Folder, restored, after: false).Concat(skipped).ToArray();
        var undone = journal with { State = "undone" };
        await SaveJournalAsync(journalRoot, undone, CancellationToken.None);
        return new RenameResult(undone, restored.Count - Verify(journal.Folder, restored, after: false).Count, mismatches);
    }

    /// <summary>Checks the folder itself: after applying, every new name exists and every old name is gone (unless reused).</summary>
    public static IReadOnlyList<string> Verify(string folder, IReadOnlyList<RenamePair> pairs, bool after)
    {
        var mismatches = new List<string>();
        var newNames = pairs.Select(pair => after ? pair.To : pair.From).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in pairs)
        {
            var expected = after ? pair.To : pair.From;
            var gone = after ? pair.From : pair.To;
            if (!File.Exists(Path.Combine(folder, expected))) mismatches.Add(expected + "（ありません）");
            if (!newNames.Contains(gone) && File.Exists(Path.Combine(folder, gone))) mismatches.Add(gone + "（残っています）");
        }
        if (Directory.EnumerateFiles(folder, TemporaryPrefix + "*").Any(path => IsTemporaryName(Path.GetFileName(path)))) mismatches.Add("一時的な名前のファイルが残っています");
        return mismatches;
    }

    /// <summary>
    /// Renames as a person would in Explorer: directly, one file at a time, extension unchanged. Only a file whose new
    /// name is another file's current name (a swap or chain) or that changes only letter case goes through a
    /// temporary name first, and that name keeps the extension. Mass renaming to an unknown extension is what
    /// ransomware does; on 2026-10-10 the PC's antivirus restored copies of files renamed that way (duplicates).
    /// </summary>
    private static void MoveAll(string folder, IReadOnlyList<RenamePair> pairs, List<(string Current, string Original)> moved, CancellationToken cancellationToken)
    {
        var sources = pairs.Select(pair => pair.From).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var viaTemporary = pairs.Where(pair => sources.Contains(pair.To)).ToArray();
        foreach (var pair in pairs.Except(viaTemporary))
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(Path.Combine(folder, pair.From), Path.Combine(folder, pair.To));
            moved.Add((pair.To, pair.From));
        }
        var parked = new List<(RenamePair Pair, string Temp)>();
        foreach (var pair in viaTemporary)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var temp = TemporaryPrefix + Guid.NewGuid().ToString("N")[..8] + "_" + pair.From;
            File.Move(Path.Combine(folder, pair.From), Path.Combine(folder, temp));
            moved.Add((temp, pair.From));
            parked.Add((pair, temp));
        }
        foreach (var (pair, temp) in parked)
        {
            File.Move(Path.Combine(folder, temp), Path.Combine(folder, pair.To));
            var index = moved.FindIndex(entry => entry.Current == temp);
            moved[index] = (pair.To, pair.From);
        }
    }

    private static bool IsTemporaryName(string name) => Regex.IsMatch(name, "^~ls[0-9a-f]{8}_", RegexOptions.CultureInvariant);

    private static string NewName(FileInfo file, int index, IReadOnlyList<RenameStep> steps)
    {
        var extension = file.Extension;
        var name = Path.GetFileNameWithoutExtension(file.Name);
        foreach (var step in steps)
        {
            name = step.Op switch
            {
                "replace" => name.Replace(step.Find!, step.With!, StringComparison.Ordinal),
                "remove" => name.Replace(step.Find!, "", StringComparison.Ordinal),
                "prefix" => step.Text + name,
                "suffix" => name + step.Text,
                "case" => step.To == "upper" ? name.ToUpperInvariant() : name.ToLowerInvariant(),
                "trim" => Regex.Replace(name.Trim(), @"\s{2,}", " ", RegexOptions.CultureInvariant),
                "clear" => "",
                "number" => Place(name, ((step.Start ?? 1) + index).ToString(CultureInfo.InvariantCulture).PadLeft(step.Digits ?? 3, '0'), step),
                "date" => Place(name, file.LastWriteTime.ToString(step.Format ?? "yyyyMMdd", CultureInfo.InvariantCulture), step),
                _ => name
            };
        }
        return name + extension;
    }

    private static string Place(string name, string value, RenameStep step)
    {
        if (name.Length == 0) return value;
        var separator = step.Text ?? "_";
        return step.Position == "suffix" ? name + separator + value : value + separator + name;
    }

    private static bool IsDateFormat(string format) =>
        format.Length is > 0 and <= 20 && Regex.IsMatch(format, "^[yMdHms_\\-.]+$", RegexOptions.CultureInvariant);

    private static bool IsValidName(string name) =>
        name.Length is > 0 and <= 200 && !name.StartsWith('.') && ProjectFilePolicy.HasSafeWindowsNames(name) && !IsTemporaryName(name);

    private static readonly JsonSerializerOptions JournalOptions = new() { WriteIndented = true };

    private static async Task SaveJournalAsync(string journalRoot, RenameJournal journal, CancellationToken cancellationToken)
    {
        UserSpaceGuard.EnsureAllowed(journalRoot);
        Directory.CreateDirectory(journalRoot);
        var path = Path.Combine(journalRoot, "rename-" + journal.Id + ".json");
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(journal, JournalOptions), cancellationToken);
        File.Move(temporary, path, overwrite: true);
    }

    private static async Task<RenameJournal> LoadJournalAsync(string journalRoot, string id, CancellationToken cancellationToken)
    {
        if (!Regex.IsMatch(id, "^[0-9a-f]{12}$", RegexOptions.CultureInvariant)) throw new InvalidOperationException("記録が見つかりません。");
        var path = Path.Combine(journalRoot, "rename-" + id + ".json");
        if (!File.Exists(path)) throw new InvalidOperationException("記録が見つかりません。");
        return JsonSerializer.Deserialize<RenameJournal>(await File.ReadAllTextAsync(path, cancellationToken)) ?? throw new InvalidDataException("記録を読み取れません。");
    }
}

/// <summary>"file2" before "file10", the order people expect when numbering.</summary>
public sealed class NaturalNameComparer : IComparer<string>
{
    public static NaturalNameComparer Instance { get; } = new();

    public int Compare(string? left, string? right)
    {
        var a = Regex.Split(left ?? "", @"(\d+)", RegexOptions.CultureInvariant);
        var b = Regex.Split(right ?? "", @"(\d+)", RegexOptions.CultureInvariant);
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var result = long.TryParse(a[i], out var x) && long.TryParse(b[i], out var y) ? x.CompareTo(y) : string.Compare(a[i], b[i], StringComparison.OrdinalIgnoreCase);
            if (result != 0) return result;
        }
        return a.Length.CompareTo(b.Length);
    }
}
