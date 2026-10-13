using System.Collections.ObjectModel;
using System.Reflection;
using System.Text.Json;
using Lineup.Common.Configuration;
using Lineup.Extensibility.Configuration;
using Lineup.VirtualOutputs.Runtime;

namespace Lineup.Web.Services;

/// <summary>
/// Represents the stable versioned Lineup status response.
/// </summary>
internal sealed record LineupStatusResponse(
    string SchemaVersion,
    DateTime GeneratedAtUtc,
    ApplicationStatusResponse Application,
    ConfigurationStatusResponse Configuration,
    GuideStatusResponse Guide,
    ChannelsStatusResponse Channels,
    IReadOnlyList<PhysicalDeviceStatusResponse> PhysicalDevices,
    IReadOnlyList<VirtualDeviceStatusResponse> VirtualDevices,
    IReadOnlyList<VirtualOutputRuntimeStatusResponse> VirtualOutputWorkers,
    IReadOnlyList<ReceiverStatusResponse> Receivers,
    ActiveStreamsStatusResponse Streams,
    StreamingGraphStatusResponse StreamingGraph,
    StreamingFlowsStatusResponse StreamingFlows);

/// <summary>
/// Represents application identity, setup, and uptime status.
/// </summary>
internal sealed record ApplicationStatusResponse(
    string Version,
    string InstanceId,
    bool SetupComplete,
    DateTime StartedAtUtc,
    long UptimeSeconds,
    string StartupStatus,
    string? StartupMessage);

/// <summary>
/// Represents guide coverage, refresh, and XMLTV status.
/// </summary>
internal sealed record GuideStatusResponse(
    string Status,
    string? Error,
    int? ChannelCount,
    int? ProgramCount,
    DateTime? EarliestProgramStartUtc,
    DateTime? LatestProgramEndUtc,
    double? CoveredHours,
    DateTime? SafeFetchStartUtc,
    AutoFetchStatusResponse AutoFetch,
    XmltvStatusResponse Xmltv,
    GuideSourceImportsStatusResponse SourceImports,
    IReadOnlyList<GuideOutputStatusResponse> Outputs);

/// <summary>
/// Represents automatic guide-fetch state.
/// </summary>
internal sealed record AutoFetchStatusResponse(
    bool Enabled,
    bool Running,
    string? Status,
    string? Message,
    string? Error,
    int? PercentComplete,
    int? FetchCount,
    int? ProgramsFetched,
    int? ChannelsFetched,
    DateTime? CurrentEndTimeUtc,
    DateTime? TargetEndTimeUtc,
    DateTime? LastFetchTimeUtc,
    DateTime? NextFetchTimeUtc);

/// <summary>
/// Represents generated XMLTV availability.
/// </summary>
internal sealed record XmltvStatusResponse(bool Enabled, bool Available, string? DownloadUrl, DateTime? LastWriteTimeUtc);

/// <summary>
/// Represents one configured physical device state.
/// </summary>
internal sealed record PhysicalDeviceStatusResponse(
    string PhysicalDeviceId,
    string Kind,
    string Name,
    string Status,
    string? Error,
    int? Capacity,
    bool SupportsReceiverControl,
    bool SupportsRestart,
    IReadOnlyDictionary<string, string> Details,
    DateTime? ObservedAtUtc);

/// <summary>
/// Represents configured and resolved virtual HDHomeRun device state.
/// </summary>
internal sealed record VirtualDeviceStatusResponse(
    string VirtualDeviceId,
    bool Enabled,
    bool Primary,
    bool Available,
    string? Error,
    string? FriendlyName,
    int? TunerCount,
    int? TunerCountCap,
    string? PhysicalAddress,
    string? AdvertisedUrl,
    DateTime? LastResolvedAtUtc,
    DateTime? LastAttemptAtUtc);

/// <summary>Represents desired-versus-applied isolated virtual-output worker state.</summary>
internal sealed record VirtualOutputRuntimeStatusResponse(
    string VirtualDeviceId,
    string State,
    long? Generation,
    int? ProcessId,
    int? ProtocolVersion,
    IReadOnlyList<string> Bindings,
    int RestartCount,
    int ActiveConnections,
    DateTimeOffset? ReadyAtUtc,
    string? LastFailure);

/// <summary>
/// Represents one physical receiver status snapshot.
/// </summary>
internal sealed record ReceiverStatusResponse(
    string PhysicalDeviceId,
    int Index,
    bool Active,
    bool Streaming,
    bool Locked,
    string? Channel,
    string? VirtualChannel,
    string? TargetUrl,
    string? LockType,
    int? SignalStrength,
    int? SignalToNoiseQuality,
    int? SymbolErrorQuality,
    long? BitsPerSecond,
    int? PacketsPerSecond);

/// <summary>
/// Represents the active stream collection.
/// </summary>
internal sealed record ActiveStreamsStatusResponse(int Count, IReadOnlyList<ActiveStreamStatusResponse> Items);

/// <summary>Represents shared producer and physical-capacity state.</summary>
internal sealed record StreamingGraphStatusResponse(
    IReadOnlyList<StreamingGraphNodeStatusResponse> Nodes,
    IReadOnlyList<StreamingCapacityStatusResponse> Capacity);

/// <summary>Represents one active shared acquisition or transform node.</summary>
internal sealed record StreamingGraphNodeStatusResponse(
    string Kind,
    string Key,
    int ReceiverCount,
    bool Running,
    bool Warm,
    long TotalSubscriptions,
    string NodeId,
    string? ParentNodeId,
    string? Stage);

