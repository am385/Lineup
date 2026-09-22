using Lineup.Core;
using Lineup.Core.Storage;
using Lineup.HDHomeRun.Api.Models;
using Lineup.HDHomeRun.Device.Models;
using Lineup.HDHomeRun.Device.Protocol;
using Lineup.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lineup.Web.Components.Pages;

/// <summary>
/// Represents watch.
/// </summary>
public partial class Watch : IAsyncDisposable
{
    private const string PreferencesStorageKey = "lineup-watch-preferences-v1";

    [Inject]
    private IEpgRepository Repository { get; set; } = default!;

    [Inject]
    private ChannelLineupStore ChannelLineupStore { get; set; } = default!;

    [Inject]
    private IAppSettingsService SettingsService { get; set; } = default!;

    [Inject]
    private IDeviceStateService DeviceState { get; set; } = default!;

    [Inject]
    private IActiveStreamRegistry ActiveStreamRegistry { get; set; } = default!;

    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    [Inject]
    private NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    private ILogger<Watch> Logger { get; set; } = default!;

    /// <summary>
    /// Gets or sets channel number.
    /// </summary>
    [Parameter]
    public string? ChannelNumber { get; set; }

    private List<HDHomeRunChannelEpgSegment> _channels = [];
    private List<HDHomeRunProgram> _programs = [];
    private HDHomeRunChannelEpgSegment? _selectedChannel;
    private string? _selectedChannelNumber;
    private string _manualChannelNumber = string.Empty;
    private string? _manualTuneValidationMessage;
    private HDHomeRunProgram? _currentProgram;
    private string? _streamUrl;
    private string? _errorMessage;
    private bool _isPlaying;
    private bool _isLoadingChannels = true;
    private bool _showCodecError;
    private bool _needsPlayerInit;
    private bool _isJsInteropReady;
    private bool _preferencesRestored;
    private bool _disposed;
    private readonly string _clientId = Guid.NewGuid().ToString("N");
    private string? _lastRouteChannelNumber;
    private string? _pendingRouteChannelNumber;
    private Timer? _streamInfoTimer;
    private ActiveStreamSnapshot? _activeStream;
    private DateTime _lastTunerRefreshRequestUtc = DateTime.MinValue;
    private WebPlayerQuality _quality = WebPlayerQuality.AppDefault;
    private WatchAudioOutput _audioOutput = WatchAudioOutput.Stereo;
    private int? _audioTrack;
    private int? _subtitleTrack;
    private bool _subtitlesEnabled;
    private bool _subtitleRestoreApplied;
    private WatchSubtitlePreference? _subtitlePreference;
    private bool IsContentProtectedError => _errorMessage?.Contains("Content Protection Required", StringComparison.OrdinalIgnoreCase) == true;
    private TunerStatus? SelectedTuner => DeviceState.TunerStatuses.FirstOrDefault(tuner => string.Equals(tuner.VirtualChannel, _selectedChannelNumber, StringComparison.Ordinal));

    /// <summary>
    /// Performs the on initialized operation.
    /// </summary>
    protected override async Task OnInitializedAsync()
    {
        DeviceState.OnStateChanged += OnDeviceStateChanged;
        ActiveStreamRegistry.StopRequested += OnActiveStreamStopRequested;
        await LoadChannelsAsync();
    }

    /// <summary>
    /// Applies channel numbers supplied through the Watch route.
    /// </summary>
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

    /// <summary>
    /// Performs the on after render operation.
    /// </summary>
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

