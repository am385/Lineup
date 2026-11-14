using System.Text.Json;
using Lineup.Core.Streaming.FFmpeg;

namespace Lineup.Core.Hosting.Streaming;

/// <summary>
/// Adapts URI-based streaming callers to the provider-neutral Core session graph.
/// </summary>
public interface ITunerStreamMultiplexer : ISharedStreamShutdown
{
    /// <summary>Subscribes within one overall source-acquisition deadline, dividing time among remaining candidates without limiting established stream lifetime.</summary>
    ValueTask<ResolvedTunerStream> SubscribeFirstAvailableAsync(ResolvedStreamingRoute route, CancellationToken cancellationToken);

    /// <summary>Subscribes to a provider-owned physical acquisition.</summary>
    ValueTask<Stream> SubscribeAsync(StreamAcquisitionDescriptor descriptor, StreamAcquisitionFactory acquisitionFactory, CancellationToken cancellationToken);

}

/// <summary>Owns a subscription and identifies the exact physical candidate that produced it.</summary>
public sealed class ResolvedTunerStream(ResolvedStreamingCandidate candidate, Stream stream) : IAsyncDisposable
{
    /// <summary>Gets the selected physical candidate.</summary>
    public ResolvedStreamingCandidate Candidate { get; } = candidate;

    /// <summary>Gets the selected shared source stream.</summary>
    public Stream Stream { get; } = stream;

    /// <inheritdoc />
    public ValueTask DisposeAsync() => Stream.DisposeAsync();
}

/// <summary>
/// Exposes optional shared progressive source normalization.
/// </summary>
public interface IEffectiveTunerStreamMultiplexer
{
    /// <summary>Subscribes to an effective provider-owned source.</summary>
    ValueTask<Stream> SubscribeEffectiveAsync(
        StreamAcquisitionDescriptor descriptor,
        StreamAcquisitionFactory acquisitionFactory,
        string sourceName,
        MediaProbeResult source,
        PhysicalTranscodeProfile physicalProfile,
        LineupSettings settings,
        CancellationToken cancellationToken
    );

    /// <summary>Subscribes to a shared MPEG-TS output built from the effective physical source.</summary>
    ValueTask<Stream> SubscribeMpegTsAsync(
        StreamAcquisitionDescriptor descriptor,
        StreamAcquisitionFactory acquisitionFactory,
        string sourceName,
        MediaProbeResult source,
        PhysicalTranscodeProfile physicalProfile,
        VirtualTranscodeProfile virtualProfile,
        LineupSettings settings,
        TimeSpan keepWarmPeriod,
        CancellationToken cancellationToken
    );

    /// <summary>Returns metadata describing the effective source.</summary>
    MediaProbeResult GetEffectiveSource(MediaProbeResult source, PhysicalTranscodeProfile physicalProfile);
}

/// <summary>Exposes the producer graph node associated with a hosted stream wrapper.</summary>
public interface IStreamProducerCorrelation
{
    /// <summary>Gets the producer graph node identifier.</summary>
    string ProducerNodeId { get; }
}

/// <summary>
/// Uses the Core session graph for acquisition and transform sharing.
/// </summary>
public sealed class TunerStreamMultiplexer : ITunerStreamMultiplexer, IEffectiveTunerStreamMultiplexer
{
    private static readonly TimeSpan _candidateAcquisitionTimeout = TimeSpan.FromSeconds(10);
    private readonly IStreamingSessionGraph _graph;
    private readonly IMediaProcessFactory _processFactory;
    private readonly ILogger<TunerStreamMultiplexer> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly StreamingResourceOptions _resources;

    /// <summary>Initializes the Core graph adapter.</summary>
    public TunerStreamMultiplexer(IStreamingSessionGraph graph, IMediaProcessFactory processFactory, ILogger<TunerStreamMultiplexer> logger)
        : this(graph, processFactory, logger, TimeProvider.System)
    {
    }

    /// <summary>Initializes the graph adapter with a clock for physical acquisition deadlines.</summary>
    public TunerStreamMultiplexer(IStreamingSessionGraph graph, IMediaProcessFactory processFactory, ILogger<TunerStreamMultiplexer> logger, TimeProvider timeProvider, StreamingResourceOptions? resources = null)
    {
        _graph = graph;
        _processFactory = processFactory;
        _logger = logger;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _resources = resources ?? new();
        _resources.Validate();
    }