/// <summary>Represents physical tuner capacity consumed by shared acquisitions.</summary>
internal sealed record StreamingCapacityStatusResponse(
    string PhysicalDeviceId,
    int? Capacity,
    int ActiveAcquisitions,
    int Receivers);

/// <summary>
/// Represents one active stream and its media tracks.
/// </summary>
internal sealed record ActiveStreamStatusResponse(
    string SessionId,
    string? ClientId,
    string? AuthenticatedClientId,
    string? AuthenticatedClientName,
    string? ClientAddress,
    string Channel,
    string Format,
    DateTime StartedAtUtc,
    long DurationSeconds,
    long? SourceBitRate,
    IReadOnlyList<ActiveStreamTrackStatusResponse> Tracks,
    ActiveStreamMediaStatusResponse? PhysicalInput,
    ActiveStreamMediaStatusResponse? PhysicalOutput,
    ActiveStreamMediaStatusResponse? VirtualOutput);

/// <summary>Represents media tracks at one active-stream processing boundary.</summary>
internal sealed record ActiveStreamMediaStatusResponse(long? BitRate, IReadOnlyList<ActiveStreamMediaTrackStatusResponse> Tracks);

/// <summary>Represents one media track at an active-stream processing boundary.</summary>
internal sealed record ActiveStreamMediaTrackStatusResponse(
    int Index,
    string Type,
    string Codec,
    long? BitRate,
    int? Width,
    int? Height,
    int? Channels,
    int? SampleRate,
    string? Language,
    string? Title,
    string? RenditionTitle);

/// <summary>
/// Represents one source-to-output media track mapping.
/// </summary>
internal sealed record ActiveStreamTrackStatusResponse(
    int SourceIndex,
    string Type,
    string SourceCodec,
    string OutputCodec,
    long? SourceBitRate,
    long? OutputBitRate,
    int? SourceWidth,
    int? SourceHeight,
    int? SourceChannels,
    int? OutputChannels,
    int? OutputSampleRate,
    string? Language,
    string? Title,
    bool Default,
    bool Forced,
    bool HearingImpaired,
    bool ClosedCaptions,
    bool Selected,
    string? SubtitlePresentation);

/// <summary>
/// Provides process identity and lifetime values for status responses.
/// </summary>
internal sealed class StatusApiRuntime
{
    /// <summary>
    /// Initializes runtime status using the supplied process instance identifier.
    /// </summary>
    internal StatusApiRuntime(string instanceId)
    {
        InstanceId = instanceId;
        StartedAtUtc = DateTime.UtcNow;
        Version = typeof(StatusApiRuntime).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? typeof(StatusApiRuntime).Assembly.GetName().Version?.ToString(3) ?? "unknown";
    }

    /// <summary>
    /// Unique identifier for this application process.
    /// </summary>
    internal string InstanceId { get; }

    /// <summary>
    /// UTC time at which this process initialized status reporting.
    /// </summary>
    internal DateTime StartedAtUtc { get; }

    /// <summary>
    /// Informational application version.
    /// </summary>
    internal string Version { get; }
}

/// <summary>
/// Aggregates passive application state into the versioned status response.
/// </summary>
internal sealed class StatusApiService
{
    private readonly ILineupGuideReader _guideReader;
    private readonly ILineupSettingsService _settings;
    private readonly IWebSettingsService _webSettings;
    private readonly IPhysicalDeviceStatusService _deviceStatuses;
    private readonly IAutoFetchStateService _autoFetch;
    private readonly IActiveStreamRegistry _activeStreams;
    private readonly StatusApiRuntime _runtime;
    private readonly ILogger<StatusApiService> _logger;
    private readonly IXmltvPublicationStore _publications;
    private readonly LineupStartupState _startupState;
    private readonly IHdHomeRunProxyProfileProvider? _profiles;
    private readonly IStreamingSessionGraph? _streamingGraph;
    private readonly IStreamingAdapterRegistry? _adapters;
    private readonly IVirtualOutputRuntimeStatus? _virtualOutputRuntime;
    private readonly StreamingTopologyStore? _topologyStore;
    private readonly DeviceGuideStore? _guideStore;
    private readonly IStreamingFlowSnapshotService? _streamingFlows;

    /// <summary>
    /// Initializes the status aggregation service.
    /// </summary>
    public StatusApiService(
        ILineupGuideReader guideReader,
        ILineupSettingsService settings,
        IWebSettingsService webSettings,
        IPhysicalDeviceStatusService deviceStatuses,
        IAutoFetchStateService autoFetch,
        IActiveStreamRegistry activeStreams,
        StatusApiRuntime runtime,
        ILogger<StatusApiService> logger,
        IXmltvPublicationStore publications,
        LineupStartupState startupState,
        IHdHomeRunProxyProfileProvider? profiles = null,
        IStreamingSessionGraph? streamingGraph = null,
        IStreamingAdapterRegistry? adapters = null,
        IVirtualOutputRuntimeStatus? virtualOutputRuntime = null,
        StreamingTopologyStore? topologyStore = null,
        DeviceGuideStore? guideStore = null,
        IStreamingFlowSnapshotService? streamingFlows = null)
    {
        _guideReader = guideReader;
        _settings = settings;
        _webSettings = webSettings;
        _deviceStatuses = deviceStatuses;
        _autoFetch = autoFetch;
        _activeStreams = activeStreams;
        _runtime = runtime;
        _logger = logger;
        _publications = publications;
        _startupState = startupState;
        _adapters = adapters;
        _profiles = profiles;
        _streamingGraph = streamingGraph;
        _virtualOutputRuntime = virtualOutputRuntime;
        _topologyStore = topologyStore;
        _guideStore = guideStore;
        _streamingFlows = streamingFlows;
    }