        // Initialize fMP4 player after render when we have a stream URL
        if (_isPlaying && !string.IsNullOrEmpty(_streamUrl) && _needsPlayerInit)
        {
            _needsPlayerInit = false;
            try
            {
                var channelNumber = _selectedChannelNumber;
                if (string.IsNullOrWhiteSpace(channelNumber))
                {
                    _errorMessage = "The selected channel does not have a valid channel number.";
                    await StopStreamAsync();
                    StateHasChanged();
                    return;
                }

                var diagnosticUrl = $"/api/stream/test/{Uri.EscapeDataString(channelNumber)}?transcode=none";
                var error = _subtitleTrack.HasValue
                    ? await JS.InvokeAsync<string?>(
                        "initFmp4Player",
                        "videoPlayer",
                        _streamUrl,
                        diagnosticUrl,
                        $"/api/stream/fmp4/client/{_clientId}/subtitles.vtt")
                    : await JS.InvokeAsync<string?>("initFmp4Player", "videoPlayer", _streamUrl, diagnosticUrl);
                if (!string.IsNullOrWhiteSpace(error))
                {
                    _errorMessage = error;
                    await StopStreamAsync();
                    StateHasChanged();
                }
                else
                {
                    await DeviceState.RefreshTunerStatusAsync();
                }
            }
            catch (Exception ex)
            {
                _errorMessage = $"Failed to initialize player: {ex.Message}";
                StateHasChanged();
            }
        }
    }

    private async Task LoadChannelsAsync()
    {
        _isLoadingChannels = true;
        StateHasChanged();

        try
        {
            var guideChannels = await Repository.GetChannelsAsync();
            var channelLineup = await ChannelLineupStore.ReadAsync();
            _channels = MergeChannels(guideChannels, channelLineup);

            // Load current programs for "now playing" display
            var now = DateTime.UtcNow;
            var endTime = now.AddHours(1);
            _programs = await Repository.GetProgramsAsync(now, endTime);
        }
        finally
        {
            _isLoadingChannels = false;
        }
    }

    private static List<HDHomeRunChannelEpgSegment> MergeChannels(IReadOnlyList<HDHomeRunChannelEpgSegment> guideChannels, ChannelLineupSnapshot? channelLineup)
    {
        if (channelLineup == null)
        {
            return guideChannels.ToList();
        }

        var guideChannelsByNumber = guideChannels
            .Where(channel => !string.IsNullOrWhiteSpace(channel.GuideNumber))
            .DistinctBy(channel => channel.GuideNumber!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(channel => channel.GuideNumber!.Trim(), StringComparer.OrdinalIgnoreCase);
        return channelLineup.Channels
            .Where(channel => channelLineup.IsChannelEnabled(channel.GuideNumber))
            .Select(channel => MergeChannel(channel, guideChannelsByNumber))
            .ToList();
    }

    private static HDHomeRunChannelEpgSegment MergeChannel(HDHomeRunChannel channel, IReadOnlyDictionary<string, HDHomeRunChannelEpgSegment> guideChannels)
    {
        guideChannels.TryGetValue(channel.GuideNumber.Trim(), out var guideChannel);
        return new HDHomeRunChannelEpgSegment
        {
            GuideNumber = channel.GuideNumber,
            GuideName = channel.GuideName,
            Affiliate = guideChannel?.Affiliate,
            ImageURL = guideChannel?.ImageURL,
            DRM = channel.DRM,
            Favorite = channel.Favorite,
            Guide = guideChannel?.Guide ?? []
        };
    }

    private async Task SelectChannel(HDHomeRunChannelEpgSegment channel)
    {
        await TuneChannelAsync(channel.GuideNumber, channel);
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
        var channel = _channels.FirstOrDefault(candidate => string.Equals(candidate.GuideNumber, channelNumber, StringComparison.Ordinal));
        await TuneChannelAsync(channelNumber, channel);
    }

    private async Task TuneChannelAsync(string? channelNumber, HDHomeRunChannelEpgSegment? channel)
    {
        var normalizedChannelNumber = channelNumber?.Trim();
        _manualChannelNumber = normalizedChannelNumber ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedChannelNumber) || (channel is null && !IsValidChannelNumber(normalizedChannelNumber)))
        {
            _manualTuneValidationMessage = "Enter a channel number using digits, optionally with one decimal point.";
            return;
        }

        var validChannelNumber = normalizedChannelNumber!;
        var channelChanged = !string.Equals(_selectedChannelNumber, validChannelNumber, StringComparison.Ordinal);
        await StopStreamAsync();
        if (channelChanged)
        {
            _audioTrack = null;
            _subtitleTrack = null;
            _subtitleRestoreApplied = !_subtitlesEnabled;
        }

        _selectedChannel = channel;
        _selectedChannelNumber = validChannelNumber;
        _manualTuneValidationMessage = null;
        _errorMessage = null;

        _streamUrl = BuildStreamUrl(validChannelNumber);
        _isPlaying = true;
        _needsPlayerInit = true;
        StartStreamInfoRefresh();
        if (TryApplySubtitlePreference())
        {
            _streamUrl = BuildStreamUrl(validChannelNumber);
        }

        _currentProgram = GetCurrentProgram(validChannelNumber);
        _showCodecError = false;
    }

    private async Task ChangeQuality(ChangeEventArgs args)
    {
        if (!Enum.TryParse<WebPlayerQuality>(args.Value?.ToString(), out var quality))
        {
            return;
        }

        _quality = quality;
        await SavePreferencesAsync();
        if (_selectedChannelNumber is { } channelNumber && _isPlaying)
        {
            await TuneChannelAsync(channelNumber, _selectedChannel);
        }
    }

    private async Task ChangeAudioTrack(ChangeEventArgs args)
    {
        if (!int.TryParse(args.Value?.ToString(), out var index))
        {
            return;
        }

        _audioTrack = index;
        await RestartCurrentStreamAsync();
    }

    private async Task ChangeAudioOutput(ChangeEventArgs args)
    {
        if (!Enum.TryParse<WatchAudioOutput>(args.Value?.ToString(), out var audioOutput))
        {
            return;
        }

        _audioOutput = audioOutput;
        await SavePreferencesAsync();
        await RestartCurrentStreamAsync();
    }

    private async Task ChangeSubtitleTrack(ChangeEventArgs args)
    {
        var subtitleTrack = int.TryParse(args.Value?.ToString(), out var index) && index >= 0 ? index : (int?)null;
        if (subtitleTrack.HasValue)
        {
            var selectedTrack = _activeStream?.Tracks.FirstOrDefault(
                track => track.Type == MediaTrackType.Subtitle &&
                    track.SourceIndex == subtitleTrack.Value &&
                    track.SubtitlePresentation != SubtitlePresentation.Unsupported);
            if (selectedTrack == null)
            {
                return;
            }

            _subtitlePreference = WatchSubtitlePreference.FromTrack(selectedTrack);
        }

        _subtitleTrack = subtitleTrack;
        _subtitlesEnabled = subtitleTrack.HasValue;
        _subtitleRestoreApplied = true;
        await SavePreferencesAsync();
        await RestartCurrentStreamAsync();
    }

    private Task RestartCurrentStreamAsync() =>
        _selectedChannelNumber is { } channelNumber && _isPlaying
            ? TuneChannelAsync(channelNumber, _selectedChannel)
            : Task.CompletedTask;

    private string BuildStreamUrl(string channelNumber)
    {
        var url = $"/api/stream/fmp4/{Uri.EscapeDataString(channelNumber)}?clientId={_clientId}&quality={_quality}&audioOutput={_audioOutput}";
        if (_audioTrack.HasValue)
        {
            url += $"&audioTrack={_audioTrack.Value}";
        }
        if (_subtitleTrack.HasValue)
        {
            url += $"&subtitleTrack={_subtitleTrack.Value}";
            var subtitlePresentation = SubtitleCapabilityPolicy.Classify(_subtitlePreference?.SourceCodec);
            if (subtitlePresentation != SubtitlePresentation.Unsupported)
            {
                url += $"&subtitlePresentation={subtitlePresentation}";
            }
            if (_subtitlePreference?.IsEmbeddedClosedCaptions == true)
            {
                url += "&embeddedCaptions=true";
            }
        }
        return url;
    }

    private async Task StopStreamAsync()
    {
        if (_isPlaying && _isJsInteropReady)
        {
            try
            {
                await JS.InvokeVoidAsync("stopMediaPlayer", "videoPlayer");
            }
            catch (JSDisconnectedException)
            {
                // The circuit is already gone, so the browser has also released the media request.
            }
            catch (ObjectDisposedException)
            {
                // Component disposal can race with renderer shutdown.
            }
        }

        _isPlaying = false;
        _streamUrl = null;
        _currentProgram = null;
        _showCodecError = false;
        _needsPlayerInit = false;
        _activeStream = null;
        _streamInfoTimer?.Dispose();
        _streamInfoTimer = null;
    }

    private async Task StopStream()
    {
        await StopStreamAsync();
    }

    private HDHomeRunProgram? GetCurrentProgram(string? guideNumber)
    {
        if (string.IsNullOrEmpty(guideNumber))
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return _programs.FirstOrDefault(p =>
            p.GuideNumber == guideNumber &&
            p.StartTime <= now &&
            p.EndTime > now);
    }

    private async Task CopyStreamUrlToClipboard()
    {
        if (_selectedChannelNumber is { } channelNumber)
        {
            var directUrl = $"http://{SettingsService.Settings.DeviceAddress}:5004/auto/v{Uri.EscapeDataString(channelNumber)}";
            try
            {
                await JS.InvokeVoidAsync("navigator.clipboard.writeText", directUrl);
                _errorMessage = "Stream URL copied to clipboard!";
                StateHasChanged();

                // Clear message after 2 seconds
                await Task.Delay(2000);
                if (_errorMessage == "Stream URL copied to clipboard!")
                {
                    _errorMessage = null;
                    StateHasChanged();
                }
            }
            catch
            {
                _errorMessage = "Failed to copy to clipboard";
            }
        }
    }

    private void StartStreamInfoRefresh()
    {
        _streamInfoTimer?.Dispose();
        RefreshStreamInfo();
        _streamInfoTimer = new Timer(
            _ => _ = InvokeAsync(RefreshStreamInfoAsync),
            null,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));
    }

    private async Task RefreshStreamInfoAsync()
    {
        if (_disposed)
        {
            return;
        }

        RefreshStreamInfo();
        StateHasChanged();

        if (TryApplySubtitlePreference())
        {
            await RestartCurrentStreamAsync();
            StateHasChanged();
            return;
        }

        if (_isPlaying && DateTime.UtcNow - _lastTunerRefreshRequestUtc >= TimeSpan.FromSeconds(5))
        {
            _lastTunerRefreshRequestUtc = DateTime.UtcNow;
            await DeviceState.RefreshTunerStatusAsync();
        }
    }

    private void RefreshStreamInfo()
    {
        var activeStreams = ActiveStreamRegistry.GetActiveStreams();
        _activeStream = activeStreams
            .Where(stream => string.Equals(stream.ClientId, _clientId, StringComparison.Ordinal))
            .OrderByDescending(stream => stream.StartedAtUtc)
            .FirstOrDefault()
            ?? activeStreams
                .Where(stream => stream.ClientId is null && string.Equals(stream.Channel, _selectedChannelNumber, StringComparison.Ordinal))
                .OrderByDescending(stream => stream.StartedAtUtc)
                .FirstOrDefault();
        var selectedAudio = _activeStream?.Tracks.FirstOrDefault(track => track.Type == MediaTrackType.Audio && track.IsSelected);
        if (!_audioTrack.HasValue && selectedAudio is not null)
        {
            _audioTrack = selectedAudio.SourceIndex;
        }
    }

    private bool TryApplySubtitlePreference()
    {
        if (!_isPlaying || !_subtitlesEnabled || _subtitleRestoreApplied || _subtitlePreference == null || _activeStream == null)
        {
            return false;
        }

        var matchingTrack = _subtitlePreference.FindMatch(_activeStream.Tracks);
        if (matchingTrack == null)
        {
            return false;
        }

        _subtitleTrack = matchingTrack.SourceIndex;
        _subtitleRestoreApplied = true;
        return true;
    }

    private async Task RestorePreferencesAsync()
    {
        try
        {
            var json = await JS.InvokeAsync<string?>("localStorage.getItem", PreferencesStorageKey);
            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            var preferences = JsonSerializer.Deserialize<WatchPreferences>(json);
            if (preferences == null)
            {
                return;
            }

            if (Enum.IsDefined(preferences.Quality))
            {
                _quality = preferences.Quality;
            }

            if (Enum.IsDefined(preferences.AudioOutput))
            {
                _audioOutput = preferences.AudioOutput;
            }

            _subtitlesEnabled = preferences.SubtitlesEnabled && preferences.Subtitle != null;
            _subtitlePreference = preferences.Subtitle;
            _subtitleRestoreApplied = !_subtitlesEnabled;
        }
        catch (JsonException ex)
        {
            Logger.LogWarning(ex, "Ignoring invalid saved Watch preferences");
        }
        catch (JSDisconnectedException ex)
        {
            Logger.LogDebug(ex, "The browser disconnected while restoring Watch preferences");
        }
        catch (JSException ex)
        {
            Logger.LogWarning(ex, "Unable to restore Watch preferences from browser storage");
        }
        catch (InvalidOperationException ex)
        {
            Logger.LogDebug(ex, "Browser storage is not available while rendering the Watch page");
        }
        catch (OperationCanceledException ex)
        {
            Logger.LogWarning(ex, "Restoring Watch preferences timed out");
        }
    }

    private async Task SavePreferencesAsync()
    {
        var preferences = new WatchPreferences
        {
            Quality = _quality,
            AudioOutput = _audioOutput,
            SubtitlesEnabled = _subtitlesEnabled,
            Subtitle = _subtitlePreference
        };

        try
        {
            await JS.InvokeVoidAsync("localStorage.setItem", PreferencesStorageKey, JsonSerializer.Serialize(preferences));
        }
        catch (JSDisconnectedException ex)
        {
            Logger.LogDebug(ex, "The browser disconnected while saving Watch preferences");
        }
        catch (JSException ex)
        {
            Logger.LogWarning(ex, "Unable to save Watch preferences to browser storage");
        }
        catch (InvalidOperationException ex)
        {
            Logger.LogDebug(ex, "Browser storage is not available while saving Watch preferences");
        }
        catch (OperationCanceledException ex)
        {
            Logger.LogWarning(ex, "Saving Watch preferences timed out");
        }
    }

    private static bool IsValidChannelNumber(string? channelNumber)
    {
        return !string.IsNullOrWhiteSpace(channelNumber) &&
            ChannelNumberPattern().IsMatch(channelNumber);
    }

    private void UpdateManualChannelNumber(ChangeEventArgs args)
    {
        _manualChannelNumber = args.Value?.ToString() ?? string.Empty;
        _manualTuneValidationMessage = null;
    }

    [GeneratedRegex(@"^[0-9]+(?:\.[0-9]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex ChannelNumberPattern();

    private void OnActiveStreamStopRequested(ActiveStreamSnapshot stream)
    {
        if (!_disposed && string.Equals(stream.ClientId, _clientId, StringComparison.Ordinal))
        {
            _ = InvokeAsync(HandleActiveStreamStopAsync);
        }
    }

    private async Task HandleActiveStreamStopAsync()
    {
        await StopStreamAsync();
        StateHasChanged();
    }

    private void OnDeviceStateChanged()
    {
        if (!_disposed)
        {
            _ = InvokeAsync(StateHasChanged);
        }
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
        var codec = track.OutputCodec == "copy" ? $"{track.SourceCodec} (copy)" : track.OutputCodec;
        var details = track.Type == MediaTrackType.Audio && track.OutputChannels.HasValue ? $"{track.OutputChannels} ch" : null;
        return string.Join(" · ", new[] { codec, details, FormatBitRate(track.OutputBitRate) }.Where(value => !string.IsNullOrEmpty(value)));
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

    /// <summary>
    /// Performs the dispose operation.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        ActiveStreamRegistry.StopRequested -= OnActiveStreamStopRequested;
        await StopStreamAsync();
        _disposed = true;
        DeviceState.OnStateChanged -= OnDeviceStateChanged;
        _streamInfoTimer?.Dispose();
    }

    private sealed record WatchPreferences
    {
        /// <summary>Gets the preferred browser video quality.</summary>
        public WebPlayerQuality Quality { get; init; } = WebPlayerQuality.AppDefault;

        /// <summary>Gets the preferred browser audio output.</summary>
        public WatchAudioOutput AudioOutput { get; init; } = WatchAudioOutput.Stereo;

        /// <summary>Gets whether subtitles are enabled.</summary>
        public bool SubtitlesEnabled { get; init; }

        /// <summary>Gets the preferred subtitle identity.</summary>
        public WatchSubtitlePreference? Subtitle { get; init; }
    }

    private sealed record WatchSubtitlePreference
    {
        /// <summary>Gets the normalized subtitle language.</summary>
        public string? Language { get; init; }

        /// <summary>Gets the normalized subtitle title.</summary>
        public string? Title { get; init; }

        /// <summary>Gets the source subtitle codec.</summary>
        public string SourceCodec { get; init; } = string.Empty;

        /// <summary>Gets whether the preferred subtitle is forced.</summary>
        public bool IsForced { get; init; }

        /// <summary>Gets whether the preferred subtitle is intended for hearing-impaired viewers.</summary>
        public bool IsHearingImpaired { get; init; }

        /// <summary>Gets whether the preference represents embedded closed captions.</summary>
        public bool IsEmbeddedClosedCaptions { get; init; }

        /// <summary>Creates a stable preference identity from an active subtitle track.</summary>
        /// <param name="track">The active subtitle track.</param>
        /// <returns>The saved subtitle preference.</returns>
        public static WatchSubtitlePreference FromTrack(ActiveStreamTrack track) =>
            new()
            {
                Language = Normalize(track.Language),
                Title = Normalize(track.Title),
                SourceCodec = track.SourceCodec,
                IsForced = track.IsForced,
                IsHearingImpaired = track.IsHearingImpaired,
                IsEmbeddedClosedCaptions = track.IsEmbeddedClosedCaptions
            };

        /// <summary>Finds the active subtitle track that best matches this preference.</summary>
        /// <param name="tracks">The available active-stream tracks.</param>
        /// <returns>The best matching subtitle track, or <see langword="null"/> when none matches.</returns>
        public ActiveStreamTrack? FindMatch(IEnumerable<ActiveStreamTrack> tracks)
        {
            var candidates = tracks
                .Where(track => track.Type == MediaTrackType.Subtitle && track.SubtitlePresentation != SubtitlePresentation.Unsupported)
                .ToArray();
            if (!string.IsNullOrWhiteSpace(Language))
            {
                candidates = candidates.Where(track => string.Equals(Normalize(track.Language), Language, StringComparison.OrdinalIgnoreCase)).ToArray();
            }
            else if (!string.IsNullOrWhiteSpace(Title))
            {
                candidates = candidates.Where(track => string.Equals(Normalize(track.Title), Title, StringComparison.OrdinalIgnoreCase)).ToArray();
            }
            else
            {
                return null;
            }

            return candidates
                .OrderByDescending(track => string.Equals(Normalize(track.Title), Title, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(track => track.IsForced == IsForced && track.IsHearingImpaired == IsHearingImpaired)
                .ThenByDescending(track => string.Equals(track.SourceCodec, SourceCodec, StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();
        }

        private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
