namespace Lineup.WebPlayer;

/// <summary>Identifies a synthetic video codec offered by Watch Test.</summary>
public enum CmafTestVideoCodec
{
    /// <summary>H.264/AVC encoded by libx264.</summary>
    H264,
    /// <summary>HEVC encoded by libx265 and tagged for browser MP4 playback.</summary>
    Hevc
}

/// <summary>Identifies a synthetic audio codec offered by Watch Test.</summary>
public enum CmafTestAudioCodec
{
    /// <summary>Advanced Audio Coding.</summary>
    Aac,
    /// <summary>Dolby Digital.</summary>
    Ac3,
    /// <summary>Dolby Digital Plus.</summary>
    Eac3,
    /// <summary>Dolby AC-4, which the bundled FFmpeg can decode but cannot encode.</summary>
    Ac4
}

/// <summary>Identifies a standard audio channel layout offered by Watch Test.</summary>
public enum CmafTestChannelLayout
{
    /// <summary>One center channel.</summary>
    Mono,
    /// <summary>Left and right channels.</summary>
    Stereo,
    /// <summary>Left, right, and center channels.</summary>
    Surround3Point0,
    /// <summary>Four-channel quadraphonic audio.</summary>
    Quad,
    /// <summary>Five full-range surround channels.</summary>
    Surround5Point0,
    /// <summary>Five full-range channels plus LFE.</summary>
    Surround5Point1,
    /// <summary>Seven full-range channels plus LFE.</summary>
    Surround7Point1
}

/// <summary>Configures one exact synthetic CMAF compatibility presentation.</summary>
public sealed record CmafCompatibilityTestRequest
{
    /// <summary>Gets the protocol the browser will test.</summary>
    public CmafProtocol Protocol { get; init; } = CmafProtocol.Dash;
    /// <summary>Gets the production quality profile used by the synthetic encoder.</summary>
    public WebPlayerQuality Quality { get; init; } = WebPlayerQuality.AppDefault;
    /// <summary>Gets the only video codec included in the presentation.</summary>
    public CmafTestVideoCodec VideoCodec { get; init; } = CmafTestVideoCodec.H264;
    /// <summary>Gets the exact synthetic video profile.</summary>
    public CmafTestVideoProfile VideoProfile { get; init; } = CmafTestVideoProfile.Main;
    /// <summary>Gets the only audio codec included in the presentation.</summary>
    public CmafTestAudioCodec AudioCodec { get; init; } = CmafTestAudioCodec.Aac;
    /// <summary>Gets the generated audio channel layout.</summary>
    public CmafTestChannelLayout ChannelLayout { get; init; } = CmafTestChannelLayout.Stereo;
    /// <summary>Gets the exact synthetic subtitle presentation.</summary>
    public CmafTestSubtitleMode SubtitleMode { get; init; }
}

/// <summary>Describes one started synthetic CMAF compatibility presentation.</summary>
public sealed record CmafCompatibilityTestResponse(
    string SessionId,
    string ManifestUrl,
    string VideoCodec,
    string AudioCodec,
    int Channels,
    int Width,
    int Height,
    string? SubtitleUrl = null);

/// <summary>Provides encoder-independent compatibility-test metadata.</summary>
public static class CmafCompatibilityTestPolicy
{
    /// <summary>Returns layouts supported by the selected synthetic codec.</summary>
    public static IReadOnlyList<CmafTestChannelLayout> GetSupportedLayouts(CmafTestAudioCodec codec) =>
        codec switch
        {
            CmafTestAudioCodec.Aac => Enum.GetValues<CmafTestChannelLayout>(),
            CmafTestAudioCodec.Ac3 or CmafTestAudioCodec.Eac3 => Enum.GetValues<CmafTestChannelLayout>()
                .Where(layout => GetChannelCount(layout) <= 6)
                .ToArray(),
            CmafTestAudioCodec.Ac4 => [],
            _ => throw new ArgumentOutOfRangeException(nameof(codec), codec, "Unsupported Watch Test audio codec.")
        };

    /// <summary>Returns the channel count represented by a test layout.</summary>
    public static int GetChannelCount(CmafTestChannelLayout layout) =>
        layout switch
        {
            CmafTestChannelLayout.Mono => 1,
            CmafTestChannelLayout.Stereo => 2,
            CmafTestChannelLayout.Surround3Point0 => 3,
            CmafTestChannelLayout.Quad => 4,
            CmafTestChannelLayout.Surround5Point0 => 5,
            CmafTestChannelLayout.Surround5Point1 => 6,
            CmafTestChannelLayout.Surround7Point1 => 8,
            _ => throw new ArgumentOutOfRangeException(nameof(layout), layout, "Unsupported Watch Test channel layout.")
        };

    /// <summary>Returns the deterministic synthetic resolution for a production quality profile.</summary>
    public static (int Width, int Height) GetResolution(WebPlayerQuality quality) =>
        quality switch
        {
            WebPlayerQuality.AppDefault or WebPlayerQuality.High => (1920, 1080),
            WebPlayerQuality.Medium => (1280, 720),
            WebPlayerQuality.Low => (854, 480),
            _ => throw new ArgumentOutOfRangeException(nameof(quality), quality, "Unsupported Watch Test quality.")
        };
}
