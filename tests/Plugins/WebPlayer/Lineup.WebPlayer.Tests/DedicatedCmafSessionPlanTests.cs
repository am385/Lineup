using Lineup.Common.Streaming;
using Lineup.VirtualOutputs.Protocol;
using Lineup.WebPlayer.VirtualOutputHost;
using Xunit;

namespace Lineup.WebPlayer.Tests;

/// <summary>Verifies dedicated workers build browser presentations with the shared CMAF planner.</summary>
public sealed class DedicatedCmafSessionPlanTests
{
    /// <summary>Verifies request overrides, track selection, output settings, and captions are included in one dedicated plan.</summary>
    [Fact]
    public void Create_BrokerMetadata_UsesSharedCmafPlanning()
    {
        // Arrange
        var directory = Path.Combine(Path.GetTempPath(), "lineup-plan-tests", Guid.NewGuid().ToString("N"));
        var response = new VirtualOutputOpenStreamResponse
        {
            Accepted = true,
            RawSource = new MediaProbeResult(
            [
                new MediaTrackMetadata(0, MediaTrackType.Video, "mpeg2video", 9_000_000, 1920, 1080, null, null),
                new MediaTrackMetadata(1, MediaTrackType.Audio, "ac3", 448_000, null, null, 6, 48_000)
            ],
            9_500_000),
            Source = new MediaProbeResult(
            [
                new MediaTrackMetadata(0, MediaTrackType.Video, "h264", 8_000_000, 1920, 1080, null, null) { ScanType = VideoScanType.Progressive },
                new MediaTrackMetadata(1, MediaTrackType.Audio, "ac3", 448_000, null, null, 6, 48_000),
                new MediaTrackMetadata(2, MediaTrackType.Subtitle, "webvtt", null, null, null, null, null)
                {
                    Language = "eng",
                    Title = "English",
                    SubtitlePresentation = SubtitlePresentation.WebVtt
                },
                new MediaTrackMetadata(3, MediaTrackType.Subtitle, "eia_608", null, null, null, null, null)
                {
                    Title = "Closed Captions",
                    IsEmbeddedClosedCaptions = true,
                    SubtitlePresentation = SubtitlePresentation.WebVtt
                }
            ],
            8_500_000),
            Profile = new VirtualTranscodeProfile
            {
                VideoPreset = VideoEncoderPreset.Medium,
                VideoQuality = 18,
                MaximumVideoBitRateMbps = 14
            }
        };
        var request = new CmafStreamRequest
        {
            AudioTrack = 1,
            Overrides = new CmafStreamOverrides
            {
                Enabled = true,
                Quality = WebPlayerQuality.Low,
                Video = CmafPreferredVideo.Fallback,
                Audio = CmafPreferredAudio.Fallback,
                FallbackAudio = CmafFallbackAudio.AacStereo
            }
        };

        // Act
        var plan = DedicatedCmafSessionPlan.Create(response, request, directory);
        var started = plan.CreateResponse("session", new Uri("http://127.0.0.1:18082/"));
        var snapshot = plan.CreateActiveStreamSnapshot("broker-session", "48.3", DateTime.UtcNow);

        // Assert
        Assert.Equal(WebPlayerQuality.Low, plan.Request.Quality);
        Assert.Equal(1, plan.Selection.Audio!.Index);
        Assert.Contains(plan.Arguments.Select((value, index) => (value, index)), item => item.value == "-map" && plan.Arguments[item.index + 1] == "0:1");
        AssertOption(plan.Arguments, "-c:v:0", "libx264");
        AssertOption(plan.Arguments, "-preset:v:0", "medium");
        AssertOption(plan.Arguments, "-crf", "25");
        AssertOption(plan.Arguments, "-maxrate:v:0", "2M");
        Assert.Equal(3, plan.EmbeddedCaptions!.Index);
        Assert.Equal(2, started.Subtitles.Count);
        Assert.Contains(started.Subtitles, subtitle => subtitle.SourceIndex == 2 && subtitle.Url == "http://127.0.0.1:18082/media/session/captions-2.vtt");
        Assert.Contains(started.Subtitles, subtitle => subtitle.SourceIndex == 3 && subtitle.IsEmbeddedClosedCaptions);
        Assert.Equal("http://127.0.0.1:18082/media/stop/session", started.StopUrl);
        Assert.Equal("aac", started.FallbackAudioCodec);
        Assert.Equal(["mpeg2video", "ac3"], snapshot.PhysicalInput!.Tracks.Select(track => track.Codec));
        Assert.Equal(["h264", "ac3", "webvtt", "eia_608"], snapshot.PhysicalOutput!.Tracks.Select(track => track.Codec));
        Assert.Equal(["h264", "aac", "webvtt", "webvtt"], snapshot.VirtualOutput!.Tracks.Select(track => track.Codec));
    }

