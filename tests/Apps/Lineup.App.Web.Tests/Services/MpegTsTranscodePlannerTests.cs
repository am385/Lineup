using Xunit;

namespace Lineup.App.Web.Tests.Services;

/// <summary>
/// Verifies FFprobe/FFmpeg argument planning and HDHomeRun stream URI construction.
/// </summary>
public class MpegTsTranscodePlannerTests
{
    /// <summary>Effective argument reporting distinguishes same-codec encoding from copy and never treats remux as codec conversion.</summary>
    [Theory]
    [InlineData(false, OutputAudioPolicy.Copy)]
    [InlineData(false, OutputAudioPolicy.Ac3)]
    [InlineData(true, OutputAudioPolicy.Copy)]
    [InlineData(true, OutputAudioPolicy.Ac3)]
    public void ProcessingDiagnostics_ActualMappings_ReportIndependentOperations(bool encodeVideo, OutputAudioPolicy audio)
    {
        // Arrange
        var source = new MediaProbeResult(
        [
            new(2, MediaTrackType.Video, "h264", null, 1920, 1080, null, null),
            new(7, MediaTrackType.Audio, "ac3", null, null, null, 6, 48000) { Language = "eng", Title = "Main" },
            new(12, MediaTrackType.Audio, "ac4", null, null, null, 12, 46034)
        ], null);
        var profile = new VirtualTranscodeProfile { AlwaysTranscodeVideo = encodeVideo, Audio = audio, Ac4Audio = OutputAudioPolicy.Copy };
        var arguments = MpegTsTranscodePlanner.CreateFfmpegArguments(_inputUri, "h264", [new(7, "ac3", 6), new(12, "ac4", 12)], profile);

        // Act
        var processing = MpegTsProcessingDiagnostics.Create(source, arguments);

        // Assert
        Assert.Equal(StreamTransportOperation.Remux, processing.Transport);
        Assert.Equal(encodeVideo || audio != OutputAudioPolicy.Copy, processing.Transcodes);
        Assert.True(processing.VideoKnown);
        Assert.True(processing.AudioKnown);
        Assert.Equal(encodeVideo ? "h264" : "copy", processing.Tracks[0].OutputCodec);
        Assert.Equal(audio == OutputAudioPolicy.Copy ? "copy" : "ac3", processing.Tracks[1].OutputCodec);
        Assert.Equal("copy", processing.Tracks[2].OutputCodec);
        Assert.Equal([2, 7, 12], processing.Tracks.Select(track => track.SourceIndex));
        Assert.Equal("eng", processing.Tracks[1].Language);
        Assert.Equal("Main", processing.Tracks[1].Title);
    }

    /// <summary>Source-stage diagnostics retain only the video actually mapped and distinguish absent audio from incomplete discovery.</summary>
    [Theory]
    [InlineData(AudioDiscoveryStatus.Unknown, false)]
    [InlineData(AudioDiscoveryStatus.DeclaredAbsent, true)]
    [InlineData(AudioDiscoveryStatus.ExpectedMissing, false)]
    public void ProcessingDiagnostics_SourceNormalization_UsesExactMappingAndDiscovery(AudioDiscoveryStatus discovery, bool audioKnown)
    {
        // Arrange
        var source = new MediaProbeResult(
        [
            new(2, MediaTrackType.Video, "hevc", null, 1920, 1080, null, null),
            new(8, MediaTrackType.Video, "hevc", null, 1920, 1080, null, null)
        ], null)
        { AudioDiscovery = discovery };
        var arguments = SourceDeinterlacePlanner.CreateArguments(source, new PhysicalTranscodeProfile { AlwaysTranscodeVideo = true });

        // Act
        var processing = MpegTsProcessingDiagnostics.Create(source, arguments);

        // Assert
        Assert.Equal(2, Assert.Single(processing.Tracks).SourceIndex);
        Assert.True(processing.Transcodes);
        Assert.Equal(audioKnown, processing.AudioKnown);
        Assert.Equal(StreamTransportOperation.Remux, processing.Transport);
    }

