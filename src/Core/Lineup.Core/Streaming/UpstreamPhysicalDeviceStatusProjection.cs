namespace Lineup.Core.Streaming;

/// <summary>Projects subscribed devices' cached observations for read-only upstream publication.</summary>
public static class UpstreamPhysicalDeviceStatusProjection
{
    /// <summary>Creates ordered observations without polling hardware or exporting stream targets and descriptive configuration.</summary>
    public static IReadOnlyList<UpstreamPhysicalDeviceStatus> Create(
        IReadOnlyList<PhysicalDeviceDefinition> subscribedDevices,
        IReadOnlyList<PhysicalDeviceRuntimeStatus> cachedStatuses)
    {
        ArgumentNullException.ThrowIfNull(subscribedDevices);
        ArgumentNullException.ThrowIfNull(cachedStatuses);
        var statuses = cachedStatuses.ToDictionary(status => status.Device.Id);
        return subscribedDevices.Select(device =>
        {
            statuses.TryGetValue(device.Id, out var cached);
            if (cached?.Device.Kind != device.Adapter.Kind)
            {
                cached = null;
            }
            var status = cached?.Status;
            var observedAt = status is null ? null : cached?.StatusObservedAtUtc;
            return new UpstreamPhysicalDeviceStatus
            {
                DeviceId = device.Id.Value,
                Name = device.Name,
                Kind = device.Adapter.Kind,
                ObservedAtUtc = observedAt,
                Error = cached?.Error ?? (observedAt is null ? "Receiver status has not been observed yet." : null),
                ReceiversAreStreamSlots = status?.ReceiversAreStreamSlots == true,
                Receivers = observedAt is not null && status is not null
                    ? status.Receivers.Select(receiver => receiver with { Target = null }).ToArray()
                    : []
            };
        }).ToArray();
    }
}
