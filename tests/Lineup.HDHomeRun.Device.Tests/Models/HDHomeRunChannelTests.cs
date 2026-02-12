using Lineup.HDHomeRun.Device.Models;
using Xunit;

namespace Lineup.HDHomeRun.Device.Tests.Models;

public class HDHomeRunChannelTests
{
    [Fact]
    public void HDHomeRunChannel_CanBeCreated_WithRequiredProperties()
    {
        // Arrange & Act
        var channel = new HDHomeRunChannel
        {
            GuideNumber = "5.1",
            GuideName = "Test Channel",
            URL = "http://device/auto/v5.1"
        };

        // Assert
        Assert.Equal("5.1", channel.GuideNumber);
        Assert.Equal("Test Channel", channel.GuideName);
        Assert.Equal("http://device/auto/v5.1", channel.URL);
    }

    [Fact]
    public void HDHomeRunChannel_OptionalProperties_HaveDefaults()
    {
        // Arrange & Act
        var channel = new HDHomeRunChannel
        {
            GuideNumber = "5.1",
            GuideName = "Test Channel",
            URL = "http://device/auto/v5.1"
        };

        // Assert - optional properties have default values
        Assert.False(channel.HD);
        Assert.False(channel.Favorite);
        Assert.False(channel.DRM);
    }

    [Theory]
    [InlineData("2.1")]
    [InlineData("5.1")]
    [InlineData("11.1")]
    [InlineData("100.3")]
    public void HDHomeRunChannel_AcceptsVarious_GuideNumberFormats(string guideNumber)
    {
        // Arrange & Act
        var channel = new HDHomeRunChannel
        {
            GuideNumber = guideNumber,
            GuideName = "Test Channel",
            URL = $"http://device/auto/v{guideNumber}"
        };

        // Assert
        Assert.Equal(guideNumber, channel.GuideNumber);
    }
}
