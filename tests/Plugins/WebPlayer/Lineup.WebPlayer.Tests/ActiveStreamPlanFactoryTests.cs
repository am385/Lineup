using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;
using Lineup.WebPlayer.FFmpeg;
using Xunit;

namespace Lineup.WebPlayer.Tests;

/// <summary>Verifies active-stream media snapshots across processing stages.</summary>
public sealed class ActiveStreamPlanFactoryTests
{
    /// <summary>Verifies MPEG-TS planning retains raw, effective, and final media independently.</summary>
    [Fact]
    public void CreateMpegTs_ProcessedSource_RetainsEveryMediaBoundary()
    {
        // Arrange
        var rawSource = Source("mpeg2video", "ac3");
        var effectiveSource = Source("hevc", "ac3");
        var profile = new VirtualTranscodeProfile { ConvertHevcToH264 = true };

        // Act
        var snapshot = ActiveStreamPlanFactory.CreateMpegTs("session", "7.1", DateTime.UtcNow, rawSource, effectiveSource, profile);

        // Assert
        Assert.Equal("mpeg2video", Assert.Single(snapshot.PhysicalInput!.Tracks, track => track.Type == MediaTrackType.Video).Codec);
        Assert.Equal("hevc", Assert.Single(snapshot.PhysicalOutput!.Tracks, track => track.Type == MediaTrackType.Video).Codec);
        Assert.Equal("h264", Assert.Single(snapshot.VirtualOutput!.Tracks, track => track.Type == MediaTrackType.Video).Codec);
        Assert.Equal("ac3", Assert.Single(snapshot.VirtualOutput.Tracks, track => track.Type == MediaTrackType.Audio).Codec);
    }

    /// <summary>Verifies generated slates expose complete media at every stage.</summary>
    [Fact]
    public void CreateProtectedSlate_SyntheticMedia_PopulatesEveryBoundary()
    {
        // Arrange
        var startedAtUtc = DateTime.UtcNow;

        // Act
        var snapshot = ActiveStreamPlanFactory.CreateProtectedSlate("session", "7.1", HostedStreamFormat.Cmaf, startedAtUtc);

        // Assert
        Assert.Equal(["h264", "aac"], snapshot.PhysicalInput!.Tracks.Select(track => track.Codec));
        Assert.Equal(["h264", "aac"], snapshot.PhysicalOutput!.Tracks.Select(track => track.Codec));
        Assert.Equal(["h264", "aac"], snapshot.VirtualOutput!.Tracks.Select(track => track.Codec));
    }

    /// <summary>Verifies an unknown copied-video bitrate does not produce a misleading audio-only total.</summary>
    [Fact]
    public void CreateMpegTs_CopyWithUnknownVideoBitRate_LeavesOutputTotalUnknown()
    {
        // Arrange
        var source = new MediaProbeResult(
            [
                new MediaTrackMetadata(0, MediaTrackType.Video, "h264", null, 1920, 1080, null, null),
                new MediaTrackMetadata(1, MediaTrackType.Audio, "ac3", 192_000, null, null, 2, 48_000)
            ],
            null);

        // Act
        var snapshot = ActiveStreamPlanFactory.CreateMpegTs("session", "7.1", DateTime.UtcNow, source, new VirtualTranscodeProfile());

        // Assert
        Assert.Null(snapshot.VirtualOutput!.BitRate);
    }

    private static MediaProbeResult Source(string videoCodec, string audioCodec) =>
        new(
            [
                new MediaTrackMetadata(0, MediaTrackType.Video, videoCodec, 3_500_000, 1920, 1080, null, null),
                new MediaTrackMetadata(1, MediaTrackType.Audio, audioCodec, 192_000, null, null, 6, 48_000)
            ],
            4_000_000);
}