    /// <summary>Verifies copied AC-4 is retained and assigned to actual output positions, independently of video encoding and source indexes.</summary>
    [Theory]
    [InlineData(0, HardwareVideoEncoder.Software)]
    [InlineData(2, HardwareVideoEncoder.Software)]
    [InlineData(2, HardwareVideoEncoder.Qsv)]
    public void Ac4Copy_MapsOnlyCopiedTracksToExplicitPids(int videoCount, HardwareVideoEncoder encoder)
    {
        // Arrange
        AudioStreamInfo[] streams = [new(7, "aac", 2), new(12, "ac4", 12), new(24, "AC4", 6)];
        var profile = new VirtualTranscodeProfile
        {
            Audio = OutputAudioPolicy.Eac3,
            Ac4Audio = OutputAudioPolicy.Copy,
            AlwaysTranscodeVideo = videoCount > 0,
            HardwareEncoding = new() { Encoder = encoder },
            Ac4TimestampCompensation = true
        };

        // Act
        var arguments = MpegTsTranscodePlanner.CreateFfmpegArguments(
            _inputUri, videoCount == 0 ? null : "hevc", streams, profile, sourceVideoStreamCount: videoCount);

        // Assert
        Assert.Contains("-copyinkf:a:1", arguments);
        Assert.Contains("-copyinkf:a:2", arguments);
        Assert.DoesNotContain("-copyinkf:a:0", arguments);
        Assert.Contains($"{videoCount + 1}:{0x101 + videoCount}", arguments);
        Assert.Contains($"{videoCount + 2}:{0x102 + videoCount}", arguments);
        AssertOption(arguments, "-c:a:0", "eac3");
        Assert.DoesNotContain("-ar:a:1", arguments);
        Assert.DoesNotContain("-filter:a:1", arguments);
    }

    /// <summary>Verifies AC-4 encoding always uses 48 kHz while only opted-in transcodes receive timestamp compensation.</summary>
    [Theory]
    [InlineData("ac4", OutputAudioPolicy.Ac3, false)]
    [InlineData("ac4", OutputAudioPolicy.Ac3, true)]
    [InlineData("AC4", OutputAudioPolicy.Eac3, false)]
    [InlineData("AC4", OutputAudioPolicy.Eac3, true)]
    [InlineData("ac4", OutputAudioPolicy.Copy, false)]
    [InlineData("ac4", OutputAudioPolicy.Copy, true)]
    public void Ac4Resampling_AppliesOnlyToEncodedAc4(string sourceCodec, OutputAudioPolicy policy, bool compensate)
    {
        // Arrange
        var profile = new VirtualTranscodeProfile { Audio = OutputAudioPolicy.Ac3, Ac4Audio = policy, Ac4TimestampCompensation = compensate };
        AudioStreamInfo[] streams = [new(3, "aac", 2), new(7, sourceCodec, 6)];

        // Act
        var arguments = MpegTsTranscodePlanner.CreateFfmpegArguments(_inputUri, "hevc", streams, profile);

        // Assert
        AssertOption(arguments, "-c:a:0", "ac3");
        Assert.DoesNotContain("-filter:a:0", arguments);
        Assert.DoesNotContain("-ar:a:0", arguments);
        if (policy == OutputAudioPolicy.Copy)
        {
            Assert.Contains("-copyinkf:a:1", arguments);
            Assert.DoesNotContain("-filter:a:1", arguments);
            Assert.DoesNotContain("-ar:a:1", arguments);
        }
        else
        {
            Assert.DoesNotContain("-copyinkf:a:1", arguments);
            AssertOption(arguments, "-c:a:1", policy == OutputAudioPolicy.Ac3 ? "ac3" : "eac3");
            AssertOption(arguments, "-ar:a:1", "48000");
        }
        if (compensate && policy != OutputAudioPolicy.Copy)
        {
            AssertOption(arguments, "-filter:a:1", "aresample=async=1");
        }
        else
        {
            Assert.DoesNotContain("-filter:a:1", arguments);
        }
        Assert.DoesNotContain("-filter:a:7", arguments);
    }

