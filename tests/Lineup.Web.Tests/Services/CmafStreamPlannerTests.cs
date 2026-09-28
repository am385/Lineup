using Lineup.Web.Services;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>
/// Verifies shared CMAF DASH/HLS planning.
/// </summary>
public class CmafStreamPlannerTests
{
    /// <summary>
    /// Verifies one DASH muxer emits both protocols over shared two-second fragmented MP4 media.
    /// </summary>
    [Fact]
    public void CreateArguments_DefaultPresentation_UsesSharedDashAndHlsFragments()
    {
        // Arrange
        var source = Source("aac");
        var selection = WatchStreamPlanner.SelectTracks(source, 1, null);

        // Act
        var arguments = CmafStreamPlanner.CreateArguments(new AppSettings(), source, selection, new CmafStreamRequest(), "manifest.mpd");

        // Assert
        AssertOption(arguments, "-f", "dash");
        AssertOption(arguments, "-hls_playlist", "1");
        AssertOption(arguments, "-seg_duration", "2");
        AssertOption(arguments, "-frag_duration", "2");
        AssertOption(arguments, "-window_size", "10");
        AssertOption(arguments, "-init_seg_name", "init-$RepresentationID$.mp4");
        AssertOption(arguments, "-media_seg_name", "chunk-$RepresentationID$-$Number%05d$.m4s");
        Assert.Equal("manifest.mpd", arguments[^1]);
    }

    /// <summary>
    /// Verifies AC-4 is explicitly eligible for source copy without fallback normalization.
    /// </summary>
    [Fact]
    public void CreateAudioPlan_Ac4Source_CopiesSource()
    {
        // Arrange
        var audio = Source("ac4").Tracks[1] with { Channels = 12, SampleRate = 46_034 };

        // Act
        var plan = CmafStreamPlanner.CreateAudioPlan(audio, CmafPreferredAudio.Source, CmafFallbackAudio.AacStereo);

        // Assert
        Assert.True(plan.CopySource);
        Assert.Equal("ac4", plan.Codec);
        Assert.Equal(12, plan.Channels);
        Assert.Equal(46_034, plan.SampleRate);
    }

    /// <summary>
    /// Verifies eligible source audio is copied without continuously encoding an unused fallback rendition.
    /// </summary>
    [Fact]
    public void CreateAudioRenditions_Ac4Source_ProducesOnlySource()
    {
        // Arrange
        var audio = Source("ac4").Tracks[1] with { Channels = 6, SampleRate = 48_000 };

        // Act
        var renditions = CmafStreamPlanner.CreateAudioRenditions(audio, CmafPreferredAudio.Source, CmafFallbackAudio.Ac3);

        // Assert
        var source = Assert.Single(renditions);
        Assert.True(source.CopySource);
        Assert.Equal("ac4", source.Codec);
    }

    /// <summary>
    /// Verifies an explicit fallback preference omits an otherwise eligible source-copy rendition.
    /// </summary>
    [Fact]
    public void CreateAudioRenditions_FallbackPreference_ProducesOnlyConfiguredFallback()
    {
        // Arrange
        var audio = Source("ac4").Tracks[1];

        // Act
        var rendition = Assert.Single(CmafStreamPlanner.CreateAudioRenditions(audio, CmafPreferredAudio.Fallback, CmafFallbackAudio.Eac3));

        // Assert
        Assert.False(rendition.CopySource);
        Assert.Equal("eac3", rendition.Codec);
    }

    /// <summary>
    /// Verifies eligible HEVC source video is copied without paying for an unused H.264 encode.
    /// </summary>
    [Fact]
    public void CreateArguments_HevcSource_MapsOnlySource()
    {
        // Arrange
        var source = Source("aac") with
        {
            Tracks =
            [
                Track(0, MediaTrackType.Video, "hevc") with { BitRate = 12_000_000, Profile = "Main 10", Level = 123 },
                Track(1, MediaTrackType.Audio, "aac") with { Channels = 2 }
            ]
        };
        var selection = WatchStreamPlanner.SelectTracks(source, 1, null);

        // Act
        var arguments = CmafStreamPlanner.CreateArguments(new AppSettings(), source, selection, new CmafStreamRequest(), "manifest.mpd");

        // Assert
        Assert.Single(arguments.Select((argument, index) => (argument, index)), item => item.argument == "-map" && arguments[item.index + 1] == "0:v:0?");
        AssertOption(arguments, "-c:v:0", "copy");
        AssertOption(arguments, "-tag:v:0", "hvc1");
        AssertOption(arguments, "-metadata:s:v:0", "title=Source");
        Assert.DoesNotContain("-c:v:1", arguments);
        Assert.DoesNotContain("title=Fallback H.264", arguments);
    }

