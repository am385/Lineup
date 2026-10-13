using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;

namespace Lineup.WebPlayer.FFmpeg;

/// <summary>Plans deinterlacing required by a Web Player CMAF output.</summary>
internal static class CmafDeinterlacePlanner
{
    /// <summary>Returns whether confirmed interlaced video requires the configured filter.</summary>
    public static bool ShouldDeinterlace(MediaTrackMetadata? video, SourceDeinterlacePolicy mode) =>
        mode != SourceDeinterlacePolicy.Preserve &&
        video?.Type == MediaTrackType.Video &&
        video.ScanType == VideoScanType.Interlaced;

    /// <summary>Returns the FFmpeg video filter for the selected deinterlace policy.</summary>
    public static string CreateFilter(SourceDeinterlacePolicy mode) =>
        mode switch
        {
            SourceDeinterlacePolicy.SourceFrameRate => "bwdif=mode=send_frame:parity=auto:deint=interlaced",
            SourceDeinterlacePolicy.SourceFieldRate => "bwdif=mode=send_field:parity=auto:deint=interlaced",
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "A deinterlace filter requires an enabled policy.")
        };
}
