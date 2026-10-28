using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace HomeManagement.Agent.Resilience;

[SupportedOSPlatform("windows")]
public sealed partial class WindowsResourceLimits : IDisposable
{
    internal const int CpuLimitPercent = 15;
    internal const int MemoryLimitPercent = 18;
    private const uint CpuRateControlEnable = 0x1;
    private const uint CpuRateControlHardCap = 0x4;
    private const uint JobObjectLimitJobMemory = 0x200;
    private const int CpuRateInformationClass = 15;
    private const int ExtendedLimitInformationClass = 9;
    private readonly SafeJobHandle _job;
    internal int ProcessorCount { get; }

    private WindowsResourceLimits(SafeJobHandle job, int processorCount)
    {
        _job = job;
        ProcessorCount = processorCount;
    }

    internal static WindowsResourceLimits Create()
    {
        var job = CreateJobObject(0, 0);
        if (job.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            job.Dispose();
            throw new Win32Exception(error, "Unable to create the agent resource-limit job.");
        }

        try
        {
            var processorCount = GetActiveProcessorCount(ushort.MaxValue);
            if (processorCount == 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Unable to determine total machine CPU capacity.");

            var cpu = new CpuRateInformation
            {
                ControlFlags = CpuRateControlEnable | CpuRateControlHardCap,
                CpuRate = CpuLimitPercent * 100
            };
            if (!SetCpuRate(job, CpuRateInformationClass, in cpu, (uint)Marshal.SizeOf<CpuRateInformation>()))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Unable to enforce the agent CPU limit.");

            var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            if (!GlobalMemoryStatusEx(ref memory))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Unable to determine physical memory for the agent limit.");

            var limits = new ExtendedLimitInformation
            {
                BasicLimitInformation = new BasicLimitInformation { LimitFlags = JobObjectLimitJobMemory },
                JobMemoryLimit = checked((nuint)(memory.TotalPhysical / 100 * MemoryLimitPercent))
            };
            if (!SetMemoryLimit(job, ExtendedLimitInformationClass, in limits, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Unable to enforce the agent committed-memory limit.");

            using var process = Process.GetCurrentProcess();
            if (!AssignProcessToJobObject(job, process.SafeHandle))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Unable to assign the agent to its resource-limit job.");

            return new WindowsResourceLimits(job, checked((int)processorCount));
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }

    public void Dispose() => _job.Dispose();

    internal TimeSpan GetTotalProcessorTime()
    {
        if (!QueryAccounting(_job, 1, out var accounting, (uint)Marshal.SizeOf<BasicAccountingInformation>(), 0))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Unable to read agent job CPU accounting.");

        return TimeSpan.FromTicks(accounting.TotalUserTime + accounting.TotalKernelTime);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicAccountingInformation
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CpuRateInformation
    {
        public uint ControlFlags;
        public uint CpuRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
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
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeJobHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial SafeJobHandle CreateJobObject(nint attributes, nint name);

    [LibraryImport("kernel32.dll", EntryPoint = "SetInformationJobObject", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetCpuRate(SafeJobHandle job, int informationClass, in CpuRateInformation information, uint length);

    [LibraryImport("kernel32.dll", EntryPoint = "SetInformationJobObject", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetMemoryLimit(SafeJobHandle job, int informationClass, in ExtendedLimitInformation information, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle process);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryAccounting(SafeJobHandle job, int informationClass, out BasicAccountingInformation information,
        uint length, nint returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetActiveProcessorCount(ushort groupNumber);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
