namespace Lineup.Streaming.FFmpeg;

/// <summary>
/// Describes an audio stream discovered in an input media source.
/// </summary>
/// <param name="Index">The input stream index reported by FFprobe.</param>
/// <param name="CodecName">The FFmpeg codec name.</param>
/// <param name="Channels">The number of audio channels.</param>
public sealed record AudioStreamInfo(int Index, string CodecName, int Channels);

/// <summary>
/// Represents an FFprobe or FFmpeg failure while producing an MPEG-TS stream.
/// </summary>
public sealed class MpegTsTranscodeException : Exception
{
    /// <summary>
    /// Initializes the exception with an error message.
    /// </summary>
    /// <param name="message">The error message.</param>
    public MpegTsTranscodeException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes the exception with an error message and underlying exception.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The exception that caused the transcode failure.</param>
    public MpegTsTranscodeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Builds deterministic FFprobe and FFmpeg argument lists for MPEG-TS audio transcoding.
/// </summary>
public static class MpegTsTranscodePlanner
{
    /// <summary>
    /// Creates FFmpeg arguments that copy video and apply per-track audio codec decisions.
    /// </summary>
    /// <param name="inputUri">The live media source.</param>
    /// <param name="sourceVideoCodec">The detected source video codec.</param>
    /// <param name="audioStreams">Audio stream metadata in output mapping order.</param>
    /// <param name="settings">The current video and audio transcode settings.</param>
    /// <param name="subtitleStreams">Detected standalone subtitle streams.</param>
    /// <returns>The FFmpeg argument list.</returns>
    public static IReadOnlyList<string> CreateFfmpegArguments(
        Uri inputUri,
        string? sourceVideoCodec,
        IReadOnlyList<AudioStreamInfo> audioStreams,
        LineupSettings settings,
        IReadOnlyList<MediaTrackMetadata>? subtitleStreams = null)
        => CreateFfmpegArgumentsCore(
            inputUri,
            sourceVideoCodec,
            audioStreams,
            settings.Streaming.VirtualDefaults,
            subtitleStreams,
            false,
            codec => ResolveAudioCodec(codec, settings.Streaming.VirtualDefaults));

    /// <summary>Creates FFmpeg arguments using an effective topology virtual profile.</summary>
    public static IReadOnlyList<string> CreateFfmpegArguments(
        Uri inputUri,
        string? sourceVideoCodec,
        IReadOnlyList<AudioStreamInfo> audioStreams,
        VirtualTranscodeProfile profile,
        LineupSettings encoderSettings,
        IReadOnlyList<MediaTrackMetadata>? subtitleStreams = null,
        bool sourceVideoIsInterlaced = false)
        => CreateFfmpegArgumentsCore(
            inputUri,
            sourceVideoCodec,
            audioStreams,
            profile,
            subtitleStreams,
            sourceVideoIsInterlaced,
            codec => string.Equals(codec, "ac4", StringComparison.OrdinalIgnoreCase)
                ? profile.Ac4Audio switch
                {
                    OutputAudioPolicy.Ac3 => "ac3",
                    OutputAudioPolicy.Eac3 => "eac3",
                    _ => "copy"
                }
                : profile.Audio switch
                {
                    OutputAudioPolicy.Ac3 => "ac3",
                    OutputAudioPolicy.Eac3 => "eac3",
                    _ => "copy"
                });

    private static IReadOnlyList<string> CreateFfmpegArgumentsCore(
        Uri inputUri,
        string? sourceVideoCodec,
        IReadOnlyList<AudioStreamInfo> audioStreams,
        VirtualTranscodeProfile profile,
        IReadOnlyList<MediaTrackMetadata>? subtitleStreams,
        bool sourceVideoIsInterlaced,
        Func<string, string> resolveAudioCodec)
    {
        List<string> arguments =
        [
            "-hide_banner",
            "-loglevel", "warning",
            "-analyzeduration", "1000000",
            "-probesize", "1000000",
            "-fflags", "+genpts",
            "-i", inputUri.AbsoluteUri,
            "-map", "0:v?",
            "-map", "0:a?",
            "-map_metadata", "0",
            "-c", "copy"
        ];

        foreach (var subtitle in subtitleStreams ?? [])
        {
            if (subtitle.Type != MediaTrackType.Subtitle)
            {
                throw new ArgumentException($"Stream index {subtitle.Index} is not a subtitle stream.", nameof(subtitleStreams));
            }

            if (SubtitleCapabilityPolicy.CanCopyToMpegTs(subtitle.Codec))
            {
                arguments.AddRange(["-map", $"0:{subtitle.Index}"]);
            }
        }

        var deinterlace = sourceVideoIsInterlaced && profile.Deinterlace != SourceDeinterlacePolicy.Preserve;
        if (deinterlace)
        {
            var inputIndex = arguments.IndexOf("-i");
            arguments.InsertRange(inputIndex, ["-copyts", "-start_at_zero"]);
        }
        if (deinterlace || profile.ConvertHevcToH264 && string.Equals(sourceVideoCodec, "hevc", StringComparison.OrdinalIgnoreCase))
        {
            arguments.AddRange(
            [
                "-c:v:0", "libx264",
                "-preset", profile.VideoPreset.ToString().ToLowerInvariant(),
                "-tune", "zerolatency",
                "-crf", profile.VideoQuality.ToString(),
                "-maxrate", $"{profile.MaximumVideoBitRateMbps}M",
                "-bufsize", $"{profile.MaximumVideoBitRateMbps * 2}M",
                "-profile:v:0", "high",
                "-level:v:0", "4.2",
                "-pix_fmt:v:0", "yuv420p",
                "-a53cc:v:0", "1",
                "-flags:v:0", "+cgop",
                "-g:v:0", "120",
                "-keyint_min:v:0", "60",
                "-sc_threshold:v:0", "0",
                "-x264-params:v:0", "repeat-headers=1"
            ]);
            if (deinterlace)
            {
                arguments.AddRange([
                    "-filter:v:0", SourceDeinterlacePlanner.CreateFilter(profile.Deinterlace),
                    "-fps_mode:v:0", "passthrough"
                ]);
            }
        }

        for (var audioIndex = 0; audioIndex < audioStreams.Count; audioIndex++)
        {
            var stream = audioStreams[audioIndex];
            var codec = resolveAudioCodec(stream.CodecName);
            if (codec == "copy")
            {
                continue;
            }

            arguments.Add($"-c:a:{audioIndex}");
            arguments.Add(codec);
            arguments.Add($"-b:a:{audioIndex}");
            arguments.Add(codec == "ac3" ? "448k" : "640k");

            if (stream.Channels > 6)
            {
                arguments.Add($"-ac:a:{audioIndex}");
                arguments.Add("6");
            }
        }

        arguments.AddRange(["-max_muxing_queue_size", "1024", "-mpegts_flags", "+resend_headers", "-muxdelay", "0", "-f", "mpegts", "pipe:1"]);

        return arguments;
    }

    private static string ResolveAudioCodec(string codec, VirtualTranscodeProfile profile)
    {
        var policy = string.Equals(codec, "ac4", StringComparison.OrdinalIgnoreCase) ? profile.Ac4Audio : profile.Audio;
        return policy switch
        {
            OutputAudioPolicy.Ac3 => "ac3",
            OutputAudioPolicy.Eac3 => "eac3",
            _ => "copy"
        };
    }
}
