using Lineup.Core;
using Lineup.Core.Storage;
using Lineup.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Lineup.Web.Components.Pages;

/// <summary>
/// Displays EPG, device, tuner, and active hosted stream status.
/// </summary>
public partial class Dashboard : IDisposable
{
    [Inject]
    private IEpgRepository Repository { get; set; } = default!;

    [Inject]
    private EpgOrchestrator Orchestrator { get; set; } = default!;

    [Inject]
    private ChannelLineupRefreshService ChannelLineupRefresh { get; set; } = default!;

    [Inject]
    private ChannelLineupStore ChannelLineupStore { get; set; } = default!;

    [Inject]
    private IDeviceStateService DeviceState { get; set; } = default!;

    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    [Inject]
    private IAppSettingsService SettingsService { get; set; } = default!;

    [Inject]
    private NavigationManager Navigation { get; set; } = default!;

    [Inject]
    private IAutoFetchStateService AutoFetchState { get; set; } = default!;

    [Inject]
    private ITimeZoneService Tz { get; set; } = default!;

    [Inject]
    private IActiveStreamRegistry ActiveStreamRegistry { get; set; } = default!;

    private CacheStatistics? _stats;
    private DateTime? _safeFetchStart;
    private TimeSpan? _channelGap;
    private bool _isBusy;
    private string _currentAction = "";
    private string _statusMessage = "";
    private bool _isError;
    private int _targetDays = 3;
    private FetchProgressInfo? _fetchProgress;
    private bool _xmltvFileExists;
    private ChannelLineupSnapshot? _channelLineup;
    private TimeSpan _countdown;
    private Timer? _countdownTimer;
    private Timer? _activeStreamTimer;
    private readonly object _timerLock = new();
    private bool _disposed;
    private IReadOnlyList<ActiveStreamSnapshot> _activeStreams = [];
    private DateTime? _activeStreamsLastRefreshUtc;
    private HashSet<string> _stoppingStreams = [];
    private bool _guideExpanded = true;
    private bool _deviceExpanded = true;
    private bool _activeStreamsExpanded = true;

    // Device control state
    private bool _isRestarting;
    private bool _showRestartConfirm;
    private HashSet<int> _stoppingTuner = [];

    private const string AutoFetchIntervalDisplay = "20-28h (randomized)";
    private string XmltvFilename => SettingsService.Settings.XmltvOutputPath;

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        // Redirect to Settings for initial setup on first launch
        if (!SettingsService.Settings.IsSetupComplete)
        {
            Navigation.NavigateTo("/settings#device", replace: true);
            return;
        }

        _targetDays = SettingsService.Settings.TargetDays;

        // Subscribe to auto-fetch state changes
        AutoFetchState.OnStateChanged += OnAutoFetchStateChanged;

        // Subscribe to settings changes
        SettingsService.OnSettingsChanged += OnSettingsChanged;

        // Subscribe to device state changes (auto-refresh)
        DeviceState.OnStateChanged += OnDeviceStateChanged;

        // Start countdown timer (updates every second)
        _countdownTimer = new Timer(UpdateCountdown, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        RefreshActiveStreams();
        ConfigureActiveStreamTimer();

        await LoadStatsAsync();
        await LoadChannelLineupAsync();
        CheckXmltvFileExists();
    }

    private void OnSettingsChanged()
    {
        if (_disposed)
        {
            return;
        }

        _targetDays = SettingsService.Settings.TargetDays;
        CheckXmltvFileExists(); // Refresh in case output path changed
        ConfigureActiveStreamTimer();
        InvokeAsync(StateHasChanged);
    }

    private void OnDeviceStateChanged()
    {
        InvokeAsync(StateHasChanged);
    }

