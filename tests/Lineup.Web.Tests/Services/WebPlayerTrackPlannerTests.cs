using Lineup.Web.Services;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>
/// Verifies web-player audio and subtitle track validation.
/// </summary>
public class WebPlayerTrackPlannerTests
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
        var defaultSelection = WebPlayerTrackPlanner.SelectTracks(Source, null, null);

        // Act
        var explicitSelection = WebPlayerTrackPlanner.SelectTracks(Source, 4, null);

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
        var exception = Assert.Throws<ArgumentException>(() => WebPlayerTrackPlanner.SelectTracks(source, index, null));

        // Assert
        Assert.Contains(message, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies a previously discovered audio index remains usable when a retry cannot probe source tracks.
    /// </summary>
    [Fact]
    public void SelectTracks_ExplicitAudioAfterProbeFailure_RetainsRequestedIndex()
    {
        // Arrange
        var source = new MediaProbeResult([], null);

        // Act
        var selection = WebPlayerTrackPlanner.SelectTracks(source, 4, null);

        // Assert
        Assert.Equal(4, selection.Audio?.Index);
        Assert.Equal(MediaTrackType.Audio, selection.Audio?.Type);
        Assert.Equal("unknown", selection.Audio?.Codec);
    }

    /// <summary>
    /// Verifies retry metadata restores embedded WebVTT captions when probing temporarily returns no tracks.
    /// </summary>
    [Fact]
    public void SelectTracks_EmbeddedCaptionAfterProbeFailure_RetainsRequestedPlan()
    {
        // Arrange
        var source = new MediaProbeResult([], null);

        // Act
        var selection = WebPlayerTrackPlanner.SelectTracks(source, null, 9, SubtitlePresentation.WebVtt, retryEmbeddedClosedCaptions: true);

        // Assert
        Assert.Equal(9, selection.Subtitle?.Index);
        Assert.True(selection.Subtitle?.IsEmbeddedClosedCaptions);
        Assert.Equal(SubtitlePresentation.WebVtt, selection.SubtitlePresentation);
    }

    /// <summary>
    /// Verifies unsupported probed subtitle codecs remain rejected even when retry metadata is supplied.
    /// </summary>
    [Fact]
    public void SelectTracks_UnsupportedProbedSubtitle_RejectsRetryPresentation()
    {
        // Arrange
        var source = new MediaProbeResult(
        [
            Track(0, MediaTrackType.Video, "h264"),
            Track(6, MediaTrackType.Subtitle, "unknown")
        ],
        null);

        // Act
        var exception = Assert.Throws<ArgumentException>(
            () => WebPlayerTrackPlanner.SelectTracks(source, null, 6, SubtitlePresentation.WebVtt));

        // Assert
        Assert.Contains("unsupported codec", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies embedded captions use the dedicated WebVTT pipe extractor.
    /// </summary>
    [Fact]
    public void CreateEmbeddedCaptionArguments_UsesDedicatedWebVttExtractor()
    {
        // Arrange
        const string path = "captions.vtt";

        // Act
        var arguments = WebPlayerTrackPlanner.CreateEmbeddedCaptionArguments(path);

        // Assert
        Assert.Contains(arguments.Select((argument, index) => (argument, index)), item => item.argument == "-f" && arguments[item.index + 1] == "lavfi");
        AssertOption(arguments, "-i", "movie='pipe\\:0'[out+subcc]");
        AssertOption(arguments, "-c:s", "webvtt");
        AssertOption(arguments, "-flush_packets", "1");
        Assert.Equal(path, arguments[^1]);
    }

    private static MediaTrackMetadata Track(int index, MediaTrackType type, string codec) =>
        new(index, type, codec, null, null, null, null, null);

    private static void AssertOption(IReadOnlyList<string> arguments, string option, string expectedValue)
    {
        var index = arguments.ToList().LastIndexOf(option);
        Assert.True(index >= 0);
        Assert.Equal(expectedValue, arguments[index + 1]);
    }
}
