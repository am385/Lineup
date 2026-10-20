using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>
/// Verifies shared interlaced-source normalization planning.
/// </summary>
public class SourceDeinterlacePlannerTests
{
    /// <summary>Verifies the effective physical topology profile controls source normalization arguments.</summary>
    [Fact]
    public void CreateArguments_TopologyPhysicalProfile_ControlsNormalization()
    {
        // Arrange
        var source = new MediaProbeResult(
            [new MediaTrackMetadata(0, MediaTrackType.Video, "mpeg2video", 8_000_000, 1920, 1080, null, null) { ScanType = VideoScanType.Interlaced }],
            null);
        var encoder = new LineupSettings();
        var profile = new PhysicalTranscodeProfile
        {
            Deinterlace = SourceDeinterlacePolicy.SourceFieldRate,
            VideoPreset = VideoEncoderPreset.Fast,
            VideoQuality = 18,
            MaximumVideoBitRateMbps = 16
        };

        // Act
        var arguments = SourceDeinterlacePlanner.CreateArguments(source, profile, encoder);

        // Assert
        AssertOption(arguments, "-filter:v:0", "bwdif=mode=send_field:parity=auto:deint=interlaced");
        Assert.Contains("-copyts", arguments);
        Assert.Contains("-start_at_zero", arguments);
        AssertOption(arguments, "-fps_mode:v:0", "passthrough");
        AssertOption(arguments, "-preset:v:0", "fast");
        AssertOption(arguments, "-crf:v:0", "18");
        AssertOption(arguments, "-maxrate:v:0", "16M");
    }

    /// <summary>Verifies physical audio policies trigger one shared transform before multiplexing.</summary>
    [Fact]
    public void CreateArguments_PhysicalAudioPolicies_TranscodeBeforeMultiplexing()
    {
        // Arrange
        var source = new MediaProbeResult(
        [
            new MediaTrackMetadata(0, MediaTrackType.Video, "h264", 6_000_000, 1920, 1080, null, null) { ScanType = VideoScanType.Progressive },
            new MediaTrackMetadata(1, MediaTrackType.Audio, "aac", 128_000, null, null, 2, 48_000),
            new MediaTrackMetadata(2, MediaTrackType.Audio, "ac4", 384_000, null, null, 8, 48_000)
        ],
        null);
        var profile = new PhysicalTranscodeProfile
        {
            Audio = OutputAudioPolicy.Ac3,
            Ac4Audio = OutputAudioPolicy.Eac3
        };

        // Act
        var arguments = SourceDeinterlacePlanner.CreateArguments(source, profile, new LineupSettings());
        var effective = SourceDeinterlacePlanner.CreateEffectiveSource(source, profile);

        // Assert
        Assert.True(SourceDeinterlacePlanner.ShouldNormalize(source, profile));
        Assert.DoesNotContain("-c:v:0", arguments);
        Assert.DoesNotContain("-copyts", arguments);
        Assert.DoesNotContain("-fps_mode:v:0", arguments);
        AssertOption(arguments, "-c:a:0", "ac3");
        AssertOption(arguments, "-c:a:1", "eac3");
        AssertOption(arguments, "-ac:a:1", "6");
        Assert.Equal("h264", Assert.Single(effective.Tracks, track => track.Type == MediaTrackType.Video).Codec);
        Assert.Equal(["ac3", "eac3"], effective.Tracks.Where(track => track.Type == MediaTrackType.Audio).Select(track => track.Codec));
    }
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
        var settings = new LineupSettings
        {
            Streaming = new StreamingConfiguration
            {
                PhysicalDefaults = new PhysicalTranscodeProfile { Deinterlace = SourceDeinterlacePolicy.SourceFieldRate }
            }
        };

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
        var normalize = SourceDeinterlacePlanner.ShouldNormalize(source, SourceDeinterlacePolicy.SourceFieldRate);

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
        var settings = new LineupSettings
        {
            Streaming = new StreamingConfiguration
            {
                PhysicalDefaults = new PhysicalTranscodeProfile { Deinterlace = SourceDeinterlacePolicy.SourceFieldRate },
                VirtualDefaults = new VirtualTranscodeProfile { Deinterlace = SourceDeinterlacePolicy.SourceFieldRate }
            }
        };
        var effectiveSource = SourceDeinterlacePlanner.CreateEffectiveSource(rawSource, settings);
        var selection = WebPlayerTrackPlanner.SelectTracks(effectiveSource, 1, null);

        // Act
        var cmafArguments = CmafStreamPlanner.CreateArguments(settings.Streaming.VirtualDefaults, effectiveSource, selection, new CmafStreamRequest(), "manifest.mpd");

        // Assert
        AssertOption(cmafArguments, "-c:v:0", "copy");
        Assert.DoesNotContain("-filter:v:0", cmafArguments);
    }

    /// <summary>Verifies virtual MPEG-TS metadata reflects output transforms and remapped stream indexes.</summary>
    [Fact]
    public void CreateVirtualMpegTsSource_VirtualProfile_DescribesDeliveredWorkerStream()
    {
        // Arrange
        var source = new MediaProbeResult(
        [
            new MediaTrackMetadata(0, MediaTrackType.Video, "hevc", 12_000_000, 1920, 1080, null, null) { ScanType = VideoScanType.Interlaced },
            new MediaTrackMetadata(4, MediaTrackType.Audio, "ac4", 384_000, null, null, 8, 48_000),
            new MediaTrackMetadata(7, MediaTrackType.Subtitle, "eia_608", null, null, null, null, null)
            {
                IsEmbeddedClosedCaptions = true,
                SubtitlePresentation = SubtitlePresentation.WebVtt
            }
        ],
        12_500_000);
        var profile = new VirtualTranscodeProfile
        {
            ConvertHevcToH264 = true,
            Deinterlace = SourceDeinterlacePolicy.SourceFieldRate,
            Ac4Audio = OutputAudioPolicy.Ac3,
            MaximumVideoBitRateMbps = 8
        };

        // Act
        var effective = SourceDeinterlacePlanner.CreateVirtualMpegTsSource(source, profile);

        // Assert
        Assert.Equal([0, 1, 2], effective.Tracks.Select(track => track.Index));
        var video = Assert.Single(effective.Tracks, track => track.Type == MediaTrackType.Video);
        Assert.Equal("h264", video.Codec);
        Assert.Equal(VideoScanType.Progressive, video.ScanType);
        Assert.Equal(8_000_000, video.BitRate);
        var audio = Assert.Single(effective.Tracks, track => track.Type == MediaTrackType.Audio);
        Assert.Equal("ac3", audio.Codec);
        Assert.Equal(6, audio.Channels);
        Assert.True(Assert.Single(effective.Tracks, track => track.Type == MediaTrackType.Subtitle).IsEmbeddedClosedCaptions);
    }

    private static void AssertOption(IReadOnlyList<string> arguments, string option, string value)
    {
        var index = Assert.Single(arguments.Select((argument, index) => (argument, index)), item => item.argument == option).index;
        Assert.Equal(value, arguments[index + 1]);
    }

    private static void AssertMapping(IReadOnlyList<string> arguments, string mapping) =>
        Assert.Contains(arguments.Select((argument, index) => (argument, index)), item => item.argument == "-map" && arguments[item.index + 1] == mapping);
}
