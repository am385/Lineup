using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;

namespace Lineup.WebPlayer;

/// <summary>Registers one reusable CMAF output and its transport-neutral state.</summary>
public sealed record CmafOutputRegistration(IAsyncDisposable Owner, object State, Action<StreamSessionId>? ViewerReleased = null);

/// <summary>Immutable viewer lookup returned to HTTP transport adapters.</summary>
public sealed record CmafViewerSnapshot(StreamSessionId ViewerSessionId, StreamSessionId OutputId, StreamTransformKey TransformKey, object State);

/// <summary>Immutable diagnostic state for one reusable CMAF producer.</summary>
public sealed record CmafOutputDiagnostics(StreamSessionId OutputId, StreamTransformKey TransformKey, int ViewerCount, bool IsWarm);

/// <summary>Owns one viewer alias reference to a reusable CMAF output.</summary>
public interface ICmafOutputLease : IAsyncDisposable
{
    /// <summary>Gets the unique viewer alias identity.</summary>
    StreamSessionId ViewerSessionId { get; }
    /// <summary>Gets the stable output identity shared by equivalent viewers.</summary>
    StreamSessionId OutputId { get; }
    /// <summary>Gets the transform identity used by the output.</summary>
    StreamTransformKey TransformKey { get; }
    /// <summary>Gets the transport-neutral output state.</summary>
    object State { get; }
}

/// <summary>Coordinates HTTP viewer aliases and reference-counted CMAF output lifetimes.</summary>
public interface ICmafOutputCoordinator : IAsyncDisposable, IStreamingProducerDiagnosticsContributor
{
    /// <summary>Acquires or creates the output for a canonical transform.</summary>
    ValueTask<ICmafOutputLease> AcquireAsync(
        StreamTransformKey transformKey,
        Func<CancellationToken, ValueTask<IAsyncDisposable>> startOutput,
        CancellationToken cancellationToken = default);

    /// <summary>Acquires or creates an output with state exposed to transport adapters.</summary>
    ValueTask<ICmafOutputLease> AcquireAsync(
        StreamTransformKey transformKey,
        Func<CancellationToken, ValueTask<CmafOutputRegistration>> startOutput,
        CancellationToken cancellationToken = default);

    /// <summary>Acquires an output for a caller-selected externally visible viewer identity.</summary>
    ValueTask<ICmafOutputLease> AcquireAsync(
        StreamTransformKey transformKey,
        StreamSessionId viewerSessionId,
        Func<CancellationToken, ValueTask<CmafOutputRegistration>> startOutput,
        CancellationToken cancellationToken = default);

    /// <summary>Acquires an output with a configured idle producer reuse period.</summary>
    ValueTask<ICmafOutputLease> AcquireAsync(
        StreamTransformKey transformKey,
        StreamSessionId viewerSessionId,
        Func<CancellationToken, ValueTask<CmafOutputRegistration>> startOutput,
        TimeSpan keepWarmPeriod,
        CancellationToken cancellationToken = default);

    /// <summary>Finds and touches one active viewer alias.</summary>
    bool TryGetViewer(StreamSessionId viewerSessionId, out CmafViewerSnapshot? viewer);

    /// <summary>Releases one viewer alias; the final release disposes its output owner.</summary>
    ValueTask<bool> ReleaseViewerAsync(StreamSessionId viewerSessionId);

    /// <summary>Releases every viewer of a completed output.</summary>
    ValueTask ReleaseOutputAsync(StreamSessionId outputId);

    /// <summary>Gets all active viewer aliases.</summary>
    IReadOnlyList<CmafViewerSnapshot> GetViewers();

    /// <summary>Gets all active reusable output producers.</summary>
    IReadOnlyList<CmafOutputDiagnostics> GetDiagnostics();

    /// <summary>Gets the number of active reusable outputs.</summary>
    int ActiveOutputCount { get; }

    /// <summary>Stops all outputs and rejects subsequent acquisitions.</summary>
    ValueTask StopAsync();
}

/// <summary>Thread-safe, host-neutral CMAF output and viewer coordinator.</summary>
public sealed class CmafOutputCoordinator : ICmafOutputCoordinator
{
    private static readonly TimeSpan DefaultInactivityTimeout = TimeSpan.FromMinutes(2);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<StreamTransformKey, OutputEntry> _outputs = [];
    private readonly Dictionary<StreamSessionId, ViewerEntry> _viewers = [];
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _inactivityTimeout;
    private bool _stopping;