    /// <summary>
    /// Verifies explicit fallback video omits the otherwise eligible HEVC source rendition.
    /// </summary>
    [Fact]
    public void CreateVideoRenditions_FallbackPreference_ProducesOnlyH264()
    {
        // Arrange
        var video = Track(0, MediaTrackType.Video, "hevc") with { BitRate = 12_000_000, Profile = "Main 10", Level = 123 };
        var request = new CmafStreamRequest { PreferredVideo = CmafPreferredVideo.Fallback };

        // Act
        var rendition = Assert.Single(CmafStreamPlanner.CreateVideoRenditions(video, request, burnIn: false));

        // Assert
        Assert.False(rendition.CopySource);
        Assert.Equal("h264", rendition.Codec);
        Assert.Equal("Fallback H.264", rendition.Title);
    }

    /// <summary>
    /// Verifies HEVC source metadata produces the RFC 6381 codec string required by browser manifests.
    /// </summary>
    [Theory]
    [InlineData("Main", 120, "hvc1.1.6.L120")]
    [InlineData("Main 10", 123, "hvc1.2.4.L123")]
    [InlineData("Main 10", 153, "hvc1.2.4.L153")]
    public void CreateHevcCodecString_KnownProfileAndLevel_ReturnsBrowserCodec(string profile, int level, string expected)
    {
        // Arrange
        var video = Track(0, MediaTrackType.Video, "hevc") with { Profile = profile, Level = level };

        // Act
        var codec = CmafStreamPlanner.CreateHevcCodecString(video);

        // Assert
        Assert.Equal(expected, codec);
    }

