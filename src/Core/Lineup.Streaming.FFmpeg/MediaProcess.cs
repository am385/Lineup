using System.Diagnostics;

namespace Lineup.Streaming.FFmpeg;

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
public sealed class MediaProcessFactory : IMediaProcessFactory
{
    /// <inheritdoc />
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
            return Process.Start(startInfo) ?? throw new MediaProcessException($"Failed to start {request.FileName}.");
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
    public static async Task PumpAsync(Stream source, Process process, ILogger logger, string sourceName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(process);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            var copyTask = source.CopyToAsync(process.StandardInput.BaseStream, lifetime.Token);
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
            catch (IOException ex)
            {
                logger.LogDebug(ex, "Media input pipe closed for {SourceName}", sourceName);
            }
        }
        finally
        {
            try
            {
                process.StandardInput.Close();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
