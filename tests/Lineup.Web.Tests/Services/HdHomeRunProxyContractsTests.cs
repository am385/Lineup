using System.Text.Json;
using Lineup.HDHomeRun.Device.Models;
using Lineup.Web.Services;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>
/// Verifies the virtual HDHomeRun compatibility contracts.
/// </summary>
public class HdHomeRunProxyContractsTests
{
    /// <summary>
    /// Verifies that generated identities are stable, distinct from the physical ID, and checksum-valid.
    /// </summary>
    [Fact]
    public void CreateDeviceId_ProducesStableValidProxyIdentity()
    {
        // Arrange
        const string physicalDeviceId = "10AC75CB";

        // Act
        var first = HdHomeRunProxyIdentity.CreateDeviceId(physicalDeviceId);
        var second = HdHomeRunProxyIdentity.CreateDeviceId(physicalDeviceId);

        // Assert
        Assert.Equal(first, second);
        Assert.NotEqual(physicalDeviceId, first);
        Assert.True(HdHomeRunProxyIdentity.IsValidDeviceId(first));
    }

    /// <summary>
    /// Verifies that lineup channels advertise Lineup-hosted streams and compatible AC-4 output.
    /// </summary>
    [Fact]
    public void CreateChannel_RewritesStreamUrlAndAc4Codec()
    {
        // Arrange
        var channel = new HDHomeRunChannel
        {
            GuideNumber = "104.1",
            GuideName = "ATSC 3.0",
            VideoCodec = "HEVC",
            AudioCodec = "AC4",
            HD = true,
            DRM = false,
            Favorite = true,
            Tags = "subscription,favorite",
            SignalStrength = 91,
            SignalQuality = 84,
            URL = "http://10.0.60.15:5004/auto/v104.1"
        };

        // Act
        var proxyChannel = HdHomeRunProxyChannel.Create(channel, new Uri("http://lineup.local:8080/"), VirtualTunerVideoMode.ConvertHevcToH264);

        // Assert
        Assert.Equal("http://lineup.local:8080/auto/v104.1", proxyChannel.Url);
        Assert.Equal("H264", proxyChannel.VideoCodec);
        Assert.Equal("AC3", proxyChannel.AudioCodec);
        Assert.Equal("subscription,favorite", proxyChannel.Tags);
        Assert.Equal(1, proxyChannel.Hd);
        Assert.Equal(91, proxyChannel.SignalStrength);
        Assert.Equal(84, proxyChannel.SignalQuality);
    }

    /// <summary>
    /// Verifies that the default virtual-tuner policy preserves HEVC metadata.
    /// </summary>
    [Fact]
    public void CreateChannel_DefaultPolicyPreservesHevc()
    {
        // Arrange
        var channel = new HDHomeRunChannel
        {
            GuideNumber = "104.1",
            GuideName = "ATSC 3.0",
            VideoCodec = "HEVC",
            AudioCodec = "AC4",
            URL = "http://10.0.60.15:5004/auto/v104.1"
        };

        // Act
        var proxyChannel = HdHomeRunProxyChannel.Create(channel, new Uri("http://lineup.local:8080/"));

        // Assert
        Assert.Equal("HEVC", proxyChannel.VideoCodec);
        Assert.Equal("AC3", proxyChannel.AudioCodec);
    }

    /// <summary>
    /// Verifies that firmware-defined properties survive proxying while Lineup-owned fields cannot be overridden.
    /// </summary>
    [Fact]
    public void CreateChannel_ForwardsUnknownPropertiesWithoutOverridingOwnedFields()
    {
        // Arrange
        var channel = JsonSerializer.Deserialize<HDHomeRunChannel>(
            """
            {
              "GuideNumber": "7.1",
              "GuideName": "TEST",
              "VideoCodec": "MPEG2",
              "AudioCodec": "AC3",
              "SignalStrength": 87,
              "SignalQuality": 93,
              "URL": "http://device/auto/v7.1",
              "FirmwareMetric": { "Value": 42 }
            }
            """)!;
        channel.AdditionalProperties!["url"] = JsonSerializer.SerializeToElement("http://incorrect/");

        // Act
        var proxyChannel = HdHomeRunProxyChannel.Create(channel, new Uri("http://lineup.local:8080/"));
        var json = JsonSerializer.SerializeToElement(proxyChannel);

        // Assert
        Assert.Equal("http://lineup.local:8080/auto/v7.1", json.GetProperty("URL").GetString());
        Assert.Equal(87, json.GetProperty("SignalStrength").GetInt32());
        Assert.Equal(93, json.GetProperty("SignalQuality").GetInt32());
        Assert.Equal(42, json.GetProperty("FirmwareMetric").GetProperty("Value").GetInt32());
        Assert.DoesNotContain(json.EnumerateObject(), property => property.Name == "url");
    }

    /// <summary>
    /// Verifies that optional properties removed by firmware remain absent from the virtual lineup.
    /// </summary>
    [Fact]
    public void CreateChannel_OmitsMissingOptionalProperties()
    {
        // Arrange
        var channel = JsonSerializer.Deserialize<HDHomeRunChannel>(
            """
            {
              "GuideNumber": "7.1",
              "GuideName": "TEST",
              "URL": "http://device/auto/v7.1"
            }
            """)!;

        // Act
        var json = JsonSerializer.SerializeToElement(HdHomeRunProxyChannel.Create(channel, new Uri("http://lineup.local:8080/")));

        // Assert
        Assert.False(json.TryGetProperty("SignalStrength", out _));
        Assert.False(json.TryGetProperty("SignalQuality", out _));
    }

    /// <summary>
    /// Verifies that malformed DeviceIDs do not pass checksum validation.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("123")]
    [InlineData("NOTHEXID")]
    [InlineData("FFFFFFFF")]
    public void IsValidDeviceId_RejectsInvalidValues(string value)
    {

        // Arrange
        // Act
        var isValid = HdHomeRunProxyIdentity.IsValidDeviceId(value);

        // Assert
        Assert.False(isValid);
    }
}
