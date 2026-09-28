using Lineup.Core;
using Lineup.Core.Storage;
using Lineup.HDHomeRun.Api.Models;
using Lineup.HDHomeRun.Device.Models;
using Lineup.HDHomeRun.Device.Protocol;
using Lineup.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lineup.Web.Components.Pages;

/// <summary>
/// Provides experimental HLS and DASH playback over one shared CMAF presentation.
/// </summary>
public partial class WatchCmaf : IAsyncDisposable
{
    [Inject]
    private IEpgRepository Repository { get; set; } = default!;

    [Inject]
    private ChannelLineupStore ChannelLineupStore { get; set; } = default!;

    [Inject]
    private IActiveStreamRegistry ActiveStreamRegistry { get; set; } = default!;

    [Inject]
    private IAppSettingsService SettingsService { get; set; } = default!;

    [Inject]
    private IDeviceStateService DeviceState { get; set; } = default!;

    [Inject]
    private IBrowserDataStore BrowserData { get; set; } = default!;

    [Inject]
    private IStatusNotificationService Notifications { get; set; } = default!;

    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    [Inject]
    private NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    private ILogger<WatchCmaf> Logger { get; set; } = default!;

    /// <summary>Gets or sets a channel number supplied through the route.</summary>
    [Parameter]
    public string? ChannelNumber { get; set; }

    private readonly string _clientId = Guid.NewGuid().ToString("N");
    private List<HDHomeRunChannelEpgSegment> _channels = [];
    private List<HDHomeRunProgram> _programs = [];
    private HDHomeRunChannelEpgSegment? _selectedChannel;
    private HDHomeRunProgram? _currentProgram;
    private ActiveStreamSnapshot? _activeStream;
    private CmafPlayerAudio? _playerAudio;
    private CmafPlayerVideo? _playerVideo;
    private CmafStartResponse? _session;
    private DotNetObjectReference<WatchCmaf>? _dotNetReference;
    private IDisposable? _locationChangingRegistration;
    private Timer? _streamInfoTimer;
    private string? _selectedChannelNumber;
    private string? _errorMessage;
    private string? _lastRouteChannelNumber;
    private string? _pendingRouteChannelNumber;
    private string _manualChannelNumber = string.Empty;
    private string? _manualTuneValidationMessage;
    private string _effectiveProtocol = "Waiting";
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
    private DateTime _lastTunerRefreshRequestUtc = DateTime.MinValue;
    private TunerStatus? SelectedTuner => DeviceState.TunerStatuses.FirstOrDefault(tuner => string.Equals(tuner.VirtualChannel, _selectedChannelNumber, StringComparison.Ordinal));
    private string OverrideProtocolValue => _streamOverrides.Enabled ? _streamOverrides.Protocol?.ToString() ?? string.Empty : _protocol.ToString();
    private string OverrideQualityValue => _streamOverrides.Enabled ? _streamOverrides.Quality?.ToString() ?? string.Empty : _quality.ToString();
    private string OverrideVideoValue => _streamOverrides.Enabled ? _streamOverrides.Video?.ToString() ?? string.Empty : _preferredVideo.ToString();
    private string OverrideAudioValue => _streamOverrides.Enabled ? _streamOverrides.Audio?.ToString() ?? string.Empty : _preferredAudio.ToString();
    private string OverrideFallbackAudioValue => _streamOverrides.Enabled ? _streamOverrides.FallbackAudio?.ToString() ?? string.Empty : _fallbackAudio.ToString();
    private CmafPlaybackProtocol EffectiveProtocol => _streamOverrides.Enabled ? _streamOverrides.Protocol ?? CmafPlaybackProtocol.Auto : _protocol;