    /// <summary>Verifies forced encoding constrains copy-eligible video with either software or GPU encoding.</summary>
    [Theory]
    [InlineData(false, HardwareVideoEncoder.Software, null)]
    [InlineData(true, HardwareVideoEncoder.Software, "libx264")]
    [InlineData(true, HardwareVideoEncoder.Qsv, "h264_qsv")]
    public void ForcedVideo_ControlsEncodingAndBitrate(bool force, HardwareVideoEncoder encoder, string? expectedEncoder)
    {
        // Arrange
        var profile = new VirtualTranscodeProfile
        {
            AlwaysTranscodeVideo = force,
            HardwareEncoding = new() { Encoder = encoder },
            MaximumVideoBitRateMbps = 3
        };

        // Act
        var arguments = MpegTsTranscodePlanner.CreateFfmpegArguments(
            _inputUri, "h264", [new AudioStreamInfo(1, "aac", 2)], profile);

        // Assert
        if (expectedEncoder is null)
        {
            Assert.DoesNotContain("-c:v:0", arguments);
        }
        else
        {
            AssertOption(arguments, "-c:v", expectedEncoder);
            AssertOption(arguments, "-maxrate", "3M");
            AssertOption(arguments, "-bufsize", "6M");
        }
        Assert.DoesNotContain("-c:a:0", arguments);
        Assert.DoesNotContain("-filter:v:0", arguments);
    }

    private static readonly Uri _inputUri = new("http://hdhomerun.local:5004/auto/v7.1");

    /// <summary>Verifies the lower-work software profile reaches FFmpeg while retaining AC-4 Copy and bitrate policy.</summary>
    [Theory]
    [InlineData(VideoEncoderPreset.VeryFast, "veryfast")]
    [InlineData(VideoEncoderPreset.UltraFast, "ultrafast")]
    public void SoftwarePreset_ControlsEncodingWithoutChangingAc4Copy(VideoEncoderPreset preset, string expected)
    {
        // Arrange
        var profile = new VirtualTranscodeProfile
        {
            ConvertHevcToH264 = true,
            VideoPreset = preset,
            Ac4Audio = OutputAudioPolicy.Copy
        };

        // Act
        var arguments = MpegTsTranscodePlanner.CreateFfmpegArguments(
            _inputUri, "hevc", [new AudioStreamInfo(1, "ac4", 12)], profile);

        // Assert
        AssertOption(arguments, "-c:v:0", "libx264");
        AssertOption(arguments, "-preset", expected);
        AssertOption(arguments, "-c", "copy");
        Assert.DoesNotContain("-c:a:0", arguments);
        AssertOption(arguments, "-maxrate", "10M");
    }

    /// <summary>Verifies effective topology settings, rather than legacy app values, control MPEG-TS output.</summary>
    [Fact]
    public void TopologyProfile_ControlsActualMpegTsArguments()
    {
        // Arrange
        var profile = new VirtualTranscodeProfile
        {
            Audio = OutputAudioPolicy.Eac3,
            Ac4Audio = OutputAudioPolicy.Ac3,
            ConvertHevcToH264 = true,
            Deinterlace = SourceDeinterlacePolicy.SourceFieldRate,
            VideoPreset = VideoEncoderPreset.Fast,
            VideoQuality = 18,
            MaximumVideoBitRateMbps = 19
        };

        // Act
        var arguments = MpegTsTranscodePlanner.CreateFfmpegArguments(
            _inputUri,
            "hevc",
            [new AudioStreamInfo(1, "aac", 2), new AudioStreamInfo(2, "ac4", 2)],
            profile,
            sourceVideoIsInterlaced: true);

        // Assert
        AssertOption(arguments, "-c:v:0", "libx264");
        AssertOption(arguments, "-preset", "fast");
        AssertOption(arguments, "-crf", "18");
        AssertOption(arguments, "-maxrate", "19M");
        AssertOption(arguments, "-filter:v:0", "bwdif=mode=send_field:parity=auto:deint=interlaced");
        Assert.Contains("-copyts", arguments);
        Assert.Contains("-start_at_zero", arguments);
        AssertOption(arguments, "-fps_mode:v:0", "passthrough");
        AssertOption(arguments, "-c:a:0", "eac3");
        AssertOption(arguments, "-c:a:1", "ac3");
    }

