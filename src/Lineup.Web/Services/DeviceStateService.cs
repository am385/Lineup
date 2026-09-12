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

/// <summary>
/// Represents device state service.
/// </summary>
public class DeviceStateService : IDeviceStateService
{
    private readonly ILogger<DeviceStateService> _logger;
    private readonly HDHomeRunDeviceClient _httpClient;
    private readonly HDHomeRunService _deviceProtocolService;
    private readonly IAppSettingsService _settingsService;

    private readonly object _lock = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private HDHomeRunDeviceInfo? _deviceInfo;
    private HDHomeRunDevice? _protocolDevice;
    private IReadOnlyList<TunerStatus> _tunerStatuses = Array.Empty<TunerStatus>();
    private string? _lastError;
    private bool _isDiscovering;
    private bool _isRefreshingTuners;
    private DateTime? _lastDeviceRefresh;
    private DateTime? _lastTunerRefresh;
    private DateTime? _nextDeviceRefresh;
    private DateTime? _nextTunerRefresh;

    /// <summary>
    /// Gets device info.
    /// </summary>
    public HDHomeRunDeviceInfo? DeviceInfo => _deviceInfo;
    /// <summary>
    /// Gets protocol device.
    /// </summary>
    public HDHomeRunDevice? ProtocolDevice => _protocolDevice;
    /// <summary>
    /// Gets tuner statuses.
    /// </summary>
    public IReadOnlyList<TunerStatus> TunerStatuses => Volatile.Read(ref _tunerStatuses);
    /// <summary>
    /// Gets is discovered.
    /// </summary>
    public bool IsDiscovered => _deviceInfo != null;
    /// <summary>
    /// Gets is discovering.
    /// </summary>
    public bool IsDiscovering => _isDiscovering;
    /// <summary>
    /// Gets is refreshing tuners.
    /// </summary>
    public bool IsRefreshingTuners => _isRefreshingTuners;
    /// <summary>
    /// Gets last error.
    /// </summary>
    public string? LastError => _lastError;
    /// <summary>
    /// Gets last device refresh.
    /// </summary>
    public DateTime? LastDeviceRefresh => _lastDeviceRefresh;
    /// <summary>
    /// Gets last tuner refresh.
    /// </summary>
    public DateTime? LastTunerRefresh => _lastTunerRefresh;
    /// <summary>
    /// Gets next device refresh.
    /// </summary>
    public DateTime? NextDeviceRefresh => _nextDeviceRefresh;
    /// <summary>
    /// Gets next tuner refresh.
    /// </summary>
    public DateTime? NextTunerRefresh => _nextTunerRefresh;

    /// <summary>
    /// Occurs when on state changed.
    /// </summary>
    public event Action? OnStateChanged;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeviceStateService"/> class.
    /// </summary>
    public DeviceStateService(ILogger<DeviceStateService> logger, HDHomeRunDeviceClient httpClient, HDHomeRunService deviceProtocolService, IAppSettingsService settingsService)
    {
        _logger = logger;
        _httpClient = httpClient;
        _deviceProtocolService = deviceProtocolService;
        _settingsService = settingsService;
    }

    /// <summary>
    /// Performs the discover device operation.
    /// </summary>
    public async Task DiscoverDeviceAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            _isDiscovering = true;
            _lastError = null;
            NotifyStateChanged();
            _logger.LogInformation("Discovering HDHomeRun device at {Address}", _settingsService.Settings.DeviceAddress);

            // Discover via HTTP API for device info
            var deviceInfo = await _httpClient.DiscoverDeviceAsync(cancellationToken);
            var refreshedAt = DateTime.UtcNow;

            _logger.LogInformation("Discovered device: {Model} ({DeviceId})", deviceInfo.ModelNumber, deviceInfo.DeviceID);

            var deviceAddress = _settingsService.Settings.DeviceAddress;
            var protocolDevice = await _deviceProtocolService.GetDeviceByIpAsync(deviceAddress, cancellationToken);
            lock (_lock)
            {
                _deviceInfo = deviceInfo;
                _protocolDevice = protocolDevice;
                _lastDeviceRefresh = refreshedAt;
                _nextDeviceRefresh = refreshedAt.AddMinutes(10);
            }