    private async void UpdateCountdown(object? state)
    {
        if (_disposed)
        {
            return;
        }

        if (AutoFetchState.NextFetchTime.HasValue && !AutoFetchState.IsRunning)
        {
            _countdown = AutoFetchState.NextFetchTime.Value - DateTime.UtcNow;
            if (_countdown < TimeSpan.Zero)
            {
                _countdown = TimeSpan.Zero;
            }
        }
        else
        {
            _countdown = TimeSpan.Zero;
        }

        try
        {
            await InvokeAsync(StateHasChanged);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static string FormatCountdown(TimeSpan countdown)
    {
        if (countdown <= TimeSpan.Zero)
        {
            return "Starting soon...";
        }

        if (countdown.TotalHours >= 1)
        {
            return $"{(int)countdown.TotalHours:D2}:{countdown.Minutes:D2}:{countdown.Seconds:D2}";
        }

        return $"{countdown.Minutes:D2}:{countdown.Seconds:D2}";
    }

    private static string FormatTimeUntil(DateTime utcTime)
    {
        var remaining = utcTime - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return "now";
        }

        if (remaining.TotalSeconds < 60)
        {
            return $"{(int)remaining.TotalSeconds}s";
        }

        return $"{(int)remaining.TotalMinutes}m {remaining.Seconds}s";
    }

    private void OnAutoFetchStateChanged()
    {
        InvokeAsync(async () =>
        {
            // Reload stats when auto-fetch completes
            if (!AutoFetchState.IsRunning && AutoFetchState.LastFetchTime.HasValue)
            {
                await LoadStatsAsync();
            }
            StateHasChanged();
        });
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_timerLock)
        {
            _disposed = true;
            _countdownTimer?.Dispose();
            _activeStreamTimer?.Dispose();
            _countdownTimer = null;
            _activeStreamTimer = null;
        }

        AutoFetchState.OnStateChanged -= OnAutoFetchStateChanged;
        SettingsService.OnSettingsChanged -= OnSettingsChanged;
        DeviceState.OnStateChanged -= OnDeviceStateChanged;
    }

    private void ConfigureActiveStreamTimer()
    {
        lock (_timerLock)
        {
            if (_disposed)
            {
                return;
            }

            _activeStreamTimer?.Dispose();
            _activeStreamTimer = null;

            var intervalSeconds = SettingsService.Settings.ActiveStreamRefreshIntervalSeconds;
            if (intervalSeconds == 0)
            {
                return;
            }

            var interval = TimeSpan.FromSeconds(intervalSeconds);
            _activeStreamTimer = new Timer(UpdateActiveStreams, null, interval, interval);
        }
    }

    private async void UpdateActiveStreams(object? state)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            await InvokeAsync(() =>
            {
                if (!_disposed)
                {
                    RefreshActiveStreams();
                    StateHasChanged();
                }
            });
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void RefreshActiveStreams()
    {
        _activeStreams = ActiveStreamRegistry.GetActiveStreams();
        _stoppingStreams.IntersectWith(_activeStreams.Select(stream => stream.SessionId));
        _activeStreamsLastRefreshUtc = DateTime.UtcNow;
    }

    private void StopActiveStream(string sessionId)
    {
        _stoppingStreams.Add(sessionId);
        ActiveStreamRegistry.RequestStop(sessionId);
        RefreshActiveStreams();
    }

