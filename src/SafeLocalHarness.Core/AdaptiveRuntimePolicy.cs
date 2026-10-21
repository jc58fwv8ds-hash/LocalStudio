using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace SafeLocalHarness.Core;

public enum RuntimeResourceBand
{
    DeterministicOnly,
    Lightweight,
    Standard,
    HighPerformance
}

public sealed record LocalRuntimeAvailability(string Runtime, bool Available, string? ExecutablePath);

public sealed record HardwareSnapshot(
    int SchemaVersion,
    DateTimeOffset CapturedAt,
    int LogicalProcessorCount,
    long InstalledMemoryBytes,
    long AvailableMemoryBytes,
    long FreeDiskBytes,
    IReadOnlyList<string> DisplayAdapters,
    string NpuStatus,
    IReadOnlyList<LocalRuntimeAvailability> LocalRuntimes,
    string Architecture = "unknown",
    long? DedicatedVideoMemoryBytes = null,
    long? AvailablePageFileBytes = null,
    long? PhysicallyInstalledMemoryBytes = null);

public sealed record RuntimeResourcePlan(
    RuntimeResourceBand Band,
    string? RecommendedModel,
    int ContextTokens,
    int InferenceThreads,
    int HeavyModelConcurrency,
    int TestConcurrency,
    bool AllowBackgroundHeavyVerification,
    string OllamaKeepAlive,
    IReadOnlyList<string> Reasons,
    ResourceMode Mode = ResourceMode.Coexistence,
    long MemoryBudgetBytes = 0,
    long MemoryReserveBytes = 0,
    string KvCacheType = ModelMemoryEstimator.DefaultKvCacheType);

public static class SystemHardwareProbe
{
    public static HardwareSnapshot Capture(AppPaths paths)
    {
        paths.EnsureCreated();
        var memory = ReadPhysicalMemory();
        var drive = new DriveInfo(Path.GetPathRoot(paths.Root) ?? paths.Root);
        var managedOllamaRoot = Path.Combine(paths.RuntimeRoot, "ollama", "stable", "content");
        var managedOllama = Directory.Exists(managedOllamaRoot)
            ? Directory.EnumerateFiles(managedOllamaRoot, "ollama.exe", SearchOption.AllDirectories).FirstOrDefault()
            : null;
        var officialOllama = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Ollama", "ollama.exe");
        var externalOllama = File.Exists(officialOllama) ? officialOllama : null;
        var llamaPath = FindRuntime(paths.RuntimeRoot, "llama-server.exe");
        return new(
            1,
            DateTimeOffset.UtcNow,
            Environment.ProcessorCount,
            memory.Total,
            memory.Available,
            drive.AvailableFreeSpace,
            ReadDisplayAdapters(),
            "unknown-without-vendor-specific-probe",
            [
                new("ollama-managed", managedOllama is not null, ToPortableRuntimeDescription(managedOllama)),
                new("ollama-external-read-only", externalOllama is not null, ToPortableRuntimeDescription(externalOllama)),
                new("llama.cpp", llamaPath is not null, ToPortableRuntimeDescription(llamaPath)),
                new("mlx", false, null)
            ],
            RuntimeInformation.OSArchitecture.ToString(),
            null,
            memory.AvailablePageFile,
            ReadPhysicallyInstalledMemory());
    }

    /// <summary>Installed RAM as reported by firmware (e.g. exactly 16 GiB), unlike the slightly smaller OS-visible total.</summary>
    private static long? ReadPhysicallyInstalledMemory() =>
        OperatingSystem.IsWindows() && GetPhysicallyInstalledSystemMemory(out var kilobytes) && kilobytes > 0
            ? checked((long)kilobytes * 1024)
            : null;

