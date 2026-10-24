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

    /// <summary>Verifies audio-only physical transforms produce optional video mapping without video encoding or timing filters.</summary>
    [Theory]
    [InlineData(OutputAudioPolicy.Copy, OutputAudioPolicy.Ac3, "copy", "ac3")]
    [InlineData(OutputAudioPolicy.Copy, OutputAudioPolicy.Eac3, "copy", "eac3")]
    [InlineData(OutputAudioPolicy.Ac3, OutputAudioPolicy.Copy, "ac3", "copy")]
    [InlineData(OutputAudioPolicy.Eac3, OutputAudioPolicy.Copy, "eac3", "copy")]
    [InlineData(OutputAudioPolicy.Ac3, OutputAudioPolicy.Eac3, "ac3", "eac3")]
    [InlineData(OutputAudioPolicy.Eac3, OutputAudioPolicy.Ac3, "eac3", "ac3")]
    public void CreateArguments_AudioOnlyTopology_EncodesWithoutVideo(OutputAudioPolicy audioPolicy, OutputAudioPolicy ac4Policy, string aacEncoder, string ac4Encoder)
    {
        // Arrange
        var source = AudioOnlySource();
        var profile = AudioOnlyProfile(audioPolicy, ac4Policy);

        // Act
        var arguments = SourceDeinterlacePlanner.CreateArguments(source, profile, new LineupSettings());

        // Assert
        Assert.True(SourceDeinterlacePlanner.ShouldNormalize(source, profile));
        AssertMapping(arguments, "0:v:0?");
        AssertMapping(arguments, "0:a?");
        AssertMapping(arguments, "0:10");
        AssertOption(arguments, "-c", "copy");
        AssertOption(arguments, "-f", "mpegts");
        Assert.DoesNotContain("-c:v:0", arguments);
        Assert.DoesNotContain("-filter:v:0", arguments);
        Assert.DoesNotContain("-copyts", arguments);
        Assert.DoesNotContain("-fps_mode:v:0", arguments);
        Assert.DoesNotContain("0:11", arguments);
        AssertAudioEncoder(arguments, 0, aacEncoder);
        AssertAudioEncoder(arguments, 1, ac4Encoder);
        if (ac4Encoder != "copy")
        {
            AssertOption(arguments, "-ac:a:1", "6");
        }
    }

    /// <summary>Verifies audio-only effective source metadata matches physical transforms, starts at index zero, and preserves supported subtitles.</summary>
    [Theory]
    [InlineData(OutputAudioPolicy.Copy, OutputAudioPolicy.Ac3, "copy", "ac3")]
    [InlineData(OutputAudioPolicy.Copy, OutputAudioPolicy.Eac3, "copy", "eac3")]
    [InlineData(OutputAudioPolicy.Ac3, OutputAudioPolicy.Copy, "ac3", "copy")]
    [InlineData(OutputAudioPolicy.Eac3, OutputAudioPolicy.Copy, "eac3", "copy")]
    [InlineData(OutputAudioPolicy.Ac3, OutputAudioPolicy.Eac3, "ac3", "eac3")]
    [InlineData(OutputAudioPolicy.Eac3, OutputAudioPolicy.Ac3, "eac3", "ac3")]
    public void CreateEffectiveSource_AudioOnlyTopology_RemapsWithoutVideo(OutputAudioPolicy audioPolicy, OutputAudioPolicy ac4Policy, string aacEncoder, string ac4Encoder)
    {
        // Arrange
        var source = AudioOnlySource();
        var profile = AudioOnlyProfile(audioPolicy, ac4Policy);

        // Act
        var effective = SourceDeinterlacePlanner.CreateEffectiveSource(source, profile);

        // Assert
        AssertAudioOnlyMetadata(source, effective, aacEncoder, ac4Encoder);
    }

    /// <summary>Verifies virtual MPEG-TS output metadata supports the same audio-only transforms and remapping as the physical stage.</summary>
    [Theory]
    [InlineData(OutputAudioPolicy.Copy, OutputAudioPolicy.Ac3, "copy", "ac3")]
    [InlineData(OutputAudioPolicy.Copy, OutputAudioPolicy.Eac3, "copy", "eac3")]
    [InlineData(OutputAudioPolicy.Ac3, OutputAudioPolicy.Copy, "ac3", "copy")]
    [InlineData(OutputAudioPolicy.Eac3, OutputAudioPolicy.Copy, "eac3", "copy")]
    [InlineData(OutputAudioPolicy.Ac3, OutputAudioPolicy.Eac3, "ac3", "eac3")]
    [InlineData(OutputAudioPolicy.Eac3, OutputAudioPolicy.Ac3, "eac3", "ac3")]
    public void CreateVirtualMpegTsSource_AudioOnlyTopology_RemapsWithoutVideo(OutputAudioPolicy audioPolicy, OutputAudioPolicy ac4Policy, string aacEncoder, string ac4Encoder)
    {
        // Arrange
        var source = AudioOnlySource();
        var profile = new VirtualTranscodeProfile
        {
            Audio = audioPolicy,
            Ac4Audio = ac4Policy,
            ConvertHevcToH264 = true,
            Deinterlace = SourceDeinterlacePolicy.SourceFieldRate
        };

        // Act
        var effective = SourceDeinterlacePlanner.CreateVirtualMpegTsSource(source, profile);

        // Assert
        AssertAudioOnlyMetadata(source, effective, aacEncoder, ac4Encoder);
    }

    /// <summary>Verifies legacy settings overloads retain audio-only transform behavior.</summary>
    [Fact]
    public void CreatePlan_AudioOnlyTopology_SettingsOverloadsMatchProfile()
    {
        // Arrange
        var source = AudioOnlySource();
        var settings = new LineupSettings
        {
            Streaming = new StreamingConfiguration { PhysicalDefaults = AudioOnlyProfile(OutputAudioPolicy.Ac3, OutputAudioPolicy.Eac3) }
        };

        // Act
        var arguments = SourceDeinterlacePlanner.CreateArguments(source, settings);
        var effective = SourceDeinterlacePlanner.CreateEffectiveSource(source, settings);

        // Assert
        AssertAudioEncoder(arguments, 0, "ac3");
        AssertAudioEncoder(arguments, 1, "eac3");
        AssertAudioOnlyMetadata(source, effective, "ac3", "eac3");
    }

    /// <summary>Verifies video policies alone cannot create a transform for audio-only or empty sources.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShouldNormalize_AudioOnlyTopology_CopyPoliciesDoNotTransform(bool emptySource)
    {
        // Arrange
        var source = emptySource ? new MediaProbeResult([], null) : AudioOnlySource();
        var profile = AudioOnlyProfile(OutputAudioPolicy.Copy, OutputAudioPolicy.Copy);

        // Act
        var normalize = SourceDeinterlacePlanner.ShouldNormalize(source, profile);
        var effective = SourceDeinterlacePlanner.CreateEffectiveSource(source, profile);
        var exception = Record.Exception(() => SourceDeinterlacePlanner.CreateArguments(source, profile, new LineupSettings()));

        // Assert
        Assert.False(normalize);
        Assert.Same(source, effective);
        Assert.IsType<ArgumentException>(exception);
    }

    private static MediaProbeResult AudioOnlySource() => new(
    [
        new MediaTrackMetadata(3, MediaTrackType.Audio, "aac", 128_000, null, null, 2, 44_100) { Language = "eng", Title = "Main" },
        new MediaTrackMetadata(8, MediaTrackType.Audio, "AC4", 384_000, null, null, 8, 48_000) { Language = "spa", Title = "Alternate" },
        new MediaTrackMetadata(10, MediaTrackType.Subtitle, "dvb_subtitle", null, null, null, null, null),
        new MediaTrackMetadata(11, MediaTrackType.Subtitle, "subrip", null, null, null, null, null)
    ],
    512_000);

    private static PhysicalTranscodeProfile AudioOnlyProfile(OutputAudioPolicy audioPolicy, OutputAudioPolicy ac4Policy) => new()
    {
        Audio = audioPolicy,
        Ac4Audio = ac4Policy,
        ConvertHevcToH264 = true,
        Deinterlace = SourceDeinterlacePolicy.SourceFieldRate
    };

    private static void AssertAudioOnlyMetadata(MediaProbeResult source, MediaProbeResult effective, string aacEncoder, string ac4Encoder)
    {
        Assert.Equal([0, 1, 2], effective.Tracks.Select(track => track.Index));
        Assert.DoesNotContain(effective.Tracks, track => track.Type == MediaTrackType.Video);
        var audio = effective.Tracks.Where(track => track.Type == MediaTrackType.Audio).ToArray();
        for (var index = 0; index < audio.Length; index++)
        {
            var original = source.Tracks[index];
            var codec = index == 0 ? aacEncoder : ac4Encoder;
            var expected = codec == "copy" ? original with { Index = index } : original with
            {
                Index = index,
                Codec = codec,
                BitRate = codec == "ac3" ? 448_000 : 640_000,
                Channels = original.Channels is > 6 ? 6 : original.Channels
            };
            Assert.Equal(expected, audio[index]);
        }
        Assert.Equal(source.Tracks[2] with { Index = 2 }, Assert.Single(effective.Tracks, track => track.Type == MediaTrackType.Subtitle));
        Assert.Equal([3, 8, 10, 11], source.Tracks.Select(track => track.Index));
    }

    private static void AssertAudioEncoder(IReadOnlyList<string> arguments, int index, string codec)
    {
        if (codec == "copy")
        {
            Assert.DoesNotContain($"-c:a:{index}", arguments);
        }
        else
        {
            AssertOption(arguments, $"-c:a:{index}", codec);
            AssertOption(arguments, $"-b:a:{index}", codec == "ac3" ? "448k" : "640k");
        }
    }

    private static void AssertOption(IReadOnlyList<string> arguments, string option, string value)
    {
        var index = Assert.Single(arguments.Select((argument, index) => (argument, index)), item => item.argument == option).index;
        Assert.Equal(value, arguments[index + 1]);
    }

    private static void AssertMapping(IReadOnlyList<string> arguments, string mapping) =>
        Assert.Contains(arguments.Select((argument, index) => (argument, index)), item => item.argument == "-map" && arguments[item.index + 1] == mapping);
}
