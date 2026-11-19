using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Lineup.Core.Streaming.FFmpeg;

/// <summary>Private executable entry point for the packaged owned-process launch barrier and portable lifetime guard.</summary>
internal static class OwnedMediaProcessHost
{
    private static async Task<int> Main(string[] arguments)
    {
        if (arguments.Length != 3 || arguments[0] != "--owned-process" || !int.TryParse(arguments[2], out var owner))
        {
            return 125;
        }
        if (!OperatingSystem.IsWindows())
        {
            OwnedMediaProcess.SetPipeDirectory(Path.GetDirectoryName(arguments[1]) ?? throw new InvalidDataException("The owner pipe must use an absolute path."));
        }
        using var pipe = new NamedPipeClientStream(".", arguments[1], PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Process ownerProcess;
        try
        {
            ownerProcess = Process.GetProcessById(owner);
        }
        catch (ArgumentException)
        {
            return 125;
        }
        using var ownerLifetime = ownerProcess;
        var ownerExited = ownerLifetime.WaitForExitAsync(deadline.Token);
        var connection = pipe.ConnectAsync(deadline.Token);
        if (await Task.WhenAny(connection, ownerExited) == ownerExited)
        {
            await deadline.CancelAsync();
            await ObserveCanceledConnectionAsync(connection);
            return 125;
        }
        await connection;
        var reader = new StreamReader(pipe, leaveOpen: true);
        var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        var json = await reader.ReadLineAsync(deadline.Token);
        if (json is null)
        {
            return 125;
        }
        var command = JsonSerializer.Deserialize<OwnedMediaProcess.LaunchCommand>(json) ?? throw new InvalidDataException("Missing owned process command.");
        if (OperatingSystem.IsLinux() && command.Leaf)
        {
            // exec preserves both PID and inherited worker session; the kernel kills media even if its owner cannot run cleanup.
            return ExecuteLinux(command, writer, owner);
        }
        if (OperatingSystem.IsMacOS() && command.CreateSession && command.Leaf && CreateSession() < 0)
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(new OwnedMediaProcess.LaunchResult(Marshal.GetLastWin32Error())));
            return 125;
        }
        var startInfo = new ProcessStartInfo(command.FileName) { UseShellExecute = false };
        foreach (var argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        startInfo.Environment.Clear();
        foreach (var variable in command.Environment)
        {
            startInfo.Environment[variable.Key] = variable.Value;
        }
        Process target;
        try
        {
            // Unix guards survive owner death; Linux's exec leaf also has kernel parent-death protection.
            target = !OperatingSystem.IsWindows() && !command.Leaf
                ? OwnedMediaProcess.StartGuard(startInfo, command.CreateSession)
                : Process.Start(startInfo) ?? throw new Win32Exception(5);
        }
        catch (Win32Exception exception)
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(new OwnedMediaProcess.LaunchResult(exception.NativeErrorCode)));
            return 125;
        }
        using (target)
        {
            try
            {
                var targetId = OwnedMediaProcess.GetTargetProcessId(target);
                var processGroup = OperatingSystem.IsMacOS() && command.CreateSession
                    ? command.Leaf ? Environment.ProcessId : OwnedMediaProcess.GetProcessGroupId(target)
                    : targetId;
                await writer.WriteLineAsync(JsonSerializer.Serialize(new OwnedMediaProcess.LaunchResult(0, targetId, processGroup, OwnedMediaProcess.GetTargetStartTimeUtcTicks(target))));
                var ownerGone = ObserveOwnerAsync(reader);
                var exited = target.WaitForExitAsync();
                if (await Task.WhenAny(ownerGone, exited) == ownerGone)
                {
                    if (!OperatingSystem.IsWindows() && command.CreateSession)
                    {
                        TerminateGroup(processGroup);
                    }
                    if (!target.HasExited)
                    {
                        target.Kill(entireProcessTree: true);
                    }
                    await exited;
                }
                var exitCode = target.ExitCode;
                if (!OperatingSystem.IsWindows() && command.CreateSession && !command.Leaf)
                {
                    TerminateGroup(processGroup);
                }
                return exitCode;
            }
            finally
            {
                if (!target.HasExited)
                {
                    if (!OperatingSystem.IsWindows() && command.CreateSession && !command.Leaf)
                    {
                        TerminateGroup(OperatingSystem.IsMacOS() ? OwnedMediaProcess.GetProcessGroupId(target) : OwnedMediaProcess.GetTargetProcessId(target));
                    }
                    target.Kill(entireProcessTree: true);
                    await target.WaitForExitAsync();
                }
            }
        }
    }

    private static async Task ObserveCanceledConnectionAsync(Task connection)
    {
        try
        {
            await connection;
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
    }

    private static void TerminateGroup(int processId)
    {
        if (Kill(-processId, 9) != 0 && Marshal.GetLastWin32Error() != 3)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not terminate the owned worker process group.");
        }
    }

    private static async Task ObserveOwnerAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync() is not null)
            {
            }
        }
        catch (IOException)
        {
        }
    }

    private static int ExecuteLinux(OwnedMediaProcess.LaunchCommand command, StreamWriter writer, int owner)
    {
        var strings = new[] { command.FileName }.Concat(command.Arguments).Select(Marshal.StringToCoTaskMemUTF8).ToArray();
        var argv = Marshal.AllocHGlobal((strings.Length + 1) * IntPtr.Size);
        try
        {
            for (var index = 0; index < strings.Length; index++)
            {
                Marshal.WriteIntPtr(argv, index * IntPtr.Size, strings[index]);
            }
            Marshal.WriteIntPtr(argv, strings.Length * IntPtr.Size, IntPtr.Zero);
            if (Prctl(1, 9, 0, 0, 0) != 0 || GetParentProcessId() != owner)
            {
                writer.WriteLine(JsonSerializer.Serialize(new OwnedMediaProcess.LaunchResult(5)));
                return 125;
            }
            Exec(command.FileName, argv);
            var error = Marshal.GetLastWin32Error();
            writer.WriteLine(JsonSerializer.Serialize(new OwnedMediaProcess.LaunchResult(error)));
            return 127;
        }
        finally
        {
            Marshal.FreeHGlobal(argv);
            foreach (var pointer in strings)
            {
                Marshal.FreeCoTaskMem(pointer);
            }
        }
    }

    [DllImport("libc", EntryPoint = "prctl", SetLastError = true)]
    private static extern int Prctl(int option, nuint argument, nuint third, nuint fourth, nuint fifth);

    [DllImport("libc", EntryPoint = "getppid")]
    private static extern int GetParentProcessId();

    [DllImport("libc", EntryPoint = "setsid", SetLastError = true)]
    private static extern int CreateSession();

    [DllImport("libc", EntryPoint = "execvp", SetLastError = true)]
    private static extern int Exec([MarshalAs(UnmanagedType.LPUTF8Str)] string executable, IntPtr arguments);

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int processId, int signal);
}
