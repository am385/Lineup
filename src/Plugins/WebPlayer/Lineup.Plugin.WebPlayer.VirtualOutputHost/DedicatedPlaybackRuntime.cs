using System.Collections.Concurrent;
using System.Diagnostics;
using Lineup.Core.Common.Streaming;
using Lineup.Core.Streaming.FFmpeg;
using Lineup.Core.VirtualOutputs.Hosting;
using Lineup.Core.VirtualOutputs.Protocol;
using Lineup.Plugin.WebPlayer;
using Lineup.Plugin.WebPlayer.FFmpeg;

namespace Lineup.Plugin.WebPlayer.VirtualOutputHost;

/// <summary>Owns dedicated producers independently of HTTP requests and tracks separately admitted viewer aliases.</summary>
internal sealed partial class DedicatedPlaybackRuntime : IAsyncDisposable
{
    private readonly VirtualOutputWorkerSession _worker;
    private readonly ILogger _logger;
    private readonly ICmafOutputCoordinator _outputs;
    private readonly IMediaWorkAdmission _mediaWork;
    private readonly ConcurrentDictionary<StreamSessionId, Viewer> _viewers = new();
    private readonly ConcurrentDictionary<StreamSessionId, Viewer> _releasing = new();
    private readonly ConcurrentDictionary<Guid, Task> _background = new();

    /// <summary>Initializes worker-local producer coordination and viewer expiration.</summary>
    internal DedicatedPlaybackRuntime(VirtualOutputWorkerSession worker, ILogger logger, ICmafOutputCoordinator? outputs = null)
    {
        _worker = worker;
        _logger = logger;
        _mediaWork = new BrokerMediaWorkAdmission(worker.Broker, worker.Context ?? throw new InvalidOperationException("The worker must be registered before playback."),
            () => Track(StopForAdmissionLossAsync()), logger);
        _outputs = outputs ?? new CmafOutputCoordinator(TimeProvider.System, CmafStreamPlanner.MinimumInactivityTimeout, _mediaWork);
    }

    /// <summary>Gets active viewer identifiers without extending their inactivity lifetimes.</summary>
    internal string[] ViewerIds => _viewers.Keys.Select(id => id.ToString()).Order(StringComparer.Ordinal).ToArray();

    /// <summary>Gets the independently owned producer inventory, including idle warm producers.</summary>
    internal IReadOnlyList<CmafOutputDiagnostics> Producers => _outputs.GetDiagnostics();

    /// <summary>Reads passive liveness without extending the viewer lease.</summary>
    internal CmafPlaybackStatus GetPlaybackStatus(string id) =>
        Guid.TryParseExact(id, "N", out var value) ? _outputs.GetPlaybackStatus(new StreamSessionId(value)) : new(false, "session_unavailable", false);

