using System.Collections.Concurrent;
using Lineup.Core.Common.Streaming;
using Lineup.Core.VirtualOutputs.Protocol;
using Microsoft.Extensions.Logging;

namespace Lineup.Core.VirtualOutputs.Runtime;

/// <summary>Tracks broker stream ownership and isolates independent orphan cleanup failures.</summary>
internal sealed class VirtualOutputBrokerStreamRegistry
{
    private static readonly TimeSpan OrphanSweepInterval = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<VirtualOutputStreamRequestId, Entry> _streams = new();
    private readonly IVirtualOutputSessionAuthority _authority;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes stream ownership, cleanup logging, and the orphan-sweep clock.</summary>
    internal VirtualOutputBrokerStreamRegistry(IVirtualOutputSessionAuthority authority, ILogger logger, TimeProvider? timeProvider = null)
    {
        _authority = authority;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal bool TryAdd(VirtualDeviceId virtualDeviceId, VirtualOutputOpenStreamRequest request, VirtualOutputBrokeredStream stream)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(stream);
        var streamSessionId = stream.Response.StreamSessionId
            ?? throw new InvalidOperationException("An accepted broker stream must include a Lineup stream session ID.");
        return _streams.TryAdd(
            request.RequestId,
            new Entry(virtualDeviceId, request.Session, request.RequestId, streamSessionId, stream));
    }

    internal int Count(VirtualDeviceId virtualDeviceId) =>
        _streams.Values.Count(entry => entry.VirtualDeviceId == virtualDeviceId);

    internal bool Matches(
        VirtualOutputStreamRequestId requestId,
        VirtualOutputSessionContext session,
        StreamSessionId streamSessionId)
    {
        return _streams.TryGetValue(requestId, out var entry) &&
            entry.Session == session &&
            entry.StreamSessionId == streamSessionId;
    }

    internal async Task<bool> CloseAsync(VirtualOutputCloseStreamRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_streams.TryGetValue(request.RequestId, out var entry))
        {
            return false;
        }
        if (entry.Session != request.Session || entry.StreamSessionId != request.StreamSessionId)
        {
            throw new VirtualOutputBrokerException(
                VirtualOutputFailureCode.InvalidRequest,
                "The broker stream close request does not match the opened stream.");
        }
        if (!_streams.TryRemove(request.RequestId, out entry))
        {
            return false;
        }
        await entry.Stream.DisposeAsync();
        return true;
    }

    internal async Task CompleteAsync(VirtualOutputStreamRequestId requestId, VirtualOutputBrokeredStream stream)
    {
        if (_streams.TryGetValue(requestId, out var entry) &&
            ReferenceEquals(entry.Stream, stream) &&
            _streams.TryRemove(requestId, out entry))
        {
            await entry.Stream.DisposeAsync();
        }
    }

    /// <summary>Attempts every unauthorized stream independently and logs incomplete disposal without retrying its ownership.</summary>
    internal async Task ExpireUnauthorizedAsync()
    {
        foreach (var (requestId, entry) in _streams.ToArray())
        {
            var authorization = _authority.Authorize(entry.Session, VirtualOutputCapability.OpenStream);
            if (authorization.Authorized || !_streams.TryRemove(requestId, out var removed))
            {
                continue;
            }
            _logger.LogWarning(
                "Closing orphaned broker stream {StreamSessionId} for request {RequestId} because its worker session is no longer authorized.",
                removed.StreamSessionId,
                removed.RequestId);
            try
            {
                await removed.Stream.DisposeAsync();
            }
            catch (Exception exception)
            {
                LogDisposalFailure(removed, exception);
            }
        }
    }

    /// <summary>Runs successive orphan sweeps until cancellation; unexpected maintenance faults propagate to the IPC service.</summary>
    internal async Task RunExpirationLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(OrphanSweepInterval, _timeProvider, cancellationToken);
                await ExpireUnauthorizedAsync();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>Attempts every remaining stream and reports all incomplete disposals after independent cleanup finishes.</summary>
    internal async Task DisposeAllAsync()
    {
        List<Exception>? failures = null;
        foreach (var requestId in _streams.Keys)
        {
            if (_streams.TryRemove(requestId, out var entry))
            {
                try
                {
                    await entry.Stream.DisposeAsync();
                }
                catch (Exception exception)
                {
                    LogDisposalFailure(entry, exception);
                    (failures ??= []).Add(exception);
                }
            }
        }
        if (failures is not null)
        {
            throw new AggregateException("One or more broker streams could not be disposed during shutdown.", failures);
        }
    }

    private void LogDisposalFailure(Entry entry, Exception exception) =>
        _logger.LogError(exception,
            "Cleanup of broker stream {StreamSessionId} for request {RequestId} and virtual device {VirtualDeviceId} failed; resource cleanup is incomplete.",
            entry.StreamSessionId, entry.RequestId, entry.VirtualDeviceId);

    private sealed record Entry(
        VirtualDeviceId VirtualDeviceId,
        VirtualOutputSessionContext Session,
        VirtualOutputStreamRequestId RequestId,
        StreamSessionId StreamSessionId,
        VirtualOutputBrokeredStream Stream);
}