    /// <summary>
    /// Builds the current passive Lineup status snapshot.
    /// </summary>
    internal async Task<LineupStatusResponse> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var generatedAtUtc = DateTime.UtcNow;
        var settings = _settings.Settings;
        var webSettings = _webSettings.Settings;
        var guide = _startupState.IsFactoryResetRequired
            ? CreateRestrictedGuideStatus(settings)
            : await GetGuideStatusAsync(settings, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var virtualDevices = _startupState.IsFactoryResetRequired || _profiles is null
            ? []
            : await _profiles.GetProfilesAsync(cancellationToken);
        var channels = _startupState.IsFactoryResetRequired
            ? new ChannelsStatusResponse("unavailable", "Channel state is unavailable until factory reset.", 0, [], [])
            : await GetChannelsStatusAsync(settings.Streaming, webSettings, cancellationToken);

        long uptimeSeconds = Math.Max(0, (long)(generatedAtUtc - _runtime.StartedAtUtc).TotalSeconds);
        return new LineupStatusResponse(
            "3.0",
            generatedAtUtc,
            new ApplicationStatusResponse(
                _runtime.Version,
                _runtime.InstanceId,
                webSettings.IsSetupComplete,
                _runtime.StartedAtUtc,
                uptimeSeconds,
                _startupState.IsFactoryResetRequired ? "factoryResetRequired" : "ready",
                _startupState.Reason),
            GetConfigurationStatus(settings, webSettings),
            guide,
            channels,
            GetPhysicalDeviceStatuses(webSettings),
            GetVirtualDeviceStatuses(settings.Streaming, webSettings, virtualDevices),
            GetVirtualOutputWorkerStatuses(webSettings),
            GetReceiverStatuses(webSettings),
            GetActiveStreamStatuses(webSettings, generatedAtUtc),
            GetStreamingGraphStatus(),
            GetStreamingFlowsStatus(webSettings));
    }

    private ConfigurationStatusResponse GetConfigurationStatus(LineupSettings settings, WebSettings webSettings)
    {
        var configuration = settings.Streaming;
        return new ConfigurationStatusResponse(
            new CoreSettingsStatusResponse(
                settings.SchemaVersion,
                settings.TargetDays,
                settings.EpgHistoryRetentionHours,
                settings.AutoFetchInterval.TotalSeconds,
                settings.RefreshChannelsBeforeGuideFetch,
                ToUtc(settings.NextAutoFetchTime),
                settings.DeviceRefreshIntervalMinutes,
                settings.TunerRefreshIntervalSeconds,
                settings.AutoGenerateXmltv,
                !string.IsNullOrWhiteSpace(settings.XmltvOutputPath),
                settings.TimeZoneId,
                FormatName(settings.ProtectedContentMode),
                FormatName(settings.DisabledChannelMode),
                settings.MaximumConcurrentStreams,
                ToUtc(settings.LastAutoFetchTime)),
            new WebSettingsStatusResponse(
                webSettings.SchemaVersion,
                webSettings.Theme,
                webSettings.ActiveStreamRefreshIntervalSeconds,
                webSettings.EnableNetworkDiscovery,
                webSettings.EnableFileLogging,
                webSettings.OverrideLoggingDefaults,
                webSettings.ApplicationLogLevel,
                webSettings.LogCategoryOverrides.Select(item => new LogCategoryStatusResponse(item.Category, item.Level)).ToArray(),
                webSettings.FileLogLevel,
                webSettings.FileLogRetentionDays,
                webSettings.FileLogSizeLimitMb,
                new ApiPrivacyStatusResponse(webSettings.RedactApiClientAddresses, webSettings.RedactApiDeviceAddresses, webSettings.RedactApiUrls)),
            new StreamingConfigurationStatusResponse(
                configuration.SchemaVersion,
                configuration.KeepWarmPeriodSeconds,
                ToTranscodeProfile(configuration.PhysicalDefaults),
                ToTranscodeProfile(configuration.VirtualDefaults),
                configuration.PhysicalDevices.Select(device => new PhysicalDeviceConfigurationStatusResponse(
                    device.Id.ToString(),
                    device.Name,
                    device.Enabled,
                    device.Order,
                    GetAdapterStatus(device.Adapter, _adapters is null ? null : _adapters.GetPhysicalSettings, webSettings),
                    ToTranscodeOverrides(device.TranscodeOverrides),
                    ToTranscodeProfile(device.TranscodeOverrides.Apply(configuration.PhysicalDefaults)))).ToArray(),
                configuration.GuideSources.Select(source => new GuideSourceConfigurationStatusResponse(
                    source.Id.ToString(),
                    source.Name,
                    source.Enabled,
                    GetAdapterStatus(source.Adapter, _adapters is null ? null : _adapters.GetGuideSettings, webSettings),
                    ToSubscription(source.Subscription))).ToArray(),
                configuration.VirtualDevices.Select(device => new VirtualDeviceConfigurationStatusResponse(
                    device.Id.ToString(),
                    device.Name,
                    device.Enabled,
                    GetAdapterStatus(device.Adapter, _adapters is null ? null : _adapters.GetVirtualSettings, webSettings),
                    ToSubscription(device.Subscription),
                    ToTranscodeOverrides(device.TranscodeOverrides),
                    ToTranscodeProfile(device.TranscodeOverrides.Apply(configuration.VirtualDefaults)),
                    ToHosting(device.Hosting, webSettings),
                    GetAccessGrants(device))).ToArray(),
                configuration.GuideOutputs.Select(output => new GuideOutputConfigurationStatusResponse(
                    output.Id.ToString(),
                    output.Name,
                    output.Key,
                    output.Enabled,
                    output.IsBuiltIn,
                    GetAdapterStatus(output.Adapter, _adapters is null ? null : _adapters.GetGuideOutputSettings, webSettings),
                    FormatName(output.VirtualDeviceSelectionMode),
                    output.SelectedVirtualDeviceIds.Select(id => id.ToString()).OrderBy(id => id, StringComparer.Ordinal).ToArray(),
                    FormatName(output.GuideSourceSelectionMode),
                    output.SelectedGuideSourceIds.Select(id => id.ToString()).OrderBy(id => id, StringComparer.Ordinal).ToArray())).ToArray(),
                configuration.VirtualOutputSecurityPolicies.Select(policy => new VirtualOutputSecurityPolicyStatusResponse(
                    policy.Key,
                    policy.Name,
                    !string.IsNullOrWhiteSpace(policy.CertificateReference),
                    FormatName(policy.ClientCertificateMode),
                    webSettings.RedactApiDeviceAddresses ? null : policy.AllowedNetworks,
                    policy.MaximumConnections,
                    policy.RequestsPerMinutePerClient,
                    policy.AllowForwardedHeaders,
                    webSettings.RedactApiDeviceAddresses ? null : policy.TrustedProxies)).ToArray(),
                configuration.VirtualOutputWorkerPolicies.Select(policy => new VirtualOutputWorkerPolicyStatusResponse(
                    policy.Key,
                    policy.Name,
                    policy.MaximumRestarts,
                    policy.RestartWindowSeconds,
                    policy.DrainTimeoutSeconds,
                    policy.LeaseGracePeriodSeconds,
                    policy.MaximumOutstandingBrokerCalls,
                    policy.MaximumMemoryMib,
                    policy.MaximumCpuPercent)).ToArray()));
    }

