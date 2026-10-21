using System.Text;
using System.Text.Json;

namespace SafeLocalHarness.Core;

/// <summary>ResourceMode defaults to Coexistence; Focus is stored only after the user explicitly selects it.</summary>
public sealed record LocalStudioSettings(string SelectedModel, ResourceMode ResourceMode = ResourceMode.Coexistence)
{
    public const string DefaultModel = "qwen2.5-coder:7b-instruct-q3_K_S";
    // Chosen by the user for busy / 8 GB PCs after the small-model bench (2026-10-09).
    public const string LightweightModel = "qwen2.5-coder:1.5b-instruct-q4_K_M";
    // Reads screenshots (3-5); downloaded with the user's approval on 2026-10-09 (includes the image projector).
    public const string VisionModel = "qwen3.5:2b-q4_K_M";
    public static LocalStudioSettings Default => new(DefaultModel);
}

public interface ISettingsStore
{
    LocalStudioSettings Load();
    Task SaveAsync(LocalStudioSettings settings, CancellationToken cancellationToken = default);
}

public sealed class SettingsStore(AppPaths paths) : ISettingsStore
{
    private string SettingsPath => Path.Combine(paths.StateRoot, "settings.json");

    public LocalStudioSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return LocalStudioSettings.Default;
            var value = JsonSerializer.Deserialize<LocalStudioSettings>(File.ReadAllText(SettingsPath, Encoding.UTF8));
            if (!IsAllowedModel(value?.SelectedModel)) return LocalStudioSettings.Default;
            // Only Coexistence and Focus are persistent; Expert (session only) or unknown values fall back to Coexistence.
            return value!.ResourceMode is ResourceMode.Coexistence or ResourceMode.Focus ? value : value with { ResourceMode = ResourceMode.Coexistence };
        }
        catch (JsonException) { return LocalStudioSettings.Default; }
        catch (IOException) { return LocalStudioSettings.Default; }
    }

    public async Task SaveAsync(LocalStudioSettings settings, CancellationToken cancellationToken = default)
    {
        if (!IsAllowedModel(settings.SelectedModel)) throw new InvalidDataException("未対応のモデルは設定へ保存できません。");
        if (settings.ResourceMode is not (ResourceMode.Coexistence or ResourceMode.Focus))
            throw new InvalidDataException("エキスパートモードは保存しません（起動中のみ有効です）。");
        paths.EnsureCreated();
        var temporary = SettingsPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(temporary, json, new UTF8Encoding(false), cancellationToken);
            File.Move(temporary, SettingsPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    // Smaller candidates for shared office PCs (approved 2026-10-09; Apache-2.0; pins in ManagedModelManager).
    public static bool IsAllowedModel(string? model) => model is
        "qwen2.5-coder:7b-instruct-q3_K_S" or "qwen2.5-coder:14b-instruct-q4_K_M" or
        "qwen3.5:2b-q4_K_M" or "qwen3.5:4b-q4_K_M" or "qwen2.5-coder:1.5b-instruct-q4_K_M";
}
