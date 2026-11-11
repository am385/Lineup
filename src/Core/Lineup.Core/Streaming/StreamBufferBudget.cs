using System.Diagnostics.Metrics;

namespace Lineup.Core.Streaming;

/// <summary>Accounts distinct shared payload arrays and evicts lagging subscriptions without blocking producers.</summary>
internal sealed class StreamBufferBudget : IDisposable
{
    private static readonly Meter Metrics = new("Lineup.Streaming");
    private static readonly UpDownCounter<long> BufferedPayload = Metrics.CreateUpDownCounter<long>("lineup.streaming.buffered_bytes", "By", "Distinct payload bytes retained by shared-stream queues.");
    private static readonly Counter<long> BufferEvictions = Metrics.CreateCounter<long>("lineup.streaming.buffer_evictions", description: "Subscriptions evicted by bounded buffering policy.");
    private readonly Lock _gate = new();
    private readonly HashSet<Subscription> _subscriptions = [];
    private readonly TimeProvider _clock;
    private readonly long _subscriberLimit;
    private readonly long _graphLimit;
    private readonly TimeSpan _maximumAge;
    private ITimer? _expiration;
    private bool _disposed;
    private long _retainedBytes;
    private long _evictions;

    /// <summary>Initializes a graph-wide budget using a monotonic clock.</summary>
    internal StreamBufferBudget(StreamingResourceOptions options, TimeProvider clock)
    {
        options.Validate();
        _clock = clock;
        _subscriberLimit = (long)options.SubscriberBufferMib * 1024 * 1024;
        _graphLimit = (long)options.GraphBufferMib * 1024 * 1024;
        _maximumAge = TimeSpan.FromSeconds(options.MaximumBufferAgeSeconds);
    }

    /// <summary>Gets distinct payload bytes retained by queues, counting shared arrays only once.</summary>
    internal long RetainedBytes { get { lock (_gate) { return _retainedBytes; } } }

    /// <summary>Gets the process-lifetime count of subscriptions evicted by this graph.</summary>
    internal long Evictions { get { lock (_gate) { return _evictions; } } }