    private AdapterConfigurationStatusResponse GetAdapterStatus(
        AdapterConfiguration configuration,
        Func<string, IAdapterSettingsProvider>? getProvider,
        WebSettings settings)
    {
        if (getProvider is null)
        {
            return new AdapterConfigurationStatusResponse(configuration.Kind, "Adapter registry is unavailable.", []);
        }

        try
        {
            var provider = getProvider(configuration.Kind);
            var snapshot = provider.GetSettings(configuration);
            var values = snapshot.Values.ToDictionary(value => value.Key, StringComparer.Ordinal);
            return new AdapterConfigurationStatusResponse(
                configuration.Kind,
                null,
                provider.SettingsDescriptor.Fields.Select(field =>
                {
                    values.TryGetValue(field.Key, out var value);
                    return new AdapterSettingStatusResponse(
                        field.Key,
                        FormatName(field.Kind),
                        value?.HasValue ?? false,
                        ShouldRedactAdapterSetting(field, settings) ? null : value?.Value);
                }).ToArray());
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or JsonException)
        {
            _logger.LogWarning(ex, "Unable to read {AdapterKind} adapter settings for the status API", configuration.Kind);
            return new AdapterConfigurationStatusResponse(configuration.Kind, "Adapter settings are unavailable.", []);
        }
    }

    private static bool ShouldRedactAdapterSetting(AdapterSettingDescriptor field, WebSettings settings)
    {
        if (field.Kind is AdapterSettingKind.Secret or AdapterSettingKind.GeneratedSecret or AdapterSettingKind.FilePath or AdapterSettingKind.DirectoryPath)
        {
            return true;
        }

        var identity = $"{field.Key} {field.Label}";
        return (settings.RedactApiUrls && (field.Kind == AdapterSettingKind.Uri || identity.Contains("url", StringComparison.OrdinalIgnoreCase) || identity.Contains("uri", StringComparison.OrdinalIgnoreCase)))
            || (settings.RedactApiDeviceAddresses
                && (identity.Contains("address", StringComparison.OrdinalIgnoreCase)
                    || identity.Contains("hostname", StringComparison.OrdinalIgnoreCase)
                    || identity.Contains("host name", StringComparison.OrdinalIgnoreCase)));
    }

    private AccessGrantsStatusResponse GetAccessGrants(VirtualDeviceDefinition device)
    {
        if (_adapters is null)
        {
            return new AccessGrantsStatusResponse("unavailable", "Adapter registry is unavailable.", []);
        }

        try
        {
            if (_adapters.GetVirtual(device.Adapter.Kind) is not IAdapterAccessGrantProvider grants)
            {
                return new AccessGrantsStatusResponse("notSupported", null, []);
            }

            return new AccessGrantsStatusResponse(
                "available",
                null,
                grants.GetAccessGrants(device.Adapter).Select(grant => new AccessGrantStatusResponse(
                    grant.Id.ToString("D"),
                    grant.Name,
                    grant.CreatedAtUtc,
                    grant.RotatedAtUtc,
                    grant.ExpiresAtUtc,
                    grant.RevokedAtUtc)).ToArray());
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or JsonException)
        {
            _logger.LogWarning(ex, "Unable to read access grants for virtual device {VirtualDeviceId}", device.Id);
            return new AccessGrantsStatusResponse("unavailable", "Access-grant state is unavailable.", []);
        }
    }

    private static PhysicalDeviceSubscriptionStatusResponse ToSubscription(PhysicalDeviceSubscription subscription) =>
        new(
            FormatName(subscription.Mode),
            subscription.SelectedDeviceIds.Select(id => id.ToString()).OrderBy(id => id, StringComparer.Ordinal).ToArray());

