using Lineup.Core.Common.Streaming;
using Lineup.Core.Extensibility.Streaming;

namespace Lineup.Plugin.WebPlayer;

/// <summary>Registers one reusable CMAF output and its transport-neutral state.</summary>
public sealed record CmafOutputRegistration(IAsyncDisposable Owner, object State, Action<StreamSessionId>? ViewerReleased = null);

/// <summary>Immutable viewer lookup returned to HTTP transport adapters.</summary>
public sealed record CmafViewerSnapshot(StreamSessionId ViewerSessionId, StreamSessionId OutputId, StreamTransformKey TransformKey, object State);

/// <summary>Immutable diagnostic state for one reusable CMAF producer.</summary>
public sealed record CmafOutputDiagnostics(StreamSessionId OutputId, StreamTransformKey TransformKey, int ViewerCount, bool IsWarm);

/// <summary>Reports passive viewer liveness or a bounded, explicitly classified terminal outcome.</summary>
public sealed record CmafPlaybackStatus(bool IsActive, string? Code, bool Retryable);

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
    ValueTask<ICmafOutputLease> AcquireAsync(StreamTransformKey transformKey, Func<CancellationToken, ValueTask<IAsyncDisposable>> startOutput, CancellationToken cancellationToken = default);

    /// <summary>Acquires or creates an output with state exposed to transport adapters.</summary>
    ValueTask<ICmafOutputLease> AcquireAsync(StreamTransformKey transformKey, Func<CancellationToken, ValueTask<CmafOutputRegistration>> startOutput, CancellationToken cancellationToken = default);

    /// <summary>Acquires an output for a caller-selected externally visible viewer identity.</summary>
    ValueTask<ICmafOutputLease> AcquireAsync(
        StreamTransformKey transformKey,
        StreamSessionId viewerSessionId,
        Func<CancellationToken, ValueTask<CmafOutputRegistration>> startOutput,
        CancellationToken cancellationToken = default
    );

    /// <summary>Acquires an output with a configured idle producer reuse period.</summary>
    ValueTask<ICmafOutputLease> AcquireAsync(
        StreamTransformKey transformKey,
        StreamSessionId viewerSessionId,
        Func<CancellationToken, ValueTask<CmafOutputRegistration>> startOutput,
        TimeSpan keepWarmPeriod,
        CancellationToken cancellationToken = default
    );

    /// <summary>Finds and touches one active viewer alias.</summary>
    bool TryGetViewer(StreamSessionId viewerSessionId, out CmafViewerSnapshot? viewer);

    /// <summary>Releases one viewer alias; the final release disposes its output owner.</summary>
    ValueTask<bool> ReleaseViewerAsync(StreamSessionId viewerSessionId, string? terminationCode = null);

    /// <summary>Releases every viewer of a completed output.</summary>
    ValueTask ReleaseOutputAsync(StreamSessionId outputId, string? terminationCode = null);

    /// <summary>Reads viewer status without renewing its inactivity lease.</summary>
    CmafPlaybackStatus GetPlaybackStatus(StreamSessionId viewerSessionId);

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
    private static readonly TimeSpan _defaultInactivityTimeout = TimeSpan.FromMinutes(2);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _creationGate = new(1, 1);
    private readonly IMediaWorkAdmission? _mediaWork;
    private readonly Dictionary<StreamTransformKey, OutputEntry> _outputs = [];
    private readonly Dictionary<StreamSessionId, ViewerEntry> _viewers = [];
    private readonly Dictionary<StreamSessionId, (string Code, long Timestamp)> _terminations = [];
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _inactivityTimeout;
    private bool _stopping;

    /// <summary>Initializes a coordinator.</summary>
    public CmafOutputCoordinator(TimeProvider? timeProvider = null, TimeSpan? inactivityTimeout = null, IMediaWorkAdmission? mediaWork = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _inactivityTimeout = NormalizeTimeout(inactivityTimeout);
        _mediaWork = mediaWork;
    }

    /// <summary>Gets the active output count.</summary>
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

    /// <summary>Executes acquire asynchronously.</summary>
    /// <param name="transformKey">The transform key value.</param>
    /// <param name="startOutput">The start output value.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The operation's result.</returns>
    public ValueTask<ICmafOutputLease> AcquireAsync(StreamTransformKey transformKey, Func<CancellationToken, ValueTask<IAsyncDisposable>> startOutput, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startOutput);
        return AcquireAsync(
            transformKey,
            async token => new CmafOutputRegistration(await startOutput(token), new object()),
            cancellationToken);
    }

    /// <summary>Executes acquire asynchronously.</summary>
    /// <param name="transformKey">The transform key value.</param>
    /// <param name="startOutput">The start output value.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The operation's result.</returns>
    public async ValueTask<ICmafOutputLease> AcquireAsync(StreamTransformKey transformKey, Func<CancellationToken, ValueTask<CmafOutputRegistration>> startOutput, CancellationToken cancellationToken = default)
        => await AcquireAsync(transformKey, StreamSessionId.New(), startOutput, cancellationToken);

    /// <summary>Executes acquire asynchronously.</summary>
    /// <param name="transformKey">The transform key value.</param>
    /// <param name="viewerSessionId">The viewer session ID value.</param>
    /// <param name="startOutput">The start output value.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The operation's result.</returns>
    public async ValueTask<ICmafOutputLease> AcquireAsync(
        StreamTransformKey transformKey,
        StreamSessionId viewerSessionId,
        Func<CancellationToken, ValueTask<CmafOutputRegistration>> startOutput,
        CancellationToken cancellationToken = default
    )
        => await AcquireAsync(transformKey, viewerSessionId, startOutput, TimeSpan.Zero, cancellationToken);

    /// <summary>Executes acquire asynchronously.</summary>
    /// <param name="transformKey">The transform key value.</param>
    /// <param name="viewerSessionId">The viewer session ID value.</param>
    /// <param name="startOutput">The start output value.</param>
    /// <param name="keepWarmPeriod">The keep warm period value.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The operation's result.</returns>
    public async ValueTask<ICmafOutputLease> AcquireAsync(
        StreamTransformKey transformKey,
        StreamSessionId viewerSessionId,
        Func<CancellationToken, ValueTask<CmafOutputRegistration>> startOutput,
        TimeSpan keepWarmPeriod,
        CancellationToken cancellationToken = default
    )
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

        await _creationGate.WaitAsync(cancellationToken);
        IAsyncDisposable? reservation = null;
        OutputEntry? reservedOutput = null;
        try
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                ObjectDisposedException.ThrowIf(_stopping, this);
                if (_viewers.ContainsKey(viewerSessionId))
                {
                    throw new InvalidOperationException($"CMAF viewer {viewerSessionId} already exists.");
                }
                if (_outputs.TryGetValue(transformKey, out var existing))
                {
                    return AddViewer(transformKey, viewerSessionId, existing, keepWarmPeriod);
                }
            }
            finally
            {
                _gate.Release();
            }
            if (_mediaWork is not null)
            {
                reservation = await _mediaWork.AcquireAsync(MediaWorkKind.Producer, token => RetireIdleAsync(transformKey, reservedOutput, token), cancellationToken);
            }
            await _gate.WaitAsync(cancellationToken);
            try
            {
                ObjectDisposedException.ThrowIf(_stopping, this);
                var registration = await startOutput(cancellationToken);
                var output = new OutputEntry(StreamSessionId.New(), new ReservedOwner(registration.Owner, reservation), registration.State, registration.ViewerReleased);
                reservedOutput = output;
                reservation = null;
                _outputs.Add(transformKey, output);
                Lineup.Core.Extensibility.Diagnostics.LineupTelemetry.CmafProducers.Add(1);
                return AddViewer(transformKey, viewerSessionId, output, keepWarmPeriod);
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            try
            {
                if (reservation is not null)
                {
                    await reservation.DisposeAsync();
                }
            }
            finally
            {
                _creationGate.Release();
            }
        }
    }

    private OutputLease AddViewer(StreamTransformKey key, StreamSessionId viewerId, OutputEntry output, TimeSpan keepWarmPeriod)
    {
        CancelWarmExpiration(output);
        var viewer = new ViewerEntry(viewerId, key, output, _timeProvider.GetTimestamp(), keepWarmPeriod);
        output.ReferenceCount++;
        _terminations.Remove(viewerId);
        _viewers.Add(viewerId, viewer);
        viewer.ExpirationTask = ExpireAsync(viewer, viewer.ExpirationCancellation.Token);
        return new OutputLease(this, viewer);
    }

    private async Task<bool> RetireIdleAsync(StreamTransformKey key, OutputEntry? reservedOutput, CancellationToken cancellationToken)
    {
        if (reservedOutput is null)
        {
            return false;
        }
        IAsyncDisposable? owner = null;
        if (!await _gate.WaitAsync(0, cancellationToken))
        {
            return false;
        }
        try
        {
            if (!_outputs.TryGetValue(key, out var output) || !ReferenceEquals(output, reservedOutput))
            {
                owner = reservedOutput.Owner;
            }
            else if (output.ReferenceCount == 0 && output.WarmExpirationCancellation is not null)
            {
                _outputs.Remove(key);
                CancelWarmExpiration(output);
                Lineup.Core.Extensibility.Diagnostics.LineupTelemetry.CmafProducers.Add(-1);
                owner = output.Owner;
            }
        }
        finally
        {
            _gate.Release();
        }
        if (owner is null)
        {
            return false;
        }
        await owner.DisposeAsync().AsTask().WaitAsync(cancellationToken);
        return true;
    }

    private sealed class ReservedOwner(IAsyncDisposable owner, IAsyncDisposable? reservation) : IAsyncDisposable
    {
        private readonly Lock _gate = new();
        private Task? _disposal;

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            lock (_gate)
            {
                return new(_disposal ??= DisposeCoreAsync());
            }
        }

        private async Task DisposeCoreAsync()
        {
            await owner.DisposeAsync();
            if (reservation is not null)
            {
                await reservation.DisposeAsync();
            }
        }
    }

    /// <summary>Attempts to get viewer.</summary>
    /// <param name="viewerSessionId">The viewer session ID value.</param>
    /// <param name="viewer">The viewer value.</param>
    /// <returns>The operation's result.</returns>
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

    /// <summary>Releases viewer asynchronously.</summary>
    /// <param name="viewerSessionId">The viewer session ID value.</param>
    /// <param name="terminationCode">The termination code value.</param>
    /// <returns>The operation's result.</returns>
    public async ValueTask<bool> ReleaseViewerAsync(StreamSessionId viewerSessionId, string? terminationCode = null)
    {
        IAsyncDisposable? outputOwner = null;
        await _gate.WaitAsync();
        try
        {
            if (terminationCode is null)
            {
                _terminations.Remove(viewerSessionId);
            }
            if (!_viewers.Remove(viewerSessionId, out var viewer))
            {
                return false;
            }
            RememberTermination(viewerSessionId, terminationCode);
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

    /// <summary>Releases output asynchronously.</summary>
    /// <param name="outputId">The output ID value.</param>
    /// <param name="terminationCode">The termination code value.</param>
    /// <returns>A task that completes when the operation has finished.</returns>
    public async ValueTask ReleaseOutputAsync(StreamSessionId outputId, string? terminationCode = null)
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
                RememberTermination(viewer.ViewerSessionId, terminationCode);
                viewer.ExpirationCancellation.Cancel();
                viewer.Output.ViewerReleased?.Invoke(viewer.ViewerSessionId);
            }
            var output = _outputs.SingleOrDefault(pair => pair.Value.OutputId == outputId);
            if (output.Value is not null)
            {
                _outputs.Remove(output.Key);
                Lineup.Core.Extensibility.Diagnostics.LineupTelemetry.CmafProducers.Add(-1);
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

    /// <summary>Gets playback status.</summary>
    /// <param name="viewerSessionId">The viewer session ID value.</param>
    /// <returns>The operation's result.</returns>
    public CmafPlaybackStatus GetPlaybackStatus(StreamSessionId viewerSessionId)
    {
        _gate.Wait();
        try
        {
            PruneTerminations();
            return _viewers.ContainsKey(viewerSessionId)
                ? new(true, null, false)
                : _terminations.TryGetValue(viewerSessionId, out var termination)
                    ? new(false, termination.Code, true)
                    : new(false, "session_unavailable", false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void PruneTerminations()
    {
        foreach (var id in _terminations.Where(pair => _timeProvider.GetElapsedTime(pair.Value.Timestamp) >= TimeSpan.FromMinutes(2)).Select(pair => pair.Key).ToArray())
        {
            _terminations.Remove(id);
        }
    }

    private void RememberTermination(StreamSessionId viewerId, string? code)
    {
        if (code is not ("source_eof" or "source_failure"))
        {
            return;
        }
        PruneTerminations();
        if (_terminations.Count >= 2048)
        {
            _terminations.Remove(_terminations.MinBy(pair => pair.Value.Timestamp).Key);
        }
        _terminations[viewerId] = (code, _timeProvider.GetTimestamp());
    }

    /// <summary>Gets viewers.</summary>
    /// <returns>The operation's result.</returns>
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

    /// <summary>Gets diagnostics.</summary>
    /// <returns>The operation's result.</returns>
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

    /// <summary>Gets producer diagnostics.</summary>
    /// <returns>The operation's result.</returns>
    public StreamingProducerDiagnostics GetProducerDiagnostics()
    {
        var diagnostics = GetDiagnostics();
        return new StreamingProducerDiagnostics(
            diagnostics.Count(output => !output.IsWarm),
            diagnostics.Count(output => output.IsWarm),
            diagnostics.Count);
    }

    /// <summary>Stops the CMAF output coordinator asynchronously.</summary>
    /// <returns>A task that completes when the operation has finished.</returns>
    public async ValueTask StopAsync()
    {
        IAsyncDisposable[] owners;
        ViewerEntry[] viewers;
        await _gate.WaitAsync();
        try
        {
            if (_stopping)
            {
                return;
            }
            _stopping = true;
            viewers = _viewers.Values.ToArray();
            foreach (var viewer in viewers)
            {
                viewer.ExpirationCancellation.Cancel();
            }
            foreach (var output in _outputs.Values)
            {
                CancelWarmExpiration(output);
            }
            owners = _outputs.Values.Select(output => output.Owner).ToArray();
            _viewers.Clear();
            _terminations.Clear();
            Lineup.Core.Extensibility.Diagnostics.LineupTelemetry.CmafProducers.Add(-_outputs.Count);
            _outputs.Clear();
        }
        finally
        {
            _gate.Release();
        }
        List<Exception>? failures = null;
        foreach (var viewer in viewers)
        {
            try
            {
                viewer.Output.ViewerReleased?.Invoke(viewer.ViewerSessionId);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }
        foreach (var owner in owners)
        {
            try
            {
                await owner.DisposeAsync();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }
        if (failures is not null)
        {
            throw new AggregateException("One or more CMAF viewers or producers could not be released during shutdown.", failures);
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
        finally
        {
            viewer.ExpirationCancellation.Dispose();
        }
    }

    private static TimeSpan NormalizeTimeout(TimeSpan? timeout)
    {
        var value = timeout ?? _defaultInactivityTimeout;
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
            Lineup.Core.Extensibility.Diagnostics.LineupTelemetry.CmafProducers.Add(-1);
            return output.Owner;
        }

        CancelWarmExpiration(output);
        var expiration = new CancellationTokenSource();
        output.WarmExpirationCancellation = expiration;
        Lineup.Core.Extensibility.Diagnostics.LineupTelemetry.WarmCmafProducers.Add(1);
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
                    Lineup.Core.Extensibility.Diagnostics.LineupTelemetry.WarmCmafProducers.Add(-1);
                    _outputs.Remove(transformKey);
                    Lineup.Core.Extensibility.Diagnostics.LineupTelemetry.CmafProducers.Add(-1);
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
        if (expiration is not null)
        {
            Lineup.Core.Extensibility.Diagnostics.LineupTelemetry.WarmCmafProducers.Add(-1);
        }
        expiration?.Cancel();
    }

    private sealed class OutputEntry(StreamSessionId outputId, IAsyncDisposable owner, object state, Action<StreamSessionId>? viewerReleased)
    {
        /// <summary>Gets the output ID.</summary>
        public StreamSessionId OutputId { get; } = outputId;
        /// <summary>Gets the owner.</summary>
        public IAsyncDisposable Owner { get; } = owner;
        /// <summary>Gets the state.</summary>
        public object State { get; } = state;
        /// <summary>Gets the viewer released.</summary>
        public Action<StreamSessionId>? ViewerReleased { get; } = viewerReleased;
        /// <summary>Gets or sets the reference count.</summary>
        public int ReferenceCount { get; set; }
        /// <summary>Gets or sets the warm expiration cancellation.</summary>
        public CancellationTokenSource? WarmExpirationCancellation { get; set; }
    }

    private sealed class ViewerEntry(StreamSessionId viewerSessionId, StreamTransformKey transformKey, OutputEntry output, long lastAccessTimestamp, TimeSpan keepWarmPeriod)
    {
        /// <summary>Gets the viewer session ID.</summary>
        public StreamSessionId ViewerSessionId { get; } = viewerSessionId;
        /// <summary>Gets the transform key.</summary>
        public StreamTransformKey TransformKey { get; } = transformKey;
        /// <summary>Gets the output.</summary>
        public OutputEntry Output { get; } = output;
        /// <summary>Gets or sets the last access timestamp.</summary>
        public long LastAccessTimestamp { get; set; } = lastAccessTimestamp;
        /// <summary>Gets the keep warm period.</summary>
        public TimeSpan KeepWarmPeriod { get; } = keepWarmPeriod;
        /// <summary>Gets the expiration cancellation.</summary>
        public CancellationTokenSource ExpirationCancellation { get; } = new();
        /// <summary>Gets or sets the expiration task.</summary>
        public Task? ExpirationTask { get; set; }

        /// <summary>Creates snapshot.</summary>
        /// <returns>The operation's result.</returns>
        public CmafViewerSnapshot CreateSnapshot() => new(ViewerSessionId, Output.OutputId, TransformKey, Output.State);
    }

    private sealed class OutputLease(CmafOutputCoordinator coordinator, ViewerEntry viewer) : ICmafOutputLease
    {
        private CmafOutputCoordinator? _coordinator = coordinator;

        /// <summary>Gets the viewer session ID.</summary>
        public StreamSessionId ViewerSessionId => viewer.ViewerSessionId;
        /// <summary>Gets the output ID.</summary>
        public StreamSessionId OutputId => viewer.Output.OutputId;
        /// <summary>Gets the transform key.</summary>
        public StreamTransformKey TransformKey => viewer.TransformKey;
        /// <summary>Gets the state.</summary>
        public object State => viewer.Output.State;

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            var owner = Interlocked.Exchange(ref _coordinator, null);
            return owner is null ? ValueTask.CompletedTask : ReleaseAsync(owner, ViewerSessionId);
        }

        private static async ValueTask ReleaseAsync(CmafOutputCoordinator owner, StreamSessionId viewerSessionId) =>
            _ = await owner.ReleaseViewerAsync(viewerSessionId);
    }
}
