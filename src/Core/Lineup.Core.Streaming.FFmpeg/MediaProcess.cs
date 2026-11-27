using System.Diagnostics;

namespace Lineup.Core.Streaming.FFmpeg;

/// <summary>
/// Describes one isolated FFmpeg or FFprobe process.
/// </summary>
public sealed record MediaProcessRequest
{
    /// <summary>Gets the executable name or path.</summary>
    public required string FileName { get; init; }

    /// <summary>Gets process arguments without shell escaping.</summary>
    public required IReadOnlyList<string> Arguments { get; init; }

    /// <summary>Gets the optional working directory.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Gets whether standard input is redirected.</summary>
    public bool RedirectStandardInput { get; init; }

    /// <summary>Gets whether standard output is redirected.</summary>
    public bool RedirectStandardOutput { get; init; }

    /// <summary>Gets whether standard error is redirected.</summary>
    public bool RedirectStandardError { get; init; } = true;
}

/// <summary>
/// Starts media processes without shell interpretation.
/// </summary>
public interface IMediaProcessFactory
{
    /// <summary>Starts a process or throws a descriptive media-process exception.</summary>
    Process Start(MediaProcessRequest request);
}

/// <summary>
/// Default FFmpeg and FFprobe process factory.
/// </summary>
public sealed class MediaProcessFactory(Lineup.Core.Common.Streaming.IMediaWorkAdmission? admission = null) : IMediaProcessFactory
{
    /// <summary>Starts the media process factory.</summary>
    /// <param name="request">The request value.</param>
    /// <returns>The operation's result.</returns>
    public Process Start(MediaProcessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            WorkingDirectory = request.WorkingDirectory ?? string.Empty,
            RedirectStandardInput = request.RedirectStandardInput,
            RedirectStandardOutput = request.RedirectStandardOutput,
            RedirectStandardError = request.RedirectStandardError,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            return MediaProcessAdmission.Start(
                () => OwnedMediaProcess.Start(startInfo), admission, request.WorkingDirectory);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            var reason = ex.NativeErrorCode == 2
                ? "was not found. Install Jellyfin FFmpeg and ensure it is on PATH"
                : $"could not be started: {ex.Message}";
            throw new MediaProcessException($"{request.FileName} {reason}.", ex);
        }
    }
}

/// <summary>
/// Indicates that FFmpeg or FFprobe could not start or complete.
/// </summary>
public sealed class MediaProcessException : Exception
{
    /// <summary>Gets the terminal process exit code when the failure followed process execution.</summary>
    public int? ExitCode { get; }

    /// <summary>Gets the bounded stderr retained for a terminal process failure.</summary>
    public string? Diagnostics { get; }

    /// <summary>Initializes an executed FFmpeg failure with structured exit status and bounded stderr.</summary>
    public MediaProcessException(int exitCode, string diagnostics)
        : base($"FFmpeg exited with code {exitCode}: {MediaProcessDiagnostics.GetTail(diagnostics)}")
    {
        ExitCode = exitCode;
        Diagnostics = MediaProcessDiagnostics.GetTail(diagnostics);
        Data[nameof(ExitCode)] = exitCode;
        Data[nameof(Diagnostics)] = Diagnostics;
    }