    /// <summary>Loads channel and programme data and registers stream lifecycle handlers.</summary>
    protected override async Task OnInitializedAsync()
    {
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
        if (_preferencesRestored && !string.IsNullOrWhiteSpace(ChannelNumber))
        {
            var channel = _channels.FirstOrDefault(candidate => string.Equals(candidate.GuideNumber, ChannelNumber, StringComparison.Ordinal));
            _pendingRouteChannelNumber = null;
            await TuneChannelAsync(ChannelNumber, channel);
        }
    }

    /// <summary>Restores preferences and initializes Shaka after the video element is rendered.</summary>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        _isJsInteropReady = true;
        if (firstRender)
        {
            await RestorePreferencesAsync();
            _preferencesRestored = true;
            if (!string.IsNullOrWhiteSpace(_pendingRouteChannelNumber))
            {
                var channelNumber = _pendingRouteChannelNumber;
                var channel = _channels.FirstOrDefault(candidate => string.Equals(candidate.GuideNumber, channelNumber, StringComparison.Ordinal));
                _pendingRouteChannelNumber = null;
                await TuneChannelAsync(channelNumber, channel);
                StateHasChanged();
                return;
            }
        }

        if (!_needsPlayerInit || !_isPlaying || _session is null)
        {
            return;
        }

