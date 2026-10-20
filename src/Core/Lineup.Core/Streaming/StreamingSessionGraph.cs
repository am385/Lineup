using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
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
public sealed record StreamAcquisitionDescriptor(
    PhysicalDeviceId DeviceId,
    PhysicalAcquisitionKey AcquisitionKey,
    PhysicalTunerCapacity Capacity,
    bool EnforceCapacity = true);

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
public sealed record StreamNodeDiagnostics(
    string Kind,
    string Key,
    int ReceiverCount,
    bool IsRunning,
    bool IsWarm = false,
    long TotalSubscriptions = 0)
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
public sealed record StreamingSessionGraphDiagnostics(
    IReadOnlyList<StreamNodeDiagnostics> Nodes,
    IReadOnlyList<PhysicalCapacityDiagnostics> Capacity);

/// <summary>
/// Shares physical acquisitions and identical transforms across consumer sessions.
/// </summary>
public interface IStreamingSessionGraph
{
    /// <summary>Acquires a subscription to a shared physical source.</summary>
    ValueTask<IStreamSessionLease> AcquireAsync(
        StreamAcquisitionDescriptor descriptor,
        StreamAcquisitionFactory acquisitionFactory,
        CancellationToken cancellationToken = default);

    /// <summary>Acquires a subscription to a shared transform over a physical source.</summary>
    ValueTask<IStreamSessionLease> AcquireTransformAsync(
        StreamAcquisitionDescriptor descriptor,
        StreamTransformKey transformKey,
        StreamAcquisitionFactory acquisitionFactory,
        IStreamTransform transform,
        CancellationToken cancellationToken = default);

    /// <summary>Acquires an ordered reusable transform pipeline over a physical source.</summary>
    ValueTask<IStreamSessionLease> AcquirePipelineAsync(
        StreamAcquisitionDescriptor descriptor,
        StreamAcquisitionFactory acquisitionFactory,
        IReadOnlyList<StreamTransformStage> stages,
        TimeSpan keepWarmPeriod,
        CancellationToken cancellationToken = default);

    /// <summary>Gets or creates metadata scoped to the lifetime of one shared physical acquisition.</summary>
    Task<T> GetOrCreateAcquisitionMetadataAsync<T>(
        StreamAcquisitionDescriptor descriptor,
        StreamAcquisitionFactory acquisitionFactory,
        string metadataKey,
        Func<Stream, CancellationToken, Task<T>> metadataFactory,
        CancellationToken cancellationToken = default);

    /// <summary>Gets complete metadata or performs one shared delayed refresh when the cached result remains provisional.</summary>
    Task<T> GetOrRefreshAcquisitionMetadataAsync<T>(
        StreamAcquisitionDescriptor descriptor,
        StreamAcquisitionFactory acquisitionFactory,
        string metadataKey,
        Func<Stream, CancellationToken, Task<T>> metadataFactory,
        AcquisitionMetadataRefreshPolicy<T> refreshPolicy,
        CancellationToken cancellationToken = default);

    /// <summary>Gets immutable active graph and capacity diagnostics.</summary>
    StreamingSessionGraphDiagnostics GetDiagnostics();

    /// <summary>Rejects new sessions and stops transforms before acquisitions.</summary>
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
    private const int SubscriberBufferCount = 2048;
    private readonly IPhysicalCapacityLeaseRegistry _capacity;
    private readonly ILogger<StreamingSessionGraph> _logger;
    private readonly ConcurrentDictionary<AcquisitionNodeKey, SharedNode> _acquisitions = [];
    private readonly ConcurrentDictionary<TransformNodeKey, SharedNode> _transforms = [];
    private readonly ConcurrentDictionary<AcquisitionMetadataKey, Lazy<Task<AcquisitionMetadataSnapshot>>> _metadata = [];
    private readonly Lock _lifecycleGate = new();
    private bool _isStopping;

    /// <summary>Initializes the shared graph.</summary>
    public StreamingSessionGraph(IPhysicalCapacityLeaseRegistry capacity, ILogger<StreamingSessionGraph> logger)
    {
        _capacity = capacity;
        _logger = logger;
    }

