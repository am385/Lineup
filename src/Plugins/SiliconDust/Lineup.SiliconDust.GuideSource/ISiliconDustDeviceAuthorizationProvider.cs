using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;

namespace Lineup.SiliconDust.GuideSource;

/// <summary>
/// Supplies short-lived SiliconDust authorization for a physical device.
/// </summary>
public interface ISiliconDustDeviceAuthorizationProvider
{
    /// <summary>
    /// Gets authorization for one physical device.
    /// </summary>
    /// <param name="physicalDeviceId">The subscribed physical device.</param>
    /// <param name="cancellationToken">Cancels authorization retrieval.</param>
    /// <returns>Authorization to use for the immediate provider request.</returns>
    ValueTask<string> GetDeviceAuthAsync(PhysicalDeviceId physicalDeviceId, CancellationToken cancellationToken = default);
}
