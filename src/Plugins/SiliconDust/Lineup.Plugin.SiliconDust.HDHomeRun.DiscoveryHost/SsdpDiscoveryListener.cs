using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Lineup.Plugin.SiliconDust.HDHomeRun.VirtualTuner;

namespace Lineup.Plugin.SiliconDust.HDHomeRun.DiscoveryHost;

/// <summary>Processes SSDP searches for the shared discovery worker.</summary>
public static class SsdpDiscoveryListener
{
    /// <summary>Receives supported searches, limits response attempts, and replies with the configured device advertisements until canceled.</summary>
    public static async Task ReceiveAsync(UdpClient client, IReadOnlyList<HdHomeRunAdvertisedDevice> advertisements, Func<IPAddress, bool> tryAcquireResponsePermit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(advertisements);
        ArgumentNullException.ThrowIfNull(tryAcquireResponsePermit);
        try
        {
            while (true)
            {
                var request = await client.ReceiveAsync(cancellationToken);
                var text = Encoding.ASCII.GetString(request.Buffer);
                if (!SsdpDiscoveryProtocol.TryParseSearch(text, out var requestedTarget) ||
                    !tryAcquireResponsePermit(request.RemoteEndPoint.Address))
                {
                    continue;
                }
                var responseCount = 0;
                var respondedDeviceIds = new List<string>();
                foreach (var advertisement in advertisements)
                {
                    var responses = SsdpDiscoveryProtocol.CreateSearchResponses(text, advertisement);
                    foreach (var response in responses)
                    {
                        await client.SendAsync(Encoding.ASCII.GetBytes(response), request.RemoteEndPoint, cancellationToken);
                        responseCount++;
                    }
                    if (responses.Count > 0)
                    {
                        respondedDeviceIds.Add(advertisement.DeviceId.ToString("X8", CultureInfo.InvariantCulture));
                    }
                }
                var deviceIds = respondedDeviceIds.Count == 0 ? "none" : string.Join(", ", respondedDeviceIds);
                Console.WriteLine($"SSDP discovery request received from {request.RemoteEndPoint} for '{requestedTarget}'; sent {responseCount} response(s) for device(s): {deviceIds}.");
            }
        }
        catch (SocketException exception) when (cancellationToken.IsCancellationRequested && exception.SocketErrorCode == SocketError.OperationAborted)
        {
            throw new OperationCanceledException("SSDP discovery was canceled.", exception, cancellationToken);
        }
    }
}
