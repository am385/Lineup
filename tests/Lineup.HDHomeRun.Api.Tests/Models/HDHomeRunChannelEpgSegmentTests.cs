using Lineup.HDHomeRun.Api.Models;
using Xunit;

namespace Lineup.HDHomeRun.Api.Tests.Models;

/// <summary>
/// Represents hd home run channel epg segment tests.
/// </summary>
public class HDHomeRunChannelEpgSegmentTests
{
    /// <summary>
    /// Performs the hd home run channel epg segment_can be created_with all properties operation.
    /// </summary>
    [Fact]
    public void HDHomeRunChannelEpgSegment_CanBeCreated_WithAllProperties()
    {
        // Arrange
        // Act
        var segment = new HDHomeRunChannelEpgSegment
        {
            GuideNumber = "5.1",
            GuideName = "WRAL-HD",
            Affiliate = "NBC",
            ImageURL = "http://example.com/logo.png",
            Guide =
            [
                new HDHomeRunProgram { Title = "Show 1" },
                new HDHomeRunProgram { Title = "Show 2" }
            ]
        };

        // Assert
        Assert.Equal("5.1", segment.GuideNumber);
        Assert.Equal("WRAL-HD", segment.GuideName);
        Assert.Equal("NBC", segment.Affiliate);
        Assert.Equal(2, segment.Guide.Count);
    }

    /// <summary>
    /// Performs the hd home run channel epg segment_guide_defaults to empty list operation.
    /// </summary>
    [Fact]
    public void HDHomeRunChannelEpgSegment_Guide_DefaultsToEmptyList()
    {
        // Arrange
        // Act
        var segment = new HDHomeRunChannelEpgSegment
        {
            GuideNumber = "5.1"
        };

        // Assert
        Assert.NotNull(segment.Guide);
        Assert.Empty(segment.Guide);
    }

    /// <summary>
    /// Performs the hd home run channel epg segment_can add programs_to guide operation.
    /// </summary>
    [Fact]
    public void HDHomeRunChannelEpgSegment_CanAddPrograms_ToGuide()
    {
        // Arrange
        var segment = new HDHomeRunChannelEpgSegment
        {
            GuideNumber = "5.1",
            Guide = []
        };

        // Act
        segment.Guide.Add(new HDHomeRunProgram { Title = "New Show" });

        // Assert
        Assert.Single(segment.Guide);
        Assert.Equal("New Show", segment.Guide[0].Title);
    }
}