    /// <summary>Executes subscribe first available asynchronously.</summary>
    /// <param name="route">The route value.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The operation's result.</returns>
    public async ValueTask<ResolvedTunerStream> SubscribeFirstAvailableAsync(ResolvedStreamingRoute route, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(route);
        using var operation = Lineup.Core.Extensibility.Diagnostics.LineupTelemetry.Begin("acquisition", cancellationToken: cancellationToken);
        var failures = new List<Exception>();
        var candidateFailures = new List<StreamAcquisitionCandidateFailure>();
        var allowance = TimeSpan.FromSeconds(_resources.AcquisitionTimeoutSeconds);
        var started = _timeProvider.GetTimestamp();
        using var overallDeadline = new CancellationTokenSource(allowance, _timeProvider);
        for (var index = 0; index < route.Candidates.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = route.Candidates[index];
            var remaining = allowance - _timeProvider.GetElapsedTime(started);
            if (overallDeadline.IsCancellationRequested || remaining <= TimeSpan.Zero)
            {
                candidateFailures.Add(CreateCandidateFailure(candidate, PhysicalStreamFailureCategory.Unavailable, "source_acquisition_timeout", "The overall source acquisition deadline was exhausted.", true));
                failures.Add(new TimeoutException("The overall source acquisition deadline was exhausted."));
                break;
            }
            var slice = TimeSpan.FromTicks(Math.Min(_candidateAcquisitionTimeout.Ticks, Math.Max(1, remaining.Ticks / (route.Candidates.Count - index))));
            using var deadline = new CancellationTokenSource(slice, _timeProvider);
            using var acquisition = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token, overallDeadline.Token);
            using var attempt = Lineup.Core.Extensibility.Diagnostics.LineupTelemetry.Begin("source_candidate");
            try
            {
                var stream = await SubscribeAsync(candidate.Acquisition, candidate.OpenAsync, acquisition.Token);
                if (index > 0)
                {
                    Lineup.Core.Extensibility.Diagnostics.LineupTelemetry.Fallbacks.Add(1);
                }
                operation.Succeed();
                attempt.Succeed();
                return new ResolvedTunerStream(candidate, stream);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                operation.Cancel();
                attempt.Cancel();
                throw;
            }
            catch (OperationCanceledException exception) when (deadline.IsCancellationRequested || overallDeadline.IsCancellationRequested)
            {
                var message = $"The physical source did not complete stream acquisition within its {slice.TotalSeconds:0.###}-second allowance.";
                failures.Add(new TimeoutException(message, exception));
                candidateFailures.Add(CreateCandidateFailure(
                    candidate,
                    PhysicalStreamFailureCategory.Unavailable,
                    "source_acquisition_timeout",
                    message,
                    retryable: true));
                _logger.LogWarning("Physical stream acquisition timed out for device {PhysicalDeviceId}, channel {SourceChannelId}", candidate.Acquisition.DeviceId, candidate.SourceChannelId);
            }
            catch (MediaResourceLimitException)
            {
                throw;
            }
            catch (StreamCapacityExceededException exception)
            {
                failures.Add(exception);
                candidateFailures.Add(new StreamAcquisitionCandidateFailure
                {
                    DeviceId = candidate.SourceChannelId.PhysicalDeviceId,
                    ChannelId = candidate.SourceChannelId,
                    Category = PhysicalStreamFailureCategory.Capacity,
                    Code = "source_capacity_exceeded",
                    Message = "The physical source has no available tuner capacity.",
                    Retryable = true
                });
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or HttpRequestException or InvalidOperationException or KeyNotFoundException)
            {
                failures.Add(exception);
                candidateFailures.Add(CreateCandidateFailure(candidate, exception));
            }
        }