    private static VirtualOutputHostingStatusResponse? ToHosting(VirtualOutputHostingDefinition? hosting, WebSettings settings) =>
        hosting is null
            ? null
            : new VirtualOutputHostingStatusResponse(
                FormatName(hosting.ExecutionMode),
                hosting.WorkerPolicyKey,
                hosting.Bindings.Select(binding => new VirtualOutputBindingStatusResponse(
                    FormatName(binding.Transport),
                    settings.RedactApiDeviceAddresses ? null : binding.Addresses,
                    binding.Port,
                    settings.RedactApiUrls ? null : NullIfEmpty(binding.PublicBaseUri),
                    binding.SecurityPolicyKey)).ToArray());

    private static TranscodeProfileStatusResponse ToTranscodeProfile(PhysicalTranscodeProfile profile) =>
        new(
            profile.ConvertHevcToH264,
            FormatName(profile.Deinterlace),
            FormatName(profile.Audio),
            FormatName(profile.Ac4Audio),
            FormatName(profile.VideoPreset),
            profile.VideoQuality,
            profile.MaximumVideoBitRateMbps);

    private static TranscodeProfileStatusResponse ToTranscodeProfile(VirtualTranscodeProfile profile) =>
        new(
            profile.ConvertHevcToH264,
            FormatName(profile.Deinterlace),
            FormatName(profile.Audio),
            FormatName(profile.Ac4Audio),
            FormatName(profile.VideoPreset),
            profile.VideoQuality,
            profile.MaximumVideoBitRateMbps);

    private static TranscodeOverridesStatusResponse ToTranscodeOverrides(PhysicalTranscodeOverrides overrides) =>
        new(
            overrides.ConvertHevcToH264,
            overrides.Deinterlace is null ? null : FormatName(overrides.Deinterlace.Value),
            overrides.Audio is null ? null : FormatName(overrides.Audio.Value),
            overrides.Ac4Audio is null ? null : FormatName(overrides.Ac4Audio.Value),
            overrides.VideoPreset is null ? null : FormatName(overrides.VideoPreset.Value),
            overrides.VideoQuality,
            overrides.MaximumVideoBitRateMbps);

    private static TranscodeOverridesStatusResponse ToTranscodeOverrides(VirtualTranscodeOverrides overrides) =>
        new(
            overrides.ConvertHevcToH264,
            overrides.Deinterlace is null ? null : FormatName(overrides.Deinterlace.Value),
            overrides.Audio is null ? null : FormatName(overrides.Audio.Value),
            overrides.Ac4Audio is null ? null : FormatName(overrides.Ac4Audio.Value),
            overrides.VideoPreset is null ? null : FormatName(overrides.VideoPreset.Value),
            overrides.VideoQuality,
            overrides.MaximumVideoBitRateMbps);

    private async Task<ChannelsStatusResponse> GetChannelsStatusAsync(StreamingConfiguration configuration, WebSettings settings, CancellationToken cancellationToken)
    {
        if (_topologyStore is null || _guideStore is null)
        {
            return new ChannelsStatusResponse("unavailable", "Channel stores are unavailable.", 0, [], []);
        }

        try
        {
            var channels = await _topologyStore.ReadActiveAsync(cancellationToken);
            var scopes = configuration.GuideSources
                .Where(source => source.Enabled)
                .Select(source => GuideSourceSubscriptionResolver.Resolve(source, configuration.PhysicalDevices, _adapters!.GetGuide(source.Adapter.Kind)))
                .Where(scope => scope.PhysicalDeviceIds.Count > 0)
                .ToArray();
            var bindings = (await _guideStore.ReadBindingStatesAsync(cancellationToken, scopes)).ToDictionary(binding => binding.SourceChannelId);
            var items = channels.Select(channel =>
            {
                bindings.TryGetValue(channel.Source.Id, out var binding);
                return new PhysicalChannelStatusResponse(
                    channel.Source.Id.PhysicalDeviceId.ToString(),
                    channel.Source.Id.ProviderKey,
                    channel.Source.ProviderNumber,
                    channel.Source.ProviderName,
                    channel.EffectiveNumber,
                    channel.EffectiveName,
                    channel.NumberOverride,
                    channel.NameOverride,
                    channel.EquivalenceGroupId?.ToString("D"),
                    channel.IsEnabled,
                    FilterDetails(channel.Source.Metadata, settings),
                    ToUtc(channel.FirstSeenUtc) ?? channel.FirstSeenUtc,
                    ToUtc(channel.LastSeenUtc) ?? channel.LastSeenUtc,
                    binding is null
                        ? null
                        : new GuideBindingStatusResponse(
                            FormatName(binding.State),
                            binding.SelectedGuideChannel is null ? null : ToGuideChannel(binding.SelectedGuideChannel, settings),
                            binding.MatchingCandidates.Select(candidate => ToGuideChannel(candidate, settings)).ToArray()));
            }).ToArray();
            var projections = configuration.VirtualDevices.Select(device =>
            {
                var resolution = ChannelTopologyResolver.Resolve(device, configuration.PhysicalDevices, channels);
                return new VirtualChannelProjectionStatusResponse(
                    device.Id.ToString(),
                    resolution.Channels.Count,
                    resolution.Channels.Select(channel => new ResolvedVirtualChannelStatusResponse(
                        channel.Number,
                        channel.Name,
                        channel.Sources.Select(ToSourceChannel).ToArray())).ToArray(),
                    resolution.Conflicts.Select(conflict => new ChannelConflictStatusResponse(
                        conflict.Number,
                        ToSourceChannel(conflict.PublishedSource),
                        ToSourceChannel(conflict.ConflictingSource))).ToArray());
            }).ToArray();
            return new ChannelsStatusResponse("available", null, items.Length, items, projections);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to build channel section for the status API");
            return new ChannelsStatusResponse("unavailable", "Channel state is unavailable.", 0, [], []);
        }
    }

