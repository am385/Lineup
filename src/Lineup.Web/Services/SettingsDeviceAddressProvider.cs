using Lineup.HDHomeRun.Device;

namespace Lineup.Web.Services;

/// <summary>
/// Device address provider that reads from application settings.
/// Allows dynamic changing of the HDHomeRun device address.
/// </summary>
public class SettingsDeviceAddressProvider : IDeviceAddressProvider
{
    private readonly IAppSettingsService _settingsService;

    public SettingsDeviceAddressProvider(IAppSettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public string DeviceAddress => _settingsService.Settings.DeviceAddress;
}
