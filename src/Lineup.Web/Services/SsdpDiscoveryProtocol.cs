using System.Text;

namespace Lineup.Web.Services;

/// <summary>
/// Parses and formats SSDP messages for a virtual UPnP MediaServer.
/// </summary>
public static class SsdpDiscoveryProtocol
{
    /// <summary>UPnP root-device search target.</summary>
    public const string RootDeviceTarget = "upnp:rootdevice";

    /// <summary>UPnP MediaServer search target.</summary>
    public const string MediaServerTarget = "urn:schemas-upnp-org:device:MediaServer:1";

    private const string AllTarget = "ssdp:all";

    /// <summary>
    /// Creates SSDP responses for a supported M-SEARCH datagram.
    /// </summary>
    /// <param name="request">Raw HTTPU request text.</param>
    /// <param name="device">Virtual device to advertise.</param>
    /// <returns>Zero or more complete HTTPU responses.</returns>
    public static IReadOnlyList<string> CreateSearchResponses(string request, HdHomeRunAdvertisedDevice device)
    {
        if (!TryParseSearch(request, out var requestedTarget))
        {
            return [];
        }

        var targets = requestedTarget.Equals(AllTarget, StringComparison.OrdinalIgnoreCase)
            ? new[] { RootDeviceTarget, MediaServerTarget }
            : new[] { requestedTarget };
        return targets.Select(target => FormatSearchResponse(target, device)).ToArray();
    }

    /// <summary>
    /// Validates a supported SSDP M-SEARCH request and extracts its search target.
    /// </summary>
    /// <param name="request">Raw HTTPU request text.</param>
    /// <param name="target">Supported search target when parsing succeeds.</param>
    /// <returns><see langword="true"/> when the datagram is a supported discovery search.</returns>
    public static bool TryParseSearch(string request, out string target)
    {
        return TryGetSearchTarget(request, out target);
    }

    /// <summary>
    /// Creates an SSDP alive or byebye notification.
    /// </summary>
    /// <param name="target">Notification target.</param>
    /// <param name="device">Virtual device to advertise.</param>
    /// <param name="alive">Whether to emit an alive rather than byebye notification.</param>
    /// <returns>A complete HTTPU NOTIFY message.</returns>
    public static string FormatNotification(string target, HdHomeRunAdvertisedDevice device, bool alive)
    {
        var builder = new StringBuilder()
            .Append("NOTIFY * HTTP/1.1\r\n")
            .Append("HOST: 239.255.255.250:1900\r\n")
            .Append("NT: ").Append(target).Append("\r\n")
            .Append("NTS: ssdp:").Append(alive ? "alive" : "byebye").Append("\r\n")
            .Append("USN: ").Append(GetUsn(target, device)).Append("\r\n");
        if (alive)
        {
            builder.Append("CACHE-CONTROL: max-age=1800\r\n")
                .Append("LOCATION: ").Append(new Uri(device.BaseUri, "device.xml")).Append("\r\n")
                .Append("SERVER: Lineup/1.0 UPnP/1.0 Lineup-HDHomeRun/1.0\r\n");
        }

        return builder.Append("\r\n").ToString();
    }

    private static bool TryGetSearchTarget(string request, out string target)
    {
        target = string.Empty;
        var lines = request.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0 || !lines[0].Trim().Equals("M-SEARCH * HTTP/1.1", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string? man = null;
        string? foundTarget = null;
        foreach (var line in lines.Skip(1))
        {
            var parts = line.Split(':', 2);
            if (parts.Length != 2)
            {
                continue;
            }

            if (parts[0].Trim().Equals("MAN", StringComparison.OrdinalIgnoreCase))
            {
                man = parts[1].Trim();
            }
            else if (parts[0].Trim().Equals("ST", StringComparison.OrdinalIgnoreCase))
            {
                foundTarget = parts[1].Trim();
            }
        }

        if (man == null || !man.Trim('"').Equals("ssdp:discover", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(foundTarget))
        {
            return false;
        }

        target = foundTarget;
        return target.Equals(AllTarget, StringComparison.OrdinalIgnoreCase) ||
            target.Equals(RootDeviceTarget, StringComparison.OrdinalIgnoreCase) ||
            target.Equals(MediaServerTarget, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatSearchResponse(string target, HdHomeRunAdvertisedDevice device)
    {
        return new StringBuilder()
            .Append("HTTP/1.1 200 OK\r\n")
            .Append("CACHE-CONTROL: max-age=1800\r\n")
            .Append("EXT:\r\n")
            .Append("LOCATION: ").Append(new Uri(device.BaseUri, "device.xml")).Append("\r\n")
            .Append("SERVER: Lineup/1.0 UPnP/1.0 Lineup-HDHomeRun/1.0\r\n")
            .Append("ST: ").Append(target).Append("\r\n")
            .Append("USN: ").Append(GetUsn(target, device)).Append("\r\n\r\n")
            .ToString();
    }

    private static string GetUsn(string target, HdHomeRunAdvertisedDevice device)
    {
        return $"uuid:{device.Uuid}::{target}";
    }
}