    /// <summary>Verifies a dedicated worker rejects a broker response that cannot describe its media stream.</summary>
    [Fact]
    public void Create_MissingBrokerMetadata_Throws()
    {
        // Arrange
        var response = new VirtualOutputOpenStreamResponse { Accepted = true };

        // Act
        var exception = Assert.Throws<InvalidDataException>(() =>
            DedicatedCmafSessionPlan.Create(response, new CmafStreamRequest(), Path.GetTempPath()));

        // Assert
        Assert.Contains("media metadata", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Verifies virtual profile defaults apply once while explicit browser overrides retain management-hosted precedence.</summary>
    [Fact]
    public void Create_VirtualProfileAndExplicitOverride_UseManagementPrecedence()
    {
        // Arrange
        var response = new VirtualOutputOpenStreamResponse
        {
            Accepted = true,
            Source = new MediaProbeResult(
            [
                new MediaTrackMetadata(0, MediaTrackType.Video, "hevc", 10_000_000, 1920, 1080, null, null) { Profile = "Main", Level = 120 },
                new MediaTrackMetadata(1, MediaTrackType.Audio, "aac", 192_000, null, null, 2, 48_000)
            ],
            10_500_000),
            Profile = new VirtualTranscodeProfile { ConvertHevcToH264 = true }
        };
        var explicitSourceRequest = new CmafStreamRequest
        {
            Overrides = new CmafStreamOverrides
            {
                Enabled = true,
                Video = CmafPreferredVideo.Source
            }
        };

        // Act
        var profilePlan = DedicatedCmafSessionPlan.Create(response, new CmafStreamRequest(), Path.Combine(Path.GetTempPath(), "profile"));
        var overridePlan = DedicatedCmafSessionPlan.Create(response, explicitSourceRequest, Path.Combine(Path.GetTempPath(), "override"));

        // Assert
        AssertOption(profilePlan.Arguments, "-c:v:0", "libx264");
        AssertOption(overridePlan.Arguments, "-c:v:0", "copy");
        AssertOption(profilePlan.Arguments, "-bsf:a:0", "aac_adtstoasc");
        AssertOption(overridePlan.Arguments, "-bsf:a:0", "aac_adtstoasc");
        AssertOption(profilePlan.Arguments, "-tag:a:0", "mp4a");
        AssertOption(overridePlan.Arguments, "-tag:a:0", "mp4a");
        Assert.False(profilePlan.HasSourceVideoRendition);
        Assert.True(overridePlan.HasSourceVideoRendition);
    }

    /// <summary>Verifies dedicated CMAF planning applies the virtual AC-4 policy before creating FFmpeg arguments and status.</summary>
    [Fact]
    public void Create_Ac4TopologyPolicy_ProducesAc3Presentation()
    {
        // Arrange
        var source = new MediaProbeResult(
        [
            new MediaTrackMetadata(0, MediaTrackType.Video, "hevc", 10_000_000, 1920, 1080, null, null) { Profile = "Main 10", Level = 123 },
            new MediaTrackMetadata(1, MediaTrackType.Audio, "ac4", null, null, null, null, null)
        ],
        null);
        var response = new VirtualOutputOpenStreamResponse
        {
            Accepted = true,
            Source = source,
            RawSource = source,
            Profile = new VirtualTranscodeProfile { Ac4Audio = OutputAudioPolicy.Ac3 }
        };

        // Act
        var plan = DedicatedCmafSessionPlan.Create(response, new CmafStreamRequest(), Path.Combine(Path.GetTempPath(), "ac4-policy"));
        var snapshot = plan.CreateActiveStreamSnapshot("broker-session", "122.1", DateTime.UtcNow);

        // Assert
        AssertOption(plan.Arguments, "-c:a:0", "ac3");
        AssertOption(plan.Arguments, "-ac:a:0", "2");
        AssertOption(plan.Arguments, "-ar:a:0", "48000");
        var sourceAudio = Assert.Single(snapshot.PhysicalInput!.Tracks, track => track.Type == MediaTrackType.Audio);
        var outputAudio = Assert.Single(snapshot.VirtualOutput!.Tracks, track => track.Type == MediaTrackType.Audio);
        Assert.Equal("ac4", sourceAudio.Codec);
        Assert.Equal("ac3", outputAudio.Codec);
    }

    private static void AssertOption(IReadOnlyList<string> arguments, string option, string value)
    {
        var index = Assert.Single(arguments.Select((argument, index) => (argument, index)), item => item.argument == option).index;
        Assert.Equal(value, arguments[index + 1]);
    }
}
