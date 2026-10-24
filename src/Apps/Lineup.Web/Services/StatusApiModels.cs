using System.Text.Json;

namespace Lineup.Web.Services;

/// <summary>Represents every non-secret persisted setting relevant to Lineup runtime behavior.</summary>
internal sealed record ConfigurationStatusResponse(
    CoreSettingsStatusResponse Core,
    WebSettingsStatusResponse Web,
    StreamingConfigurationStatusResponse Streaming);

/// <summary>Represents provider-neutral application settings.</summary>
internal sealed record CoreSettingsStatusResponse(
    int SchemaVersion,
    int TargetDays,
    int EpgHistoryRetentionHours,
    double AutoFetchIntervalSeconds,
    bool RefreshChannelsBeforeGuideFetch,
    DateTime? NextAutoFetchTimeUtc,
    int DeviceRefreshIntervalMinutes,
    int TunerRefreshIntervalSeconds,
    bool AutoGenerateXmltv,
    bool XmltvOutputPathConfigured,
    string TimeZoneId,
    string ProtectedContentMode,
    string DisabledChannelMode,
    int MaximumConcurrentStreams,
    DateTime? LastAutoFetchTimeUtc);

/// <summary>Represents settings owned by the Web frontend and host.</summary>
internal sealed record WebSettingsStatusResponse(
    int SchemaVersion,
    string Theme,
    string UiCulture,
    int ActiveStreamRefreshIntervalSeconds,
    bool EnableNetworkDiscovery,
    bool EnableFileLogging,
    bool OverrideLoggingDefaults,
    string ApplicationLogLevel,
    IReadOnlyList<LogCategoryStatusResponse> LogCategoryOverrides,
    string FileLogLevel,
    int FileLogRetentionDays,
    int FileLogSizeLimitMb,
    ApiPrivacyStatusResponse ApiPrivacy);

/// <summary>Represents one configured logging category override.</summary>
internal sealed record LogCategoryStatusResponse(string Category, string Level);

/// <summary>Represents status API privacy settings.</summary>
internal sealed record ApiPrivacyStatusResponse(bool RedactClientAddresses, bool RedactDeviceAddresses, bool RedactUrls);

/// <summary>Represents the complete provider-neutral streaming topology configuration.</summary>
internal sealed record StreamingConfigurationStatusResponse(
    int SchemaVersion,
    int KeepWarmPeriodSeconds,
    TranscodeProfileStatusResponse PhysicalDefaults,
    TranscodeProfileStatusResponse VirtualDefaults,
    IReadOnlyList<PhysicalDeviceConfigurationStatusResponse> PhysicalDevices,
    IReadOnlyList<GuideSourceConfigurationStatusResponse> GuideSources,
    IReadOnlyList<VirtualDeviceConfigurationStatusResponse> VirtualDevices,
    IReadOnlyList<GuideOutputConfigurationStatusResponse> GuideOutputs,
    IReadOnlyList<VirtualOutputSecurityPolicyStatusResponse> SecurityPolicies,
    IReadOnlyList<VirtualOutputWorkerPolicyStatusResponse> WorkerPolicies);

/// <summary>Represents one effective transcoding profile.</summary>
internal sealed record TranscodeProfileStatusResponse(
    bool ConvertHevcToH264,
    string Deinterlace,
    string Audio,
    string Ac4Audio,
    string VideoPreset,
    int VideoQuality,
    int MaximumVideoBitRateMbps);

/// <summary>Represents sparse transcoding overrides.</summary>
internal sealed record TranscodeOverridesStatusResponse(
    bool? ConvertHevcToH264,
    string? Deinterlace,
    string? Audio,
    string? Ac4Audio,
    string? VideoPreset,
    int? VideoQuality,
    int? MaximumVideoBitRateMbps);

/// <summary>Represents safe adapter-owned settings without secret values.</summary>
internal sealed record AdapterConfigurationStatusResponse(string Kind, string? Error, IReadOnlyList<AdapterSettingStatusResponse> Settings);

/// <summary>Represents one safe adapter setting.</summary>
internal sealed record AdapterSettingStatusResponse(string Key, string Kind, bool HasValue, JsonElement? Value);

/// <summary>Represents one configured physical tuner.</summary>
internal sealed record PhysicalDeviceConfigurationStatusResponse(
    string PhysicalDeviceId,
    string Name,
    bool Enabled,
    int Order,
    AdapterConfigurationStatusResponse Adapter,
    TranscodeOverridesStatusResponse TranscodeOverrides,
    TranscodeProfileStatusResponse EffectiveTranscodeProfile);

