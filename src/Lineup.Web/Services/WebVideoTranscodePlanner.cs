namespace Lineup.Web.Services;

/// <summary>
/// Selects an optional per-session Watch player quality override.
/// </summary>
public enum WebPlayerQuality
{
    /// <summary>Uses persisted application defaults and permits H.264 passthrough.</summary>
    AppDefault,

    /// <summary>Uses a high-quality 1080p-oriented transcode.</summary>
    High,

    /// <summary>Uses a bandwidth-conscious 720p transcode.</summary>
    Medium,

    /// <summary>Uses a low-bandwidth 480p transcode.</summary>
    Low
}

/// <summary>
/// Provides shared video constraints used by browser-compatible streams.
/// </summary>
public static class WebVideoTranscodePlanner
{
    /// <summary>
    /// Returns the effective video bitrate ceiling for a quality selection.
    /// </summary>
    public static long GetMaximumBitRate(AppSettings settings, WebPlayerQuality quality) => quality switch
    {
        WebPlayerQuality.Medium => Math.Min(settings.MaximumVideoBitRateMbps, 5) * 1_000_000L,
        WebPlayerQuality.Low => Math.Min(settings.MaximumVideoBitRateMbps, 2) * 1_000_000L,
        _ => settings.MaximumVideoBitRateMbps * 1_000_000L
    };
}
