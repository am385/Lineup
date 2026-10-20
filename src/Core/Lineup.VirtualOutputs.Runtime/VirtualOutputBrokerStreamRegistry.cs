using System.Collections.Concurrent;
using Lineup.Common.Streaming;
using Lineup.VirtualOutputs.Protocol;
using Microsoft.Extensions.Logging;

namespace Lineup.VirtualOutputs.Runtime;

internal sealed class VirtualOutputBrokerStreamRegistry
{
    private static readonly TimeSpan OrphanSweepInterval = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<VirtualOutputStreamRequestId, Entry> _streams = new();
    private readonly IVirtualOutputSessionAuthority _authority;
    private readonly ILogger _logger;

    internal VirtualOutputBrokerStreamRegistry(IVirtualOutputSessionAuthority authority, ILogger logger)
    {
        _authority = authority;
        _logger = logger;
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
            await removed.Stream.DisposeAsync();
        }
    }

    internal async Task RunExpirationLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(OrphanSweepInterval, cancellationToken);
                await ExpireUnauthorizedAsync();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    internal async Task DisposeAllAsync()
    {
        foreach (var requestId in _streams.Keys)
        {
            if (_streams.TryRemove(requestId, out var entry))
            {
                await entry.Stream.DisposeAsync();
            }
        }
    }

    private sealed record Entry(
        VirtualDeviceId VirtualDeviceId,
        VirtualOutputSessionContext Session,
        VirtualOutputStreamRequestId RequestId,
        StreamSessionId StreamSessionId,
        VirtualOutputBrokeredStream Stream);
}
