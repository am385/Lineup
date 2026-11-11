using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Lineup.Core.Common.Streaming;

namespace Lineup.Core.VirtualOutputs.Runtime;

internal static class VirtualOutputProcessLimits
{
    /// <summary>Probes requested enforcement before creating a worker or consuming its restart allowance.</summary>
    internal static void Preflight(VirtualOutputWorkerPolicyDefinition policy)
    {
        if (policy.MaximumMemoryMib < 0 || policy.MaximumCpuPercent is < 0 or > 100)
        {
            throw new VirtualOutputResourcePolicyException("Worker memory must be nonnegative and CPU percentage must be between zero and 100.");
        }
        if (policy.MaximumMemoryMib == 0 && policy.MaximumCpuPercent == 0)
        {
            return;
        }
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var job = WindowsJobLimits.Create(policy);
                return;
            }
            if (OperatingSystem.IsLinux())
            {
                LinuxCgroupLimits.Preflight(policy);
                return;
            }
            throw new PlatformNotSupportedException("Worker CPU and memory enforcement is supported only on Windows and Linux.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or Win32Exception)
        {
            throw new VirtualOutputResourcePolicyException(
                "The requested worker CPU/memory policy cannot be enforced. On Linux, configure LINEUP_CGROUP_ROOT to a writable delegated cgroup v2 with the requested controllers enabled. " +
                "Worker limits do not cover host-side FFmpeg or graph buffers. Disable the requested limits or correct the deployment permissions.", exception);
        }
    }

    /// <summary>Probes one explicitly owned Linux cgroup root without modifying process-wide configuration.</summary>
    internal static void PreflightLinux(VirtualOutputWorkerPolicyDefinition policy, string root) => LinuxCgroupLimits.Preflight(policy, root);

