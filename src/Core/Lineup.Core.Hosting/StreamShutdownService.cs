using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lineup.Core.Hosting;

/// <summary>
/// Drains active streams and shared tuner inputs during application shutdown.
/// </summary>
public sealed class StreamShutdownService : IHostedService
{
    private readonly IActiveStreamRegistry _activeStreams;
    private readonly ISharedStreamShutdown _sharedStreams;
    private readonly ILogger<StreamShutdownService> _logger;
    private readonly TimeSpan _streamDrainTimeout;

    /// <summary>
    /// Initializes a stream shutdown service.
    /// </summary>
    /// <param name="activeStreams">Active stream lifecycle registry.</param>
    /// <param name="sharedStreams">Shared streaming producers.</param>
    /// <param name="logger">Shutdown diagnostics logger.</param>
    /// <param name="streamDrainTimeout">Optional active-stream drain timeout used by tests.</param>
    public StreamShutdownService(
        IActiveStreamRegistry activeStreams,
        ISharedStreamShutdown sharedStreams,
        ILogger<StreamShutdownService> logger,
        TimeSpan? streamDrainTimeout = null)
    {
        _activeStreams = activeStreams;
        _sharedStreams = sharedStreams;
        _logger = logger;
        _streamDrainTimeout = streamDrainTimeout ?? TimeSpan.FromSeconds(5);
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        using var drainCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        drainCancellation.CancelAfter(_streamDrainTimeout);
        try
        {
            await _activeStreams.StopAllAsync(drainCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            var remainingSessions = _activeStreams.GetActiveStreams().Select(stream => stream.SessionId).ToArray();
            _logger.LogWarning(
                "Timed out after {TimeoutSeconds} seconds waiting for active streams to stop; forcing tuner shutdown with {RemainingStreamCount} stream(s) remaining: {RemainingSessionIds}",
                _streamDrainTimeout.TotalSeconds,
                remainingSessions.Length,
                string.Join(", ", remainingSessions));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Active stream shutdown failed; forcing tuner shutdown");
        }

        using var tunerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        tunerCancellation.CancelAfter(_streamDrainTimeout);
        try
        {
            await _sharedStreams.StopAsync(tunerCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "Timed out after {TimeoutSeconds} seconds waiting for shared tuner sources to stop; application shutdown will continue",
                _streamDrainTimeout.TotalSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Shared tuner source shutdown failed; application shutdown will continue");
        }
    }
}
