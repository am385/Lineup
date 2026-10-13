using System.Globalization;
using System.Text.Json;
using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;

namespace Lineup.SiliconDust.HDHomeRun.VirtualTuner;

/// <summary>Adapts current application settings to virtual HDHomeRun runtime profiles.</summary>
public sealed class HdHomeRunProxyProfileProvider : IHdHomeRunProxyProfileProvider
{
    private readonly Func<IReadOnlyList<VirtualDeviceDefinition>> _virtualDevices;
    private readonly IPhysicalDeviceResolver _physicalDevices;
    private readonly HdHomeRunVirtualTunerDriver _driver;

    /// <summary>Initializes the profile provider.</summary>
    public HdHomeRunProxyProfileProvider(
        Func<IReadOnlyList<VirtualDeviceDefinition>> virtualDevices,
        IPhysicalDeviceResolver physicalDevices,
        HdHomeRunVirtualTunerDriver driver)
    {
        _virtualDevices = virtualDevices;
        _physicalDevices = physicalDevices;
        _driver = driver;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<HdHomeRunProxyProfileSnapshot>> GetProfilesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<HdHomeRunProxyProfileSnapshot> profiles = GetEnabledDevices()
            .Select((device, index) => CreateSnapshot(device, index == 0))
            .ToArray();
        return Task.FromResult(profiles);
    }

    /// <inheritdoc />
    public Task<HdHomeRunProxyProfileSnapshot?> GetPrimaryProfileAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var device = GetEnabledDevices().FirstOrDefault();
        return Task.FromResult(device is null ? null : CreateSnapshot(device, true));
    }

    /// <inheritdoc />
    public Task<HdHomeRunProxyProfileSnapshot?> FindProfileAsync(string virtualDeviceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var enabled = GetEnabledDevices();
        var device = enabled.FirstOrDefault(candidate =>
            _driver.GetDeviceId(candidate).Equals(virtualDeviceId, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(device is null ? null : CreateSnapshot(device, device == enabled[0]));
    }

    private VirtualDeviceDefinition[] GetEnabledDevices() =>
        _virtualDevices()
            .Where(device => device.Enabled && string.Equals(device.Adapter.Kind, HdHomeRunVirtualTunerDriver.AdapterKind, StringComparison.OrdinalIgnoreCase))
            .ToArray();

    private HdHomeRunProxyProfileSnapshot CreateSnapshot(VirtualDeviceDefinition device, bool isPrimary)
    {
        var configuration = device.Adapter.Configuration.Deserialize<HdHomeRunVirtualTunerConfiguration>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var deviceId = _driver.GetDeviceId(device);
        return new HdHomeRunProxyProfileSnapshot
        {
            VirtualDevice = device,
            IsPrimary = isPrimary,
            DeviceId = uint.Parse(deviceId, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            DeviceAuth = HdHomeRunProxyIdentity.CreateDeviceAuth(device.Id.Value.ToString("N")),
            TunerCount = _driver.GetTunerCount(device, _physicalDevices.Devices.Select(physical => physical.Snapshot)),
            TunerCountCap = configuration.TunerCountCap,
            FriendlyName = device.Name,
            AdvertisedBaseUrl = configuration.AdvertisedBaseUrl
        };
    }
}
