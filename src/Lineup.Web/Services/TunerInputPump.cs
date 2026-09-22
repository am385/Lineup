using System.Diagnostics;
using System.Threading.Channels;

namespace Lineup.Web.Services;

/// <summary>
/// Connects a shared tuner subscription to an FFmpeg or FFprobe standard-input pipe.
/// </summary>
public static class TunerInputPump
{
    /// <summary>
    /// Pumps shared tuner bytes until the media process exits, the source closes, or the request is canceled.
    /// </summary>
    /// <param name="multiplexer">Provides the shared tuner subscription.</param>
    /// <param name="sourceUri">Identifies the tuner stream to share.</param>
    /// <param name="process">The media process receiving bytes through standard input.</param>
    /// <param name="logger">Logs input-pipe shutdown details.</param>
    /// <param name="cancellationToken">Stops the pump when the client disconnects.</param>
    /// <param name="onSourceError">Optionally receives a shared tuner source failure.</param>
    public static async Task PumpAsync(ITunerStreamMultiplexer multiplexer, Uri sourceUri, Process process, ILogger logger, CancellationToken cancellationToken, Action<Exception>? onSourceError = null)
    {
        var source = await multiplexer.SubscribeAsync(sourceUri, cancellationToken);
        await PumpAsync(source, sourceUri, process, logger, cancellationToken, onSourceError);
    }

    /// <summary>
    /// Pumps an existing tuner subscription until the media process exits, the source closes, or the request is canceled.
    /// </summary>
    /// <param name="source">The tuner subscription, which is disposed when pumping ends.</param>
    /// <param name="sourceUri">Identifies the tuner stream for logging.</param>
    /// <param name="process">The media process receiving bytes through standard input.</param>
    /// <param name="logger">Logs input-pipe shutdown details.</param>
    /// <param name="cancellationToken">Stops the pump when the client disconnects.</param>
    /// <param name="onSourceError">Optionally receives a shared tuner source failure.</param>
    public static async Task PumpAsync(Stream source, Uri sourceUri, Process process, ILogger logger, CancellationToken cancellationToken, Action<Exception>? onSourceError = null)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using (source)
        {
            try
            {
                var copyTask = source.CopyToAsync(process.StandardInput.BaseStream, lifetime.Token);
                var exitTask = process.WaitForExitAsync(lifetime.Token);
                await Task.WhenAny(copyTask, exitTask);
                lifetime.Cancel();

                try
                {
                    await copyTask;
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                {
                }
                catch (IOException ex)
                {
                    logger.LogDebug(ex, "Shared tuner input pipe closed for {SourceUri}", sourceUri);
                }
                catch (ChannelClosedException ex)
                {
                    logger.LogDebug(ex, "Shared tuner source closed for {SourceUri}", sourceUri);
                    onSourceError?.Invoke(ex.InnerException ?? ex);
                }
                catch (ObjectDisposedException) when (process.HasExited)
                {
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
}
