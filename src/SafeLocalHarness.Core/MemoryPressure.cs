using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SafeLocalHarness.Core;

/// <summary>
/// Coexistence is the default. Focus widens the budget and is used only when the user explicitly selects it.
/// Expert uses all available memory, keeps only a last safety line, is confirmed by the user, and lasts for the
/// current session only (it is never persisted; the next launch starts in Coexistence).
/// </summary>
public enum ResourceMode
{
    Coexistence,
    Focus,
    Expert
}

/// <summary>The effective mode: a session-only Expert override on top of the persisted Coexistence/Focus setting.</summary>
public sealed class ResourceModeState
{
    private readonly ISettingsStore _settings;
    private volatile bool _sessionExpert;

    public ResourceModeState(ISettingsStore settings) => _settings = settings;

    public ResourceMode Current => _sessionExpert ? ResourceMode.Expert : _settings.Load().ResourceMode;

    public async Task SetAsync(ResourceMode mode, CancellationToken cancellationToken = default)
    {
        if (mode == ResourceMode.Expert)
        {
            // Persist Coexistence so that closing the app always returns to the default.
            await _settings.SaveAsync(_settings.Load() with { ResourceMode = ResourceMode.Coexistence }, cancellationToken);
            _sessionExpert = true;
            return;
        }
        _sessionExpert = false;
        await _settings.SaveAsync(_settings.Load() with { ResourceMode = mode }, cancellationToken);
    }
}

public enum MemoryPressureLevel
{
    Normal,
    Elevated,
    Critical
}

public sealed record MemoryPressureSample(
    DateTimeOffset At,
    long AvailableBytes,
    long TotalVisibleBytes,
    int CommitPercent,
    double? HardFaultsPerSecond,
    long? CompressedBytes,
    bool LowMemorySignaled);

public sealed record MemoryPressureAssessment(
    MemoryPressureLevel Level,
    bool CanStartHeavyWork,
    ResourceMode Mode,
    long AvailableBytes,
    long ReserveBytes,
    long BudgetBytes,
    IReadOnlyList<string> Reasons);

/// <summary>
/// Decides from signals observed while running, not from total or start-time free RAM alone.
/// Hard page faults also count DLL and file page-ins (measured on 2026-10-06 while free RAM stayed flat),
/// so they only count as pressure together with low free memory.
/// </summary>
public static class MemoryBudgetPolicy
{
    private const long MiB = 1024L * 1024;
    private const long GiB = 1024L * MiB;
    public const int SustainedHardFaultSamples = 5;
    public const double HardFaultsPerSecondThreshold = 500;
    public const long CompressionGrowthThresholdBytes = 256 * MiB;

    /// <summary>Always kept free for Windows and other work: the larger of a share of installed RAM or a fixed floor.</summary>
    public static long ReserveBytes(long installedBytes, ResourceMode mode) => mode switch
    {
        ResourceMode.Expert => 512 * MiB,
        ResourceMode.Focus => Math.Max(installedBytes / 16, 1 * GiB),
        _ => Math.Max(installedBytes / 10, 3 * GiB / 2)
    };

    /// <summary>Expert keeps only this last safety line: beyond it Windows itself is at risk, so model use stops.</summary>
    public const int ExpertCriticalCommitPercent = 95;

    /// <summary>
    /// Start-time allowance above "available minus reserve": Windows can still trim standby and idle working sets.
    /// Focus mode (explicit user choice) may therefore budget above the currently available memory.
    /// </summary>
    public static long StartAllowanceBytes(ResourceMode mode) => mode switch
    {
        ResourceMode.Expert => 3 * GiB,
        ResourceMode.Focus => 2 * GiB,
        _ => 512 * MiB
    };

    public static long BudgetBytes(long availableBytes, long installedBytes, ResourceMode mode) =>
        Math.Max(0, availableBytes - ReserveBytes(installedBytes, mode) + StartAllowanceBytes(mode));

