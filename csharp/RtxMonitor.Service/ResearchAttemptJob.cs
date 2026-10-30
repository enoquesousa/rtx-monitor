using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RtxMonitor.Service;

/// <summary>
/// Owns one collector attempt and its descendants, including descendants whose
/// parent already exited. Assign the stdin-gated bootstrap before releasing it.
/// This unnamed job can be nested under the desktop application's lifetime job.
/// </summary>
internal sealed class ResearchAttemptJob : IDisposable
{
    private const uint KillOnJobClose = 0x00002000;
    private const int BasicAccountingInformation = 1;
    private const int ExtendedLimitInformation = 9;
    private readonly SafeJobHandle handle;
    private readonly object sync = new();
    private bool terminating;
    private bool disposed;

    private ResearchAttemptJob(SafeJobHandle handle) => this.handle = handle;

    internal static ResearchAttemptJob Create()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("O isolamento do coletor exige Windows.");

        SafeJobHandle job = Native.CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            job.Dispose();
            throw new Win32Exception(error, "Não foi possível criar o job da tentativa de coleta.");
        }
        try
        {
            var limits = new ExtendedLimits
            {
                BasicLimitInformation = new BasicLimits { LimitFlags = KillOnJobClose },
            };
            if (!Native.SetInformationJobObject(job, ExtendedLimitInformation, ref limits,
                    checked((uint)Marshal.SizeOf<ExtendedLimits>())))
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Não foi possível habilitar o encerramento dos descendentes da coleta.");
            return new ResearchAttemptJob(job);
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }

    internal void Assign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (terminating)
                throw new InvalidOperationException("A tentativa já está sendo encerrada.");
            // SafeProcessHandle keeps the exact started process alive during the
            // native call; no PID discovery, reopening, or process-name matching.
            if (!Native.AssignProcessToJobObject(handle, process.SafeHandle))
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Não foi possível isolar a tentativa antes de liberar o coletor.");
        }
    }

    /// <summary>
    /// Terminates only this attempt's job. True means the kernel confirmed zero
    /// active processes in the job; root-process exit alone is never sufficient.
    /// A timeout returns false. Native termination/query errors remain errors.
    /// </summary>
    internal bool TerminateAndWait(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "O prazo deve ser finito e não negativo.");
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            terminating = true;
            var elapsed = Stopwatch.StartNew();
            if (!Native.TerminateJobObject(handle, 1))
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Não foi possível encerrar o job da tentativa de coleta.");
            while (true)
            {
                if (!Native.QueryInformationJobObject(handle, BasicAccountingInformation,
                        out BasicAccounting accounting, checked((uint)Marshal.SizeOf<BasicAccounting>()),
                        out uint returned))
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        "Não foi possível confirmar a saída dos descendentes da coleta.");
                if (returned != Marshal.SizeOf<BasicAccounting>())
                    throw new InvalidOperationException("O Windows retornou um estado incompleto do job da coleta.");
                if (accounting.ActiveProcesses == 0) return true;
                TimeSpan remaining = timeout - elapsed.Elapsed;
                if (remaining <= TimeSpan.Zero) return false;
                Thread.Sleep(remaining < TimeSpan.FromMilliseconds(10)
                    ? remaining : TimeSpan.FromMilliseconds(10));
            }
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            terminating = true;
            // Best effort here is deliberate: only TerminateAndWait(true) can
            // authorize a restart. Closing the last handle also enforces KILL_ON_JOB_CLOSE.
            _ = Native.TerminateJobObject(handle, 1);
            handle.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
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
    private struct ExtendedLimits
    {
        public BasicLimits BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicAccounting
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

    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeJobHandle() : base(ownsHandle: true) { }
        protected override bool ReleaseHandle() => Native.CloseHandle(handle);
    }

    private static class Native
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        internal static extern SafeJobHandle CreateJobObjectW(IntPtr attributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetInformationJobObject(SafeJobHandle job, int informationClass,
            ref ExtendedLimits information, uint informationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TerminateJobObject(SafeJobHandle job, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryInformationJobObject(SafeJobHandle job, int informationClass,
            out BasicAccounting information, uint informationLength, out uint returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);
    }
}
