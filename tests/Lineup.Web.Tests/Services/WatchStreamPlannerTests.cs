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
    /// Verifies a previously discovered audio index remains usable when a retry cannot probe source tracks.
    /// </summary>
    [Fact]
    public void SelectTracks_ExplicitAudioAfterProbeFailure_RetainsRequestedIndex()
    {
        // Arrange
        var source = new MediaProbeResult([], null);

        // Act
        var selection = WatchStreamPlanner.SelectTracks(source, 4, null);

        // Assert
        Assert.Equal(4, selection.Audio?.Index);
        Assert.Equal(MediaTrackType.Audio, selection.Audio?.Type);
        Assert.Equal("unknown", selection.Audio?.Codec);
    }

    /// <summary>
    /// Verifies a partial probe cannot bypass explicit audio-index validation.
    /// </summary>
    [Theory]
    [InlineData(99, "not found")]
    [InlineData(0, "not Audio")]
    public void SelectTracks_ExplicitAudioAfterPartialProbe_ThrowsExplicitError(int index, string message)
    {
        // Arrange
        var source = new MediaProbeResult(
        [
            Track(0, MediaTrackType.Video, "h264"),
            Track(6, MediaTrackType.Subtitle, "subrip") with { SubtitlePresentation = SubtitlePresentation.WebVtt }
        ],
        null);

        // Act
        var exception = Assert.Throws<ArgumentException>(() => WatchStreamPlanner.SelectTracks(source, index, null));

        // Assert
        Assert.Contains(message, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies a previously validated subtitle index and presentation remain usable when a retry cannot probe source tracks.
    /// </summary>
    [Theory]
    [InlineData(SubtitlePresentation.WebVtt)]
    [InlineData(SubtitlePresentation.BurnIn)]
    public void SelectTracks_ExplicitSubtitleAfterProbeFailure_RetainsRequestedPlan(SubtitlePresentation presentation)
    {
        // Arrange
        var source = new MediaProbeResult([], null);

        // Act
        var selection = WatchStreamPlanner.SelectTracks(source, null, 6, presentation);
        var arguments = WatchStreamPlanner.CreateArguments(
            new AppSettings(),
            source,
            selection,
            WebPlayerQuality.AppDefault,
            presentation == SubtitlePresentation.WebVtt ? "captions.vtt" : null);

        // Assert
        Assert.Equal(6, selection.Subtitle?.Index);
        Assert.Equal(MediaTrackType.Subtitle, selection.Subtitle?.Type);
        Assert.Equal("unknown", selection.Subtitle?.Codec);
        Assert.Equal(presentation, selection.SubtitlePresentation);
        if (presentation == SubtitlePresentation.WebVtt)
        {
            AssertMapping(arguments, "0:6");
            AssertOption(arguments, "-c:s", "webvtt");
        }
        else
        {
            AssertOption(arguments, "-filter_complex", "[0:v:0][0:6]overlay[v]");
        }
    }

    /// <summary>
    /// Verifies a previously validated embedded-caption selection remains synthetic when a retry probe fails.
    /// </summary>
    [Fact]
    public void SelectTracks_EmbeddedCaptionAfterProbeFailure_RetainsExtractorPlan()
    {
        // Arrange
        var source = new MediaProbeResult([], null);

        // Act
        var selection = WatchStreamPlanner.SelectTracks(source, null, 2, SubtitlePresentation.WebVtt, retryEmbeddedClosedCaptions: true);
        var arguments = WatchStreamPlanner.CreateArguments(new AppSettings(), source, selection, WebPlayerQuality.AppDefault, "captions.vtt");

        // Assert
        Assert.True(selection.Subtitle?.IsEmbeddedClosedCaptions);
        Assert.DoesNotContain("0:2", arguments);
    }

    /// <summary>
    /// Verifies a previously validated embedded-caption selection remains synthetic when a retry probe finds only audio and video tracks.
    /// </summary>
    [Fact]
    public void SelectTracks_EmbeddedCaptionMissingFromRetryProbe_RetainsExtractorPlan()
    {
        // Arrange
        var source = new MediaProbeResult(
        [
            Track(0, MediaTrackType.Video, "mpeg2video"),
            Track(1, MediaTrackType.Audio, "ac3")
        ],
        null);

        // Act
        var selection = WatchStreamPlanner.SelectTracks(source, 1, 2, SubtitlePresentation.WebVtt, retryEmbeddedClosedCaptions: true);
        var arguments = WatchStreamPlanner.CreateArguments(new AppSettings(), source, selection, WebPlayerQuality.AppDefault, "captions.vtt");

        // Assert
        Assert.True(selection.Subtitle?.IsEmbeddedClosedCaptions);
        Assert.Equal(2, selection.Subtitle?.Index);
        Assert.DoesNotContain("0:2", arguments);
    }

    /// <summary>
    /// Verifies retry metadata cannot override subtitle codec validation when probe tracks are available.
    /// </summary>
    [Fact]
    public void SelectTracks_ProbeMetadataExists_UsesProbedSubtitlePresentation()
    {
        // Arrange
        var source = Source;

        // Act
        var selection = WatchStreamPlanner.SelectTracks(source, null, 6, SubtitlePresentation.BurnIn);

        // Assert
        Assert.Equal(SubtitlePresentation.WebVtt, selection.SubtitlePresentation);
        Assert.Equal("subrip", selection.Subtitle?.Codec);
    }

    /// <summary>
    /// Verifies retry presentation metadata cannot make a probed unsupported subtitle codec selectable.
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
            () => WatchStreamPlanner.SelectTracks(source, null, 6, SubtitlePresentation.WebVtt));

        // Assert
        Assert.Contains("unsupported codec", exception.Message, StringComparison.Ordinal);
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
    /// Verifies embedded captions use a dedicated pipe extractor instead of mapping a synthetic stream in the video process.
    /// </summary>
    [Fact]
    public void CreateArguments_EmbeddedCaptions_UsesDedicatedWebVttExtractor()
    {
        // Arrange
        var captions = Track(9, MediaTrackType.Subtitle, "eia_608") with
        {
            IsEmbeddedClosedCaptions = true,
            SubtitlePresentation = SubtitlePresentation.WebVtt
        };
        var source = new MediaProbeResult([Track(0, MediaTrackType.Video, "mpeg2video"), Track(1, MediaTrackType.Audio, "ac3"), captions], null);
        var selection = WatchStreamPlanner.SelectTracks(source, null, 9);

        // Act
        var videoArguments = WatchStreamPlanner.CreateArguments(new AppSettings(), source, selection, WebPlayerQuality.AppDefault, "captions.vtt");
        var captionArguments = WatchStreamPlanner.CreateEmbeddedCaptionArguments("captions.vtt");

        // Assert
        Assert.DoesNotContain("0:9", videoArguments);
        AssertOption(captionArguments, "-f", "lavfi");
        AssertOption(captionArguments, "-i", "movie='pipe\\:0'[out+subcc]");
        AssertOption(captionArguments, "-c:s", "webvtt");
        AssertOption(captionArguments, "-flush_packets", "1");
        Assert.Equal("captions.vtt", captionArguments[^1]);
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

    /// <summary>
    /// Verifies the default browser output remains a compatibility-focused stereo downmix.
    /// </summary>
    [Fact]
    public void CreateArguments_DefaultAudioOutput_DownmixesToStereo()
    {
        // Arrange
        var source = new MediaProbeResult(
        [
            Track(0, MediaTrackType.Video, "h264"),
            Track(2, MediaTrackType.Audio, "ac4") with { Channels = 6 }
        ],
        null);
        var selection = WatchStreamPlanner.SelectTracks(source, null, null);

        // Act
        var arguments = WatchStreamPlanner.CreateArguments(new AppSettings(), source, selection, WebPlayerQuality.AppDefault, null);

        // Assert
        AssertOption(arguments, "-b:a", "128k");
        AssertOption(arguments, "-ac", "2");
        AssertOption(arguments, "-ar", "48000");
    }

    /// <summary>
    /// Verifies 5.1 output limits an eight-channel source to six AAC channels.
    /// </summary>
    [Fact]
    public void CreateArguments_UpTo5Point1AudioOutput_LimitsEightChannels()
    {
        // Arrange
        var source = new MediaProbeResult(
        [
            Track(0, MediaTrackType.Video, "h264"),
            Track(2, MediaTrackType.Audio, "ac4") with { Channels = 8 }
        ],
        null);
        var selection = WatchStreamPlanner.SelectTracks(source, null, null);

        // Act
        var arguments = WatchStreamPlanner.CreateArguments(
            new AppSettings(),
            source,
            selection,
            WebPlayerQuality.AppDefault,
            null,
            WatchAudioOutput.UpTo5Point1);

        // Assert
        AssertOption(arguments, "-c:a", "aac");
        AssertOption(arguments, "-b:a", "384k");
        AssertOption(arguments, "-ac", "6");
        AssertOption(arguments, "-ar", "48000");
    }

    /// <summary>
    /// Verifies 5.1 compatibility output safely assumes stereo when source channels are unknown.
    /// </summary>
    [Fact]
    public void CreateArguments_UpTo5Point1AudioOutput_UnknownChannelsUsesStereo()
    {
        // Arrange
        var source = new MediaProbeResult([], null);
        var selection = WatchStreamPlanner.SelectTracks(source, 2, null);

        // Act
        var arguments = WatchStreamPlanner.CreateArguments(
            new AppSettings(),
            source,
            selection,
            WebPlayerQuality.AppDefault,
            null,
            WatchAudioOutput.UpTo5Point1);

        // Assert
        AssertOption(arguments, "-c:a", "aac");
        AssertOption(arguments, "-b:a", "128k");
        AssertOption(arguments, "-ac", "2");
        AssertOption(arguments, "-ar", "48000");
    }

    /// <summary>
    /// Verifies 7.1 compatibility output safely assumes stereo when retry probe metadata is unavailable.
    /// </summary>
    [Fact]
    public void CreateArguments_UpTo7Point1AudioOutput_UnknownChannelsUsesStereo()
    {
        // Arrange
        var source = new MediaProbeResult([], null);
        var selection = WatchStreamPlanner.SelectTracks(source, 2, null);

        // Act
        var arguments = WatchStreamPlanner.CreateArguments(
            new AppSettings(),
            source,
            selection,
            WebPlayerQuality.AppDefault,
            null,
            WatchAudioOutput.UpTo7Point1);

        // Assert
        AssertOption(arguments, "-c:a", "aac");
        AssertOption(arguments, "-b:a", "128k");
        AssertOption(arguments, "-ac", "2");
        AssertOption(arguments, "-ar", "48000");
    }

    /// <summary>
    /// Verifies 7.1 output retains an eight-channel source, normalizes its layout, and scales its AAC bitrate.
    /// </summary>
    [Fact]
    public void CreateArguments_UpTo7Point1AudioOutput_RetainsEightChannels()
    {
        // Arrange
        var source = new MediaProbeResult(
        [
            Track(0, MediaTrackType.Video, "h264"),
            Track(2, MediaTrackType.Audio, "ac4") with { Channels = 8 }
        ],
        null);
        var selection = WatchStreamPlanner.SelectTracks(source, null, null);

        // Act
        var arguments = WatchStreamPlanner.CreateArguments(
            new AppSettings(),
            source,
            selection,
            WebPlayerQuality.AppDefault,
            null,
            WatchAudioOutput.UpTo7Point1);

        // Assert
        AssertOption(arguments, "-c:a", "aac");
        AssertOption(arguments, "-b:a", "512k");
        AssertOption(arguments, "-ac", "8");
        AssertOption(arguments, "-ar", "48000");
    }

    /// <summary>
    /// Verifies 7.1 output caps an object-based 7.1.4 source at the AAC encoder's channel limit.
    /// </summary>
    [Fact]
    public void CreateArguments_UpTo7Point1AudioOutput_LimitsTwelveChannelsToEight()
    {
        // Arrange
        var source = new MediaProbeResult(
        [
            Track(0, MediaTrackType.Video, "hevc"),
            Track(1, MediaTrackType.Audio, "ac4") with { Channels = 12, SampleRate = 46_034 }
        ],
        null);
        var selection = WatchStreamPlanner.SelectTracks(source, 1, null);

        // Act
        var arguments = WatchStreamPlanner.CreateArguments(
            new AppSettings(),
            source,
            selection,
            WebPlayerQuality.AppDefault,
            null,
            WatchAudioOutput.UpTo7Point1);

        // Assert
        AssertOption(arguments, "-c:a", "aac");
        AssertOption(arguments, "-b:a", "512k");
        AssertOption(arguments, "-ac", "8");
        AssertOption(arguments, "-ar", "48000");
    }

    /// <summary>
    /// Verifies source audio passthrough copies the selected codec without AAC normalization.
    /// </summary>
    [Fact]
    public void CreateArguments_SourceAudioOutput_CopiesSelectedTrack()
    {
        // Arrange
        var source = new MediaProbeResult(
        [
            Track(0, MediaTrackType.Video, "h264"),
            Track(2, MediaTrackType.Audio, "ac4") with { Channels = 12, SampleRate = 46_034 }
        ],
        null);
        var selection = WatchStreamPlanner.SelectTracks(source, 2, null);

        // Act
        var arguments = WatchStreamPlanner.CreateArguments(
            new AppSettings(),
            source,
            selection,
            WebPlayerQuality.AppDefault,
            null,
            WatchAudioOutput.Source);

        // Assert
        AssertOption(arguments, "-c:a", "copy");
        Assert.DoesNotContain("-b:a", arguments);
        Assert.DoesNotContain("-ac", arguments);
        Assert.DoesNotContain("-ar", arguments);
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
