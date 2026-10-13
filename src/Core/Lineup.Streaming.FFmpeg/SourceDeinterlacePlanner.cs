namespace Lineup.Streaming.FFmpeg;

using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;

/// <summary>
/// Plans shared source normalization for confirmed interlaced video.
/// </summary>
public static class SourceDeinterlacePlanner
{
    /// <summary>
    /// Returns whether one confirmed interlaced video track requires the configured filter.
    /// </summary>
    public static bool ShouldDeinterlace(MediaTrackMetadata? video, SourceDeinterlacePolicy mode) =>
        mode != SourceDeinterlacePolicy.Preserve &&
        video?.Type == MediaTrackType.Video &&
        video.ScanType == VideoScanType.Interlaced;

    /// <summary>
    /// Returns whether the source requires shared deinterlacing.
    /// </summary>
    public static bool ShouldNormalize(MediaProbeResult source, SourceDeinterlacePolicy mode) =>
        ShouldDeinterlace(source.Tracks.FirstOrDefault(track => track.Type == MediaTrackType.Video), mode);

    /// <summary>Returns whether the effective physical profile requires a shared source transform.</summary>
    public static bool ShouldNormalize(MediaProbeResult source, PhysicalTranscodeProfile profile)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(profile);
        var video = source.Tracks.FirstOrDefault(track => track.Type == MediaTrackType.Video);
        return ShouldDeinterlace(video, profile.Deinterlace)
            || profile.ConvertHevcToH264 && string.Equals(video?.Codec, "hevc", StringComparison.OrdinalIgnoreCase)
            || source.Tracks.Any(track => track.Type == MediaTrackType.Audio && ResolveAudioCodec(track.Codec, profile) != "copy");
    }

    /// <summary>
    /// Returns metadata describing the effective shared source delivered to playback consumers.
    /// </summary>
    public static MediaProbeResult CreateEffectiveSource(MediaProbeResult source, LineupSettings settings)
        => CreateEffectiveSource(source, ToPhysicalProfile(settings));

    /// <summary>Returns metadata for a source normalized with an effective topology profile.</summary>
    public static MediaProbeResult CreateEffectiveSource(MediaProbeResult source, PhysicalTranscodeProfile profile)
    {
        if (!ShouldNormalize(source, profile))
        {
            return source;
        }

        var primaryVideo = source.Tracks.First(track => track.Type == MediaTrackType.Video);
        var transcodeVideo = ShouldTranscodeVideo(primaryVideo, profile);
        var outputIndex = 0;
        var tracks = new List<MediaTrackMetadata>
        {
            transcodeVideo
                ? primaryVideo with
                {
                    Index = outputIndex++,
                    Codec = "h264",
                    BitRate = profile.MaximumVideoBitRateMbps * 1_000_000L,
                    Profile = "High",
                    Level = 42,
                    ScanType = SourceDeinterlacePlanner.ShouldDeinterlace(primaryVideo, profile.Deinterlace)
                        ? VideoScanType.Progressive
                        : primaryVideo.ScanType
                }
                : primaryVideo with
            {
                Index = outputIndex++
            }
        };
        tracks.AddRange(source.Tracks
            .Where(track => track.Type == MediaTrackType.Audio)
            .Select(track =>
            {
                var codec = ResolveAudioCodec(track.Codec, profile);
                return codec == "copy"
                    ? track with { Index = outputIndex++ }
                    : track with
                    {
                        Index = outputIndex++,
                        Codec = codec,
                        BitRate = codec == "ac3" ? 448_000 : 640_000,
                        Channels = track.Channels is > 6 ? 6 : track.Channels
                    };
            }));
        tracks.AddRange(source.Tracks
            .Where(track => track.Type == MediaTrackType.Subtitle && (track.IsEmbeddedClosedCaptions || SubtitleCapabilityPolicy.CanCopyToMpegTs(track.Codec)))
            .Select(track => track with { Index = outputIndex++ }));
        return source with { Tracks = tracks };
    }

    /// <summary>
    /// Creates FFmpeg arguments for one shared progressive H.264 MPEG-TS source.
    /// </summary>
    public static IReadOnlyList<string> CreateArguments(MediaProbeResult source, LineupSettings settings)
        => CreateArguments(source, ToPhysicalProfile(settings), settings);

    /// <summary>Creates FFmpeg arguments using an effective topology physical profile.</summary>
    public static IReadOnlyList<string> CreateArguments(MediaProbeResult source, PhysicalTranscodeProfile profile, LineupSettings encoderSettings)
    {
        if (!ShouldNormalize(source, profile))
        {
            throw new ArgumentException("Shared source normalization requires at least one enabled physical-stage transform.", nameof(source));
        }

        var video = source.Tracks.First(track => track.Type == MediaTrackType.Video);
        var deinterlace = ShouldDeinterlace(video, profile.Deinterlace);
        var transcodeVideo = ShouldTranscodeVideo(video, profile);
        List<string> arguments =
        [
            "-hide_banner", "-loglevel", "warning",
            "-analyzeduration", "10000000", "-probesize", "10000000",
            "-fflags", "+genpts"
        ];
        if (deinterlace)
        {
            arguments.AddRange(["-copyts", "-start_at_zero"]);
        }
        arguments.AddRange([
            "-i", "pipe:0",
            "-map", "0:v:0?", "-map", "0:a?", "-map_metadata", "0",
            "-c", "copy"
        ]);
        foreach (var subtitle in source.Tracks.Where(track => track.Type == MediaTrackType.Subtitle && SubtitleCapabilityPolicy.CanCopyToMpegTs(track.Codec)))
        {
            arguments.AddRange(["-map", $"0:{subtitle.Index}"]);
        }

        if (transcodeVideo)
        {
            arguments.AddRange(
            [
                "-c:v:0", "libx264",
                "-preset:v:0", profile.VideoPreset.ToString().ToLowerInvariant(),
                "-tune:v:0", "zerolatency",
                "-crf:v:0", profile.VideoQuality.ToString(),
                "-maxrate:v:0", $"{profile.MaximumVideoBitRateMbps}M",
                "-bufsize:v:0", $"{profile.MaximumVideoBitRateMbps * 2}M",
                "-profile:v:0", "high",
                "-level:v:0", "4.2",
                "-pix_fmt:v:0", "yuv420p",
                "-a53cc:v:0", "1",
                "-flags:v:0", "+cgop",
                "-g:v:0", "120",
                "-keyint_min:v:0", "60",
                "-sc_threshold:v:0", "0",
                "-force_key_frames:v:0", "expr:gte(t,n_forced*2)",
                "-x264-params:v:0", "repeat-headers=1"
            ]);
            if (deinterlace)
            {
                arguments.AddRange([
                    "-filter:v:0", CreateFilter(profile.Deinterlace),
                    "-fps_mode:v:0", "passthrough"
                ]);
            }
        }

        var audioTracks = source.Tracks.Where(track => track.Type == MediaTrackType.Audio).ToArray();
        for (var index = 0; index < audioTracks.Length; index++)
        {
            var codec = ResolveAudioCodec(audioTracks[index].Codec, profile);
            if (codec == "copy")
            {
                continue;
            }

            arguments.AddRange([$"-c:a:{index}", codec, $"-b:a:{index}", codec == "ac3" ? "448k" : "640k"]);
            if (audioTracks[index].Channels is > 6)
            {
                arguments.AddRange([$"-ac:a:{index}", "6"]);
            }
        }

        arguments.AddRange([
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
    public static string CreateFilter(SourceDeinterlacePolicy mode) =>
        mode switch
        {
            SourceDeinterlacePolicy.SourceFrameRate => "bwdif=mode=send_frame:parity=auto:deint=interlaced",
            SourceDeinterlacePolicy.SourceFieldRate => "bwdif=mode=send_field:parity=auto:deint=interlaced",
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Deinterlacing must be enabled to create a filter.")
        };

    private static bool ShouldTranscodeVideo(MediaTrackMetadata video, PhysicalTranscodeProfile profile) =>
        ShouldDeinterlace(video, profile.Deinterlace)
        || profile.ConvertHevcToH264 && string.Equals(video.Codec, "hevc", StringComparison.OrdinalIgnoreCase);

    private static string ResolveAudioCodec(string codec, PhysicalTranscodeProfile profile)
    {
        var policy = string.Equals(codec, "ac4", StringComparison.OrdinalIgnoreCase) ? profile.Ac4Audio : profile.Audio;
        return policy switch
        {
            OutputAudioPolicy.Ac3 => "ac3",
            OutputAudioPolicy.Eac3 => "eac3",
            _ => "copy"
        };
    }

    private static PhysicalTranscodeProfile ToPhysicalProfile(LineupSettings settings) => settings.Streaming.PhysicalDefaults;
}
