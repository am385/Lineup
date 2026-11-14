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
                () => Process.Start(startInfo) ?? throw new MediaProcessException($"Failed to start {request.FileName}."), admission, request.WorkingDirectory);
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
    public static async Task PumpAsync(Stream source, Process process, ILogger logger, string sourceName, CancellationToken cancellationToken, Action<string>? sourceTerminated = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(process);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            var copyTask = CopyToProcessAsync(source, process, logger, sourceName, lifetime.Token, sourceTerminated);
            var exitTask = process.WaitForExitAsync(lifetime.Token);
            await Task.WhenAny(copyTask, exitTask);
            await lifetime.CancelAsync();
            try
            {
                await copyTask;
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
        }
        finally
        {
            try
            {
                process.StandardInput.BaseStream.Close();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private static async Task CopyToProcessAsync(Stream source, Process process, ILogger logger, string sourceName, CancellationToken cancellationToken, Action<string>? sourceTerminated)
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
            try
            {
                await process.StandardInput.BaseStream.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            }
            catch (IOException exception)
            {
                logger.LogDebug(exception, "Media input pipe closed for {SourceName}", sourceName);
                await process.WaitForExitAsync(cancellationToken);
                return;
            }
        }
    }
}
