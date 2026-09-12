using Lineup.Core.Storage;
using Lineup.HDHomeRun.Api.Models;
using Lineup.HDHomeRun.Device.Protocol;
using Lineup.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using System.Text.RegularExpressions;

namespace Lineup.Web.Components.Pages;

/// <summary>
/// Represents watch.
/// </summary>
public partial class Watch : IAsyncDisposable
{
    [Inject]
    private IEpgRepository Repository { get; set; } = default!;

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
    private bool _disposed;
    private readonly string _clientId = Guid.NewGuid().ToString("N");
    private string? _lastRouteChannelNumber;
    private Timer? _streamInfoTimer;
    private ActiveStreamSnapshot? _activeStream;
    private DateTime _lastTunerRefreshRequestUtc = DateTime.MinValue;
    private WebPlayerQuality _quality = WebPlayerQuality.AppDefault;
    private int? _audioTrack;
    private int? _subtitleTrack;
    private bool IsContentProtectedError => _errorMessage?.Contains("Content Protection Required", StringComparison.OrdinalIgnoreCase) == true;
    private TunerStatus? SelectedTuner => DeviceState.TunerStatuses.FirstOrDefault(
        tuner => string.Equals(tuner.VirtualChannel, _selectedChannelNumber, StringComparison.Ordinal));

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
        if (!string.IsNullOrWhiteSpace(ChannelNumber))
        {
            var channel = _channels.FirstOrDefault(candidate => string.Equals(candidate.GuideNumber, ChannelNumber, StringComparison.Ordinal));
            await TuneChannelAsync(ChannelNumber, channel);
        }
    }

    /// <summary>
    /// Performs the on after render operation.
    /// </summary>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        _isJsInteropReady = true;

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
            _channels = await Repository.GetChannelsAsync();

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
        }

        _selectedChannel = channel;
        _selectedChannelNumber = validChannelNumber;
        _manualTuneValidationMessage = null;
        _errorMessage = null;

        _streamUrl = BuildStreamUrl(validChannelNumber);
        _isPlaying = true;
        _needsPlayerInit = true;
        StartStreamInfoRefresh();

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

    private async Task ChangeSubtitleTrack(ChangeEventArgs args)
    {
        _subtitleTrack = int.TryParse(args.Value?.ToString(), out var index) && index >= 0 ? index : null;
        await RestartCurrentStreamAsync();
    }

    private Task RestartCurrentStreamAsync() =>
        _selectedChannelNumber is { } channelNumber && _isPlaying
            ? TuneChannelAsync(channelNumber, _selectedChannel)
            : Task.CompletedTask;

    private string BuildStreamUrl(string channelNumber)
    {
        var url = $"/api/stream/fmp4/{Uri.EscapeDataString(channelNumber)}?clientId={_clientId}&quality={_quality}";
        if (_audioTrack.HasValue)
        {
            url += $"&audioTrack={_audioTrack.Value}";
        }
        if (_subtitleTrack.HasValue)
        {
            url += $"&subtitleTrack={_subtitleTrack.Value}";
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
}
