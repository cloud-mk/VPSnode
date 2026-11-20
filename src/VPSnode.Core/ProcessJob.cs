using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VPSnode.Core;

internal static class ProcessJob
{
    private const uint KillOnClose = 0x2000;
    private const int ExtendedLimitInformation = 9;

    internal static SafeFileHandle? Attach(Process process)
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        var limits = new Limits { Basic = new BasicLimits { Flags = KillOnClose } };
        try
        {
            if (!job.IsInvalid && SetInformationJobObject(job, ExtendedLimitInformation, ref limits,
                (uint)Marshal.SizeOf<Limits>()) && !process.HasExited && AssignProcessToJobObject(job, process.Handle))
                return job;
        }
        catch (InvalidOperationException) { }
        job.Dispose();
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessTime;
        public long PerJobTime;
        public uint Flags;
        public UIntPtr MinWorkingSet;
        public UIntPtr MaxWorkingSet;
        public uint ActiveCount;
        public UIntPtr Affinity;
        public uint Priority;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Limits
    {
        public BasicLimits Basic;
        public ulong ReadOperations;
        public ulong WriteOperations;
        public ulong OtherOperations;
        public ulong ReadBytes;
        public ulong WriteBytes;
        public ulong OtherBytes;
        public UIntPtr ProcessMemory;
        public UIntPtr JobMemory;
        public UIntPtr PeakProcessMemory;
        public UIntPtr PeakJobMemory;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref Limits limits, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}