            if (protocolDevice != null)
            {
                _logger.LogInformation("Connected to device via native protocol");
                await RefreshTunerStatusInternalAsync(protocolDevice, deviceInfo.TunerCount, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to discover device");
            lock (_lock)
            {
                _lastError = ex.Message;
                _deviceInfo = null;
                _protocolDevice = null;
                _tunerStatuses = Array.Empty<TunerStatus>();
                _nextDeviceRefresh = DateTime.UtcNow.AddMinutes(1);
            }
        }
        finally
        {
            _isDiscovering = false;
            NotifyStateChanged();
            _operationGate.Release();
        }
    }

    /// <summary>
    /// Performs the refresh tuner status operation.
    /// </summary>
    public async Task RefreshTunerStatusAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var protocolDevice = _protocolDevice;
            if (protocolDevice == null)
            {
                return;
            }

            _isRefreshingTuners = true;
            NotifyStateChanged();
            var tunerCount = _deviceInfo?.TunerCount ?? protocolDevice.DeviceInfo.TunerCount;
            await RefreshTunerStatusInternalAsync(protocolDevice, tunerCount, cancellationToken);
        }
        finally
        {
            _isRefreshingTuners = false;
            NotifyStateChanged();
            _operationGate.Release();
        }
    }

    private async Task RefreshTunerStatusInternalAsync(HDHomeRunDevice protocolDevice, int tunerCount, CancellationToken cancellationToken)
    {
        var newStatuses = new List<TunerStatus>();

        for (int i = 0; i < tunerCount; i++)
        {
            try
            {
                var status = await protocolDevice.GetTunerStatusAsync(i, cancellationToken);
                newStatuses.Add(status);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to get tuner {TunerIndex} status", i);
                newStatuses.Add(new TunerStatus { TunerIndex = i });
            }
        }

        lock (_lock)
        {
            _tunerStatuses = newStatuses.ToArray();
            _lastTunerRefresh = DateTime.UtcNow;
            // Note: _nextTunerRefresh is set by DeviceRefreshService based on settings
        }

        _logger.LogDebug("Refreshed status for {Count} tuners", newStatuses.Count);
    }

    /// <summary>
    /// Performs the stop tuner operation.
    /// </summary>
    public async Task StopTunerAsync(int tunerIndex, CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var protocolDevice = _protocolDevice ??
                throw new InvalidOperationException("Device not connected");

            _logger.LogInformation("Stopping tuner {TunerIndex}", tunerIndex);
            await protocolDevice.StopStreamingAsync(tunerIndex, cancellationToken);

            var tunerCount = _deviceInfo?.TunerCount ?? protocolDevice.DeviceInfo.TunerCount;
            await RefreshTunerStatusInternalAsync(protocolDevice, tunerCount, cancellationToken);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>
    /// Performs the restart device operation.
    /// </summary>
    public async Task RestartDeviceAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var protocolDevice = _protocolDevice ??
                throw new InvalidOperationException("Device not connected");

            _logger.LogWarning("Restarting HDHomeRun device");
            await protocolDevice.RestartAsync(cancellationToken);

            lock (_lock)
            {
                if (ReferenceEquals(_protocolDevice, protocolDevice))
                {
                    _deviceInfo = null;
                    _protocolDevice = null;
                    _tunerStatuses = Array.Empty<TunerStatus>();
                    _lastError = null;
                    _nextDeviceRefresh = DateTime.UtcNow.AddSeconds(45);
                }
            }

            NotifyStateChanged();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>
    /// Performs the set next device refresh operation.
    /// </summary>
    internal void SetNextDeviceRefresh(DateTime? utcTime)
    {
        _nextDeviceRefresh = utcTime;
    }

    /// <summary>
    /// Performs the set next tuner refresh operation.
    /// </summary>
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