    /// <summary>Admits a viewer, selects an exact source and plan, and joins or starts one reusable producer.</summary>
    internal async Task<PlaybackView> StartAsync(string channel, CmafStreamRequest request, VirtualOutputClientContext client, CancellationToken cancellationToken)
    {
        var stage = DedicatedWorkerStartupStage.Broker;
        var id = StreamSessionId.New();
        var directory = Path.Combine(Path.GetTempPath(), "lineup-web-player", id.ToString());
        VirtualOutputClientStream? source = null;
        Viewer? viewer = null;
        try
        {
            source = await _worker.Broker.OpenStreamAsync(new VirtualOutputOpenStreamRequest
            {
                Session = _worker.Context!,
                RequestId = VirtualOutputStreamRequestId.New(),
                ChannelKey = channel,
                Stage = VirtualOutputStreamStage.PhysicalOutput,
                RetainSourceForProducer = true,
                Client = client
            }, cancellationToken);
            stage = DedicatedWorkerStartupStage.Planning;
            var plan = DedicatedCmafSessionPlan.Create(source.Response, request, directory);
            var exactSource = string.IsNullOrWhiteSpace(source.Response.ProducerNodeId) ? source.RequestId.ToString() : source.Response.ProducerNodeId;
            var key = new StreamTransformKey("web-player-cmaf", $"{exactSource}|{channel}|{plan.SharingKey}");
            var producerSource = source;
            var lease = await _outputs.AcquireAsync(key, id, _ =>
            {
                _mediaWork.CheckWorkspace(directory);
                Directory.CreateDirectory(directory);
                var producer = new Producer(this, producerSource, plan, directory);
                return ValueTask.FromResult(new CmafOutputRegistration(producer, producer, ReleaseViewer));
            }, TimeSpan.FromSeconds(source.Response.KeepWarmPeriodSeconds), cancellationToken);
            var shared = (Producer)lease.State;
            shared.Id ??= lease.OutputId.ToString();
            viewer = new Viewer(this, id, channel, source, shared, lease);
            if (!_viewers.TryAdd(id, viewer))
            {
                await lease.DisposeAsync();
                throw new InvalidOperationException("The viewer identity is already active.");
            }
            if (ReferenceEquals(shared.Source, source))
            {
                shared.Start();
            }
            else
            {
                await MediaSessionStartupGuard.DeleteDirectoryAsync(directory);
                viewer.StartDrain();
            }
            stage = DedicatedWorkerStartupStage.Readiness;
            Track(MonitorViewerAsync(viewer));
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, viewer.Token);
            var session = await shared.Ready.WaitAsync(waiting.Token);
            if (await ReportAsync(viewer, session.Plan, VirtualOutputStreamStartupStage.Ready, release: false, waiting.Token))
            {
                throw new OperationCanceledException("The viewer was stopped during startup.");
            }
            return new PlaybackView(id.ToString(), session.Directory, session.Plan);
        }
        catch (Exception exception)
        {
            if (viewer is not null)
            {
                await _outputs.ReleaseViewerAsync(id);
                await viewer.CloseAsync();
            }
            else if (source is not null)
            {
                await source.DisposeAsync();
                await MediaSessionStartupGuard.DeleteDirectoryAsync(directory);
            }
            if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            if (exception is PlaybackStartupException)
            {
                throw;
            }
            MediaProcessDiagnostics.LogFailure(_logger, exception, stage.ToString(), id.ToString(), source?.RequestId.ToString());
            throw new PlaybackStartupException(stage, exception);
        }
    }

    private async Task StopForAdmissionLossAsync()
    {
        await _outputs.StopAsync();
    }

    /// <summary>Resolves and touches only an active viewer; a warm producer is not a public media alias.</summary>
    internal bool TryGet(string id, out PlaybackView? view)
    {
        view = null;
        if (!Guid.TryParseExact(id, "N", out var value) ||
            !_outputs.TryGetViewer(new StreamSessionId(value), out var snapshot) ||
            snapshot?.State is not Producer { Session: { } session })
        {
            return false;
        }
        view = new PlaybackView(id, session.Directory, session.Plan);
        return true;
    }

    /// <summary>Stops one alias and releases its admission without stopping another viewer's producer.</summary>
    internal async Task<bool> StopAsync(string id)
    {
        if (!Guid.TryParseExact(id, "N", out var value))
        {
            return false;
        }
        var key = new StreamSessionId(value);
        if (!_viewers.TryGetValue(key, out var viewer))
        {
            _releasing.TryGetValue(key, out viewer);
        }
        var removed = await _outputs.ReleaseViewerAsync(key);
        if (viewer is not null)
        {
            await viewer.CloseAsync();
        }
        return removed || viewer is not null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            await _outputs.StopAsync();
        }
        finally
        {
            await Task.WhenAll(_background.Values.ToArray());
        }
    }

    private void ReleaseViewer(StreamSessionId id)
    {
        if (_viewers.TryRemove(id, out var viewer))
        {
            _releasing[id] = viewer;
            var closing = viewer.CloseAsync();
            viewer.Producer.AddCleanup(closing);
            Track(closing);
        }
    }

    private async Task<bool> ReportAsync(Viewer viewer, DedicatedCmafSessionPlan plan, VirtualOutputStreamStartupStage stage, bool release, CancellationToken cancellationToken)
    {
        var streamId = viewer.Source.Response.StreamSessionId ?? throw new InvalidDataException("The broker did not return a stream identity.");
        var snapshot = plan.CreateActiveStreamSnapshot(streamId.ToString(), viewer.Channel, viewer.StartedAtUtc);
        var result = await _worker.Broker.ReportStreamStatusAsync(new VirtualOutputStreamStatusReport
        {
            Session = _worker.Context!,
            RequestId = viewer.Source.RequestId,
            StreamSessionId = streamId,
            WorkerMediaSessionId = viewer.Id.ToString(),
            WorkerProducerId = viewer.Producer.Id,
            ReleaseViewer = release,
            Format = snapshot.Format,
            Tracks = snapshot.Tracks,
            PhysicalInput = snapshot.PhysicalInput,
            PhysicalOutput = snapshot.PhysicalOutput,
            VirtualOutput = snapshot.VirtualOutput,
            StartupStage = stage
        }, cancellationToken);
        if (!result.Updated)
        {
            throw new InvalidOperationException("The broker no longer recognizes this playback viewer.");
        }
        return result.StopRequested;
    }

    private async Task MonitorViewerAsync(Viewer viewer)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), viewer.Token);
                if (viewer.Drain is { IsCompleted: true })
                {
                    try
                    {
                        await viewer.Drain;
                    }
                    catch (IOException) when (!viewer.Token.IsCancellationRequested)
                    {
                        await _outputs.ReleaseViewerAsync(viewer.Id, "source_failure");
                        return;
                    }
                    if (!viewer.Token.IsCancellationRequested)
                    {
                        await _outputs.ReleaseViewerAsync(viewer.Id, "source_eof");
                    }
                    return;
                }
                var producer = viewer.Producer;
                var stage = producer.Ready.IsCompletedSuccessfully ? VirtualOutputStreamStartupStage.Ready : VirtualOutputStreamStartupStage.Starting;
                if (await ReportAsync(viewer, producer.Session?.Plan ?? producer.InitialPlan, stage, false, viewer.Token))
                {
                    break;
                }
            }
            await StopAsync(viewer.Id.ToString());
        }
        catch (OperationCanceledException) when (viewer.Lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Dedicated viewer {ViewerId} lost its broker lease.", viewer.Id);
            await StopAsync(viewer.Id.ToString());
        }
    }

    private void Track(Task task)
    {
        var id = Guid.NewGuid();
        _background[id] = task;
        _ = ObserveAsync();
        async Task ObserveAsync()
        {
            try
            {
                await task;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Dedicated playback background cleanup failed.");
            }
            finally
            {
                _background.TryRemove(id, out _);
            }
        }
    }

    private sealed class Viewer
    {
        private readonly DedicatedPlaybackRuntime _owner;
        private readonly Lock _gate = new();
        private Task? _closing;
        /// <summary>Initializes an independently admitted viewer over a producer-owned source.</summary>
        internal Viewer(DedicatedPlaybackRuntime owner, StreamSessionId id, string channel, VirtualOutputClientStream source, Producer producer, ICmafOutputLease lease)
        {
            _owner = owner;
            Id = id;
            Channel = channel;
            Source = source;
            Producer = producer;
            Lease = lease;
            Token = Lifetime.Token;
        }

        /// <summary>Gets the public viewer alias.</summary>
        internal StreamSessionId Id { get; }
        /// <summary>Gets the requested channel.</summary>
        internal string Channel { get; }
        /// <summary>Gets this viewer's admission time.</summary>
        internal DateTime StartedAtUtc { get; } = DateTime.UtcNow;
        /// <summary>Gets this viewer's independently admitted broker input.</summary>
        internal VirtualOutputClientStream Source { get; }
        /// <summary>Gets the producer shared by equivalent viewers.</summary>
        internal Producer Producer { get; }
        /// <summary>Gets the coordinator lease corresponding to this alias.</summary>
        internal ICmafOutputLease Lease { get; }
        /// <summary>Gets cancellation for only this viewer.</summary>
        internal CancellationTokenSource Lifetime { get; } = new();
        /// <summary>Gets the lifetime token without accessing a disposed source.</summary>
        internal CancellationToken Token { get; }
        /// <summary>Gets the owned drain for an input not consumed by the producer.</summary>
        internal Task? Drain { get; private set; }

        /// <summary>Consumes the independently admitted input without creating a second media producer.</summary>
        internal void StartDrain() => Drain = Source.Stream.CopyToAsync(Stream.Null, Token);

        /// <summary>Releases logical admission and any non-producer-owned input.</summary>
        internal Task CloseAsync()
        {
            lock (_gate)
            {
                if (_closing is null || _closing.IsFaulted)
                {
                    _closing = CloseCoreAsync();
                }
                return _closing;
            }
        }

        private async Task CloseCoreAsync()
        {
            if (!Lifetime.IsCancellationRequested)
            {
                await Lifetime.CancelAsync();
            }
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _owner.ReportAsync(this, Producer.Session?.Plan ?? Producer.InitialPlan, VirtualOutputStreamStartupStage.Ready, true, timeout.Token);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or VirtualOutputClientException or InvalidOperationException)
            {
                if (!Producer.IsStopping)
                {
                    _owner._logger.LogWarning(exception, "Could not confirm admission release for viewer {ViewerId}; producer cleanup will release its source.", Id);
                    if (ReferenceEquals(Source, Producer.Source))
                    {
                        throw;
                    }
                }
            }
            finally
            {
                try
                {
                    if (!ReferenceEquals(Source, Producer.Source))
                    {
                        await Source.DisposeAsync();
                    }
                    if (Drain is not null)
                    {
                        try
                        {
                            await Drain;
                        }
                        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested)
                        {
                        }
                    }
                }
                finally
                {
                    Lifetime.Dispose();
                }
            }
            _owner._releasing.TryRemove(Id, out _);
        }
    }

    private sealed class Producer(DedicatedPlaybackRuntime owner, VirtualOutputClientStream source, DedicatedCmafSessionPlan initialPlan, string directory) : IAsyncDisposable
    {
        private readonly CancellationTokenSource _lifetime = new();
        private readonly TaskCompletionSource<MediaSession> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Lock _gate = new();
        private readonly List<Task> _cleanup = [];
        private Task? _starting;
        private int _stopping;
        /// <summary>Gets or sets the coordinator's stable producer identity before startup.</summary>
        internal string? Id { get; set; }
        /// <summary>Gets the input retained independently of its initiating viewer.</summary>
        internal VirtualOutputClientStream Source { get; } = source;
        /// <summary>Gets the canonical requested presentation plan.</summary>
        internal DedicatedCmafSessionPlan InitialPlan { get; } = initialPlan;
        /// <summary>Gets the current media attempt, including any fallback.</summary>
        internal MediaSession? Session { get; private set; }
        /// <summary>Gets the producer-owned readiness result.</summary>
        internal Task<MediaSession> Ready => _ready.Task;
        /// <summary>Gets whether final source cleanup has begun.</summary>
        internal bool IsStopping => Volatile.Read(ref _stopping) != 0;

        /// <summary>Tracks outstanding admission-release work without retaining completed viewer tasks indefinitely.</summary>
        internal void AddCleanup(Task task)
        {
            lock (_gate)
            {
                _cleanup.RemoveAll(completed => completed.IsCompleted);
                _cleanup.Add(task);
            }
        }

        /// <summary>Starts media independently of any viewer's HTTP cancellation.</summary>
        internal void Start() => _starting = RunAsync();

        private async Task RunAsync()
        {
            var stage = DedicatedWorkerStartupStage.Process;
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(20));
                var plan = InitialPlan;
                Session = await StartMediaSessionAsync(Id!, directory, Source, plan, owner._logger, owner._mediaWork);
                stage = DedicatedWorkerStartupStage.Readiness;
                try
                {
                    await WaitForOutputAsync(Session, TimeSpan.FromSeconds(20), deadline.Token);
                }
                catch (MediaProcessException) when (!deadline.IsCancellationRequested && Session.Process.HasExited)
                {
                    var diagnostics = await Session.ErrorTask.WaitAsync(deadline.Token);
                    if (!CmafStreamPlanner.ShouldRetryWithFallback(plan.Request, plan.Source.Tracks.Where(track => track.Type == MediaTrackType.Audio), [diagnostics]))
                    {
                        throw;
                    }
                    await Session.StopMediaAsync();
                    await MediaSessionStartupGuard.DeleteDirectoryAsync(directory);
                    Directory.CreateDirectory(directory);
                    deadline.Token.ThrowIfCancellationRequested();
                    plan = plan.CreateFallback(directory);
                    stage = DedicatedWorkerStartupStage.Process;
                    Session = await StartMediaSessionAsync(Id!, directory, Source, plan, owner._logger, owner._mediaWork);
                    stage = DedicatedWorkerStartupStage.Readiness;
                    await WaitForOutputAsync(Session, TimeSpan.FromSeconds(20), deadline.Token);
                }
                deadline.Token.ThrowIfCancellationRequested();
                _ready.TrySetResult(Session);
                owner.Track(ObserveProcessAsync(Session, owner._outputs));
            }
            catch (Exception exception)
            {
                var failure = exception is OperationCanceledException && !_lifetime.IsCancellationRequested
                    ? new TimeoutException("The Web Player did not prepare the media presentation within its startup deadline.", exception)
                    : exception;
                MediaProcessDiagnostics.LogFailure(owner._logger, failure, stage.ToString(), Id!, Source.RequestId.ToString());
                _ready.TrySetException(new PlaybackStartupException(stage, failure));
                _ = _ready.Task.Exception;
                owner.Track(owner._outputs.ReleaseOutputAsync(new StreamSessionId(Guid.ParseExact(Id!, "N"))).AsTask());
            }
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _stopping, 1) != 0)
            {
                return;
            }
            try
            {
                await _lifetime.CancelAsync();
                if (_starting is not null)
                {
                    await _starting;
                }
                Task[] cleanup;
                lock (_gate) { cleanup = _cleanup.Where(task => !task.IsCompleted).ToArray(); }
                await Task.WhenAll(cleanup);
            }
            finally
            {
                try
                {
                    if (Session is not null)
                    {
                        await Session.DisposeAsync();
                    }
                    else
                    {
                        await Source.DisposeAsync();
                        await MediaSessionStartupGuard.DeleteDirectoryAsync(directory);
                    }
                }
                finally
                {
                    foreach (var (id, viewer) in owner._releasing)
                    {
                        if (ReferenceEquals(viewer.Producer, this))
                        {
                            owner._releasing.TryRemove(id, out _);
                        }
                    }
                    _lifetime.Dispose();
                }
            }
        }
    }
}

/// <summary>Contains a ready viewer's typed media lookup without any HTTP context or result.</summary>
internal sealed record PlaybackView(string Id, string Directory, DedicatedCmafSessionPlan Plan);

/// <summary>Preserves the typed startup stage for transport-specific error mapping.</summary>
internal sealed class PlaybackStartupException(DedicatedWorkerStartupStage stage, Exception cause) : Exception(cause.Message, cause)
{
    /// <summary>Gets the failed startup stage.</summary>
    internal DedicatedWorkerStartupStage Stage { get; } = stage;
}
