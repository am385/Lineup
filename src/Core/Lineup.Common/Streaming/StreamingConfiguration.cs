using Lineup.Common.Guides;

namespace Lineup.Common.Streaming;

/// <summary>
/// Current provider-neutral streaming configuration.
/// </summary>
public sealed record StreamingConfiguration
{
    /// <summary>Gets the only schema version accepted by this release.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Gets the persisted schema version.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Gets global physical-stage defaults.</summary>
    public PhysicalTranscodeProfile PhysicalDefaults { get; init; } = new();

    /// <summary>Gets global virtual-stage defaults.</summary>
    public VirtualTranscodeProfile VirtualDefaults { get; init; } = new() { Ac4Audio = OutputAudioPolicy.Ac3 };

    /// <summary>Gets how long an unused shared live pipeline remains available for reuse, from zero through 300 seconds.</summary>
    public int KeepWarmPeriodSeconds { get; init; }

    /// <summary>Gets configured physical devices.</summary>
    public IReadOnlyList<PhysicalDeviceDefinition> PhysicalDevices { get; init; } = [];

    /// <summary>Gets configured guide sources.</summary>
    public IReadOnlyList<GuideSourceDefinition> GuideSources { get; init; } = [];

    /// <summary>Gets configured virtual devices.</summary>
    public IReadOnlyList<VirtualDeviceDefinition> VirtualDevices { get; init; } = [];

    /// <summary>Gets configured guide outputs.</summary>
    public IReadOnlyList<GuideOutputDefinition> GuideOutputs { get; init; } = [];

    /// <summary>Gets reusable virtual-output listener security policies.</summary>
    public IReadOnlyList<VirtualOutputSecurityPolicyDefinition> VirtualOutputSecurityPolicies { get; init; } = [];

    /// <summary>Gets reusable virtual-output worker lifecycle and resource policies.</summary>
    public IReadOnlyList<VirtualOutputWorkerPolicyDefinition> VirtualOutputWorkerPolicies { get; init; } = [];
}

/// <summary>
/// Configures one physical tuner.
/// </summary>
public sealed record PhysicalDeviceDefinition
{
    /// <summary>Gets the stable physical identity.</summary>
    public required PhysicalDeviceId Id { get; init; }

    /// <summary>Gets the display name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets whether the device participates in discovery and streaming.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Gets deterministic conflict and failover priority.</summary>
    public int Order { get; init; }

    /// <summary>Gets adapter-owned physical configuration.</summary>
    public required AdapterConfiguration Adapter { get; init; }

    /// <summary>Gets sparse physical-stage overrides.</summary>
    public PhysicalTranscodeOverrides TranscodeOverrides { get; init; } = new();

}

/// <summary>
/// Configures one independent guide source.
/// </summary>
public sealed record GuideSourceDefinition
{
    /// <summary>Gets the stable source identity.</summary>
    public required GuideSourceId Id { get; init; }

    /// <summary>Gets the display name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets whether guide refresh is enabled.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Gets adapter-owned guide configuration.</summary>
    public required AdapterConfiguration Adapter { get; init; }

    /// <summary>Gets the physical devices whose channels may use this guide source.</summary>
    public PhysicalDeviceSubscription Subscription { get; init; } = new();
}

/// <summary>
/// Configures one virtual output.
/// </summary>
public sealed record VirtualDeviceDefinition
{
    /// <summary>Gets the stable virtual identity.</summary>
    public required VirtualDeviceId Id { get; init; }

    /// <summary>Gets the display name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets whether the virtual output is enabled.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Gets adapter-owned virtual configuration.</summary>
    public required AdapterConfiguration Adapter { get; init; }

    /// <summary>Gets subscribed physical devices.</summary>
    public PhysicalDeviceSubscription Subscription { get; init; } = new();

    /// <summary>Gets sparse virtual-stage overrides.</summary>
    public VirtualTranscodeOverrides TranscodeOverrides { get; init; } = new();

    /// <summary>Gets isolated listener and worker hosting configuration, or <see langword="null"/> when the output is not externally hosted.</summary>
    public VirtualOutputHostingDefinition? Hosting { get; init; }
}

/// <summary>
/// Identifies whether a guide output dynamically uses all enabled items or an explicit selection.
/// </summary>
public enum GuideOutputSelectionMode
{
    /// <summary>Use every enabled item.</summary>
    All,

