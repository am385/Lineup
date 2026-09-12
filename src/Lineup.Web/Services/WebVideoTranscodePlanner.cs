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
/// Builds the shared H.264 video arguments used by browser-compatible streams.
/// </summary>
public static class WebVideoTranscodePlanner
{
    /// <summary>
    /// Creates quality-oriented H.264 arguments from application settings.
    /// </summary>
    /// <param name="settings">The current transcoder settings.</param>
    /// <param name="sourceVideoCodec">The detected source video codec, when available.</param>
    /// <param name="quality">The requested browser playback quality.</param>
    /// <returns>FFmpeg arguments suitable for fMP4 or HLS output.</returns>
    public static string CreateArguments(AppSettings settings, string? sourceVideoCodec = null, WebPlayerQuality quality = WebPlayerQuality.AppDefault)
    {
        if (quality == WebPlayerQuality.AppDefault &&
            string.Equals(sourceVideoCodec, "h264", StringComparison.OrdinalIgnoreCase))
        {
            return "-c:v copy";
        }

        var profile = quality switch
        {
            WebPlayerQuality.High => new WebVideoProfile(settings.WebVideoQuality, settings.MaximumVideoBitRateMbps, null),
            WebPlayerQuality.Medium => new WebVideoProfile(23, Math.Min(settings.MaximumVideoBitRateMbps, 5), 720),
            WebPlayerQuality.Low => new WebVideoProfile(25, Math.Min(settings.MaximumVideoBitRateMbps, 2), 480),
            _ => new WebVideoProfile(settings.WebVideoQuality, settings.MaximumVideoBitRateMbps, null)
        };
        var preset = settings.WebVideoPreset.ToString().ToLowerInvariant();
        var scale = profile.MaximumHeight.HasValue
            ? $"-vf \"scale=-2:'min({profile.MaximumHeight},ih)'\" "
            : "";
        return $"-c:v libx264 -preset {preset} -tune zerolatency " +
            $"-crf {profile.Crf} -maxrate {profile.MaximumBitRateMbps}M -bufsize {profile.MaximumBitRateMbps * 2}M " +
            scale +
            "-profile:v high -level 4.2 -pix_fmt yuv420p " +
            "-flags +cgop -g 120 -keyint_min 60 -sc_threshold 0 " +
            "-force_key_frames \"expr:gte(t,n_forced*2)\"";
    }

    /// <summary>
    /// Returns the effective video bitrate ceiling for a quality selection.
    /// </summary>
    public static long GetMaximumBitRate(AppSettings settings, WebPlayerQuality quality) => quality switch
    {
        WebPlayerQuality.Medium => Math.Min(settings.MaximumVideoBitRateMbps, 5) * 1_000_000L,
        WebPlayerQuality.Low => Math.Min(settings.MaximumVideoBitRateMbps, 2) * 1_000_000L,
        _ => settings.MaximumVideoBitRateMbps * 1_000_000L
    };

    private sealed record WebVideoProfile(int Crf, int MaximumBitRateMbps, int? MaximumHeight);
}