/// <summary>Represents one configured guide source.</summary>
internal sealed record GuideSourceConfigurationStatusResponse(
    string GuideSourceId,
    string Name,
    bool Enabled,
    AdapterConfigurationStatusResponse Adapter,
    PhysicalDeviceSubscriptionStatusResponse Subscription);

/// <summary>Represents one configured virtual device.</summary>
internal sealed record VirtualDeviceConfigurationStatusResponse(
    string VirtualDeviceId,
    string Name,
    bool Enabled,
    AdapterConfigurationStatusResponse Adapter,
    PhysicalDeviceSubscriptionStatusResponse Subscription,
    TranscodeOverridesStatusResponse TranscodeOverrides,
    TranscodeProfileStatusResponse EffectiveTranscodeProfile,
    VirtualOutputHostingStatusResponse? Hosting,
    AccessGrantsStatusResponse AccessGrants);

/// <summary>Represents one configured guide output.</summary>
internal sealed record GuideOutputConfigurationStatusResponse(
    string GuideOutputId,
    string Name,
    string Key,
    bool Enabled,
    bool IsBuiltIn,
    AdapterConfigurationStatusResponse Adapter,
    string VirtualDeviceSelectionMode,
    IReadOnlyList<string> SelectedVirtualDeviceIds,
    string GuideSourceSelectionMode,
    IReadOnlyList<string> SelectedGuideSourceIds);

/// <summary>Represents a physical-device subscription.</summary>
internal sealed record PhysicalDeviceSubscriptionStatusResponse(string Mode, IReadOnlyList<string> SelectedPhysicalDeviceIds);

/// <summary>Represents isolated virtual-output hosting settings.</summary>
internal sealed record VirtualOutputHostingStatusResponse(
    string ExecutionMode,
    string WorkerPolicyKey,
    IReadOnlyList<VirtualOutputBindingStatusResponse> Bindings);

/// <summary>Represents one virtual-output listener binding.</summary>
internal sealed record VirtualOutputBindingStatusResponse(
    string Transport,
    IReadOnlyList<string>? Addresses,
    int Port,
    string? PublicBaseUri,
    string SecurityPolicyKey);

/// <summary>Represents one safely displayable adapter access grant.</summary>
internal sealed record AccessGrantStatusResponse(
    string GrantId,
    string Name,
    DateTime CreatedAtUtc,
    DateTime? RotatedAtUtc,
    DateTime? ExpiresAtUtc,
    DateTime? RevokedAtUtc);

/// <summary>Represents availability and safe metadata for adapter access grants.</summary>
internal sealed record AccessGrantsStatusResponse(
    string Status,
    string? Error,
    IReadOnlyList<AccessGrantStatusResponse> Items);

/// <summary>Represents one reusable virtual-output security policy without filesystem certificate references.</summary>
internal sealed record VirtualOutputSecurityPolicyStatusResponse(
    string Key,
    string Name,
    bool CertificateConfigured,
    string ClientCertificateMode,
    IReadOnlyList<string>? AllowedNetworks,
    int MaximumConnections,
    int RequestsPerMinutePerClient,
    bool AllowForwardedHeaders,
    IReadOnlyList<string>? TrustedProxies);

/// <summary>Represents one reusable virtual-output worker policy.</summary>
internal sealed record VirtualOutputWorkerPolicyStatusResponse(
    string Key,
    string Name,
    int MaximumRestarts,
    int RestartWindowSeconds,
    int DrainTimeoutSeconds,
    int LeaseGracePeriodSeconds,
    int MaximumOutstandingBrokerCalls,
    int MaximumMemoryMib,
    int MaximumCpuPercent);

/// <summary>Represents current physical channels, guide bindings, and per-output projections.</summary>
internal sealed record ChannelsStatusResponse(
    string Status,
    string? Error,
    int Count,
    IReadOnlyList<PhysicalChannelStatusResponse> Items,
    IReadOnlyList<VirtualChannelProjectionStatusResponse> VirtualProjections);

/// <summary>Represents one active physical source channel and its global identity.</summary>
internal sealed record PhysicalChannelStatusResponse(
    string PhysicalDeviceId,
    string ProviderKey,
    string ProviderNumber,
    string ProviderName,
    string EffectiveNumber,
    string EffectiveName,
    string? NumberOverride,
    string? NameOverride,
    string? EquivalenceGroupId,
    bool Enabled,
    IReadOnlyDictionary<string, string> Metadata,
    DateTime FirstSeenUtc,
    DateTime LastSeenUtc,
    GuideBindingStatusResponse? GuideBinding);

