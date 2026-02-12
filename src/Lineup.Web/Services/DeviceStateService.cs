using Lineup.HDHomeRun.Device;
using Lineup.HDHomeRun.Device.Models;
using Lineup.HDHomeRun.Device.Protocol;

namespace Lineup.Web.Services;

/// <summary>
/// Service that maintains the current state of HDHomeRun device discovery and tuner status.
/// Automatically discovers device on first access and refreshes periodically.
/// </summary>
public interface IDeviceStateService
{
    /// <summary>
    /// Device info from HTTP API (refreshed every 10 minutes)
    /// </summary>
    HDHomeRunDeviceInfo? DeviceInfo { get; }
    
    /// <summary>
    /// Native protocol device instance for control operations
    /// </summary>
    HDHomeRunDevice? ProtocolDevice { get; }
    
    /// <summary>
    /// Current tuner statuses (refreshed every 30 seconds)
    /// </summary>
    IReadOnlyList<TunerStatus> TunerStatuses { get; }
    
    /// <summary>
    /// Whether the device has been discovered at least once
    /// </summary>
    bool IsDiscovered { get; }
    
    /// <summary>
    /// Whether device discovery is currently in progress
    /// </summary>
    bool IsDiscovering { get; }
    
    /// <summary>
    /// Whether tuner status refresh is currently in progress
    /// </summary>
    bool IsRefreshingTuners { get; }
    
    /// <summary>
    /// Last error message from device discovery
    /// </summary>
    string? LastError { get; }
    
    /// <summary>
    /// When the device was last discovered (UTC)
    /// </summary>
    DateTime? LastDeviceRefresh { get; }
    
    /// <summary>
    /// When tuner status was last refreshed (UTC)
    /// </summary>
    DateTime? LastTunerRefresh { get; }
    
    /// <summary>
    /// Next scheduled device refresh (UTC)
    /// </summary>
    DateTime? NextDeviceRefresh { get; }
    
    /// <summary>
    /// Next scheduled tuner refresh (UTC)
    /// </summary>
    DateTime? NextTunerRefresh { get; }
    
    /// <summary>
    /// Event raised when device state changes
    /// </summary>
    event Action? OnStateChanged;
    
    /// <summary>
    /// Manually triggers device discovery
    /// </summary>
    Task DiscoverDeviceAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Manually triggers tuner status refresh
    /// </summary>
    Task RefreshTunerStatusAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Stops a specific tuner
    /// </summary>
    Task StopTunerAsync(int tunerIndex, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Restarts the device
    /// </summary>
    Task RestartDeviceAsync(CancellationToken cancellationToken = default);
}

public class DeviceStateService : IDeviceStateService
{
    private readonly ILogger<DeviceStateService> _logger;
    private readonly HDHomeRunDeviceClient _httpClient;
    private readonly HDHomeRunService _deviceProtocolService;
    private readonly IAppSettingsService _settingsService;
    
    private readonly object _lock = new();
    private HDHomeRunDeviceInfo? _deviceInfo;
    private HDHomeRunDevice? _protocolDevice;
    private List<TunerStatus> _tunerStatuses = [];
    private string? _lastError;
    private bool _isDiscovering;
    private bool _isRefreshingTuners;
    private DateTime? _lastDeviceRefresh;
    private DateTime? _lastTunerRefresh;
    private DateTime? _nextDeviceRefresh;
    private DateTime? _nextTunerRefresh;
    
    public HDHomeRunDeviceInfo? DeviceInfo => _deviceInfo;
    public HDHomeRunDevice? ProtocolDevice => _protocolDevice;
    public IReadOnlyList<TunerStatus> TunerStatuses => _tunerStatuses.AsReadOnly();
    public bool IsDiscovered => _deviceInfo != null;
    public bool IsDiscovering => _isDiscovering;
    public bool IsRefreshingTuners => _isRefreshingTuners;
    public string? LastError => _lastError;
    public DateTime? LastDeviceRefresh => _lastDeviceRefresh;
    public DateTime? LastTunerRefresh => _lastTunerRefresh;
    public DateTime? NextDeviceRefresh => _nextDeviceRefresh;
    public DateTime? NextTunerRefresh => _nextTunerRefresh;
    
    public event Action? OnStateChanged;
    
    public DeviceStateService(
        ILogger<DeviceStateService> logger,
        HDHomeRunDeviceClient httpClient,
        HDHomeRunService deviceProtocolService,
        IAppSettingsService settingsService)
    {
        _logger = logger;
        _httpClient = httpClient;
        _deviceProtocolService = deviceProtocolService;
        _settingsService = settingsService;
    }
    
