using System.Diagnostics;
using System.Text;

namespace Lineup.TestSupport;

/// <summary>Owns test-process I/O, setup, exit observation and cleanup independently of production operation deadlines.</summary>
internal static class TestProcessSupport
{
    /// <summary>Drains test-owned text output without occupying a thread-pool worker for the child's lifetime.</summary>
    /// <param name="reader">The test-owned output reader, not a media stream consumed by the system under test.</param>
    /// <returns>The complete output after EOF.</returns>
    internal static Task<string> DrainOutputAsync(TextReader reader) =>
        Task.Factory.StartNew(reader.ReadToEnd, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    /// <summary>Observes a line-based startup protocol while continuing to drain the child's output on a dedicated thread.</summary>
    /// <param name="reader">The fixture-owned text output.</param>
    /// <param name="observeLine">The startup observer, which must not block the output consumer.</param>
    /// <returns>All diagnostic lines after EOF.</returns>
    internal static Task<string> DrainOutputLinesAsync(TextReader reader, Action<string> observeLine) =>
        Task.Factory.StartNew(() =>
        {
            var output = new StringBuilder();
            while (reader.ReadLine() is { } line)
            {
                output.AppendLine(line);
                observeLine(line);
            }
            return output.ToString();
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    /// <summary>Retains an exit signal from the original process before another owner can dispose its wrapper.</summary>
    /// <param name="process">The original process whose ownership has not yet been transferred.</param>
    /// <returns>A signal that remains usable after process disposal.</returns>
    internal static Task ObserveExit(Process process)
    {
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) => exited.TrySetResult();
        process.EnableRaisingEvents = true;
        if (process.HasExited)
        {
            exited.TrySetResult();
        }
        return exited.Task;
    }

    /// <summary>Prepares a controlled, successful, empty-output probe during Arrange, before transferring it to the system under test.</summary>
    /// <param name="startInfo">The controlled probe's start information, with both output streams redirected.</param>
    /// <param name="cancellationToken">The test's setup cancellation token, not a production operation deadline.</param>
    /// <returns>An already-exited process whose wrapper is now owned by the caller.</returns>
    internal static async Task<Process> StartExitedAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!startInfo.RedirectStandardOutput || !startInfo.RedirectStandardError)
        {
            throw new ArgumentException("An exited test probe must redirect both output streams.", nameof(startInfo));
        }
        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("The controlled test probe could not start.");
        var output = DrainOutputAsync(process.StandardOutput);
        var error = DrainOutputAsync(process.StandardError);
        try
        {
            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            var contents = await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            if (process.ExitCode != 0 || contents[0].Length != 0 || contents[1].Length != 0)
            {
                throw new InvalidDataException($"The controlled empty probe exited with code {process.ExitCode}. stdout: {contents[0]} stderr: {contents[1]}");
            }
            cancellationToken.ThrowIfCancellationRequested();
            return process;
        }
        catch (TimeoutException exception)
        {
            var processId = process.Id;
            await StopAndDrainAsync(process, output, error);
            throw new TimeoutException($"Controlled test-probe setup timed out before ownership transfer (PID {processId}). stdout: {await output} stderr: {await error}", exception);
        }
        catch
        {
            await StopAndDrainAsync(process, output, error);
            throw;
        }
    }

    /// <summary>Stops the owned process tree, waits for exit and drains its test-owned output before disposing the wrapper.</summary>
    /// <param name="process">The process still owned by the fixture.</param>
    /// <param name="drains">Output consumers started immediately after launch.</param>
    /// <returns>A task completing after owned process and I/O cleanup.</returns>
    internal static async Task StopAndDrainAsync(Process process, params Task[] drains)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            try
            {
                await Task.WhenAll(drains).WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                process.Dispose();
            }
        }
    }
}
