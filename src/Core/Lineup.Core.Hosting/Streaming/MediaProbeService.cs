using Lineup.Core.Streaming.FFmpeg;

namespace Lineup.Core.Hosting.Streaming;

/// <summary>Probes provider-owned physical acquisitions with shared application metadata.</summary>
public interface IMediaProbeService
{
    /// <summary>Probes a provider-owned physical acquisition.</summary>
    Task<MediaProbeResult> ProbeAsync(
        StreamAcquisitionDescriptor descriptor,
        StreamAcquisitionFactory acquisitionFactory,
        string sourceName,
        IReadOnlyDictionary<string, string> sourceMetadata,
        CancellationToken cancellationToken
    );
}

/// <summary>Adapts physical acquisitions and shared graph metadata to the portable FFprobe process.</summary>
public sealed class MediaProbeService : IMediaProbeService
{
    private const string MetadataKey = "ffprobe-v2";
    private static readonly TimeSpan SharedProbeOperationTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SharedProbeRefreshWaitTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ProvisionalRetryDelay = TimeSpan.FromSeconds(1);
    private readonly ILogger<MediaProbeService> _logger;
    private readonly ITunerStreamMultiplexer _tunerStreamMultiplexer;
    private readonly IStreamingSessionGraph? _streamingGraph;
    private readonly MediaStreamProbe _probe;

    /// <summary>Initializes shared acquisition probing with the portable process runner.</summary>
    public MediaProbeService(ILogger<MediaProbeService> logger, ITunerStreamMultiplexer tunerStreamMultiplexer, IMediaProcessFactory processFactory, IStreamingSessionGraph? streamingGraph = null)
    {
        _logger = logger;
        _tunerStreamMultiplexer = tunerStreamMultiplexer;
        _streamingGraph = streamingGraph;
        _probe = new MediaStreamProbe(logger, processFactory);
    }

    /// <summary>Initializes the probe service with the default process factory.</summary>
    public MediaProbeService(ILogger<MediaProbeService> logger, ITunerStreamMultiplexer tunerStreamMultiplexer)
        : this(logger, tunerStreamMultiplexer, new MediaProcessFactory())
    {
    }

    /// <inheritdoc />
    public async Task<MediaProbeResult> ProbeAsync(
        StreamAcquisitionDescriptor descriptor,
        StreamAcquisitionFactory acquisitionFactory,
        string sourceName,
        IReadOnlyDictionary<string, string> sourceMetadata,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(sourceMetadata);
        if (_streamingGraph is not null)
        {
            if (!MediaProbeResultPolicy.IsDeclaredVideoOnly(sourceMetadata))
            {
                var result = await GetSharedProbeAsync(
                    () => _streamingGraph.GetOrRefreshAcquisitionMetadataAsync(
                        descriptor,
                        acquisitionFactory,
                        MetadataKey,
                        (stream, token) => _probe.ProbeAsync(sourceName, stream, token),
                        new AcquisitionMetadataRefreshPolicy<MediaProbeResult>
                        {
                            IsReusable = MediaProbeResultPolicy.HasAudio,
                            Merge = MediaProbeResultPolicy.Merge,
                            RetryDelay = ProvisionalRetryDelay,
                            OperationTimeout = SharedProbeOperationTimeout,
                            RefreshWaitTimeout = SharedProbeRefreshWaitTimeout
                        },
                        cancellationToken),
                    sourceName);
                if (!MediaProbeResultPolicy.HasAudio(result) && MediaProbeResultPolicy.ExpectsAudio(sourceMetadata))
                {
                    _logger.LogWarning(
                        "FFprobe did not discover advertised audio after a bounded refresh for {InputSource}; a later viewer will probe again",
                        sourceName);
                }
                return MediaProbeResultPolicy.Assess(result, sourceMetadata);
            }
            var declaredVideoOnly = await GetSharedProbeAsync(
                () => _streamingGraph.GetOrCreateAcquisitionMetadataAsync(
                    descriptor,
                    acquisitionFactory,
                    MetadataKey,
                    (stream, token) => _probe.ProbeAsync(sourceName, stream, token),
                    cancellationToken),
                sourceName);
            return MediaProbeResultPolicy.Assess(declaredVideoOnly, sourceMetadata);
        }
        var observation = await ProbeCoreAsync(descriptor, acquisitionFactory, sourceName, cancellationToken);
        if (!MediaProbeResultPolicy.IsDeclaredVideoOnly(sourceMetadata) && !MediaProbeResultPolicy.HasAudio(observation))
        {
            await Task.Delay(ProvisionalRetryDelay, cancellationToken);
            var refreshed = await ProbeCoreAsync(descriptor, acquisitionFactory, sourceName, cancellationToken);
            observation = MediaProbeResultPolicy.Merge(observation, refreshed);
        }
        return MediaProbeResultPolicy.Assess(observation, sourceMetadata);
    }

    private static async Task<MediaProbeResult> GetSharedProbeAsync(Func<Task<MediaProbeResult>> probeFactory, string sourceName)
    {
        try
        {
            return await probeFactory();
        }
        catch (TimeoutException exception)
        {
            throw new MpegTsTranscodeException(
                $"FFprobe source acquisition and analysis timed out after {SharedProbeOperationTimeout.TotalSeconds:0} seconds for {sourceName}.",
                exception);
        }
    }

    private async Task<MediaProbeResult> ProbeCoreAsync(StreamAcquisitionDescriptor descriptor, StreamAcquisitionFactory acquisitionFactory, string sourceName, CancellationToken cancellationToken)
    {
        await using var input = await _tunerStreamMultiplexer.SubscribeAsync(descriptor, acquisitionFactory, cancellationToken);
        return await _probe.ProbeAsync(sourceName, input, cancellationToken);
    }
}