        throw StreamAcquisitionException.Create(candidateFailures, failures);
    }

    /// <summary>Executes subscribe asynchronously.</summary>
    /// <param name="descriptor">The descriptor value.</param>
    /// <param name="acquisitionFactory">The acquisition factory value.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The operation's result.</returns>
    public async ValueTask<Stream> SubscribeAsync(StreamAcquisitionDescriptor descriptor, StreamAcquisitionFactory acquisitionFactory, CancellationToken cancellationToken)
    {
        var lease = await _graph.AcquireAsync(descriptor, acquisitionFactory, cancellationToken);
        return new SessionOwnedStream(lease);
    }

    private static StreamAcquisitionCandidateFailure CreateCandidateFailure(ResolvedStreamingCandidate candidate, Exception exception)
    {
        if (exception is PhysicalStreamAcquisitionException physicalFailure)
        {
            return new StreamAcquisitionCandidateFailure
            {
                DeviceId = physicalFailure.DeviceId,
                ChannelId = physicalFailure.ChannelId,
                Category = physicalFailure.Category,
                Code = physicalFailure.Code,
                Message = physicalFailure.Message,
                Retryable = physicalFailure.Retryable
            };
        }

        return exception switch
        {
            KeyNotFoundException => CreateCandidateFailure(
                candidate,
                PhysicalStreamFailureCategory.NotFound,
                "source_not_found",
                "The physical source channel is no longer available.",
                retryable: false),
            HttpRequestException httpException => CreateCandidateFailure(
                candidate,
                PhysicalStreamFailureCategory.Unavailable,
                "source_unavailable",
                httpException.StatusCode is { } status
                    ? $"The physical source returned HTTP {(int)status}."
                    : "The physical source could not be reached.",
                retryable: true),
            _ => CreateCandidateFailure(
                candidate,
                PhysicalStreamFailureCategory.Unavailable,
                "source_unavailable",
                "The physical source could not be opened.",
                retryable: true)
        };
    }

    private static StreamAcquisitionCandidateFailure CreateCandidateFailure(ResolvedStreamingCandidate candidate, PhysicalStreamFailureCategory category, string code, string message, bool retryable) =>
        new()
        {
            DeviceId = candidate.SourceChannelId.PhysicalDeviceId,
            ChannelId = candidate.SourceChannelId,
            Category = category,
            Code = code,
            Message = message,
            Retryable = retryable
        };

    /// <summary>Executes subscribe effective asynchronously.</summary>
    /// <param name="descriptor">The descriptor value.</param>
    /// <param name="acquisitionFactory">The acquisition factory value.</param>
    /// <param name="sourceName">The source name value.</param>
    /// <param name="source">The source value.</param>
    /// <param name="physicalProfile">The physical profile value.</param>
    /// <param name="settings">The settings value.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The operation's result.</returns>
    public async ValueTask<Stream> SubscribeEffectiveAsync(
        StreamAcquisitionDescriptor descriptor,
        StreamAcquisitionFactory acquisitionFactory,
        string sourceName,
        MediaProbeResult source,
        PhysicalTranscodeProfile physicalProfile,
        LineupSettings settings,
        CancellationToken cancellationToken
    )
    {
        if (!SourceDeinterlacePlanner.ShouldNormalize(source, physicalProfile))
        {
            return await SubscribeAsync(descriptor, acquisitionFactory, cancellationToken);
        }

        var stage = CreateStage(
            "source-normalization",
            SourceDeinterlacePlanner.CreateArguments(source, physicalProfile),
            sourceName);
        var lease = await _graph.AcquireTransformAsync(descriptor, stage.Key, acquisitionFactory, stage.Transform, cancellationToken);
        return new SessionOwnedStream(lease);
    }

    /// <summary>Gets effective source.</summary>
    /// <param name="source">The source value.</param>
    /// <param name="physicalProfile">The physical profile value.</param>
    /// <returns>The operation's result.</returns>
    public MediaProbeResult GetEffectiveSource(MediaProbeResult source, PhysicalTranscodeProfile physicalProfile) =>
        SourceDeinterlacePlanner.CreateEffectiveSource(source, physicalProfile);

    /// <summary>Executes subscribe mpeg ts asynchronously.</summary>
    /// <param name="descriptor">The descriptor value.</param>
    /// <param name="acquisitionFactory">The acquisition factory value.</param>
    /// <param name="sourceName">The source name value.</param>
    /// <param name="source">The source value.</param>
    /// <param name="physicalProfile">The physical profile value.</param>
    /// <param name="virtualProfile">The virtual profile value.</param>
    /// <param name="settings">The settings value.</param>
    /// <param name="keepWarmPeriod">The keep warm period value.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The operation's result.</returns>
    public async ValueTask<Stream> SubscribeMpegTsAsync(
        StreamAcquisitionDescriptor descriptor,
        StreamAcquisitionFactory acquisitionFactory,
        string sourceName,
        MediaProbeResult source,
        PhysicalTranscodeProfile physicalProfile,
        VirtualTranscodeProfile virtualProfile,
        LineupSettings settings,
        TimeSpan keepWarmPeriod,
        CancellationToken cancellationToken
    )
    {
        var stages = new List<StreamTransformStage>();
        var effectiveSource = source;
        if (SourceDeinterlacePlanner.ShouldNormalize(source, physicalProfile))
        {
            var physicalArguments = SourceDeinterlacePlanner.CreateArguments(source, physicalProfile);
            stages.Add(CreateStage("source-normalization", physicalArguments, sourceName));
            effectiveSource = SourceDeinterlacePlanner.CreateEffectiveSource(source, physicalProfile);
        }

        var audioStreams = effectiveSource.Tracks
            .Where(track => track.Type == MediaTrackType.Audio)
            .Select(track => new AudioStreamInfo(track.Index, track.Codec, track.Channels ?? 0))
            .ToArray();
        var subtitles = effectiveSource.Tracks.Where(track => track.Type == MediaTrackType.Subtitle).ToArray();
        var sourceVideo = effectiveSource.Tracks.FirstOrDefault(track => track.Type == MediaTrackType.Video);
        var virtualArguments = MpegTsTranscodePlanner.CreateFfmpegArguments(
            new Uri("http://lineup.invalid/source"),
            sourceVideo?.Codec,
            audioStreams,
            virtualProfile,
            subtitles,
            sourceVideo?.ScanType == VideoScanType.Interlaced).ToList();
        virtualArguments[virtualArguments.IndexOf("http://lineup.invalid/source")] = "pipe:0";
        stages.Add(CreateStage("virtual-mpegts", virtualArguments, sourceName));

        var lease = await _graph.AcquirePipelineAsync(descriptor, acquisitionFactory, stages, keepWarmPeriod, cancellationToken);
        return new SessionOwnedStream(lease);
    }

    /// <summary>Stops the tuner stream multiplexer asynchronously.</summary>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task that completes when the operation has finished.</returns>
    public Task StopAsync(CancellationToken cancellationToken) => _graph.StopAsync(cancellationToken);

    private StreamTransformStage CreateStage(string stage, IReadOnlyList<string> arguments, string sourceName)
    {
        var settingsKey = JsonSerializer.Serialize(arguments);
        return new StreamTransformStage(
            new StreamTransformKey(stage, settingsKey),
            new FfmpegGraphTransform(new FfmpegStreamTransform(arguments, _processFactory, _logger, sourceName)));
    }

    private sealed class SessionOwnedStream(IStreamSessionLease lease) : Stream, IStreamProducerCorrelation
    {
        private IStreamSessionLease? _lease = lease;

        private Stream Inner => _lease?.Stream ?? throw new ObjectDisposedException(nameof(SessionOwnedStream));

        /// <summary>Gets the producer node ID.</summary>
        public string ProducerNodeId => _lease?.ProducerNodeId ?? throw new ObjectDisposedException(nameof(SessionOwnedStream));

        /// <inheritdoc />
        public override bool CanRead => _lease?.Stream.CanRead == true;
        /// <inheritdoc />
        public override bool CanSeek => false;
        /// <inheritdoc />
        public override bool CanWrite => false;
        /// <inheritdoc />
        public override long Length => throw new NotSupportedException();
        /// <inheritdoc />
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) => Inner.Read(buffer, offset, count);
        /// <inheritdoc />
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => Inner.ReadAsync(buffer, cancellationToken);

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Interlocked.Exchange(ref _lease, null)?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            base.Dispose(disposing);
        }

        /// <inheritdoc />
        public override async ValueTask DisposeAsync()
        {
            var owner = Interlocked.Exchange(ref _lease, null);
            if (owner is not null)
            {
                await owner.DisposeAsync();
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

    private sealed class LoggerAdapter<T>(ILogger logger) : ILogger<T>
    {
        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => logger.BeginScope(state);

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => logger.IsEnabled(logLevel);

        /// <inheritdoc />
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            logger.Log(logLevel, eventId, state, exception, formatter);
    }
}
