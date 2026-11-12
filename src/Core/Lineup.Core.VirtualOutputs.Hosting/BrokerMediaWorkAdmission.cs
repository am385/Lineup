using Lineup.Core.Common.Streaming;
using Lineup.Core.VirtualOutputs.Protocol;
using Microsoft.Extensions.Logging;

namespace Lineup.Core.VirtualOutputs.Hosting;

/// <summary>Owns deployment-wide media permits through authenticated, fail-closed local IPC channels.</summary>
public sealed class BrokerMediaWorkAdmission(VirtualOutputBrokerClient client, VirtualOutputSessionContext session, Action ownershipLost, ILogger logger) : IMediaWorkAdmission
{
    private int _minimumFreeSpaceMib = -1;

    /// <inheritdoc />
    public async ValueTask<IAsyncDisposable> AcquireAsync(MediaWorkKind kind, Func<CancellationToken, Task<bool>>? retireIdle = null, CancellationToken cancellationToken = default)
    {
        var (lease, response) = await client.ReserveMediaWorkAsync(new(session, kind == MediaWorkKind.Processing, retireIdle is not null), retireIdle, ownershipLost, logger, cancellationToken);
        Volatile.Write(ref _minimumFreeSpaceMib, response.MinimumFreeSpaceMib);
        return lease;
    }

    /// <inheritdoc />
    public Task ReclaimIdleAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Worker media reclamation is coordinated by the trusted deployment admission service.");

    /// <inheritdoc />
    public void CheckWorkspace(string path)
    {
        var watermark = Volatile.Read(ref _minimumFreeSpaceMib);
        if (watermark < 0)
        {
            throw new InvalidOperationException("The broker must admit media ownership before workspace allocation.");
        }
        MediaWorkspaceAdmission.Check(path, watermark);
    }
}

/// <summary>Provides authenticated media-resource ownership alongside the worker capability broker.</summary>
public sealed partial class VirtualOutputBrokerClient
{
    internal async Task<(IAsyncDisposable Lease, VirtualOutputMediaWorkResponse Response)> ReserveMediaWorkAsync(
        VirtualOutputMediaWorkRequest request,
        Func<CancellationToken, Task<bool>>? retireIdle,
        Action ownershipLost,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var connection = await ConnectAsync(deadline.Token);
        try
        {
            await VirtualOutputWireProtocol.WriteAsync(connection, VirtualOutputIpcRequest.Create(VirtualOutputIpcOperation.ReserveMediaWork, request), deadline.Token);
            var response = await VirtualOutputWireProtocol.ReadAsync<VirtualOutputIpcResponse>(connection, deadline.Token);
            ThrowIfFailed(response);
            var reservation = response.ReadPayload<VirtualOutputMediaWorkResponse>();
            return (new MediaLease(connection, retireIdle, ownershipLost, logger), reservation);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private sealed class MediaLease : IAsyncDisposable
    {
        private readonly Stream _connection;
        private readonly Func<CancellationToken, Task<bool>>? _retireIdle;
        private readonly Action _ownershipLost;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SemaphoreSlim _writes = new(1, 1);
        private readonly CancellationToken _token;
        private int _disposed;
        private int _retiring;

        internal MediaLease(Stream connection, Func<CancellationToken, Task<bool>>? retireIdle, Action ownershipLost, ILogger logger)
        {
            _connection = connection;
            _retireIdle = retireIdle;
            _ownershipLost = ownershipLost;
            _logger = logger;
            _token = _lifetime.Token;
            _ = MonitorAsync();
        }

        private async Task MonitorAsync()
        {
            try
            {
                var command = new byte[1];
                while (await _connection.ReadAsync(command, _token) != 0)
                {
                    if (command[0] == 3)
                    {
                        _released.TrySetResult();
                        return;
                    }
                    if (command[0] != 1)
                    {
                        if (Volatile.Read(ref _disposed) == 0)
                        {
                            _logger.LogError("Media reservation ownership was revoked; stopping dedicated playback.");
                            _ownershipLost();
                        }
                        continue;
                    }
                    if (Interlocked.CompareExchange(ref _retiring, 1, 0) == 0)
                    {
                        _ = RetireAsync();
                    }
                }
                _released.TrySetException(new IOException("The media reservation ownership channel closed before acknowledging release."));
                if (Volatile.Read(ref _disposed) == 0)
                {
                    _logger.LogError("Media reservation ownership was lost; stopping dedicated playback.");
                    _ownershipLost();
                }
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
            {
                _released.TrySetException(exception);
                if (Volatile.Read(ref _disposed) == 0)
                {
                    _logger.LogError(exception, "Media reservation control failed; stopping dedicated playback.");
                    _ownershipLost();
                }
            }
        }

        private async Task RetireAsync()
        {
            try
            {
                var retired = _retireIdle is not null && await _retireIdle(_token);
                if (Volatile.Read(ref _disposed) == 0)
                {
                    await SendAsync(retired ? (byte)1 : (byte)0);
                }
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
            {
                if (Volatile.Read(ref _disposed) == 0)
                {
                    _logger.LogError(exception, "Media retirement control failed; stopping dedicated playback.");
                    _ownershipLost();
                }
            }
            finally
            {
                Volatile.Write(ref _retiring, 0);
            }
        }

        private async Task SendAsync(byte command)
        {
            await _writes.WaitAsync(_token);
            try
            {
                await _connection.WriteAsync(new byte[] { command }, _token);
                await _connection.FlushAsync(_token);
            }
            finally
            {
                _writes.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                await _released.Task;
                return;
            }
            try
            {
                await SendAsync(3);
                await _released.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                _lifetime.Cancel();
                await _connection.DisposeAsync();
                _lifetime.Dispose();
            }
        }
    }
}
