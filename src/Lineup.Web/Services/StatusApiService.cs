using System.Collections.ObjectModel;
using System.Reflection;
using Lineup.Core.Storage;

namespace Lineup.Web.Services;

/// <summary>
/// Represents the stable versioned Lineup status response.
/// </summary>
internal sealed record LineupStatusResponse(
    string SchemaVersion,
    DateTime GeneratedAtUtc,
    ApplicationStatusResponse Application,
    GuideStatusResponse Guide,
    PhysicalDeviceStatusResponse PhysicalDevice,
    IReadOnlyList<VirtualDeviceStatusResponse> VirtualDevices,
    IReadOnlyList<TunerStatusResponse> Tuners,
    ActiveStreamsStatusResponse Streams);

/// <summary>
/// Represents application identity, setup, and uptime status.
/// </summary>
internal sealed record ApplicationStatusResponse(string Version, string InstanceId, bool SetupComplete, DateTime StartedAtUtc, long UptimeSeconds);

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
    XmltvStatusResponse Xmltv);

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
/// Represents physical HDHomeRun device state.
/// </summary>
internal sealed record PhysicalDeviceStatusResponse(
    string Status,
    string? Error,
    bool Discovered,
    bool Discovering,
    bool RefreshingTuners,
    string? ConfiguredAddress,
    string? FriendlyName,
    string? ModelNumber,
    string? FirmwareName,
    string? FirmwareVersion,
    string? DeviceId,
    int? TunerCount,
    string? BaseUrl,
    string? LineupUrl,
    DateTime? LastDeviceRefreshUtc,
    DateTime? NextDeviceRefreshUtc,
    DateTime? LastTunerRefreshUtc,
    DateTime? NextTunerRefreshUtc);

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

/// <summary>
/// Represents one physical tuner status snapshot.
/// </summary>
internal sealed record TunerStatusResponse(
    int Index,
    bool Active,
    bool Streaming,
    bool Locked,
    string? Channel,
    string? VirtualChannel,
    string? TargetUrl,
    string? LockType,
    int SignalStrength,
    int SignalToNoiseQuality,
    int SymbolErrorQuality,
    long BitsPerSecond,
    int PacketsPerSecond);

/// <summary>
/// Represents the active stream collection.
/// </summary>
internal sealed record ActiveStreamsStatusResponse(int Count, IReadOnlyList<ActiveStreamStatusResponse> Items);

