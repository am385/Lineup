using System.Net;

namespace Lineup.HDHomeRun.Device.Protocol;

/// <summary>
/// Creates HTTP control clients for discovered HDHomeRun devices.
/// </summary>
public interface IHDHomeRunHttpControlFactory
{
    /// <summary>
    /// Creates a control client for a device address.
    /// </summary>
    /// <param name="deviceAddress">The device IP address.</param>
    /// <returns>A new HTTP control client.</returns>
    HDHomeRunHttpControl Create(IPAddress deviceAddress);

    /// <summary>
    /// Creates a control client for a device base URL.
    /// </summary>
    /// <param name="baseUrl">The device base URL.</param>
    /// <returns>A new HTTP control client.</returns>
    HDHomeRunHttpControl Create(string baseUrl);
}
