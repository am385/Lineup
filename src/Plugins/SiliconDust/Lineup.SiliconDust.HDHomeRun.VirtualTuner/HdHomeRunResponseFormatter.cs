using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Lineup.SiliconDust.HDHomeRun.VirtualTuner;

/// <summary>Formats HDHomeRun-compatible HTTP response representations.</summary>
public static class HdHomeRunProxyLineupFormatter
{
    /// <summary>Formats channels as an HDHomeRun JSON lineup.</summary>
    /// <param name="channels">Virtual-device channels.</param>
    /// <returns>A JSON lineup document.</returns>
    public static string ToJson(IEnumerable<HdHomeRunProxyChannel> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        return JsonSerializer.Serialize(channels);
    }

    /// <summary>Formats channels as an HDHomeRun XML lineup.</summary>
    /// <param name="channels">Virtual-device channels.</param>
    /// <returns>An XML lineup document.</returns>
    public static string ToXml(IEnumerable<HdHomeRunProxyChannel> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        var root = new XElement("Lineup");
        foreach (var channel in channels)
        {
            var program = new XElement("Program",
                new XElement("GuideNumber", SanitizeXmlValue(channel.GuideNumber)),
                new XElement("GuideName", SanitizeXmlValue(channel.GuideName)));
            AddElement(program, "Tags", channel.Tags);
            AddElement(program, "VideoCodec", channel.VideoCodec);
            AddElement(program, "AudioCodec", channel.AudioCodec);
            AddElement(program, "HD", channel.Hd);
            AddElement(program, "DRM", channel.Drm);
            AddElement(program, "Favorite", channel.Favorite);
            AddElement(program, "SignalStrength", channel.SignalStrength);
            AddElement(program, "SignalQuality", channel.SignalQuality);
            if (channel.AdditionalProperties != null)
            {
                foreach (var property in channel.AdditionalProperties)
                {
                    if (IsValidXmlName(property.Key) && TryGetXmlValue(property.Value, out var value))
                    {
                        program.Add(new XElement(property.Key, SanitizeXmlValue(value!)));
                    }
                }
            }
            program.Add(new XElement("URL", SanitizeXmlValue(channel.Url)));
            root.Add(program);
        }

        return $"<?xml version=\"1.0\" encoding=\"utf-8\"?>{root.ToString(SaveOptions.DisableFormatting)}";
    }

    /// <summary>Formats channels as an HDHomeRun extended M3U lineup.</summary>
    /// <param name="channels">Virtual-device channels.</param>
    /// <returns>An extended M3U playlist.</returns>
    public static string ToM3u(IEnumerable<HdHomeRunProxyChannel> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        var playlist = new StringBuilder("#EXTM3U\n");
        foreach (var channel in channels)
        {
            var guideNumber = SanitizeM3uValue(channel.GuideNumber, isAttribute: true);
            var guideName = SanitizeM3uValue(channel.GuideName, isAttribute: true);
            playlist.Append("#EXTINF:-1 channel-id=\"").Append(guideNumber).Append("\" channel-number=\"").Append(guideNumber)
                .Append("\" tvg-name=\"").Append(guideName).Append('"');
            if (IsFavorite(channel))
            {
                playlist.Append(" group-title=\"Favorites\"");
            }
            playlist.Append(',').Append(SanitizeM3uValue(channel.GuideName, isAttribute: false)).Append('\n').Append(channel.Url).Append('\n');
        }
        return playlist.ToString();
    }

    /// <summary>Formats a UPnP device description for the virtual tuner.</summary>
    /// <param name="baseUri">Virtual device base URI.</param>
    /// <param name="friendlyName">Client-facing device name.</param>
    /// <param name="deviceId">Virtual HDHomeRun DeviceID.</param>
    /// <returns>A UPnP device description document.</returns>
    public static string ToDeviceXml(Uri baseUri, string friendlyName, string deviceId)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(friendlyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        XNamespace ns = "urn:schemas-upnp-org:device-1-0";
        var document = new XDocument(
            new XElement(ns + "root",
                new XElement(ns + "URLBase", baseUri.AbsoluteUri.TrimEnd('/')),
                new XElement(ns + "specVersion", new XElement(ns + "major", 1), new XElement(ns + "minor", 0)),
                new XElement(ns + "device",
                    new XElement(ns + "deviceType", "urn:schemas-upnp-org:device:MediaServer:1"),
                    new XElement(ns + "friendlyName", friendlyName),
                    new XElement(ns + "manufacturer", "Lineup"),
                    new XElement(ns + "modelName", "Lineup HDHomeRun Proxy"),
                    new XElement(ns + "modelNumber", "LINEUP-1"),
                    new XElement(ns + "serialNumber", deviceId),
                    new XElement(ns + "UDN", $"uuid:{deviceId}"))));
        return document.ToString(SaveOptions.DisableFormatting);
    }

    private static void AddElement(XElement parent, string name, string? value)
    {
        if (value != null)
        {
            parent.Add(new XElement(name, SanitizeXmlValue(value)));
        }
    }

    private static void AddElement(XElement parent, string name, int? value)
    {
        if (value.HasValue)
        {
            parent.Add(new XElement(name, value.Value.ToString(CultureInfo.InvariantCulture)));
        }
    }

    private static bool IsFavorite(HdHomeRunProxyChannel channel) =>
        channel.Favorite == 1 ||
        channel.Tags?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Contains("favorite", StringComparer.OrdinalIgnoreCase) == true;

    private static string SanitizeM3uValue(string value, bool isAttribute)
    {
        var sanitized = value.Replace('\r', ' ').Replace('\n', ' ');
        return isAttribute ? sanitized.Replace('"', '\'') : sanitized;
    }

    private static bool IsValidXmlName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }
        try
        {
            XmlConvert.VerifyNCName(name);
            return true;
        }
        catch (Exception exception) when (exception is XmlException or ArgumentException)
        {
            return false;
        }
    }

    private static string SanitizeXmlValue(string value)
    {
        var sanitized = new StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes())
        {
            var scalar = rune.Value;
            if (scalar is 0x9 or 0xA or 0xD || scalar is >= 0x20 and <= 0xD7FF || scalar is >= 0xE000 and <= 0xFFFD || scalar is >= 0x10000 and <= 0x10FFFF)
            {
                sanitized.Append(rune.ToString());
            }
        }
        return sanitized.ToString();
    }

    private static bool TryGetXmlValue(JsonElement element, out string? value)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                value = null;
                return false;
            case JsonValueKind.String:
                value = element.GetString();
                return true;
            default:
                value = element.GetRawText();
                return true;
        }
    }
}
