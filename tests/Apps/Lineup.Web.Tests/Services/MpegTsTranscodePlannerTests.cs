using Lineup.Web.Controllers;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>
/// Verifies FFprobe/FFmpeg argument planning and HDHomeRun stream URI construction.
/// </summary>
public class MpegTsTranscodePlannerTests
{
    private static readonly Uri InputUri = new("http://hdhomerun.local:5004/auto/v7.1");

    /// <summary>Verifies effective topology settings, rather than legacy app values, control MPEG-TS output.</summary>
    [Fact]
    public void TopologyProfile_ControlsActualMpegTsArguments()
    {
        // Arrange
        var settings = new LineupSettings();
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
            InputUri,
            "hevc",
            [new AudioStreamInfo(1, "aac", 2), new AudioStreamInfo(2, "ac4", 2)],
            profile,
            settings,
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
        var arguments = MpegTsTranscodePlanner.CreateFfmpegArguments(InputUri, "hevc", streams, settings);

        // Assert
        Assert.DoesNotContain("-c:a:0", arguments);
        AssertOption(arguments, "-c:a:1", "ac3");
        AssertOption(arguments, "-b:a:1", "448k");
        AssertOption(arguments, "-ac:a:1", "6");
        AssertOption(arguments, "-analyzeduration", "1000000");
        AssertOption(arguments, "-probesize", "1000000");
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
        var arguments = MpegTsTranscodePlanner.CreateFfmpegArguments(InputUri, "hevc", streams, settings);

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
        var arguments = MpegTsTranscodePlanner.CreateFfmpegArguments(InputUri, "h264", streams, settings);

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
        var arguments = MpegTsTranscodePlanner.CreateFfmpegArguments(InputUri, "h264", streams, settings);

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
        var arguments = MpegTsTranscodePlanner.CreateFfmpegArguments(InputUri, "h264", [], new LineupSettings(), subtitles);

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
        var arguments = MediaProbeParser.CreateArguments(InputUri);

        AssertOption(arguments, "-analyzeduration", "1000000");
        AssertOption(arguments, "-probesize", "1000000");
        AssertOption(arguments, "-rw_timeout", "10000000");
        AssertOption(arguments, "-reconnect_on_http_error", "503");
        AssertOption(arguments, "-reconnect_max_retries", "3");
        AssertOption(arguments, "-read_intervals", "%+3");
        // Assert
        Assert.Equal(InputUri.AbsoluteUri, arguments[^1]);
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
