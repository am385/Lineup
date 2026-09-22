namespace Lineup.Web.Services;

/// <summary>
/// Specifies the browser audio output selected for a Watch session.
/// </summary>
public enum WatchAudioOutput
{
    /// <summary>Downmixes audio to two channels for broad browser compatibility.</summary>
    Stereo,

    /// <summary>Retains the source channel count up to six channels and normalizes the output layout to 5.1.</summary>
    UpTo5Point1,

    /// <summary>Retains the source channel count up to eight channels and normalizes the output layout to 7.1.</summary>
    UpTo7Point1
}

/// <summary>
/// Describes the planned AAC output for a Watch audio track.
/// </summary>
/// <param name="Channels">Output channel count.</param>
/// <param name="BitRate">Output bitrate in bits per second.</param>
/// <param name="SampleRate">Output sample rate in hertz.</param>
public sealed record WatchAudioOutputProfile(int Channels, int BitRate, int SampleRate);

/// <summary>
/// Describes validated per-client Watch track choices.
/// </summary>
public sealed record WatchTrackSelection(MediaTrackMetadata? Audio, MediaTrackMetadata? Subtitle, bool UseDefaultAudioFallback = false)
{
    /// <summary>Gets the effective subtitle presentation.</summary>
    public SubtitlePresentation? SubtitlePresentation => Subtitle?.SubtitlePresentation;
}

/// <summary>
/// Builds track-aware FFmpeg arguments for fragmented MP4 Watch output.
/// </summary>
public static class WatchStreamPlanner
{
    /// <summary>
    /// Validates source indexes and creates a selection with default audio and subtitles off.
    /// </summary>
    public static WatchTrackSelection SelectTracks(MediaProbeResult source, int? audioIndex, int? subtitleIndex, SubtitlePresentation? retrySubtitlePresentation = null, bool retryEmbeddedClosedCaptions = false)
    {
        var audioTracks = source.Tracks.Where(track => track.Type == MediaTrackType.Audio).ToArray();
        MediaTrackMetadata? audio;
        if (audioIndex.HasValue)
        {
            audio = source.Tracks.Count == 0
                ? new MediaTrackMetadata(audioIndex.Value, MediaTrackType.Audio, "unknown", null, null, null, null, null)
                : FindRequired(source, audioIndex.Value, MediaTrackType.Audio);
        }
        else
        {
            audio = audioTracks.FirstOrDefault(track => track.IsDefault) ?? audioTracks.FirstOrDefault();
        }

        MediaTrackMetadata? subtitle = null;
        if (subtitleIndex.HasValue)
        {
            var embeddedCaptionsMissingFromRetryProbe = retryEmbeddedClosedCaptions &&
                !source.Tracks.Any(track => track.Index == subtitleIndex.Value && track.Type == MediaTrackType.Subtitle);
            subtitle = source.Tracks.Count == 0 || embeddedCaptionsMissingFromRetryProbe
                ? CreateRetrySubtitle(subtitleIndex.Value, retrySubtitlePresentation, retryEmbeddedClosedCaptions)
                : FindRequired(source, subtitleIndex.Value, MediaTrackType.Subtitle);
        }

        if (subtitle?.SubtitlePresentation == SubtitlePresentation.Unsupported)
        {
            throw new ArgumentException($"Subtitle stream index {subtitle.Index} uses unsupported codec '{subtitle.Codec}'.", nameof(subtitleIndex));
        }

        return new WatchTrackSelection(audio, subtitle, !audioIndex.HasValue && audioTracks.Length == 0);
    }

    /// <summary>Creates FFmpeg arguments for one selected audio and optional subtitle track.</summary>
    public static IReadOnlyList<string> CreateArguments(
        AppSettings settings,
        MediaProbeResult source,
        WatchTrackSelection selection,
        WebPlayerQuality quality,
        string? webVttPath,
        WatchAudioOutput audioOutput = WatchAudioOutput.Stereo)
    {
        var video = source.Tracks.FirstOrDefault(track => track.Type == MediaTrackType.Video);
        var burnIn = selection.SubtitlePresentation == SubtitlePresentation.BurnIn;
        List<string> arguments =
        [
            "-hide_banner", "-loglevel", "info",
            "-analyzeduration", "1000000", "-probesize", "1000000",
            "-fflags", "+genpts", "-i", "pipe:0"
        ];

        if (burnIn)
        {
            var filter = quality switch
            {
                WebPlayerQuality.Medium => $"[0:v:0][0:{selection.Subtitle!.Index}]overlay,scale=-2:min(720\\,ih)[v]",
                WebPlayerQuality.Low => $"[0:v:0][0:{selection.Subtitle!.Index}]overlay,scale=-2:min(480\\,ih)[v]",
                _ => $"[0:v:0][0:{selection.Subtitle!.Index}]overlay[v]"
            };
            arguments.AddRange([
                "-filter_complex", filter,
                "-map", "[v]"
            ]);
            AddH264Arguments(arguments, settings, quality, addScale: false);
        }
        else
        {
            arguments.AddRange(["-map", "0:v:0?"]);
            AddVideoArguments(arguments, settings, video?.Codec, quality);
        }

        if (selection.Audio is not null)
        {
            arguments.AddRange(["-map", $"0:{selection.Audio.Index}"]);
        }
        else if (selection.UseDefaultAudioFallback)
        {
            arguments.AddRange(["-map", "0:a:0?"]);
        }

        AddAudioArguments(arguments, selection, audioOutput);
        arguments.AddRange([
            "-f", "mp4", "-movflags", "frag_keyframe+empty_moov+default_base_moof",
            "-frag_duration", "1000000", "pipe:1"
        ]);

        if (selection.SubtitlePresentation == SubtitlePresentation.WebVtt && selection.Subtitle?.IsEmbeddedClosedCaptions != true)
        {
            if (string.IsNullOrWhiteSpace(webVttPath))
            {
                throw new ArgumentException("A contained WebVTT path is required for text subtitles.", nameof(webVttPath));
            }

            arguments.AddRange(["-map", $"0:{selection.Subtitle!.Index}", "-c:s", "webvtt", "-f", "webvtt", "-y", webVttPath]);
        }

        return arguments;
    }

