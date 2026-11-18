using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Lineup.Core.Streaming.FFmpeg;

/// <summary>Starts media behind a launch barrier and contains its lifetime independently of CPU and memory policies.</summary>
public static class OwnedMediaProcess
{
    private static readonly TimeSpan _launchTimeout = TimeSpan.FromSeconds(30);
    private static readonly object _jobsLock = new();
    private static readonly Dictionary<NamedPipeServerStream, SafeFileHandle?> _owners = [];
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Process, Identity> _identities = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Process, ResourceIdentity> _resources = new();
    private static string _pipeDirectory = Environment.GetEnvironmentVariable("LINEUP_PROCESS_PIPE_ROOT") is { Length: > 0 } root ? root : Path.GetTempPath();

    /// <summary>Starts an owned media process without shell interpretation. The returned process owns the standard streams and mirrors the media exit code.</summary>
    /// <param name="startInfo">Executable, arguments, working directory, environment, and standard stream configuration.</param>
    /// <returns>The owned process. Callers must terminate and await it before joining redirected readers.</returns>
    /// <remarks>UseShellExecute must be false and arguments must use ArgumentList. Id identifies the supervisor; GetTargetProcessId identifies the actual media process.</remarks>
    public static Process Start(ProcessStartInfo startInfo) => StartCore(startInfo, null, false, false);

    /// <summary>Gets the target worker identifier while retaining the returned supervisor as the standard-stream and cleanup owner.</summary>
    public static int GetTargetProcessId(Process process) => _identities.TryGetValue(process, out var identity) ? identity.ProcessId : process.Id;