    /// <summary>
    /// Verifies omitted FFmpeg HEVC codec values are supplied in both DASH and HLS manifests.
    /// </summary>
    [Fact]
    public void RewriteManifestCodecs_EmptyHevcValue_InsertsBrowserCodec()
    {
        // Arrange
        const string manifest = "<Representation codecs=\"\" />\n#EXT-X-STREAM-INF:CODECS=\",mp4a.40.2\"";

        // Act
        var rewritten = CmafStreamPlanner.RewriteManifestCodecs(manifest, "hvc1.2.4.L123");

        // Assert
        Assert.Contains("codecs=\"hvc1.2.4.L123\"", rewritten, StringComparison.Ordinal);
        Assert.Contains("CODECS=\"hvc1.2.4.L123,mp4a.40.2\"", rewritten, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies the sole source label is inserted into DASH and HLS manifests.
    /// </summary>
    [Fact]
    public void RewriteManifestAudioLabels_FfmpegDefaults_InsertsRenditionMetadata()
    {
        // Arrange
        const string dash = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011"><Period>
            <AdaptationSet contentType="video"><Representation id="0" /></AdaptationSet>
            <AdaptationSet contentType="audio" lang="eng"><Representation id="1" /></AdaptationSet>
            </Period></MPD>
            """;
        const string hls = """
            #EXTM3U
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="group_A1",NAME="audio_1",DEFAULT=YES,URI="media_1.m3u8"
            """;
        var source = Source("ac3") with
        {
            Tracks = [Track(0, MediaTrackType.Video, "h264"), Track(1, MediaTrackType.Audio, "ac3") with { Language = "eng" }]
        };
        var renditions = CmafStreamPlanner.CreatePresentationAudioRenditions(
            source,
            source.Tracks[1],
            CmafPreferredAudio.Source,
            CmafFallbackAudio.AacStereo);

        // Act
        var rewrittenDash = CmafStreamPlanner.RewriteManifestAudioLabels(dash, renditions);
        var rewrittenHls = CmafStreamPlanner.RewriteManifestAudioLabels(hls, renditions);

        // Assert
        Assert.Contains("<Label>Audio #1 · eng · Source</Label>", rewrittenDash, StringComparison.Ordinal);
        Assert.Contains("NAME=\"Audio #1 · eng · Source\",LANGUAGE=\"eng\"", rewrittenHls, StringComparison.Ordinal);
        Assert.DoesNotContain("Fallback", rewrittenDash, StringComparison.Ordinal);
        Assert.DoesNotContain("Fallback", rewrittenHls, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies an AC-4 MP4 tag failure requests one fallback-only startup retry.
    /// </summary>
    [Fact]
    public void ShouldRetryWithFallback_Ac4ContainerTagFailure_ReturnsTrue()
    {
        // Arrange
        var audio = Source("ac4").Tracks[1];
        var diagnostics = new[] { "[mp4] Could not find tag for codec ac4 in stream #0, codec not currently supported in container" };

        // Act
        var retry = CmafStreamPlanner.ShouldRetryWithFallback(new CmafStreamRequest { PreferredAudio = CmafPreferredAudio.Source }, audio, diagnostics);

        // Assert
        Assert.True(retry);
    }

    /// <summary>
    /// Verifies unrelated conversion failures do not silently remove the requested source rendition.
    /// </summary>
    [Fact]
    public void ShouldRetryWithFallback_UnrelatedFailure_ReturnsFalse()
    {
        // Arrange
        var audio = Source("ac4").Tracks[1];

        // Act
        var retry = CmafStreamPlanner.ShouldRetryWithFallback(
            new CmafStreamRequest { PreferredAudio = CmafPreferredAudio.Source },
            audio,
            ["Error while decoding video stream"]);

        // Assert
        Assert.False(retry);
    }

    /// <summary>
    /// Verifies FFmpeg maps a compatible Dolby source once without an unused fallback encoder.
    /// </summary>
    [Fact]
    public void CreateArguments_Ac4Source_MapsOnlySourceAudio()
    {
        // Arrange
        var source = Source("ac4");
        var selection = WatchStreamPlanner.SelectTracks(source, 1, null);

        // Act
        var arguments = CmafStreamPlanner.CreateArguments(new AppSettings(), source, selection, new CmafStreamRequest(), "manifest.mpd");

        // Assert
        Assert.Equal(1, arguments.Select((argument, index) => (argument, index)).Count(item => item.argument == "-map" && arguments[item.index + 1] == "0:1"));
        AssertOption(arguments, "-c:a:0", "copy");
        AssertOption(arguments, "-metadata:s:a:0", "title=Audio #1 · Source");
        Assert.DoesNotContain("-c:a:1", arguments);
    }

    /// <summary>
    /// Verifies every compatible source audio track is mapped exactly once.
    /// </summary>
    [Fact]
    public void CreateArguments_MultipleAudioTracks_MapsEachSourceOnce()
    {
        // Arrange
        var source = Source("ac3") with
        {
            Tracks =
            [
                Track(0, MediaTrackType.Video, "h264"),
                Track(1, MediaTrackType.Audio, "ac3") with { Channels = 6, Language = "eng" },
                Track(2, MediaTrackType.Audio, "ac3") with { Channels = 2, Language = "spa" }
            ]
        };
        var selection = WatchStreamPlanner.SelectTracks(source, 1, null);

        // Act
        var arguments = CmafStreamPlanner.CreateArguments(new AppSettings(), source, selection, new CmafStreamRequest(), "manifest.mpd");

        // Assert
        Assert.Equal(1, arguments.Select((argument, index) => (argument, index)).Count(item => item.argument == "-map" && arguments[item.index + 1] == "0:1"));
        Assert.Equal(1, arguments.Select((argument, index) => (argument, index)).Count(item => item.argument == "-map" && arguments[item.index + 1] == "0:2"));
        AssertOptionValue(arguments, "-metadata:s:a:0", "language=eng");
        AssertOptionValue(arguments, "-metadata:s:a:1", "language=spa");
        Assert.Contains("title=Audio #1 · eng · Source", arguments);
        Assert.Contains("title=Audio #2 · spa · Source", arguments);
        Assert.DoesNotContain(arguments, argument => argument.Contains("Fallback", StringComparison.Ordinal));
        AssertOption(arguments, "-adaptation_sets", "id=0,streams=0 id=1,streams=1 id=2,streams=2");
    }

    /// <summary>
    /// Verifies codec-based fallback choices produce the expected FFmpeg encoder, bitrate, channels, and title.
    /// </summary>
    [Theory]
    [InlineData(CmafFallbackAudio.Eac3, "eac3", "640k", "6", "title=Fallback EAC3")]
    [InlineData(CmafFallbackAudio.Ac3, "ac3", "448k", "6", "title=Fallback AC3")]
    public void CreateArguments_DolbyFallback_UsesSelectedProfile(
        CmafFallbackAudio fallback,
        string codec,
        string bitRate,
        string channels,
        string title)
    {
        // Arrange
        var source = Source("ac4") with
        {
            Tracks = [Track(0, MediaTrackType.Video, "h264"), Track(1, MediaTrackType.Audio, "ac4") with { Channels = 8 }]
        };
        var selection = WatchStreamPlanner.SelectTracks(source, 1, null);
        var request = new CmafStreamRequest { PreferredAudio = CmafPreferredAudio.Fallback, FallbackAudio = fallback };

        // Act
        var arguments = CmafStreamPlanner.CreateArguments(new AppSettings(), source, selection, request, "manifest.mpd");

        // Assert
        AssertOption(arguments, "-c:a:0", codec);
        AssertOption(arguments, "-b:a:0", bitRate);
        AssertOption(arguments, "-ac:a:0", channels);
        AssertOption(arguments, "-metadata:s:a:0", title.Replace("title=", "title=Audio #1 · ", StringComparison.Ordinal));
    }

    /// <summary>
    /// Verifies fallback profiles apply their codec-specific channel limits and bitrates.
    /// </summary>
    [Theory]
    [InlineData(CmafFallbackAudio.Eac3, "eac3", 6, 640_000)]
    [InlineData(CmafFallbackAudio.Ac3, "ac3", 6, 448_000)]
    [InlineData(CmafFallbackAudio.AacUpTo7Point1, "aac", 8, 512_000)]
    [InlineData(CmafFallbackAudio.AacUpTo5Point1, "aac", 6, 384_000)]
    [InlineData(CmafFallbackAudio.AacStereo, "aac", 2, 128_000)]
    public void CreateAudioPlan_IneligibleSource_UsesConfiguredFallback(CmafFallbackAudio fallback, string codec, int channels, int bitRate)
    {
        // Arrange
        var audio = Source("mp2").Tracks[1] with { Channels = 8 };

        // Act
        var plan = CmafStreamPlanner.CreateAudioPlan(audio, CmafPreferredAudio.Source, fallback);

        // Assert
        Assert.False(plan.CopySource);
        Assert.Equal(codec, plan.Codec);
        Assert.Equal(channels, plan.Channels);
        Assert.Equal(bitRate, plan.BitRate);
        Assert.Equal(48_000, plan.SampleRate);
    }

    /// <summary>
    /// Verifies the legacy AAC fallback request maps to the corresponding new profile.
    /// </summary>
    [Fact]
    public void ResolveFallbackAudio_LegacyMultichannelAac_MapsToNewProfile()
    {
        // Arrange
        var request = new CmafStreamRequest { AacFallback = WatchAudioOutput.UpTo7Point1 };

        // Act
        var fallback = CmafStreamPlanner.ResolveFallbackAudio(request);

        // Assert
        Assert.Equal(CmafFallbackAudio.AacUpTo7Point1, fallback);
    }

    /// <summary>
    /// Verifies the legacy fallback contract still rejects recursive source passthrough.
    /// </summary>
    [Fact]
    public void ResolveFallbackAudio_LegacySource_ThrowsExplicitError()
    {
        // Arrange
        var request = new CmafStreamRequest { AacFallback = WatchAudioOutput.Source };

        // Act
        var exception = Assert.Throws<ArgumentException>(() => CmafStreamPlanner.ResolveFallbackAudio(request));

        // Assert
        Assert.Contains("cannot use source passthrough", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies CMAF selection preserves Watch embedded-caption extraction metadata.
    /// </summary>
    [Fact]
    public void SelectTracks_EmbeddedCaptions_PreservesExtractorSelection()
    {
        // Arrange
        var source = new MediaProbeResult([], null);
        var request = new CmafStreamRequest
        {
            SubtitleTrack = 2,
            SubtitlePresentation = SubtitlePresentation.WebVtt,
            EmbeddedCaptions = true
        };

        // Act
        var selection = CmafStreamPlanner.SelectTracks(source, request);

        // Assert
        Assert.True(selection.Subtitle?.IsEmbeddedClosedCaptions);
        Assert.Equal(SubtitlePresentation.WebVtt, selection.Subtitle?.SubtitlePresentation);
    }

    /// <summary>
    /// Verifies embedded captions are registered before standalone text subtitles in the player-facing track list.
    /// </summary>
    [Fact]
    public void GetSelectableSubtitles_EmbeddedCaptions_AreFirst()
    {
        // Arrange
        var source = new MediaProbeResult(
        [
            Track(0, MediaTrackType.Video, "h264"),
            Track(2, MediaTrackType.Subtitle, "subrip") with { SubtitlePresentation = SubtitlePresentation.WebVtt },
            Track(5, MediaTrackType.Subtitle, "eia_608") with
            {
                SubtitlePresentation = SubtitlePresentation.WebVtt,
                IsEmbeddedClosedCaptions = true
            },
            Track(3, MediaTrackType.Subtitle, "ass") with { SubtitlePresentation = SubtitlePresentation.WebVtt }
        ], null);

        // Act
        var subtitles = CmafStreamPlanner.GetSelectableSubtitles(source);

        // Assert
        Assert.Collection(
            subtitles,
            subtitle => Assert.Equal(5, subtitle.Index),
            subtitle => Assert.Equal(2, subtitle.Index),
            subtitle => Assert.Equal(3, subtitle.Index));
    }

    /// <summary>
    /// Verifies every text subtitle produces its own contained WebVTT artifact.
    /// </summary>
    [Fact]
    public void CreateArguments_TextSubtitles_ProducesAllWebVttSidecars()
    {
        // Arrange
        var source = new MediaProbeResult(
        [
            Track(0, MediaTrackType.Video, "h264"),
            Track(1, MediaTrackType.Audio, "aac"),
            Track(2, MediaTrackType.Subtitle, "subrip") with { SubtitlePresentation = SubtitlePresentation.WebVtt },
            Track(3, MediaTrackType.Subtitle, "ass") with { SubtitlePresentation = SubtitlePresentation.WebVtt }
        ], null);
        var selection = WatchStreamPlanner.SelectTracks(source, 1, null);

        // Act
        var arguments = CmafStreamPlanner.CreateArguments(
            new AppSettings(),
            source,
            selection,
            new CmafStreamRequest(),
            "manifest.mpd",
            new Dictionary<int, string> { [2] = "captions-2.vtt", [3] = "captions-3.vtt" });

        // Assert
        Assert.Equal(2, arguments.Count(argument => argument.StartsWith("captions-", StringComparison.Ordinal)));
        Assert.Contains("captions-2.vtt", arguments);
        Assert.Equal("captions-3.vtt", arguments[^1]);
    }

    private static MediaProbeResult Source(string audioCodec) =>
        new([Track(0, MediaTrackType.Video, "h264"), Track(1, MediaTrackType.Audio, audioCodec) with { Channels = 2 }], null);

    private static MediaTrackMetadata Track(int index, MediaTrackType type, string codec) =>
        new(index, type, codec, null, null, null, null, null);

    private static void AssertOption(IReadOnlyList<string> arguments, string option, string value)
    {
        var index = arguments.ToList().IndexOf(option);
        Assert.True(index >= 0, $"Expected option '{option}'.");
        Assert.True(index + 1 < arguments.Count);
        Assert.Equal(value, arguments[index + 1]);
    }

    private static void AssertOptionValue(IReadOnlyList<string> arguments, string option, string value)
    {
        Assert.Contains(
            arguments.Select((argument, index) => (argument, index)),
            item => item.argument == option && item.index + 1 < arguments.Count && arguments[item.index + 1] == value);
    }
}
