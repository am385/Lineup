namespace Lineup.Core.Streaming;

/// <summary>Resolves one virtual device's active compatible physical-device subscription.</summary>
public static class VirtualDeviceSubscriptionResolver
{
    /// <summary>Resolves compatible enabled physical definitions in deterministic topology order.</summary>
    public static IReadOnlyList<PhysicalDeviceDefinition> Resolve(
        VirtualDeviceDefinition virtualDevice,
        IReadOnlyList<PhysicalDeviceDefinition> physicalDevices,
        IVirtualTunerDriver driver)
    {
        ArgumentNullException.ThrowIfNull(virtualDevice);
        ArgumentNullException.ThrowIfNull(physicalDevices);
        ArgumentNullException.ThrowIfNull(driver);

        var configuredById = physicalDevices.ToDictionary(device => device.Id);
        if (virtualDevice.Subscription.Mode == PhysicalDeviceSubscriptionMode.Selected)
        {
            foreach (var selectedId in virtualDevice.Subscription.SelectedDeviceIds)
            {
                if (!configuredById.TryGetValue(selectedId, out var selected))
                {
                    throw new InvalidOperationException($"Virtual device '{virtualDevice.Name}' selects unknown physical device {selectedId}.");
                }
                if (!driver.SupportsPhysicalAdapter(selected.Adapter.Kind))
                {
                    throw new InvalidOperationException(
                        $"Virtual device '{virtualDevice.Name}' does not support physical device '{selected.Name}' with adapter kind '{selected.Adapter.Kind}'.");
                }
            }
        }

        return physicalDevices
            .Where(device => device.Enabled && driver.SupportsPhysicalAdapter(device.Adapter.Kind))
            .Where(device => virtualDevice.Subscription.Mode == PhysicalDeviceSubscriptionMode.All || virtualDevice.Subscription.SelectedDeviceIds.Contains(device.Id))
            .OrderBy(device => device.Order)
            .ThenBy(device => device.Id.Value)
            .ToArray();
    }
}
