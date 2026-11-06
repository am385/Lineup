using Lineup.Core.Common.Streaming;
using Lineup.Core.Extensibility.Streaming;
using Xunit;

namespace Lineup.Plugin.WebPlayer.Tests;

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
        var profile = new VirtualTranscodeProfile
        {
            MaximumVideoBitRateMbps = 10
        };

        // Act
        var bitRate = WebVideoTranscodePlanner.GetMaximumBitRate(profile, WebPlayerQuality.Medium);

        // Assert
        Assert.Equal(5_000_000, bitRate);
    }
}