    /// <inheritdoc />
    public ValueTask<IStreamSessionLease> AcquireAsync(
        StreamAcquisitionDescriptor descriptor,
        StreamAcquisitionFactory acquisitionFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(acquisitionFactory);
        ValidateDescriptor(descriptor);
        return AcquirePipelineAsync(descriptor, acquisitionFactory, [], TimeSpan.Zero, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<IStreamSessionLease> AcquireTransformAsync(
        StreamAcquisitionDescriptor descriptor,
        StreamTransformKey transformKey,
        StreamAcquisitionFactory acquisitionFactory,
        IStreamTransform transform,
        CancellationToken cancellationToken = default)
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

    /// <inheritdoc />
    public async ValueTask<IStreamSessionLease> AcquirePipelineAsync(
        StreamAcquisitionDescriptor descriptor,
        StreamAcquisitionFactory acquisitionFactory,
        IReadOnlyList<StreamTransformStage> stages,
        TimeSpan keepWarmPeriod,
        CancellationToken cancellationToken = default)
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

        cancellationToken.ThrowIfCancellationRequested();
        while (true)
        {
            SharedNode node;
            IStreamSessionLease createdLease;
            lock (_lifecycleGate)
            {
                ThrowIfStopping();
                GraphNodeKey key = AcquisitionNodeKey.Create(descriptor);
                node = GetOrCreateAcquisitionNode(descriptor, acquisitionFactory);
                foreach (var stage in stages)
                {
                    var transformKey = new TransformNodeKey(key, stage.Key);
                    node = GetOrCreateTransformNode(node, transformKey, stage.Transform);
                    key = transformKey;
                }
                if (!node.TrySubscribe(keepWarmPeriod, out createdLease))
                {
                    RemoveNode(key, node);
                    continue;
                }
            }
            try
            {
                await node.Started.WaitAsync(cancellationToken);
                return createdLease;
            }
            catch
            {
                await createdLease.DisposeAsync();
                throw;
            }
        }
    }

    /// <inheritdoc />
    public async Task<T> GetOrCreateAcquisitionMetadataAsync<T>(
        StreamAcquisitionDescriptor descriptor,
        StreamAcquisitionFactory acquisitionFactory,
        string metadataKey,
        Func<Stream, CancellationToken, Task<T>> metadataFactory,
        CancellationToken cancellationToken = default)
        => await GetAcquisitionMetadataAsync(
            descriptor,
            acquisitionFactory,
            metadataKey,
            metadataFactory,
            refreshPolicy: null,
            cancellationToken);

    /// <inheritdoc />
    public async Task<T> GetOrRefreshAcquisitionMetadataAsync<T>(
        StreamAcquisitionDescriptor descriptor,
        StreamAcquisitionFactory acquisitionFactory,
        string metadataKey,
        Func<Stream, CancellationToken, Task<T>> metadataFactory,
        AcquisitionMetadataRefreshPolicy<T> refreshPolicy,
        CancellationToken cancellationToken = default)
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
        CancellationToken cancellationToken)
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
        lock (_lifecycleGate)
        {
            ThrowIfStopping();
            acquisitionKey = AcquisitionNodeKey.Create(descriptor);
            node = GetOrCreateAcquisitionNode(descriptor, acquisitionFactory);
        }

        var key = new AcquisitionMetadataKey(acquisitionKey, metadataKey);
        var lazy = _metadata.GetOrAdd(
            key,
            _ => CreateMetadataFactory(node, metadataFactory, refreshPolicy?.OperationTimeout ?? Timeout.InfiniteTimeSpan));
        var snapshot = await AwaitMetadataAsync<T>(key, lazy, cancellationToken);
        if (refreshPolicy is null || refreshPolicy.IsReusable(snapshot.Value))
        {
            return snapshot.Value;
        }
        if (snapshot.RefreshAttempted)
        {
            _metadata.TryRemove(new KeyValuePair<AcquisitionMetadataKey, Lazy<Task<AcquisitionMetadataSnapshot>>>(key, lazy));
            return snapshot.Value;
        }

