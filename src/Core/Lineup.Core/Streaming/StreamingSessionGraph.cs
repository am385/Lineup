using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Lineup.Core.Streaming;

/// <summary>
/// Describes one reusable transform stage in an ordered streaming pipeline.
/// </summary>
/// <param name="Key">Canonical identity for the bytes produced by this stage.</param>
/// <param name="Transform">Transform factory input used only when the canonical node does not already exist.</param>
public sealed record StreamTransformStage(StreamTransformKey Key, IStreamTransform Transform);

/// <summary>
/// Describes one capacity-controlled physical acquisition.
/// </summary>
/// <param name="DeviceId">Physical device that owns the source.</param>
/// <param name="AcquisitionKey">Canonical source and tune identity.</param>
/// <param name="Capacity">Current physical device capacity.</param>
/// <param name="EnforceCapacity">Whether the graph acquires capacity itself; false when the factory selects among failover candidates.</param>
public sealed record StreamAcquisitionDescriptor(PhysicalDeviceId DeviceId, PhysicalAcquisitionKey AcquisitionKey, PhysicalTunerCapacity Capacity, bool EnforceCapacity = true)
{
    private static readonly ConditionalWeakTable<IPhysicalTunerDevice, RuntimeIdentity> _runtimeIdentities = new();

    /// <summary>Gets the host runtime captured when this route was resolved, without changing the plugin contract.</summary>
    internal IPhysicalTunerDevice? Runtime { get; init; }

    /// <summary>Gets the exact source identity, including tune parameters and the captured adapter generation.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string SourceIdentity => System.Text.Json.JsonSerializer.Serialize(new { DeviceId, AcquisitionKey, RuntimeId = GetRuntimeId(Runtime) });

    /// <summary>Gets the process-local identity of one adapter instance.</summary>
    internal static Guid GetRuntimeId(IPhysicalTunerDevice? runtime) =>
        runtime is null ? Guid.Empty : _runtimeIdentities.GetValue(runtime, _ => new RuntimeIdentity(Guid.NewGuid())).Id;

    private sealed record RuntimeIdentity(Guid Id);
}

/// <summary>
/// Owns a readable source used by the streaming session graph.
/// </summary>
public interface IStreamSourceLease : IAsyncDisposable
{
    /// <summary>Gets the readable source.</summary>
    Stream Stream { get; }

    /// <summary>Gets the source content type when known.</summary>
    string? ContentType { get; }
}

/// <summary>
/// Creates one physical source acquisition.
/// </summary>
/// <param name="cancellationToken">Stops source creation.</param>
/// <returns>An owned readable source.</returns>
public delegate ValueTask<IStreamSourceLease> StreamAcquisitionFactory(CancellationToken cancellationToken);

/// <summary>
/// Creates one reusable transform over a shared input subscription.
/// </summary>
public interface IStreamTransform
{
    /// <summary>Starts the transform and returns its owned output.</summary>
    ValueTask<IStreamSourceLease> StartAsync(Stream input, CancellationToken cancellationToken);
}

/// <summary>
/// Owns one consumer subscription to the session graph.
/// </summary>
public interface IStreamSessionLease : IAsyncDisposable
{
    /// <summary>Gets the unique consumer session identity.</summary>
    StreamSessionId SessionId { get; }

    /// <summary>Gets the stable identity of the shared graph node serving this subscription.</summary>
    string ProducerNodeId { get; }

    /// <summary>Gets the readable shared output.</summary>
    Stream Stream { get; }

    /// <summary>Gets the output content type when known.</summary>
    string? ContentType { get; }
}

/// <summary>
/// Describes one active shared stream node.
/// </summary>
/// <param name="Kind">Acquisition or transform.</param>
/// <param name="Key">Stable diagnostic key.</param>
/// <param name="ReceiverCount">Current direct receiver count.</param>
/// <param name="IsRunning">Whether the node producer is active.</param>
/// <param name="IsWarm">Whether the node has no consumers and is waiting for its reuse period to expire.</param>
/// <param name="TotalSubscriptions">Total subscriptions accepted by this node generation.</param>
public sealed record StreamNodeDiagnostics(string Kind, string Key, int ReceiverCount, bool IsRunning, bool IsWarm = false, long TotalSubscriptions = 0)
{
    /// <summary>Gets the stable identity of this runtime node.</summary>
    public string NodeId { get; init; } = "";

    /// <summary>Gets the stable identity of the parent node, or <see langword="null"/> for a physical acquisition.</summary>
    public string? ParentNodeId { get; init; }

    /// <summary>Gets the owning physical device for an acquisition node.</summary>
    public PhysicalDeviceId? PhysicalDeviceId { get; init; }

    /// <summary>Gets the acquired physical source channel for an acquisition node.</summary>
    public SourceChannelId? SourceChannelId { get; init; }

    /// <summary>Gets the transform stage name for a transform node.</summary>
    public string? Stage { get; init; }
}

/// <summary>
/// Immutable diagnostic snapshot of the provider-neutral stream graph.
/// </summary>
/// <param name="Nodes">Active acquisition and transform nodes.</param>
/// <param name="Capacity">Physical capacity state.</param>
public sealed record StreamingSessionGraphDiagnostics(IReadOnlyList<StreamNodeDiagnostics> Nodes, IReadOnlyList<PhysicalCapacityDiagnostics> Capacity)
{
    /// <summary>Gets distinct queued payload bytes retained across the graph.</summary>
    public long BufferedBytes { get; init; }

    /// <summary>Gets the number of subscriptions evicted for buffering limits.</summary>
    public long BufferEvictions { get; init; }

    /// <summary>Gets the last 64 unexpected producer completions; reconnect creates a new route acquisition.</summary>
    public IReadOnlyList<StreamTermination> RecentTerminations { get; init; } = [];
}

/// <summary>Describes a safe terminal reason without exposing source addresses or raw exceptions.</summary>
/// <param name="NodeId">Producer identity used by stream diagnostics.</param>
/// <param name="Kind">Acquisition or transform.</param>
/// <param name="Reason">Stable terminal reason.</param>
/// <param name="ObservedAtUtc">Termination time.</param>
/// <param name="CorrelationId">Originating trace identity.</param>
public sealed record StreamTermination(string NodeId, string Kind, string Reason, DateTimeOffset ObservedAtUtc, string? CorrelationId);

