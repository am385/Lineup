using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;

namespace Lineup.SiliconDust.GuideSource;

/// <summary>
/// Acquires SiliconDust authorization from a subscribed physical device immediately before use.
/// </summary>
public sealed class PhysicalDeviceSiliconDustAuthorizationProvider : ISiliconDustDeviceAuthorizationProvider
{
    private readonly IPhysicalDeviceResolver _devices;

    /// <summary>Initializes a physical-device-scoped authorization provider.</summary>
    public PhysicalDeviceSiliconDustAuthorizationProvider(IPhysicalDeviceResolver devices)
    {
        _devices = devices;
    }

    /// <inheritdoc />
    public ValueTask<string> GetDeviceAuthAsync(PhysicalDeviceId physicalDeviceId, CancellationToken cancellationToken = default)
    {
        var device = _devices.GetRequired(physicalDeviceId);
        if (device is not IPhysicalDeviceAuthorizationCapability capability)
        {
            throw new InvalidOperationException($"Physical device {physicalDeviceId} does not provide guide authorization.");
        }

        return capability.GetAuthorizationAsync(cancellationToken);
    }
}
