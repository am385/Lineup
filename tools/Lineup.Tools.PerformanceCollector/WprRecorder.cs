using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Lineup.Tools.PerformanceCollector;

/// <summary>Returns real recording state separately from IPC/export success.</summary>
internal sealed record CollectorRecording(bool Success, bool CleanupSucceeded, string? Error);

/// <summary>Restricts the server to recording rather than arbitrary process execution.</summary>
internal interface ICollectorRecorder
{
    /// <summary>Records one bounded observation into the server-selected private directory.</summary>
    Task<CollectorRecording> CaptureAsync(CollectorRequest request, string directory, CancellationToken cancellationToken);
}

/// <summary>Invokes only the operating system's WPR executable with internally constructed arguments.</summary>
internal sealed class WprRecorder : ICollectorRecorder
{
    private readonly TimeProvider _timeProvider;
    private readonly Func<IReadOnlyList<string>, string, string, TimeSpan, Task> _runWpr;
    private readonly Func<string, long> _freeBytes;

    /// <summary>Creates a recorder with operating-system defaults; deterministic tests can replace recording and elapsed time without invoking WPR.</summary>
    internal WprRecorder(TimeProvider? timeProvider = null, Func<IReadOnlyList<string>, string, string, TimeSpan, Task>? runWpr = null, Func<string, long>? freeBytes = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _runWpr = runWpr ?? RunWprAsync;
        _freeBytes = freeBytes ?? (directory => new DriveInfo(Path.GetPathRoot(directory)!).AvailableFreeSpace);
    }

    /// <inheritdoc />
    public async Task<CollectorRecording> CaptureAsync(CollectorRequest request, string directory, CancellationToken cancellationToken)
    {
        request.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (_freeBytes(directory) < 8L * 1024 * 1024 * 1024)
        {
            throw new IOException("Collector needs at least 8 GiB free for capture and merge.");
        }
        Directory.CreateDirectory(directory);
        var temporary = Directory.CreateDirectory(Path.Combine(directory, "wpr-temporary")).FullName;
        var instance = $"LineupProfile-{Guid.NewGuid():N}";
        List<string> profiles = ["CPU"];
        if (request.Gpu)
        {
            profiles.Add("GPU");
        }
        if (request.DotNet)
        {
            profiles.Add("DotNET");
        }
        if (request.Io)
        {
            profiles.AddRange(["DiskIO", "FileIO", "Network"]);
        }
        var manifest = new Dictionary<string, object?>
        {
            ["schema"] = 1,
            ["scenario"] = request.Scenario,
            ["state"] = "starting",
            ["roots"] = request.Roots,
            ["profiles"] = profiles,
            ["requestedDurationSeconds"] = request.DurationSeconds,
            ["wprInstance"] = instance,
            ["startedUtc"] = null,
            ["measurementFinishedUtc"] = null,
            ["finishedUtc"] = null,
            ["traceSaved"] = false,
            ["failure"] = null,
            ["cleanupFailure"] = null,
            ["limitations"] = new[] { "System-wide private trace. No process census in collector mode; use ETW lifetimes. Inspect lost events and symbols before accepting results." }
        };
        var trace = Path.Combine(directory, "system.etl");
        var started = false;
        var success = false;
        var cleanupSucceeded = true;
        string? failure = null;
        try
        {
            SaveManifest(directory, manifest);
            List<string> arguments = [];
            foreach (var profile in profiles)
            {
                arguments.AddRange(["-start", profile]);
            }
            arguments.AddRange(["-filemode", "-recordtempto", temporary, "-instancename", instance]);
            started = true;
            await _runWpr(arguments, directory, "wpr-start.txt", TimeSpan.FromMinutes(1));
            manifest["state"] = "recording";
            manifest["startedUtc"] = _timeProvider.GetUtcNow();
            SaveManifest(directory, manifest);
            var startedAt = _timeProvider.GetTimestamp();
            while (_timeProvider.GetElapsedTime(startedAt) < TimeSpan.FromSeconds(request.DurationSeconds))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_freeBytes(directory) < 2L * 1024 * 1024 * 1024)
                {
                    throw new IOException("Collector stopped observation below 2 GiB free; space for merging is not guaranteed.");
                }
                var remaining = TimeSpan.FromSeconds(request.DurationSeconds) - _timeProvider.GetElapsedTime(startedAt);
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1), _timeProvider, cancellationToken);
                }
            }
            success = true;
        }
        catch (Exception exception) when (exception is IOException or Win32Exception or InvalidOperationException or OperationCanceledException)
        {
            failure = exception.Message;
            manifest["failure"] = failure;
        }
        finally
        {
            manifest["measurementFinishedUtc"] = _timeProvider.GetUtcNow();
            if (started)
            {
                try
                {
                    // This independent drain budget saves our named trace even after caller/session cancellation.
                    await _runWpr(["-stop", trace, "-instancename", instance], directory, "wpr-stop.txt", TimeSpan.FromMinutes(10));
                    if (!File.Exists(trace) || new FileInfo(trace).Length == 0)
                    {
                        throw new IOException("WPR did not produce a non-empty trace.");
                    }
                    manifest["traceSaved"] = true;
                }
                catch (Exception exception) when (exception is IOException or Win32Exception or InvalidOperationException or OperationCanceledException)
                {
                    cleanupSucceeded = false;
                    manifest["cleanupFailure"] = exception.Message;
                    failure ??= exception.Message;
                }
            }
            manifest["state"] = !cleanupSucceeded ? "cleanup-failed" : success ? "captured" : "failed";
            manifest["finishedUtc"] = _timeProvider.GetUtcNow();
            SaveManifest(directory, manifest);
        }
        return new CollectorRecording(success && cleanupSucceeded, cleanupSucceeded, failure);
    }

    private static void SaveManifest(string directory, Dictionary<string, object?> manifest)
    {
        var path = Path.Combine(directory, "capture.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(manifest, CollectorWire.Json));
        File.Move(path + ".tmp", path, true);
    }

    private static async Task RunWprAsync(IReadOnlyList<string> arguments, string directory, string logName, TimeSpan timeout)
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var start = new ProcessStartInfo(Path.Combine(system, "wpr.exe"))
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.Environment.Clear();
        start.Environment["SystemRoot"] = windows;
        start.Environment["WINDIR"] = windows;
        start.Environment["PATH"] = $"{system};{windows};{Path.Combine(system, "Wbem")}";
        start.Environment["TEMP"] = Path.Combine(directory, "wpr-temporary");
        start.Environment["TMP"] = start.Environment["TEMP"];
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start) ?? throw new IOException("Could not start the Windows WPR executable.");
        using var deadline = new CancellationTokenSource(timeout);
        var stdout = DrainAsync(process.StandardOutput, deadline.Token);
        var stderr = DrainAsync(process.StandardError, deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            using var termination = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(termination.Token);
            throw;
        }
        finally
        {
            var logs = await Task.WhenAll(stdout, stderr);
            await File.WriteAllTextAsync(Path.Combine(directory, logName), logs[0] + Environment.NewLine + logs[1], CancellationToken.None);
        }
        if (process.ExitCode != 0)
        {
            throw new IOException($"WPR exited with code {process.ExitCode}. Inspect {logName}; only the collector's named instance was addressed.");
        }
    }

    private static async Task<string> DrainAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            result.Append(buffer, 0, read);
            if (result.Length > 32 * 1024)
            {
                result.Remove(0, result.Length - 32 * 1024);
            }
        }
        return result.ToString();
    }
}