/// <summary>
/// Shares physical acquisitions and identical transforms across consumer sessions.
/// </summary>
public interface IStreamingSessionGraph
{
    /// <summary>Acquires a subscription to a shared physical source.</summary>
    ValueTask<IStreamSessionLease> AcquireAsync(StreamAcquisitionDescriptor descriptor, StreamAcquisitionFactory acquisitionFactory, CancellationToken cancellationToken = default);

    /// <summary>Acquires a subscription to a shared transform over a physical source.</summary>
    ValueTask<IStreamSessionLease> AcquireTransformAsync(
        StreamAcquisitionDescriptor descriptor,
        StreamTransformKey transformKey,
        StreamAcquisitionFactory acquisitionFactory,
        IStreamTransform transform,
        CancellationToken cancellationToken = default
    );

    /// <summary>Acquires an ordered reusable transform pipeline, retaining an unused leaf until its idle deadline regardless of incoming traffic.</summary>
    ValueTask<IStreamSessionLease> AcquirePipelineAsync(
        StreamAcquisitionDescriptor descriptor,
        StreamAcquisitionFactory acquisitionFactory,
        IReadOnlyList<StreamTransformStage> stages,
        TimeSpan keepWarmPeriod,
        CancellationToken cancellationToken = default
    );

    /// <summary>Gets or creates metadata scoped to the lifetime of one shared physical acquisition.</summary>
    Task<T> GetOrCreateAcquisitionMetadataAsync<T>(
        StreamAcquisitionDescriptor descriptor,
        StreamAcquisitionFactory acquisitionFactory,
        string metadataKey,
        Func<Stream, CancellationToken, Task<T>> metadataFactory,
        CancellationToken cancellationToken = default
    );

    /// <summary>Gets complete metadata or performs one shared delayed refresh when the cached result remains provisional.</summary>
    Task<T> GetOrRefreshAcquisitionMetadataAsync<T>(
        StreamAcquisitionDescriptor descriptor,
        StreamAcquisitionFactory acquisitionFactory,
        string metadataKey,
        Func<Stream, CancellationToken, Task<T>> metadataFactory,
        AcquisitionMetadataRefreshPolicy<T> refreshPolicy,
        CancellationToken cancellationToken = default
    );

    /// <summary>Gets immutable active graph and capacity diagnostics.</summary>
    StreamingSessionGraphDiagnostics GetDiagnostics();

    /// <summary>Rejects new sessions and stops transforms before acquisitions; cancellation bounds waiting without skipping acquisition stop signals.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}

/// <summary>Defines completeness, merge, and retry behavior for successful but provisional acquisition metadata.</summary>
public sealed record AcquisitionMetadataRefreshPolicy<T>
{
    /// <summary>Gets the predicate that determines whether metadata is complete enough to reuse.</summary>
    public required Func<T, bool> IsReusable { get; init; }

    /// <summary>Gets the function that retains the richest fields from the prior and refreshed observations.</summary>
    public required Func<T, T, T> Merge { get; init; }

    /// <summary>Gets the delay before refreshing one provisional observation.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets the maximum duration of each shared metadata operation, including source acquisition.</summary>
    public TimeSpan OperationTimeout { get; init; } = Timeout.InfiniteTimeSpan;

    /// <summary>Gets how long a caller waits for a shared refresh before using the provisional observation.</summary>
    public TimeSpan RefreshWaitTimeout { get; init; } = Timeout.InfiniteTimeSpan;
}

/// <summary>
/// Thread-safe, reference-counted provider-neutral streaming session graph.
/// </summary>
public sealed class StreamingSessionGraph : IStreamingSessionGraph
{
    private const int BufferSize = 64 * 1024;
    private readonly StreamBufferBudget _buffers;
    private readonly IPhysicalCapacityLeaseRegistry _capacity;
    private readonly ILogger<StreamingSessionGraph> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly PhysicalDeviceRuntimeState? _runtimes;
    private readonly IMediaWorkAdmission _mediaWork;
    private readonly ConcurrentDictionary<AcquisitionNodeKey, SharedNode> _acquisitions = [];
    private readonly ConcurrentDictionary<TransformNodeKey, SharedNode> _transforms = [];
    private readonly ConcurrentDictionary<AcquisitionMetadataKey, Lazy<Task<AcquisitionMetadataSnapshot>>> _metadata = [];
    private readonly Lock _lifecycleGate = new();
    private readonly ConcurrentQueue<StreamTermination> _terminations = new();
    private bool _isStopping;

    /// <summary>Initializes the shared graph.</summary>
    public StreamingSessionGraph(IPhysicalCapacityLeaseRegistry capacity, ILogger<StreamingSessionGraph> logger)
        : this(capacity, logger, TimeProvider.System)
    {
    }

    /// <summary>Initializes the shared graph with a clock for metadata timing and keep-warm expiration.</summary>
    internal StreamingSessionGraph(IPhysicalCapacityLeaseRegistry capacity, ILogger<StreamingSessionGraph> logger, TimeProvider timeProvider)
        : this(capacity, logger, timeProvider, null)
    {
    }

    /// <summary>Initializes the graph with catalog admission and retirement notifications.</summary>
    internal StreamingSessionGraph(
        IPhysicalCapacityLeaseRegistry capacity,
        ILogger<StreamingSessionGraph> logger,
        TimeProvider timeProvider,
        PhysicalDeviceRuntimeState? runtimes,
        StreamingResourceOptions? resources = null,
        IMediaWorkAdmission? mediaWork = null
    )
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _capacity = capacity;
        _logger = logger;
        _timeProvider = timeProvider;
        _runtimes = runtimes;
        _buffers = new(resources ?? new(), timeProvider);
        _mediaWork = mediaWork ?? new MediaWorkAdmission(resources ?? new());
        if (_runtimes is not null)
        {
            _runtimes.Retiring += RetireDevicesAsync;
        }
    }

    /// <summary>Executes acquire asynchronously.</summary>
    /// <param name="descriptor">The descriptor value.</param>
    /// <param name="acquisitionFactory">The acquisition factory value.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The operation's result.</returns>
    public ValueTask<IStreamSessionLease> AcquireAsync(StreamAcquisitionDescriptor descriptor, StreamAcquisitionFactory acquisitionFactory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(acquisitionFactory);
        ValidateDescriptor(descriptor);
        return AcquirePipelineAsync(descriptor, acquisitionFactory, [], TimeSpan.Zero, cancellationToken);
    }

