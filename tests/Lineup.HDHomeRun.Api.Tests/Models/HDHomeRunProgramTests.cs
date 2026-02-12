using Lineup.HDHomeRun.Api.Models;
using Xunit;

namespace Lineup.HDHomeRun.Api.Tests.Models;

public class HDHomeRunProgramTests
{
    [Fact]
    public void HDHomeRunProgram_CanBeCreated_WithAllProperties()
    {
        // Arrange & Act
        var program = new HDHomeRunProgram
        {
            Title = "Test Show",
            EpisodeTitle = "Pilot",
            Synopsis = "A great show about testing",
            StartTime = 1700000000,
            EndTime = 1700003600,
            ImageURL = "http://example.com/image.jpg",
            PosterURL = "http://example.com/poster.jpg",
            EpisodeNumber = "S01E01",
            OriginalAirdate = 1699900000,
            First = 1,
            SeriesID = "EP123456",
            Filter = ["drama", "comedy"],
            GuideNumber = "5.1"
        };

        // Assert
        Assert.Equal("Test Show", program.Title);
        Assert.Equal("Pilot", program.EpisodeTitle);
        Assert.Equal("A great show about testing", program.Synopsis);
        Assert.Equal(1700000000, program.StartTime);
        Assert.Equal(1700003600, program.EndTime);
        Assert.Equal("S01E01", program.EpisodeNumber);
        Assert.Equal(1, program.First);
        Assert.Contains("drama", program.Filter!);
    }

    [Fact]
    public void HDHomeRunProgram_Record_SupportsWithExpression()
    {
        // Arrange
        var original = new HDHomeRunProgram
        {
            Title = "Original Title",
            StartTime = 1000,
            EndTime = 2000
        };

        // Act
        var modified = original with { GuideNumber = "2.1" };

        // Assert
        Assert.Equal("Original Title", modified.Title);
        Assert.Equal("2.1", modified.GuideNumber);
        Assert.Null(original.GuideNumber); // Original unchanged
    }

    [Fact]
    public void HDHomeRunProgram_Filter_CanBeNullOrEmpty()
    {
        // Arrange & Act
        var programWithNull = new HDHomeRunProgram { Title = "Show 1", Filter = null };
        var programWithEmpty = new HDHomeRunProgram { Title = "Show 2", Filter = [] };

        // Assert
        Assert.Null(programWithNull.Filter);
        Assert.Empty(programWithEmpty.Filter);
    }
}
