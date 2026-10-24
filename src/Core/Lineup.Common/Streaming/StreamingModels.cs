using System.Text.Json;

namespace Lineup.Common.Streaming;

/// <summary>
/// Describes the finite or unbounded capacity of a physical source.
/// </summary>
/// <param name="TunerCount">Finite tuner count, or <see langword="null"/> when capacity is not tuner-limited.</param>
public sealed record PhysicalTunerCapacity(int? TunerCount)
{
    /// <summary>Gets whether capacity is finite.</summary>
    public bool IsFinite => TunerCount.HasValue;
}

/// <summary>
/// Describes one channel discovered from a physical source.
/// </summary>
public sealed record PhysicalSourceChannel
{
    /// <summary>Gets the source-scoped identity.</summary>
    public required SourceChannelId Id { get; init; }

    /// <summary>Gets the provider-supplied display number.</summary>
    public required string ProviderNumber { get; init; }

    /// <summary>Gets the provider-supplied display name.</summary>
    public required string ProviderName { get; init; }

    /// <summary>Gets the optional provider metadata.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// Immutable physical device metadata.
/// </summary>
public sealed record PhysicalDeviceSnapshot
{
    /// <summary>Gets the configured identity.</summary>
    public required PhysicalDeviceId Id { get; init; }

    /// <summary>Gets the adapter kind.</summary>
    public required string Kind { get; init; }

    /// <summary>Gets the display name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the current capacity.</summary>
    public required PhysicalTunerCapacity Capacity { get; init; }
}

/// <summary>
/// Declares adapter features that can be validated before runtime.
/// </summary>
public sealed record DeviceCapabilities
{
    /// <summary>Gets whether the adapter exposes finite tuner capacity.</summary>
    public bool HasFiniteCapacity { get; init; }

    /// <summary>Gets whether the adapter can report live tuner state.</summary>
    public bool SupportsTunerStatus { get; init; }

    /// <summary>Gets whether the adapter can stop an active tuner.</summary>
    public bool SupportsTunerControl { get; init; }

    /// <summary>Gets whether the adapter accepts hardware transcode profiles.</summary>
    public bool SupportsHardwareTranscoding { get; init; }
}

/// <summary>
/// Adapter-owned configuration envelope.
/// </summary>
/// <param name="Kind">Registered adapter kind.</param>
/// <param name="Configuration">Adapter-specific JSON configuration.</param>
public sealed record AdapterConfiguration(string Kind, JsonElement Configuration);

/// <summary>
/// Request to acquire one physical source channel.
/// </summary>
public sealed record PhysicalStreamRequest
{
    /// <summary>Gets the requested source channel.</summary>
    public required SourceChannelId ChannelId { get; init; }

    /// <summary>Gets optional adapter-specific tune parameters.</summary>
    public IReadOnlyDictionary<string, string> TuneParameters { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// Provider-neutral current status for one physical device.
/// </summary>
public sealed record PhysicalDeviceStatusSnapshot
{
    /// <summary>Gets the physical device.</summary>
    public required PhysicalDeviceSnapshot Device { get; init; }

    /// <summary>Gets provider-neutral descriptive fields.</summary>
    public IReadOnlyDictionary<string, string> Details { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Gets current receiver states.</summary>
    public IReadOnlyList<PhysicalReceiverStatus> Receivers { get; init; } = [];

    /// <summary>Gets when status was observed.</summary>
    public DateTimeOffset ObservedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Provider-neutral current state for one physical receiver.
/// </summary>
public sealed record PhysicalReceiverStatus
{
    /// <summary>Gets the adapter-defined receiver index.</summary>
    public required int Index { get; init; }

    /// <summary>Gets whether the receiver is tuned.</summary>
    public bool IsActive { get; init; }

    /// <summary>Gets whether the receiver is delivering a stream.</summary>
    public bool IsStreaming { get; init; }

    /// <summary>Gets whether the receiver reports signal lock.</summary>
    public bool HasLock { get; init; }

    /// <summary>Gets the physical channel descriptor.</summary>
    public string? Channel { get; init; }

    /// <summary>Gets the virtual channel number.</summary>
    public string? VirtualChannel { get; init; }

    /// <summary>Gets the stream target when supplied by the adapter.</summary>
    public string? Target { get; init; }

    /// <summary>Gets the signal lock type.</summary>
    public string? LockType { get; init; }

    /// <summary>Gets signal strength as a percentage.</summary>
    public int? SignalStrength { get; init; }

    /// <summary>Gets signal-to-noise quality as a percentage.</summary>
    public int? SignalQuality { get; init; }

    /// <summary>Gets symbol error quality as a percentage.</summary>
    public int? SymbolQuality { get; init; }

    /// <summary>Gets the reported bit rate.</summary>
    public long? BitsPerSecond { get; init; }

    /// <summary>Gets the reported packet rate.</summary>
    public int? PacketsPerSecond { get; init; }
}

/// <summary>
/// Describes a successful physical-device connection and identity probe.
/// </summary>
public sealed record PhysicalDeviceProbeResult
{
    /// <summary>Gets the optional device name suggested by the provider.</summary>
    public string? SuggestedName { get; init; }

    /// <summary>Gets ordered, user-facing identity details that contain no secrets.</summary>
    public IReadOnlyList<PhysicalDeviceProbeDetail> Details { get; init; } = [];
}

/// <summary>
/// Describes one safe, user-facing physical-device identity value.
/// </summary>
public sealed record PhysicalDeviceProbeDetail
{
    /// <summary>Gets the display label.</summary>
    public required string Label { get; init; }

    /// <summary>Gets the display value.</summary>
    public required string Value { get; init; }
}

/// <summary>Supplies host-owned defaults while creating a fresh streaming configuration.</summary>
public sealed record StreamingConfigurationInitializationContext
{
    /// <summary>Gets the default XMLTV output path.</summary>
    public required string DefaultXmltvOutputPath { get; init; }
}

/// <summary>
/// Subscription behavior for a virtual device.
/// </summary>
public enum PhysicalDeviceSubscriptionMode
{
    /// <summary>Subscribe dynamically to every enabled physical device.</summary>
    All,

    /// <summary>Subscribe only to explicitly selected physical devices.</summary>
    Selected
}

/// <summary>
/// Defines the physical devices visible to one virtual output.
/// </summary>
public sealed record PhysicalDeviceSubscription
{
    /// <summary>Gets the subscription mode.</summary>
    public PhysicalDeviceSubscriptionMode Mode { get; init; } = PhysicalDeviceSubscriptionMode.All;

    /// <summary>Gets selected physical IDs when <see cref="Mode"/> is <see cref="PhysicalDeviceSubscriptionMode.Selected"/>.</summary>
    public HashSet<PhysicalDeviceId> SelectedDeviceIds { get; init; } = [];
}