    /// <summary>Executes acquire transform asynchronously.</summary>
    /// <param name="descriptor">The descriptor value.</param>
    /// <param name="transformKey">The transform key value.</param>
    /// <param name="acquisitionFactory">The acquisition factory value.</param>
    /// <param name="transform">The transform value.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The operation's result.</returns>
    public ValueTask<IStreamSessionLease> AcquireTransformAsync(
        StreamAcquisitionDescriptor descriptor,
        StreamTransformKey transformKey,
        StreamAcquisitionFactory acquisitionFactory,
        IStreamTransform transform,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(acquisitionFactory);
        ArgumentNullException.ThrowIfNull(transform);
        ValidateDescriptor(descriptor);
        if (string.IsNullOrWhiteSpace(transformKey.Stage) || string.IsNullOrWhiteSpace(transformKey.SettingsKey))
        {
            throw new ArgumentException("Transform stage and settings keys are required.", nameof(transformKey));
        }

        return AcquirePipelineAsync(
            descriptor,
            acquisitionFactory,
            [new StreamTransformStage(transformKey, transform)],
            TimeSpan.Zero,
            cancellationToken);
    }

    /// <summary>Executes acquire pipeline asynchronously.</summary>
    /// <param name="descriptor">The descriptor value.</param>
    /// <param name="acquisitionFactory">The acquisition factory value.</param>
    /// <param name="stages">The stages value.</param>
    /// <param name="keepWarmPeriod">The keep warm period value.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The operation's result.</returns>
    public async ValueTask<IStreamSessionLease> AcquirePipelineAsync(
        StreamAcquisitionDescriptor descriptor,
        StreamAcquisitionFactory acquisitionFactory,
        IReadOnlyList<StreamTransformStage> stages,
        TimeSpan keepWarmPeriod,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(acquisitionFactory);
        ArgumentNullException.ThrowIfNull(stages);
        ValidateDescriptor(descriptor);
        if (keepWarmPeriod < TimeSpan.Zero || keepWarmPeriod > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(keepWarmPeriod), keepWarmPeriod, "Keep-warm period must be between zero and five minutes.");
        }
        foreach (var stage in stages)
        {
            ArgumentNullException.ThrowIfNull(stage);
            ArgumentNullException.ThrowIfNull(stage.Transform);
            if (string.IsNullOrWhiteSpace(stage.Key.Stage) || string.IsNullOrWhiteSpace(stage.Key.SettingsKey))
            {
                throw new ArgumentException("Transform stage and settings keys are required.", nameof(stages));
            }
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SharedNode node;
            IStreamSessionLease? createdLease = null;
            Task? cleanup = null;
            lock (_lifecycleGate)
            {
                ThrowIfStopping();
                ThrowIfRuntimeUnavailable(descriptor);
                GraphNodeKey key = AcquisitionNodeKey.Create(descriptor);
                node = GetOrCreateAcquisitionNode(descriptor, acquisitionFactory);
                foreach (var stage in stages)
                {
                    if (!node.CanSubscribe)
                    {
                        cleanup = node.Completion;
                        break;
                    }
                    var transformKey = new TransformNodeKey(key, stage.Key);
                    node = GetOrCreateTransformNode(node, transformKey, stage.Transform);
                    key = transformKey;
                }
                if (cleanup is null && !node.TrySubscribe(keepWarmPeriod, out createdLease))
                {
                    cleanup = node.Completion;
                }
            }
            if (cleanup is not null)
            {
                await cleanup.WaitAsync(cancellationToken);
                continue;
            }
            if (createdLease is null)
            {
                throw new InvalidOperationException("The shared stream did not create a subscription.");
            }
            try
            {
                await node.Started.WaitAsync(cancellationToken);
                ThrowIfRuntimeUnavailable(descriptor);
                return createdLease;
            }
            catch (SharedParentStoppedException)
            {
                await createdLease.DisposeAsync();
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch
            {
                await createdLease.DisposeAsync();
                throw;
            }
        }
    }

    /// <summary>Gets or create acquisition metadata asynchronously.</summary>
    /// <param name="descriptor">The descriptor value.</param>
    /// <param name="acquisitionFactory">The acquisition factory value.</param>
    /// <param name="metadataKey">The metadata key value.</param>
    /// <param name="metadataFactory">The metadata factory value.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <typeparam name="T">Type represented by the generic parameter.</typeparam>
    /// <returns>The operation's result.</returns>
    public async Task<T> GetOrCreateAcquisitionMetadataAsync<T>(
        StreamAcquisitionDescriptor descriptor,
        StreamAcquisitionFactory acquisitionFactory,
        string metadataKey,
        Func<Stream, CancellationToken, Task<T>> metadataFactory,
        CancellationToken cancellationToken = default
    )
        => await GetAcquisitionMetadataAsync(
            descriptor,
            acquisitionFactory,
            metadataKey,
            metadataFactory,
            refreshPolicy: null,
            cancellationToken);

    /// <summary>Gets or refresh acquisition metadata asynchronously.</summary>
    /// <param name="descriptor">The descriptor value.</param>
    /// <param name="acquisitionFactory">The acquisition factory value.</param>
    /// <param name="metadataKey">The metadata key value.</param>
    /// <param name="metadataFactory">The metadata factory value.</param>
    /// <param name="refreshPolicy">The refresh policy value.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <typeparam name="T">Type represented by the generic parameter.</typeparam>
    /// <returns>The operation's result.</returns>
    public async Task<T> GetOrRefreshAcquisitionMetadataAsync<T>(
        StreamAcquisitionDescriptor descriptor,
        StreamAcquisitionFactory acquisitionFactory,
        string metadataKey,
        Func<Stream, CancellationToken, Task<T>> metadataFactory,
        AcquisitionMetadataRefreshPolicy<T> refreshPolicy,
        CancellationToken cancellationToken = default
    )
        => await GetAcquisitionMetadataAsync(
            descriptor,
            acquisitionFactory,
            metadataKey,
            metadataFactory,
            refreshPolicy,
            cancellationToken);

