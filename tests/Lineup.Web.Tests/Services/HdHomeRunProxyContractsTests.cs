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
            URL = "http://10.0.60.15:5004/auto/v104.1"
        };

        // Act
        var proxyChannel = HdHomeRunProxyChannel.Create(channel, new Uri("http://lineup.local:8080/"), VirtualTunerVideoMode.ConvertHevcToH264);

        // Assert
        Assert.Equal("http://lineup.local:8080/auto/v104.1", proxyChannel.Url);
        Assert.Equal("H264", proxyChannel.VideoCodec);
        Assert.Equal("AC3", proxyChannel.AudioCodec);
        Assert.Equal("favorite", proxyChannel.Tags);
        Assert.Equal(1, proxyChannel.Hd);
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
