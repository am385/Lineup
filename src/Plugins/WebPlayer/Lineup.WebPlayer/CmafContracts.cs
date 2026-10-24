using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;

namespace Lineup.WebPlayer;

/// <summary>Identifies a manifest protocol exposed by a shared CMAF presentation.</summary>
public enum CmafProtocol
{
    /// <summary>Dynamic Adaptive Streaming over HTTP.</summary>
    Dash,
    /// <summary>HTTP Live Streaming.</summary>
    Hls
}

/// <summary>Selects the client playback protocol, including automatic DASH-to-HLS fallback.</summary>
public enum CmafPlaybackProtocol
{
    /// <summary>Prefers DASH and permits HLS fallback.</summary>
    Auto,
    /// <summary>Uses HLS only.</summary>
    Hls,
    /// <summary>Uses DASH only.</summary>
    Dash
}

/// <summary>Selects the audio policy for a shared CMAF presentation.</summary>
public enum CmafPreferredAudio
{
    /// <summary>Copies an eligible source codec and otherwise uses the configured fallback.</summary>
    Source,
    /// <summary>Always uses the configured fallback output.</summary>
    Fallback,
    /// <summary>Uses measured browser capabilities to choose source copy or one fallback.</summary>
    Auto
}

/// <summary>Selects the video policy for a shared CMAF presentation.</summary>
public enum CmafPreferredVideo
{
    /// <summary>Copies eligible source video.</summary>
    Source,
    /// <summary>Uses an H.264 compatibility output.</summary>
    Fallback,
    /// <summary>Uses measured browser capabilities to choose source copy or H.264.</summary>
    Auto
}

/// <summary>Selects the codec and channel limit for a compatibility audio rendition.</summary>
public enum CmafFallbackAudio
{
    /// <summary>Encodes broadly compatible stereo AAC.</summary>
    AacStereo,
    /// <summary>Encodes AAC matching the source up to 5.1 channels.</summary>
    AacUpTo5Point1,
    /// <summary>Encodes AAC matching the source up to 7.1 channels.</summary>
    AacUpTo7Point1,
    /// <summary>Encodes AC-3 matching the source up to 5.1 channels.</summary>
    Ac3,
    /// <summary>Encodes channel-based E-AC-3 matching the source up to 5.1 channels.</summary>
    Eac3
}

/// <summary>Describes optional per-setting manual overrides applied over a measured browser profile.</summary>
public sealed record CmafStreamOverrides
{
    /// <summary>Gets whether manual stream-setting overrides are enabled.</summary>
    public bool Enabled { get; init; }
    /// <summary>Gets the explicit protocol.</summary>
    public CmafPlaybackProtocol? Protocol { get; init; }
    /// <summary>Gets the explicit quality cap.</summary>
    public WebPlayerQuality? Quality { get; init; }
    /// <summary>Gets the explicit video policy.</summary>
    public CmafPreferredVideo? Video { get; init; }
    /// <summary>Gets the explicit audio policy.</summary>
    public CmafPreferredAudio? Audio { get; init; }
    /// <summary>Gets the explicit fallback profile.</summary>
    public CmafFallbackAudio? FallbackAudio { get; init; }
}

/// <summary>Configures creation of one shared CMAF DASH/HLS presentation.</summary>
public sealed record CmafStreamRequest
{
    /// <summary>Gets the selected video quality.</summary>
    public WebPlayerQuality Quality { get; init; } = WebPlayerQuality.AppDefault;
    /// <summary>Gets the requested source-video or H.264 fallback policy.</summary>
    public CmafPreferredVideo PreferredVideo { get; init; } = CmafPreferredVideo.Source;
    /// <summary>Gets the absolute selected source audio stream index.</summary>
    public int? AudioTrack { get; init; }
    /// <summary>Gets the absolute selected source subtitle stream index.</summary>
    public int? SubtitleTrack { get; init; }
    /// <summary>Gets the requested source-audio or fallback policy.</summary>
    public CmafPreferredAudio PreferredAudio { get; init; } = CmafPreferredAudio.Source;
    /// <summary>Gets the codec and channel limit used when source audio is not CMAF-copy eligible.</summary>
    public CmafFallbackAudio? FallbackAudio { get; init; }
    /// <summary>Gets the previously validated subtitle presentation used after an incomplete retry probe.</summary>
    public SubtitlePresentation? SubtitlePresentation { get; init; }
    /// <summary>Gets whether the selected retry subtitle represents embedded captions.</summary>
    public bool EmbeddedCaptions { get; init; }
    /// <summary>Gets the optional owning web-player identifier.</summary>
    public string? ClientId { get; init; }
    /// <summary>Gets the optional completed browser compatibility profile used by Auto policies.</summary>
    public CmafCompatibilityProfile? CompatibilityProfile { get; init; }
    /// <summary>Gets optional manual settings applied over the measured compatibility profile.</summary>
    public CmafStreamOverrides Overrides { get; init; } = new();
}

/// <summary>Describes a started shared CMAF presentation.</summary>
/// <param name="SessionId">The stream session identifier.</param>
/// <param name="HlsManifestUrl">The shared HLS master-playlist URL.</param>
/// <param name="DashManifestUrl">The DASH MPD URL.</param>
public sealed record CmafStreamResponse(string SessionId, string HlsManifestUrl, string DashManifestUrl)
{
    /// <summary>Gets the protocols backed by the shared fragments.</summary>
    public IReadOnlyList<CmafProtocol> Protocols { get; init; } = [CmafProtocol.Dash, CmafProtocol.Hls];
    /// <summary>Gets whether source audio required a fallback-only startup retry.</summary>
    public bool SourceAudioFallbackApplied { get; init; }
    /// <summary>Gets the configured fallback rendition title.</summary>
    public string? FallbackAudioTitle { get; init; }
    /// <summary>Gets the configured fallback rendition codec.</summary>
    public string? FallbackAudioCodec { get; init; }
    /// <summary>Gets browser-selectable subtitle sidecars.</summary>
    public IReadOnlyList<CmafSubtitleRendition> Subtitles { get; init; } = [];
    /// <summary>Gets whether this presentation includes copied source video.</summary>
    public bool HasSourceVideoRendition { get; init; }
    /// <summary>Gets the RFC 6381 codec string for copied source video.</summary>
    public string? SourceVideoCodec { get; init; }
}

/// <summary>Describes one browser-selectable CMAF WebVTT sidecar.</summary>
public sealed record CmafSubtitleRendition(int SourceIndex, string Label, string? Language, string Url, bool IsEmbeddedClosedCaptions);

/// <summary>Describes the effective CMAF audio output.</summary>
public sealed record CmafAudioPlan(bool CopySource, string Codec, int? Channels, long? BitRate, int? SampleRate, string Title);

/// <summary>Associates one packaged CMAF audio output with its source track.</summary>
public sealed record CmafAudioRendition(MediaTrackMetadata Source, CmafAudioPlan Plan);

/// <summary>Describes one CMAF video output.</summary>
public sealed record CmafVideoPlan(bool CopySource, string Codec, long? BitRate, string Title);