    private async Task<T> GetAcquisitionMetadataAsync<T>(
        StreamAcquisitionDescriptor descriptor,
        StreamAcquisitionFactory acquisitionFactory,
        string metadataKey,
        Func<Stream, CancellationToken, Task<T>> metadataFactory,
        AcquisitionMetadataRefreshPolicy<T>? refreshPolicy,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(acquisitionFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(metadataKey);
        ArgumentNullException.ThrowIfNull(metadataFactory);
        if (refreshPolicy is not null)
        {
            ArgumentNullException.ThrowIfNull(refreshPolicy.IsReusable);
            ArgumentNullException.ThrowIfNull(refreshPolicy.Merge);
            if (refreshPolicy.RetryDelay < TimeSpan.Zero || refreshPolicy.RetryDelay > TimeSpan.FromSeconds(30))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(refreshPolicy),
                    refreshPolicy.RetryDelay,
                    "The metadata refresh delay must be between zero and 30 seconds.");
            }
            ValidateMetadataTimeout(refreshPolicy.OperationTimeout, nameof(refreshPolicy.OperationTimeout));
            ValidateMetadataTimeout(refreshPolicy.RefreshWaitTimeout, nameof(refreshPolicy.RefreshWaitTimeout));
        }
        ValidateDescriptor(descriptor);