    public static MemoryPressureAssessment Assess(IReadOnlyList<MemoryPressureSample> recent, long installedBytes, ResourceMode mode)
    {
        if (recent.Count == 0)
            return new(MemoryPressureLevel.Normal, true, mode, 0, ReserveBytes(installedBytes, mode), 0, ["まだ計測していません。"]);

        var last = recent[^1];
        var reserve = ReserveBytes(installedBytes, mode);
        var budget = BudgetBytes(last.AvailableBytes, installedBytes, mode);
        var critical = new List<string>();
        var elevated = new List<string>();

        var criticalCommit = mode == ResourceMode.Expert ? ExpertCriticalCommitPercent : 90;
        if (last.LowMemorySignaled) critical.Add("Windowsが低メモリ状態を通知しています。");
        if (last.CommitPercent >= criticalCommit) critical.Add($"メモリの確保量が上限の{last.CommitPercent}%に達しています。");
        if (last.AvailableBytes < reserve) critical.Add($"空きメモリ（{Gb(last.AvailableBytes)}）がOSと他の作業のための予備（{Gb(reserve)}）を下回っています。");

        if (last.CommitPercent >= 85 && last.CommitPercent < criticalCommit) elevated.Add($"メモリの確保量が上限の{last.CommitPercent}%です。");

        var compressionSamples = recent.Where(sample => sample.CompressedBytes is not null).ToArray();
        if (compressionSamples.Length >= 2 &&
            compressionSamples[^1].CompressedBytes!.Value - compressionSamples[0].CompressedBytes!.Value >= CompressionGrowthThresholdBytes &&
            last.AvailableBytes < reserve * 2)
            elevated.Add("メモリ圧縮が増え続けています。");

        var tail = recent.TakeLast(SustainedHardFaultSamples).ToArray();
        if (tail.Length == SustainedHardFaultSamples &&
            tail.All(sample => sample.HardFaultsPerSecond is >= HardFaultsPerSecondThreshold) &&
            last.AvailableBytes < reserve * 3 / 2)
            elevated.Add("空きが少ない状態でディスクからの読み戻し（ハードページフォルト）が続いています。");

        if (critical.Count > 0)
            return new(MemoryPressureLevel.Critical, false, mode, last.AvailableBytes, reserve, budget,
                [.. critical, .. elevated, "AIのモデルを解放し、新しい重い処理は開始しません。"]);
        if (elevated.Count > 0 && mode == ResourceMode.Expert)
            return new(MemoryPressureLevel.Elevated, true, mode, last.AvailableBytes, reserve, budget,
                [.. elevated, "エキスパートモードのため処理は続けます（他のアプリが遅くなる可能性があります）。"]);
        if (elevated.Count > 0)
            return new(MemoryPressureLevel.Elevated, false, mode, last.AvailableBytes, reserve, budget,
                [.. elevated, "使用量を増やさず、新しい重い処理は開始しません。"]);
        return new(MemoryPressureLevel.Normal, true, mode, last.AvailableBytes, reserve, budget, ["圧迫の兆候はありません。"]);
    }

    internal static string Gb(long bytes) => $"{bytes / (double)GiB:0.0} GB";
}

/// <summary>Approximate resident memory of a local model: weights plus runtime overhead plus KV cache for the context.</summary>
public static class ModelMemoryEstimator
{
    private const long MiB = 1024L * 1024;

    private static readonly IReadOnlyDictionary<string, (long WeightBytes, long KvBytesPerToken)> Known =
        new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase)
        {
            // Weights = the reviewed model layer size (3,492,368,896 bytes, pinned 2026-10-06).
            ["qwen2.5-coder:7b-instruct-q3_K_S"] = (3_492_368_896, 64 * 1024),
            ["qwen2.5-coder:14b-instruct-q4_K_M"] = (9_000_000_000, 200 * 1024),
            // Pinned 2026-10-09. Qwen3.5 weights include the image projector; its attention cache per token is
            // estimated conservatively (hybrid attention) until measured.
            ["qwen3.5:2b-q4_K_M"] = (1_945_769_760, 32 * 1024),
            ["qwen3.5:4b-q4_K_M"] = (3_324_162_336, 48 * 1024),
            ["qwen2.5-coder:1.5b-instruct-q4_K_M"] = (986_048_576, 28 * 1024)
        };

    /// <summary>
    /// The owned runtime keeps its attention cache (KV cache) in 8-bit, which halves that part of memory with a small
    /// quality cost (llama.cpp / Ollama OLLAMA_KV_CACHE_TYPE; it requires Flash Attention, which is enabled with it).
    /// </summary>
    public const string DefaultKvCacheType = "q8_0";

    public static long RequiredBytes(string model, int contextTokens, string kvCacheType = "f16")
    {
        var context = Math.Max(2048, contextTokens);
        if (!Known.TryGetValue(model, out var shape)) return long.MaxValue;
        var kvBytesPerToken = kvCacheType switch
        {
            "q8_0" => shape.KvBytesPerToken / 2,
            "q4_0" => shape.KvBytesPerToken / 4,
            _ => shape.KvBytesPerToken
        };
        return checked(shape.WeightBytes + shape.WeightBytes / 10 + 300 * MiB + context * kvBytesPerToken);
    }
}

