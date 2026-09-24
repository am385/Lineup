using Lineup.HDHomeRun.Api.Models;
using Lineup.HDHomeRun.Device.Models;
using Xunit;

namespace Lineup.Core.Tests;

/// <summary>
/// Verifies provider-neutral guide projection onto physical channels.
/// </summary>
public sealed class GuideSnapshotProjectorTests
{
    /// <summary>
    /// Verifies unavailable provider channels are removed and missing physical channels receive full-range placeholders.
    /// </summary>
    [Fact]
    public void Project_MixedProviderCoverage_ReturnsPhysicalLineupWithPlaceholder()
    {
        // Arrange
        var projector = new GuideSnapshotProjector();
        var start = new DateTimeOffset(2026, 9, 14, 22, 0, 0, TimeSpan.Zero);
        HDHomeRunChannelEpgSegment[] providerSegments =
        [
            CreateSegment("7.1", "Available", "Available Show", start, start.AddMinutes(30)),
            CreateSegment("99.1", "Unavailable", "Unavailable Show", start, start.AddDays(1))
        ];

        // Act
        var result = projector.Project(providerSegments, [CreateChannel("7.1", "Available"), CreateChannel("9.1", "Missing")]);

        // Assert
        Assert.Equal(["7.1", "9.1"], result.Select(segment => segment.GuideNumber));
        Assert.Equal("Available Show", Assert.Single(result[0].Guide).Title);
        var placeholder = Assert.Single(result[1].Guide);
        Assert.Equal("Not Available", placeholder.Title);
        Assert.Equal(start.ToUnixTimeSeconds(), placeholder.StartTime);
        Assert.Equal(start.AddDays(1).ToUnixTimeSeconds(), placeholder.EndTime);
    }

    private static HDHomeRunChannelEpgSegment CreateSegment(string guideNumber, string guideName, string title, DateTimeOffset start, DateTimeOffset end) => new()
    {
        GuideNumber = guideNumber,
        GuideName = guideName,
        Guide =
        [
            new HDHomeRunProgram
            {
                GuideNumber = guideNumber,
                Title = title,
                StartTime = start.ToUnixTimeSeconds(),
                EndTime = end.ToUnixTimeSeconds()
            }
        ]
    };

    private static HDHomeRunChannel CreateChannel(string guideNumber, string guideName) => new()
    {
        GuideNumber = guideNumber,
        GuideName = guideName,
        URL = $"http://device/auto/v{guideNumber}"
    };
}
