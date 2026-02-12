namespace Lineup.HDHomeRun.Device;

/// <summary>
/// Provides the device address for HDHomeRun device connections.
/// Implement this interface to provide dynamic device address configuration.
/// </summary>
public interface IDeviceAddressProvider
{
    /// <summary>
    /// Gets the current device address (hostname or IP).
    /// </summary>
    string DeviceAddress { get; }

    /// <summary>
    /// Gets the full base URI for the device.
    /// </summary>
    Uri BaseUri => new($"http://{DeviceAddress}");
}

/// <summary>
/// Default implementation that uses a fixed device address.
/// </summary>
public class FixedDeviceAddressProvider : IDeviceAddressProvider
{
    public string DeviceAddress { get; }

    public FixedDeviceAddressProvider(string deviceAddress)
    {
        DeviceAddress = deviceAddress ?? throw new ArgumentNullException(nameof(deviceAddress));
    }
}