        var remainingDelay = refreshPolicy.RetryDelay - (DateTimeOffset.UtcNow - snapshot.ObservedAtUtc);
        if (remainingDelay > TimeSpan.Zero)
        {
            await Task.Delay(remainingDelay, cancellationToken);
        }

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

    private static Lazy<Task<AcquisitionMetadataSnapshot>> CreateMetadataFactory<T>(
        SharedNode node,
        Func<Stream, CancellationToken, Task<T>> metadataFactory,
        TimeSpan operationTimeout,
        bool refreshAttempted = false) =>
        new(
            async () =>
            {
                using var timeout = operationTimeout == Timeout.InfiniteTimeSpan
                    ? null
                    : new CancellationTokenSource(operationTimeout);
                var operationToken = timeout?.Token ?? CancellationToken.None;
                try
                {
                    await using var lease = await SubscribeNodeAsync(node, operationToken);
                    var value = (object?)await metadataFactory(lease.Stream, operationToken)
                        ?? throw new InvalidOperationException("Shared acquisition metadata cannot be null.");
                    return new AcquisitionMetadataSnapshot(value, DateTimeOffset.UtcNow, refreshAttempted);
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
        bool evictOnTimeout = true)
    {
        try
        {
            var snapshot = waitTimeout == default || waitTimeout == Timeout.InfiniteTimeSpan
                ? await lazy.Value.WaitAsync(cancellationToken)
                : await lazy.Value.WaitAsync(waitTimeout, cancellationToken);
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

    /// <inheritdoc />
    public StreamingSessionGraphDiagnostics GetDiagnostics()
    {
        var nodes = _acquisitions.Values
            .Concat(_transforms.Values)
            .Select(node => node.GetDiagnostics())
            .OrderBy(node => node.Kind, StringComparer.Ordinal)
            .ThenBy(node => node.Key, StringComparer.Ordinal)
            .ToArray();
        return new StreamingSessionGraphDiagnostics(nodes, _capacity.GetDiagnostics());
    }

    /// <inheritdoc />
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

        foreach (var node in transforms)
        {
            node.Stop();
        }
        await Task.WhenAll(transforms.Select(node => node.Completion)).WaitAsync(cancellationToken);

        foreach (var node in acquisitions)
        {
            node.Stop();
        }
        await Task.WhenAll(acquisitions.Select(node => node.Completion)).WaitAsync(cancellationToken);
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
                    var capacityLease = await _capacity.TryAcquireAsync(descriptor.DeviceId, descriptor.AcquisitionKey, descriptor.Capacity, lifetime);
                    if (capacityLease is null)
                    {
                        throw new StreamCapacityExceededException(descriptor.DeviceId, descriptor.Capacity);
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
                completed => RemoveAcquisitionNode(key, completed)));
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
                completed => _transforms.TryRemove(new KeyValuePair<TransformNodeKey, SharedNode>(key, completed))));
    }

    private static async ValueTask<IStreamSessionLease> SubscribeNodeAsync(SharedNode node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!node.TrySubscribe(TimeSpan.Zero, out var lease))
        {
            throw new IOException("The shared parent stream stopped before the transform could subscribe.");
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

    private void RemoveNode(GraphNodeKey key, SharedNode node)
    {
        if (key is AcquisitionNodeKey acquisition)
        {
            _acquisitions.TryRemove(new KeyValuePair<AcquisitionNodeKey, SharedNode>(acquisition, node));
            return;
        }
        _transforms.TryRemove(new KeyValuePair<TransformNodeKey, SharedNode>((TransformNodeKey)key, node));
    }

    private void RemoveAcquisitionNode(AcquisitionNodeKey key, SharedNode node)
    {
        _acquisitions.TryRemove(new KeyValuePair<AcquisitionNodeKey, SharedNode>(key, node));
        foreach (var metadataKey in _metadata.Keys.Where(candidate => candidate.Acquisition == key))
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
        public string NodeId
        {
            get
            {
                var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(ToString()));
                return Convert.ToHexString(bytes.AsSpan(0, 12)).ToLowerInvariant();
            }
        }
    }