    /// <summary>Use explicitly selected enabled items.</summary>
    Selected
}

/// <summary>
/// Configures one guide publication output.
/// </summary>
public sealed record GuideOutputDefinition
{
    /// <summary>Gets the stable output identity.</summary>
    public required GuideOutputId Id { get; init; }

    /// <summary>Gets the display name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the stable code-safe key used by named API routes and default destinations.</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>Gets whether publication is enabled.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Gets whether this is a plugin-owned built-in output.</summary>
    public bool IsBuiltIn { get; init; }

    /// <summary>Gets adapter-owned output configuration.</summary>
    public required AdapterConfiguration Adapter { get; init; }

    /// <summary>Gets virtual-device selection behavior.</summary>
    public GuideOutputSelectionMode VirtualDeviceSelectionMode { get; init; } = GuideOutputSelectionMode.All;

    /// <summary>Gets selected virtual devices when selection mode is explicit.</summary>
    public HashSet<VirtualDeviceId> SelectedVirtualDeviceIds { get; init; } = [];

    /// <summary>Gets guide-source selection behavior.</summary>
    public GuideOutputSelectionMode GuideSourceSelectionMode { get; init; } = GuideOutputSelectionMode.All;

    /// <summary>Gets selected guide sources when selection mode is explicit.</summary>
    public HashSet<GuideSourceId> SelectedGuideSourceIds { get; init; } = [];
}

/// <summary>
/// Controls physical-stage source normalization.
/// </summary>
public sealed record PhysicalTranscodeProfile
{
    /// <summary>Gets whether HEVC video is converted to H.264.</summary>
    public bool ConvertHevcToH264 { get; init; }

    /// <summary>Gets the deinterlacing behavior.</summary>
    public SourceDeinterlacePolicy Deinterlace { get; init; }

    /// <summary>Gets the preferred non-AC-4 audio policy.</summary>
    public OutputAudioPolicy Audio { get; init; } = OutputAudioPolicy.Copy;

    /// <summary>Gets the preferred AC-4 audio policy.</summary>
    public OutputAudioPolicy Ac4Audio { get; init; } = OutputAudioPolicy.Copy;

    /// <summary>Gets the video encoder preset.</summary>
    public VideoEncoderPreset VideoPreset { get; init; } = VideoEncoderPreset.VeryFast;

    /// <summary>Gets the constant-rate-factor video quality, where lower values provide higher quality.</summary>
    public int VideoQuality { get; init; } = 21;

    /// <summary>Gets the maximum encoded bitrate in megabits per second.</summary>
    public int MaximumVideoBitRateMbps { get; init; } = 10;
}

/// <summary>
/// Sparse physical-stage settings.
/// </summary>
public sealed record PhysicalTranscodeOverrides
{
    /// <summary>Gets an optional HEVC conversion override.</summary>
    public bool? ConvertHevcToH264 { get; init; }

    /// <summary>Gets an optional deinterlacing override.</summary>
    public SourceDeinterlacePolicy? Deinterlace { get; init; }

    /// <summary>Gets an optional non-AC-4 audio policy override.</summary>
    public OutputAudioPolicy? Audio { get; init; }

    /// <summary>Gets an optional AC-4 audio policy override.</summary>
    public OutputAudioPolicy? Ac4Audio { get; init; }

    /// <summary>Gets an optional video encoder preset override.</summary>
    public VideoEncoderPreset? VideoPreset { get; init; }

    /// <summary>Gets an optional video quality override.</summary>
    public int? VideoQuality { get; init; }

    /// <summary>Gets an optional maximum encoded bitrate override.</summary>
    public int? MaximumVideoBitRateMbps { get; init; }

    /// <summary>Overlays these values on global defaults.</summary>
    public PhysicalTranscodeProfile Apply(PhysicalTranscodeProfile defaults) => defaults with
    {
        ConvertHevcToH264 = ConvertHevcToH264 ?? defaults.ConvertHevcToH264,
        Deinterlace = Deinterlace ?? defaults.Deinterlace,
        Audio = Audio ?? defaults.Audio,
        Ac4Audio = Ac4Audio ?? defaults.Ac4Audio,
        VideoPreset = VideoPreset ?? defaults.VideoPreset,
        VideoQuality = VideoQuality ?? defaults.VideoQuality,
        MaximumVideoBitRateMbps = MaximumVideoBitRateMbps ?? defaults.MaximumVideoBitRateMbps
    };
}