    /// <summary>
    /// Verifies that the defaults copy ordinary audio while converting AC-4 to AC-3.
    /// </summary>
    [Fact]
    public void DefaultSettings_CopyOrdinaryAudioAndTranscodeAc4ToAc3()
    {
        // Arrange
        var settings = new LineupSettings
        {
            Streaming = new StreamingConfiguration
            {
                VirtualDefaults = new VirtualTranscodeProfile
                {
                    ConvertHevcToH264 = true,
                    Ac4Audio = OutputAudioPolicy.Ac3
                }
            }
        };
        AudioStreamInfo[] streams =
        [
            new(1, "aac", 2),
            new(2, "ac4", 8)
        ];

        // Act
        var arguments = MpegTsTranscodePlanner.CreateFfmpegArguments(_inputUri, "hevc", streams, settings.Streaming.VirtualDefaults);

        // Assert
        Assert.DoesNotContain("-c:a:0", arguments);
        AssertOption(arguments, "-c:a:1", "ac3");
        AssertOption(arguments, "-b:a:1", "448k");
        AssertOption(arguments, "-ac:a:1", "6");
        AssertOption(arguments, "-analyzeduration", "10000000");
        AssertOption(arguments, "-probesize", "10000000");
        AssertOption(arguments, "-c:v:0", "libx264");
        AssertOption(arguments, "-preset", "veryfast");
        AssertOption(arguments, "-maxrate", "10M");
        AssertOption(arguments, "-pix_fmt:v:0", "yuv420p");
        AssertOption(arguments, "-x264-params:v:0", "repeat-headers=1");
        Assert.DoesNotContain("0:s?", arguments);
        Assert.DoesNotContain("0:d?", arguments);
        AssertOption(arguments, "-c", "copy");
        AssertOption(arguments, "-max_muxing_queue_size", "1024");
        Assert.Equal("pipe:1", arguments[^1]);
    }

    /// <summary>
    /// Verifies that the AC-4 target overrides the all-audio codec for AC-4 tracks.
    /// </summary>
    [Fact]
    public void Ac4Override_SupersedesAllAudioMode()
    {
        // Arrange
        var settings = new LineupSettings
        {
            Streaming = new StreamingConfiguration
            {
                VirtualDefaults = new VirtualTranscodeProfile
                {
                    Audio = OutputAudioPolicy.Ac3,
                    Ac4Audio = OutputAudioPolicy.Eac3,
                    ConvertHevcToH264 = true
                }
            }
        };
        AudioStreamInfo[] streams =
        [
            new(1, "aac", 2),
            new(2, "ac4", 6)
        ];

        // Act
        var arguments = MpegTsTranscodePlanner.CreateFfmpegArguments(_inputUri, "hevc", streams, settings.Streaming.VirtualDefaults);

        AssertOption(arguments, "-c:a:0", "ac3");
        AssertOption(arguments, "-b:a:0", "448k");
        AssertOption(arguments, "-c:a:1", "eac3");
        AssertOption(arguments, "-b:a:1", "640k");
        // Assert
        Assert.DoesNotContain("-ac:a:1", arguments);
    }

    /// <summary>
    /// Verifies that copy mode leaves non-AC-4 tracks untouched.
    /// </summary>
    [Fact]
    public void CopyModeWithoutAc4_LeavesEveryTrackCopied()
    {
        // Arrange
        var settings = new LineupSettings();
        AudioStreamInfo[] streams =
        [
            new(1, "aac", 2),
            new(2, "ac3", 6)
        ];

        // Act
        var arguments = MpegTsTranscodePlanner.CreateFfmpegArguments(_inputUri, "h264", streams, settings.Streaming.VirtualDefaults);

        // Assert
        Assert.DoesNotContain(arguments, argument => argument.StartsWith("-c:a:", StringComparison.Ordinal));
        Assert.DoesNotContain("-c:v:0", arguments);
        AssertOption(arguments, "-c", "copy");
    }

