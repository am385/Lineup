namespace Lineup.Web.Services;

/// <summary>
/// Background service that automatically discovers the HDHomeRun device on startup
/// and refreshes device info and tuner status on configurable intervals.
/// - Device discovery/info: Configurable (default 10 minutes)
/// - Tuner status: Configurable (default 30 seconds)
/// </summary>
public class DeviceRefreshService : BackgroundService
{
    private readonly ILogger<DeviceRefreshService> _logger;
    private readonly DeviceStateService _deviceState;
    private readonly IAppSettingsService _settingsService;
    
    private static readonly TimeSpan InitialDiscoveryDelay = TimeSpan.FromSeconds(2);
    
    private TimeSpan DeviceRefreshInterval => TimeSpan.FromMinutes(_settingsService.Settings.DeviceRefreshIntervalMinutes);
    private TimeSpan TunerRefreshInterval => TimeSpan.FromSeconds(_settingsService.Settings.TunerRefreshIntervalSeconds);
    
    public DeviceRefreshService(
        ILogger<DeviceRefreshService> logger,
        IDeviceStateService deviceState,
        IAppSettingsService settingsService)
    {
        _logger = logger;
        _deviceState = (DeviceStateService)deviceState;
        _settingsService = settingsService;
    }
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Device refresh service starting");
        
        // Wait a moment for the app to fully start
        await Task.Delay(InitialDiscoveryDelay, stoppingToken);
        
        // Initial device discovery (always try once regardless of settings)
        await DiscoverDeviceIfNeededAsync(stoppingToken);
        
        // Main loop - check what needs refreshing
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.UtcNow;
                var nextCheck = TimeSpan.FromSeconds(5); // Default check interval
                
                // Check if device needs refresh (only if auto-refresh is enabled)
                if (_settingsService.Settings.IsDeviceRefreshEnabled)
                {
                    if (_deviceState.NextDeviceRefresh.HasValue && now >= _deviceState.NextDeviceRefresh.Value)
                    {
                        await DiscoverDeviceIfNeededAsync(stoppingToken);
                    }
                    else if (!_deviceState.IsDiscovered && !_deviceState.IsDiscovering)
                    {
                        // Device not discovered yet, try to discover
                        await DiscoverDeviceIfNeededAsync(stoppingToken);
                    }
                }
                
                // Check if tuner status needs refresh (only if auto-refresh is enabled)
                if (_settingsService.Settings.IsTunerRefreshEnabled && _deviceState.IsDiscovered)
                {
                    if (_deviceState.NextTunerRefresh.HasValue && now >= _deviceState.NextTunerRefresh.Value)
                    {
                        await RefreshTunerStatusIfNeededAsync(stoppingToken);
                    }
                    else if (!_deviceState.LastTunerRefresh.HasValue)
                    {
                        // Tuners never refreshed, do initial refresh
                        await RefreshTunerStatusIfNeededAsync(stoppingToken);
                    }
                }
                
                // Calculate time until next action
                var nextAction = GetTimeUntilNextAction(now);
                if (nextAction < nextCheck)
                {
                    nextCheck = nextAction;
                }
                
                // Ensure minimum wait time
                if (nextCheck < TimeSpan.FromSeconds(1))
                {
                    nextCheck = TimeSpan.FromSeconds(1);
                }
                
                await Task.Delay(nextCheck, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in device refresh loop");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
        
        _logger.LogInformation("Device refresh service stopping");
    }
    
    private async Task DiscoverDeviceIfNeededAsync(CancellationToken stoppingToken)
    {
        if (_deviceState.IsDiscovering)
        {
            return;
        }
        
        _logger.LogDebug("Starting device discovery");
        
        try
        {
            await _deviceState.DiscoverDeviceAsync(stoppingToken);
            
            // Schedule next device refresh only if auto-refresh is enabled
            if (_settingsService.Settings.IsDeviceRefreshEnabled)
            {
                _deviceState.SetNextDeviceRefresh(DateTime.UtcNow.Add(DeviceRefreshInterval));
            }
            else
            {
                _deviceState.SetNextDeviceRefresh(null);
            }
            
            // Also schedule the next tuner refresh after successful discovery
            if (_deviceState.IsDiscovered && _settingsService.Settings.IsTunerRefreshEnabled)
            {
                _deviceState.SetNextTunerRefresh(DateTime.UtcNow.Add(TunerRefreshInterval));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Device discovery failed, will retry in 1 minute");
            _deviceState.SetNextDeviceRefresh(DateTime.UtcNow.AddMinutes(1));
        }
    }
    
    private async Task RefreshTunerStatusIfNeededAsync(CancellationToken stoppingToken)
    {
        if (_deviceState.IsRefreshingTuners || !_deviceState.IsDiscovered)
        {
            return;
        }
        
        _logger.LogDebug("Refreshing tuner status");
        
        try
        {
            await _deviceState.RefreshTunerStatusAsync(stoppingToken);
            
            // Schedule next refresh only if auto-refresh is enabled
            if (_settingsService.Settings.IsTunerRefreshEnabled)
            {
                _deviceState.SetNextTunerRefresh(DateTime.UtcNow.Add(TunerRefreshInterval));
            }
            else
            {
                _deviceState.SetNextTunerRefresh(null);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tuner status refresh failed");
            _deviceState.SetNextTunerRefresh(DateTime.UtcNow.AddSeconds(10));
        }
    }
    
    private TimeSpan GetTimeUntilNextAction(DateTime now)
    {
        var times = new List<DateTime>();
        
        if (_deviceState.NextDeviceRefresh.HasValue)
        {
            times.Add(_deviceState.NextDeviceRefresh.Value);
        }
        
        if (_deviceState.NextTunerRefresh.HasValue)
        {
            times.Add(_deviceState.NextTunerRefresh.Value);
        }
        
        if (times.Count == 0)
        {
            return TimeSpan.FromSeconds(5);
        }
        
        var next = times.Min();
        var timeUntil = next - now;
        
        return timeUntil > TimeSpan.Zero ? timeUntil : TimeSpan.Zero;
    }
}