    private sealed record AcquisitionNodeKey(PhysicalDeviceId DeviceId, PhysicalAcquisitionKey AcquisitionKey) : GraphNodeKey
    {
        public static AcquisitionNodeKey Create(StreamAcquisitionDescriptor descriptor) =>
            new(descriptor.DeviceId, descriptor.AcquisitionKey);

        public override string ToString() => $"{DeviceId}:{AcquisitionKey.ChannelId.ProviderKey}:{AcquisitionKey.TuneKey}";
    }

    private sealed record TransformNodeKey(GraphNodeKey Parent, StreamTransformKey Transform) : GraphNodeKey
    {
        public override string ToString() => $"{Parent}>{Transform.Stage}:{Transform.SettingsKey}";
    }

    private sealed record AcquisitionMetadataKey(AcquisitionNodeKey Acquisition, string MetadataKey);

    private sealed record AcquisitionMetadataSnapshot(object Value, DateTimeOffset ObservedAtUtc, bool RefreshAttempted);

    private sealed record AcquisitionMetadataValue<T>(T Value, DateTimeOffset ObservedAtUtc, bool RefreshAttempted);

    private sealed class SharedNode
    {
        private readonly string _kind;
        private readonly GraphNodeKey _key;
        private readonly Func<CancellationToken, ValueTask<IStreamSourceLease>> _sourceFactory;
        private readonly ILogger _logger;
        private readonly Action<SharedNode> _onCompleted;
        private readonly object _gate = new();
        private readonly Dictionary<Guid, Subscriber> _subscribers = [];
        private readonly CancellationTokenSource _lifetime = new();
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task? _runTask;
        private CancellationTokenSource? _warmCancellation;
        private long _totalSubscriptions;
        private bool _completed;

        public SharedNode(
            string kind,
            GraphNodeKey key,
            Func<CancellationToken, ValueTask<IStreamSourceLease>> sourceFactory,
            ILogger logger,
            Action<SharedNode> onCompleted)
        {
            _kind = kind;
            _key = key;
            _sourceFactory = sourceFactory;
            _logger = logger;
            _onCompleted = onCompleted;
        }

        public Task Completion => _runTask ?? Task.CompletedTask;

        public Task Started => _started.Task;