/// <summary>
/// Represents one active stream and its media tracks.
/// </summary>
internal sealed record ActiveStreamStatusResponse(
    string SessionId,
    string? ClientId,
    string? ClientAddress,
    string Channel,
    string Format,
    DateTime StartedAtUtc,
    long DurationSeconds,
    long? SourceBitRate,
    IReadOnlyList<ActiveStreamTrackStatusResponse> Tracks);

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
    private readonly IEpgRepository _repository;
    private readonly IAppSettingsService _settings;
    private readonly IDeviceStateService _deviceState;
    private readonly IAutoFetchStateService _autoFetch;
    private readonly IActiveStreamRegistry _activeStreams;
    private readonly VirtualDeviceStatusCache _virtualDevices;
    private readonly StatusApiRuntime _runtime;
    private readonly ILogger<StatusApiService> _logger;
    private readonly IXmltvPublicationStore _publications;

    /// <summary>
    /// Initializes the status aggregation service.
    /// </summary>
    public StatusApiService(
        IEpgRepository repository,
        IAppSettingsService settings,
        IDeviceStateService deviceState,
        IAutoFetchStateService autoFetch,
        IActiveStreamRegistry activeStreams,
        VirtualDeviceStatusCache virtualDevices,
        StatusApiRuntime runtime,
        ILogger<StatusApiService> logger,
        IXmltvPublicationStore publications)
    {
        _repository = repository;
        _settings = settings;
        _deviceState = deviceState;
        _autoFetch = autoFetch;
        _activeStreams = activeStreams;
        _virtualDevices = virtualDevices;
        _runtime = runtime;
        _logger = logger;
        _publications = publications;
    }

    /// <summary>
    /// Builds the current passive Lineup status snapshot.
    /// </summary>
    internal async Task<LineupStatusResponse> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var generatedAtUtc = DateTime.UtcNow;
        var settings = _settings.Settings;
        var guide = await GetGuideStatusAsync(settings, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        long uptimeSeconds = Math.Max(0, (long)(generatedAtUtc - _runtime.StartedAtUtc).TotalSeconds);
        return new LineupStatusResponse(
            "1.0",
            generatedAtUtc,
            new ApplicationStatusResponse(_runtime.Version, _runtime.InstanceId, settings.IsSetupComplete, _runtime.StartedAtUtc, uptimeSeconds),
            guide,
            GetPhysicalDeviceStatus(settings),
            GetVirtualDeviceStatuses(settings),
            GetTunerStatuses(settings),
            GetActiveStreamStatuses(settings, generatedAtUtc));
    }

    private async Task<GuideStatusResponse> GetGuideStatusAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var autoFetch = GetAutoFetchStatus();
        var xmltv = GetXmltvStatus(settings);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _repository.EnsureDatabaseCreatedAsync();
            var statistics = await _repository.GetCacheStatisticsAsync();
            var safeFetchStart = await _repository.GetSafeFetchStartTimeAsync();
            cancellationToken.ThrowIfCancellationRequested();
            return new GuideStatusResponse(
                "available",
                null,
                statistics.ChannelCount,
                statistics.ProgramCount,
                ToUtc(statistics.EarliestProgramStart),
                ToUtc(statistics.LatestProgramEnd),
                statistics.TotalTimeSpan?.TotalHours,
                ToUtc(safeFetchStart),
                autoFetch,
                xmltv);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to build guide section for the status API");
            return new GuideStatusResponse("unavailable", "Guide state is unavailable.", null, null, null, null, null, null, autoFetch, xmltv);
        }
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

    private XmltvStatusResponse GetXmltvStatus(AppSettings settings)
    {
        if (!settings.AutoGenerateXmltv)
        {
            return new XmltvStatusResponse(false, false, null, null);
        }

        try
        {
            var available = _publications.Exists(settings.XmltvOutputPath);
            return new XmltvStatusResponse(
                true,
                available,
                settings.RedactApiUrls || !available ? null : "/api/xmltv",
                available ? _publications.GetLastWriteTimeUtc(settings.XmltvOutputPath) : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new XmltvStatusResponse(true, false, null, null);
        }
    }

    private PhysicalDeviceStatusResponse GetPhysicalDeviceStatus(AppSettings settings)
    {
        var device = _deviceState.DeviceInfo;
        var status = _deviceState.IsDiscovering
            ? "discovering"
            : device != null
                ? "available"
                : _deviceState.LastError != null ? "error" : "unavailable";
        return new PhysicalDeviceStatusResponse(
            status,
            _deviceState.LastError == null ? null : "Device state is unavailable.",
            _deviceState.IsDiscovered,
            _deviceState.IsDiscovering,
            _deviceState.IsRefreshingTuners,
            settings.RedactApiDeviceAddresses ? null : settings.DeviceAddress,
            device?.FriendlyName,
            device?.ModelNumber,
            device?.FirmwareName,
            device?.FirmwareVersion,
            device?.DeviceID,
            device?.TunerCount,
            settings.RedactApiUrls ? null : device?.BaseURL,
            settings.RedactApiUrls ? null : device?.LineupURL,
            ToUtc(_deviceState.LastDeviceRefresh),
            ToUtc(_deviceState.NextDeviceRefresh),
            ToUtc(_deviceState.LastTunerRefresh),
            ToUtc(_deviceState.NextTunerRefresh));
    }

    private ReadOnlyCollection<VirtualDeviceStatusResponse> GetVirtualDeviceStatuses(AppSettings settings)
    {
        return settings.HdHomeRunProxyProfiles.Select((profile, index) =>
        {
            var cached = _virtualDevices.Get(profile.VirtualDeviceId, profile.PhysicalAddress);
            var resolved = cached?.LastSuccessfulSnapshot;
            var enabled = settings.EnableHdHomeRunProxy && profile.Enabled;
            return new VirtualDeviceStatusResponse(
                profile.VirtualDeviceId,
                enabled,
                index == 0,
                enabled && resolved != null,
                cached?.LastError == null ? null : "Virtual device state is unavailable.",
                resolved?.FriendlyName ?? NullIfEmpty(profile.FriendlyName),
                resolved?.TunerCount,
                profile.TunerCountCap,
                settings.RedactApiDeviceAddresses ? null : profile.PhysicalAddress,
                settings.RedactApiUrls ? null : NullIfEmpty(profile.AdvertisedBaseUrl),
                ToUtc(resolved?.ResolvedAtUtc),
                ToUtc(cached?.LastAttemptAtUtc));
        }).ToArray().AsReadOnly();
    }

    private ReadOnlyCollection<TunerStatusResponse> GetTunerStatuses(AppSettings settings)
    {
        return _deviceState.TunerStatuses.Select(tuner => new TunerStatusResponse(
            tuner.TunerIndex,
            tuner.IsActive,
            tuner.IsStreaming,
            tuner.HasLock,
            tuner.Channel,
            tuner.VirtualChannel,
            settings.RedactApiUrls ? null : tuner.Target,
            tuner.LockType,
            tuner.SignalStrength,
            tuner.SignalToNoiseQuality,
            tuner.SymbolErrorQuality,
            tuner.BitsPerSecond,
            tuner.PacketsPerSecond)).ToArray().AsReadOnly();
    }

    private ActiveStreamsStatusResponse GetActiveStreamStatuses(AppSettings settings, DateTime generatedAtUtc)
    {
        var streams = _activeStreams.GetActiveStreams().Select(stream => new ActiveStreamStatusResponse(
            stream.SessionId,
            stream.ClientId,
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
                track.SubtitlePresentation is null ? null : FormatName(track.SubtitlePresentation.Value))).ToArray())).ToArray();
        return new ActiveStreamsStatusResponse(streams.Length, streams);
    }

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
