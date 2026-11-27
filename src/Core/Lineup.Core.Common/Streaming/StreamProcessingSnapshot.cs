using System.Text.Json.Serialization;

namespace Lineup.Core.Common.Streaming;

/// <summary>Identifies how a producer handles its output transport independently of codec processing.</summary>
public enum StreamTransportOperation
{
    /// <summary>The producer has not reported its transport operation.</summary>
    Unknown,
    /// <summary>The producer forwards the existing transport without rebuilding it.</summary>
    PassThrough,
    /// <summary>The producer rebuilds the container or transport, with or without codec conversion.</summary>
    Remux
}

/// <summary>Reports effective output mappings for one processing stage, independently of saved policy.</summary>
public sealed record StreamProcessingSnapshot(StreamTransportOperation Transport, IReadOnlyList<ActiveStreamTrack> Tracks)
{
    /// <summary>Gets whether the producer's output mapping establishes video presence or absence.</summary>
    public bool VideoKnown { get; init; }

    /// <summary>Gets whether the producer's output mapping establishes audio presence or absence.</summary>
    public bool AudioKnown { get; init; }

    /// <summary>Gets whether at least one selected video or audio rendition is re-encoded.</summary>
    [JsonIgnore]
    public bool Transcodes => Tracks.Any(track => track.IsSelected && track.Type is MediaTrackType.Video or MediaTrackType.Audio && track.OutputCodec is not ("copy" or "not-mapped"));

    /// <summary>Creates explicit copy mappings for a stage known to forward its input unchanged.</summary>
    public static StreamProcessingSnapshot Forward(ActiveStreamMediaSnapshot? media) =>
        new(StreamTransportOperation.PassThrough, media?.Tracks.Where(track => track.Type is MediaTrackType.Video or MediaTrackType.Audio)
            .Select(track => new ActiveStreamTrack(track.Type, track.Codec, "copy", track.BitRate, track.BitRate,
                track.Width, track.Height, track.Channels, track.Channels, track.SampleRate)
            {
                SourceIndex = track.Index,
                Language = track.Language,
                Title = track.Title
            }).ToArray() ?? [])
        {
            VideoKnown = media?.Tracks.Count > 0,
            AudioKnown = media?.Tracks.Any(track => track.Type == MediaTrackType.Audio) == true || media?.AudioDiscovery == AudioDiscoveryStatus.DeclaredAbsent
        };
}