    /// <summary>
    /// Verifies AC-4 preserve mode leaves AC-4 tracks unchanged.
    /// </summary>
    [Fact]
    public void PreserveAc4_LeavesAc4TrackCopied()
    {
        // Arrange
        var settings = new LineupSettings
        {
            Streaming = new StreamingConfiguration
            {
                VirtualDefaults = new VirtualTranscodeProfile { Ac4Audio = OutputAudioPolicy.Copy }
            }
        };
        AudioStreamInfo[] streams = [new(1, "ac4", 6)];

        // Act
        var arguments = MpegTsTranscodePlanner.CreateFfmpegArguments(_inputUri, "h264", streams, settings.Streaming.VirtualDefaults);

        // Assert
        Assert.DoesNotContain(arguments, argument => argument.StartsWith("-c:a:", StringComparison.Ordinal));
        AssertOption(arguments, "-c", "copy");
    }

    /// <summary>
    /// Verifies every MPEG-TS-compatible subtitle is mapped and incompatible text is excluded.
    /// </summary>
    [Fact]
    public void SubtitleMapping_MapsEveryCompatibleAbsoluteStreamIndex()
    {
        // Arrange
        MediaTrackMetadata[] subtitles =
        [
            new(5, MediaTrackType.Subtitle, "dvb_subtitle", null, null, null, null, null),
            new(7, MediaTrackType.Subtitle, "dvb_teletext", null, null, null, null, null),
            new(9, MediaTrackType.Subtitle, "subrip", null, null, null, null, null)
        ];

        // Act
        var arguments = MpegTsTranscodePlanner.CreateFfmpegArguments(_inputUri, "h264", [], new VirtualTranscodeProfile(), subtitles);

        // Assert
        AssertMappings(arguments, "0:5", "0:7");
        Assert.DoesNotContain("0:9", arguments);
        AssertOption(arguments, "-map_metadata", "0");
    }

    /// <summary>
    /// Verifies that live stream probing uses deterministic analysis limits.
    /// </summary>
    [Fact]
    public void ProbeArguments_UseBoundedLiveStreamAnalysis()
    {

        // Arrange
        // Act
        var arguments = MediaProbeParser.CreateArguments(_inputUri);

        AssertOption(arguments, "-analyzeduration", "1000000");
        AssertOption(arguments, "-probesize", "1000000");
        AssertOption(arguments, "-rw_timeout", "10000000");
        AssertOption(arguments, "-reconnect_on_http_error", "503");
        AssertOption(arguments, "-reconnect_max_retries", "3");
        AssertOption(arguments, "-read_intervals", "%+3");
        // Assert
        Assert.Equal(_inputUri.AbsoluteUri, arguments[^1]);
    }

    /// <summary>
    /// Verifies that shared input probing reads standard input without HTTP-only options.
    /// </summary>
    [Fact]
    public void PipeProbeArguments_ReadStandardInput()
    {

        // Arrange
        // Act
        var arguments = MediaProbeParser.CreatePipeArguments();

        AssertOption(arguments, "-analyzeduration", "1000000");
        AssertOption(arguments, "-probesize", "1000000");
        AssertOption(arguments, "-rw_timeout", null, optionMustBeAbsent: true);
        AssertOption(arguments, "-reconnect", null, optionMustBeAbsent: true);
        // Assert
        Assert.Equal("pipe:0", arguments[^1]);
    }

    private static void AssertOption(IReadOnlyList<string> arguments, string option, string? expectedValue, bool optionMustBeAbsent = false)
    {
        var optionIndex = -1;
        for (var index = 0; index < arguments.Count; index++)
        {
            if (arguments[index] == option)
            {
                optionIndex = index;
                break;
            }
        }
        if (optionMustBeAbsent)
        {
            Assert.Equal(-1, optionIndex);
            return;
        }

        Assert.True(optionIndex >= 0, $"Expected option '{option}'.");
        Assert.True(optionIndex + 1 < arguments.Count, $"Expected a value after '{option}'.");
        Assert.Equal(expectedValue, arguments[optionIndex + 1]);
    }

    private static void AssertMappings(IReadOnlyList<string> arguments, params string[] expected)
    {
        var mappings = arguments
            .Select((value, index) => (value, index))
            .Where(item => item.value == "-map")
            .Select(item => arguments[item.index + 1]);
        Assert.All(expected, mapping => Assert.Contains(mapping, mappings));
    }
}
