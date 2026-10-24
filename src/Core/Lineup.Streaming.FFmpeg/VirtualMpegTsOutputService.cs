namespace Lineup.Streaming.FFmpeg;

/// <summary>Opens multiplexed MPEG-TS output for configured virtual devices.</summary>
public interface IVirtualMpegTsOutputService
{
    /// <summary>Opens one channel from a configured virtual device.</summary>
    Task<VirtualMpegTsOutput> OpenAsync(VirtualDeviceId virtualDeviceId, string channelNumber, CancellationToken cancellationToken);

    /// <summary>Opens one channel after physical transforms while retaining its effective virtual presentation profile.</summary>
    Task<VirtualMpegTsOutput> OpenPhysicalOutputAsync(VirtualDeviceId virtualDeviceId, string channelNumber, CancellationToken cancellationToken);
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
    private readonly IChannelSlateStreamFactory _slates;
    private readonly ILogger<VirtualMpegTsOutputService> _logger;

    /// <summary>Initializes the virtual MPEG-TS output service.</summary>
    public VirtualMpegTsOutputService(
        IStreamingRouteResolver routes,
        ILineupSettingsService settings,
        IMediaProbeService probe,
        ITunerStreamMultiplexer multiplexer,
        IChannelSlateStreamFactory slates,
        ILogger<VirtualMpegTsOutputService> logger)
    {
        _routes = routes;
        _settings = settings;
        _probe = probe;
        _multiplexer = multiplexer;
        _slates = slates;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<VirtualMpegTsOutput> OpenAsync(
        VirtualDeviceId virtualDeviceId,
        string channelNumber,
        CancellationToken cancellationToken)
        => await OpenCoreAsync(virtualDeviceId, channelNumber, applyVirtualProfile: true, cancellationToken);

    /// <inheritdoc />
    public async Task<VirtualMpegTsOutput> OpenPhysicalOutputAsync(
        VirtualDeviceId virtualDeviceId,
        string channelNumber,
        CancellationToken cancellationToken)
        => await OpenCoreAsync(virtualDeviceId, channelNumber, applyVirtualProfile: false, cancellationToken);

    private async Task<VirtualMpegTsOutput> OpenCoreAsync(
        VirtualDeviceId virtualDeviceId,
        string channelNumber,
        bool applyVirtualProfile,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelNumber);
        var route = await _routes.ResolveVirtualAsync(virtualDeviceId, channelNumber.Trim(), cancellationToken)
            ?? throw new KeyNotFoundException($"Channel '{channelNumber}' is not published by the requested virtual device.");
        var settings = _settings.Settings;
        var protection = ProtectedContentDetector.Detect(route);
        if (protection.IsProtected)
        {
            if (settings.ProtectedContentMode == ProtectedContentMode.ReturnError)
            {
                throw ProtectedContentDetector.CreateAcquisitionException(route);
            }
            var media = ChannelSlatePlanner.CreateMedia();
            return new VirtualMpegTsOutput(
                _slates.OpenMpegTs(channelNumber.Trim()),
                media,
                media,
                route.VirtualProfile,
                new ActiveStreamFlowContext
                {
                    VirtualDeviceId = route.VirtualDevice.Id,
                    SyntheticSource = "Protected content slate"
                });
        }
        var selected = await _multiplexer.SubscribeFirstAvailableAsync(route, cancellationToken);
        await using var reservation = selected.Stream;
        var source = await ProbeBestEffortAsync(selected.Candidate, cancellationToken);
        if (_multiplexer is not IEffectiveTunerStreamMultiplexer effective)
        {
            throw new InvalidOperationException("The configured stream multiplexer cannot produce virtual MPEG-TS output.");
        }

        var physicalSource = effective.GetEffectiveSource(source, selected.Candidate.PhysicalProfile);
        var output = applyVirtualProfile
            ? await effective.SubscribeMpegTsAsync(
                selected.Candidate.Acquisition,
                selected.Candidate.OpenAsync,
                selected.Candidate.DiagnosticUri.AbsoluteUri,
                source,
                selected.Candidate.PhysicalProfile,
                route.VirtualProfile,
                settings,
                TimeSpan.FromSeconds(settings.Streaming.KeepWarmPeriodSeconds),
                cancellationToken)
            : await effective.SubscribeEffectiveAsync(
                selected.Candidate.Acquisition,
                selected.Candidate.OpenAsync,
                selected.Candidate.DiagnosticUri.AbsoluteUri,
                source,
                selected.Candidate.PhysicalProfile,
                settings,
                cancellationToken);
        return new VirtualMpegTsOutput(
            output,
            source,
            applyVirtualProfile
                ? SourceDeinterlacePlanner.CreateVirtualMpegTsSource(physicalSource, route.VirtualProfile)
                : physicalSource,
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
                candidate.SourceMetadata,
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
