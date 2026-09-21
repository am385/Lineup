using System.Text.Json;
using System.Xml.Linq;
using Lineup.Web.Controllers;
using Lineup.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>
/// Verifies HDHomeRun-compatible alternate lineup representations.
/// </summary>
public class HdHomeRunProxyLineupFormatterTests
{
    /// <summary>
    /// Verifies XML contains the transformed fields and preserves firmware-defined metadata.
    /// </summary>
    [Fact]
    public void ToXml_WritesNativeLineupShapeAndFutureFields()
    {
        // Arrange
        var channel = CreateChannel() with
        {
            GuideName = "TE\u0001ST",
            AdditionalProperties = new Dictionary<string, JsonElement>
            {
                ["FirmwareMetric"] = JsonSerializer.SerializeToElement(new { Value = 42 }),
                [""] = JsonSerializer.SerializeToElement("empty"),
                ["a:b"] = JsonSerializer.SerializeToElement("colon"),
                ["1bad"] = JsonSerializer.SerializeToElement("number")
            }
        };

        // Act
        var content = HdHomeRunProxyLineupFormatter.ToXml([channel]);
        var document = XDocument.Parse(content);
        var program = Assert.Single(document.Root!.Elements("Program"));

        // Assert
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", content);
        Assert.Equal("7.1", program.Element("GuideNumber")!.Value);
        Assert.Equal("TEST", program.Element("GuideName")!.Value);
        Assert.Equal("H264", program.Element("VideoCodec")!.Value);
        Assert.Equal("AC3", program.Element("AudioCodec")!.Value);
        Assert.Equal("88", program.Element("SignalStrength")!.Value);
        Assert.Equal("""{"Value":42}""", program.Element("FirmwareMetric")!.Value);
        Assert.Equal("http://lineup.local/auto/v7.1", program.Element("URL")!.Value);
        Assert.DoesNotContain("empty", content);
        Assert.DoesNotContain("colon", content);
        Assert.DoesNotContain("number", content);
    }

    /// <summary>
    /// Verifies M3U uses native metadata, Favorite grouping, safe text, and the transformed stream URL.
    /// </summary>
    [Fact]
    public void ToM3u_WritesNativeExtendedPlaylist()
    {
        // Arrange
        var channel = CreateChannel() with
        {
            GuideName = "News \"One\"\nExtra",
            Tags = "subscription,favorite",
            Favorite = null
        };

        // Act
        var lines = HdHomeRunProxyLineupFormatter.ToM3u([channel]).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Assert
        Assert.Equal("#EXTM3U", lines[0]);
        Assert.Equal("""#EXTINF:-1 channel-id="7.1" channel-number="7.1" tvg-name="News 'One' Extra" group-title="Favorites",News "One" Extra""", lines[1]);
        Assert.Equal("http://lineup.local/auto/v7.1", lines[2]);
    }

    /// <summary>
    /// Verifies root and device-scoped routes match the existing JSON endpoint pattern.
    /// </summary>
    [Theory]
    [InlineData(nameof(HdHomeRunProxyController.LineupXml), "/lineup.xml", "/hdhomerun/{virtualDeviceId}/lineup.xml")]
    [InlineData(nameof(HdHomeRunProxyController.LineupM3u), "/lineup.m3u", "/hdhomerun/{virtualDeviceId}/lineup.m3u")]
    public void AlternateLineups_ExposeRootAndDeviceScopedRoutes(string methodName, string rootRoute, string scopedRoute)
    {
        // Arrange
        var method = typeof(HdHomeRunProxyController).GetMethod(methodName)!;

        // Act
        var routes = method.GetCustomAttributes(typeof(HttpGetAttribute), inherit: false)
            .Cast<HttpGetAttribute>()
            .Select(attribute => attribute.Template)
            .ToArray();

        // Assert
        Assert.Contains(rootRoute, routes);
        Assert.Contains(scopedRoute, routes);
    }

    private static HdHomeRunProxyChannel CreateChannel() => new()
    {
        GuideNumber = "7.1",
        GuideName = "TEST",
        Tags = "favorite",
        VideoCodec = "H264",
        AudioCodec = "AC3",
        Hd = 1,
        Favorite = 1,
        SignalStrength = 88,
        SignalQuality = 93,
        Url = "http://lineup.local/auto/v7.1"
    };
}
