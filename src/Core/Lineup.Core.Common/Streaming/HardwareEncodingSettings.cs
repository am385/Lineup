namespace Lineup.Core.Common.Streaming;

/// <summary>Identifies the optional H.264 video encoder. Software preserves the existing encoding path.</summary>
public enum HardwareVideoEncoder
{
    /// <summary>Use the software x264 encoder.</summary>
    Software,
    /// <summary>Use Intel Quick Sync Video.</summary>
    Qsv,
    /// <summary>Use NVIDIA NVENC.</summary>
    Nvenc,
    /// <summary>Use Linux VA-API.</summary>
    Vaapi,
    /// <summary>Use Windows AMD AMF.</summary>
    Amf,
    /// <summary>Use macOS VideoToolbox.</summary>
    VideoToolbox
}

/// <summary>Identifies backend-specific hardware encoder tuning, independently of software CRF.</summary>
public enum HardwareEncoderTuning
{
    /// <summary>Balance throughput and quality.</summary>
    Balanced,
    /// <summary>Prefer encoding throughput.</summary>
    Speed,
    /// <summary>Prefer encoding quality.</summary>
    Quality
}

/// <summary>Configures optional GPU encoding without enabling video conversion or hardware decoding.</summary>
public sealed record HardwareEncodingSettings
{
    /// <summary>Gets the encoder, defaulting to the existing software behavior.</summary>
    public HardwareVideoEncoder Encoder { get; init; }

    /// <summary>Gets the render-node path or backend device index; empty selects the backend default.</summary>
    public string Device { get; init; } = string.Empty;

    /// <summary>Gets the backend-specific tuning.</summary>
    public HardwareEncoderTuning Tuning { get; init; }

    /// <summary>Gets advanced encoder options, one option=value pair per line, without shell interpretation.</summary>
    public string AdvancedArguments { get; init; } = string.Empty;

    /// <summary>Gets whether a failed hardware startup check may select software before media is published.</summary>
    public bool AllowSoftwareFallback { get; init; }
}
