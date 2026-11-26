namespace Lineup.Core.Streaming.FFmpeg;

/// <summary>Projects the mapped video and audio operations of Lineup's single-output MPEG-TS argument planners.</summary>
public static class MpegTsProcessingDiagnostics
{
    /// <summary>Describes the effective arguments actually launched, including any software fallback.</summary>
    public static StreamProcessingSnapshot Create(MediaProbeResult source, IReadOnlyList<string> arguments)
    {
        var firstVideoOnly = arguments.Contains("0:v:0?");
        var tracks = new List<ActiveStreamTrack>();
        foreach (var type in new[] { MediaTrackType.Video, MediaTrackType.Audio })
        {
            var selected = source.Tracks.Where(track => track.Type == type);
            if (type == MediaTrackType.Video && firstVideoOnly) selected = selected.Take(1);
            var ordinal = 0;
            foreach (var track in selected)
            {
                var specifier = type == MediaTrackType.Video ? "v" : "a";
                string? encoder = null;
                for (var index = 0; index + 1 < arguments.Count; index++)
                {
                    if (arguments[index] == "-c" || arguments[index] == $"-c:{specifier}" || arguments[index] == $"-c:{specifier}:{ordinal}")
                    {
                        encoder = arguments[index + 1];
                    }
                }
                if (encoder is null) throw new InvalidOperationException("The MPEG-TS processing plan has no explicit codec operation.");
                var codec = encoder == "libx264" || encoder.StartsWith("h264_", StringComparison.Ordinal) ? "h264" : encoder;
                tracks.Add(new(type, track.Codec, codec, track.BitRate, null, track.Width, track.Height, track.Channels,
                    codec == "copy" ? track.Channels : null, codec == "copy" ? track.SampleRate : null)
                {
                    SourceIndex = track.Index,
                    Language = track.Language,
                    Title = track.Title
                });
                ordinal++;
            }
        }
        return new(StreamTransportOperation.Remux, tracks)
        {
            VideoKnown = source.Tracks.Count > 0,
            AudioKnown = source.Tracks.Any(track => track.Type == MediaTrackType.Audio) || source.AudioDiscovery == AudioDiscoveryStatus.DeclaredAbsent
        };
    }
}
