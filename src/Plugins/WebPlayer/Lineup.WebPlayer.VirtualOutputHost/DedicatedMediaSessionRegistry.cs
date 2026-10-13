using System.Diagnostics.CodeAnalysis;

namespace Lineup.WebPlayer.VirtualOutputHost;

internal sealed class DedicatedMediaSessionRegistry<TSession>
    where TSession : class, IAsyncDisposable
{
    private static readonly TimeSpan MaximumSweepInterval = TimeSpan.FromSeconds(30);
    private readonly Dictionary<string, Entry> _sessions = [];
    private readonly Lock _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _inactivityTimeout;

    internal DedicatedMediaSessionRegistry(TimeProvider timeProvider, TimeSpan inactivityTimeout)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (inactivityTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(inactivityTimeout));
        }
        _timeProvider = timeProvider;
        _inactivityTimeout = inactivityTimeout;
    }

    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _sessions.Count;
            }
        }
    }

    internal bool TryAdd(string id, TSession session)
    {
        lock (_gate)
        {
            return _sessions.TryAdd(id, new Entry(session, _timeProvider.GetUtcNow()));
        }
    }

    internal bool TryGetAndTouch(string id, [NotNullWhen(true)] out TSession? session)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(id, out var entry))
            {
                session = null;
                return false;
            }
            entry.LastAccessUtc = _timeProvider.GetUtcNow();
            session = entry.Session;
            return true;
        }
    }

    internal async ValueTask<bool> RemoveAndDisposeAsync(string id)
    {
        TSession? session;
        lock (_gate)
        {
            session = _sessions.Remove(id, out var entry) ? entry.Session : null;
        }
        if (session is null)
        {
            return false;
        }
        await session.DisposeAsync();
        return true;
    }

    internal async Task ExpireInactiveAsync()
    {
        List<TSession> expired = [];
        lock (_gate)
        {
            foreach (var (id, entry) in _sessions.ToArray())
            {
                if (_timeProvider.GetUtcNow() - entry.LastAccessUtc >= _inactivityTimeout &&
                    _sessions.Remove(id, out var removed))
                {
                    expired.Add(removed.Session);
                }
            }
        }
        foreach (var session in expired)
        {
            await session.DisposeAsync();
        }
    }

    internal async Task RunExpirationLoopAsync(CancellationToken cancellationToken)
    {
        var interval = _inactivityTimeout / 2;
        if (interval > MaximumSweepInterval)
        {
            interval = MaximumSweepInterval;
        }
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, _timeProvider, cancellationToken);
                await ExpireInactiveAsync();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    internal async Task DisposeAllAsync()
    {
        TSession[] sessions;
        lock (_gate)
        {
            sessions = _sessions.Values.Select(entry => entry.Session).ToArray();
            _sessions.Clear();
        }
        foreach (var session in sessions)
        {
            await session.DisposeAsync();
        }
    }

    private sealed class Entry(TSession session, DateTimeOffset lastAccessUtc)
    {
        internal TSession Session { get; } = session;

        internal DateTimeOffset LastAccessUtc { get; set; } = lastAccessUtc;
    }
}