/// <summary>
/// Controls virtual-stage output compatibility.
/// </summary>
public sealed record VirtualTranscodeProfile
{
    /// <summary>Gets whether HEVC video is converted to H.264.</summary>
    public bool ConvertHevcToH264 { get; init; }

    /// <summary>Gets the deinterlacing behavior.</summary>
    public SourceDeinterlacePolicy Deinterlace { get; init; }

    /// <summary>Gets the preferred non-AC-4 audio policy.</summary>
    public OutputAudioPolicy Audio { get; init; } = OutputAudioPolicy.Copy;

    /// <summary>Gets the preferred AC-4 audio policy.</summary>
    public OutputAudioPolicy Ac4Audio { get; init; } = OutputAudioPolicy.Copy;

    /// <summary>Gets the video encoder preset.</summary>
    public VideoEncoderPreset VideoPreset { get; init; } = VideoEncoderPreset.VeryFast;

    /// <summary>Gets the constant-rate-factor video quality, where lower values provide higher quality.</summary>
    public int VideoQuality { get; init; } = 21;

    /// <summary>Gets the maximum encoded bitrate in megabits per second.</summary>
    public int MaximumVideoBitRateMbps { get; init; } = 10;
}

/// <summary>
/// Sparse virtual-stage settings.
/// </summary>
public sealed record VirtualTranscodeOverrides
{
    /// <summary>Gets an optional HEVC conversion override.</summary>
    public bool? ConvertHevcToH264 { get; init; }

    /// <summary>Gets an optional deinterlacing override.</summary>
    public SourceDeinterlacePolicy? Deinterlace { get; init; }

    /// <summary>Gets an optional non-AC-4 audio policy override.</summary>
    public OutputAudioPolicy? Audio { get; init; }

    /// <summary>Gets an optional AC-4 audio policy override.</summary>
    public OutputAudioPolicy? Ac4Audio { get; init; }

    /// <summary>Gets an optional video encoder preset override.</summary>
    public VideoEncoderPreset? VideoPreset { get; init; }

    /// <summary>Gets an optional video quality override.</summary>
    public int? VideoQuality { get; init; }

    /// <summary>Gets an optional maximum encoded bitrate override.</summary>
    public int? MaximumVideoBitRateMbps { get; init; }

    /// <summary>Overlays these values on global defaults.</summary>
    public VirtualTranscodeProfile Apply(VirtualTranscodeProfile defaults) => defaults with
    {
        ConvertHevcToH264 = ConvertHevcToH264 ?? defaults.ConvertHevcToH264,
        Deinterlace = Deinterlace ?? defaults.Deinterlace,
        Audio = Audio ?? defaults.Audio,
        Ac4Audio = Ac4Audio ?? defaults.Ac4Audio,
        VideoPreset = VideoPreset ?? defaults.VideoPreset,
        VideoQuality = VideoQuality ?? defaults.VideoQuality,
        MaximumVideoBitRateMbps = MaximumVideoBitRateMbps ?? defaults.MaximumVideoBitRateMbps
    };
}

/// <summary>
/// Physical-stage deinterlacing behavior.
/// </summary>
public enum SourceDeinterlacePolicy
{
    /// <summary>Preserve the source.</summary>
    Preserve,

    /// <summary>Produce one progressive frame per source frame.</summary>
    SourceFrameRate,

    /// <summary>Produce one progressive frame per source field.</summary>
    SourceFieldRate
}

/// <summary>
/// Virtual-stage audio output behavior.
/// </summary>
public enum OutputAudioPolicy
{
    /// <summary>Copy compatible source audio.</summary>
    Copy,

    /// <summary>Encode audio as AC-3.</summary>
    Ac3,

    /// <summary>Encode audio as E-AC-3.</summary>
    Eac3
}

/// <summary>
/// Controls the CPU-versus-compression tradeoff for H.264 encoding.
/// </summary>
public enum VideoEncoderPreset
{
    /// <summary>Prioritizes lower CPU usage.</summary>
    VeryFast,

    /// <summary>Uses additional CPU for improved compression.</summary>
    Faster,

    /// <summary>Uses more CPU for higher compression efficiency.</summary>
    Fast,

    /// <summary>Prioritizes compression efficiency.</summary>
    Medium
}
