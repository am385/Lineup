namespace Lineup.Web.Services;

/// <summary>
/// Identifies legacy AAC fallback values retained only for browser-preference and request migration.
/// </summary>
public enum CmafLegacyAacFallback
{
    /// <summary>Uses stereo AAC.</summary>
    Stereo,

    /// <summary>Uses AAC with up to 5.1 channels.</summary>
    UpTo5Point1,

    /// <summary>Uses AAC with up to 7.1 channels.</summary>
    UpTo7Point1,

    /// <summary>Represents the unsupported historical source-passthrough value.</summary>
    Source
}

/// <summary>
/// Stores browser-local defaults shared by the Watch and Watch Test pages.
/// </summary>
public sealed record CmafWatchPreferences
{
    /// <summary>Gets the browser-local storage key.</summary>
    public const string StorageKey = "lineup-watch-cmaf-preferences-v1";

    /// <summary>Gets the current explicit Auto/Source/Fallback policy-selection version.</summary>
    public const int CurrentPolicyVersion = 1;

    /// <summary>Gets the policy-selection version, or zero for preferences saved before Auto existed.</summary>
    public int PolicyVersion { get; init; }

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

    /// <summary>Gets the legacy AAC fallback value retained for stored-data migration.</summary>
    public CmafLegacyAacFallback? AacFallback { get; init; }

    /// <summary>Gets the persisted request-scoped manual stream overrides.</summary>
    public CmafStreamOverrides Overrides { get; init; } = new();

    /// <summary>Gets the last selected source subtitle track.</summary>
    public int? SubtitleTrack { get; init; }
}
