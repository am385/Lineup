namespace Lineup.Web.Services;

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
    public static WatchTrackSelection SelectTracks(MediaProbeResult source, int? audioIndex, int? subtitleIndex)
    {
        var audioTracks = source.Tracks.Where(track => track.Type == MediaTrackType.Audio).ToArray();
        var audio = audioIndex.HasValue
            ? FindRequired(source, audioIndex.Value, MediaTrackType.Audio)
            : audioTracks.FirstOrDefault(track => track.IsDefault) ?? audioTracks.FirstOrDefault();
        var subtitle = subtitleIndex.HasValue
            ? FindRequired(source, subtitleIndex.Value, MediaTrackType.Subtitle)
            : null;
        if (subtitle?.SubtitlePresentation == SubtitlePresentation.Unsupported)
        {
            throw new ArgumentException(
                $"Subtitle stream index {subtitle.Index} uses unsupported codec '{subtitle.Codec}'.",
                nameof(subtitleIndex));
        }

        return new WatchTrackSelection(audio, subtitle, !audioIndex.HasValue && audioTracks.Length == 0);
    }

    /// <summary>Creates FFmpeg arguments for one selected audio and optional subtitle track.</summary>
    public static IReadOnlyList<string> CreateArguments(
        AppSettings settings,
        MediaProbeResult source,
        WatchTrackSelection selection,
        WebPlayerQuality quality,
        string? webVttPath)
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

        arguments.AddRange([
            "-c:a", "aac", "-b:a", "128k", "-ac", "2", "-ar", "44100",
            "-f", "mp4", "-movflags", "frag_keyframe+empty_moov+default_base_moof",
            "-frag_duration", "1000000", "pipe:1"
        ]);

        if (selection.SubtitlePresentation == SubtitlePresentation.WebVtt)
        {
            if (string.IsNullOrWhiteSpace(webVttPath))
            {
                throw new ArgumentException("A contained WebVTT path is required for text subtitles.", nameof(webVttPath));
            }

            arguments.AddRange(["-map", $"0:{selection.Subtitle!.Index}", "-c:s", "webvtt", "-f", "webvtt", "-y", webVttPath]);
        }

        return arguments;
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

    private static void AddVideoArguments(List<string> arguments, AppSettings settings, string? codec, WebPlayerQuality quality)
    {
        if (quality == WebPlayerQuality.AppDefault && string.Equals(codec, "h264", StringComparison.OrdinalIgnoreCase))
        {
            arguments.AddRange(["-c:v", "copy"]);
            return;
        }

        AddH264Arguments(arguments, settings, quality, addScale: true);
    }

    private static void AddH264Arguments(
        List<string> arguments,
        AppSettings settings,
        WebPlayerQuality quality,
        bool addScale)
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