    public async Task DiscoverDeviceAsync(CancellationToken cancellationToken = default)
    {
        if (_isDiscovering) return;
        
        _isDiscovering = true;
        _lastError = null;
        NotifyStateChanged();
        
        try
        {
            _logger.LogInformation("Discovering HDHomeRun device at {Address}", _settingsService.Settings.DeviceAddress);
            
            // Discover via HTTP API for device info
            _deviceInfo = await _httpClient.DiscoverDeviceAsync();
            _lastDeviceRefresh = DateTime.UtcNow;
            _nextDeviceRefresh = _lastDeviceRefresh.Value.AddMinutes(10);
            
            _logger.LogInformation("Discovered device: {Model} ({DeviceId})", 
                _deviceInfo.ModelNumber, _deviceInfo.DeviceID);
            
            // Connect via native protocol for control
            var deviceAddress = _settingsService.Settings.DeviceAddress;
            _protocolDevice = await _deviceProtocolService.GetDeviceByIpAsync(deviceAddress, cancellationToken);
            
            if (_protocolDevice != null)
            {
                _logger.LogInformation("Connected to device via native protocol");
                
                // Initial tuner status fetch
                await RefreshTunerStatusInternalAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to discover device");
            _lastError = ex.Message;
            _deviceInfo = null;
            _protocolDevice = null;
            _tunerStatuses.Clear();
            
            // Retry in 1 minute on failure
            _nextDeviceRefresh = DateTime.UtcNow.AddMinutes(1);
        }
        finally
        {
            _isDiscovering = false;
            NotifyStateChanged();
        }
    }
    
    public async Task RefreshTunerStatusAsync(CancellationToken cancellationToken = default)
    {
        if (_isRefreshingTuners || _protocolDevice == null) return;
        
        _isRefreshingTuners = true;
        NotifyStateChanged();
        
        try
        {
            await RefreshTunerStatusInternalAsync(cancellationToken);
        }
        finally
        {
            _isRefreshingTuners = false;
            NotifyStateChanged();
        }
    }
    
    private async Task RefreshTunerStatusInternalAsync(CancellationToken cancellationToken)
    {
        if (_protocolDevice == null) return;
        
        var newStatuses = new List<TunerStatus>();
        var tunerCount = _deviceInfo?.TunerCount ?? _protocolDevice.DeviceInfo.TunerCount;
        
        for (int i = 0; i < tunerCount; i++)
        {
            try
            {
                var status = await _protocolDevice.GetTunerStatusAsync(i, cancellationToken);
                newStatuses.Add(status);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to get tuner {TunerIndex} status", i);
                newStatuses.Add(new TunerStatus { TunerIndex = i });
            }
        }
        
        lock (_lock)
        {
            _tunerStatuses = newStatuses;
            _lastTunerRefresh = DateTime.UtcNow;
            // Note: _nextTunerRefresh is set by DeviceRefreshService based on settings
        }
        
        _logger.LogDebug("Refreshed status for {Count} tuners", newStatuses.Count);
    }
    
    public async Task StopTunerAsync(int tunerIndex, CancellationToken cancellationToken = default)
    {
        if (_protocolDevice == null)
        {
            throw new InvalidOperationException("Device not connected");
        }
        
        _logger.LogInformation("Stopping tuner {TunerIndex}", tunerIndex);
        await _protocolDevice.StopStreamingAsync(tunerIndex, cancellationToken);
        
        // Refresh tuner status after stopping
        await RefreshTunerStatusAsync(cancellationToken);
    }
    
    public async Task RestartDeviceAsync(CancellationToken cancellationToken = default)
    {
        if (_protocolDevice == null)
        {
            throw new InvalidOperationException("Device not connected");
        }
        
        _logger.LogWarning("Restarting HDHomeRun device");
        await _protocolDevice.RestartAsync(cancellationToken);
        
        // Clear state - device will be unavailable for ~30 seconds
        lock (_lock)
        {
            _deviceInfo = null;
            _protocolDevice = null;
            _tunerStatuses.Clear();
            _lastError = null;
            _nextDeviceRefresh = DateTime.UtcNow.AddSeconds(45); // Try to reconnect after restart
        }
        
        NotifyStateChanged();
    }
    
    internal void SetNextDeviceRefresh(DateTime? utcTime)
    {
        _nextDeviceRefresh = utcTime;
    }
    
    internal void SetNextTunerRefresh(DateTime? utcTime)
    {
        _nextTunerRefresh = utcTime;
    }
    
    private void NotifyStateChanged()
    {
        try
        {
            OnStateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error notifying state change");
        }
    }
}