    private static GuideChannelStatusResponse ToGuideChannel(GuideChannel channel, WebSettings settings) =>
        new(
            channel.Id.GuideSourceId.ToString(),
            channel.Id.ProviderKey,
            channel.Number,
            channel.Name,
            channel.Affiliate,
            settings.RedactApiUrls ? null : channel.ImageUrl,
            channel.IsProtected,
            channel.IsFavorite,
            !string.IsNullOrWhiteSpace(channel.SupplementalXml));

    private static SourceChannelStatusResponse ToSourceChannel(SourceChannelId source) =>
        new(source.PhysicalDeviceId.ToString(), source.ProviderKey);

    private StreamingFlowsStatusResponse GetStreamingFlowsStatus(WebSettings settings)
    {
        var snapshot = _streamingFlows?.GetSnapshot();
        if (snapshot is null)
        {
            return new StreamingFlowsStatusResponse(0, 0, 0, [], []);
        }

        return new StreamingFlowsStatusResponse(
            snapshot.ActiveConsumerCount,
            snapshot.ActiveProducerCount,
            snapshot.WarmProducerCount,
            snapshot.Nodes.Select(node => new StreamingFlowNodeStatusResponse(
                node.Id,
                FormatName(node.Lane),
                node.Label,
                node.Subtitle,
                FormatName(node.State),
                FilterFlowDetails(node.Details, settings),
                node.ActiveChannels,
                node.ActiveSessionCount)).ToArray(),
            snapshot.ActivePaths.Select(path => new StreamingFlowPathStatusResponse(
                path.Id,
                path.SessionId,
                settings.RedactApiClientAddresses ? null : path.Endpoint,
                FormatName(path.Format),
                path.Channel,
                ToUtc(path.StartedAtUtc) ?? path.StartedAtUtc,
                path.IsSynthetic,
                path.IsShared,
                path.SharedReceiverCount,
                path.Stages.Select(stage => new StreamingFlowStageStatusResponse(
                    FormatName(stage.Kind),
                    stage.Label,
                    stage.Subtitle,
                    FilterFlowDetails(stage.Details, settings),
                    ToMediaStatus(stage.InputMedia),
                    ToMediaStatus(stage.OutputMedia))).ToArray())).ToArray());
    }

    private static IReadOnlyDictionary<string, string> FilterFlowDetails(IReadOnlyList<StreamingFlowDetail> details, WebSettings settings) =>
        details.Where(detail => !ShouldRedactDetail(settings, detail.Label))
            .ToDictionary(detail => detail.Label, detail => detail.Value, StringComparer.Ordinal);

    private IReadOnlyList<VirtualOutputRuntimeStatusResponse> GetVirtualOutputWorkerStatuses(WebSettings settings) =>
        _virtualOutputRuntime?.GetSnapshots()
            .Select(snapshot => new VirtualOutputRuntimeStatusResponse(
                snapshot.VirtualDeviceId.ToString(),
                snapshot.State.ToString(),
                snapshot.Generation,
                snapshot.ProcessId,
                snapshot.ProtocolVersion,
                settings.RedactApiUrls ? [] : snapshot.Bindings,
                snapshot.RestartCount,
                snapshot.ActiveConnections,
                snapshot.ReadySinceUtc,
                snapshot.LastFailure))
            .ToArray()
        ?? [];

    private StreamingGraphStatusResponse GetStreamingGraphStatus()
    {
        var diagnostics = _streamingGraph?.GetDiagnostics();
        return diagnostics is null
            ? new StreamingGraphStatusResponse([], [])
            : new StreamingGraphStatusResponse(
                diagnostics.Nodes.Select(node => new StreamingGraphNodeStatusResponse(
                    node.Kind,
                    node.Key,
                    node.ReceiverCount,
                    node.IsRunning,
                    node.IsWarm,
                    node.TotalSubscriptions,
                    node.NodeId,
                    node.ParentNodeId,
                    node.Stage)).ToArray(),
                diagnostics.Capacity.Select(capacity => new StreamingCapacityStatusResponse(
                    capacity.DeviceId.ToString(),
                    capacity.Capacity,
                    capacity.ActiveAcquisitions,
                    capacity.Receivers)).ToArray());
    }

    private GuideStatusResponse CreateRestrictedGuideStatus(LineupSettings settings) =>
        new(
            "unavailable",
            "Guide state is unavailable until factory reset.",
            null,
            null,
            null,
            null,
            null,
            null,
            GetAutoFetchStatus(),
            GetXmltvStatus(settings),
            new GuideSourceImportsStatusResponse("unavailable", "Guide import state is unavailable until factory reset.", []),
            GetGuideOutputStatuses(settings.Streaming, _webSettings.Settings));

