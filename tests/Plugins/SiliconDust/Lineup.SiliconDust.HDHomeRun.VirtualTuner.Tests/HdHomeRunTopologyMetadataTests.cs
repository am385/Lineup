using System.Text.Json;
using System.Xml.Linq;
using Lineup.Common.Streaming;
using Xunit;

namespace Lineup.SiliconDust.HDHomeRun.VirtualTuner.Tests;

/// <summary>Verifies provider-neutral lineup metadata survives HDHomeRun formatting without source-output claims.</summary>
public sealed class HdHomeRunTopologyMetadataTests
{
    /// <summary>Verifies topology favorites and normalized tags survive JSON, XML, and M3U formatting.</summary>
    [Theory]
    [InlineData("True", "subscription", "subscription,favorite", true)]
    [InlineData("true", " subscription , FAVORITE, subscription,, ", "subscription,FAVORITE", true)]
    [InlineData("False", "subscription", "subscription", false)]
    [InlineData("FALSE", "favorite", "favorite", false)]
    [InlineData("invalid", "subscription", "subscription", false)]
    [InlineData(null, " subscription , subscription ", "subscription", false)]
    [InlineData("True", null, "favorite", true)]
    [InlineData("False", " , , ", null, false)]
    [InlineData(null, null, null, false)]
    public void Create_Topology_PreservesFavoriteAndTags(string? favorite, string? tags, string? expectedTags, bool expectedFavorite)
    {
        // Arrange
        var metadata = new Dictionary<string, string>();
        if (favorite is not null)
        {
            metadata["favorite"] = favorite;
        }
        if (tags is not null)
        {
            metadata["tags"] = tags;
        }
        var source = new ResolvedVirtualChannel("104.1", "Saved name", []) { Metadata = metadata };

        // Act
        var channel = HdHomeRunProxyChannel.Create(source, new Uri("http://lineup.local/hdhomerun/test/"));
        using var json = JsonDocument.Parse(HdHomeRunProxyLineupFormatter.ToJson([channel]));
        var xml = XDocument.Parse(HdHomeRunProxyLineupFormatter.ToXml([channel]));
        var m3u = HdHomeRunProxyLineupFormatter.ToM3u([channel]);

        // Assert
        Assert.Equal(expectedTags, channel.Tags);
        Assert.Equal(expectedFavorite ? (int?)1 : null, channel.Favorite);
        Assert.Equal("104.1", channel.GuideNumber);
        Assert.Equal("Saved name", channel.GuideName);
        Assert.Equal("http://lineup.local/hdhomerun/test/auto/v104.1", channel.Url);
        if (expectedTags is null)
        {
            Assert.False(json.RootElement[0].TryGetProperty("Tags", out _));
            Assert.Null(xml.Root!.Element("Program")!.Element("Tags"));
        }
        else
        {
            Assert.Equal(expectedTags, json.RootElement[0].GetProperty("Tags").GetString());
            Assert.Equal(expectedTags, xml.Root!.Element("Program")!.Element("Tags")!.Value);
        }
        if (expectedFavorite)
        {
            Assert.Equal(1, json.RootElement[0].GetProperty("Favorite").GetInt32());
            Assert.Equal("1", xml.Root!.Element("Program")!.Element("Favorite")!.Value);
        }
        else
        {
            Assert.False(json.RootElement[0].TryGetProperty("Favorite", out _));
            Assert.Null(xml.Root!.Element("Program")!.Element("Favorite"));
        }
        Assert.Equal(expectedTags?.Contains("favorite", StringComparison.OrdinalIgnoreCase) == true, m3u.Contains("group-title=\"Favorites\"", StringComparison.Ordinal));
        Assert.EndsWith(channel.Url + "\n", m3u);
        Assert.Equal(tags, source.Metadata.GetValueOrDefault("tags"));
    }

    /// <summary>Verifies physical protection and codec metadata are not asserted as effective output properties.</summary>
    [Fact]
    public void Create_Topology_DoesNotCopySourceProtectionOrCodecClaims()
    {
        // Arrange
        var source = new ResolvedVirtualChannel("7.1", "News", [])
        {
            Metadata = new Dictionary<string, string>
            {
                ["favorite"] = "True",
                ["tags"] = "subscription",
                ["drm"] = "True",
                ["hd"] = "True",
                ["videoCodec"] = "HEVC",
                ["audioCodec"] = "AC4",
                ["signalStrength"] = "99",
                ["signalQuality"] = "98"
            }
        };

        // Act
        var channel = HdHomeRunProxyChannel.Create(source, new Uri("http://lineup.local/"));
        var json = JsonSerializer.SerializeToElement(channel);

        // Assert
        Assert.Equal("subscription,favorite", channel.Tags);
        Assert.Equal(1, channel.Favorite);
        foreach (var name in new[] { "DRM", "HD", "VideoCodec", "AudioCodec", "SignalStrength", "SignalQuality" })
        {
            Assert.False(json.TryGetProperty(name, out _), $"Unexpected source claim: {name}");
        }
    }
}
