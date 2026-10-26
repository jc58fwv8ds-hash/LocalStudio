using System.Text;
using System.Text.Json;

namespace SafeLocalHarness.Core;

public sealed record StudioDraft(string Request, StudioProjectKind Kind, DateTimeOffset SavedAt)
{
    public static StudioDraft Empty => new("", StudioProjectKind.DesktopOrWebApp, DateTimeOffset.MinValue);
}

public interface IStudioDraftStore
{
    StudioDraft Load();
    Task SaveAsync(StudioDraft draft, CancellationToken cancellationToken = default);
}

public sealed class StudioDraftStore(AppPaths paths) : IStudioDraftStore
{
    private string DraftPath => Path.Combine(paths.StateRoot, "studio-draft.json");

    public StudioDraft Load()
    {
        try
        {
            if (!File.Exists(DraftPath)) return StudioDraft.Empty;
            var draft = JsonSerializer.Deserialize<StudioDraft>(File.ReadAllText(DraftPath, Encoding.UTF8));
            if (draft is null || draft.Request.Length > 8000 || !Enum.IsDefined(draft.Kind)) return StudioDraft.Empty;
            return draft;
        }
        catch (JsonException) { return StudioDraft.Empty; }
        catch (IOException) { return StudioDraft.Empty; }
    }

    public async Task SaveAsync(StudioDraft draft, CancellationToken cancellationToken = default)
    {
        if (draft.Request.Length > 8000) throw new InvalidDataException("入力は8000文字以内で保存してください。");
        paths.EnsureCreated();
        UserSpaceGuard.EnsureAllowed(DraftPath);
        var temporary = DraftPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var json = JsonSerializer.Serialize(draft, new JsonSerializerOptions { WriteIndented = true });
            // The final draft is saved while the UI thread waits at shutdown; never resume on the caller's context.
            await File.WriteAllTextAsync(temporary, json, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, DraftPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public sealed record LocalProjectInfo(
    string Name,
    string Summary,
    string Kind,
    string Lifecycle,
    string ImplementationMode,
    IReadOnlyList<string> Features,
    string Path,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastModified)
{
    public string LifecycleLabel => Lifecycle switch
    {
        "user-verified" => "操作確認済み",
        "partial" => "一部確認済み",
        "failed" or "verification-failed" => "問題あり",
        "stale" => "再確認が必要",
        "pending" or "structural-preview" => "操作確認待ち",
        "verified-project" => "検証済みテンプレート",
        "design-scaffold" => "安全な設計",
        _ => Lifecycle
    };
}

public interface IProjectCatalog
{
    IReadOnlyList<LocalProjectInfo> GetProjects();
}

public sealed class ProjectCatalog(AppPaths paths) : IProjectCatalog
{
    private readonly ProjectVerificationStatusStore _verification = new(paths);

    public IReadOnlyList<LocalProjectInfo> GetProjects()
    {
        if (!Directory.Exists(paths.ProjectRoot)) return [];
        var projects = new List<LocalProjectInfo>();
        foreach (var directory in Directory.EnumerateDirectories(paths.ProjectRoot)
                     .Where(path => !Path.GetFileName(path).StartsWith(".staging-", StringComparison.OrdinalIgnoreCase)))
        {
            var info = new DirectoryInfo(directory);
            var manifestPath = Path.Combine(directory, "localstudio.json");
            var name = info.Name;
            var summary = "LocalStudioプロジェクト";
            var kind = "Unknown";
            var lifecycle = "unknown";
            var implementationMode = "unknown";
            IReadOnlyList<string> features = [];
            var createdAt = new DateTimeOffset(info.CreationTime);
            try
            {
                if (File.Exists(manifestPath))
                {
                    using var json = JsonDocument.Parse(File.ReadAllText(manifestPath, Encoding.UTF8));
                    var root = json.RootElement;
                    if (root.TryGetProperty("createdAt", out var created) && created.TryGetDateTimeOffset(out var parsed)) createdAt = parsed;
                    if (root.TryGetProperty("projectKind", out var projectKind)) kind = projectKind.GetString() ?? kind;
                    if (root.TryGetProperty("lifecycle", out var life)) lifecycle = life.GetString() ?? lifecycle;
                    if (root.TryGetProperty("implementationMode", out var mode)) implementationMode = mode.GetString() ?? implementationMode;
                    if (root.TryGetProperty("blueprint", out var blueprint))
                    {
                        if (blueprint.TryGetProperty("name", out var projectName)) name = projectName.GetString() ?? name;
                        if (blueprint.TryGetProperty("summary", out var projectSummary)) summary = projectSummary.GetString() ?? summary;
                        if (blueprint.TryGetProperty("features", out var projectFeatures) && projectFeatures.ValueKind == JsonValueKind.Array)
                            features = projectFeatures.EnumerateArray().Select(item => item.GetString() ?? "").Where(item => !string.IsNullOrWhiteSpace(item)).Take(8).ToArray();
                    }
                }
            }
            catch (JsonException) { lifecycle = "manifest-error"; }
            catch (IOException) { lifecycle = "read-error"; }
            var verification = _verification.Load(info.FullName);
            if (verification.Applicable) lifecycle = verification.Level;
            projects.Add(new(name, summary, kind, lifecycle, implementationMode, features, info.FullName, createdAt, new(info.LastWriteTime)));
        }
        return projects.OrderByDescending(project => project.CreatedAt).ToArray();
    }
}
