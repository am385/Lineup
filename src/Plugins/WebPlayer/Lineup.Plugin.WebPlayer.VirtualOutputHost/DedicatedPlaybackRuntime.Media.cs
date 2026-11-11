using System.Diagnostics;
using Lineup.Core.Common.Streaming;
using Lineup.Core.Streaming.FFmpeg;
using Lineup.Core.VirtualOutputs.Hosting;
using Lineup.Core.VirtualOutputs.Protocol;
using Lineup.Plugin.WebPlayer;
using Lineup.Plugin.WebPlayer.FFmpeg;

namespace Lineup.Plugin.WebPlayer.VirtualOutputHost;

internal sealed partial class DedicatedPlaybackRuntime
{
    private static Process StartMediaProcess(string directory, IReadOnlyList<string> arguments)
    {
        var configuredExecutable = Environment.GetEnvironmentVariable("LINEUP_FFMPEG_EXECUTABLE");
        var executable = string.IsNullOrWhiteSpace(configuredExecutable) ? "ffmpeg" : configuredExecutable;
        var startInfo = new ProcessStartInfo
        {
            FileName = executable.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? "dotnet" : executable,
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true
        };
        if (executable.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add(executable);
        }
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start FFmpeg for the Web Player output.");
    }

    private static async Task<MediaSession> StartMediaSessionAsync(string id, string directory, VirtualOutputClientStream source, DedicatedCmafSessionPlan plan, ILogger logger)
    {
        await using var guard = new MediaSessionStartupGuard(directory, source);
        var process = StartMediaProcess(directory, plan.Arguments);
        guard.RegisterProcess(process);
        Process? captionProcess = null;
        if (plan.EmbeddedCaptions is not null)
        {
            captionProcess = StartMediaProcess(directory, WebPlayerTrackPlanner.CreateEmbeddedCaptionArguments(plan.SubtitlePaths[plan.EmbeddedCaptions.Index]));
            guard.RegisterProcess(captionProcess);
        }
        var session = new MediaSession(id, directory, process, captionProcess, source, plan, new CancellationTokenSource(), logger);
        guard.TransferOwnership();
        session.PumpTask = PumpInputAsync(session);
        return session;
    }

