using Lineup.Web.Components.Playback;
using Lineup.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lineup.Web.Components.Pages;

/// <summary>
/// Provides live TV playback over one shared CMAF presentation.
/// </summary>
public partial class Watch : IAsyncDisposable
{
    private CmafPlayerSurface? _playerSurface;

    [Inject]
    private ILineupGuideReader GuideReader { get; set; } = default!;

    [Inject]
    private IActiveStreamRegistry ActiveStreamRegistry { get; set; } = default!;

    [Inject]
    private ILineupSettingsService SettingsService { get; set; } = default!;

    [Inject]
    private IPhysicalDeviceStatusService DeviceStatuses { get; set; } = default!;

    [Inject]
    private IBrowserDataStore BrowserData { get; set; } = default!;

    [Inject]
    private IStatusNotificationService Notifications { get; set; } = default!;

    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    [Inject]
    private NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    private ILogger<Watch> Logger { get; set; } = default!;

    /// <summary>Gets or sets a channel number supplied through the route.</summary>
    [Parameter]
    public string? ChannelNumber { get; set; }

    private readonly string _clientId = Guid.NewGuid().ToString("N");
    private readonly SemaphoreSlim _stopGate = new(1, 1);
    private List<GuideChannelSegment> _channels = [];
    private List<GuideCacheProgram> _programs = [];
    private GuideChannelSegment? _selectedChannel;
    private GuideCacheProgram? _currentProgram;
    private ActiveStreamSnapshot? _activeStream;
    private CmafPlayerAudio? _playerAudio;
    private CmafPlayerVideo? _playerVideo;
    private CmafStartResponse? _session;
    private DotNetObjectReference<Watch>? _dotNetReference;
    private IDisposable? _locationChangingRegistration;
    private Timer? _streamInfoTimer;
    private string? _selectedChannelNumber;
    private string? _errorMessage;
    private string? _lastRouteChannelNumber;
    private string? _pendingRouteChannelNumber;
    private string _manualChannelNumber = string.Empty;
    private string? _manualTuneValidationMessage;
    private string? _pendingStartId;
    private string _effectiveProtocol = string.Empty;
    private CmafPlaybackProtocol _protocol = CmafPlaybackProtocol.Auto;
    private WebPlayerQuality _quality = WebPlayerQuality.AppDefault;
    private CmafPreferredVideo _preferredVideo = CmafPreferredVideo.Source;
    private CmafPreferredVideo _streamPreferredVideo = CmafPreferredVideo.Source;
    private CmafPreferredAudio _preferredAudio = CmafPreferredAudio.Source;
    private CmafPreferredAudio _streamPreferredAudio = CmafPreferredAudio.Source;
    private CmafCompatibilityProfile? _compatibilityProfile;
    private CmafStreamOverrides _streamOverrides = new();
    private CmafPreferredVideo? _runtimeVideoOverride;
    private CmafPreferredAudio? _runtimeAudioOverride;
    private CmafFallbackAudio? _runtimeFallbackAudioOverride;
    private CmafFallbackAudio _fallbackAudio = CmafFallbackAudio.AacStereo;
    private CmafFallbackAudio _streamFallbackAudio = CmafFallbackAudio.AacStereo;
    private int? _audioTrack;
    private int? _subtitleTrack;
    private SubtitlePresentation? _subtitlePresentation;
    private bool _subtitleEmbedded;
    private bool _sessionUsesBurnIn;
    private bool _isLoadingChannels = true;
    private bool _isPlaying;
    private bool _needsPlayerInit;
    private bool _isJsInteropReady;
    private bool _preferencesRestored;
    private bool _isStarting;
    private bool _isStopping;
    private bool _isPlayerLoading;
    private bool _disposed;
    private long _playbackGeneration;
    private DateTime _lastTunerRefreshRequestUtc = DateTime.MinValue;
    private PhysicalReceiverStatus? SelectedReceiver => DeviceStatuses.Devices
        .SelectMany(device => device.Status?.Receivers ?? [])
        .FirstOrDefault(receiver => string.Equals(receiver.VirtualChannel, _selectedChannelNumber, StringComparison.Ordinal));
    private string OverrideProtocolValue => _streamOverrides.Enabled ? _streamOverrides.Protocol?.ToString() ?? string.Empty : _protocol.ToString();
    private string OverrideQualityValue => _streamOverrides.Enabled ? _streamOverrides.Quality?.ToString() ?? string.Empty : _quality.ToString();
    private string OverrideVideoValue => _streamOverrides.Enabled ? _streamOverrides.Video?.ToString() ?? string.Empty : _preferredVideo.ToString();
    private string OverrideAudioValue => _streamOverrides.Enabled ? _streamOverrides.Audio?.ToString() ?? string.Empty : _preferredAudio.ToString();
    private string OverrideFallbackAudioValue => _streamOverrides.Enabled ? _streamOverrides.FallbackAudio?.ToString() ?? string.Empty : _fallbackAudio.ToString();
    private CmafPlaybackProtocol EffectiveProtocol => _streamOverrides.Enabled ? _streamOverrides.Protocol ?? CmafPlaybackProtocol.Auto : _protocol;

    /// <summary>Loads channel and program data and registers stream lifecycle handlers.</summary>
    protected override async Task OnInitializedAsync()
    {
        _effectiveProtocol = Text["Watch_Waiting"];
        _locationChangingRegistration = NavigationManager.RegisterLocationChangingHandler(OnLocationChangingAsync);
        ActiveStreamRegistry.StopRequested += OnActiveStreamStopRequested;
        await LoadChannelsAsync();
    }