    /// <summary>Creates an empty subscription with an asynchronous eviction notification.</summary>
    internal Subscription Subscribe(Action evicted)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var subscription = new Subscription(this, evicted);
            _subscriptions.Add(subscription);
            return subscription;
        }
    }

    /// <summary>Publishes one array to eligible readers and evicts the largest backlogs first on aggregate exhaustion.</summary>
    internal void Publish(byte[] payload, IEnumerable<Subscription> subscribers)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            Expire();
            var receivers = subscribers.Where(subscription => !subscription.Completed).ToArray();
            foreach (var subscription in receivers.Where(subscription => subscription.Bytes + payload.Length > _subscriberLimit || subscription.Queue.Count >= 2048))
            {
                var entryLimit = subscription.Queue.Count >= 2048;
                Evict(subscription, entryLimit ? "The consumer exceeded its shared-stream queue-entry limit." : "The consumer exceeded its shared-stream byte budget.", entryLimit ? "entries" : "bytes");
            }
            while (_retainedBytes + payload.Length > _graphLimit && receivers.Any(subscription => !subscription.Completed))
            {
                var slowest = _subscriptions.Where(subscription => subscription.Bytes > 0).MaxBy(subscription => subscription.Bytes);
                if (slowest is null)
                {
                    break;
                }
                Evict(slowest, "The consumer was evicted to preserve the shared-stream memory budget.", "aggregate");
            }
            receivers = receivers.Where(subscription => !subscription.Completed).ToArray();
            if (receivers.Length > 0)
            {
                var chunk = new Chunk(payload, _clock.GetTimestamp(), receivers.Length);
                _retainedBytes += payload.Length;
                BufferedPayload.Add(payload.Length);
                foreach (var subscription in receivers)
                {
                    subscription.Queue.Enqueue(new Cursor(chunk));
                    subscription.Bytes += payload.Length;
                    subscription.Available.TrySetResult();
                }
            }
            ScheduleExpiration();
        }
    }

    /// <summary>Completes a subscription, preserving ordinary EOF data but releasing failed or explicitly closed queues immediately.</summary>
    internal void Complete(Subscription subscription, Exception? failure = null, bool discard = false)
    {
        lock (_gate)
        {
            subscription.Completed = true;
            subscription.Failure ??= failure;
            if (discard || failure is not null)
            {
                Drain(subscription);
            }
            if (subscription.Queue.Count == 0)
            {
                _subscriptions.Remove(subscription);
            }
            subscription.Available.TrySetResult();
            ScheduleExpiration();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _expiration?.Dispose();
            foreach (var subscription in _subscriptions.ToArray())
            {
                Complete(subscription, discard: true);
            }
        }
    }

    private async ValueTask<int> ReadAsync(Subscription subscription, Memory<byte> destination, CancellationToken cancellationToken)
    {
        if (destination.Length == 0)
        {
            return 0;
        }
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task available;
            lock (_gate)
            {
                Expire();
                if (subscription.Failure is { } failure)
                {
                    throw new IOException("The shared stream subscription stopped.", failure);
                }
                if (subscription.Queue.TryPeek(out var cursor))
                {
                    var count = Math.Min(destination.Length, cursor.Chunk.Payload.Length - cursor.Offset);
                    cursor.Chunk.Payload.AsMemory(cursor.Offset, count).CopyTo(destination);
                    cursor.Offset += count;
                    if (cursor.Offset == cursor.Chunk.Payload.Length)
                    {
                        subscription.Queue.Dequeue();
                        Release(subscription, cursor.Chunk);
                        if (subscription.Completed && subscription.Queue.Count == 0)
                        {
                            _subscriptions.Remove(subscription);
                        }
                    }
                    ScheduleExpiration();
                    return count;
                }
                if (subscription.Completed)
                {
                    return 0;
                }
                if (subscription.Available.Task.IsCompleted)
                {
                    subscription.Available = new(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                available = subscription.Available.Task;
            }
            await available.WaitAsync(cancellationToken);
        }
    }

    private void Evict(Subscription subscription, string reason, string code)
    {
        _evictions++;
        BufferEvictions.Add(1, new KeyValuePair<string, object?>("reason", code));
        Complete(subscription, new IOException(reason), discard: true);
        ThreadPool.QueueUserWorkItem(_ => subscription.Evicted());
    }

    private void Drain(Subscription subscription)
    {
        while (subscription.Queue.TryDequeue(out var cursor))
        {
            Release(subscription, cursor.Chunk);
        }
    }

    private void Release(Subscription subscription, Chunk chunk)
    {
        subscription.Bytes -= chunk.Payload.Length;
        if (--chunk.References == 0)
        {
            _retainedBytes -= chunk.Payload.Length;
            BufferedPayload.Add(-chunk.Payload.Length);
        }
    }

    private void Expire()
    {
        foreach (var subscription in _subscriptions.Where(subscription =>
                     subscription.Queue.TryPeek(out var cursor) && _clock.GetElapsedTime(cursor.Chunk.Timestamp) >= _maximumAge).ToArray())
        {
            Evict(subscription, "The consumer exceeded its shared-stream media age budget.", "age");
        }
    }

    private void ScheduleExpiration()
    {
        if (_disposed)
        {
            return;
        }
        var timestamps = _subscriptions.Where(subscription => subscription.Queue.Count > 0).Select(subscription => subscription.Queue.Peek().Chunk.Timestamp).ToArray();
        if (timestamps.Length == 0)
        {
            _expiration?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return;
        }
        var remaining = _maximumAge - _clock.GetElapsedTime(timestamps.Min());
        _expiration ??= _clock.CreateTimer(_ =>
        {
            lock (_gate)
            {
                if (!_disposed)
                {
                    Expire();
                    ScheduleExpiration();
                }
            }
        }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _expiration.Change(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Tracks the shared ownership of one queued payload allocation.</summary>
    internal sealed class Chunk(byte[] payload, long timestamp, int references)
    {
        /// <summary>Gets the owned bytes.</summary>
        internal byte[] Payload { get; } = payload;
        /// <summary>Gets the enqueue timestamp.</summary>
        internal long Timestamp { get; } = timestamp;
        /// <summary>Gets or sets the number of queues retaining the array.</summary>
        internal int References { get; set; } = references;
    }

    /// <summary>Retains one subscriber's position without prematurely releasing its payload.</summary>
    internal sealed class Cursor(Chunk chunk)
    {
        /// <summary>Gets the retained allocation.</summary>
        internal Chunk Chunk { get; } = chunk;
        /// <summary>Gets or sets bytes consumed from the allocation.</summary>
        internal int Offset { get; set; }
    }

    /// <summary>Owns a byte-accounted queue, including its partially consumed head array.</summary>
    internal sealed class Subscription(StreamBufferBudget owner, Action evicted)
    {
        /// <summary>Gets payload retained by this reader; access requires the owning budget lock.</summary>
        internal Queue<Cursor> Queue { get; } = [];
        /// <summary>Gets or sets retained array bytes.</summary>
        internal long Bytes { get; set; }
        /// <summary>Gets or sets whether further publication is rejected.</summary>
        internal bool Completed { get; set; }
        /// <summary>Gets or sets the terminal failure.</summary>
        internal Exception? Failure { get; set; }
        /// <summary>Gets the deferred node-cleanup callback.</summary>
        internal Action Evicted { get; } = evicted;
        /// <summary>Gets or sets the pending-read signal.</summary>
        internal TaskCompletionSource Available { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Reads queued bytes within the graph budget.</summary>
        internal ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken) => owner.ReadAsync(this, destination, cancellationToken);

        /// <summary>Releases queued payload when its stream owner is disposed.</summary>
        internal void Close() => owner.Complete(this, discard: true);
    }
}
