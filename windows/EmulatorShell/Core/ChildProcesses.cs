using System.Diagnostics;
using System.Runtime.InteropServices;

namespace EmulatorShell;

/// Long-running helpers this app starts (`adb track-devices`, the `adb shell` that runs scrcpy-server) go into a
/// job object that ends them when the app ends, however it ends: quitting stops them itself, but closing the
/// last window or a crash would otherwise leave them running, one more each time.
static partial class ChildProcesses
{
    private static readonly Lazy<nint> Job = new(CreateJob);

    public static void Adopt(Process process)
    {
        try
        {
            if (Job.Value != 0 && !AssignProcessToJobObject(Job.Value, process.Handle))
                Log.Debug($"cannot tie process {process.Id} to the app: {Marshal.GetLastPInvokeError()}");
        }
        catch (Exception error)
        {
            Log.Debug($"cannot tie a process to the app: {error.Message}");
        }
    }

    private static nint CreateJob()
    {
        var job = CreateJobObjectW(0, null);
        if (job == 0) return 0;
        var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        limits.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, pointer, false);
            // 9: JobObjectExtendedLimitInformation. The handle stays open for the app's life; closing it with the
            // process is what ends the children.
            if (!SetInformationJobObject(job, 9, pointer, (uint)size)) return 0;
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
        return job;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateJobObjectW(nint attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(nint job, int infoClass, nint info, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);
}