        SharedNode node;
        AcquisitionNodeKey acquisitionKey;
        AcquisitionMetadataKey key;
        Lazy<Task<AcquisitionMetadataSnapshot>> lazy;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_lifecycleGate)
            {
                ThrowIfStopping();
                ThrowIfRuntimeUnavailable(descriptor);
                acquisitionKey = AcquisitionNodeKey.Create(descriptor);
                node = GetOrCreateAcquisitionNode(descriptor, acquisitionFactory);
                if (node.CanSubscribe)
                {
                    key = new AcquisitionMetadataKey(acquisitionKey, metadataKey, node);
                    lazy = _metadata.GetOrAdd(key, _ => CreateMetadataFactory(node, metadataFactory, refreshPolicy?.OperationTimeout ?? Timeout.InfiniteTimeSpan));
                    _ = lazy.Value;
                    break;
                }
            }
            await node.Completion.WaitAsync(cancellationToken);
        }

        var snapshot = await AwaitMetadataAsync<T>(key, lazy, cancellationToken);
        ThrowIfRuntimeUnavailable(descriptor);
        if (refreshPolicy is null || refreshPolicy.IsReusable(snapshot.Value))
        {
            return snapshot.Value;
        }
        if (snapshot.RefreshAttempted)
        {
            _metadata.TryRemove(new KeyValuePair<AcquisitionMetadataKey, Lazy<Task<AcquisitionMetadataSnapshot>>>(key, lazy));
            return snapshot.Value;
        }

        var remainingDelay = refreshPolicy.RetryDelay - (_timeProvider.GetUtcNow() - snapshot.ObservedAtUtc);
        if (remainingDelay > TimeSpan.Zero)
        {
            await Task.Delay(remainingDelay, _timeProvider, cancellationToken);
        }
        ThrowIfRuntimeUnavailable(descriptor);

        var replacement = CreateMetadataFactory(
            node,
            async (stream, token) => refreshPolicy.Merge(snapshot.Value, await metadataFactory(stream, token)),
            refreshPolicy.OperationTimeout,
            refreshAttempted: true);
        if (!_metadata.TryUpdate(key, replacement, lazy))
        {
            if (!_metadata.TryGetValue(key, out replacement))
            {
                return snapshot.Value;
            }
        }

        AcquisitionMetadataValue<T> refreshed;
        try
        {
            refreshed = await AwaitMetadataAsync<T>(
                key,
                replacement,
                cancellationToken,
                refreshPolicy.RefreshWaitTimeout,
                evictOnTimeout: false);
        }
        catch (TimeoutException)
        {
            return snapshot.Value;
        }
        if (!refreshPolicy.IsReusable(refreshed.Value))
        {
            _metadata.TryRemove(new KeyValuePair<AcquisitionMetadataKey, Lazy<Task<AcquisitionMetadataSnapshot>>>(key, replacement));
        }
        return refreshed.Value;
    }

    private Lazy<Task<AcquisitionMetadataSnapshot>> CreateMetadataFactory<T>(SharedNode node, Func<Stream, CancellationToken, Task<T>> metadataFactory, TimeSpan operationTimeout, bool refreshAttempted = false) =>
        new(
            async () =>
            {
                using var timeout = operationTimeout == Timeout.InfiniteTimeSpan ? null : new CancellationTokenSource(operationTimeout, _timeProvider);
                using var operation = CancellationTokenSource.CreateLinkedTokenSource(node.LifetimeToken, timeout?.Token ?? CancellationToken.None);
                var operationToken = operation.Token;
                try
                {
                    await using var lease = await SubscribeNodeAsync(node, operationToken);
                    var value = (object?)await metadataFactory(lease.Stream, operationToken)
                        ?? throw new InvalidOperationException("Shared acquisition metadata cannot be null.");
                    operationToken.ThrowIfCancellationRequested();
                    return new AcquisitionMetadataSnapshot(value, _timeProvider.GetUtcNow(), refreshAttempted);
                }
                catch (OperationCanceledException exception) when (timeout?.IsCancellationRequested == true)
                {
                    throw new TimeoutException($"Shared acquisition metadata did not complete within {operationTimeout}.", exception);
                }
            },
            LazyThreadSafetyMode.ExecutionAndPublication);

    private async Task<AcquisitionMetadataValue<T>> AwaitMetadataAsync<T>(
        AcquisitionMetadataKey key,
        Lazy<Task<AcquisitionMetadataSnapshot>> lazy,
        CancellationToken cancellationToken,
        TimeSpan waitTimeout = default,
        bool evictOnTimeout = true
    )
    {
        try
        {
            var snapshot = waitTimeout == default || waitTimeout == Timeout.InfiniteTimeSpan
                ? await lazy.Value.WaitAsync(cancellationToken)
                : await lazy.Value.WaitAsync(waitTimeout, _timeProvider, cancellationToken);
            return new AcquisitionMetadataValue<T>((T)snapshot.Value, snapshot.ObservedAtUtc, snapshot.RefreshAttempted);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException) when (!evictOnTimeout)
        {
            throw;
        }
        catch
        {
            _metadata.TryRemove(new KeyValuePair<AcquisitionMetadataKey, Lazy<Task<AcquisitionMetadataSnapshot>>>(key, lazy));
            throw;
        }
    }

    private static void ValidateMetadataTimeout(TimeSpan timeout, string parameterName)
    {
        if (timeout != Timeout.InfiniteTimeSpan && (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(5)))
        {
            throw new ArgumentOutOfRangeException(parameterName, timeout, "The metadata timeout must be infinite or between zero and five minutes.");
        }
    }

    /// <summary>Gets diagnostics.</summary>
    /// <returns>The operation's result.</returns>
    public StreamingSessionGraphDiagnostics GetDiagnostics()
    {
        var nodes = _acquisitions.Values
            .Concat(_transforms.Values)
            .Select(node => node.GetDiagnostics())
            .OrderBy(node => node.Kind, StringComparer.Ordinal)
            .ThenBy(node => node.Key, StringComparer.Ordinal)
            .ToArray();
        return new StreamingSessionGraphDiagnostics(nodes, _capacity.GetDiagnostics())
        {
            BufferedBytes = _buffers.RetainedBytes,
            BufferEvictions = _buffers.Evictions,
            RecentTerminations = _terminations.ToArray()
        };
    }

    /// <summary>Stops the streaming session graph asynchronously.</summary>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task that completes when the operation has finished.</returns>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        SharedNode[] transforms;
        SharedNode[] acquisitions;
        lock (_lifecycleGate)
        {
            _isStopping = true;
            transforms = _transforms.Values.ToArray();
            acquisitions = _acquisitions.Values.ToArray();
        }

        try
        {
            foreach (var node in transforms)
            {
                node.Stop();
            }
            await Task.WhenAll(transforms.Select(node => node.Completion)).WaitAsync(cancellationToken);
        }
        finally
        {
            foreach (var node in acquisitions)
            {
                node.Stop();
            }
            _buffers.Dispose();
        }
        await Task.WhenAll(acquisitions.Select(node => node.Completion)).WaitAsync(cancellationToken);
    }

    private async Task RetireDevicesAsync(IReadOnlyList<IPhysicalTunerDevice> devices)
    {
        var deviceIds = devices.Select(device => device.Snapshot.Id).ToHashSet();
        SharedNode[] retired;
        lock (_lifecycleGate)
        {
            retired = _transforms.Where(pair => deviceIds.Contains(GetAcquisition(pair.Key).DeviceId)).Select(pair => pair.Value)
                .Concat(_acquisitions.Where(pair => deviceIds.Contains(pair.Key.DeviceId)).Select(pair => pair.Value)).ToArray();
            foreach (var node in retired)
            {
                node.Stop();
            }
        }
        await Task.WhenAll(retired.Select(node => node.Completion));
    }

    private static AcquisitionNodeKey GetAcquisition(GraphNodeKey key) =>
        key is TransformNodeKey transform ? GetAcquisition(transform.Parent) : (AcquisitionNodeKey)key;

    private void ThrowIfRuntimeUnavailable(StreamAcquisitionDescriptor descriptor)
    {
        if (descriptor.Runtime is { } runtime && _runtimes is not null && !_runtimes.IsCurrent(runtime))
        {
            throw new InvalidOperationException("The physical device runtime changed. Resolve a new route before opening or probing the stream.");
        }
    }

    private SharedNode GetOrCreateAcquisitionNode(StreamAcquisitionDescriptor descriptor, StreamAcquisitionFactory acquisitionFactory)
    {
        var key = AcquisitionNodeKey.Create(descriptor);
        return _acquisitions.GetOrAdd(
            key,
            _ => new SharedNode(
                "acquisition",
                key,
                async lifetime =>
                {
                    if (!descriptor.EnforceCapacity)
                    {
                        return await acquisitionFactory(lifetime);
                    }
                    var capacityKey = descriptor.AcquisitionKey with { RuntimeId = StreamAcquisitionDescriptor.GetRuntimeId(descriptor.Runtime) };
                    var capacityLease = await _capacity.TryAcquireAsync(descriptor.DeviceId, capacityKey, descriptor.Capacity, lifetime);
                    if (capacityLease is null)
                    {
                        await _mediaWork.ReclaimIdleAsync(lifetime);
                        capacityLease = await _capacity.TryAcquireAsync(descriptor.DeviceId, capacityKey, descriptor.Capacity, lifetime)
                            ?? throw new StreamCapacityExceededException(descriptor.DeviceId, descriptor.Capacity);
                    }
                    try
                    {
                        var source = await acquisitionFactory(lifetime);
                        return new CompositeSourceLease(source, capacityLease);
                    }
                    catch
                    {
                        await capacityLease.DisposeAsync();
                        throw;
                    }
                },
                _logger,
                _timeProvider,
                completed =>
                {
                    RecordTermination(completed);
                    RemoveAcquisitionNode(key, completed);
                },
                _buffers,
                _mediaWork));
    }

    private SharedNode GetOrCreateTransformNode(SharedNode parent, TransformNodeKey key, IStreamTransform transform)
    {
        return _transforms.GetOrAdd(
            key,
            _ => new SharedNode(
                "transform",
                key,
                async lifetime =>
                {
                    var input = await SubscribeNodeAsync(parent, lifetime);
                    try
                    {
                        var output = await transform.StartAsync(input.Stream, lifetime);
                        return new CompositeSourceLease(output, input);
                    }
                    catch
                    {
                        await input.DisposeAsync();
                        throw;
                    }
                },
                _logger,
                _timeProvider,
                completed =>
                {
                    RecordTermination(completed);
                    _transforms.TryRemove(new KeyValuePair<TransformNodeKey, SharedNode>(key, completed));
                },
                _buffers,
                _mediaWork));
    }

    private static async ValueTask<IStreamSessionLease> SubscribeNodeAsync(SharedNode node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!node.TrySubscribe(TimeSpan.Zero, out var lease))
        {
            throw new SharedParentStoppedException();
        }
        try
        {
            await node.Started.WaitAsync(cancellationToken);
            return lease;
        }
        catch
        {
            await lease.DisposeAsync();
            throw;
        }
    }

    private void RecordTermination(SharedNode node)
    {
        if (node.Termination is not { } termination)
        {
            return;
        }
        _terminations.Enqueue(termination);
        while (_terminations.Count > 64)
        {
            _terminations.TryDequeue(out _);
        }
    }

    private void RemoveAcquisitionNode(AcquisitionNodeKey key, SharedNode node)
    {
        _acquisitions.TryRemove(new KeyValuePair<AcquisitionNodeKey, SharedNode>(key, node));
        foreach (var metadataKey in _metadata.Keys.Where(candidate => ReferenceEquals(candidate.Node, node)))
        {
            _metadata.TryRemove(metadataKey, out _);
        }
    }

    private static void ValidateDescriptor(StreamAcquisitionDescriptor descriptor)
    {
        if (descriptor.AcquisitionKey.ChannelId.PhysicalDeviceId != descriptor.DeviceId)
        {
            throw new ArgumentException("The acquisition channel must belong to the requested physical device.", nameof(descriptor));
        }
    }

    private void ThrowIfStopping()
    {
        if (_isStopping)
        {
            throw new ObjectDisposedException(nameof(StreamingSessionGraph), "The streaming session graph is shutting down.");
        }
    }

    private abstract record GraphNodeKey
    {
        /// <summary>Gets the node ID.</summary>
        public string NodeId
        {
            get
            {
                var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(ToString()));
                return Convert.ToHexString(bytes.AsSpan(0, 12)).ToLowerInvariant();
            }
        }
    }

    private sealed record AcquisitionNodeKey(PhysicalDeviceId DeviceId, PhysicalAcquisitionKey AcquisitionKey, Guid RuntimeId) : GraphNodeKey
    {
        /// <summary>Creates the acquisition node key.</summary>
        /// <param name="descriptor">The descriptor value.</param>
        /// <returns>The operation's result.</returns>
        public static AcquisitionNodeKey Create(StreamAcquisitionDescriptor descriptor) =>
            new(descriptor.DeviceId, descriptor.AcquisitionKey, StreamAcquisitionDescriptor.GetRuntimeId(descriptor.Runtime));

        /// <inheritdoc />
        public override string ToString() =>
            $"{DeviceId}:{AcquisitionKey.ChannelId.ProviderKey}:{AcquisitionKey.TuneKey}{(RuntimeId == Guid.Empty ? "" : $":{RuntimeId:N}")}";
    }

    private sealed record TransformNodeKey(GraphNodeKey Parent, StreamTransformKey Transform) : GraphNodeKey
    {
        /// <inheritdoc />
        public override string ToString() => $"{Parent}>{Transform.Stage}:{Transform.SettingsKey}";
    }

    private sealed record AcquisitionMetadataKey(AcquisitionNodeKey Acquisition, string MetadataKey, SharedNode Node);

    private sealed record AcquisitionMetadataSnapshot(object Value, DateTimeOffset ObservedAtUtc, bool RefreshAttempted);

    private sealed record AcquisitionMetadataValue<T>(T Value, DateTimeOffset ObservedAtUtc, bool RefreshAttempted);

    private sealed class SharedParentStoppedException() : IOException("The shared parent stream stopped before the transform could subscribe.");

    private sealed class SharedNode
    {
        private readonly string _kind;
        private readonly GraphNodeKey _key;
        private readonly Func<CancellationToken, ValueTask<IStreamSourceLease>> _sourceFactory;
        private readonly ILogger _logger;
        private readonly TimeProvider _timeProvider;
        private readonly Action<SharedNode> _onCompleted;
        private readonly StreamBufferBudget _buffers;
        private readonly IMediaWorkAdmission _mediaWork;
        private readonly object _gate = new();
        private readonly Dictionary<Guid, Subscriber> _subscribers = [];
        private readonly CancellationTokenSource _lifetime = new();
        private readonly CancellationToken _lifetimeToken;
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task? _runTask;
        private CancellationTokenSource? _warmCancellation;
        private long _totalSubscriptions;
        private bool _completed;
        private readonly string? _traceParent = System.Diagnostics.Activity.Current?.Id;

        /// <summary>Gets a terminal reason only for unexpected completion.</summary>
        public StreamTermination? Termination { get; private set; }

        /// <summary>Initializes the shared node.</summary>
        /// <param name="kind">The kind value.</param>
        /// <param name="key">The key value.</param>
        /// <param name="sourceFactory">The source factory value.</param>
        /// <param name="logger">The logger value.</param>
        /// <param name="timeProvider">The time provider value.</param>
        /// <param name="onCompleted">The on completed value.</param>
        /// <param name="buffers">The buffers value.</param>
        /// <param name="mediaWork">The media work value.</param>
        public SharedNode(
            string kind,
            GraphNodeKey key,
            Func<CancellationToken, ValueTask<IStreamSourceLease>> sourceFactory,
            ILogger logger,
            TimeProvider timeProvider,
            Action<SharedNode> onCompleted,
            StreamBufferBudget buffers,
            IMediaWorkAdmission mediaWork
        )
        {
            _kind = kind;
            _key = key;
            _sourceFactory = sourceFactory;
            _logger = logger;
            _timeProvider = timeProvider;
            _onCompleted = onCompleted;
            _buffers = buffers;
            _mediaWork = mediaWork;
            _lifetimeToken = _lifetime.Token;
        }

        /// <summary>Gets the producer lifetime, including after its cancellation source is disposed.</summary>
        public CancellationToken LifetimeToken => _lifetimeToken;

        /// <summary>Gets the completion.</summary>
        public Task Completion => _runTask ?? Task.CompletedTask;

        /// <summary>Gets the started.</summary>
        public Task Started => _started.Task;

        /// <summary>Gets whether a new consumer can join this node before cleanup starts.</summary>
        public bool CanSubscribe
        {
            get
            {
                lock (_gate)
                {
                    return !_completed;
                }
            }
        }

        /// <summary>Attempts to subscribe.</summary>
        /// <param name="keepWarmPeriod">The keep warm period value.</param>
        /// <param name="lease">The lease value.</param>
        /// <returns>The operation's result.</returns>
        public bool TrySubscribe(TimeSpan keepWarmPeriod, out IStreamSessionLease lease)
        {
            var id = Guid.NewGuid();
            lock (_gate)
            {
                if (_completed)
                {
                    lease = null!;
                    return false;
                }
                CancelWarmExpiration();
                var subscription = _buffers.Subscribe(() => _ = RemoveEvictedSubscriberAsync(id));
                _subscribers.Add(id, new Subscriber(subscription, keepWarmPeriod));
                _totalSubscriptions++;
                _runTask ??= Task.Run(RunAsync);
                lease = new SessionLease(_key.NodeId, subscription, () => RemoveSubscriberAsync(id));
            }
            return true;
        }

        /// <summary>Gets diagnostics.</summary>
        /// <returns>The operation's result.</returns>
        public StreamNodeDiagnostics GetDiagnostics()
        {
            lock (_gate)
            {
                return new StreamNodeDiagnostics(
                    _kind,
                    _key.ToString(),
                    _subscribers.Count,
                    _runTask is { IsCompleted: false },
                    _warmCancellation is not null,
                    _totalSubscriptions)
                {
                    NodeId = _key.NodeId,
                    ParentNodeId = (_key as TransformNodeKey)?.Parent.NodeId,
                    PhysicalDeviceId = (_key as AcquisitionNodeKey)?.DeviceId,
                    SourceChannelId = (_key as AcquisitionNodeKey)?.AcquisitionKey.ChannelId,
                    Stage = (_key as TransformNodeKey)?.Transform.Stage
                };
            }
        }

        /// <summary>Stops the shared node.</summary>
        public void Stop()
        {
            lock (_gate)
            {
                if (_completed)
                {
                    return;
                }
                _completed = true;
                CancelWarmExpiration();
                foreach (var subscriber in _subscribers.Values)
                {
                    _buffers.Complete(subscriber.Subscription, discard: true);
                }
                _subscribers.Clear();
                _lifetime.Cancel();
            }
        }

        private async Task<bool> RetireIdleAsync(CancellationToken cancellationToken)
        {
            Task completion;
            lock (_gate)
            {
                if (!_completed && (_warmCancellation is null || _subscribers.Count != 0))
                {
                    return false;
                }
                Stop();
                completion = Completion;
            }
            await completion.WaitAsync(cancellationToken);
            return true;
        }

        private async Task RunAsync()
        {
            using var operation = Lineup.Core.Extensibility.Diagnostics.LineupTelemetry.Begin("producer", _traceParent, _lifetimeToken);
            Lineup.Core.Extensibility.Diagnostics.LineupTelemetry.Producers.Add(1);
            Exception? failure = null;
            try
            {
                await using var reservation = await _mediaWork.AcquireAsync(MediaWorkKind.Producer, RetireIdleAsync, _lifetime.Token);
                await using var source = await _sourceFactory(_lifetime.Token);
                _started.TrySetResult();
                while (!_lifetime.IsCancellationRequested)
                {
                    var buffer = new byte[BufferSize];
                    var count = await source.Stream.ReadAsync(buffer, _lifetime.Token);
                    if (count == 0)
                    {
                        if (!_lifetime.IsCancellationRequested)
                        {
                            Termination = new(_key.NodeId, _kind, "source_eof", _timeProvider.GetUtcNow(), System.Diagnostics.Activity.Current?.TraceId.ToString());
                            _logger.LogWarning("Shared {StreamKind} producer {ProducerNodeId} ended: source_eof. Clients must reconnect; no source is spliced into this stream.", _kind, _key.NodeId);
                        }
                        break;
                    }
                    if (count != buffer.Length)
                    {
                        Array.Resize(ref buffer, count);
                    }
                    Broadcast(buffer);
                }
            }
            catch (Exception ex) when (_lifetime.IsCancellationRequested && ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
                operation.Cancel();
                _started.TrySetCanceled(_lifetime.Token);
            }
            catch (Exception ex)
            {
                failure = ex;
                Termination = new(_key.NodeId, _kind, "source_failure", _timeProvider.GetUtcNow(), System.Diagnostics.Activity.Current?.TraceId.ToString());
                _started.TrySetException(ex);
                _logger.LogWarning(ex, "Shared stream {StreamKind} node {StreamKey} stopped unexpectedly", _kind, _key);
            }
            finally
            {
                if (Termination is not null)
                {
                    Lineup.Core.Extensibility.Diagnostics.LineupTelemetry.SourceLosses.Add(1);
                }
                Lineup.Core.Extensibility.Diagnostics.LineupTelemetry.Producers.Add(-1);
                CompleteSubscribers(failure);
                _onCompleted(this);
                _lifetime.Dispose();
            }
        }

        private void Broadcast(byte[] buffer)
        {
            lock (_gate)
            {
                _buffers.Publish(buffer, _subscribers.Values.Select(subscriber => subscriber.Subscription));
            }
        }

        private async Task RemoveEvictedSubscriberAsync(Guid id)
        {
            _logger.LogWarning("Evicting a lagging consumer from shared {StreamKind} node {StreamKey}.", _kind, _key);
            try
            {
                await RemoveSubscriberAsync(id);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Could not finish cleanup of an evicted shared-stream consumer.");
            }
        }

        private async ValueTask RemoveSubscriberAsync(Guid id)
        {
            Task? completion = null;
            lock (_gate)
            {
                var keepWarmPeriod = TimeSpan.Zero;
                if (_subscribers.Remove(id, out var subscriber))
                {
                    keepWarmPeriod = subscriber.KeepWarmPeriod;
                    _buffers.Complete(subscriber.Subscription, discard: true);
                }
                completion = BeginIdle(keepWarmPeriod);
            }
            if (completion is not null)
            {
                await completion;
            }
        }

        private void CompleteSubscribers(Exception? failure)
        {
            lock (_gate)
            {
                _completed = true;
                CancelWarmExpiration();
                foreach (var subscriber in _subscribers.Values)
                {
                    _buffers.Complete(subscriber.Subscription, failure);
                }
                _subscribers.Clear();
            }
        }

        private Task? BeginIdle(TimeSpan keepWarmPeriod)
        {
            if (_subscribers.Count != 0 || _completed || _warmCancellation is not null)
            {
                return null;
            }
            if (keepWarmPeriod <= TimeSpan.Zero)
            {
                _completed = true;
                _lifetime.Cancel();
                return _runTask;
            }

            var expiration = new CancellationTokenSource();
            _warmCancellation = expiration;
            Lineup.Core.Extensibility.Diagnostics.LineupTelemetry.WarmProducers.Add(1);
            _ = ExpireWarmAsync(expiration, keepWarmPeriod);
            return null;
        }

        private async Task ExpireWarmAsync(CancellationTokenSource expiration, TimeSpan keepWarmPeriod)
        {
            try
            {
                await Task.Delay(keepWarmPeriod, _timeProvider, expiration.Token);
                lock (_gate)
                {
                    if (!ReferenceEquals(_warmCancellation, expiration) || _subscribers.Count != 0 || _completed)
                    {
                        return;
                    }
                    _warmCancellation = null;
                    Lineup.Core.Extensibility.Diagnostics.LineupTelemetry.WarmProducers.Add(-1);
                    _completed = true;
                    _lifetime.Cancel();
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

        private void CancelWarmExpiration()
        {
            var expiration = _warmCancellation;
            _warmCancellation = null;
            if (expiration is not null)
            {
                Lineup.Core.Extensibility.Diagnostics.LineupTelemetry.WarmProducers.Add(-1);
            }
            expiration?.Cancel();
        }

        private sealed record Subscriber(StreamBufferBudget.Subscription Subscription, TimeSpan KeepWarmPeriod);
    }

    private sealed class SessionLease : IStreamSessionLease
    {
        private readonly SubscriptionStream _stream;

        /// <summary>Initializes the session lease.</summary>
        /// <param name="producerNodeId">The producer node ID value.</param>
        /// <param name="reader">The reader value.</param>
        /// <param name="unsubscribe">The unsubscribe value.</param>
        public SessionLease(string producerNodeId, StreamBufferBudget.Subscription reader, Func<ValueTask> unsubscribe)
        {
            SessionId = StreamSessionId.New();
            ProducerNodeId = producerNodeId;
            _stream = new SubscriptionStream(reader, unsubscribe);
        }

        /// <summary>Gets the session ID.</summary>
        public StreamSessionId SessionId { get; }

        /// <summary>Gets the producer node ID.</summary>
        public string ProducerNodeId { get; }

        /// <summary>Gets the stream.</summary>
        public Stream Stream => _stream;

        /// <summary>Gets the content type.</summary>
        public string? ContentType => null;

        /// <inheritdoc />
        public ValueTask DisposeAsync() => _stream.DisposeAsync();
    }

    private sealed class CompositeSourceLease : IStreamSourceLease
    {
        private IAsyncDisposable? _sourceOwner;
        private IAsyncDisposable? _parentOwner;

        /// <summary>Initializes the composite source lease.</summary>
        /// <param name="source">The source value.</param>
        /// <param name="parentOwner">The parent owner value.</param>
        public CompositeSourceLease(IStreamSourceLease source, IAsyncDisposable parentOwner)
        {
            Stream = source.Stream;
            ContentType = source.ContentType;
            _sourceOwner = source;
            _parentOwner = parentOwner;
        }

        /// <summary>Gets the stream.</summary>
        public Stream Stream { get; }

        /// <summary>Gets the content type.</summary>
        public string? ContentType { get; }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            var source = Interlocked.Exchange(ref _sourceOwner, null);
            var parent = Interlocked.Exchange(ref _parentOwner, null);
            Exception? sourceFailure = null;
            if (source is not null)
            {
                try
                {
                    await source.DisposeAsync();
                }
                catch (Exception exception)
                {
                    sourceFailure = exception;
                }
            }
            Exception? parentFailure = null;
            if (parent is not null)
            {
                try
                {
                    await parent.DisposeAsync();
                }
                catch (Exception exception)
                {
                    parentFailure = exception;
                }
            }

            if (sourceFailure is not null && parentFailure is not null)
            {
                throw new AggregateException(sourceFailure, parentFailure);
            }
            if (sourceFailure is not null)
            {
                ExceptionDispatchInfo.Capture(sourceFailure).Throw();
            }
            if (parentFailure is not null)
            {
                ExceptionDispatchInfo.Capture(parentFailure).Throw();
            }
        }
    }

    private sealed class SubscriptionStream : Stream
    {
        private readonly StreamBufferBudget.Subscription _reader;
        private Func<ValueTask>? _unsubscribe;

        /// <summary>Initializes the subscription stream.</summary>
        /// <param name="reader">The reader value.</param>
        /// <param name="unsubscribe">The unsubscribe value.</param>
        public SubscriptionStream(StreamBufferBudget.Subscription reader, Func<ValueTask> unsubscribe)
        {
            _reader = reader;
            _unsubscribe = unsubscribe;
        }

        /// <inheritdoc />
        public override bool CanRead => _unsubscribe is not null;
        /// <inheritdoc />
        public override bool CanSeek => false;
        /// <inheritdoc />
        public override bool CanWrite => false;
        /// <inheritdoc />
        public override long Length => throw new NotSupportedException();
        /// <inheritdoc />
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        /// <inheritdoc />
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_unsubscribe is null, this);
            return await _reader.ReadAsync(buffer, cancellationToken);
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                var unsubscribe = Interlocked.Exchange(ref _unsubscribe, null);
                _reader.Close();
                unsubscribe?.Invoke().AsTask().GetAwaiter().GetResult();
            }
            base.Dispose(disposing);
        }

        /// <inheritdoc />
        public override async ValueTask DisposeAsync()
        {
            var unsubscribe = Interlocked.Exchange(ref _unsubscribe, null);
            _reader.Close();
            if (unsubscribe is not null)
            {
                await unsubscribe();
            }
            GC.SuppressFinalize(this);
        }

        /// <inheritdoc />
        public override void Flush() => throw new NotSupportedException();
        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException();
        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>
/// Indicates that a physical source cannot start because all capacity is occupied.
/// </summary>
public sealed class StreamCapacityExceededException : Exception
{
    /// <summary>Initializes the capacity exception.</summary>
    public StreamCapacityExceededException(PhysicalDeviceId deviceId, PhysicalTunerCapacity capacity)
        : base($"Physical device {deviceId} has no available tuner capacity ({capacity.TunerCount?.ToString() ?? "unbounded"} configured).")
    {
        DeviceId = deviceId;
    }

    /// <summary>Gets the exhausted physical device.</summary>
    public PhysicalDeviceId DeviceId { get; }
}
