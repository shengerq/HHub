using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Hhub.Desktop;

internal static class RuntimeMemory
{
    internal static ulong? PrivateWorkingSet()
    {
        string? name = Environment.GetEnvironmentVariable("HHUB_RUNTIME_JOB");
        if (string.IsNullOrEmpty(name)) return null;
        using var job = OpenJobObject(4, false, name); // JOB_OBJECT_QUERY
        if (job.IsInvalid || !IsProcessInJob(GetCurrentProcess(), job, out bool member) || !member) return null;

        const int header = 8;
        byte[] list = new byte[header + 512 * IntPtr.Size];
        if (!QueryInformationJobObject(job, 3, list, (uint)list.Length, 0)) return null; // BasicProcessIdList
        int count = BitConverter.ToInt32(list, 4);
        if (count is < 1 or > 512) return null;

        ulong bytes = 0;
        bool sampled = false;
        for (int i = 0; i < count; i++)
        {
            uint pid = BitConverter.ToUInt32(list, header + i * IntPtr.Size);
            using var process = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
            if (process.IsInvalid || !IsProcessInJob(process.DangerousGetHandle(), job, out bool stillMember) || !stillMember)
                continue;
            var counters = new MemoryCounters { Size = (uint)Marshal.SizeOf<MemoryCounters>() };
            if (!GetProcessMemoryInfo(process, ref counters, counters.Size)) return null;
            bytes += (ulong)counters.PrivateWorkingSetSize;
            sampled = true;
        }
        return sampled ? bytes : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryCounters
    {
        public uint Size, PageFaultCount;
        public nuint PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
        public nuint PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }

    [DllImport("kernel32.dll", EntryPoint = "OpenJobObjectW", CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle OpenJobObject(uint access, bool inherit, string name);
    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll")]
    private static extern bool IsProcessInJob(nint process, SafeFileHandle job, out bool result);
    [DllImport("kernel32.dll")]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int informationClass, [Out] byte[] buffer, uint length, nint returned);
    [DllImport("kernel32.dll")]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("psapi.dll")]
    private static extern bool GetProcessMemoryInfo(SafeProcessHandle process, ref MemoryCounters counters, uint size);
}