    /// <summary>Initializes a coordinator.</summary>
    public CmafOutputCoordinator(TimeProvider? timeProvider = null, TimeSpan? inactivityTimeout = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _inactivityTimeout = NormalizeTimeout(inactivityTimeout);
    }

    /// <inheritdoc />
    public int ActiveOutputCount
    {
        get
        {
            _gate.Wait();
            try
            {
                return _outputs.Count;
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<ICmafOutputLease> AcquireAsync(
        StreamTransformKey transformKey,
        Func<CancellationToken, ValueTask<IAsyncDisposable>> startOutput,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startOutput);
        return AcquireAsync(
            transformKey,
            async token => new CmafOutputRegistration(await startOutput(token), new object()),
            cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask<ICmafOutputLease> AcquireAsync(
        StreamTransformKey transformKey,
        Func<CancellationToken, ValueTask<CmafOutputRegistration>> startOutput,
        CancellationToken cancellationToken = default)
        => await AcquireAsync(transformKey, StreamSessionId.New(), startOutput, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<ICmafOutputLease> AcquireAsync(
        StreamTransformKey transformKey,
        StreamSessionId viewerSessionId,
        Func<CancellationToken, ValueTask<CmafOutputRegistration>> startOutput,
        CancellationToken cancellationToken = default)
        => await AcquireAsync(transformKey, viewerSessionId, startOutput, TimeSpan.Zero, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<ICmafOutputLease> AcquireAsync(
        StreamTransformKey transformKey,
        StreamSessionId viewerSessionId,
        Func<CancellationToken, ValueTask<CmafOutputRegistration>> startOutput,
        TimeSpan keepWarmPeriod,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transformKey);
        ArgumentNullException.ThrowIfNull(startOutput);
        if (string.IsNullOrWhiteSpace(transformKey.Stage) || string.IsNullOrWhiteSpace(transformKey.SettingsKey))
        {
            throw new ArgumentException("Transform stage and settings keys are required.", nameof(transformKey));
        }
        if (keepWarmPeriod < TimeSpan.Zero || keepWarmPeriod > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(keepWarmPeriod), keepWarmPeriod, "Keep-warm period must be between zero and five minutes.");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            if (!_outputs.TryGetValue(transformKey, out var output))
            {
                var registration = await startOutput(cancellationToken);
                output = new OutputEntry(StreamSessionId.New(), registration.Owner, registration.State, registration.ViewerReleased);
                _outputs.Add(transformKey, output);
            }
            CancelWarmExpiration(output);

            if (_viewers.ContainsKey(viewerSessionId))
            {
                throw new InvalidOperationException($"CMAF viewer {viewerSessionId} already exists.");
            }
            var viewer = new ViewerEntry(viewerSessionId, transformKey, output, _timeProvider.GetTimestamp(), keepWarmPeriod);
            output.ReferenceCount++;
            _viewers.Add(viewerSessionId, viewer);
            viewer.ExpirationTask = ExpireAsync(viewer, viewer.ExpirationCancellation.Token);
            return new OutputLease(this, viewer);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public bool TryGetViewer(StreamSessionId viewerSessionId, out CmafViewerSnapshot? viewer)
    {
        _gate.Wait();
        try
        {
            if (!_viewers.TryGetValue(viewerSessionId, out var entry))
            {
                viewer = null;
                return false;
            }

            entry.LastAccessTimestamp = _timeProvider.GetTimestamp();
            viewer = entry.CreateSnapshot();
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<bool> ReleaseViewerAsync(StreamSessionId viewerSessionId)
    {
        IAsyncDisposable? outputOwner = null;
        await _gate.WaitAsync();
        try
        {
            if (!_viewers.Remove(viewerSessionId, out var viewer))
            {
                return false;
            }
            viewer.ExpirationCancellation.Cancel();
            viewer.Output.ViewerReleased?.Invoke(viewerSessionId);
            if (--viewer.Output.ReferenceCount == 0)
            {
                outputOwner = BeginOutputIdle(viewer.TransformKey, viewer.Output, viewer.KeepWarmPeriod);
            }
        }
        finally
        {
            _gate.Release();
        }

        if (outputOwner is not null)
        {
            await outputOwner.DisposeAsync();
        }
        return true;
    }

    /// <inheritdoc />
    public async ValueTask ReleaseOutputAsync(StreamSessionId outputId)
    {
        IAsyncDisposable? owner = null;
        await _gate.WaitAsync();
        try
        {
            var viewers = _viewers.Values
                .Where(viewer => viewer.Output.OutputId == outputId)
                .ToArray();
            foreach (var viewer in viewers)
            {
                _viewers.Remove(viewer.ViewerSessionId);
                viewer.ExpirationCancellation.Cancel();
                viewer.Output.ViewerReleased?.Invoke(viewer.ViewerSessionId);
            }
            var output = _outputs.SingleOrDefault(pair => pair.Value.OutputId == outputId);
            if (output.Value is not null)
            {
                _outputs.Remove(output.Key);
                CancelWarmExpiration(output.Value);
                owner = output.Value.Owner;
            }
        }
        finally
        {
            _gate.Release();
        }
        if (owner is not null)
        {
            await owner.DisposeAsync();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<CmafViewerSnapshot> GetViewers()
    {
        _gate.Wait();
        try
        {
            return _viewers.Values.Select(viewer => viewer.CreateSnapshot()).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<CmafOutputDiagnostics> GetDiagnostics()
    {
        _gate.Wait();
        try
        {
            return _outputs
                .Select(pair => new CmafOutputDiagnostics(
                    pair.Value.OutputId,
                    pair.Key,
                    pair.Value.ReferenceCount,
                    pair.Value.ReferenceCount == 0 && pair.Value.WarmExpirationCancellation is not null))
                .OrderBy(output => output.OutputId.Value)
                .ToArray();
        }

        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public StreamingProducerDiagnostics GetProducerDiagnostics()
    {
        var diagnostics = GetDiagnostics();
        return new StreamingProducerDiagnostics(
            diagnostics.Count(output => !output.IsWarm),
            diagnostics.Count(output => output.IsWarm),
            diagnostics.Count);
    }

    /// <inheritdoc />
    public async ValueTask StopAsync()
    {
        IAsyncDisposable[] owners;
        await _gate.WaitAsync();
        try
        {
            if (_stopping)
            {
                return;
            }
            _stopping = true;
            foreach (var viewer in _viewers.Values)
            {
                viewer.ExpirationCancellation.Cancel();
            }
            foreach (var output in _outputs.Values)
            {
                CancelWarmExpiration(output);
            }
            owners = _outputs.Values.Select(output => output.Owner).ToArray();
            _viewers.Clear();
            _outputs.Clear();
        }
        finally
        {
            _gate.Release();
        }
        foreach (var owner in owners)
        {
            await owner.DisposeAsync();
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => StopAsync();

    private async Task ExpireAsync(ViewerEntry viewer, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                TimeSpan remaining;
                IAsyncDisposable? outputOwner = null;
                await _gate.WaitAsync(cancellationToken);
                try
                {
                    if (!_viewers.TryGetValue(viewer.ViewerSessionId, out var current) || !ReferenceEquals(current, viewer))
                    {
                        return;
                    }
                    remaining = _inactivityTimeout - _timeProvider.GetElapsedTime(viewer.LastAccessTimestamp);
                    if (remaining <= TimeSpan.Zero)
                    {
                        _viewers.Remove(viewer.ViewerSessionId);
                        viewer.Output.ViewerReleased?.Invoke(viewer.ViewerSessionId);
                        if (--viewer.Output.ReferenceCount == 0)
                        {
                            outputOwner = BeginOutputIdle(viewer.TransformKey, viewer.Output, viewer.KeepWarmPeriod);
                        }
                    }
                }
                finally
                {
                    _gate.Release();
                }
                if (outputOwner is not null)
                {
                    await outputOwner.DisposeAsync();
                    return;
                }
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining, _timeProvider, cancellationToken);
                    continue;
                }
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static TimeSpan NormalizeTimeout(TimeSpan? timeout)
    {
        var value = timeout ?? DefaultInactivityTimeout;
        return value < TimeSpan.FromMilliseconds(10)
            ? TimeSpan.FromMilliseconds(10)
            : value > TimeSpan.FromHours(1)
                ? TimeSpan.FromHours(1)
                : value;
    }

    private IAsyncDisposable? BeginOutputIdle(StreamTransformKey transformKey, OutputEntry output, TimeSpan keepWarmPeriod)
    {
        if (keepWarmPeriod <= TimeSpan.Zero)
        {
            _outputs.Remove(transformKey);
            return output.Owner;
        }

        CancelWarmExpiration(output);
        var expiration = new CancellationTokenSource();
        output.WarmExpirationCancellation = expiration;
        _ = ExpireOutputAsync(transformKey, output, keepWarmPeriod, expiration);
        return null;
    }

    private async Task ExpireOutputAsync(StreamTransformKey transformKey, OutputEntry output, TimeSpan keepWarmPeriod, CancellationTokenSource expiration)
    {
        IAsyncDisposable? owner = null;
        try
        {
            await Task.Delay(keepWarmPeriod, _timeProvider, expiration.Token);
            await _gate.WaitAsync(expiration.Token);
            try
            {
                if (ReferenceEquals(output.WarmExpirationCancellation, expiration) &&
                    output.ReferenceCount == 0 &&
                    _outputs.TryGetValue(transformKey, out var current) &&
                    ReferenceEquals(current, output))
                {
                    output.WarmExpirationCancellation = null;
                    _outputs.Remove(transformKey);
                    owner = output.Owner;
                }
            }
            finally
            {
                _gate.Release();
            }
            if (owner is not null)
            {
                await owner.DisposeAsync();
            }
        }
        catch (OperationCanceledException) when (expiration.IsCancellationRequested)
        {
        }
        finally
        {
            expiration.Dispose();
        }
    }

    private static void CancelWarmExpiration(OutputEntry output)
    {
        var expiration = output.WarmExpirationCancellation;
        output.WarmExpirationCancellation = null;
        expiration?.Cancel();
    }

    private sealed class OutputEntry(StreamSessionId outputId, IAsyncDisposable owner, object state, Action<StreamSessionId>? viewerReleased)
    {
        public StreamSessionId OutputId { get; } = outputId;
        public IAsyncDisposable Owner { get; } = owner;
        public object State { get; } = state;
        public Action<StreamSessionId>? ViewerReleased { get; } = viewerReleased;
        public int ReferenceCount { get; set; }
        public CancellationTokenSource? WarmExpirationCancellation { get; set; }
    }

    private sealed class ViewerEntry(StreamSessionId viewerSessionId, StreamTransformKey transformKey, OutputEntry output, long lastAccessTimestamp, TimeSpan keepWarmPeriod)
    {
        public StreamSessionId ViewerSessionId { get; } = viewerSessionId;
        public StreamTransformKey TransformKey { get; } = transformKey;
        public OutputEntry Output { get; } = output;
        public long LastAccessTimestamp { get; set; } = lastAccessTimestamp;
        public TimeSpan KeepWarmPeriod { get; } = keepWarmPeriod;
        public CancellationTokenSource ExpirationCancellation { get; } = new();
        public Task? ExpirationTask { get; set; }

        public CmafViewerSnapshot CreateSnapshot() => new(ViewerSessionId, Output.OutputId, TransformKey, Output.State);
    }

    private sealed class OutputLease(CmafOutputCoordinator coordinator, ViewerEntry viewer) : ICmafOutputLease
    {
        private CmafOutputCoordinator? _coordinator = coordinator;

        public StreamSessionId ViewerSessionId => viewer.ViewerSessionId;
        public StreamSessionId OutputId => viewer.Output.OutputId;
        public StreamTransformKey TransformKey => viewer.TransformKey;
        public object State => viewer.Output.State;

        public ValueTask DisposeAsync()
        {
            var owner = Interlocked.Exchange(ref _coordinator, null);
            return owner is null ? ValueTask.CompletedTask : ReleaseAsync(owner, ViewerSessionId);
        }

        private static async ValueTask ReleaseAsync(CmafOutputCoordinator owner, StreamSessionId viewerSessionId) =>
            _ = await owner.ReleaseViewerAsync(viewerSessionId);
    }
}
