using Microsoft.Extensions.Logging;

namespace Lineup.Core.VirtualOutputs.Hosting;

/// <summary>Retains authenticated IPC ownership for one public TCP connection and fails closed on revocation.</summary>
public sealed class VirtualOutputClientConnection : IAsyncDisposable
{
    private readonly Stream _connection;
    private readonly CancellationTokenSource _stop = new();
    private readonly Action _abortConnection;
    private readonly ILogger _logger;
    private readonly Task _monitor;
    private readonly Lazy<Task> _disposal;

    /// <summary>Initializes an accepted connection ownership channel.</summary>
    internal VirtualOutputClientConnection(Stream connection, Action abortConnection, ILogger logger)
    {
        _connection = connection;
        _abortConnection = abortConnection;
        _logger = logger;
        _monitor = MonitorAsync();
        _disposal = new Lazy<Task>(DisposeCoreAsync, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => new(_disposal.Value);

    private async Task MonitorAsync()
    {
        try
        {
            var signal = new byte[1];
            var received = await _connection.ReadAsync(signal, _stop.Token);
            if (!_stop.IsCancellationRequested)
            {
                if (received != 0 && signal[0] != 1)
                {
                    _logger.LogWarning("The broker returned an invalid connection-revocation signal; aborting the public connection.");
                }
                else
                {
                    _logger.LogDebug("Plugin connection ownership ended; aborting the public connection.");
                }
                _abortConnection();
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested)
        {
        }
        catch (IOException exception)
        {
            if (!_stop.IsCancellationRequested)
            {
                _logger.LogWarning(exception, "Plugin connection admission IPC failed; aborting the public connection.");
                _abortConnection();
            }
        }
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            await _stop.CancelAsync();
            await _connection.DisposeAsync();
            await _monitor;
        }
        finally
        {
            _stop.Dispose();
        }
    }
}
