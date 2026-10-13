using Lineup.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using System.Text.Json;

namespace Lineup.Web.Components.Pages;

/// <summary>
/// Displays EPG, device, tuner, and active hosted stream status.
/// </summary>
public partial class Dashboard : IDisposable
{
    [Inject]
    private ILineupGuideReader GuideReader { get; set; } = default!;

    [Inject]
    private ITopologyGuideService Guides { get; set; } = default!;

    [Inject]
    private IPhysicalLineupService LineupRefresh { get; set; } = default!;

    [Inject]
    private StreamingTopologyStore TopologyStore { get; set; } = default!;

    [Inject]
    private IPhysicalDeviceStatusService DeviceStatuses { get; set; } = default!;

    [Inject]
    private IBrowserDataStore BrowserData { get; set; } = default!;

    [Inject]
    private IXmltvPublicationStore Publications { get; set; } = default!;

    [Inject]
    private ILineupSettingsService SettingsService { get; set; } = default!;

    [Inject]
    private IServiceProvider Services { get; set; } = default!;

    private IWebSettingsService? _webSettingsService;

    private IWebSettingsService WebSettingsService => _webSettingsService
        ?? throw new InvalidOperationException("The dashboard has not been initialized.");

    [Inject]
    private NavigationManager Navigation { get; set; } = default!;

    [Inject]
    private IAutoFetchStateService AutoFetchState { get; set; } = default!;

    [Inject]
    private ITimeZoneService Tz { get; set; } = default!;

    [Inject]
    private IActiveStreamRegistry ActiveStreamRegistry { get; set; } = default!;

    [Inject]
    private IStreamingFlowSnapshotService StreamingFlowSnapshots { get; set; } = default!;

    [Inject]
    private IStatusNotificationService Notifications { get; set; } = default!;

    [Inject]
    private ILogger<Dashboard> Logger { get; set; } = default!;

    private GuideStatistics? _stats;
    private DateTimeOffset? _safeFetchStart;
    private TimeSpan? _channelGap;
    private bool _isBusy;
    private string _currentAction = "";
    private FetchProgressInfo? _fetchProgress;
    private bool _xmltvFileExists;
    private IReadOnlyList<PhysicalChannelIdentity> _topologyChannels = [];
    private TimeSpan _countdown;
    private Timer? _countdownTimer;
    private Timer? _activeStreamTimer;
    private readonly object _timerLock = new();
    private bool _disposed;
    private IReadOnlyList<ActiveStreamSnapshot> _activeStreams = [];
    private StreamingFlowSnapshot _streamingFlow = new([], [], 0, 0, 0);
    private DateTime? _activeStreamsLastRefreshUtc;
    private HashSet<string> _stoppingStreams = [];
    private bool _channelsExpanded = true;
    private bool _guideExpanded = true;
    private bool _deviceExpanded = true;
    private bool _activeStreamsExpanded = true;

    // Device control state
    private bool _isRefreshingDevices;
    private readonly HashSet<(PhysicalDeviceId DeviceId, int ReceiverIndex)> _stoppingReceivers = [];

    private const string SectionStateStorageKey = "lineup-dashboard-sections-v1";
    private string XmltvFilename => SettingsService.Settings.XmltvOutputPath;

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        _webSettingsService = Services.GetService<IWebSettingsService>() ??
            WebSettingsServiceFallback.Get(SettingsService);

        // Redirect to Settings for initial setup on first launch
        if (!WebSettingsService.Settings.IsSetupComplete)
        {
            Navigation.NavigateTo("/settings#physical-devices", replace: true);
            return;
        }

        // Subscribe to auto-fetch state changes
        AutoFetchState.OnStateChanged += OnAutoFetchStateChanged;

        // Subscribe to settings changes
        SettingsService.OnSettingsChanged += OnSettingsChanged;
        WebSettingsService.OnSettingsChanged += OnSettingsChanged;

        // Subscribe to device state changes (auto-refresh)
        DeviceStatuses.StateChanged += OnDeviceStateChanged;

