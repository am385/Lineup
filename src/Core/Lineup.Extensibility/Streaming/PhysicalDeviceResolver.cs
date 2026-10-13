using Lineup.Common.Streaming;

namespace Lineup.Extensibility.Streaming;

/// <summary>
/// Provides plugins with access to configured physical tuner runtime instances.
/// </summary>
public interface IPhysicalDeviceResolver
{
    /// <summary>Gets all active physical devices in configured priority order.</summary>
    IReadOnlyList<IPhysicalTunerDevice> Devices { get; }

    /// <summary>Finds one active physical device.</summary>
    IPhysicalTunerDevice GetRequired(PhysicalDeviceId deviceId);
}