    internal static IDisposable? Apply(Process process, VirtualOutputWorkerPolicyDefinition policy)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.MaximumMemoryMib == 0 && policy.MaximumCpuPercent == 0)
        {
            return null;
        }
        if (OperatingSystem.IsWindows())
        {
            return WindowsJobLimits.Apply(process, policy);
        }
        if (OperatingSystem.IsLinux())
        {
            return LinuxCgroupLimits.Apply(process, policy);
        }
        throw new PlatformNotSupportedException("Virtual-output process CPU and memory limits are supported only on Windows and Linux.");
    }

    private static class LinuxCgroupLimits
    {
        private static string CgroupRoot => Environment.GetEnvironmentVariable("LINEUP_CGROUP_ROOT") is { Length: > 0 } root ? root : "/sys/fs/cgroup";

        /// <summary>Checks that the kernel exposes writable requested controllers in a temporary delegated child.</summary>
        internal static void Preflight(VirtualOutputWorkerPolicyDefinition policy, string? root = null)
        {
            root ??= CgroupRoot;
            RequireCgroupV2(root);
            var path = Path.Combine(root, $"lineup-preflight-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            try
            {
                foreach (var name in new[] { "cgroup.procs" }
                             .Concat(policy.MaximumMemoryMib > 0 ? ["memory.max"] : Array.Empty<string>())
                             .Concat(policy.MaximumCpuPercent > 0 ? ["cpu.max"] : Array.Empty<string>()))
                {
                    using var file = new FileStream(Path.Combine(path, name), FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                }
                Configure(path, policy);
            }
            finally
            {
                Directory.Delete(path);
            }
        }

        private static void RequireCgroupV2(string? root = null)
        {
            if (!File.Exists(Path.Combine(root ?? CgroupRoot, "cgroup.controllers")))
            {
                throw new PlatformNotSupportedException("Worker limits require a delegated Linux cgroup v2 directory.");
            }
        }

        private static void Configure(string path, VirtualOutputWorkerPolicyDefinition policy)
        {
            if (policy.MaximumMemoryMib > 0)
            {
                File.WriteAllText(Path.Combine(path, "memory.max"), checked((long)policy.MaximumMemoryMib * 1024 * 1024).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            if (policy.MaximumCpuPercent > 0)
            {
                File.WriteAllText(Path.Combine(path, "cpu.max"), checked(policy.MaximumCpuPercent * 1000).ToString(System.Globalization.CultureInfo.InvariantCulture) + " 100000");
            }
        }

        internal static IDisposable Apply(Process process, VirtualOutputWorkerPolicyDefinition policy)
        {
            RequireCgroupV2();
            var path = Path.Combine(CgroupRoot, $"lineup-output-{process.Id}");
            Directory.CreateDirectory(path);
            try
            {
                Configure(path, policy);
                File.WriteAllText(
                    Path.Combine(path, "cgroup.procs"),
                    process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return new CgroupLease(path);
            }
            catch
            {
                TryDelete(path);
                throw;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                Directory.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private sealed class CgroupLease(string path) : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    TryDelete(path);
                }
            }
        }
    }

    private static class WindowsJobLimits
    {
        private const uint JobObjectLimitProcessMemory = 0x00000100;
        private const uint JobObjectLimitKillOnJobClose = 0x00002000;
        private const uint CpuRateControlEnable = 0x1;
        private const uint CpuRateControlHardCap = 0x4;

        internal static IDisposable Apply(Process process, VirtualOutputWorkerPolicyDefinition policy)
        {
            var job = Create(policy);
            if (!AssignProcessToJobObject(job, process.Handle))
            {
                var error = Marshal.GetLastWin32Error();
                job.Dispose();
                throw new Win32Exception(error, "Could not assign the virtual-output worker to its job object.");
            }
            return job;
        }

        /// <summary>Creates and configures a job without attaching a running worker.</summary>
        internal static SafeFileHandle Create(VirtualOutputWorkerPolicyDefinition policy)
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create a virtual-output worker job object.");
            }
            try
            {
                var limits = new JobObjectExtendedLimitInformation
                {
                    BasicLimitInformation = new JobObjectBasicLimitInformation
                    {
                        LimitFlags = JobObjectLimitKillOnJobClose |
                            (policy.MaximumMemoryMib > 0 ? JobObjectLimitProcessMemory : 0)
                    },
                    ProcessMemoryLimit = policy.MaximumMemoryMib > 0
                        ? checked((nuint)((long)policy.MaximumMemoryMib * 1024 * 1024))
                        : 0
                };
                SetInformation(job, JobObjectInfoType.ExtendedLimitInformation, limits);
                if (policy.MaximumCpuPercent > 0)
                {
                    SetInformation(job, JobObjectInfoType.CpuRateControlInformation, new JobObjectCpuRateControlInformation
                    {
                        ControlFlags = CpuRateControlEnable | CpuRateControlHardCap,
                        CpuRate = checked((uint)policy.MaximumCpuPercent * 100)
                    });
                }
                return job;
            }
            catch
            {
                job.Dispose();
                throw;
            }
        }

        private static void SetInformation<T>(SafeFileHandle job, JobObjectInfoType type, T value)
            where T : struct
        {
            var length = Marshal.SizeOf<T>();
            var pointer = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(value, pointer, fDeleteOld: false);
                if (!SetInformationJobObject(job, type, pointer, (uint)length))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not configure virtual-output job information {type}.");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
        }

        private enum JobObjectInfoType
        {
            ExtendedLimitInformation = 9,
            CpuRateControlInformation = 15
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
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
        private struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public nuint ProcessMemoryLimit;
            public nuint JobMemoryLimit;
            public nuint PeakProcessMemoryUsed;
            public nuint PeakJobMemoryUsed;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectCpuRateControlInformation
        {
            public uint ControlFlags;
            public uint CpuRate;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateJobObject(IntPtr jobAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(
            SafeFileHandle job,
            JobObjectInfoType jobObjectInfoClass,
            IntPtr jobObjectInfo,
            uint jobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    }
}
