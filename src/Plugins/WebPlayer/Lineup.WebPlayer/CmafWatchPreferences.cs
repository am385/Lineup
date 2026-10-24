namespace Lineup.WebPlayer;

/// <summary>
/// Stores browser-local defaults shared by the Watch and Watch Test pages.
/// </summary>
public sealed record CmafWatchPreferences
{
    /// <summary>Gets the browser-local storage key.</summary>
    public const string StorageKey = "lineup-watch-cmaf-preferences-v2";

    /// <summary>Gets the preferred playback protocol.</summary>
    public CmafPlaybackProtocol Protocol { get; init; }

    /// <summary>Gets the preferred video quality.</summary>
    public WebPlayerQuality Quality { get; init; }

    /// <summary>Gets the source-video or H.264 fallback preference.</summary>
    public CmafPreferredVideo PreferredVideo { get; init; }

    /// <summary>Gets the source-audio or encoded-fallback preference.</summary>
    public CmafPreferredAudio PreferredAudio { get; init; }

    /// <summary>Gets the encoded fallback-audio profile.</summary>
    public CmafFallbackAudio? FallbackAudio { get; init; }

    /// <summary>Gets the persisted request-scoped manual stream overrides.</summary>
    public CmafStreamOverrides Overrides { get; init; } = new();

    /// <summary>Gets the last selected source subtitle track.</summary>
    public int? SubtitleTrack { get; init; }
}
