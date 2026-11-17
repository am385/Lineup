using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace Lineup.Tools.PerformanceCollector;

/// <summary>Serializes bounded recording requests for one approving account and one temporary process lifetime.</summary>
[SupportedOSPlatform("windows")]
internal sealed class CollectorServer
{
    private readonly string _sessionId;
    private readonly string _spoolRoot;
    private readonly SecurityIdentifier _user;
    private readonly ICollectorRecorder _recorder;
    private readonly TimeProvider _timeProvider;
    private bool _cleanupRequired;

    /// <summary>Creates one collector bound to a private spool and approving Windows account.</summary>
    internal CollectorServer(string sessionId, string spoolRoot, SecurityIdentifier user, ICollectorRecorder recorder, TimeProvider? timeProvider = null)
    {
        _sessionId = sessionId;
        _spoolRoot = spoolRoot;
        _user = user;
        _recorder = recorder;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Gets the complete export allowlist; temporary WPR internals are never caller-selected.</summary>
    internal static IReadOnlySet<string> ArtifactNames { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "system.etl", "capture.json", "markers.jsonl", "process-census.csv", "wpr-start.txt", "wpr-stop.txt", "wpr-preflight.txt"
    };

    /// <summary>Serves local requests until shutdown, idle expiry, session expiry, or an unconfirmed WPR cleanup.</summary>
    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        using var expiry = new CancellationTokenSource(TimeSpan.FromMinutes(90), _timeProvider);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, expiry.Token);
        var readyPath = Path.Combine(_spoolRoot, "session.json");
        var captureCount = 0;
        var shutdown = false;
        try
        {
            await using var pipe = CollectorPlatform.CreatePipe(_sessionId, _user);
            using var self = Process.GetCurrentProcess();
            var session = new CollectorSession(_sessionId, self.Id, self.StartTime.ToUniversalTime().Ticks);
            await File.WriteAllTextAsync(readyPath + ".tmp", JsonSerializer.Serialize(session, CollectorWire.Json), lifetime.Token);
            File.Move(readyPath + ".tmp", readyPath);
            Console.WriteLine($"Collector ready: {_sessionId}. Local fixed-purpose requests only; idle expiry 10 minutes, lifetime 90 minutes.");
            while (!lifetime.IsCancellationRequested && !shutdown && !_cleanupRequired)
            {
                using var idleExpiry = new CancellationTokenSource(TimeSpan.FromMinutes(10), _timeProvider);
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, idleExpiry.Token);
                try
                {
                    await pipe.WaitForConnectionAsync(idle.Token);
                }
                catch (OperationCanceledException) when (idle.IsCancellationRequested)
                {
                    break;
                }
                try
                {
                    CollectorPlatform.ValidateClient(pipe, _user);
                    using var readExpiry = new CancellationTokenSource(TimeSpan.FromSeconds(10), _timeProvider);
                    using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, readExpiry.Token);
                    var request = await CollectorWire.ReadAsync<CollectorRequest>(pipe, readDeadline.Token);
                    request.Validate();
                    if (request.Action == "capture")
                    {
                        if (++captureCount > 20)
                        {
                            await ReplyAsync(pipe, new(false, "limit", "Session capture limit reached; start a new approved collector."));
                            continue;
                        }
                        CollectorPlatform.ValidateRoots(request.Roots);
                        shutdown = !await CaptureAsync(pipe, request, lifetime.Token);
                    }
                    else
                    {
                        shutdown = request.Action == "stop";
                        await ReplyAsync(pipe, new(true, shutdown ? "stopping" : "ready"));
                    }
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or JsonException or UnauthorizedAccessException or
                    OperationCanceledException or Win32Exception or InvalidOperationException)
                {
                    Console.Error.WriteLine($"Collector request failed: {exception.Message}. Any unexported artifacts remain in {_spoolRoot}.");
                    if (pipe.IsConnected)
                    {
                        try
                        {
                            await ReplyAsync(pipe, new(false, "request-failed", "Request rejected, disconnected, or timed out; inspect the collector console."));
                        }
                        catch (Exception writeFailure) when (writeFailure is IOException or OperationCanceledException)
                        {
                            Console.Error.WriteLine($"Could not report request failure: {writeFailure.Message}");
                        }
                    }
                }
                finally
                {
                    if (pipe.IsConnected)
                    {
                        // Disconnect discards buffered pipe output. Let the client consume its final reply and close first.
                        using var closeDeadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        closeDeadline.CancelAfter(TimeSpan.FromSeconds(10));
                        try
                        {
                            var unexpected = await pipe.ReadAsync(new byte[1], closeDeadline.Token);
                            if (unexpected != 0)
                            {
                                Console.Error.WriteLine("Unexpected data after the completed collector request; closing the connection.");
                            }
                        }
                        catch (Exception exception) when (exception is IOException or OperationCanceledException)
                        {
                            Console.Error.WriteLine($"Collector connection closed without a clean peer shutdown: {exception.Message}");
                        }
                        pipe.Disconnect();
                    }
                }
            }
        }
        finally
        {
            File.Delete(readyPath);
            var outputRoot = Path.Combine(_spoolRoot, "out");
            if (Directory.Exists(outputRoot) && !Directory.EnumerateFileSystemEntries(outputRoot).Any())
            {
                Directory.Delete(outputRoot);
            }
            if (!Directory.EnumerateFileSystemEntries(_spoolRoot).Any())
            {
                Directory.Delete(_spoolRoot);
            }
            else
            {
                Console.Error.WriteLine($"Unexported or incomplete recordings retained for recovery: {_spoolRoot}");
            }
        }
    }

    private async Task<bool> CaptureAsync(NamedPipeServerStream pipe, CollectorRequest request, CancellationToken cancellationToken)
    {
        var runId = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(_spoolRoot, "out", runId);
        CollectorRecording result;
        try
        {
            result = await _recorder.CaptureAsync(request, directory, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.Error.WriteLine($"Recording failed before a confirmed outcome: {exception.Message}");
            result = new CollectorRecording(false, false, "Recording could not confirm cleanup; inspect the collector console and retained files.");
        }
        _cleanupRequired = !result.CleanupSucceeded;
        using var checksumDeadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        List<CollectorArtifact> artifacts = [];
        foreach (var name in ArtifactNames)
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path))
            {
                await using var stream = File.OpenRead(path);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, checksumDeadline.Token));
                artifacts.Add(new CollectorArtifact(name, stream.Length, hash));
            }
        }
        await ReplyAsync(pipe, new(result.Success, result.Success ? "captured" : "capture-failed", result.Error, runId, artifacts.ToArray()));
        try
        {
            using var exportDeadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var acknowledgment = await CollectorWire.ReadAsync<CollectorAcknowledgment>(pipe, exportDeadline.Token);
            if (!string.Equals(acknowledgment.RunId, runId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Export acknowledgment did not match the owned run.");
            }
            if (result.CleanupSucceeded && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
            await ReplyAsync(pipe, new(true, result.CleanupSucceeded ? "exported" : "cleanup-required"));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or OperationCanceledException)
        {
            Console.Error.WriteLine($"Export was not acknowledged: {exception.Message}. Private recording retained at {directory}.");
        }
        return result.CleanupSucceeded;
    }

    private static async Task ReplyAsync(Stream pipe, CollectorReply reply)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await CollectorWire.WriteAsync(pipe, reply, deadline.Token);
    }
}