/// <summary>Decides, before a model is loaded, whether it fits the memory budget, so a load never pushes the PC into paging.</summary>
public static class ModelFitPolicy
{
    public static bool Fits(long requiredBytes, long budgetBytes) => requiredBytes != long.MaxValue && requiredBytes <= budgetBytes;

    public static string Explain(long requiredBytes, long availableBytes) =>
        $"いまの空きメモリ（{MemoryBudgetPolicy.Gb(availableBytes)}）では、AIモデル（約{MemoryBudgetPolicy.Gb(requiredBytes)}）を安全に読み込めないため、開始しませんでした。" +
        "ブラウザーのタブなど他のアプリを少し閉じてから、もう一度お試しください。";
}

public interface IMemoryPressureProbe
{
    MemoryPressureSample Sample();
}

/// <summary>Reads only user-accessible Windows signals; no administrator rights or extra packages.</summary>
public sealed class WindowsMemoryPressureProbe : IMemoryPressureProbe, IDisposable
{
    private const int LowMemoryResourceNotification = 0;
    private const uint PdhFmtDouble = 0x00000200;
    private readonly IntPtr _lowMemoryNotification;
    private readonly IntPtr _pdhQuery;
    private readonly IntPtr _hardFaultCounter;
    private readonly object _gate = new();

    public WindowsMemoryPressureProbe()
    {
        if (!OperatingSystem.IsWindows()) return;
        _lowMemoryNotification = CreateMemoryResourceNotification(LowMemoryResourceNotification);
        if (PdhOpenQuery(null, IntPtr.Zero, out var query) == 0)
        {
            if (PdhAddEnglishCounter(query, @"\Memory\Pages Input/sec", IntPtr.Zero, out var counter) == 0 && PdhCollectQueryData(query) == 0)
            {
                _pdhQuery = query;
                _hardFaultCounter = counter;
            }
            else
            {
                PdhCloseQuery(query);
            }
        }
    }

    public MemoryPressureSample Sample()
    {
        lock (_gate)
        {
            var status = new MemoryStatusEx();
            long available = 0, total = 0;
            var commitPercent = 0;
            if (OperatingSystem.IsWindows() && GlobalMemoryStatusEx(status))
            {
                available = (long)status.AvailablePhysical;
                total = (long)status.TotalPhysical;
                if (status.TotalPageFile > 0)
                    commitPercent = (int)Math.Round(100.0 * (status.TotalPageFile - status.AvailablePageFile) / status.TotalPageFile);
            }

            var low = false;
            if (_lowMemoryNotification != IntPtr.Zero && QueryMemoryResourceNotification(_lowMemoryNotification, out var signaled))
                low = signaled;

            double? hardFaults = null;
            if (_pdhQuery != IntPtr.Zero && PdhCollectQueryData(_pdhQuery) == 0 &&
                PdhGetFormattedCounterValue(_hardFaultCounter, PdhFmtDouble, IntPtr.Zero, out var value) == 0 && value.CStatus == 0)
                hardFaults = value.DoubleValue;

            return new(DateTimeOffset.UtcNow, available, total, commitPercent, hardFaults, ReadCompressionStoreBytes(), low);
        }
    }