    /// <summary>Gets the original target creation identity without reopening its PID after exit.</summary>
    internal static long? GetTargetStartTimeUtcTicks(Process process)
    {
        if (_identities.TryGetValue(process, out var identity))
        {
            return identity.StartTimeUtcTicks;
        }
        try
        {
            return process.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Gets resource identities for the actual media process, independently of its lifetime supervisor.</summary>
    public static IReadOnlyList<string> GetResourceIds(Process process) => _resources.TryGetValue(process, out var resource) ? [resource.Target] : [];

    /// <summary>Labels an owned media process without exposing command arguments or transferring cleanup ownership.</summary>
    public static void SetResourceRole(Process process, string role)
    {
        foreach (var id in GetResourceIds(process))
        {
            ProcessResourceRegistry.Local.SetRole(id, role);
        }
    }

    /// <summary>Gets the worker session's process group, which remains inherited by its media launches.</summary>
    public static int GetProcessGroupId(Process process) => _identities.TryGetValue(process, out var identity) ? identity.ProcessGroupId : process.Id;

    /// <summary>Starts a worker behind the same barrier, applying its lifetime and resource scope before allowing any worker code to execute.</summary>
    /// <param name="startInfo">Worker launch configuration, including any Linux session launcher.</param>
    /// <param name="beforeRelease">Attaches the already running launcher to its mandatory worker scope.</param>
    /// <param name="createSession">Owns a Unix worker session, using the configured setsid launcher on Linux or a new guarded session on macOS.</param>
    /// <returns>The owned worker process.</returns>
    public static Process Start(ProcessStartInfo startInfo, Action<Process>? beforeRelease, bool createSession = false) => StartCore(startInfo, beforeRelease, createSession, false);

    /// <summary>Starts the independent Unix guard whose owner is the outer launcher.</summary>
    internal static Process StartGuard(ProcessStartInfo startInfo, bool createSession) => StartCore(startInfo, null, createSession, true);

    /// <summary>Keeps nested Unix socket names beneath the original owner's transport root rather than the worker's longer isolated TMPDIR.</summary>
    internal static void SetPipeDirectory(string directory) => _pipeDirectory = directory;

    private static Process StartCore(ProcessStartInfo startInfo, Action<Process>? beforeRelease, bool createSession, bool leaf)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (startInfo.UseShellExecute || startInfo.Arguments.Length != 0)
        {
            throw new ArgumentException("Owned processes require UseShellExecute=false and ArgumentList.", nameof(startInfo));
        }
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("Owned process lifetime requires Windows, Linux, or macOS.");
        }
        var pipeName = $"lineup-owner-{Guid.NewGuid():N}";
        if (!OperatingSystem.IsWindows())
        {
            // Workers deliberately use a different TMPDIR. Both endpoints must use the owner's exact socket path.
            pipeName = Path.Combine(_pipeDirectory, pipeName);
        }
        var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var assemblyPath = typeof(OwnedMediaProcess).Assembly.Location;
        var appHost = Path.Combine(Path.GetDirectoryName(assemblyPath)!, Path.GetFileNameWithoutExtension(assemblyPath) + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
        var useAppHost = File.Exists(appHost);
        var helper = new ProcessStartInfo
        {
            FileName = useAppHost ? appHost : ResolveDotnet(),
            WorkingDirectory = startInfo.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = startInfo.CreateNoWindow,
            RedirectStandardInput = startInfo.RedirectStandardInput,
            RedirectStandardOutput = startInfo.RedirectStandardOutput,
            RedirectStandardError = startInfo.RedirectStandardError
        };
        if (!useAppHost)
        {
            helper.ArgumentList.Add(assemblyPath);
        }
        helper.ArgumentList.Add("--owned-process");
        helper.ArgumentList.Add(pipeName);
        helper.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        helper.Environment.Clear();
        foreach (var item in startInfo.Environment)
        {
            helper.Environment[item.Key] = item.Value;
        }
        Process? process = null;
        SafeFileHandle? job = null;
        try
        {
            process = StartLauncher(helper);
            var supervisorStartTime = GetTargetStartTimeUtcTicks(process);
            // The helper cannot execute the target before the job/resource attachment and command handshake.
            if (OperatingSystem.IsWindows() && beforeRelease is null && !HasKillOnCloseJob())
            {
                job = CreateJob(process);
            }
            beforeRelease?.Invoke(process);
            using var timeout = new CancellationTokenSource(_launchTimeout);
            pipe.WaitForConnectionAsync(timeout.Token).GetAwaiter().GetResult();
            var command = new LaunchCommand(startInfo.FileName, startInfo.ArgumentList.ToArray(), startInfo.Environment.ToDictionary(), leaf, createSession);
            var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            writer.WriteLine(JsonSerializer.Serialize(command));
            var reader = new StreamReader(pipe, leaveOpen: true);
            var response = reader.ReadLineAsync(timeout.Token).AsTask().GetAwaiter().GetResult();
            var result = response is null ? null : JsonSerializer.Deserialize<LaunchResult>(response);
            if ((result is null && !(OperatingSystem.IsLinux() && leaf)) || result is { Error: not 0 })
            {
                throw new Win32Exception(result?.Error ?? 5, "The owned media executable could not be started.");
            }
            var targetStartTime = result?.StartTimeUtcTicks ?? (result is null ? supervisorStartTime : null);
            _identities.Add(process, new Identity(result?.ProcessId ?? process.Id, result?.ProcessGroupId ?? process.Id, targetStartTime));
            lock (_jobsLock)
            {
                _owners.Add(pipe, job);
                job = null;
            }
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => Release(pipe);
            var registry = ProcessResourceRegistry.Local;
            var targetResource = registry.Register(result?.ProcessId ?? process.Id, "media", targetStartTime);
            var supervisorResource = process.Id == (result?.ProcessId ?? process.Id) ? null : registry.Register(process.Id, "helper", supervisorStartTime);
            _resources.Add(process, new(targetResource));
            process.Exited += (_, _) =>
            {
                registry.Unregister(targetResource);
                if (supervisorResource is not null)
                {
                    registry.Unregister(supervisorResource);
                }
            };
            if (!process.HasExited)
            {
                if (targetStartTime is not null)
                {
                    registry.Attach(targetResource);
                }
                if (supervisorResource is not null && supervisorStartTime is not null)
                {
                    registry.Attach(supervisorResource);
                }
            }
            if (process.HasExited)
            {
                Release(pipe);
                registry.Unregister(targetResource);
                if (supervisorResource is not null)
                {
                    registry.Unregister(supervisorResource);
                }
            }

            return process;
        }
        catch (Exception failure)
        {
            job?.Dispose();
            pipe.Dispose();
            if (process is not null)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
                if (!process.WaitForExit((int)_launchTimeout.TotalMilliseconds))
                {
                    process.Dispose();
                    throw new TimeoutException("Owned process launch failed and target cleanup could not be confirmed.", failure);
                }
                process.Dispose();
            }
            throw;
        }
    }

