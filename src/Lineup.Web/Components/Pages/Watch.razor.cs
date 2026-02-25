using Lineup.Core.Storage;
using Lineup.HDHomeRun.Api.Models;
using Lineup.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Lineup.Web.Components.Pages;

public partial class Watch : IAsyncDisposable
{
    [Inject]
    private IEpgRepository Repository { get; set; } = default!;

    [Inject]
    private IAppSettingsService SettingsService { get; set; } = default!;

    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    [Inject]
    private NavigationManager NavigationManager { get; set; } = default!;

    [Parameter]
    public string? ChannelNumber { get; set; }

    private List<HDHomeRunChannelEpgSegment> _channels = [];
    private List<HDHomeRunProgram> _programs = [];
    private HDHomeRunChannelEpgSegment? _selectedChannel;
    private HDHomeRunProgram? _currentProgram;
    private string? _streamUrl;
    private string? _errorMessage;
    private bool _isPlaying;
    private bool _isLoadingChannels = true;
    private bool _showCodecError;
    private bool _needsPlayerInit;

    protected override async Task OnInitializedAsync()
    {
        await LoadChannelsAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            // If a channel was specified in the URL, auto-play it
            if (!string.IsNullOrEmpty(ChannelNumber))
            {
                var channel = _channels.FirstOrDefault(c => c.GuideNumber == ChannelNumber);
                if (channel != null)
                {
                    await SelectChannel(channel);
                    StateHasChanged();
                }
            }
        }

        // Initialize fMP4 player after render when we have a stream URL
        if (_isPlaying && !string.IsNullOrEmpty(_streamUrl) && _needsPlayerInit)
        {
            _needsPlayerInit = false;
            try
            {
                var success = await JS.InvokeAsync<bool>("initFmp4Player", "videoPlayer", _streamUrl);
                if (!success)
                {
                    _errorMessage = "Failed to initialize video player.";
                    StateHasChanged();
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
        // Stop current stream first
        await StopStreamAsync();

        _selectedChannel = channel;
        _errorMessage = null;

        // Use fMP4 streaming endpoint - streams directly, no session management needed
        // FFmpeg transcodes to memory (no disk I/O) and pipes to browser
        _streamUrl = $"/api/stream/fmp4/{channel.GuideNumber}";
        _isPlaying = true;
        _needsPlayerInit = true;

        // Get current program for this channel
        _currentProgram = GetCurrentProgram(channel.GuideNumber);
        _showCodecError = false;
    }

    private async Task StopStreamAsync()
    {
        // Destroy video player (stops the stream - FFmpeg process auto-terminates when client disconnects)
        try
        {
            await JS.InvokeVoidAsync("destroyHlsPlayer");
        }
        catch { /* Ignore */ }

        _isPlaying = false;
        _streamUrl = null;
        _currentProgram = null;
        _showCodecError = false;
        _needsPlayerInit = false;
    }

    private async Task StopStream()
    {
        await StopStreamAsync();
    }

    private HDHomeRunProgram? GetCurrentProgram(string? guideNumber)
    {
        if (string.IsNullOrEmpty(guideNumber)) return null;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return _programs.FirstOrDefault(p =>
            p.GuideNumber == guideNumber &&
            p.StartTime <= now &&
            p.EndTime > now);
    }

    private async Task CopyStreamUrlToClipboard()
    {
        if (_selectedChannel != null)
        {
            var directUrl = $"http://{SettingsService.Settings.DeviceAddress}:5004/auto/v{_selectedChannel.GuideNumber}";
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

    public async ValueTask DisposeAsync()
    {
        await StopStreamAsync();
    }
}
