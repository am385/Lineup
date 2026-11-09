using System.Net;
using System.Net.Sockets;

namespace Lineup.Plugin.SiliconDust.HDHomeRun.VirtualTuner;

/// <summary>
/// Creates SSDP UDP sockets with portable address-sharing semantics.
/// </summary>
public static class SsdpDiscoverySocket
{
    /// <summary>
    /// Creates and binds an IPv4 UDP listener with optional address sharing and support for same-host multicast clients on Windows.
    /// </summary>
    /// <param name="localAddress">Specific local interface or <see cref="IPAddress.Any"/>.</param>
    /// <param name="port">UDP port, or zero to select an ephemeral port.</param>
    /// <param name="allowAddressSharing">Whether another process may bind the same address and port.</param>
    /// <returns>A bound UDP listener owned by the caller.</returns>
    public static UdpClient CreateBoundListener(IPAddress localAddress, int port, bool allowAddressSharing = true)
    {
        var client = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            client.ExclusiveAddressUse = !allowAddressSharing;
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, allowAddressSharing);
            client.Client.Bind(new IPEndPoint(localAddress, port));
            // Windows applies multicast loopback to reception; POSIX applies it to transmission.
            client.MulticastLoopback = OperatingSystem.IsWindows();
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}