    private sealed record ResourceIdentity(string Target);

    private static Process StartLauncher(ProcessStartInfo startInfo)
    {
        if (!OperatingSystem.IsLinux())
        {
            return Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the owned process launcher.");
        }
        // Linux parent-death signals track the spawning thread, not just its process. Keep that thread alive until exec's process has exited.
        var started = new TaskCompletionSource<Process>(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(() =>
        {
            try
            {
                var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the owned process launcher.");
                using var observer = Process.GetProcessById(process.Id);
                _ = observer.Handle;
                started.SetResult(process);
                observer.WaitForExit();
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or ArgumentException)
            {
                started.TrySetException(exception);
            }
        })
        { IsBackground = true, Name = "Owned media launcher" }.Start();
        return started.Task.GetAwaiter().GetResult();
    }

    private static void Release(NamedPipeServerStream pipe)
    {
        lock (_jobsLock)
        {
            if (_owners.Remove(pipe, out var job))
            {
                job?.Dispose();
                pipe.Dispose();
            }
        }
    }

    private static string ResolveDotnet()
    {
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return Environment.ProcessPath!;
        }
        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        return root is { Length: > 0 } ? Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet") : "dotnet";
    }

    private static bool HasKillOnCloseJob()
    {
        var name = Environment.GetEnvironmentVariable("LINEUP_WORKER_JOB");
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }
        using var job = OpenJobObject(4, false, name);
        var information = new JobInformation();
        return !job.IsInvalid && IsProcessInJob(new IntPtr(-1), job, out var contained) && contained &&
            QueryInformationJobObject(job.DangerousGetHandle(), 9, ref information, (uint)Marshal.SizeOf<JobInformation>(), IntPtr.Zero) && (information.Flags & 0x2000) != 0;
    }

    private static SafeFileHandle CreateJob(Process process)
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        try
        {
            var information = new JobInformation { Flags = 0x2000 };
            if (job.IsInvalid || !SetInformationJobObject(job, 9, ref information, (uint)Marshal.SizeOf<JobInformation>()) || !AssignProcessToJobObject(job, process.Handle))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not contain the owned media process.");
            }
            return job;
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobInformation
    {
        /// <summary>Stores the native job time limits.</summary>
        public long ProcessTime, JobTime;
        /// <summary>Stores native job flags.</summary>
        public uint Flags;
        /// <summary>Stores native working set limits.</summary>
        public nuint MinimumWorkingSet, MaximumWorkingSet;
        /// <summary>Stores the native active process limit.</summary>
        public uint ActiveProcesses;
        /// <summary>Stores native affinity.</summary>
        public nuint Affinity;
        /// <summary>Stores native priority information.</summary>
        public uint PriorityClass, SchedulingClass;
        /// <summary>Stores native I/O accounting.</summary>
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        /// <summary>Stores native memory limits and accounting.</summary>
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenJobObject(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(IntPtr process, SafeFileHandle job, [MarshalAs(UnmanagedType.Bool)] out bool contained);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref JobInformation information, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(IntPtr job, int informationClass, ref JobInformation information, uint length, IntPtr returnedLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);

    /// <summary>Describes a target released through the private launch channel.</summary>
    internal sealed record LaunchCommand(string FileName, string[] Arguments, Dictionary<string, string?> Environment, bool Leaf, bool CreateSession);

    /// <summary>Reports target launch failure without putting executable diagnostics on the private channel.</summary>
    internal sealed record LaunchResult(int Error, int ProcessId = 0, int ProcessGroupId = 0, long? StartTimeUtcTicks = null);

    private sealed record Identity(int ProcessId, int ProcessGroupId, long? StartTimeUtcTicks);
}
