using Lineup.Common.Streaming;

namespace Lineup.Extensibility.Streaming;

/// <summary>
/// Owns one physical source acquisition.
/// </summary>
public interface IPhysicalStreamLease : IAsyncDisposable
{
    /// <summary>Gets the readable live stream.</summary>
    Stream Stream { get; }

    /// <summary>Gets an adapter-neutral media content type when known.</summary>
    string? ContentType { get; }
}

/// <summary>
/// Runtime instance of one configured physical tuner.
/// </summary>
public interface IPhysicalTunerDevice
{
    /// <summary>Gets current immutable metadata.</summary>
    PhysicalDeviceSnapshot Snapshot { get; }

    /// <summary>Refreshes and returns the current source lineup.</summary>
    Task<IReadOnlyList<PhysicalSourceChannel>> RefreshLineupAsync(CancellationToken cancellationToken = default);

    /// <summary>Opens one physical source acquisition.</summary>
    ValueTask<IPhysicalStreamLease> OpenStreamAsync(PhysicalStreamRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Exposes authorization that must be acquired from one physical device immediately before use.
/// </summary>
public interface IPhysicalDeviceAuthorizationCapability
{
    /// <summary>Gets short-lived authorization without persisting it.</summary>
    ValueTask<string> GetAuthorizationAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Exposes provider-neutral health and receiver status for one physical device.
/// </summary>
public interface IPhysicalDeviceStatusCapability
{
    /// <summary>Refreshes current receiver status using the active device connection.</summary>
    Task<PhysicalDeviceStatusSnapshot> RefreshStatusAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Refreshes descriptive information for one physical device independently from receiver status.
/// </summary>
public interface IPhysicalDeviceInformationCapability
{
    /// <summary>Refreshes current device metadata without polling receiver status.</summary>
    Task<PhysicalDeviceStatusSnapshot> RefreshDeviceInformationAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Stops an active physical receiver when supported by the adapter.
/// </summary>
public interface IPhysicalReceiverControlCapability
{
    /// <summary>Stops one receiver by its adapter-defined index.</summary>
    Task StopReceiverAsync(int receiverIndex, CancellationToken cancellationToken = default);
}

/// <summary>
/// Restarts one physical device when supported by the adapter.
/// </summary>
public interface IPhysicalDeviceRestartCapability
{
    /// <summary>Restarts the physical device.</summary>
    Task RestartAsync(CancellationToken cancellationToken = default);
}

/// <summary>Creates a diagnostic source URI for one physical channel.</summary>
public interface IPhysicalStreamDiagnosticCapability
{
    /// <summary>Creates a safe diagnostic URI without embedded credentials.</summary>
    Uri CreateDiagnosticUri(string providerKey);
}

/// <summary>
/// Creates runtime physical tuner instances for one adapter kind.
/// </summary>
public interface IPhysicalTunerDriver
{
    /// <summary>Gets the stable adapter kind.</summary>
    string Kind { get; }

    /// <summary>Gets the user-facing physical-device type name.</summary>
    string DisplayName { get; }

    /// <summary>Gets whether users can create additional devices of this type.</summary>
    bool SupportsUserCreation { get; }

    /// <summary>Gets supported physical-device capabilities.</summary>
    DeviceCapabilities Capabilities { get; }

    /// <summary>Creates the default adapter configuration for a new physical device.</summary>
    AdapterConfiguration CreateDefaultConfiguration(PhysicalDeviceId deviceId);

    /// <summary>Validates adapter configuration and returns all errors.</summary>
    IReadOnlyList<string> Validate(AdapterConfiguration configuration);

    /// <summary>Creates one configured runtime physical tuner.</summary>
    ValueTask<IPhysicalTunerDevice> CreateAsync(PhysicalDeviceId deviceId, string name, AdapterConfiguration configuration, CancellationToken cancellationToken = default);
}

/// <summary>
/// Tests and identifies one physical device from editable adapter configuration without activating it.
/// </summary>
public interface IPhysicalDeviceProbe
{
    /// <summary>Tests the connection and returns safe provider identity details.</summary>
    ValueTask<PhysicalDeviceProbeResult> ProbeAsync(AdapterConfiguration configuration, CancellationToken cancellationToken = default);
}

/// <summary>
/// Creates output plans for one virtual tuner adapter kind.
/// </summary>
public interface IVirtualTunerDriver
{
    /// <summary>Gets the stable adapter kind.</summary>
    string Kind { get; }

    /// <summary>Gets the user-facing virtual-device type name.</summary>
    string DisplayName { get; }

    /// <summary>Gets whether users can create additional devices of this type.</summary>
    bool SupportsUserCreation { get; }

    /// <summary>Gets host-neutral listener and discovery capabilities.</summary>
    VirtualOutputCapabilities OutputCapabilities => new();

    /// <summary>Gets whether this virtual adapter can consume one physical adapter kind.</summary>
    bool SupportsPhysicalAdapter(string physicalAdapterKind) => true;

    /// <summary>Creates the default adapter configuration for a new virtual device.</summary>
    AdapterConfiguration CreateDefaultConfiguration(VirtualDeviceId deviceId);

    /// <summary>Validates adapter configuration and returns all errors.</summary>
    IReadOnlyList<string> Validate(AdapterConfiguration configuration);
}

/// <summary>Derives externally advertised tuner capacity for one virtual output.</summary>
public interface IVirtualTunerCapacityProvider
{
    /// <summary>Gets output capacity from the virtual definition and current physical-device snapshots.</summary>
    PhysicalTunerCapacity GetCapacity(VirtualDeviceDefinition virtualDevice, IEnumerable<PhysicalDeviceSnapshot> physicalDevices);
}

/// <summary>
/// Initializes plugin-owned defaults in a fresh streaming configuration.
/// </summary>
public interface IStreamingConfigurationNormalizer
{
    /// <summary>
    /// Returns a streaming configuration containing this plugin's defaults.
    /// </summary>
    /// <param name="configuration">Configuration to normalize.</param>
    /// <param name="context">Host-owned initialization defaults.</param>
    /// <returns>The initialized configuration.</returns>
    StreamingConfiguration Normalize(
        StreamingConfiguration configuration,
        StreamingConfigurationInitializationContext context);
}
