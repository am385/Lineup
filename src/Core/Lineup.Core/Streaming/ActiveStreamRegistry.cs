using System.Collections.Concurrent;
using Lineup.Core.Extensibility.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Lineup.Core.Streaming;

/// <summary>Provides process-local active stream registration and snapshots.</summary>
public interface IActiveStreamRegistry
{
    /// <summary>Reserves active-stream capacity and a startup slot before acquiring or probing media; returns null on overload or shutdown.</summary>
    IStreamStartupLease? TryBeginStartup(int maximumConcurrentStreams, Action stop);

    /// <summary>Releases a registered worker's startup slot after its ready or terminal status report.</summary>
    void CompleteStartup(string sessionId);

    /// <summary>Occurs when an active stream receives an explicit stop request.</summary>
    event Action<ActiveStreamSnapshot>? StopRequested;

    /// <summary>Registers or replaces an active stream.</summary>
    void Register(ActiveStreamSnapshot stream, Action? stop = null);

    /// <summary>Registers a new active stream when the configured concurrency limit permits it.</summary>
    bool TryRegister(ActiveStreamSnapshot stream, int maximumConcurrentStreams, Action stop);

    /// <summary>Requests that an active stream stop.</summary>
    bool RequestStop(string sessionId);

    /// <summary>Requests that every active stream owned by a client stop.</summary>
    int RequestStopByClientId(string clientId);

    /// <summary>Stops accepting new streams, requests every active stream to stop, and waits for cleanup.</summary>
    Task StopAllAsync(CancellationToken cancellationToken);

    /// <summary>Removes an active stream.</summary>
    bool Unregister(string sessionId);

    /// <summary>Atomically updates an existing active stream while preserving its stop callback.</summary>
    bool TryUpdate(string sessionId, Func<ActiveStreamSnapshot, ActiveStreamSnapshot> update);

    /// <summary>Returns immutable active stream snapshots ordered by start time.</summary>
    IReadOnlyList<ActiveStreamSnapshot> GetActiveStreams();
}

/// <summary>Owns a pending startup reservation until failure or completion; registered stream ownership remains with the active registry.</summary>
public interface IStreamStartupLease : IDisposable
{
    /// <summary>Transfers reserved active-stream capacity to one active session without releasing the startup slot yet.</summary>
    bool TryRegister(ActiveStreamSnapshot stream, Action stop);
}

/// <summary>Thread-safe in-memory active stream registry.</summary>
public sealed class ActiveStreamRegistry : IActiveStreamRegistry
{
    private readonly ConcurrentDictionary<string, ActiveStreamRegistration> _streams = new(StringComparer.Ordinal);
    private readonly Lock _registrationLock = new();
    private readonly Dictionary<Guid, StartupLease> _startups = [];
    private readonly int _maximumPendingStarts;
    private readonly ILogger<ActiveStreamRegistry> _logger;
    private TaskCompletionSource _allStreamsStopped = CompletedTaskCompletionSource();
    private bool _isStopping;

    /// <summary>Initializes an active stream registry.</summary>
    public ActiveStreamRegistry(ILogger<ActiveStreamRegistry>? logger = null, StreamingResourceOptions? resources = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ActiveStreamRegistry>.Instance;
        resources ??= new();
        resources.Validate();
        _maximumPendingStarts = resources.MaximumPendingStarts;
    }

