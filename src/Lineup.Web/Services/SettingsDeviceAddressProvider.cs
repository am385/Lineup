using Lineup.HDHomeRun.Device;

namespace Lineup.Web.Services;

/// <summary>
/// Device address provider that reads from application settings.
/// Allows dynamic changing of the HDHomeRun device address.
/// </summary>
public class SettingsDeviceAddressProvider : IDeviceAddressProvider
{
    private readonly IAppSettingsService _settingsService;

    /// <summary>
    /// Initializes a new instance of the <see cref="SettingsDeviceAddressProvider"/> class.
    /// </summary>
    public SettingsDeviceAddressProvider(IAppSettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    /// <summary>
    /// Gets device address.
    /// </summary>
    public string DeviceAddress => _settingsService.Settings.DeviceAddress;
}
