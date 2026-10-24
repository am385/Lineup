namespace Lineup.Common.Streaming;

/// <summary>Identifies a media track type.</summary>
public enum MediaTrackType
{
    /// <summary>Video track.</summary>
    Video,
    /// <summary>Audio track.</summary>
    Audio,
    /// <summary>Subtitle or caption track.</summary>
    Subtitle
}

/// <summary>Identifies the scan type reported for a video track.</summary>
public enum VideoScanType
{
    /// <summary>The source did not report a usable scan type.</summary>
    Unknown,
    /// <summary>The source contains complete progressive frames.</summary>
    Progressive,
    /// <summary>The source contains temporally distinct interlaced fields.</summary>
    Interlaced
}

/// <summary>Identifies how a subtitle can be presented by a virtual output.</summary>
public enum SubtitlePresentation
{
    /// <summary>The codec cannot be delivered by the output.</summary>
    Unsupported,
    /// <summary>The codec can be converted to browser WebVTT.</summary>
    WebVtt,
    /// <summary>The bitmap codec must be burned into video.</summary>
    BurnIn
}

/// <summary>Describes a media track reported by a source probe.</summary>
/// <param name="Index">The input stream index.</param>
/// <param name="Type">The media track type.</param>
/// <param name="Codec">The codec name.</param>
/// <param name="BitRate">The reported source bitrate in bits per second.</param>
/// <param name="Width">The video width in pixels.</param>
/// <param name="Height">The video height in pixels.</param>
/// <param name="Channels">The audio channel count.</param>
/// <param name="SampleRate">The audio sample rate in hertz.</param>
public sealed partial record MediaTrackMetadata(int Index, MediaTrackType Type, string Codec, long? BitRate, int? Width, int? Height, int? Channels, int? SampleRate);

/// <summary>Provides optional media-track details reported by a source probe.</summary>
public sealed partial record MediaTrackMetadata
{
    /// <summary>Gets the codec profile.</summary>
    public string? Profile { get; init; }
    /// <summary>Gets the codec level.</summary>
    public int? Level { get; init; }
    /// <summary>Gets the ISO language tag.</summary>
    public string? Language { get; init; }
    /// <summary>Gets the source track title.</summary>
    public string? Title { get; init; }
    /// <summary>Gets whether the source marks this track as default.</summary>
    public bool IsDefault { get; init; }
    /// <summary>Gets whether the source marks this track as forced.</summary>
    public bool IsForced { get; init; }
    /// <summary>Gets whether the source marks this track for hearing-impaired viewers.</summary>
    public bool IsHearingImpaired { get; init; }
    /// <summary>Gets whether a video track reports embedded closed captions.</summary>
    public bool HasClosedCaptions { get; init; }
    /// <summary>Gets the scan type reported for a video track.</summary>
    public VideoScanType ScanType { get; init; }
    /// <summary>Gets whether this synthetic subtitle track represents captions embedded in a video stream.</summary>
    public bool IsEmbeddedClosedCaptions { get; init; }
    /// <summary>Gets the supported subtitle presentation.</summary>
    public SubtitlePresentation SubtitlePresentation { get; init; }
}

/// <summary>Describes all media tracks and the overall source bitrate reported by a source probe.</summary>
/// <param name="Tracks">The discovered media tracks.</param>
/// <param name="BitRate">The reported overall source bitrate in bits per second.</param>
public sealed record MediaProbeResult(IReadOnlyList<MediaTrackMetadata> Tracks, long? BitRate);