    /// <summary>Initializes a media-process failure.</summary>
    public MediaProcessException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a media-process failure with its cause.</summary>
    public MediaProcessException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Pumps a shared input stream into a media process and coordinates pipe shutdown.
/// </summary>
public static class MediaStreamPump
{
    /// <summary>Pumps input until the source closes, the process exits, or cancellation is requested.</summary>
    /// <param name="source">The borrowed input subscription.</param>
    /// <param name="process">The process receiving input.</param>
    /// <param name="logger">Receives bounded input-flow observations.</param>
    /// <param name="sourceName">The diagnostic source label.</param>
    /// <param name="cancellationToken">Stops input feeding.</param>
    /// <param name="sourceTerminated">Optional source-termination notification.</param>
    /// <param name="timeProvider">The input-observation clock; defaults to system time.</param>
    public static async Task PumpAsync(
        Stream source,
        Process process,
        ILogger logger,
        string sourceName,
        CancellationToken cancellationToken,
        Action<string>? sourceTerminated = null,
        TimeProvider? timeProvider = null
    )
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(process);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var flow = new InputFlowDiagnostics(process.Id, logger, timeProvider ?? TimeProvider.System);
        var input = process.StandardInput.BaseStream;
        try
        {
            var exitTask = process.WaitForExitAsync(lifetime.Token);
            var copyTask = CopyToProcessAsync(source, input, exitTask, logger, sourceName, lifetime.Token, sourceTerminated, flow);
            await Task.WhenAny(copyTask, exitTask);
            await lifetime.CancelAsync();
            try
            {
                await Task.WhenAll(copyTask, exitTask);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
        }
        finally
        {
            try
            {
                input.Close();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private static async Task CopyToProcessAsync(
        Stream source,
        Stream input,
        Task exitTask,
        ILogger logger,
        string sourceName,
        CancellationToken cancellationToken,
        Action<string>? sourceTerminated,
        InputFlowDiagnostics flow
    )
    {
        var buffer = new byte[64 * 1024];
        while (true)
        {
            int count;
            try
            {
                count = await source.ReadAsync(buffer, cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is IOException or MediaProcessException)
            {
                sourceTerminated?.Invoke("source_failure");
                throw;
            }
            if (count == 0)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    sourceTerminated?.Invoke("source_eof");
                }
                return;
            }
            var written = false;
            flow.BeginWrite();
            try
            {
                await input.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                written = true;
            }
            catch (Exception exception) when (exception is IOException || exception is ObjectDisposedException && cancellationToken.IsCancellationRequested)
            {
                logger.LogDebug(exception, "Media input pipe closed for {SourceName}", sourceName);
                await exitTask;
                return;
            }
            finally
            {
                flow.EndWrite(written ? count : 0);
            }
        }
    }

    private sealed class InputFlowDiagnostics : IDisposable
    {
        private readonly Lock _gate = new();
        private readonly TimeProvider _clock;
        private readonly ILogger _logger;
        private readonly int _processId;
        private readonly ITimer? _timer;
        private long? _writeStarted;
        private long _written;
        private TimeSpan _longest;

        /// <summary>Starts bounded process-level sampling without retaining input data or process arguments.</summary>
        public InputFlowDiagnostics(int processId, ILogger logger, TimeProvider clock)
        {
            _processId = processId;
            _logger = logger;
            _clock = clock;
            if (logger.IsEnabled(LogLevel.Information))
            {
                _timer = clock.CreateTimer(_ => Sample(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
            }
        }

        /// <summary>Records when the next stdin write begins.</summary>
        public void BeginWrite()
        {
            lock (_gate)
            {
                _writeStarted = _clock.GetTimestamp();
            }
        }

        /// <summary>Records completion without counting failed writes as delivered media.</summary>
        public void EndWrite(int bytesWritten)
        {
            lock (_gate)
            {
                if (_writeStarted is { } started)
                {
                    var elapsed = _clock.GetElapsedTime(started);
                    _longest = elapsed > _longest ? elapsed : _longest;
                }
                _writeStarted = null;
                _written += bytesWritten;
            }
        }

        private void Sample()
        {
            long written;
            TimeSpan blocked;
            TimeSpan longest;
            lock (_gate)
            {
                written = _written;
                blocked = _writeStarted is { } started ? _clock.GetElapsedTime(started) : TimeSpan.Zero;
                longest = blocked > _longest ? blocked : _longest;
            }
            _logger.LogInformation(
                "Media input flow for process {MediaProcessId}: {InputBytesWritten} bytes written; current stdin block {StdinBlockedMs} ms, longest write {LongestStdinWriteMs} ms.",
                _processId, written, blocked.TotalMilliseconds, longest.TotalMilliseconds);
        }

        /// <inheritdoc />
        public void Dispose() => _timer?.Dispose();
    }
}