    public static async Task SaveAsync(AppPaths paths, HardwareSnapshot snapshot, RuntimeResourcePlan plan, CancellationToken cancellationToken = default)
    {
        paths.EnsureCreated();
        var target = Path.Combine(paths.StateRoot, "hardware-profile.json");
        var json = JsonSerializer.Serialize(new { snapshot, plan }, new JsonSerializerOptions { WriteIndented = true });
        var temporary = target + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            // Callers may block a UI thread on this task at startup; never resume on the caller's context.
            await File.WriteAllTextAsync(temporary, json, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, target, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static long CaptureMemoryAvailability() => ReadPhysicalMemory().Available;

    private static (long Total, long Available, long AvailablePageFile) ReadPhysicalMemory()
    {
        if (OperatingSystem.IsWindows())
        {
            var status = new MemoryStatusEx();
            if (GlobalMemoryStatusEx(status)) return ((long)status.TotalPhysical, (long)status.AvailablePhysical, (long)status.AvailablePageFile);
        }
        var fallback = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return (fallback, Math.Max(0, fallback - GC.GetTotalMemory(false)), 0);
    }

    private static IReadOnlyList<string> ReadDisplayAdapters()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var result = new List<string>();
        for (uint index = 0; index < 16; index++)
        {
            var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevices(null, index, ref device, 0)) break;
            if (!string.IsNullOrWhiteSpace(device.DeviceString)) result.Add(device.DeviceString.Trim());
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string? FindRuntime(string root, string fileName) => Directory.Exists(root)
        ? Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories).FirstOrDefault()
        : null;

    private static string? ToPortableRuntimeDescription(string? path) => path is null
        ? null
        : Path.GetFileName(path) + " (user-space detected)";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential)]
    private sealed class MemoryStatusEx
    {
        public uint Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalMemoryInKilobytes);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string? device, uint deviceNumber, ref DisplayDevice displayDevice, uint flags);
}

public static class AdaptiveRuntimePolicy
{
    private const long GiB = 1024L * 1024 * 1024;

    // OS-visible RAM sits slightly below the installed amount (15.6 GiB on a 16 GB PC); tiers tolerate that gap.
    private const double InstalledTolerance = 0.9;

    public static long EffectiveInstalledBytes(HardwareSnapshot hardware) =>
        Math.Max(hardware.InstalledMemoryBytes, hardware.PhysicallyInstalledMemoryBytes ?? 0);

