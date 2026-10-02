using Lineup.Web.Services;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>
/// Verifies browser-compatible video transcode planning.
/// </summary>
public class WebVideoTranscodePlannerTests
{
    /// <summary>
    /// Verifies that a medium override applies the browser stream bitrate ceiling.
    /// </summary>
    [Fact]
    public void GetMaximumBitRate_MediumOverrideLimitsBitRate()
    {
        // Arrange
        var settings = new AppSettings
        {
            MaximumVideoBitRateMbps = 10
        };

        // Act
        var bitRate = WebVideoTranscodePlanner.GetMaximumBitRate(settings, WebPlayerQuality.Medium);

        // Assert
        Assert.Equal(5_000_000, bitRate);
    }
}