    private async Task<GuideStatusResponse> GetGuideStatusAsync(LineupSettings settings, CancellationToken cancellationToken)
    {
        var autoFetch = GetAutoFetchStatus();
        var xmltv = GetXmltvStatus(settings);
        var imports = await GetGuideSourceImportStatusesAsync(cancellationToken);
        var outputs = GetGuideOutputStatuses(settings.Streaming, _webSettings.Settings);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var statistics = await _guideReader.GetStatisticsAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return new GuideStatusResponse(
                "available",
                null,
                statistics.ChannelCount,
                statistics.ProgramCount,
                statistics.EarliestProgramStartUtc?.UtcDateTime,
                statistics.LatestProgramEndUtc?.UtcDateTime,
                statistics.TotalTimeSpan?.TotalHours,
                statistics.SafeCoverageEndUtc?.UtcDateTime,
                autoFetch,
                xmltv,
                imports,
                outputs);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to build guide section for the status API");
            return new GuideStatusResponse("unavailable", "Guide state is unavailable.", null, null, null, null, null, null, autoFetch, xmltv, imports, outputs);
        }
    }

    private async Task<GuideSourceImportsStatusResponse> GetGuideSourceImportStatusesAsync(CancellationToken cancellationToken)
    {
        if (_guideStore is null)
        {
            return new GuideSourceImportsStatusResponse("unavailable", "Guide store is unavailable.", []);
        }

        try
        {
            var imports = await _guideStore.ReadImportStatesAsync(cancellationToken);
            return new GuideSourceImportsStatusResponse(
                "available",
                null,
                imports.Select(importState => new GuideSourceImportStatusResponse(
                    importState.GuideSourceId.ToString(),
                    importState.FetchedAtUtc,
                    importState.CommittedAtUtc,
                    importState.CoverageStartUtc.UtcDateTime,
                    importState.CoverageEndUtc.UtcDateTime,
                    importState.ChannelCount,
                    importState.ProgramCount,
                    importState.HasSupplementalMetadata)).ToArray());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to build guide-source import metadata for the status API");
            return new GuideSourceImportsStatusResponse("unavailable", "Guide import state is unavailable.", []);
        }
    }

    private IReadOnlyList<GuideOutputStatusResponse> GetGuideOutputStatuses(StreamingConfiguration configuration, WebSettings settings)
    {
        return configuration.GuideOutputs.Select(output =>
        {
            try
            {
                if (_adapters is null)
                {
                    return new GuideOutputStatusResponse(output.Id.ToString(), output.Name, output.Key, output.Enabled, false, null, null, "Adapter registry is unavailable.");
                }

                var destination = _adapters.GetGuideOutput(output.Adapter.Kind).ResolveDestination(output.Adapter);
                var available = output.Enabled && _publications.Exists(destination.Path);
                var downloadUrl = settings.RedactApiUrls || !output.Enabled
                    ? null
                    : $"/api/guide-outputs/{Uri.EscapeDataString(output.Key)}";
                return new GuideOutputStatusResponse(
                    output.Id.ToString(),
                    output.Name,
                    output.Key,
                    output.Enabled,
                    available,
                    downloadUrl,
                    available ? _publications.GetLastWriteTimeUtc(destination.Path) : null,
                    null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
            {
                _logger.LogWarning(ex, "Unable to read guide-output state for {GuideOutputId}", output.Id);
                return new GuideOutputStatusResponse(output.Id.ToString(), output.Name, output.Key, output.Enabled, false, null, null, "Guide output state is unavailable.");
            }
        }).ToArray();
    }

    private AutoFetchStatusResponse GetAutoFetchStatus()
    {
        var progress = _autoFetch.CurrentProgress;
        return new AutoFetchStatusResponse(
            _autoFetch.IsEnabled,
            _autoFetch.IsRunning,
            progress?.Status.ToString().ToLowerInvariant(),
            progress?.Message,
            progress?.ErrorMessage == null ? null : "Automatic guide fetch failed.",
            progress?.PercentComplete,
            progress?.FetchCount,
            progress?.TotalProgramsFetched,
            progress?.TotalChannelsFetched,
            ToUtc(progress?.CurrentEndTime),
            progress?.TargetEndTime == default ? null : ToUtc(progress?.TargetEndTime),
            ToUtc(_autoFetch.LastFetchTime),
            ToUtc(_autoFetch.NextFetchTime));
    }

    private XmltvStatusResponse GetXmltvStatus(LineupSettings settings)
    {
        if (!settings.AutoGenerateXmltv)
        {
            return new XmltvStatusResponse(false, false, null, null);
        }

        try
        {
            var builtIn = settings.Streaming.GuideOutputs.FirstOrDefault(output => output.IsBuiltIn && output.Enabled);
            var path = builtIn is not null && _adapters is not null
                ? _adapters.GetGuideOutput(builtIn.Adapter.Kind).ResolveDestination(builtIn.Adapter).Path
                : settings.XmltvOutputPath;
            var available = _publications.Exists(path);
            return new XmltvStatusResponse(
                true,
                available,
                _webSettings.Settings.RedactApiUrls || !available ? null : "/api/xmltv",
                available ? _publications.GetLastWriteTimeUtc(path) : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new XmltvStatusResponse(true, false, null, null);
        }
    }

    private IReadOnlyList<PhysicalDeviceStatusResponse> GetPhysicalDeviceStatuses(WebSettings settings)
    {
        return _deviceStatuses.Devices.Select(device =>
        {
            var details = device.Status?.Details
                .Where(detail => !ShouldRedactDetail(settings, detail.Key))
                .ToDictionary(detail => detail.Key, detail => detail.Value, StringComparer.Ordinal)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
            return new PhysicalDeviceStatusResponse(
                device.Device.Id.ToString(),
                device.Device.Kind,
                device.Device.Name,
                device.Error is null ? "available" : "error",
                device.Error,
                device.Device.Capacity.TunerCount,
                device.SupportsReceiverControl,
                device.SupportsRestart,
                details,
                device.Status?.ObservedAtUtc.UtcDateTime);
        }).ToArray();
    }

    private ReadOnlyCollection<VirtualDeviceStatusResponse> GetVirtualDeviceStatuses(
        StreamingConfiguration configuration,
        WebSettings settings,
        IReadOnlyList<HdHomeRunProxyProfileSnapshot> profiles)
    {
        var profilesByDevice = profiles.ToDictionary(profile => profile.VirtualDevice.Id);
        var workersByDevice = _virtualOutputRuntime?.GetSnapshots().ToDictionary(snapshot => snapshot.VirtualDeviceId)
            ?? new Dictionary<VirtualDeviceId, VirtualOutputRuntimeSnapshot>();
        return configuration.VirtualDevices.Select(device =>
        {
            profilesByDevice.TryGetValue(device.Id, out var profile);
            workersByDevice.TryGetValue(device.Id, out var worker);
            var available = device.Enabled && (device.Hosting is null || worker?.State == VirtualOutputRuntimeState.Healthy);
            return new VirtualDeviceStatusResponse(
                profile?.DeviceIdText ?? device.Id.ToString(),
                device.Enabled,
                profile?.IsPrimary ?? false,
                available,
                worker?.LastFailure,
                profile?.FriendlyName ?? device.Name,
                profile?.TunerCount,
                profile?.TunerCountCap,
                null,
                settings.RedactApiUrls ? null : NullIfEmpty(profile?.AdvertisedBaseUrl),
                null,
                null);
        }).ToArray().AsReadOnly();
    }

    private ReadOnlyCollection<ReceiverStatusResponse> GetReceiverStatuses(WebSettings settings)
    {
        return _deviceStatuses.Devices.SelectMany(device => device.Status?.Receivers.Select(receiver => new ReceiverStatusResponse(
            device.Device.Id.ToString(),
            receiver.Index,
            receiver.IsActive,
            receiver.IsStreaming,
            receiver.HasLock,
            receiver.Channel,
            receiver.VirtualChannel,
            settings.RedactApiUrls ? null : receiver.Target,
            receiver.LockType,
            receiver.SignalStrength,
            receiver.SignalQuality,
            receiver.SymbolQuality,
            receiver.BitsPerSecond,
            receiver.PacketsPerSecond)) ?? []).ToArray().AsReadOnly();
    }

    private static IReadOnlyDictionary<string, string> FilterDetails(IReadOnlyDictionary<string, string> details, WebSettings settings) =>
        details.Where(detail => !ShouldRedactDetail(settings, detail.Key))
            .ToDictionary(detail => detail.Key, detail => detail.Value, StringComparer.Ordinal);

    private static bool ShouldRedactDetail(WebSettings settings, string key) =>
        (settings.RedactApiDeviceAddresses
            && (key.Contains("address", StringComparison.OrdinalIgnoreCase)
                || key.Contains("hostname", StringComparison.OrdinalIgnoreCase)))
        || (settings.RedactApiUrls
            && (key.Contains("url", StringComparison.OrdinalIgnoreCase)
                || key.Contains("uri", StringComparison.OrdinalIgnoreCase)));

    private ActiveStreamsStatusResponse GetActiveStreamStatuses(WebSettings settings, DateTime generatedAtUtc)
    {
        var streams = _activeStreams.GetActiveStreams().Select(stream => new ActiveStreamStatusResponse(
            stream.SessionId,
            stream.ClientId,
            stream.AuthenticatedClientId,
            stream.AuthenticatedClientName,
            settings.RedactApiClientAddresses ? null : stream.ClientAddress,
            stream.Channel,
            FormatName(stream.Format),
            ToUtc(stream.StartedAtUtc) ?? stream.StartedAtUtc,
            Math.Max(0, (long)(generatedAtUtc - stream.StartedAtUtc).TotalSeconds),
            stream.SourceBitRate,
            stream.Tracks.Select(track => new ActiveStreamTrackStatusResponse(
                track.SourceIndex,
                FormatName(track.Type),
                track.SourceCodec,
                track.OutputCodec,
                track.SourceBitRate,
                track.OutputBitRate,
                track.SourceWidth,
                track.SourceHeight,
                track.SourceChannels,
                track.OutputChannels,
                track.OutputSampleRate,
                track.Language,
                track.Title,
                track.IsDefault,
                track.IsForced,
                track.IsHearingImpaired,
                track.HasClosedCaptions,
                track.IsSelected,
                track.SubtitlePresentation is null ? null : FormatName(track.SubtitlePresentation.Value))).ToArray(),
            ToMediaStatus(stream.PhysicalInput),
            ToMediaStatus(stream.PhysicalOutput),
            ToMediaStatus(stream.VirtualOutput))).ToArray();
        return new ActiveStreamsStatusResponse(streams.Length, streams);
    }

    private static ActiveStreamMediaStatusResponse? ToMediaStatus(ActiveStreamMediaSnapshot? media) =>
        media is null
            ? null
            : new ActiveStreamMediaStatusResponse(
                media.BitRate,
                media.Tracks.Select(track => new ActiveStreamMediaTrackStatusResponse(
                    track.Index,
                    FormatName(track.Type),
                    track.Codec,
                    track.BitRate,
                    track.Width,
                    track.Height,
                    track.Channels,
                    track.SampleRate,
                    track.Language,
                    track.Title,
                    track.RenditionTitle)).ToArray());

    private static string FormatName<T>(T value) where T : struct, Enum
    {
        var name = value.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    private static DateTime? ToUtc(DateTime? value)
    {
        return value switch
        {
            null => null,
            { Kind: DateTimeKind.Utc } utc => utc,
            { Kind: DateTimeKind.Unspecified } unspecified => DateTime.SpecifyKind(unspecified, DateTimeKind.Utc),
            { } local => local.ToUniversalTime()
        };
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