    private static async Task PumpInputAsync(MediaSession session)
    {
        var buffer = new byte[64 * 1024];
        var captionInputAvailable = session.CaptionProcess is not null;
        try
        {
            while (true)
            {
                var bytesRead = await session.Source.Stream.ReadAsync(buffer, session.Lifetime.Token);
                if (bytesRead == 0)
                {
                    break;
                }
                try
                {
                    await session.Process.StandardInput.BaseStream.WriteAsync(buffer.AsMemory(0, bytesRead), session.Lifetime.Token);
                }
                catch (IOException)
                {
                    await session.Process.WaitForExitAsync(session.Lifetime.Token);
                    break;
                }
                if (captionInputAvailable)
                {
                    try
                    {
                        await session.CaptionProcess!.StandardInput.BaseStream.WriteAsync(buffer.AsMemory(0, bytesRead), session.Lifetime.Token);
                    }
                    catch (Exception exception) when (exception is IOException or ObjectDisposedException)
                    {
                        captionInputAvailable = false;
                        session.CaptionProcess!.StandardInput.BaseStream.Close();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (session.Lifetime.IsCancellationRequested)
        {
        }
        catch (IOException exception)
        {
            throw new MediaInputException("The upstream media stream failed before the presentation was ready.", exception);
        }
        finally
        {
            session.Process.StandardInput.BaseStream.Close();
            session.CaptionProcess?.StandardInput.BaseStream.Close();
        }
    }

    private static async Task ObserveProcessAsync(MediaSession session, ICmafOutputCoordinator sessions)
    {
        await session.Process.WaitForExitAsync();
        await sessions.ReleaseOutputAsync(new StreamSessionId(Guid.ParseExact(session.Id, "N")));
    }

    private static Task WaitForOutputAsync(MediaSession session, TimeSpan timeout, CancellationToken cancellationToken) =>
        DedicatedMediaReadiness.WaitAsync(
            session.Process,
            session.ErrorTask,
            session.PumpTask,
            Path.Combine(session.Directory, CmafStreamPlanner.DashManifestName),
            Path.Combine(session.Directory, CmafStreamPlanner.HlsManifestName),
            session.Plan.AudioRenditions.Count + 1,
            timeout,
            cancellationToken);

    /// <summary>Resolves an allowed media filename beneath its producer-owned directory.</summary>
    internal static bool TryResolveMediaFile(string directory, string filename, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(filename) ||
            Path.GetFileName(filename) != filename ||
            !new[] { ".mpd", ".m3u8", ".m4s", ".mp4", ".vtt" }.Contains(Path.GetExtension(filename), StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }
        var root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(directory, filename));
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        path = candidate;
        return true;
    }

    /// <summary>Owns one FFmpeg attempt, its caption process, input pump, diagnostics, broker source, and artifacts.</summary>
    internal sealed class MediaSession : IAsyncDisposable
    {
        private int _disposed;
        private bool _mediaStopped;

        /// <summary>Initializes media ownership after process creation succeeds.</summary>
        public MediaSession(string id, string directory, Process process, Process? captionProcess, VirtualOutputClientStream source, DedicatedCmafSessionPlan plan, CancellationTokenSource lifetime, ILogger logger)
        {
            Id = id;
            Directory = directory;
            Process = process;
            CaptionProcess = captionProcess;
            Source = source;
            Plan = plan;
            Lifetime = lifetime;
            Logger = logger;
            ErrorTask = ReadDiagnosticTailAsync(process.StandardError);
            CaptionErrorTask = captionProcess is null ? null : ReadDiagnosticTailAsync(captionProcess.StandardError);
        }

        /// <summary>Gets the stable producer identity.</summary>
        public string Id { get; }

        /// <summary>Gets the producer-owned artifact directory.</summary>
        public string Directory { get; }

        /// <summary>Gets the main media process.</summary>
        public Process Process { get; }

        /// <summary>Gets the optional embedded-caption process.</summary>
        public Process? CaptionProcess { get; }

        /// <summary>Gets the retained broker input.</summary>
        public VirtualOutputClientStream Source { get; }

        /// <summary>Gets the effective presentation plan.</summary>
        public DedicatedCmafSessionPlan Plan { get; }

        /// <summary>Gets cancellation for this media attempt.</summary>
        public CancellationTokenSource Lifetime { get; }

        /// <summary>Gets the lifecycle logger.</summary>
        public ILogger Logger { get; }

        /// <summary>Gets or sets the owned media and caption input pump.</summary>
        public Task? PumpTask { get; set; }

        /// <summary>Gets the bounded, continuously drained media diagnostics.</summary>
        public Task<string> ErrorTask { get; }

        /// <summary>Gets the bounded, continuously drained caption diagnostics.</summary>
        public Task<string>? CaptionErrorTask { get; }

        /// <summary>Gets the final processor exit code retained after process disposal.</summary>
        public int? ExitCode { get; private set; }

        /// <summary>Drains FFmpeg stderr without owning the reader, retaining at most fifty non-empty lines from the last 16,384 characters with normalized LF separators.</summary>
        internal static Task<string> ReadDiagnosticTailAsync(TextReader reader) => MediaProcessDiagnostics.ReadTailAsync(reader);

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            try
            {
                await StopMediaAsync();
                await CloseBrokerStreamAsync();
            }
            finally
            {
                try
                {
                    await Source.DisposeAsync();
                }
                finally
                {
                    await MediaSessionStartupGuard.DeleteDirectoryAsync(Directory);
                }
            }
        }

        /// <summary>Stops and releases this media attempt without closing its reusable broker source or deleting output.</summary>
        internal async Task StopMediaAsync()
        {
            if (_mediaStopped)
            {
                return;
            }
            _mediaStopped = true;
            Lifetime.Cancel();
            try
            {
                if (!Process.HasExited)
                {
                    Process.Kill(entireProcessTree: true);
                }
                if (CaptionProcess is not null && !CaptionProcess.HasExited)
                {
                    CaptionProcess.Kill(entireProcessTree: true);
                }
                await Process.WaitForExitAsync();
                ExitCode = Process.ExitCode;
                if (CaptionProcess is not null)
                {
                    await CaptionProcess.WaitForExitAsync();
                }
                if (PumpTask is not null)
                {
                    try
                    {
                        await PumpTask;
                    }
                    catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
                    {
                    }
                }
                await ErrorTask;
                if (CaptionErrorTask is not null)
                {
                    await CaptionErrorTask;
                }
            }
            finally
            {
                Process.Dispose();
                CaptionProcess?.Dispose();
                Lifetime.Dispose();
            }
        }

        private async Task CloseBrokerStreamAsync()
        {
            using var closeCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                var closed = await Source.CloseAsync(closeCancellation.Token);
                Logger.LogDebug(
                    "Dedicated Web Player session {MediaSessionId} closed broker request {BrokerRequestId} and stream {StreamSessionId}: {Closed}.",
                    Id,
                    Source.RequestId,
                    Source.Response.StreamSessionId,
                    closed);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException or VirtualOutputClientException)
            {
                Logger.LogWarning(
                    exception,
                    "Dedicated Web Player session {MediaSessionId} could not confirm closure of broker request {BrokerRequestId} and stream {StreamSessionId}; releasing the IPC connection as fallback.",
                    Id,
                    Source.RequestId,
                    Source.Response.StreamSessionId);
            }
        }
    }

}