    /// <summary>
    /// The tier reflects what the PC can do (installed RAM, CPU, disk). Whether a model may run now is decided
    /// separately against the memory budget, which keeps a reserve for Windows and other work.
    /// </summary>
    public static RuntimeResourcePlan Create(HardwareSnapshot hardware, string preferredModel, ResourceMode mode = ResourceMode.Coexistence, long reclaimableBytes = 0)
    {
        var installed = EffectiveInstalledBytes(hardware);
        var cpu = hardware.LogicalProcessorCount;
        var disk = hardware.FreeDiskBytes;
        var band =
            HasInstalled(installed, 32) && cpu >= 8 && disk >= 40 * GiB ? RuntimeResourceBand.HighPerformance :
            HasInstalled(installed, 16) && cpu >= 4 && disk >= 15 * GiB ? RuntimeResourceBand.Standard :
            HasInstalled(installed, 8) && cpu >= 2 && disk >= 8 * GiB ? RuntimeResourceBand.Lightweight :
            RuntimeResourceBand.DeterministicOnly;
        var reserve = MemoryBudgetPolicy.ReserveBytes(installed, mode);
        var budget = MemoryBudgetPolicy.BudgetBytes(hardware.AvailableMemoryBytes, installed, mode);
        var reasons = new List<string>
        {
            mode switch
            {
                ResourceMode.Expert => "エキスパートモード（ユーザーが選択・この起動中のみ）",
                ResourceMode.Focus => "集中モード（ユーザーが選択）",
                _ => "共存モード（標準）"
            },
            $"メモリ 搭載 {MemoryBudgetPolicy.Gb(installed)} / 空き {MemoryBudgetPolicy.Gb(hardware.AvailableMemoryBytes)} / 予備 {MemoryBudgetPolicy.Gb(reserve)} / AIの予算 {MemoryBudgetPolicy.Gb(budget)}",
            $"CPU {cpu} 論理プロセッサ",
            $"ディスク空き {MemoryBudgetPolicy.Gb(disk)}"
        };

        var context = band switch
        {
            RuntimeResourceBand.HighPerformance => 16_384,
            RuntimeResourceBand.Standard => 8_192,
            RuntimeResourceBand.Lightweight => 4_096,
            _ => 0
        };
        // A chosen model no larger (in memory) than the default is honoured on any band; larger ones only on high-end PCs.
        var preferredIsLighter = SettingsStore.IsAllowedModel(preferredModel) &&
                                 ModelMemoryEstimator.RequiredBytes(preferredModel, 4096) <= ModelMemoryEstimator.RequiredBytes(LocalStudioSettings.DefaultModel, 4096);
        string? recommended = band switch
        {
            RuntimeResourceBand.DeterministicOnly => null,
            RuntimeResourceBand.HighPerformance => preferredModel,
            _ when preferredIsLighter => preferredModel,
            RuntimeResourceBand.Lightweight => LocalStudioSettings.LightweightModel,
            _ => LocalStudioSettings.DefaultModel
        };
        if (band == RuntimeResourceBand.DeterministicOnly)
        {
            reasons.Add("搭載メモリ・CPU・ディスクのいずれかがAIの最低条件を満たしません。");
        }
        else
        {
            // Degrade to a smaller model when the preferred one does not fit; never exceed the budget.
            // The 1.5B coder is the last step down: on a busy 16 GB PC it scored about 4x the 7B (bench 2026-10-09).
            var fitting = new[] { recommended!, LocalStudioSettings.DefaultModel, LocalStudioSettings.LightweightModel }
                .Distinct(StringComparer.OrdinalIgnoreCase)
                // Memory other apps can give back when the model loads counts too (user, 2026-10-14: AI first while it runs).
                .FirstOrDefault(model => ModelMemoryEstimator.RequiredBytes(model, context) <= budget + Math.Max(0, reclaimableBytes));
            if (fitting is null)
            {
                reasons.Add($"いまの予算 {MemoryBudgetPolicy.Gb(budget)} では最小のモデル（必要 約{MemoryBudgetPolicy.Gb(ModelMemoryEstimator.RequiredBytes(LocalStudioSettings.LightweightModel, context))}）も安全に動かせないため、AIを一時停止します。他のアプリを閉じるか、集中モードを選ぶと使える場合があります。");
                band = RuntimeResourceBand.DeterministicOnly;
                recommended = null;
                context = 0;
            }
            else if (!fitting.Equals(recommended, StringComparison.OrdinalIgnoreCase))
            {
                reasons.Add($"予算に合わせて、より小さいモデル {fitting} に切り替えました。");
                recommended = fitting;
            }
        }

        var threads = band switch
        {
            RuntimeResourceBand.HighPerformance => Math.Clamp(cpu - 2, 2, 8),
            RuntimeResourceBand.Standard => Math.Clamp(cpu - 2, 2, 4),
            RuntimeResourceBand.Lightweight => Math.Min(2, cpu),
            _ => 1
        };
        var focusHigh = band == RuntimeResourceBand.HighPerformance && mode is ResourceMode.Focus or ResourceMode.Expert;
        return new(
            band,
            recommended,
            context,
            threads,
            focusHigh ? 2 : band == RuntimeResourceBand.DeterministicOnly ? 0 : 1,
            focusHigh ? Math.Clamp(cpu / 2, 2, 6) : 1,
            focusHigh && budget >= 20 * GiB,
            mode is ResourceMode.Focus or ResourceMode.Expert ? "5m" : "30s",
            reasons,
            mode,
            budget,
            reserve);
    }

    /// <summary>True when the model's estimated resident memory fits the budget measured from this snapshot.</summary>
    public static bool ModelFitsBudget(HardwareSnapshot hardware, RuntimeResourcePlan plan, string model) =>
        ModelMemoryEstimator.RequiredBytes(model, plan.ContextTokens) <=
        MemoryBudgetPolicy.BudgetBytes(hardware.AvailableMemoryBytes, EffectiveInstalledBytes(hardware), plan.Mode);

    private static bool HasInstalled(long installedBytes, long nominalGiB) =>
        installedBytes >= (long)(nominalGiB * GiB * InstalledTolerance);
}