    /// <inheritdoc />
    public IStreamStartupLease? TryBeginStartup(int maximumConcurrentStreams, Action stop)
    {
        ArgumentNullException.ThrowIfNull(stop);
        lock (_registrationLock)
        {
            if (_isStopping || _startups.Count >= _maximumPendingStarts ||
                maximumConcurrentStreams > 0 && _streams.Count + _startups.Values.Count(startup => !startup.Registered) >= maximumConcurrentStreams)
            {
                _logger.LogWarning("Stream startup rejected: {ActiveStreams} active streams and {PendingStarts} pending startups.", _streams.Count, _startups.Count);
                LineupTelemetry.Rejections.Add(1);
                return null;
            }
            if (_streams.IsEmpty && _startups.Count == 0)
            {
                _allStreamsStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            var lease = new StartupLease(this, stop);
            _startups.Add(lease.Id, lease);
            LineupTelemetry.PendingStarts.Add(1);
            return lease;
        }
    }

    /// <inheritdoc />
    public void CompleteStartup(string sessionId)
    {
        lock (_registrationLock)
        {
            foreach (var startup in _startups.Values.Where(startup => startup.SessionId == sessionId).ToArray())
            {
                startup.Dispose();
            }
        }
    }

    /// <inheritdoc />
    public event Action<ActiveStreamSnapshot>? StopRequested;

    /// <inheritdoc />
    public void Register(ActiveStreamSnapshot stream, Action? stop = null)
    {
        lock (_registrationLock)
        {
            if (_isStopping && !_streams.ContainsKey(stream.SessionId))
            {
                return;
            }
            RegisterCore(stream, stop);
        }
    }

    /// <inheritdoc />
    public bool TryRegister(ActiveStreamSnapshot stream, int maximumConcurrentStreams, Action stop)
    {
        lock (_registrationLock)
        {
            if (_isStopping ||
                !_streams.ContainsKey(stream.SessionId) &&
                maximumConcurrentStreams > 0 &&
                _streams.Count + _startups.Values.Count(startup => !startup.Registered) >= maximumConcurrentStreams)
            {
                return false;
            }
            RegisterCore(stream, stop);
            return true;
        }
    }

    /// <inheritdoc />
    public bool RequestStop(string sessionId)
    {
        ActiveStreamRegistration registration;
        Action stop;
        lock (_registrationLock)
        {
            if (!_streams.TryGetValue(sessionId, out var current) || current.Stop is not { } stopAction)
            {
                return false;
            }
            registration = current;
            stop = stopAction;
            _streams[sessionId] = current with { Stop = null };
        }

        StopRequested?.Invoke(registration.Snapshot);
        stop();
        return true;
    }

    /// <inheritdoc />
    public int RequestStopByClientId(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return 0;
        }

        string[] sessionIds;
        lock (_registrationLock)
        {
            sessionIds = _streams.Values
                .Where(registration => string.Equals(registration.Snapshot.ClientId, clientId, StringComparison.Ordinal))
                .Select(registration => registration.Snapshot.SessionId)
                .ToArray();
        }
        return sessionIds.Count(RequestStop);
    }

