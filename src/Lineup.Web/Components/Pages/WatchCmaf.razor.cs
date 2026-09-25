using Lineup.Core;
using Lineup.Core.Storage;
using Lineup.HDHomeRun.Api.Models;
using Lineup.HDHomeRun.Device.Models;
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
    private const string PreferencesStorageKey = "lineup-watch-cmaf-preferences-v1";

    [Inject]
    private IEpgRepository Repository { get; set; } = default!;

    [Inject]
    private ChannelLineupStore ChannelLineupStore { get; set; } = default!;

    [Inject]
    private IActiveStreamRegistry ActiveStreamRegistry { get; set; } = default!;

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
    private CmafPreferredAudio _preferredAudio = CmafPreferredAudio.Source;
    private CmafFallbackAudio _fallbackAudio = CmafFallbackAudio.AacStereo;
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
    private bool _disposed;

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
                _preferredAudio.ToString(),
                _session.Subtitles ?? [],
                _dotNetReference);
            if (!result.Success)
            {
                _errorMessage = result.Error ?? "Unable to initialize CMAF playback.";
                await StopStreamAsync();
            }
            else
            {
                _effectiveProtocol = string.Equals(result.ManifestUrl, _session.DashManifestUrl, StringComparison.Ordinal) ? "DASH" : "HLS";
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
    public Task OnCmafPlayerEvent(CmafPlayerEvent playerEvent)
    {
        if (string.Equals(playerEvent.Kind, "audio-fallback", StringComparison.Ordinal))
        {
            Notifications.ShowError(playerEvent.Message);
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
        return Task.CompletedTask;
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

    private async Task TuneChannelAsync(string? channelNumber, HDHomeRunChannelEpgSegment? channel)
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
        try
        {
            var changed = !string.Equals(_selectedChannelNumber, normalized, StringComparison.Ordinal);
            await StopStreamAsync();
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
            _session = await JS.InvokeAsync<CmafStartResponse>("startCmafSession", BuildStartUrl(normalized));
            if (_session.SourceAudioFallbackApplied)
            {
                Notifications.ShowError($"Source audio cannot be packaged by this FFmpeg build. Using {_session.FallbackAudioTitle ?? "fallback audio"} for this stream.");
            }
            _isPlaying = true;
            _sessionUsesBurnIn = _subtitlePresentation == SubtitlePresentation.BurnIn;
            _needsPlayerInit = true;
            _effectiveProtocol = _protocol == CmafPlaybackProtocol.Auto ? "DASH (Auto)" : _protocol.ToString().ToUpperInvariant();
            StartStreamInfoRefresh();
        }
        catch (JSException ex)
        {
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
        var url = $"/api/stream/cmaf/start/{Uri.EscapeDataString(channelNumber)}?clientId={_clientId}&quality={_quality}&preferredAudio={_preferredAudio}&fallbackAudio={_fallbackAudio}";
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

    private (string ManifestUrl, string? FallbackUrl) ResolveManifestUrls(CmafStartResponse session) =>
        _protocol switch
        {
            CmafPlaybackProtocol.Hls => (session.HlsManifestUrl, null),
            CmafPlaybackProtocol.Dash => (session.DashManifestUrl, null),
            _ => (session.DashManifestUrl, session.HlsManifestUrl)
        };

    private async Task ChangeProtocol(ChangeEventArgs args)
    {
        if (Enum.TryParse<CmafPlaybackProtocol>(args.Value?.ToString(), out var value))
        {
            _protocol = value;
            await SavePreferencesAndRestartAsync();
        }
    }

    private async Task ChangeQuality(ChangeEventArgs args)
    {
        if (Enum.TryParse<WebPlayerQuality>(args.Value?.ToString(), out var value))
        {
            _quality = value;
            await SavePreferencesAndRestartAsync();
        }
    }

    private async Task ChangePreferredAudio(ChangeEventArgs args)
    {
        if (Enum.TryParse<CmafPreferredAudio>(args.Value?.ToString(), out var value))
        {
            _preferredAudio = value;
            await SavePreferencesAndRestartAsync();
        }
    }

    private async Task ChangeFallbackAudio(ChangeEventArgs args)
    {
        if (Enum.TryParse<CmafFallbackAudio>(args.Value?.ToString(), out var value))
        {
            _fallbackAudio = value;
            await SavePreferencesAndRestartAsync();
        }
    }

    private async Task ChangeAudioTrack(ChangeEventArgs args)
    {
        if (int.TryParse(args.Value?.ToString(), out var value))
        {
            _audioTrack = value;
            await RestartCurrentStreamAsync();
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

    private async Task StopStreamAsync()
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
            _sessionUsesBurnIn = false;
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
        if (_isPlaying)
        {
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
            var preferences = await BrowserData.ReadAsync<CmafPreferences>(PreferencesStorageKey);
            if (preferences is null)
            {
                return;
            }
            if (Enum.IsDefined(preferences.Protocol))
            {
                _protocol = preferences.Protocol;
            }
            if (Enum.IsDefined(preferences.Quality))
            {
                _quality = preferences.Quality;
            }
            if (Enum.IsDefined(preferences.PreferredAudio))
            {
                _preferredAudio = preferences.PreferredAudio;
            }
            if (preferences.FallbackAudio is { } fallbackAudio && Enum.IsDefined(fallbackAudio))
            {
                _fallbackAudio = fallbackAudio;
            }
            else if (preferences.AacFallback is { } legacyFallback && legacyFallback != WatchAudioOutput.Source)
            {
                _fallbackAudio = legacyFallback switch
                {
                    WatchAudioOutput.UpTo5Point1 => CmafFallbackAudio.AacUpTo5Point1,
                    WatchAudioOutput.UpTo7Point1 => CmafFallbackAudio.AacUpTo7Point1,
                    _ => CmafFallbackAudio.AacStereo
                };
            }
            _subtitleTrack = preferences.SubtitleTrack;
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
            await BrowserData.WriteAsync(
                PreferencesStorageKey,
                new CmafPreferences
                {
                    Protocol = _protocol,
                    Quality = _quality,
                    PreferredAudio = _preferredAudio,
                    FallbackAudio = _fallbackAudio,
                    SubtitleTrack = _subtitleTrack
                });
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException or OperationCanceledException)
        {
            Logger.LogWarning(ex, "Unable to save Watch CMAF preferences");
        }
    }

    private HDHomeRunProgram? GetCurrentProgram(string? guideNumber)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return _programs.FirstOrDefault(program => program.GuideNumber == guideNumber && program.StartTime <= now && program.EndTime > now);
    }

    private static string FormatTrackLabel(ActiveStreamTrack track)
    {
        var channels = track.SourceChannels.HasValue ? $"{track.SourceChannels} ch" : null;
        return string.Join(" · ", new[] { track.Language ?? "und", track.Title, track.SourceCodec, channels }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

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

    private sealed record CmafPreferences
    {
        public CmafPlaybackProtocol Protocol { get; init; }
        public WebPlayerQuality Quality { get; init; }
        public CmafPreferredAudio PreferredAudio { get; init; }
        public CmafFallbackAudio? FallbackAudio { get; init; }
        public WatchAudioOutput? AacFallback { get; init; }
        public int? SubtitleTrack { get; init; }
    }

    /// <summary>Describes a started shared CMAF presentation returned by the streaming API.</summary>
    /// <param name="SessionId">The server session identifier.</param>
    /// <param name="HlsManifestUrl">The canonical HLS master-playlist URL.</param>
    /// <param name="HlsCompatibilityManifestUrl">The compatibility HLS master-playlist URL.</param>
    /// <param name="DashManifestUrl">The DASH manifest URL.</param>
    /// <param name="SourceAudioFallbackApplied">Whether startup retried with only the configured fallback rendition.</param>
    /// <param name="FallbackAudioTitle">The configured fallback rendition title.</param>
    /// <param name="Subtitles">The browser-selectable WebVTT sidecars prepared for the session.</param>
    public sealed record CmafStartResponse(
        string SessionId,
        string HlsManifestUrl,
        string HlsCompatibilityManifestUrl,
        string DashManifestUrl,
        bool SourceAudioFallbackApplied = false,
        string? FallbackAudioTitle = null,
        IReadOnlyList<CmafSubtitleResponse>? Subtitles = null);

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
    public sealed record CmafPlayerResult(bool Success, string? Error, string? ManifestUrl);

    /// <summary>Describes one asynchronous Shaka player event.</summary>
    /// <param name="Kind">The event category.</param>
    /// <param name="Message">The user-facing event message.</param>
    /// <param name="Details">Optional structured player details.</param>
    public sealed record CmafPlayerEvent(string Kind, string Message, JsonElement? Details);
}
