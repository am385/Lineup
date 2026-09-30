namespace Lineup.Web.Services;

/// <summary>
/// Describes validated per-client web-player track choices.
/// </summary>
public sealed record WebPlayerTrackSelection(MediaTrackMetadata? Audio, MediaTrackMetadata? Subtitle, bool UseDefaultAudioFallback = false)
{
    /// <summary>Gets the effective subtitle presentation.</summary>
    public SubtitlePresentation? SubtitlePresentation => Subtitle?.SubtitlePresentation;
}

/// <summary>
/// Validates web-player track choices and builds shared caption-extraction arguments.
/// </summary>
public static class WebPlayerTrackPlanner
{
    /// <summary>
    /// Validates source indexes and creates a selection with default audio and subtitles off.
    /// </summary>
    public static WebPlayerTrackSelection SelectTracks(
        MediaProbeResult source,
        int? audioIndex,
        int? subtitleIndex,
        SubtitlePresentation? retrySubtitlePresentation = null,
        bool retryEmbeddedClosedCaptions = false)
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

        return new WebPlayerTrackSelection(audio, subtitle, !audioIndex.HasValue && audioTracks.Length == 0);
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
}