    private static long? ReadCompressionStoreBytes()
    {
        try
        {
            var processes = Process.GetProcessesByName("Memory Compression");
            try { return processes.Length == 0 ? null : processes.Sum(process => process.WorkingSet64); }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        catch (InvalidOperationException) { return null; }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    public void Dispose()
    {
        if (_pdhQuery != IntPtr.Zero) PdhCloseQuery(_pdhQuery);
        if (_lowMemoryNotification != IntPtr.Zero) CloseHandle(_lowMemoryNotification);
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

    [StructLayout(LayoutKind.Explicit)]
    private struct PdhFmtCounterValue
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double DoubleValue;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateMemoryResourceNotification(int notificationType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryMemoryResourceNotification(IntPtr handle, [MarshalAs(UnmanagedType.Bool)] out bool resourceState);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string counterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr type, out PdhFmtCounterValue value);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}

public sealed class ResourcePressureException(string message) : InvalidOperationException(message);

/// <summary>Totals working set and CPU time over every owned process (server and its children), skipping exited ones.</summary>
public static class OwnedProcessMetrics
{
    public static (long WorkingSetBytes, TimeSpan ProcessorTime) Measure(IEnumerable<int> processIds)
    {
        long workingSet = 0;
        var cpu = TimeSpan.Zero;
        foreach (var id in processIds.Distinct())
        {
            try
            {
                using var process = Process.GetProcessById(id);
                process.Refresh();
                workingSet += process.WorkingSet64;
                cpu += process.TotalProcessorTime;
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
        return (workingSet, cpu);
    }
}

/// <summary>
/// Keeps a short history of samples (periodic background sampling) and answers whether heavy work may start.
/// On a Critical transition it asks the owned model runtime to release memory once current work finishes.
/// </summary>
public sealed class MemoryPressureMonitor : IDisposable
{
    public const int HistoryLength = 12;
    private readonly IMemoryPressureProbe _probe;
    private readonly Func<ResourceMode> _mode;
    private readonly long _installedBytes;
    private readonly Queue<MemoryPressureSample> _history = new();
    private readonly object _gate = new();
    private Timer? _timer;
    private MemoryPressureLevel _lastLevel = MemoryPressureLevel.Normal;

    public MemoryPressureMonitor(IMemoryPressureProbe probe, Func<ResourceMode> mode, long installedBytes)
    {
        _probe = probe;
        _mode = mode;
        _installedBytes = installedBytes;
    }

    public long InstalledBytes => _installedBytes;
    public ResourceMode Mode => _mode();
    public event Action<MemoryPressureAssessment>? EnteredCritical;

    public void Start(TimeSpan interval) => _timer ??= new Timer(_ => SampleAndAssess(), null, TimeSpan.Zero, interval);

    public MemoryPressureAssessment SampleAndAssess()
    {
        MemoryPressureAssessment assessment;
        bool enteredCritical;
        lock (_gate)
        {
            _history.Enqueue(_probe.Sample());
            while (_history.Count > HistoryLength) _history.Dequeue();
            assessment = MemoryBudgetPolicy.Assess(_history.ToArray(), _installedBytes, _mode());
            enteredCritical = assessment.Level == MemoryPressureLevel.Critical && _lastLevel != MemoryPressureLevel.Critical;
            _lastLevel = assessment.Level;
        }
        if (enteredCritical) EnteredCritical?.Invoke(assessment);
        return assessment;
    }

    /// <summary>Starting a model or other heavy work is "expansion"; it is refused with the reasons when any pressure sign is present.</summary>
    public void ThrowIfHeavyWorkUnsafe()
    {
        var assessment = SampleAndAssess();
        if (assessment.CanStartHeavyWork) return;
        // AI work comes first while it runs (user, 2026-10-14): other apps are asked to give back memory they are not
        // using, a step at a time, before giving up.
        for (var step = 1; step <= 4 && !assessment.CanStartHeavyWork; step++)
        {
            var target = assessment.AvailableBytes + step * 512L * 1024 * 1024;
            MemoryRelief.MakeRoom(target, () => _probe.Sample().AvailableBytes, []);
            assessment = SampleAndAssess();
        }
        if (!assessment.CanStartHeavyWork)
            throw new ResourcePressureException("ほかのアプリの未使用メモリを空けても余裕がないため、AIの処理を開始しません。" + string.Join(" ", assessment.Reasons));
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
