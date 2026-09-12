using Lineup.Web.Services;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>
/// Verifies validated Watch audio and subtitle output planning.
/// </summary>
public class WatchStreamPlannerTests
{
    private static readonly MediaProbeResult Source = new(
    [
        Track(0, MediaTrackType.Video, "h264"),
        Track(2, MediaTrackType.Audio, "ac3") with { Language = "eng", IsDefault = true },
        Track(4, MediaTrackType.Audio, "ac3") with { Language = "spa" },
        Track(6, MediaTrackType.Subtitle, "subrip") with { SubtitlePresentation = SubtitlePresentation.WebVtt },
        Track(8, MediaTrackType.Subtitle, "dvb_subtitle") with { SubtitlePresentation = SubtitlePresentation.BurnIn }
    ],
    null);

    /// <summary>
    /// Verifies default and explicit audio use absolute source indexes while subtitles default off.
    /// </summary>
    [Fact]
    public void SelectTracks_DefaultAndExplicitAudio_UseValidatedAbsoluteIndexes()
    {
        // Arrange
        var defaultSelection = WatchStreamPlanner.SelectTracks(Source, null, null);

        // Act
        var explicitSelection = WatchStreamPlanner.SelectTracks(Source, 4, null);

        // Assert
        Assert.Equal(2, defaultSelection.Audio?.Index);
        Assert.Null(defaultSelection.Subtitle);
        Assert.Equal(4, explicitSelection.Audio?.Index);
    }

    /// <summary>
    /// Verifies absent and wrong-media indexes are rejected with explicit errors.
    /// </summary>
    [Theory]
    [InlineData(99, "not found")]
    [InlineData(0, "not Audio")]
    public void SelectTracks_InvalidAudioIndex_ThrowsExplicitError(int index, string message)
    {
        // Arrange
        var source = Source;

        // Act
        var exception = Assert.Throws<ArgumentException>(() => WatchStreamPlanner.SelectTracks(source, index, null));

        // Assert
        Assert.Contains(message, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies text subtitles add a synchronized WebVTT output without video re-encoding.
    /// </summary>
    [Fact]
    public void CreateArguments_TextSubtitle_MapsSidecarAndCopiesH264Video()
    {
        // Arrange
        var selection = WatchStreamPlanner.SelectTracks(Source, 4, 6);

        // Act
        var arguments = WatchStreamPlanner.CreateArguments(new AppSettings(), Source, selection, WebPlayerQuality.AppDefault, "captions.vtt");

        // Assert
        AssertOption(arguments, "-c:v", "copy");
        AssertMapping(arguments, "0:4");
        AssertMapping(arguments, "0:6");
        AssertOption(arguments, "-c:s", "webvtt");
        Assert.Equal("captions.vtt", arguments[^1]);
    }

    /// <summary>
    /// Verifies bitmap subtitle selection overlays the requested stream and forces H.264.
    /// </summary>
    [Fact]
    public void CreateArguments_BitmapSubtitle_ForcesOverlayAndVideoEncode()
    {
        // Arrange
        var selection = WatchStreamPlanner.SelectTracks(Source, 2, 8);

        // Act
        var arguments = WatchStreamPlanner.CreateArguments(new AppSettings(), Source, selection, WebPlayerQuality.AppDefault, null);

        // Assert
        AssertOption(arguments, "-filter_complex", "[0:v:0][0:8]overlay[v]");
        AssertOption(arguments, "-c:v", "libx264");
        Assert.DoesNotContain("-c:s", arguments);
    }

    /// <summary>
    /// Verifies a failed best-effort probe retains the historical optional first-audio mapping.
    /// </summary>
    [Fact]
    public void CreateArguments_UnknownSource_MapsOptionalFirstAudio()
    {
        // Arrange
        var source = new MediaProbeResult([], null);
        var selection = WatchStreamPlanner.SelectTracks(source, null, null);

        // Act
        var arguments = WatchStreamPlanner.CreateArguments(new AppSettings(), source, selection, WebPlayerQuality.AppDefault, null);

        // Assert
        Assert.True(selection.UseDefaultAudioFallback);
        AssertOption(arguments, "-loglevel", "info");
        AssertMapping(arguments, "0:a:0?");
    }

    private static MediaTrackMetadata Track(int index, MediaTrackType type, string codec) =>
        new(index, type, codec, null, null, null, type == MediaTrackType.Audio ? 2 : null, null);

    private static void AssertOption(IReadOnlyList<string> arguments, string option, string value)
    {
        var index = arguments.ToList().IndexOf(option);
        Assert.True(index >= 0);
        Assert.Equal(value, arguments[index + 1]);
    }

    private static void AssertMapping(IReadOnlyList<string> arguments, string mapping)
    {
        var indexes = arguments.Select((value, index) => (value, index)).Where(item => item.value == "-map");
        Assert.Contains(indexes, item => item.index + 1 < arguments.Count && arguments[item.index + 1] == mapping);
    }
}