    /// <summary>Stages a route channel until browser preferences are restored.</summary>
    protected override async Task OnParametersSetAsync()
    {
        if (string.Equals(ChannelNumber, _lastRouteChannelNumber, StringComparison.Ordinal))
        {
            return;
        }

        _lastRouteChannelNumber = ChannelNumber;
        _pendingRouteChannelNumber = ChannelNumber;
        await ProcessPendingRouteAsync();
    }

    /// <summary>Restores preferences and initializes Shaka after the video element is rendered.</summary>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        _isJsInteropReady = true;
        if (firstRender)
        {
            await RestorePreferencesAsync();
            _preferencesRestored = true;
            StateHasChanged();
            if (await ProcessPendingRouteAsync())
            {
                StateHasChanged();
                return;
            }
        }

        if (!_needsPlayerInit || !_isPlaying || _session is null)
        {
            return;
        }

        _needsPlayerInit = false;
        var initializingSession = _session;
        var (manifestUrl, fallbackUrl) = ResolveManifestUrls(initializingSession);
        _dotNetReference ??= DotNetObjectReference.Create(this);
        if (_playerSurface is null)
        {
            _needsPlayerInit = true;
            return;
        }
        try
        {
            var result = await _playerSurface.InitializeLiveAsync<CmafPlayerResult, Watch>(
                manifestUrl,
                fallbackUrl,
                _streamPreferredAudio.ToString(),
                _streamPreferredVideo.ToString(),
                initializingSession.HasSourceVideoRendition,
                initializingSession.SourceVideoCodec,
                initializingSession.FallbackAudioCodec,
                initializingSession.Subtitles ?? [],
                _dotNetReference,
                initializingSession.SessionId);
            if (!string.Equals(_session?.SessionId, initializingSession.SessionId, StringComparison.Ordinal))
            {
                return;
            }
            if (!result.Success)
            {
                var retryVideo = result.ErrorCode is 3016 or 4032 &&
                    initializingSession.HasSourceVideoRendition &&
                    _streamPreferredVideo is CmafPreferredVideo.Source or CmafPreferredVideo.Auto &&
                    (result.ErrorCode == 3016 || result.SourceVideoSupported == false);
                var retrySourceAudio = result.ErrorCode == 4032 &&
                    !retryVideo &&
                    initializingSession.FallbackAudioCodec is null &&
                    _streamPreferredAudio is CmafPreferredAudio.Source or CmafPreferredAudio.Auto;
                var retryPackagedFallbackAudio = result.ErrorCode == 4032 &&
                    initializingSession.FallbackAudioCodec is not null &&
                    _streamFallbackAudio != CmafFallbackAudio.AacStereo &&
                    result.FallbackAudioSupported == false;
                var retryAudio = retrySourceAudio || retryPackagedFallbackAudio;
                if ((retryVideo || retryAudio) && _selectedChannelNumber is { } retryChannelNumber)
                {
                    var videoPreference = retryVideo ? CmafPreferredVideo.Fallback : _streamPreferredVideo;
                    var audioPreference = retryAudio ? CmafPreferredAudio.Fallback : _streamPreferredAudio;
                    var fallbackAudio = retryAudio ? CmafFallbackAudio.AacStereo : _streamFallbackAudio;
                    var unavailable = retryVideo && retryAudio
                        ? Text["Watch_SourceVideoAndAudioUnavailable", FormatFallbackAudio(_streamFallbackAudio)]
                        : retryVideo
                            ? Text["Watch_SourceVideoUnavailable"]
                            : Text["Watch_AudioUnavailable", FormatFallbackAudio(_streamFallbackAudio)];
                    var replacement = retryVideo && retryAudio
                        ? Text["Watch_H264AndAacStereo"].Value
                        : retryVideo
                            ? "H.264"
                            : "AAC Stereo";
                    Notifications.ShowError(Text["Watch_RetryingWithReplacement", unavailable, replacement]);
                    await TuneChannelAsync(retryChannelNumber, _selectedChannel, fallbackAudio, videoPreference, audioPreference);
                    StateHasChanged();
                    return;
                }

                _errorMessage = result.Error ?? Text["Watch_UnableToInitializePlayback"];
                await StopStreamAsync();
            }
            else
            {
                _effectiveProtocol = string.Equals(result.ManifestUrl, initializingSession.DashManifestUrl, StringComparison.Ordinal) ? "DASH" : "HLS";
                _playerAudio = result.Audio;
                _playerVideo = result.Video;
                _isPlayerLoading = false;
                await DeviceStatuses.RefreshAsync(SettingsService.Settings.Streaming);
            }
            StateHasChanged();
        }
        catch (JSException ex)
        {
            _errorMessage = Text["Watch_UnableToInitializePlaybackWithError", ex.Message];
            await StopStreamAsync();
            StateHasChanged();
        }
    }

    /// <summary>Receives asynchronous player fallback and failure events from Shaka.</summary>
    [JSInvokable]
    public async Task OnCmafPlayerEvent(CmafPlayerEvent playerEvent)
    {
        if (!string.Equals(playerEvent.SessionId, _session?.SessionId, StringComparison.Ordinal))
        {
            return;
        }

        if (string.Equals(playerEvent.Kind, "audio-track-changed", StringComparison.Ordinal))
        {
            _playerAudio = CmafPlayerAudio.FromDetails(playerEvent.Details);
        }
        else if (string.Equals(playerEvent.Kind, "audio-fallback", StringComparison.Ordinal))
        {
            _playerAudio = CmafPlayerAudio.FromDetails(playerEvent.Details);
            Notifications.ShowError(playerEvent.Message);
        }
        else if (string.Equals(playerEvent.Kind, "video-fallback", StringComparison.Ordinal))
        {
            _playerVideo = CmafPlayerVideo.FromDetails(playerEvent.Details);
            Notifications.ShowError(playerEvent.Message);
        }
        else if (string.Equals(playerEvent.Kind, "video-fallback-required", StringComparison.Ordinal) &&
            _streamPreferredVideo is CmafPreferredVideo.Source or CmafPreferredVideo.Auto &&
            _selectedChannelNumber is { } channelNumber)
        {
            Notifications.ShowError(playerEvent.Message);
            await TuneChannelAsync(channelNumber, _selectedChannel, _streamFallbackAudio, CmafPreferredVideo.Fallback);
        }
        else if (string.Equals(playerEvent.Kind, "protocol-fallback", StringComparison.Ordinal))
        {
            Notifications.ShowSuccess(playerEvent.Message);
            _effectiveProtocol = "HLS";
        }
        else if (string.Equals(playerEvent.Kind, "error", StringComparison.Ordinal))
        {
            _errorMessage = playerEvent.Message;
            await StopStreamAsync();
        }
        StateHasChanged();
    }

    private async Task LoadChannelsAsync()
    {
        try
        {
            var guideChannels = await GuideReader.GetChannelsAsync();
            _channels = guideChannels
                .OrderBy(channel => GetChannelSortValue(channel.GuideNumber))
                .ThenBy(channel => channel.GuideNumber, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var now = DateTime.UtcNow;
            _programs = (await GuideReader.GetProgramsAsync(now, now.AddHours(1))).ToList();
        }
        finally
        {
            _isLoadingChannels = false;
        }
    }

    private static decimal GetChannelSortValue(string? channelNumber) =>
        decimal.TryParse(channelNumber, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            ? value
            : decimal.MaxValue;

    private async Task ManualTuneAsync()
    {
        var channelNumber = _manualChannelNumber.Trim();
        if (!IsValidChannelNumber(channelNumber))
        {
            _manualTuneValidationMessage = Text["Watch_InvalidChannelNumber"];
            return;
        }

        _manualTuneValidationMessage = null;
        await TuneChannelAsync(channelNumber, _channels.FirstOrDefault(channel => channel.GuideNumber == channelNumber));
    }

    private async Task TuneChannelAsync(
        string? channelNumber,
        GuideChannelSegment? channel,
        CmafFallbackAudio? streamFallbackAudio = null,
        CmafPreferredVideo? streamPreferredVideo = null,
        CmafPreferredAudio? streamPreferredAudio = null)
    {
        if (_isStarting)
        {
            return;
        }

        var normalized = channelNumber?.Trim();
        _manualChannelNumber = normalized ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized) || (channel is null && !IsValidChannelNumber(normalized)))
        {
            _manualTuneValidationMessage = Text["Watch_InvalidChannelNumber"];
            return;
        }

        _isStarting = true;
        _isPlayerLoading = true;
        _errorMessage = null;
        StateHasChanged();
        var generation = 0L;
        string? startupId = null;
        try
        {
            var changed = !string.Equals(_selectedChannelNumber, normalized, StringComparison.Ordinal);
            await StopStreamAsync(preserveLoadingIndicator: true);
            if (_disposed)
            {
                return;
            }

            generation = Interlocked.Increment(ref _playbackGeneration);
            startupId = Guid.NewGuid().ToString("N");
            _pendingStartId = startupId;
            if (changed)
            {
                _audioTrack = null;
                _subtitleTrack = null;
                _subtitlePresentation = null;
                _subtitleEmbedded = false;
            }

            _selectedChannelNumber = normalized;
            _selectedChannel = channel;
            _currentProgram = GetCurrentProgram(normalized);
            _manualTuneValidationMessage = null;
            _runtimeVideoOverride = streamPreferredVideo;
            _runtimeAudioOverride = streamPreferredAudio;
            _runtimeFallbackAudioOverride = streamFallbackAudio;
            var useMeasuredProfile = _compatibilityProfile is not null;
            _streamFallbackAudio = streamFallbackAudio ?? (_streamOverrides.Enabled ? _streamOverrides.FallbackAudio ?? _fallbackAudio : _fallbackAudio);
            _streamPreferredVideo = streamPreferredVideo ??
                (_streamOverrides.Enabled && _streamOverrides.Video.HasValue
                    ? _streamOverrides.Video.Value
                    : useMeasuredProfile ? CmafPreferredVideo.Auto : _preferredVideo);
            _streamPreferredAudio = streamPreferredAudio ??
                (_streamOverrides.Enabled && _streamOverrides.Audio.HasValue
                    ? _streamOverrides.Audio.Value
                    : useMeasuredProfile ? CmafPreferredAudio.Auto : _preferredAudio);
            var player = GetWebPlayer();
            if (player is null)
            {
                _isPlayerLoading = false;
                _errorMessage = Text["Watch_WebPlayerNotEnabled"];
                Notifications.ShowError(_errorMessage);
                return;
            }
            var managementHosted = player.Hosting is null;
            var startUrl = managementHosted
                ? $"/api/stream/cmaf/start/{Uri.EscapeDataString(normalized)}"
                : GetDedicatedMediaUri(player) is { } dedicatedMediaUri
                    ? new Uri(dedicatedMediaUri, $"media/start/{Uri.EscapeDataString(normalized)}").AbsoluteUri
                    : null;
            if (startUrl is null)
            {
                _isPlayerLoading = false;
                _errorMessage = Text["Watch_DedicatedListenerNotConfigured"];
                Notifications.ShowError(_errorMessage);
                return;
            }
            var startedSession = await JS.StartCmafSessionAsync<CmafStartResponse>(
                startUrl,
                BuildStartRequest(),
                startupId);
            if (startedSession is null)
            {
                _isPlayerLoading = false;
                _errorMessage = Text["Watch_NoPlaybackSession"];
                Notifications.ShowError(_errorMessage);
                return;
            }
            if (managementHosted)
            {
                startedSession = startedSession with
                {
                    StopUrl = $"/api/stream/cmaf/stop/{Uri.EscapeDataString(startedSession.SessionId)}"
                };
            }
            if (_disposed || generation != Volatile.Read(ref _playbackGeneration) || !string.Equals(_pendingStartId, startupId, StringComparison.Ordinal))
            {
                await StopCmafSessionAsync(startedSession);
                return;
            }

            _pendingStartId = null;
            _session = startedSession;
            if (startedSession.SourceAudioFallbackApplied)
            {
                Notifications.ShowError(Text["Watch_SourceAudioPackagingFallback", startedSession.FallbackAudioTitle ?? Text["Watch_FallbackAudioLower"]]);
            }
            _isPlaying = true;
            _sessionUsesBurnIn = _subtitlePresentation == SubtitlePresentation.BurnIn;
            _needsPlayerInit = true;
            _effectiveProtocol = EffectiveProtocol == CmafPlaybackProtocol.Auto ? Text["Watch_AutoProtocol", "DASH"] : EffectiveProtocol.ToString().ToUpperInvariant();
            StartStreamInfoRefresh();
        }
        catch (JSException ex)
        {
            if (_disposed || generation != Volatile.Read(ref _playbackGeneration))
            {
                return;
            }

            _isPlayerLoading = false;
            _errorMessage = ex.Message;
            Notifications.ShowError(Text["Watch_UnableToStartPlayback", ex.Message]);
        }
        finally
        {
            if (string.Equals(_pendingStartId, startupId, StringComparison.Ordinal))
            {
                _pendingStartId = null;
            }
            _isStarting = false;
            await ProcessPendingRouteAsync();
        }
    }

    private async Task<bool> ProcessPendingRouteAsync()
    {
        if (_disposed || _isStarting || !_preferencesRestored || string.IsNullOrWhiteSpace(_pendingRouteChannelNumber))
        {
            return false;
        }

        var channelNumber = _pendingRouteChannelNumber;
        _pendingRouteChannelNumber = null;
        var channel = _channels.FirstOrDefault(candidate => string.Equals(candidate.GuideNumber, channelNumber, StringComparison.Ordinal));
        await TuneChannelAsync(channelNumber, channel);
        return true;
    }

    private CmafStreamRequest BuildStartRequest() =>
        new()
        {
            ClientId = _clientId,
            Quality = _quality,
            PreferredVideo = _streamPreferredVideo,
            PreferredAudio = _streamPreferredAudio,
            FallbackAudio = _streamFallbackAudio,
            AudioTrack = _audioTrack,
            SubtitleTrack = _subtitleTrack,
            SubtitlePresentation = _subtitlePresentation,
            EmbeddedCaptions = _subtitleEmbedded,
            CompatibilityProfile = _compatibilityProfile,
            Overrides = _streamOverrides with
            {
                Video = _runtimeVideoOverride ?? _streamOverrides.Video,
                Audio = _runtimeAudioOverride ?? _streamOverrides.Audio,
                FallbackAudio = _runtimeFallbackAudioOverride ?? _streamOverrides.FallbackAudio
            }
        };

    private (string ManifestUrl, string? FallbackUrl) ResolveManifestUrls(CmafStartResponse session) =>
        EffectiveProtocol switch
        {
            CmafPlaybackProtocol.Hls => (session.HlsManifestUrl, null),
            CmafPlaybackProtocol.Dash => (session.DashManifestUrl, null),
            CmafPlaybackProtocol.Auto when _compatibilityProfile is not null &&
                !CmafCompatibilityProfilePolicy.SupportsProtocol(_compatibilityProfile, CmafProtocol.Dash) &&
                CmafCompatibilityProfilePolicy.SupportsProtocol(_compatibilityProfile, CmafProtocol.Hls) => (session.HlsManifestUrl, null),
            CmafPlaybackProtocol.Auto when _compatibilityProfile is not null &&
                CmafCompatibilityProfilePolicy.SupportsProtocol(_compatibilityProfile, CmafProtocol.Dash) &&
                !CmafCompatibilityProfilePolicy.SupportsProtocol(_compatibilityProfile, CmafProtocol.Hls) => (session.DashManifestUrl, null),
            _ => (session.DashManifestUrl, session.HlsManifestUrl)
        };

    private async Task ChangeOverrideEnabled(ChangeEventArgs args)
    {
        _streamOverrides = _streamOverrides with { Enabled = bool.TryParse(args.Value?.ToString(), out var enabled) && enabled };
        await SavePreferencesAndRestartAsync();
    }

    private async Task ChangeProtocol(ChangeEventArgs args)
    {
        if (_streamOverrides.Enabled)
        {
            _streamOverrides = _streamOverrides with
            {
                Protocol = Enum.TryParse<CmafPlaybackProtocol>(args.Value?.ToString(), out var overridden) ? overridden : null
            };
            await SavePreferencesAndRestartAsync();
            return;
        }
        if (Enum.TryParse<CmafPlaybackProtocol>(args.Value?.ToString(), out var value))
        {
            _protocol = value;
            await SavePreferencesAndRestartAsync();
        }
    }

    private async Task ChangeQuality(ChangeEventArgs args)
    {
        if (_streamOverrides.Enabled)
        {
            _streamOverrides = _streamOverrides with
            {
                Quality = Enum.TryParse<WebPlayerQuality>(args.Value?.ToString(), out var overridden) ? overridden : null
            };
            await SavePreferencesAndRestartAsync();
            return;
        }
        if (Enum.TryParse<WebPlayerQuality>(args.Value?.ToString(), out var value))
        {
            _quality = value;
            await SavePreferencesAndRestartAsync();
        }
    }

    private async Task ChangePreferredVideo(ChangeEventArgs args)
    {
        if (_streamOverrides.Enabled)
        {
            _streamOverrides = _streamOverrides with
            {
                Video = Enum.TryParse<CmafPreferredVideo>(args.Value?.ToString(), out var overridden) ? overridden : null
            };
            await SavePreferencesAndRestartAsync();
            return;
        }
        if (Enum.TryParse<CmafPreferredVideo>(args.Value?.ToString(), out var value))
        {
            _preferredVideo = value;
            await SavePreferencesAndRestartAsync();
        }
    }

    private async Task ChangePreferredAudio(ChangeEventArgs args)
    {
        if (_streamOverrides.Enabled)
        {
            _streamOverrides = _streamOverrides with
            {
                Audio = Enum.TryParse<CmafPreferredAudio>(args.Value?.ToString(), out var overridden) ? overridden : null
            };
            await SavePreferencesAndRestartAsync();
            return;
        }
        if (Enum.TryParse<CmafPreferredAudio>(args.Value?.ToString(), out var value))
        {
            _preferredAudio = value;
            await SavePreferencesAndRestartAsync();
        }
    }

    private async Task ChangeFallbackAudio(ChangeEventArgs args)
    {
        if (_streamOverrides.Enabled)
        {
            _streamOverrides = _streamOverrides with
            {
                FallbackAudio = Enum.TryParse<CmafFallbackAudio>(args.Value?.ToString(), out var overridden) ? overridden : null
            };
            await SavePreferencesAndRestartAsync();
            return;
        }
        if (Enum.TryParse<CmafFallbackAudio>(args.Value?.ToString(), out var value))
        {
            _fallbackAudio = value;
            await SavePreferencesAndRestartAsync();
        }
    }

    private async Task ChangeSubtitleTrack(ChangeEventArgs args)
    {
        _subtitleTrack = int.TryParse(args.Value?.ToString(), out var value) && value >= 0 ? value : null;
        var subtitle = _activeStream?.Tracks
            .FirstOrDefault(track => track.Type == MediaTrackType.Subtitle && track.SourceIndex == _subtitleTrack);
        _subtitlePresentation = subtitle?.SubtitlePresentation;
        _subtitleEmbedded = subtitle?.IsEmbeddedClosedCaptions == true;
        await SavePreferencesAsync();

        if (_sessionUsesBurnIn || _subtitlePresentation == SubtitlePresentation.BurnIn)
        {
            await RestartCurrentStreamAsync();
            return;
        }

    }

    private async Task SavePreferencesAndRestartAsync()
    {
        await SavePreferencesAsync();
        await RestartCurrentStreamAsync();
    }

    private Task RestartCurrentStreamAsync() =>
        _selectedChannelNumber is { } channelNumber && _isPlaying
            ? TuneChannelAsync(channelNumber, _selectedChannel)
            : Task.CompletedTask;

    private Task StopStreamAsync() => StopStreamAsync(preserveLoadingIndicator: false);

    private async Task StopStreamAsync(bool preserveLoadingIndicator)
    {
        Interlocked.Increment(ref _playbackGeneration);
        var startupId = _pendingStartId;
        _pendingStartId = null;
        var session = _session;
        _session = null;
        _isPlaying = false;
        _needsPlayerInit = false;
        _activeStream = null;
        _playerAudio = null;
        _playerVideo = null;
        _sessionUsesBurnIn = false;
        if (!preserveLoadingIndicator)
        {
            _isPlayerLoading = false;
        }
        _streamInfoTimer?.Dispose();
        _streamInfoTimer = null;

        await _stopGate.WaitAsync();
        _isStopping = true;
        try
        {
            if (_isJsInteropReady)
            {
                try
                {
                    if (startupId is not null)
                    {
                        await JS.CancelCmafStartAsync(startupId);
                    }
                    if (_playerSurface is not null)
                    {
                        await _playerSurface.StopAsync();
                    }
                    await StopCmafSessionAsync(session);
                }
                catch (Exception ex) when (ex is JSDisconnectedException or ObjectDisposedException)
                {
                    Logger.LogDebug(ex, "Browser disconnected while stopping CMAF playback");
                }
            }
            ActiveStreamRegistry.RequestStopByClientId(_clientId);
        }
        finally
        {
            _isStopping = false;
            _stopGate.Release();
        }
    }

    private async Task StopCmafSessionAsync(CmafStartResponse? session)
    {
        if (_isJsInteropReady && session is not null)
        {
            await JS.StopCmafSessionAsync(session.SessionId, session.StopUrl);
        }
    }

    private VirtualDeviceDefinition? GetWebPlayer() =>
        SettingsService.Settings.Streaming.VirtualDevices.SingleOrDefault(device =>
            device.Enabled &&
            string.Equals(device.Adapter.Kind, WebPlayerVirtualTunerDriver.AdapterKind, StringComparison.OrdinalIgnoreCase));

    private static Uri? GetDedicatedMediaUri(VirtualDeviceDefinition player)
    {
        var publicBaseUri = player.Hosting is { } hosting
            ? hosting.Bindings.Select(binding => binding.PublicBaseUri).FirstOrDefault()
            : null;
        return Uri.TryCreate(publicBaseUri, UriKind.Absolute, out var uri)
            ? new Uri(uri.AbsoluteUri.EndsWith('/') ? uri.AbsoluteUri : $"{uri.AbsoluteUri}/")
            : null;
    }

    private void StartStreamInfoRefresh()
    {
        _streamInfoTimer?.Dispose();
        RefreshStreamInfo();
        _streamInfoTimer = new Timer(_ => _ = InvokeAsync(RefreshStreamInfoAsync), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private async Task RefreshStreamInfoAsync()
    {
        if (_disposed)
        {
            return;
        }

        RefreshStreamInfo();
        StateHasChanged();
        if (_isPlaying && DateTime.UtcNow - _lastTunerRefreshRequestUtc >= TimeSpan.FromSeconds(5))
        {
            _lastTunerRefreshRequestUtc = DateTime.UtcNow;
            await DeviceStatuses.RefreshAsync(SettingsService.Settings.Streaming);
        }
    }

    private void RefreshStreamInfo()
    {
        _activeStream = ActiveStreamRegistry.GetActiveStreams()
            .Where(stream => string.Equals(stream.ClientId, _clientId, StringComparison.Ordinal))
            .OrderByDescending(stream => stream.StartedAtUtc)
            .FirstOrDefault();
        var selectedAudio = _activeStream?.Tracks.FirstOrDefault(track => track.Type == MediaTrackType.Audio && track.IsSelected);
        _audioTrack ??= selectedAudio?.SourceIndex;
        if (_subtitleTrack.HasValue && !_subtitlePresentation.HasValue)
        {
            _subtitlePresentation = _activeStream?.Tracks
                .FirstOrDefault(track => track.Type == MediaTrackType.Subtitle && track.SourceIndex == _subtitleTrack)
                ?.SubtitlePresentation;
            _subtitleEmbedded = _activeStream?.Tracks
                .FirstOrDefault(track => track.Type == MediaTrackType.Subtitle && track.SourceIndex == _subtitleTrack)
                ?.IsEmbeddedClosedCaptions == true;
        }
    }

    private async Task RestorePreferencesAsync()
    {
        try
        {
            var preferences = await BrowserData.ReadAsync<CmafWatchPreferences>(CmafWatchPreferences.StorageKey);
            if (preferences is not null && Enum.IsDefined(preferences.Protocol))
            {
                _protocol = preferences.Protocol;
            }
            if (preferences is not null && Enum.IsDefined(preferences.Quality))
            {
                _quality = preferences.Quality;
            }
            if (preferences is not null && Enum.IsDefined(preferences.PreferredVideo))
            {
                _preferredVideo = preferences.PreferredVideo;
            }
            if (preferences is not null && Enum.IsDefined(preferences.PreferredAudio))
            {
                _preferredAudio = preferences.PreferredAudio;
            }
            if (preferences?.FallbackAudio is { } fallbackAudio && Enum.IsDefined(fallbackAudio))
            {
                _fallbackAudio = fallbackAudio;
            }
            if (preferences?.Overrides is { } overrides)
            {
                try
                {
                    CmafStreamPlanner.ValidateOverrides(overrides);
                    _streamOverrides = overrides;
                }
                catch (ArgumentException ex)
                {
                    Logger.LogWarning(ex, "Ignoring invalid Watch stream overrides");
                }
            }
            _subtitleTrack = preferences?.SubtitleTrack;
            var profile = await BrowserData.ReadAsync<CmafCompatibilityProfile>(CmafCompatibilityProfile.StorageKey);
            if (CmafCompatibilityProfilePolicy.IsValid(profile))
            {
                var browserIdentity = await JS.GetCmafBrowserIdentityAsync();
                _compatibilityProfile = string.Equals(profile!.BrowserIdentity, browserIdentity, StringComparison.Ordinal) ? profile : null;
            }
        }
        catch (Exception ex) when (ex is JsonException or JSException or InvalidOperationException or OperationCanceledException)
        {
            Logger.LogWarning(ex, "Unable to restore Watch preferences");
        }
    }

    private async Task SavePreferencesAsync()
    {
        try
        {
            CmafWatchPreferences watchPreferences = new()
            {
                Protocol = _protocol,
                Quality = _quality,
                PreferredVideo = _preferredVideo,
                PreferredAudio = _preferredAudio,
                FallbackAudio = _fallbackAudio,
                Overrides = _streamOverrides,
                SubtitleTrack = _subtitleTrack
            };
            await BrowserData.WriteAsync(CmafWatchPreferences.StorageKey, watchPreferences);
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException or OperationCanceledException)
        {
            Logger.LogWarning(ex, "Unable to save Watch preferences");
        }
    }

    private GuideCacheProgram? GetCurrentProgram(string? guideNumber)
    {
        if (string.IsNullOrWhiteSpace(guideNumber))
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return _programs.FirstOrDefault(program => program.GuideNumber == guideNumber && program.StartTime <= now && program.EndTime > now);
    }

    private string FormatBitRate(long? bitRate)
    {
        if (!bitRate.HasValue)
        {
            return Text["Watch_Unknown"];
        }

        return bitRate.Value >= 1_000_000
            ? $"{bitRate.Value / 1_000_000d:0.##} Mbps"
            : $"{bitRate.Value / 1_000d:0} kbps";
    }

    private string FormatSourceTrack(ActiveStreamTrack track)
    {
        var details = track.Type == MediaTrackType.Video && track.SourceWidth.HasValue && track.SourceHeight.HasValue
            ? $"{track.SourceWidth}x{track.SourceHeight}"
            : track.Type == MediaTrackType.Audio && track.SourceChannels.HasValue
                ? $"{track.SourceChannels} ch"
                : null;
        return string.Join(
            " · ",
            new[] { $"#{track.SourceIndex}", track.Language, track.Title, track.SourceCodec, details, FormatBitRate(track.SourceBitRate) }
                .Where(value => !string.IsNullOrEmpty(value)));
    }

    private string FormatOutputTrack(ActiveStreamTrack track)
    {
        if (string.Equals(track.OutputCodec, "not-mapped", StringComparison.OrdinalIgnoreCase))
        {
            return Text["Watch_TrackNotIncluded"];
        }
        if (IsWebVttSidecar(track))
        {
            return Text["Watch_WebVttSidecar"];
        }

        var codec = track.OutputCodec == "copy" ? $"{track.SourceCodec} (copy)" : track.OutputCodec;
        var details = track.Type == MediaTrackType.Audio && track.OutputChannels.HasValue ? $"{track.OutputChannels} ch" : null;
        return string.Join(
            " · ",
            new[] { track.OutputTitle, codec, details, FormatBitRate(track.OutputBitRate) }
                .Where(value => !string.IsNullOrEmpty(value)));
    }

    private bool IsPlayingTrack(ActiveStreamTrack track)
    {
        if (!track.IsSelected)
        {
            return false;
        }

        if (track.Type == MediaTrackType.Video && _playerVideo is not null)
        {
            return CodecsMatch(GetOutputCodec(track), _playerVideo.Codec);
        }

        if (track.Type == MediaTrackType.Audio && _playerAudio is not null)
        {
            var labelMatches = string.IsNullOrWhiteSpace(_playerAudio.Label) ||
                string.IsNullOrWhiteSpace(track.OutputTitle) ||
                string.Equals(track.OutputTitle, _playerAudio.Label, StringComparison.OrdinalIgnoreCase);
            return labelMatches &&
                CodecsMatch(GetOutputCodec(track), _playerAudio.Codec) &&
                (!track.OutputChannels.HasValue || !_playerAudio.Channels.HasValue || track.OutputChannels == _playerAudio.Channels);
        }

        return false;
    }

    private static bool IsWebVttSidecar(ActiveStreamTrack track) =>
        track.Type == MediaTrackType.Subtitle &&
        string.Equals(track.OutputCodec, "webvtt", StringComparison.OrdinalIgnoreCase);

    private static string GetOutputCodec(ActiveStreamTrack track) =>
        string.Equals(track.OutputCodec, "copy", StringComparison.OrdinalIgnoreCase)
            ? track.SourceCodec
            : track.OutputCodec;

    private static bool CodecsMatch(string? plannedCodec, string? playerCodec)
    {
        if (string.IsNullOrWhiteSpace(plannedCodec) || string.IsNullOrWhiteSpace(playerCodec))
        {
            return false;
        }

        return NormalizeCodec(plannedCodec) == NormalizeCodec(playerCodec);
    }

    private static string NormalizeCodec(string codec)
    {
        var normalized = codec.Trim().ToLowerInvariant();
        if (normalized is "h264" || normalized.StartsWith("avc1", StringComparison.Ordinal) || normalized.StartsWith("avc3", StringComparison.Ordinal))
        {
            return "h264";
        }
        if (normalized is "hevc" or "h265" || normalized.StartsWith("hvc1", StringComparison.Ordinal) || normalized.StartsWith("hev1", StringComparison.Ordinal))
        {
            return "hevc";
        }
        if (normalized is "aac" || normalized.StartsWith("mp4a.40", StringComparison.Ordinal))
        {
            return "aac";
        }
        if (normalized is "ac3" or "ac-3")
        {
            return "ac3";
        }
        if (normalized is "eac3" or "ec-3")
        {
            return "eac3";
        }
        if (normalized is "ac4" or "ac-4")
        {
            return "ac4";
        }
        return normalized;
    }

    private static string FormatTrackLabel(ActiveStreamTrack track)
    {
        var disposition = string.Join(
            "/",
            new[]
            {
                track.IsDefault ? "default" : null,
                track.IsForced ? "forced" : null,
                track.IsHearingImpaired ? "hearing impaired" : null
            }.Where(value => value is not null));
        var channels = track.SourceChannels.HasValue ? $"{track.SourceChannels} ch" : null;
        return string.Join(
            " · ",
            new[] { track.Language ?? "und", track.Title, track.SourceCodec, channels, string.IsNullOrEmpty(disposition) ? null : disposition }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string FormatFallbackAudio(CmafFallbackAudio fallbackAudio) =>
        fallbackAudio switch
        {
            CmafFallbackAudio.Eac3 => "EAC3",
            CmafFallbackAudio.Ac3 => "AC3",
            CmafFallbackAudio.AacUpTo7Point1 => "AAC up to 7.1",
            CmafFallbackAudio.AacUpTo5Point1 => "AAC up to 5.1",
            _ => "AAC Stereo"
        };

    private void UpdateManualChannelNumber(ChangeEventArgs args)
    {
        _manualChannelNumber = args.Value?.ToString() ?? string.Empty;
        _manualTuneValidationMessage = null;
    }

    private static bool IsValidChannelNumber(string? channelNumber) =>
        !string.IsNullOrWhiteSpace(channelNumber) && ChannelNumberPattern().IsMatch(channelNumber);

    [GeneratedRegex(@"^[0-9]+(?:\.[0-9]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex ChannelNumberPattern();

    private async ValueTask OnLocationChangingAsync(LocationChangingContext context) => await StopStreamAsync();

    private void OnActiveStreamStopRequested(ActiveStreamSnapshot stream)
    {
        if (!_disposed && !_isStopping && string.Equals(stream.ClientId, _clientId, StringComparison.Ordinal))
        {
            _ = InvokeAsync(async () =>
            {
                await StopStreamAsync();
                StateHasChanged();
            });
        }
    }

    /// <summary>Stops playback and releases browser and server resources.</summary>
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _locationChangingRegistration?.Dispose();
        ActiveStreamRegistry.StopRequested -= OnActiveStreamStopRequested;
        await StopStreamAsync();
        _dotNetReference?.Dispose();
        _streamInfoTimer?.Dispose();
    }

    /// <summary>Describes a started shared CMAF presentation returned by the streaming API.</summary>
    /// <param name="SessionId">The server session identifier.</param>
    /// <param name="HlsManifestUrl">The canonical HLS master-playlist URL.</param>
    /// <param name="DashManifestUrl">The DASH manifest URL.</param>
    /// <param name="SourceAudioFallbackApplied">Whether startup retried with only the configured fallback rendition.</param>
    /// <param name="FallbackAudioTitle">The configured fallback rendition title.</param>
    /// <param name="Subtitles">The browser-selectable WebVTT sidecars prepared for the session.</param>
    /// <param name="HasSourceVideoRendition">Whether the presentation contains copied source video.</param>
    /// <param name="SourceVideoCodec">The copied source-video codec used for browser capability testing.</param>
    /// <param name="FallbackAudioCodec">The configured fallback-audio codec used for browser capability testing.</param>
    /// <param name="StopUrl">The optional isolated-worker endpoint used to stop the presentation.</param>
    public sealed record CmafStartResponse(
        string SessionId,
        string HlsManifestUrl,
        string DashManifestUrl,
        bool SourceAudioFallbackApplied = false,
        string? FallbackAudioTitle = null,
        IReadOnlyList<CmafSubtitleResponse>? Subtitles = null,
        bool HasSourceVideoRendition = false,
        string? SourceVideoCodec = null,
        string? FallbackAudioCodec = null,
        string? StopUrl = null);

    /// <summary>Describes one browser-selectable CMAF subtitle sidecar.</summary>
    /// <param name="SourceIndex">The absolute source subtitle index.</param>
    /// <param name="Label">The display label.</param>
    /// <param name="Language">The optional language.</param>
    /// <param name="Url">The incremental WebVTT URL.</param>
    /// <param name="IsEmbeddedClosedCaptions">Whether captions are extracted from video.</param>
    public sealed record CmafSubtitleResponse(int SourceIndex, string Label, string? Language, string Url, bool IsEmbeddedClosedCaptions);

    /// <summary>Describes the result of initializing Shaka Player.</summary>
    /// <param name="Success">Whether the selected manifest loaded.</param>
    /// <param name="Error">The failure message, when unsuccessful.</param>
    /// <param name="ManifestUrl">The effective manifest URL after protocol fallback.</param>
    /// <param name="ErrorCode">The Shaka error code, when initialization fails.</param>
    /// <param name="Audio">The audio variant Shaka selected after loading the presentation.</param>
    /// <param name="Video">The video variant Shaka selected after loading the presentation.</param>
    /// <param name="SourceVideoSupported">Whether the browser reports MSE support for copied source video.</param>
    /// <param name="FallbackAudioSupported">Whether the browser reports MSE support for configured fallback audio.</param>
    public sealed record CmafPlayerResult(
        bool Success,
        string? Error,
        string? ManifestUrl,
        int? ErrorCode = null,
        CmafPlayerAudio? Audio = null,
        CmafPlayerVideo? Video = null,
        bool? SourceVideoSupported = null,
        bool? FallbackAudioSupported = null);

    /// <summary>Describes one asynchronous Shaka player event.</summary>
    /// <param name="Kind">The event category.</param>
    /// <param name="Message">The user-facing event message.</param>
    /// <param name="Details">Optional structured player details.</param>
    /// <param name="SessionId">The server session that owns the player event.</param>
    public sealed record CmafPlayerEvent(string Kind, string Message, JsonElement? Details, string SessionId);
}