    private static string FormatHostedStreamFormat(HostedStreamFormat format) => format switch
    {
        HostedStreamFormat.MpegTs => "MPEG-TS",
        HostedStreamFormat.FragmentedMp4 => "fMP4",
        HostedStreamFormat.Hls => "HLS",
        _ => format.ToString()
    };

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
        return string.Join(" · ", new[] { track.SourceCodec, details, FormatBitRate(track.SourceBitRate) }.Where(value => !string.IsNullOrEmpty(value)));
    }

    private static string FormatOutputTrack(ActiveStreamTrack track)
    {
        var codec = track.OutputCodec == "copy" ? $"{track.SourceCodec} (copy)" : track.OutputCodec;
        var details = track.Type == MediaTrackType.Audio && track.OutputChannels.HasValue ? $"{track.OutputChannels} ch" : null;
        return string.Join(" · ", new[] { codec, details, FormatBitRate(track.OutputBitRate) }.Where(value => !string.IsNullOrEmpty(value)));
    }

    private static string FormatStreamDuration(DateTime startedAtUtc)
    {
        var duration = DateTime.UtcNow - startedAtUtc;
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}h {duration.Minutes}m {duration.Seconds}s"
            : $"{duration.Minutes}m {duration.Seconds}s";
    }

    private async Task LoadStatsAsync()
    {
        await Repository.EnsureDatabaseCreatedAsync();
        _stats = await Repository.GetCacheStatisticsAsync();
        _safeFetchStart = await Repository.GetSafeFetchStartTimeAsync();

        if (_safeFetchStart.HasValue && _stats.LatestProgramEnd.HasValue &&
            _safeFetchStart.Value < _stats.LatestProgramEnd.Value)
        {
            _channelGap = _stats.LatestProgramEnd.Value - _safeFetchStart.Value;
        }
        else
        {
            _channelGap = null;
        }
    }

    private async Task LoadChannelLineupAsync()
    {
        try
        {
            _channelLineup = await ChannelLineupStore.ReadAsync();
        }
        catch (Exception ex)
        {
            _statusMessage = $"Error loading the saved channel lineup: {ex.Message}";
            _isError = true;
        }
    }

    private async Task FetchData()
    {
        _isBusy = true;
        _currentAction = "fetch";
        _statusMessage = "";
        _fetchProgress = null;
        StateHasChanged();

        var progress = new Progress<FetchProgressInfo>(info =>
        {
            _fetchProgress = info;
            InvokeAsync(StateHasChanged);
        });

        try
        {
            await Orchestrator.FetchAndStoreEpgAsync(_targetDays, force: true, progress);
            await LoadStatsAsync();
            _statusMessage = $"EPG data fetched successfully! ({_fetchProgress?.FetchCount ?? 0} fetches, {_fetchProgress?.TotalProgramsFetched.ToString("N0") ?? "0"} programs)";
            _isError = false;
        }

        catch (Exception ex)
        {
            _statusMessage = $"Error fetching data: {ex.Message}";
            _isError = true;
        }
        finally
        {
            _isBusy = false;
            _currentAction = "";
            _fetchProgress = null;
        }
    }

    private async Task RefreshChannels()
    {
        _isBusy = true;
        _currentAction = "channels";
        _statusMessage = "";
        StateHasChanged();

        try
        {
            _channelLineup = await ChannelLineupRefresh.RefreshAsync();
            _statusMessage = $"Refreshed {_channelLineup.Channels.Count} tuner channels. The saved lineup will be applied during the next guide fetch.";
            _isError = false;
        }
        catch (Exception ex)
        {
            _statusMessage = $"Error refreshing channels: {ex.Message}";
            _isError = true;
        }
        finally
        {
            _isBusy = false;
            _currentAction = "";
        }
    }

    private static string GetStatusBadgeClass(FetchStatus status) => status switch
    {
        FetchStatus.Initializing => "bg-secondary",
        FetchStatus.Fetching => "bg-primary",
        FetchStatus.Storing => "bg-info",
        FetchStatus.Completed => "bg-success",
        FetchStatus.Failed => "bg-danger",
        _ => "bg-secondary"
    };

    private async Task GenerateXmltv()
    {
        _isBusy = true;
        _currentAction = "generate";
        _statusMessage = "";
        StateHasChanged();

        try
        {
            // Ensure directory exists if path contains directories
            var directory = Path.GetDirectoryName(XmltvFilename);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await Orchestrator.GenerateEpgFromCacheAsync(_targetDays, XmltvFilename);
            CheckXmltvFileExists();
            _statusMessage = $"XMLTV generated to {XmltvFilename} successfully!";
            _isError = false;
        }
        catch (Exception ex)
        {
            _statusMessage = $"Error generating XMLTV: {ex.Message}";
            _isError = true;
        }
        finally
        {
            _isBusy = false;
            _currentAction = "";
        }
    }

    private async Task DiscoverDevice()
    {
        await DeviceState.DiscoverDeviceAsync();
    }

    private async Task RefreshTunerStatus()
    {
        await DeviceState.RefreshTunerStatusAsync();
    }

    private async Task StopTuner(int tunerIndex)
    {
        if (DeviceState.ProtocolDevice == null)
        {
            return;
        }

        _stoppingTuner.Add(tunerIndex);
        StateHasChanged();

        try
        {
            await DeviceState.StopTunerAsync(tunerIndex);
            _statusMessage = $"Tuner {tunerIndex} stopped successfully.";
            _isError = false;
        }
        catch (Exception ex)
        {
            _statusMessage = $"Failed to stop tuner {tunerIndex}: {ex.Message}";
            _isError = true;
        }
        finally
        {
            _stoppingTuner.Remove(tunerIndex);
        }
    }

    private void ShowRestartConfirm()
    {
        _showRestartConfirm = true;
    }

    private void HideRestartConfirm()
    {
        _showRestartConfirm = false;
    }

    private async Task RestartDevice()
    {
        if (DeviceState.ProtocolDevice == null)
        {
            return;
        }

        _isRestarting = true;
        StateHasChanged();

        try
        {
            await DeviceState.RestartDeviceAsync();
            _statusMessage = "Device restart command sent. Device will be unavailable for ~30 seconds.";
            _isError = false;
        }
        catch (Exception ex)
        {
            _statusMessage = $"Failed to restart device: {ex.Message}";
            _isError = true;
        }
        finally
        {
            _isRestarting = false;
            _showRestartConfirm = false;
        }
    }

    private static string GetSignalColorClass(int signalStrength) => signalStrength switch
    {
        >= 80 => "bg-success",
        >= 60 => "bg-info",
        >= 40 => "bg-warning",
        _ => "bg-danger"
    };

    private void CheckXmltvFileExists()
    {
        _xmltvFileExists = File.Exists(XmltvFilename);
    }

    private void ClearStatus()
    {
        _statusMessage = "";
    }
}
