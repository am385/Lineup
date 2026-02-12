using Lineup.Core.Models;
using Xunit;

namespace Lineup.Core.Tests.Models;

public class HDHomeRunEnrichedChannelTests
{
    [Fact]
    public void HDHomeRunEnrichedChannel_RequiresGuideNumber()
    {
        // Arrange & Act
        var channel = new HDHomeRunEnrichedChannel
        {
            GuideNumber = "5.1"
        };

        // Assert
        Assert.Equal("5.1", channel.GuideNumber);
    }

    [Fact]
    public void HDHomeRunEnrichedChannel_CanSetAllProperties()
    {
        // Arrange & Act
        var channel = new HDHomeRunEnrichedChannel
        {
            GuideNumber = "2.1",
            GuideName = "WFMY-HD",
            Affiliate = "CBS",
            ImageURL = "http://example.com/cbs.png"
        };

        // Assert
        Assert.Equal("2.1", channel.GuideNumber);
        Assert.Equal("WFMY-HD", channel.GuideName);
        Assert.Equal("CBS", channel.Affiliate);
        Assert.Equal("http://example.com/cbs.png", channel.ImageURL);
    }

    [Fact]
    public void HDHomeRunEnrichedChannel_OptionalPropertiesAreNull()
    {
        // Arrange & Act
        var channel = new HDHomeRunEnrichedChannel
        {
            GuideNumber = "7.1"
        };

        // Assert
        Assert.Null(channel.GuideName);
        Assert.Null(channel.Affiliate);
        Assert.Null(channel.ImageURL);
    }

    [Fact]
    public void HDHomeRunEnrichedChannel_SupportsRecordEquality()
    {
        // Arrange
        var channel1 = new HDHomeRunEnrichedChannel
        {
            GuideNumber = "5.1",
            GuideName = "PBS",
            Affiliate = "PBS",
            ImageURL = "http://example.com/pbs.png"
        };

        var channel2 = new HDHomeRunEnrichedChannel
        {
            GuideNumber = "5.1",
            GuideName = "PBS",
            Affiliate = "PBS",
            ImageURL = "http://example.com/pbs.png"
        };

        var channel3 = new HDHomeRunEnrichedChannel
        {
            GuideNumber = "5.1",
            GuideName = "Different Name",
            Affiliate = "PBS",
            ImageURL = "http://example.com/pbs.png"
        };

        // Assert
        Assert.Equal(channel1, channel2);
        Assert.NotEqual(channel1, channel3);
    }

    [Fact]
    public void HDHomeRunEnrichedChannel_SupportsWithExpression()
    {
        // Arrange
        var original = new HDHomeRunEnrichedChannel
        {
            GuideNumber = "5.1",
            GuideName = "PBS",
            Affiliate = "PBS",
            ImageURL = "http://example.com/pbs.png"
        };

        // Act
        var modified = original with { GuideName = "PBS NC" };

        // Assert
        Assert.Equal("PBS", original.GuideName);
        Assert.Equal("PBS NC", modified.GuideName);
        Assert.Equal(original.GuideNumber, modified.GuideNumber);
    }
}