    /// <inheritdoc />
    public async Task StopAllAsync(CancellationToken cancellationToken)
    {
        ActiveStreamRegistration[] registrations;
        Action[] pendingStops;
        Task allStreamsStopped;
        lock (_registrationLock)
        {
            _isStopping = true;
            pendingStops = _startups.Values.Where(startup => !startup.Registered).Select(startup => startup.Stop).ToArray();
            registrations = _streams.Values.ToArray();
            foreach (var registration in registrations)
            {
                if (registration.Stop is not null)
                {
                    _streams[registration.Snapshot.SessionId] = registration with { Stop = null };
                }
            }
            allStreamsStopped = _allStreamsStopped.Task;
        }

        var stopTasks = registrations
            .Where(registration => registration.Stop is not null)
            .Select(registration => Task.Run(() => RequestShutdown(registration)))
            .Concat(pendingStops.Select(stop => Task.Run(() => RequestStartupShutdown(stop))))
            .ToArray();
        await Task.WhenAll(stopTasks).WaitAsync(cancellationToken);
        await allStreamsStopped.WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public bool Unregister(string sessionId)
    {
        lock (_registrationLock)
        {
            var removed = _streams.TryRemove(sessionId, out _);
            if (removed && _streams.IsEmpty && _startups.Count == 0)
            {
                _allStreamsStopped.TrySetResult();
            }
            return removed;
        }
    }

    /// <inheritdoc />
    public bool TryUpdate(string sessionId, Func<ActiveStreamSnapshot, ActiveStreamSnapshot> update)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(update);
        lock (_registrationLock)
        {
            if (!_streams.TryGetValue(sessionId, out var current))
            {
                return false;
            }
            var updated = update(CloneSnapshot(current.Snapshot));
            if (!string.Equals(updated.SessionId, sessionId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("An active stream update cannot change its session identifier.");
            }
            _streams[sessionId] = current with { Snapshot = CloneSnapshot(updated) };
            return true;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ActiveStreamSnapshot> GetActiveStreams() =>
        _streams.Values
            .Select(registration => registration.Snapshot)
            .OrderBy(stream => stream.StartedAtUtc)
            .Select(CloneSnapshot)
            .ToArray();

    private sealed record ActiveStreamRegistration(ActiveStreamSnapshot Snapshot, Action? Stop);

    private sealed class StartupLease(ActiveStreamRegistry owner, Action stop) : IStreamStartupLease
    {
        private readonly long _started = System.Diagnostics.Stopwatch.GetTimestamp();
        public Guid Id { get; } = Guid.NewGuid();
        public Action Stop { get; } = stop;
        public bool Registered { get; private set; }
        public string? SessionId { get; private set; }

        public bool TryRegister(ActiveStreamSnapshot stream, Action stop)
        {
            lock (owner._registrationLock)
            {
                if (owner._isStopping || !owner._startups.ContainsKey(Id) || Registered || owner._streams.ContainsKey(stream.SessionId))
                {
                    return false;
                }
                owner.RegisterCore(stream, stop);
                Registered = true;
                SessionId = stream.SessionId;
                return true;
            }
        }

        public void Dispose()
        {
            lock (owner._registrationLock)
            {
                if (owner._startups.Remove(Id))
                {
                    LineupTelemetry.PendingStarts.Add(-1);
                    LineupTelemetry.Record("admission", Registered ? "registered" : "released", System.Diagnostics.Stopwatch.GetElapsedTime(_started));
                }
                if (owner._streams.IsEmpty && owner._startups.Count == 0)
                {
                    owner._allStreamsStopped.TrySetResult();
                }
            }
        }
    }

    private void RegisterCore(ActiveStreamSnapshot stream, Action? stop)
    {
        if (_streams.IsEmpty && _startups.Count == 0)
        {
            _allStreamsStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        var immutable = CloneSnapshot(stream) with { CorrelationId = stream.CorrelationId ?? System.Diagnostics.Activity.Current?.TraceId.ToString() };
        _streams.AddOrUpdate(
            stream.SessionId,
            _ => new ActiveStreamRegistration(immutable, stop),
            (_, current) => new ActiveStreamRegistration(
                immutable with
                {
                    ClientAddress = immutable.ClientAddress ?? current.Snapshot.ClientAddress,
                    ClientId = immutable.ClientId ?? current.Snapshot.ClientId
                },
                stop ?? current.Stop));
    }

    private static ActiveStreamSnapshot CloneSnapshot(ActiveStreamSnapshot stream) =>
        stream with
        {
            Tracks = stream.Tracks.ToArray(),
            PhysicalInput = CloneMedia(stream.PhysicalInput),
            PhysicalOutput = CloneMedia(stream.PhysicalOutput),
            VirtualOutput = CloneMedia(stream.VirtualOutput)
        };

    private static ActiveStreamMediaSnapshot? CloneMedia(ActiveStreamMediaSnapshot? media) =>
        media is null ? null : media with { Tracks = media.Tracks.ToArray() };

    private static TaskCompletionSource CompletedTaskCompletionSource()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        completion.SetResult();
        return completion;
    }

    private void RequestShutdown(ActiveStreamRegistration registration)
    {
        try
        {
            StopRequested?.Invoke(registration.Snapshot);
            registration.Stop!();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to request shutdown of active stream {SessionId}", registration.Snapshot.SessionId);
        }
    }

    private void RequestStartupShutdown(Action stop)
    {
        try
        {
            stop();
        }
        catch (ObjectDisposedException)
        {
            _logger.LogDebug("The pending stream startup completed before its captured shutdown callback ran.");
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Unable to request shutdown of a pending stream startup");
        }
    }
}
