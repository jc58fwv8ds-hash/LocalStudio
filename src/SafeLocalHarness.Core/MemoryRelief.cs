using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SafeLocalHarness.Core;

/// <summary>
/// Makes room for a model when the PC is short of memory (user, 2026-10-14: 「AIモデルを使用しているときは必要なだけメモリを
/// 使用してほかのアプリのメモリ使用を必要なだけ抑制する」). Other apps of this user are asked, largest first and only until
/// enough is free, to give back memory they are not using right now (EmptyWorkingSet: their pages move out of RAM and come
/// back when touched; nothing is closed or lost). Local Studio and its runtime, the window the user is working in,
/// Windows' own processes and security software are left alone. No administrator rights are used: processes this user
/// may not adjust are skipped.
/// </summary>
public static class MemoryRelief
{
    public sealed record Result(long BytesBefore, long BytesAfter, int AppsAsked);

    /// <summary>On in the app and drills; off in tests, whose memory readings are simulated and must not touch real apps.</summary>
    public static bool Enabled { get; set; }

    /// <summary>About how much the user's other apps could give back (half of what they hold): the model chosen at start
    /// may count on it, since it is made free when the model loads.</summary>
    public static long EstimateReclaimable()
    {
        if (!Enabled || !OperatingSystem.IsWindows()) return 0;
        GetWindowThreadProcessId(GetForegroundWindow(), out var foreground);
        var session = Process.GetCurrentProcess().SessionId;
        long total = 0;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id != Environment.ProcessId && process.Id != foreground && process.SessionId == session && !LeftAlone.Contains(process.ProcessName))
                        total += process.WorkingSet64;
                }
                catch (InvalidOperationException) { }
            }
        }
        return total / 2;
    }

    // Windows itself and security products (Trend Micro, Defender) are never touched.
    private static readonly HashSet<string> LeftAlone = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso", "svchost", "dwm", "fontdrvhost",
        "sihost", "ctfmon", "MsMpEng", "NisSrv", "SecurityHealthService", "SecurityHealthSystray", "MpDefenderCoreService",
        "TmListen", "PccNTMon", "Ntrtscan", "TMBMSRV", "TmCCSF", "coreServiceShell", "coreFrameworkHost", "uiWatchDog", "uiSeAgnt", "ds_agent",
        "ollama", "ollama app", "LocalStudio", "SafeLocalHarness.App"
    };

    /// <summary>
    /// Asks other apps to release unused memory until <paramref name="neededAvailableBytes"/> is available (or every app was
    /// asked). <paramref name="availableBytes"/> reads the PC's available memory; <paramref name="keep"/> are process ids
    /// that must not be touched (Local Studio's runtime).
    /// </summary>
    public static Result MakeRoom(long neededAvailableBytes, Func<long> availableBytes, IEnumerable<int> keep)
    {
        var before = availableBytes();
        if (!Enabled || !OperatingSystem.IsWindows() || before >= neededAvailableBytes) return new(before, before, 0);
        var kept = keep.Append(Environment.ProcessId).ToHashSet();
        GetWindowThreadProcessId(GetForegroundWindow(), out var foreground);
        kept.Add((int)foreground);
        var session = Process.GetCurrentProcess().SessionId;
        var asked = 0;
        var candidates = Process.GetProcesses()
            .Where(process => !kept.Contains(process.Id) && process.SessionId == session && !LeftAlone.Contains(process.ProcessName))
            .Select(process => { try { return (Process: process, Size: process.WorkingSet64); } catch (InvalidOperationException) { return (Process: process, Size: 0L); } })
            .Where(item => item.Size > 32L * 1024 * 1024)
            .OrderByDescending(item => item.Size)
            .ToArray();
        foreach (var (process, _) in candidates)
        {
            if (availableBytes() >= neededAvailableBytes) break;
            var handle = OpenProcess(ProcessSetQuota | ProcessQueryLimitedInformation, false, process.Id);
            if (handle == IntPtr.Zero) continue;
            try { if (EmptyWorkingSet(handle)) asked++; }
            finally { CloseHandle(handle); }
        }
        foreach (var (process, _) in candidates) process.Dispose();
        return new(before, availableBytes(), asked);
    }

    private const uint ProcessSetQuota = 0x0100;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", EntryPoint = "K32EmptyWorkingSet")] private static extern bool EmptyWorkingSet(IntPtr process);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
