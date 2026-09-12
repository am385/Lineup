using Lineup.Web.Services;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>
/// Verifies browser-compatible video transcode planning.
/// </summary>
public class WebVideoTranscodePlannerTests
{
    /// <summary>
    /// Verifies that configured quality controls produce efficient HD H.264 arguments.
    /// </summary>
    [Fact]
    public void CreateArguments_UsesConfiguredQualityPresetAndBitRate()
    {
        // Arrange
        var settings = new AppSettings
        {
            WebVideoPreset = WebVideoPreset.Faster,
            WebVideoQuality = 19,
            MaximumVideoBitRateMbps = 12
        };

        // Act
        var arguments = WebVideoTranscodePlanner.CreateArguments(settings);

        // Assert
        Assert.Contains("-preset faster", arguments);
        Assert.Contains("-crf 19", arguments);
        Assert.Contains("-maxrate 12M", arguments);
        Assert.Contains("-bufsize 24M", arguments);
        Assert.Contains("-profile:v high -level 4.2", arguments);
        Assert.Contains("expr:gte(t,n_forced*2)", arguments);
        Assert.DoesNotContain("-preset ultrafast", arguments);
        Assert.DoesNotContain("-b:v 2500k", arguments);
    }

    /// <summary>
    /// Verifies that H.264 sources are copied without generation loss.
    /// </summary>
    [Fact]
    public void CreateArguments_H264SourceUsesVideoCopy()
    {
        // Arrange
        var settings = new AppSettings();

        // Act
        var arguments = WebVideoTranscodePlanner.CreateArguments(settings, "h264");

        // Assert
        Assert.Equal("-c:v copy", arguments);
    }

    /// <summary>
    /// Verifies that a medium override forces a bandwidth-limited 720p transcode.
    /// </summary>
    [Fact]
    public void CreateArguments_MediumOverrideLimitsResolutionAndBitRate()
    {
        // Arrange
        var settings = new AppSettings
        {
            MaximumVideoBitRateMbps = 10
        };

        // Act
        var arguments = WebVideoTranscodePlanner.CreateArguments(settings, "h264", WebPlayerQuality.Medium);
        var bitRate = WebVideoTranscodePlanner.GetMaximumBitRate(settings, WebPlayerQuality.Medium);

        // Assert
        Assert.Contains("-c:v libx264", arguments);
        Assert.Contains("-crf 23", arguments);
        Assert.Contains("-maxrate 5M", arguments);
        Assert.Contains("min(720,ih)", arguments);
        Assert.Equal(5_000_000, bitRate);
    }
}
