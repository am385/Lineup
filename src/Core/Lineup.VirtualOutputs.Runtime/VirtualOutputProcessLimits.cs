using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Lineup.Common.Streaming;

namespace Lineup.VirtualOutputs.Runtime;

internal static class VirtualOutputProcessLimits
{
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
        private const string CgroupRoot = "/sys/fs/cgroup";

        internal static IDisposable Apply(Process process, VirtualOutputWorkerPolicyDefinition policy)
        {
            if (!File.Exists(Path.Combine(CgroupRoot, "cgroup.controllers")))
            {
                throw new PlatformNotSupportedException("Virtual-output process limits require Linux cgroup v2.");
            }
            var path = Path.Combine(CgroupRoot, $"lineup-output-{process.Id}");
            Directory.CreateDirectory(path);
            try
            {
                if (policy.MaximumMemoryMib > 0)
                {
                    File.WriteAllText(
                        Path.Combine(path, "memory.max"),
                        checked((long)policy.MaximumMemoryMib * 1024 * 1024).ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                if (policy.MaximumCpuPercent > 0)
                {
                    var quota = checked(policy.MaximumCpuPercent * 1000);
                    File.WriteAllText(
                        Path.Combine(path, "cpu.max"),
                        $"{quota} 100000");
                }
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
                if (!AssignProcessToJobObject(job, process.Handle))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not assign the virtual-output worker to its job object.");
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
