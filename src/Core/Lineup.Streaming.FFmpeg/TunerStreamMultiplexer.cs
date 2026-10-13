using System.Text.Json;

namespace Lineup.Streaming.FFmpeg;

/// <summary>
/// Adapts URI-based streaming callers to the provider-neutral Core session graph.
/// </summary>
public interface ITunerStreamMultiplexer : ISharedStreamShutdown
{
    /// <summary>Subscribes to the first available exact physical candidate.</summary>
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
        CancellationToken cancellationToken);

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
        CancellationToken cancellationToken);

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
    private readonly IStreamingSessionGraph _graph;
    private readonly IMediaProcessFactory _processFactory;
    private readonly ILogger<TunerStreamMultiplexer> _logger;

    /// <summary>Initializes the Core graph adapter.</summary>
    public TunerStreamMultiplexer(
        IStreamingSessionGraph graph,
        IMediaProcessFactory processFactory,
        ILogger<TunerStreamMultiplexer> logger)
    {
        _graph = graph;
        _processFactory = processFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<ResolvedTunerStream> SubscribeFirstAvailableAsync(ResolvedStreamingRoute route, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(route);
        var failures = new List<Exception>();
        foreach (var candidate in route.Candidates)
        {
            try
            {
                var stream = await SubscribeAsync(candidate.Acquisition, candidate.OpenAsync, cancellationToken);
                return new ResolvedTunerStream(candidate, stream);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (StreamCapacityExceededException)
            {
            }
            catch (Exception exception) when (exception is IOException or HttpRequestException or InvalidOperationException or KeyNotFoundException)
            {
                failures.Add(exception);
            }
        }

        if (failures.Count > 0)
        {
            throw new IOException("Every equivalent physical source failed to open.", new AggregateException(failures));
        }

        var preferred = route.Candidates[0].Acquisition;
        throw new StreamCapacityExceededException(preferred.DeviceId, preferred.Capacity);
    }

    /// <inheritdoc />
    public async ValueTask<Stream> SubscribeAsync(StreamAcquisitionDescriptor descriptor, StreamAcquisitionFactory acquisitionFactory, CancellationToken cancellationToken)
    {
        var lease = await _graph.AcquireAsync(descriptor, acquisitionFactory, cancellationToken);
        return new SessionOwnedStream(lease);
    }

    /// <inheritdoc />
    public async ValueTask<Stream> SubscribeEffectiveAsync(
        StreamAcquisitionDescriptor descriptor,
        StreamAcquisitionFactory acquisitionFactory,
        string sourceName,
        MediaProbeResult source,
        PhysicalTranscodeProfile physicalProfile,
        LineupSettings settings,
        CancellationToken cancellationToken)
    {
        if (!SourceDeinterlacePlanner.ShouldNormalize(source, physicalProfile))
        {
            return await SubscribeAsync(descriptor, acquisitionFactory, cancellationToken);
        }

        var stage = CreateStage(
            "source-normalization",
            SourceDeinterlacePlanner.CreateArguments(source, physicalProfile, settings),
            sourceName);
        var lease = await _graph.AcquireTransformAsync(descriptor, stage.Key, acquisitionFactory, stage.Transform, cancellationToken);
        return new SessionOwnedStream(lease);
    }

    /// <inheritdoc />
    public MediaProbeResult GetEffectiveSource(MediaProbeResult source, PhysicalTranscodeProfile physicalProfile) =>
        SourceDeinterlacePlanner.CreateEffectiveSource(source, physicalProfile);

    /// <inheritdoc />
    public async ValueTask<Stream> SubscribeMpegTsAsync(
        StreamAcquisitionDescriptor descriptor,
        StreamAcquisitionFactory acquisitionFactory,
        string sourceName,
        MediaProbeResult source,
        PhysicalTranscodeProfile physicalProfile,
        VirtualTranscodeProfile virtualProfile,
        LineupSettings settings,
        TimeSpan keepWarmPeriod,
        CancellationToken cancellationToken)
    {
        var stages = new List<StreamTransformStage>();
        var effectiveSource = source;
        if (SourceDeinterlacePlanner.ShouldNormalize(source, physicalProfile))
        {
            var physicalArguments = SourceDeinterlacePlanner.CreateArguments(source, physicalProfile, settings);
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
            settings,
            subtitles,
            sourceVideo?.ScanType == VideoScanType.Interlaced).ToList();
        virtualArguments[virtualArguments.IndexOf("http://lineup.invalid/source")] = "pipe:0";
        stages.Add(CreateStage("virtual-mpegts", virtualArguments, sourceName));

        var lease = await _graph.AcquirePipelineAsync(descriptor, acquisitionFactory, stages, keepWarmPeriod, cancellationToken);
        return new SessionOwnedStream(lease);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => _graph.StopAsync(cancellationToken);

    private StreamTransformStage CreateStage(string stage, IReadOnlyList<string> arguments, string sourceName)
    {
        var settingsKey = JsonSerializer.Serialize(arguments);
        return new StreamTransformStage(
            new StreamTransformKey(stage, settingsKey),
            new FfmpegStreamTransform(arguments, _processFactory, _logger, sourceName));
    }

    private sealed class SessionOwnedStream(IStreamSessionLease lease) : Stream, IStreamProducerCorrelation
    {
        private IStreamSessionLease? _lease = lease;

        private Stream Inner => _lease?.Stream ?? throw new ObjectDisposedException(nameof(SessionOwnedStream));

        /// <inheritdoc />
        public string ProducerNodeId => _lease?.ProducerNodeId ?? throw new ObjectDisposedException(nameof(SessionOwnedStream));

        public override bool CanRead => _lease?.Stream.CanRead == true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => Inner.ReadAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Interlocked.Exchange(ref _lease, null)?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            var owner = Interlocked.Exchange(ref _lease, null);
            if (owner is not null)
            {
                await owner.DisposeAsync();
            }
            GC.SuppressFinalize(this);
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class LoggerAdapter<T>(ILogger logger) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => logger.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => logger.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            logger.Log(logLevel, eventId, state, exception, formatter);
    }
}