    /// <summary>Creates FFmpeg arguments that extract embedded ATSC captions from standard input as WebVTT.</summary>
    public static IReadOnlyList<string> CreateEmbeddedCaptionArguments(string webVttPath)
    {
        if (string.IsNullOrWhiteSpace(webVttPath))
        {
            throw new ArgumentException("A contained WebVTT path is required for embedded captions.", nameof(webVttPath));
        }

        return
        [
            "-hide_banner", "-loglevel", "warning",
            "-f", "lavfi", "-i", "movie='pipe\\:0'[out+subcc]",
            "-map", "0:s:0", "-c:s", "webvtt",
            "-flush_packets", "1", "-f", "webvtt", "-y", webVttPath
        ];
    }

    /// <summary>
    /// Creates the AAC output profile for the selected mode and source channel count.
    /// </summary>
    public static WatchAudioOutputProfile GetAudioOutputProfile(WatchAudioOutput audioOutput, int? sourceChannels)
    {
        var outputChannels = audioOutput switch
        {
            WatchAudioOutput.Stereo => 2,
            WatchAudioOutput.UpTo5Point1 => sourceChannels.HasValue ? Math.Min(sourceChannels.Value, 6) : 2,
            WatchAudioOutput.UpTo7Point1 => sourceChannels.HasValue ? Math.Min(sourceChannels.Value, 8) : 2,
            _ => throw new ArgumentOutOfRangeException(nameof(audioOutput), audioOutput, "Unsupported Watch audio output.")
        };
        var bitRate = Math.Max(outputChannels, 2) * 64_000;
        return new WatchAudioOutputProfile(outputChannels, bitRate, 48_000);
    }

    private static void AddAudioArguments(List<string> arguments, WatchTrackSelection selection, WatchAudioOutput audioOutput)
    {
        var profile = GetAudioOutputProfile(audioOutput, selection.Audio?.Channels);
        arguments.AddRange([
            "-c:a", "aac",
            "-b:a", $"{profile.BitRate / 1_000}k",
            "-ar", profile.SampleRate.ToString(),
            "-ac", profile.Channels.ToString()
        ]);
    }

    private static MediaTrackMetadata FindRequired(MediaProbeResult source, int index, MediaTrackType type)
    {
        var track = source.Tracks.FirstOrDefault(candidate => candidate.Index == index);
        if (track is null)
        {
            throw new ArgumentException($"Source stream index {index} was not found.");
        }

        return track.Type == type
            ? track
            : throw new ArgumentException($"Source stream index {index} is {track.Type}, not {type}.");
    }

    private static MediaTrackMetadata CreateRetrySubtitle(int index, SubtitlePresentation? retrySubtitlePresentation, bool retryEmbeddedClosedCaptions)
    {
        if (retrySubtitlePresentation is not (SubtitlePresentation.WebVtt or SubtitlePresentation.BurnIn))
        {
            throw new ArgumentException("A supported subtitle presentation is required when source tracks could not be probed.", nameof(retrySubtitlePresentation));
        }
        if (retryEmbeddedClosedCaptions && retrySubtitlePresentation != SubtitlePresentation.WebVtt)
        {
            throw new ArgumentException("Embedded closed captions require WebVTT presentation.", nameof(retryEmbeddedClosedCaptions));
        }

        return new MediaTrackMetadata(index, MediaTrackType.Subtitle, "unknown", null, null, null, null, null)
        {
            IsEmbeddedClosedCaptions = retryEmbeddedClosedCaptions,
            SubtitlePresentation = retrySubtitlePresentation.Value
        };
    }

    private static void AddVideoArguments(List<string> arguments, AppSettings settings, string? codec, WebPlayerQuality quality)
    {
        if (quality == WebPlayerQuality.AppDefault && string.Equals(codec, "h264", StringComparison.OrdinalIgnoreCase))
        {
            arguments.AddRange(["-c:v", "copy"]);
            return;
        }

        AddH264Arguments(arguments, settings, quality, addScale: true);
    }

    private static void AddH264Arguments(List<string> arguments, AppSettings settings, WebPlayerQuality quality, bool addScale)
    {
        var maximumBitRate = WebVideoTranscodePlanner.GetMaximumBitRate(settings, quality) / 1_000_000;
        var crf = quality switch
        {
            WebPlayerQuality.Medium => 23,
            WebPlayerQuality.Low => 25,
            _ => settings.WebVideoQuality
        };
        arguments.AddRange([
            "-c:v", "libx264", "-preset", settings.WebVideoPreset.ToString().ToLowerInvariant(),
            "-tune", "zerolatency", "-crf", crf.ToString(),
            "-maxrate", $"{maximumBitRate}M", "-bufsize", $"{maximumBitRate * 2}M",
            "-profile:v", "high", "-level", "4.2", "-pix_fmt", "yuv420p",
            "-flags", "+cgop", "-g", "120", "-keyint_min", "60", "-sc_threshold", "0",
            "-force_key_frames", "expr:gte(t,n_forced*2)"
        ]);
        if (addScale && quality is (WebPlayerQuality.Medium or WebPlayerQuality.Low))
        {
            arguments.AddRange(["-vf", quality == WebPlayerQuality.Medium ? "scale=-2:min(720\\,ih)" : "scale=-2:min(480\\,ih)"]);
        }
    }
}
