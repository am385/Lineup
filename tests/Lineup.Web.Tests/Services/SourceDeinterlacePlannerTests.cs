using Lineup.Web.Services;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>
/// Verifies shared interlaced-source normalization planning.
/// </summary>
public class SourceDeinterlacePlannerTests
{
    /// <summary>
    /// Verifies source-field-rate normalization emits progressive H.264 metadata and preserves accompanying streams.
    /// </summary>
    [Fact]
    public void CreatePlan_InterlacedSource_UsesBwdifAndProgressiveH264()
    {
        // Arrange
        var source = new MediaProbeResult(
        [
            new MediaTrackMetadata(0, MediaTrackType.Video, "mpeg2video", 8_000_000, 1920, 1080, null, null)
            {
                ScanType = VideoScanType.Interlaced,
                HasClosedCaptions = true
            },
            new MediaTrackMetadata(3, MediaTrackType.Audio, "ac3", 384_000, null, null, 6, 48_000),
            new MediaTrackMetadata(7, MediaTrackType.Subtitle, "dvb_subtitle", null, null, null, null, null)
        ],
        8_500_000);
        var settings = new AppSettings { SourceDeinterlaceMode = DeinterlaceMode.SourceFieldRate };

        // Act
        var arguments = SourceDeinterlacePlanner.CreateArguments(source, settings);
        var effective = SourceDeinterlacePlanner.CreateEffectiveSource(source, settings);

        // Assert
        AssertOption(arguments, "-analyzeduration", "10000000");
        AssertOption(arguments, "-probesize", "10000000");
        AssertOption(arguments, "-filter:v:0", "bwdif=mode=send_field:parity=auto:deint=interlaced");
        AssertOption(arguments, "-c:v:0", "libx264");
        AssertOption(arguments, "-a53cc:v:0", "1");
        AssertMapping(arguments, "0:a?");
        AssertMapping(arguments, "0:7");
        var video = Assert.Single(effective.Tracks, track => track.Type == MediaTrackType.Video);
        Assert.Equal("h264", video.Codec);
        Assert.Equal(VideoScanType.Progressive, video.ScanType);
        Assert.True(video.HasClosedCaptions);
        Assert.Equal(1, Assert.Single(effective.Tracks, track => track.Type == MediaTrackType.Audio).Index);
        Assert.Equal(2, Assert.Single(effective.Tracks, track => track.Type == MediaTrackType.Subtitle).Index);
    }

    /// <summary>
    /// Verifies progressive and unknown sources bypass enabled normalization.
    /// </summary>
    [Theory]
    [InlineData(VideoScanType.Progressive)]
    [InlineData(VideoScanType.Unknown)]
    public void ShouldNormalize_NonInterlacedSource_ReturnsFalse(VideoScanType scanType)
    {
        // Arrange
        var source = new MediaProbeResult(
            [new MediaTrackMetadata(0, MediaTrackType.Video, "h264", null, 1280, 720, null, null) { ScanType = scanType }],
            null);

        // Act
        var normalize = SourceDeinterlacePlanner.ShouldNormalize(source, DeinterlaceMode.SourceFieldRate);

        // Assert
        Assert.False(normalize);
    }

    /// <summary>
    /// Verifies normalized progressive H.264 is copied by the web player without a second deinterlace or encode.
    /// </summary>
    [Fact]
    public void EffectiveSource_CmafPlanner_DoesNotEncodeAgain()
    {
        // Arrange
        var rawSource = new MediaProbeResult(
        [
            new MediaTrackMetadata(0, MediaTrackType.Video, "mpeg2video", 8_000_000, 720, 480, null, null) { ScanType = VideoScanType.Interlaced },
            new MediaTrackMetadata(1, MediaTrackType.Audio, "aac", 128_000, null, null, 2, 48_000)
        ],
        null);
        var settings = new AppSettings
        {
            SourceDeinterlaceMode = DeinterlaceMode.SourceFieldRate,
            WebPlayerDeinterlaceMode = DeinterlaceMode.SourceFieldRate
        };
        var effectiveSource = SourceDeinterlacePlanner.CreateEffectiveSource(rawSource, settings);
        var selection = WebPlayerTrackPlanner.SelectTracks(effectiveSource, 1, null);

        // Act
        var cmafArguments = CmafStreamPlanner.CreateArguments(settings, effectiveSource, selection, new CmafStreamRequest(), "manifest.mpd");

        // Assert
        AssertOption(cmafArguments, "-c:v:0", "copy");
        Assert.DoesNotContain("-filter:v:0", cmafArguments);
    }

    private static void AssertOption(IReadOnlyList<string> arguments, string option, string value)
    {
        var index = Assert.Single(arguments.Select((argument, index) => (argument, index)), item => item.argument == option).index;
        Assert.Equal(value, arguments[index + 1]);
    }

    private static void AssertMapping(IReadOnlyList<string> arguments, string mapping) =>
        Assert.Contains(arguments.Select((argument, index) => (argument, index)), item => item.argument == "-map" && arguments[item.index + 1] == mapping);
}