        // Start countdown timer (updates every second)
        _countdownTimer = new Timer(UpdateCountdown, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        RefreshActiveStreams();
        ConfigureActiveStreamTimer();

        await LoadStatsAsync();
        await LoadChannelLineupAsync();
        await DeviceStatuses.RefreshAsync(SettingsService.Settings.Streaming);
        CheckXmltvFileExists();
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }

        try
        {
            var state = await BrowserData.ReadAsync<DashboardSectionState>(SectionStateStorageKey);
            if (_disposed)
            {
                return;
            }

            if (state == null)
            {
                return;
            }

            _channelsExpanded = state.ChannelsExpanded;
            _guideExpanded = state.GuideExpanded;
            _deviceExpanded = state.DeviceExpanded;
            _activeStreamsExpanded = state.ActiveStreamsExpanded;
            StateHasChanged();
        }
        catch (JsonException ex)
        {
            Logger.LogWarning(ex, "Ignoring invalid saved dashboard section state");
        }
        catch (JSDisconnectedException ex)
        {
            Logger.LogDebug(ex, "The browser disconnected while restoring dashboard section state");
        }
        catch (JSException ex)
        {
            Logger.LogWarning(ex, "Unable to restore dashboard section state from browser storage");
        }
        catch (InvalidOperationException ex)
        {
            Logger.LogDebug(ex, "Browser storage is not available while rendering the dashboard");
        }
    }

    private async Task ToggleSection(DashboardSection section)
    {
        switch (section)
        {
            case DashboardSection.Channels:
                _channelsExpanded = !_channelsExpanded;
                break;
            case DashboardSection.Guide:
                _guideExpanded = !_guideExpanded;
                break;
            case DashboardSection.Device:
                _deviceExpanded = !_deviceExpanded;
                break;
            case DashboardSection.ActiveStreams:
                _activeStreamsExpanded = !_activeStreamsExpanded;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(section), section, null);
        }

        var state = new DashboardSectionState
        {
            ChannelsExpanded = _channelsExpanded,
            GuideExpanded = _guideExpanded,
            DeviceExpanded = _deviceExpanded,
            ActiveStreamsExpanded = _activeStreamsExpanded
        };

        try
        {
            await BrowserData.WriteAsync(SectionStateStorageKey, state);
        }
        catch (JSDisconnectedException ex)
        {
            Logger.LogDebug(ex, "The browser disconnected while saving dashboard section state");
        }
        catch (JSException ex)
        {
            Logger.LogWarning(ex, "Unable to save dashboard section state to browser storage");
        }
        catch (InvalidOperationException ex)
        {
            Logger.LogDebug(ex, "Browser storage is not available while saving dashboard section state");
        }
        catch (OperationCanceledException ex)
        {
            Logger.LogWarning(ex, "Saving dashboard section state timed out");
        }
    }

    private string FormatChannelsSummary()
    {
        if (_topologyChannels.Count == 0)
        {
            return "0 enabled · 0 disabled · never refreshed";
        }

        return $"{_topologyChannels.Count(channel => channel.IsEnabled)} enabled · {_topologyChannels.Count(channel => !channel.IsEnabled)} disabled · updated {Tz.ConvertFromUtc(_topologyChannels.Max(channel => channel.LastSeenUtc)):yyyy-MM-dd HH:mm:ss}";
    }

    private string FormatGuideSummary()
    {
        var remaining = _stats?.TotalTimeSpan?.ToString(@"d' days 'h' hrs'") ?? "N/A";
        var autoFetch = !AutoFetchState.IsEnabled
            ? "auto-fetch disabled"
            : AutoFetchState.IsRunning
                ? "auto-fetch running"
                : AutoFetchState.NextFetchTime.HasValue
                    ? $"next fetch in {FormatCountdown(_countdown)}"
                    : "auto-fetch pending";
        return $"{_stats?.ChannelCount ?? 0} channels · {_stats?.ProgramCount ?? 0} programs · {remaining} remaining · {autoFetch}";
    }

    private string FormatDeviceSummary()
    {
        var devices = DeviceStatuses.Devices;
        var receivers = devices.SelectMany(device => device.Status?.Receivers ?? []).ToArray();
        var failures = devices.Count(device => device.Error is not null);
        return $"{devices.Count} devices · {receivers.Count(receiver => receiver.IsStreaming)} streaming · {failures} unavailable";
    }

    private string FormatActiveStreamsSummary()
    {
        var refreshed = _activeStreamsLastRefreshUtc.HasValue
            ? $"refreshed {Tz.ConvertFromUtc(_activeStreamsLastRefreshUtc.Value):HH:mm:ss}"
            : "not refreshed";
        var schedule = WebSettingsService.Settings.IsActiveStreamRefreshEnabled
            ? $"every {WebSettingsService.Settings.ActiveStreamRefreshIntervalSeconds} seconds"
            : "auto-refresh disabled";
        return $"{_activeStreams.Count} active · {refreshed} · {schedule}";
    }

    private void OnSettingsChanged()
    {
        if (_disposed)
        {
            return;
        }

        CheckXmltvFileExists(); // Refresh in case output path changed
        ConfigureActiveStreamTimer();
        InvokeAsync(StateHasChanged);
    }

    private enum DashboardSection
    {
        Channels,
        Guide,
        Device,
        ActiveStreams
    }

    private sealed record DashboardSectionState
    {
        /// <summary>
        /// Gets whether the Channels section is expanded.
        /// </summary>
        public bool ChannelsExpanded { get; init; } = true;

        /// <summary>
        /// Gets whether the Guide section is expanded.
        /// </summary>
        public bool GuideExpanded { get; init; } = true;

        /// <summary>
        /// Gets whether the Device section is expanded.
        /// </summary>
        public bool DeviceExpanded { get; init; } = true;

        /// <summary>
        /// Gets whether the Active Streams section is expanded.
        /// </summary>
        public bool ActiveStreamsExpanded { get; init; } = true;
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
        if (_webSettingsService is not null)
        {
            _webSettingsService.OnSettingsChanged -= OnSettingsChanged;
        }
        DeviceStatuses.StateChanged -= OnDeviceStateChanged;
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

            var intervalSeconds = WebSettingsService.Settings.ActiveStreamRefreshIntervalSeconds;
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
        _streamingFlow = StreamingFlowSnapshots.GetSnapshot();
        _stoppingStreams.IntersectWith(_activeStreams.Select(stream => stream.SessionId));
        _activeStreamsLastRefreshUtc = DateTime.UtcNow;
    }

    private void StopActiveStream(string sessionId)
    {
        _stoppingStreams.Add(sessionId);
        ActiveStreamRegistry.RequestStop(sessionId);
        RefreshActiveStreams();
    }

    private async Task LoadStatsAsync()
    {
        _stats = await GuideReader.GetStatisticsAsync();
        _safeFetchStart = _stats.SafeCoverageEndUtc;

        if (_safeFetchStart.HasValue && _stats.LatestProgramEndUtc.HasValue &&
            _safeFetchStart.Value < _stats.LatestProgramEndUtc.Value)
        {
            _channelGap = _stats.LatestProgramEndUtc.Value - _safeFetchStart.Value;
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
            _topologyChannels = await TopologyStore.ReadActiveAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"Error loading the saved channel lineup: {ex.Message}", isError: true);
        }
    }

    private async Task RefreshChannels()
    {
        _isBusy = true;
        _currentAction = "channels";

        try
        {
            var results = await LineupRefresh.RefreshAsync(SettingsService.Settings.Streaming);
            await LoadChannelLineupAsync();
            var failed = results.Count(result => !result.Succeeded);
            var channelCount = results.Where(result => result.Succeeded).Sum(result => result.ChannelCount);
            SetStatus(
                failed == 0
                    ? $"Refreshed {channelCount} physical source channels."
                    : $"Refreshed {channelCount} physical source channels with {failed} device failure(s).",
                isError: failed > 0);
        }
        catch (Exception ex)
        {
            SetStatus($"Error refreshing channels: {ex.Message}", isError: true);
        }
        finally
        {
            _isBusy = false;
            _currentAction = "";
        }
    }

    private async Task FetchData()
    {
        _isBusy = true;
        _currentAction = "fetch";
        _fetchProgress = null;
        StateHasChanged();

        _fetchProgress = new FetchProgressInfo
        {
            Status = FetchStatus.Fetching,
            Message = "Refreshing enabled guide sources"
        };

        try
        {
            if (SettingsService.Settings.RefreshChannelsBeforeGuideFetch)
            {
                await LineupRefresh.RefreshAsync(SettingsService.Settings.Streaming);
                await LoadChannelLineupAsync();
            }

            var result = await Guides.RefreshAndPublishAsync();
            await LoadStatsAsync();
            var failedOutputs = result.Outputs.Count(output => !output.Succeeded);
            if (result.FailedSourceCount > 0 || failedOutputs > 0)
            {
                SetStatus(
                    $"Guide refresh completed with {result.FailedSourceCount} failed source(s) and {failedOutputs} failed output(s). Successful imports and publications were preserved.",
                    isError: true);
            }
            else if (result.Sources.Count == 0)
            {
                SetStatus("No guide sources are enabled. XMLTV was regenerated from cached guide data.", isError: false);
            }
            else
            {
                SetStatus(
                    $"Guide data fetched successfully from {result.SuccessfulSourceCount} source(s): {result.ChannelCount:N0} channels and {result.ProgramCount:N0} programs.",
                    isError: false);
            }
        }

        catch (Exception ex)
        {
            SetStatus($"Error fetching data: {ex.Message}", isError: true);
        }
        finally
        {
            _isBusy = false;
            _currentAction = "";
            _fetchProgress = null;
        }
    }

    private async Task GenerateXmltv()
    {
        _isBusy = true;
        _currentAction = "generate";
        StateHasChanged();

        try
        {
            var results = await Guides.PublishAsync();
            CheckXmltvFileExists();
            var failures = results.Where(result => !result.Succeeded).ToArray();
            if (failures.Length > 0)
            {
                SetStatus(
                    $"Published {results.Count - failures.Length} guide output(s); {failures.Length} failed: {string.Join(" ", failures.Select(result => $"{result.Name}: {result.Error}"))}",
                    isError: true);
            }
            else
            {
                SetStatus($"Published {results.Count} guide output(s) successfully.", isError: false);
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Error generating XMLTV: {ex.Message}", isError: true);
        }
        finally
        {
            _isBusy = false;
            _currentAction = "";
        }
    }

    private async Task RefreshDevices()
    {
        _isRefreshingDevices = true;
        try
        {
            await DeviceStatuses.RefreshAsync(SettingsService.Settings.Streaming);
        }
        finally
        {
            _isRefreshingDevices = false;
        }
    }

    private async Task StopReceiver(PhysicalDeviceId deviceId, int receiverIndex)
    {
        var key = (deviceId, receiverIndex);
        _stoppingReceivers.Add(key);
        StateHasChanged();

        try
        {
            await DeviceStatuses.StopReceiverAsync(deviceId, receiverIndex);
            await DeviceStatuses.RefreshAsync(SettingsService.Settings.Streaming);
            SetStatus($"Receiver {receiverIndex} stopped successfully.", isError: false);
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to stop receiver {receiverIndex}: {ex.Message}", isError: true);
        }
        finally
        {
            _stoppingReceivers.Remove(key);
        }
    }

    private async Task RestartDevice(PhysicalDeviceId deviceId)
    {
        try
        {
            await DeviceStatuses.RestartAsync(deviceId);
            SetStatus("Device restart command sent. Device will be unavailable for ~30 seconds.", isError: false);
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to restart device: {ex.Message}", isError: true);
        }
    }

    private void CheckXmltvFileExists()
    {
        _xmltvFileExists = Publications.Exists(XmltvFilename);
    }

    private void SetStatus(string message, bool isError)
    {
        if (isError)
        {
            Notifications.ShowError(message);
        }
        else
        {
            Notifications.ShowSuccess(message);
        }
    }
}