/// <summary>Represents the authoritative guide binding for one physical channel.</summary>
internal sealed record GuideBindingStatusResponse(
    string State,
    GuideChannelStatusResponse? SelectedChannel,
    IReadOnlyList<GuideChannelStatusResponse> Candidates);

/// <summary>Represents one guide-source channel used for binding diagnostics.</summary>
internal sealed record GuideChannelStatusResponse(
    string GuideSourceId,
    string ProviderKey,
    string Number,
    string Name,
    string? Affiliate,
    string? ImageUrl,
    bool IsProtected,
    bool IsFavorite,
    bool HasSupplementalMetadata);

/// <summary>Represents the channels projected by one virtual device.</summary>
internal sealed record VirtualChannelProjectionStatusResponse(
    string VirtualDeviceId,
    int ChannelCount,
    IReadOnlyList<ResolvedVirtualChannelStatusResponse> Channels,
    IReadOnlyList<ChannelConflictStatusResponse> Conflicts);

/// <summary>Represents one resolved virtual channel and its ordered physical sources.</summary>
internal sealed record ResolvedVirtualChannelStatusResponse(
    string Number,
    string Name,
    IReadOnlyList<SourceChannelStatusResponse> Sources);

/// <summary>Represents one physical source-channel identity.</summary>
internal sealed record SourceChannelStatusResponse(string PhysicalDeviceId, string ProviderKey);

/// <summary>Represents one channel-number conflict suppressed from a virtual output.</summary>
internal sealed record ChannelConflictStatusResponse(
    string Number,
    SourceChannelStatusResponse PublishedSource,
    SourceChannelStatusResponse ConflictingSource);

/// <summary>Represents the last committed import metadata for one guide source.</summary>
internal sealed record GuideSourceImportStatusResponse(
    string GuideSourceId,
    DateTime FetchedAtUtc,
    DateTime CommittedAtUtc,
    DateTime CoverageStartUtc,
    DateTime CoverageEndUtc,
    int ChannelCount,
    int ProgramCount,
    bool HasSupplementalMetadata);

/// <summary>Represents availability and committed metadata for guide-source imports.</summary>
internal sealed record GuideSourceImportsStatusResponse(
    string Status,
    string? Error,
    IReadOnlyList<GuideSourceImportStatusResponse> Items);

/// <summary>Represents current publication state for one guide output.</summary>
internal sealed record GuideOutputStatusResponse(
    string GuideOutputId,
    string Name,
    string Key,
    bool Enabled,
    bool Available,
    string? DownloadUrl,
    DateTime? LastWriteTimeUtc,
    string? Error);

/// <summary>Represents the configured topology and all active end-to-end streaming paths.</summary>
internal sealed record StreamingFlowsStatusResponse(
    int ActiveConsumerCount,
    int ActiveProducerCount,
    int WarmProducerCount,
    IReadOnlyList<StreamingFlowNodeStatusResponse> Nodes,
    IReadOnlyList<StreamingFlowPathStatusResponse> ActivePaths);

/// <summary>Represents one configured or active streaming-flow node.</summary>
internal sealed record StreamingFlowNodeStatusResponse(
    string Id,
    string Lane,
    string Label,
    string Subtitle,
    string State,
    IReadOnlyDictionary<string, string> Details,
    IReadOnlyList<string> ActiveChannels,
    int ActiveSessionCount);

/// <summary>Represents one active consumer's end-to-end streaming path.</summary>
internal sealed record StreamingFlowPathStatusResponse(
    string Id,
    string SessionId,
    string? Endpoint,
    string Format,
    string Channel,
    DateTime StartedAtUtc,
    bool IsSynthetic,
    bool IsShared,
    int SharedReceiverCount,
    IReadOnlyList<StreamingFlowStageStatusResponse> Stages);

/// <summary>Represents one stage in an active streaming path.</summary>
internal sealed record StreamingFlowStageStatusResponse(
    string Kind,
    string Label,
    string Subtitle,
    IReadOnlyDictionary<string, string> Details,
    ActiveStreamMediaStatusResponse? InputMedia,
    ActiveStreamMediaStatusResponse? OutputMedia);
