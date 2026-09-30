namespace Lineup.Web.Services;

/// <summary>
/// Plans shared source normalization for confirmed interlaced video.
/// </summary>
public static class SourceDeinterlacePlanner
{
    /// <summary>
    /// Returns whether one confirmed interlaced video track requires the configured filter.
    /// </summary>
    public static bool ShouldDeinterlace(MediaTrackMetadata? video, DeinterlaceMode mode) =>
        mode != DeinterlaceMode.Preserve &&
        video?.Type == MediaTrackType.Video &&
        video.ScanType == VideoScanType.Interlaced;

    /// <summary>
    /// Returns whether the source requires shared deinterlacing.
    /// </summary>
    public static bool ShouldNormalize(MediaProbeResult source, DeinterlaceMode mode) =>
        ShouldDeinterlace(source.Tracks.FirstOrDefault(track => track.Type == MediaTrackType.Video), mode);

    /// <summary>
    /// Returns metadata describing the effective shared source delivered to playback consumers.
    /// </summary>
    public static MediaProbeResult CreateEffectiveSource(MediaProbeResult source, AppSettings settings)
    {
        if (!ShouldNormalize(source, settings.SourceDeinterlaceMode))
        {
            return source;
        }

        var primaryVideo = source.Tracks.First(track => track.Type == MediaTrackType.Video);
        var outputIndex = 0;
        var tracks = new List<MediaTrackMetadata>
        {
            primaryVideo with
            {
                Index = outputIndex++,
                Codec = "h264",
                BitRate = settings.MaximumVideoBitRateMbps * 1_000_000L,
                Profile = "High",
                Level = 42,
                ScanType = VideoScanType.Progressive
            }
        };
        tracks.AddRange(source.Tracks
            .Where(track => track.Type == MediaTrackType.Audio)
            .Select(track => track with { Index = outputIndex++ }));
        tracks.AddRange(source.Tracks
            .Where(track => track.Type == MediaTrackType.Subtitle && (track.IsEmbeddedClosedCaptions || SubtitleCapabilityPolicy.CanCopyToMpegTs(track.Codec)))
            .Select(track => track with { Index = outputIndex++ }));
        return source with { Tracks = tracks };
    }

    /// <summary>
    /// Creates FFmpeg arguments for one shared progressive H.264 MPEG-TS source.
    /// </summary>
    public static IReadOnlyList<string> CreateArguments(MediaProbeResult source, AppSettings settings)
    {
        if (!ShouldNormalize(source, settings.SourceDeinterlaceMode))
        {
            throw new ArgumentException("Shared source normalization requires confirmed interlaced video and an enabled mode.", nameof(source));
        }

        var filter = CreateFilter(settings.SourceDeinterlaceMode);
        List<string> arguments =
        [
            "-hide_banner", "-loglevel", "warning",
            "-analyzeduration", "10000000", "-probesize", "10000000",
            "-fflags", "+genpts", "-i", "pipe:0",
            "-map", "0:v:0?", "-map", "0:a?", "-map_metadata", "0",
            "-c", "copy"
        ];
        foreach (var subtitle in source.Tracks.Where(track => track.Type == MediaTrackType.Subtitle && SubtitleCapabilityPolicy.CanCopyToMpegTs(track.Codec)))
        {
            arguments.AddRange(["-map", $"0:{subtitle.Index}"]);
        }

        arguments.AddRange(
        [
            "-c:v:0", "libx264",
            "-preset:v:0", settings.WebVideoPreset.ToString().ToLowerInvariant(),
            "-tune:v:0", "zerolatency",
            "-crf:v:0", settings.WebVideoQuality.ToString(),
            "-maxrate:v:0", $"{settings.MaximumVideoBitRateMbps}M",
            "-bufsize:v:0", $"{settings.MaximumVideoBitRateMbps * 2}M",
            "-profile:v:0", "high",
            "-level:v:0", "4.2",
            "-pix_fmt:v:0", "yuv420p",
            "-filter:v:0", filter,
            "-a53cc:v:0", "1",
            "-flags:v:0", "+cgop",
            "-g:v:0", "120",
            "-keyint_min:v:0", "60",
            "-sc_threshold:v:0", "0",
            "-force_key_frames:v:0", "expr:gte(t,n_forced*2)",
            "-x264-params:v:0", "repeat-headers=1",
            "-f", "mpegts",
            "-mpegts_flags", "+resend_headers",
            "-flush_packets", "1",
            "-muxdelay", "0",
            "pipe:1"
        ]);
        return arguments;
    }

    /// <summary>
    /// Creates the FFmpeg deinterlacing filter for a configured mode.
    /// </summary>
    public static string CreateFilter(DeinterlaceMode mode) =>
        mode switch
        {
            DeinterlaceMode.SourceFrameRate => "bwdif=mode=send_frame:parity=auto:deint=interlaced",
            DeinterlaceMode.SourceFieldRate => "bwdif=mode=send_field:parity=auto:deint=interlaced",
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Deinterlacing must be enabled to create a filter.")
        };
}