        _needsPlayerInit = false;
        var (manifestUrl, fallbackUrl) = ResolveManifestUrls(_session);
        _dotNetReference ??= DotNetObjectReference.Create(this);
        try
        {
            var result = await JS.InvokeAsync<CmafPlayerResult>(
                "initCmafPlayer",
                "cmafVideoPlayer",
                manifestUrl,
                fallbackUrl,
                _streamPreferredAudio.ToString(),
                _streamPreferredVideo.ToString(),
                _session.SourceVideoCodec,
                _session.FallbackAudioCodec,
                _session.Subtitles ?? [],
                _dotNetReference);
            if (!result.Success)
            {
                var retryVideo = result.ErrorCode == 4032 &&
                    _session.HasSourceVideoRendition &&
                    _streamPreferredVideo is CmafPreferredVideo.Source or CmafPreferredVideo.Auto &&
                    result.SourceVideoSupported == false;
                var retrySourceAudio = result.ErrorCode == 4032 &&
                    !retryVideo &&
                    _session.FallbackAudioCodec is null &&
                    _streamPreferredAudio is CmafPreferredAudio.Source or CmafPreferredAudio.Auto;
                var retryPackagedFallbackAudio = result.ErrorCode == 4032 &&
                    _session.FallbackAudioCodec is not null &&
                    _streamFallbackAudio != CmafFallbackAudio.AacStereo &&
                    result.FallbackAudioSupported == false;
                var retryAudio = retrySourceAudio || retryPackagedFallbackAudio;
                if ((retryVideo || retryAudio) && _selectedChannelNumber is { } retryChannelNumber)
                {
                    if (_streamPreferredVideo == CmafPreferredVideo.Auto || _streamPreferredAudio == CmafPreferredAudio.Auto)
                    {
                        await InvalidateCompatibilityProfileAsync();
                    }
                    var videoPreference = retryVideo ? CmafPreferredVideo.Fallback : _streamPreferredVideo;
                    var audioPreference = retryAudio ? CmafPreferredAudio.Fallback : _streamPreferredAudio;
                    var fallbackAudio = retryAudio ? CmafFallbackAudio.AacStereo : _streamFallbackAudio;
                    var unavailable = retryVideo && retryAudio
                        ? $"Source video and {FormatFallbackAudio(_streamFallbackAudio)} are unavailable"
                        : retryVideo
                            ? "Source video is unavailable"
                            : $"{FormatFallbackAudio(_streamFallbackAudio)} is unavailable";
                    var replacement = retryVideo && retryAudio
                        ? "H.264 and AAC Stereo"
                        : retryVideo
                            ? "H.264"
                            : "AAC Stereo";
                    Notifications.ShowError($"{unavailable} in this browser. Retrying this stream with {replacement}.");
                    await TuneChannelAsync(retryChannelNumber, _selectedChannel, fallbackAudio, videoPreference, audioPreference);
                    StateHasChanged();
                    return;
                }

                _errorMessage = result.Error ?? "Unable to initialize CMAF playback.";
                await StopStreamAsync();
            }
            else
            {
                _effectiveProtocol = string.Equals(result.ManifestUrl, _session.DashManifestUrl, StringComparison.Ordinal) ? "DASH" : "HLS";
                _playerAudio = result.Audio;
                _playerVideo = result.Video;
                _isPlayerLoading = false;
                await DeviceState.RefreshTunerStatusAsync();
            }
            StateHasChanged();
        }
        catch (JSException ex)
        {
            _errorMessage = $"Unable to initialize CMAF playback: {ex.Message}";
            await StopStreamAsync();
            StateHasChanged();
        }
    }

    /// <summary>Receives asynchronous player fallback and failure events from Shaka.</summary>
    [JSInvokable]
    public async Task OnCmafPlayerEvent(CmafPlayerEvent playerEvent)
    {
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
            if (_streamPreferredVideo == CmafPreferredVideo.Auto)
            {
                await InvalidateCompatibilityProfileAsync();
            }
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
        }
        StateHasChanged();
    }

    private async Task LoadChannelsAsync()
    {
        try
        {
            var guideChannels = await Repository.GetChannelsAsync();
            var lineup = await ChannelLineupStore.ReadAsync();
            _channels = MergeChannels(guideChannels, lineup);
            var now = DateTime.UtcNow;
            _programs = (await Repository.GetProgramsAsync(now, now.AddHours(1))).ToList();
        }
        finally
        {
            _isLoadingChannels = false;
        }
    }

    private static List<HDHomeRunChannelEpgSegment> MergeChannels(IReadOnlyList<HDHomeRunChannelEpgSegment> guideChannels, ChannelLineupSnapshot? lineup)
    {
        if (lineup is null)
        {
            return guideChannels.ToList();
        }

        var guideByNumber = guideChannels
            .Where(channel => !string.IsNullOrWhiteSpace(channel.GuideNumber))
            .DistinctBy(channel => channel.GuideNumber!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(channel => channel.GuideNumber!.Trim(), StringComparer.OrdinalIgnoreCase);
        return lineup.Channels
            .Where(channel => lineup.IsChannelEnabled(channel.GuideNumber))
            .Select(channel =>
            {
                guideByNumber.TryGetValue(channel.GuideNumber.Trim(), out var guide);
                return new HDHomeRunChannelEpgSegment
                {
                    GuideNumber = channel.GuideNumber,
                    GuideName = channel.GuideName,
                    Favorite = channel.Favorite,
                    DRM = channel.DRM,
                    Affiliate = guide?.Affiliate,
                    ImageURL = guide?.ImageURL,
                    Guide = guide?.Guide ?? []
                };
            })
            .ToList();
    }

    private async Task ManualTuneAsync()
    {
        var channelNumber = _manualChannelNumber.Trim();
        if (!IsValidChannelNumber(channelNumber))
        {
            _manualTuneValidationMessage = "Enter a channel number using digits, optionally with one decimal point.";
            return;
        }

        _manualTuneValidationMessage = null;
        await TuneChannelAsync(channelNumber, _channels.FirstOrDefault(channel => channel.GuideNumber == channelNumber));
    }

    private async Task TuneChannelAsync(
        string? channelNumber,
        HDHomeRunChannelEpgSegment? channel,
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
            _manualTuneValidationMessage = "Enter a channel number using digits, optionally with one decimal point.";
            return;
        }

        _isStarting = true;
        _isPlayerLoading = true;
        StateHasChanged();
        try
        {
            var changed = !string.Equals(_selectedChannelNumber, normalized, StringComparison.Ordinal);
            await StopStreamAsync(preserveLoadingIndicator: true);
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
            _errorMessage = null;
            _manualTuneValidationMessage = null;
            _runtimeVideoOverride = streamPreferredVideo;
            _runtimeAudioOverride = streamPreferredAudio;
            _runtimeFallbackAudioOverride = streamFallbackAudio;
            var useMeasuredProfile = _compatibilityProfile is not null && _streamOverrides.Enabled;
            _streamFallbackAudio = streamFallbackAudio ?? (_streamOverrides.Enabled ? _streamOverrides.FallbackAudio ?? _fallbackAudio : _fallbackAudio);
            _streamPreferredVideo = streamPreferredVideo ?? (_streamOverrides.Enabled ? _streamOverrides.Video ?? (useMeasuredProfile ? CmafPreferredVideo.Auto : _preferredVideo) : _preferredVideo);
            _streamPreferredAudio = streamPreferredAudio ??
                (_streamOverrides.Enabled ? _streamOverrides.Audio ?? (useMeasuredProfile ? CmafPreferredAudio.Auto : _preferredAudio) : _preferredAudio);
            _session = _compatibilityProfile is null && !_streamOverrides.Enabled
                ? await JS.InvokeAsync<CmafStartResponse>("startCmafSession", BuildStartUrl(normalized))
                : await JS.InvokeAsync<CmafStartResponse>(
                    "startCmafSession",
                    $"/api/stream/cmaf/start-v2/{Uri.EscapeDataString(normalized)}",
                    BuildStartRequest());
            if (_session.SourceAudioFallbackApplied)
            {
                Notifications.ShowError($"Source audio cannot be packaged by this FFmpeg build. Using {_session.FallbackAudioTitle ?? "fallback audio"} for this stream.");
            }
            _isPlaying = true;
            _sessionUsesBurnIn = _subtitlePresentation == SubtitlePresentation.BurnIn;
            _needsPlayerInit = true;
            _effectiveProtocol = EffectiveProtocol == CmafPlaybackProtocol.Auto ? "DASH (Auto)" : EffectiveProtocol.ToString().ToUpperInvariant();
            StartStreamInfoRefresh();
        }
        catch (JSException ex)
        {
            _isPlayerLoading = false;
            _errorMessage = ex.Message;
            Notifications.ShowError($"Unable to start CMAF playback: {ex.Message}");
        }
        finally
        {
            _isStarting = false;
        }
    }

    private string BuildStartUrl(string channelNumber)
    {
        var url = $"/api/stream/cmaf/start/{Uri.EscapeDataString(channelNumber)}?clientId={_clientId}&quality={_quality}&preferredVideo={_streamPreferredVideo}&preferredAudio={_preferredAudio}&fallbackAudio={_streamFallbackAudio}";
        if (_audioTrack.HasValue)
        {
            url += $"&audioTrack={_audioTrack.Value}";
        }
        if (_subtitleTrack.HasValue)
        {
            url += $"&subtitleTrack={_subtitleTrack.Value}";
            if (_subtitlePresentation is { } presentation && presentation != SubtitlePresentation.Unsupported)
            {
                url += $"&subtitlePresentation={presentation}";
            }
            if (_subtitleEmbedded)
            {
                url += "&embeddedCaptions=true";
            }
        }
        return url;
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
        if (_isStopping)
        {
            return;
        }

        _isStopping = true;
        try
        {
            if (_isJsInteropReady)
            {
                try
                {
                    await JS.InvokeVoidAsync("stopMediaPlayer", "cmafVideoPlayer");
                    if (_session is not null)
                    {
                        await JS.InvokeVoidAsync("stopCmafSession", _session.SessionId);
                    }
                }
                catch (Exception ex) when (ex is JSDisconnectedException or ObjectDisposedException)
                {
                    Logger.LogDebug(ex, "Browser disconnected while stopping CMAF playback");
                }
            }
            ActiveStreamRegistry.RequestStopByClientId(_clientId);

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
        }
        finally
        {
            _isStopping = false;
        }
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
            await DeviceState.RefreshTunerStatusAsync();
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
            else if (preferences?.AacFallback is { } legacyFallback && legacyFallback != WatchAudioOutput.Source)
            {
                _fallbackAudio = legacyFallback switch
                {
                    WatchAudioOutput.UpTo5Point1 => CmafFallbackAudio.AacUpTo5Point1,
                    WatchAudioOutput.UpTo7Point1 => CmafFallbackAudio.AacUpTo7Point1,
                    _ => CmafFallbackAudio.AacStereo
                };
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
                    Logger.LogWarning(ex, "Ignoring invalid Watch CMAF stream overrides");
                }
            }
            _subtitleTrack = preferences?.SubtitleTrack;
            var profile = await BrowserData.ReadAsync<CmafCompatibilityProfile>(CmafCompatibilityProfile.StorageKey);
            if (CmafCompatibilityProfilePolicy.IsValid(profile))
            {
                var browserIdentity = await JS.InvokeAsync<string>("getCmafBrowserIdentity");
                _compatibilityProfile = string.Equals(profile!.BrowserIdentity, browserIdentity, StringComparison.Ordinal) ? profile : null;
            }
            if (_compatibilityProfile is not null && preferences?.PolicyVersion != CmafWatchPreferences.CurrentPolicyVersion)
            {
                _preferredVideo = CmafPreferredVideo.Auto;
                _preferredAudio = CmafPreferredAudio.Auto;
            }
        }
        catch (Exception ex) when (ex is JsonException or JSException or InvalidOperationException or OperationCanceledException)
        {
            Logger.LogWarning(ex, "Unable to restore Watch CMAF preferences");
        }
    }

    private async Task SavePreferencesAsync()
    {
        try
        {
            CmafWatchPreferences watchPreferences = new()
            {
                PolicyVersion = CmafWatchPreferences.CurrentPolicyVersion,
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
            Logger.LogWarning(ex, "Unable to save Watch CMAF preferences");
        }
    }

    private async Task InvalidateCompatibilityProfileAsync()
    {
        _compatibilityProfile = null;
        try
        {
            await BrowserData.RemoveAsync(CmafCompatibilityProfile.StorageKey);
            Notifications.ShowError("The saved browser compatibility profile contradicted runtime playback and was cleared. Run Watch Test again to refresh it.");
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException or OperationCanceledException)
        {
            Logger.LogWarning(ex, "Unable to clear a contradicted Watch CMAF compatibility profile");
        }
    }

    private HDHomeRunProgram? GetCurrentProgram(string? guideNumber)
    {
        if (string.IsNullOrWhiteSpace(guideNumber))
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return _programs.FirstOrDefault(program => program.GuideNumber == guideNumber && program.StartTime <= now && program.EndTime > now);
    }

    private static string FormatBitRate(long? bitRate)
    {
        if (!bitRate.HasValue)
        {
            return "unknown";
        }

        return bitRate.Value >= 1_000_000
            ? $"{bitRate.Value / 1_000_000d:0.##} Mbps"
            : $"{bitRate.Value / 1_000d:0} kbps";
    }

    private static string FormatSourceTrack(ActiveStreamTrack track)
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

    private static string FormatOutputTrack(ActiveStreamTrack track)
    {
        if (string.Equals(track.OutputCodec, "not-mapped", StringComparison.OrdinalIgnoreCase))
        {
            return "not included";
        }
        if (IsWebVttSidecar(track))
        {
            return "WebVTT sidecar";
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
        if (!_disposed && string.Equals(stream.ClientId, _clientId, StringComparison.Ordinal))
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
    /// <param name="HlsCompatibilityManifestUrl">The compatibility HLS master-playlist URL.</param>
    /// <param name="DashManifestUrl">The DASH manifest URL.</param>
    /// <param name="SourceAudioFallbackApplied">Whether startup retried with only the configured fallback rendition.</param>
    /// <param name="FallbackAudioTitle">The configured fallback rendition title.</param>
    /// <param name="Subtitles">The browser-selectable WebVTT sidecars prepared for the session.</param>
    /// <param name="HasSourceVideoRendition">Whether the presentation contains copied source video.</param>
    /// <param name="SourceVideoCodec">The copied source-video codec used for browser capability testing.</param>
    /// <param name="FallbackAudioCodec">The configured fallback-audio codec used for browser capability testing.</param>
    public sealed record CmafStartResponse(
        string SessionId,
        string HlsManifestUrl,
        string HlsCompatibilityManifestUrl,
        string DashManifestUrl,
        bool SourceAudioFallbackApplied = false,
        string? FallbackAudioTitle = null,
        IReadOnlyList<CmafSubtitleResponse>? Subtitles = null,
        bool HasSourceVideoRendition = false,
        string? SourceVideoCodec = null,
        string? FallbackAudioCodec = null);

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

    /// <summary>Describes the audio variant actively selected by Shaka Player.</summary>
    /// <param name="Label">The manifest rendition label.</param>
    /// <param name="Codec">The browser codec identifier.</param>
    /// <param name="Channels">The reported channel count.</param>
    /// <param name="Id">The Shaka audio-stream identifier.</param>
    /// <param name="Language">The manifest language.</param>
    public sealed record CmafPlayerAudio(string? Label, string? Codec, int? Channels, string? Id = null, string? Language = null)
    {
        /// <summary>Reads active-audio metadata from an asynchronous player event.</summary>
        /// <param name="details">The structured player event details.</param>
        /// <returns>The active audio metadata, or <see langword="null"/> when unavailable.</returns>
        public static CmafPlayerAudio? FromDetails(JsonElement? details)
        {
            if (details is not { ValueKind: JsonValueKind.Object } value)
            {
                return null;
            }

            var label = value.TryGetProperty("label", out var labelProperty) ? labelProperty.GetString() : null;
            var id = value.TryGetProperty("id", out var idProperty) ? idProperty.GetString() : null;
            var language = value.TryGetProperty("language", out var languageProperty) ? languageProperty.GetString() : null;
            var codec = value.TryGetProperty("codec", out var codecProperty) ? codecProperty.GetString() : null;
            int? channels = value.TryGetProperty("channels", out var channelsProperty) && channelsProperty.TryGetInt32(out var channelCount)
                ? channelCount
                : null;
            return new CmafPlayerAudio(label, codec, channels, id, language);
        }
    }

    /// <summary>Describes the video variant actively selected by Shaka Player.</summary>
    /// <param name="Codec">The browser codec identifier.</param>
    /// <param name="Width">The reported coded width.</param>
    /// <param name="Height">The reported coded height.</param>
    public sealed record CmafPlayerVideo(string? Codec, int? Width, int? Height)
    {
        /// <summary>Reads active-video metadata from an asynchronous player event.</summary>
        /// <param name="details">The structured player event details.</param>
        /// <returns>The active video metadata, or <see langword="null"/> when unavailable.</returns>
        public static CmafPlayerVideo? FromDetails(JsonElement? details)
        {
            if (details is not { ValueKind: JsonValueKind.Object } value)
            {
                return null;
            }

            var codec = value.TryGetProperty("codec", out var codecProperty) ? codecProperty.GetString() : null;
            int? width = value.TryGetProperty("width", out var widthProperty) && widthProperty.TryGetInt32(out var widthValue) ? widthValue : null;
            int? height = value.TryGetProperty("height", out var heightProperty) && heightProperty.TryGetInt32(out var heightValue) ? heightValue : null;
            return new CmafPlayerVideo(codec, width, height);
        }
    }

    /// <summary>Describes one asynchronous Shaka player event.</summary>
    /// <param name="Kind">The event category.</param>
    /// <param name="Message">The user-facing event message.</param>
    /// <param name="Details">Optional structured player details.</param>
    public sealed record CmafPlayerEvent(string Kind, string Message, JsonElement? Details);
}
