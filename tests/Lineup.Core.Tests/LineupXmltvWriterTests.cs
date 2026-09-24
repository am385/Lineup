using Lineup.HDHomeRun.Api.Models;
using Lineup.HDHomeRun.Device.Models;
using Xunit;

namespace Lineup.Core.Tests;

/// <summary>
/// Verifies Lineup XMLTV output independently from provider parsing.
/// </summary>
public sealed class LineupXmltvWriterTests
{
    /// <summary>
    /// Verifies normalized guide data round-trips through Lineup XMLTV without losing supported metadata.
    /// </summary>
    [Fact]
    public void Write_NormalizedData_RoundTripsSupportedMetadata()
    {
        // Arrange
        var writer = new LineupXmltvWriter();
        var parser = new SiliconDustXmltvParser();
        var start = new DateTimeOffset(2026, 9, 14, 22, 0, 0, TimeSpan.Zero);
        var segment = new HDHomeRunChannelEpgSegment
        {
            GuideNumber = "7.1",
            GuideName = "Guide Name",
            ImageURL = "https://example.test/channel.png",
            Guide =
            [
                new HDHomeRunProgram
                {
                    Title = "Test Show",
                    EpisodeTitle = "Pilot",
                    Synopsis = "Description",
                    StartTime = start.ToUnixTimeSeconds(),
                    EndTime = start.AddMinutes(30).ToUnixTimeSeconds(),
                    ImageURL = "https://example.test/program.png",
                    EpisodeNumber = "S01E01",
                    OriginalAirdate = start.AddYears(-1).ToUnixTimeSeconds(),
                    First = 1,
                    SeriesID = "series-1",
                    Filter = ["Drama", "Series"]
                }
            ]
        };
        var channel = new HDHomeRunChannel
        {
            GuideNumber = "7.1",
            GuideName = "Tuner Name",
            URL = "http://device/auto/v7.1"
        };

        // Act
        var content = writer.Write([segment], [channel], start, start.AddDays(1));
        var result = Assert.Single(parser.Parse(content));

        // Assert
        var programme = Assert.Single(result.Guide);
        Assert.Equal("Tuner Name", result.GuideName);
        Assert.Equal("https://example.test/channel.png", result.ImageURL);
        Assert.Equal("Test Show", programme.Title);
        Assert.Equal("Pilot", programme.EpisodeTitle);
        Assert.Equal("Description", programme.Synopsis);
        Assert.Equal("S01E01", programme.EpisodeNumber);
        Assert.Equal("series-1", programme.SeriesID);
        Assert.Equal(["Drama", "Series"], programme.Filter);
        Assert.Equal(1, programme.First);
    }
}
