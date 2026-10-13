using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;

namespace Lineup.Core.Guides;

/// <summary>
/// Resolves configured guide-source subscriptions against compatible active physical devices.
/// </summary>
public static class GuideSourceSubscriptionResolver
{
    /// <summary>Resolves one guide source's active physical-device scope.</summary>
    public static GuideSourceScope Resolve(
        GuideSourceDefinition source,
        IReadOnlyList<PhysicalDeviceDefinition> physicalDevices,
        IGuideSourceDriver driver)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(physicalDevices);
        ArgumentNullException.ThrowIfNull(driver);

        var configuredById = physicalDevices.ToDictionary(device => device.Id);
        if (source.Subscription.Mode == PhysicalDeviceSubscriptionMode.Selected)
        {
            foreach (var selectedId in source.Subscription.SelectedDeviceIds)
            {
                if (!configuredById.TryGetValue(selectedId, out var selected))
                {
                    throw new InvalidOperationException($"Guide source '{source.Name}' selects unknown physical device {selectedId}.");
                }
                if (!driver.SupportsPhysicalAdapter(selected.Adapter.Kind))
                {
                    throw new InvalidOperationException(
                        $"Guide source '{source.Name}' does not support physical device '{selected.Name}' with adapter kind '{selected.Adapter.Kind}'.");
                }
            }
        }

        var physicalDeviceIds = physicalDevices
            .Where(device => device.Enabled && driver.SupportsPhysicalAdapter(device.Adapter.Kind))
            .Where(device => source.Subscription.Mode == PhysicalDeviceSubscriptionMode.All || source.Subscription.SelectedDeviceIds.Contains(device.Id))
            .OrderBy(device => device.Order)
            .ThenBy(device => device.Id.Value)
            .Select(device => device.Id)
            .ToArray();
        return new GuideSourceScope(source.Id, physicalDeviceIds);
    }
}
