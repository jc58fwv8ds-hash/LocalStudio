using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SafeLocalHarness.Core;

internal sealed class WindowsProcessJob : IDisposable
{
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int JobObjectBasicProcessIdList = 3;
    private IntPtr _handle;

    private WindowsProcessJob()
    {
        _handle = CreateJobObject(IntPtr.Zero, null);
        if (_handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "LocalStudio専用プロセスの管理領域を作成できませんでした。");
        var limits = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose }
        };
        var length = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        if (!SetInformationJobObject(_handle, 9, ref limits, (uint)length))
        {
            var error = Marshal.GetLastWin32Error();
            CloseHandle(_handle);
            _handle = IntPtr.Zero;
            throw new Win32Exception(error, "LocalStudio専用プロセスの終了規則を設定できませんでした。");
        }
    }

    public static WindowsProcessJob? Attach(Process process)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var job = new WindowsProcessJob();
        if (AssignProcessToJobObject(job._handle, process.Handle)) return job;
        var error = Marshal.GetLastWin32Error();
        job.Dispose();
        throw new Win32Exception(error, "OllamaをLocalStudio専用プロセスとして管理できませんでした。");
    }

    /// <summary>
    /// Every process currently in the job, including children the owned process started (for example a model runner
    /// whose executable name differs from the server). Returns an empty list when the job is closed or cannot be read.
    /// </summary>
    public IReadOnlyList<int> ProcessIds()
    {
        const int capacity = 64;
        var handle = _handle;
        if (handle == IntPtr.Zero) return [];
        // JOBOBJECT_BASIC_PROCESS_ID_LIST: two DWORD counters, then ULONG_PTR process ids.
        var size = 8 + IntPtr.Size * capacity;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryInformationJobObject(handle, JobObjectBasicProcessIdList, buffer, (uint)size, IntPtr.Zero)) return [];
            var listed = Math.Min(Marshal.ReadInt32(buffer, 4), capacity);
            var ids = new List<int>(listed);
            for (var index = 0; index < listed; index++)
                ids.Add((int)Marshal.ReadIntPtr(buffer, 8 + index * IntPtr.Size));
            return ids;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        CloseHandle(_handle);
        _handle = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int informationClass, ref JobObjectExtendedLimitInformation information, uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(IntPtr job, int informationClass, IntPtr information, uint length, IntPtr returnLength);
}
