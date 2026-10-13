namespace Lineup.Streaming.FFmpeg;

/// <summary>Opens multiplexed MPEG-TS output for configured virtual devices.</summary>
public interface IVirtualMpegTsOutputService
{
    /// <summary>Opens one channel from a configured virtual device.</summary>
    Task<VirtualMpegTsOutput> OpenAsync(VirtualDeviceId virtualDeviceId, string channelNumber, CancellationToken cancellationToken);
}

/// <summary>Owns one virtual MPEG-TS output stream and its effective metadata.</summary>
public sealed record VirtualMpegTsOutput(
    Stream Stream,
    MediaProbeResult RawSource,
    MediaProbeResult Source,
    VirtualTranscodeProfile Profile,
    ActiveStreamFlowContext Flow) : IAsyncDisposable
{
    /// <inheritdoc />
    public ValueTask DisposeAsync() => Stream.DisposeAsync();
}

/// <summary>Uses the shared streaming graph for provider-neutral virtual MPEG-TS output.</summary>
public sealed class VirtualMpegTsOutputService : IVirtualMpegTsOutputService
{
    private readonly IStreamingRouteResolver _routes;
    private readonly ILineupSettingsService _settings;
    private readonly IMediaProbeService _probe;
    private readonly ITunerStreamMultiplexer _multiplexer;
    private readonly ILogger<VirtualMpegTsOutputService> _logger;

    /// <summary>Initializes the virtual MPEG-TS output service.</summary>
    public VirtualMpegTsOutputService(
        IStreamingRouteResolver routes,
        ILineupSettingsService settings,
        IMediaProbeService probe,
        ITunerStreamMultiplexer multiplexer,
        ILogger<VirtualMpegTsOutputService> logger)
    {
        _routes = routes;
        _settings = settings;
        _probe = probe;
        _multiplexer = multiplexer;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<VirtualMpegTsOutput> OpenAsync(
        VirtualDeviceId virtualDeviceId,
        string channelNumber,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelNumber);
        var route = await _routes.ResolveVirtualAsync(virtualDeviceId, channelNumber.Trim(), cancellationToken)
            ?? throw new KeyNotFoundException($"Channel '{channelNumber}' is not published by the requested virtual device.");
        var selected = await _multiplexer.SubscribeFirstAvailableAsync(route, cancellationToken);
        await using var reservation = selected.Stream;
        var source = await ProbeBestEffortAsync(selected.Candidate, cancellationToken);
        var settings = _settings.Settings;
        if (_multiplexer is not IEffectiveTunerStreamMultiplexer effective)
        {
            throw new InvalidOperationException("The configured stream multiplexer cannot produce virtual MPEG-TS output.");
        }

        var output = await effective.SubscribeMpegTsAsync(
            selected.Candidate.Acquisition,
            selected.Candidate.OpenAsync,
            selected.Candidate.DiagnosticUri.AbsoluteUri,
            source,
            selected.Candidate.PhysicalProfile,
            route.VirtualProfile,
            settings,
            TimeSpan.FromSeconds(settings.Streaming.KeepWarmPeriodSeconds),
            cancellationToken);
        return new VirtualMpegTsOutput(
            output,
            source,
            effective.GetEffectiveSource(source, selected.Candidate.PhysicalProfile),
            route.VirtualProfile,
            new ActiveStreamFlowContext
            {
                PhysicalDeviceId = selected.Candidate.SourceChannelId.PhysicalDeviceId,
                SourceChannelId = selected.Candidate.SourceChannelId,
                VirtualDeviceId = route.VirtualDevice.Id,
                ProducerNodeId = (output as IStreamProducerCorrelation)?.ProducerNodeId
            });
    }

    private async Task<MediaProbeResult> ProbeBestEffortAsync(
        ResolvedStreamingCandidate candidate,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _probe.ProbeAsync(
                candidate.Acquisition,
                candidate.OpenAsync,
                candidate.DiagnosticUri.AbsoluteUri,
                cancellationToken);
        }
        catch (MpegTsTranscodeException exception)
        {
            _logger.LogWarning(
                "Unable to probe media metadata for {InputUri}; streaming will continue with unknown source details: {ProbeError}",
                candidate.DiagnosticUri,
                exception.Message);
            return new MediaProbeResult([], null);
        }
    }
}