        public bool TrySubscribe(TimeSpan keepWarmPeriod, out IStreamSessionLease lease)
        {
            var id = Guid.NewGuid();
            var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(SubscriberBufferCount)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true
            });
            lock (_gate)
            {
                if (_completed)
                {
                    lease = null!;
                    return false;
                }
                CancelWarmExpiration();
                _subscribers.Add(id, new Subscriber(channel, keepWarmPeriod));
                _totalSubscriptions++;
                _runTask ??= RunAsync();
            }
            lease = new SessionLease(_key.NodeId, channel.Reader, () => RemoveSubscriberAsync(id));
            return true;
        }

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
                    subscriber.Channel.Writer.TryComplete();
                }
                _subscribers.Clear();
                _lifetime.Cancel();
            }
        }

        private async Task RunAsync()
        {
            Exception? failure = null;
            try
            {
                await using var source = await _sourceFactory(_lifetime.Token);
                _started.TrySetResult();
                while (!_lifetime.IsCancellationRequested)
                {
                    var buffer = new byte[BufferSize];
                    var count = await source.Stream.ReadAsync(buffer, _lifetime.Token);
                    if (count == 0)
                    {
                        break;
                    }
                    if (count != buffer.Length)
                    {
                        Array.Resize(ref buffer, count);
                    }
                    Broadcast(buffer);
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                _started.TrySetCanceled(_lifetime.Token);
            }
            catch (Exception ex)
            {
                failure = ex;
                _started.TrySetException(ex);
                _logger.LogWarning(ex, "Shared stream {StreamKind} node {StreamKey} stopped unexpectedly", _kind, _key);
            }
            finally
            {
                CompleteSubscribers(failure);
                _onCompleted(this);
                _lifetime.Dispose();
            }
        }

        private void Broadcast(byte[] buffer)
        {
            List<Guid>? slowSubscribers = null;
            lock (_gate)
            {
                foreach (var subscriber in _subscribers)
                {
                    if (!subscriber.Value.Channel.Writer.TryWrite(buffer))
                    {
                        (slowSubscribers ??= []).Add(subscriber.Key);
                    }
                }
                var keepWarmPeriod = TimeSpan.Zero;
                foreach (var id in slowSubscribers ?? [])
                {
                    if (_subscribers.Remove(id, out var subscriber))
                    {
                        keepWarmPeriod = keepWarmPeriod > subscriber.KeepWarmPeriod ? keepWarmPeriod : subscriber.KeepWarmPeriod;
                        subscriber.Channel.Writer.TryComplete(new IOException("The consumer could not keep up with the shared stream."));
                    }
                }
                BeginIdle(keepWarmPeriod);
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
                    subscriber.Channel.Writer.TryComplete();
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
                    subscriber.Channel.Writer.TryComplete(failure);
                }
                _subscribers.Clear();
            }
        }

        private Task? BeginIdle(TimeSpan keepWarmPeriod)
        {
            if (_subscribers.Count != 0 || _completed)
            {
                return null;
            }
            if (keepWarmPeriod <= TimeSpan.Zero)
            {
                _completed = true;
                _lifetime.Cancel();
                return _runTask;
            }

            CancelWarmExpiration();
            var expiration = new CancellationTokenSource();
            _warmCancellation = expiration;
            _ = ExpireWarmAsync(expiration, keepWarmPeriod);
            return null;
        }

        private async Task ExpireWarmAsync(CancellationTokenSource expiration, TimeSpan keepWarmPeriod)
        {
            try
            {
                await Task.Delay(keepWarmPeriod, expiration.Token);
                lock (_gate)
                {
                    if (!ReferenceEquals(_warmCancellation, expiration) || _subscribers.Count != 0 || _completed)
                    {
                        return;
                    }
                    _warmCancellation = null;
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
            expiration?.Cancel();
        }

        private sealed record Subscriber(Channel<byte[]> Channel, TimeSpan KeepWarmPeriod);
    }

    private sealed class SessionLease : IStreamSessionLease
    {
        private readonly SubscriptionStream _stream;

        public SessionLease(string producerNodeId, ChannelReader<byte[]> reader, Func<ValueTask> unsubscribe)
        {
            SessionId = StreamSessionId.New();
            ProducerNodeId = producerNodeId;
            _stream = new SubscriptionStream(reader, unsubscribe);
        }

        public StreamSessionId SessionId { get; }

        public string ProducerNodeId { get; }

        public Stream Stream => _stream;

        public string? ContentType => null;

        public ValueTask DisposeAsync() => _stream.DisposeAsync();
    }

    private sealed class CompositeSourceLease : IStreamSourceLease
    {
        private IAsyncDisposable? _sourceOwner;
        private IAsyncDisposable? _parentOwner;

        public CompositeSourceLease(IStreamSourceLease source, IAsyncDisposable parentOwner)
        {
            Stream = source.Stream;
            ContentType = source.ContentType;
            _sourceOwner = source;
            _parentOwner = parentOwner;
        }

        public Stream Stream { get; }

        public string? ContentType { get; }

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
        private readonly ChannelReader<byte[]> _reader;
        private Func<ValueTask>? _unsubscribe;
        private byte[]? _current;
        private int _offset;

        public SubscriptionStream(ChannelReader<byte[]> reader, Func<ValueTask> unsubscribe)
        {
            _reader = reader;
            _unsubscribe = unsubscribe;
        }

        public override bool CanRead => _unsubscribe is not null;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_unsubscribe is null, this);
            while (_current is null || _offset == _current.Length)
            {
                if (!await _reader.WaitToReadAsync(cancellationToken))
                {
                    return 0;
                }
                if (!_reader.TryRead(out _current))
                {
                    continue;
                }
                _offset = 0;
            }
            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                var unsubscribe = Interlocked.Exchange(ref _unsubscribe, null);
                unsubscribe?.Invoke().AsTask().GetAwaiter().GetResult();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            var unsubscribe = Interlocked.Exchange(ref _unsubscribe, null);
            if (unsubscribe is not null)
            {
                await unsubscribe();
            }
            GC.SuppressFinalize(this);
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
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
