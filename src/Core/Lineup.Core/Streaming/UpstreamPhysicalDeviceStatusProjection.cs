namespace Lineup.Core.Streaming;

/// <summary>Projects subscribed devices' cached observations for read-only upstream publication.</summary>
public static class UpstreamPhysicalDeviceStatusProjection
{
    /// <summary>Projects signal for this virtual device's authenticated client using exact source-channel identities, never local slot indices or exported channel numbers.</summary>
    public static IReadOnlyList<StreamSignalStatus> CreateStreamSignals(
        VirtualDeviceId virtualDeviceId,
        string authenticatedClientId,
        IReadOnlyList<ActiveStreamSnapshot> activeStreams,
        IReadOnlyList<PhysicalDeviceRuntimeStatus> cachedStatuses)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticatedClientId);
        ArgumentNullException.ThrowIfNull(activeStreams);
        ArgumentNullException.ThrowIfNull(cachedStatuses);
        var statuses = cachedStatuses.ToDictionary(status => status.Device.Id);
        return activeStreams
            .Where(stream => stream.Flow?.VirtualDeviceId == virtualDeviceId && stream.AuthenticatedClientId == authenticatedClientId && Guid.TryParse(stream.SessionId, out _))
            .Select(stream =>
            {
                var signal = new StreamSignalStatus { StreamSessionId = Guid.Parse(stream.SessionId) };
                if (stream.Flow?.SourceChannelId is not { } source || !statuses.TryGetValue(source.PhysicalDeviceId, out var cached) ||
                    cached.StatusObservedAtUtc is null || cached.Status is null || cached.StatusError is not null)
                {
                    return signal with { Error = "Source signal is not currently available." };
                }
                var receivers = cached.Status.Receivers.Where(receiver => receiver.SourceChannelId == source && receiver.IsStreaming).ToArray();
                if (receivers.Length != 1)
                {
                    return signal with { Error = "Source signal cannot be uniquely associated with this stream." };
                }
                var receiver = receivers[0];
                return signal with
                {
                    ObservedAtUtc = cached.StatusObservedAtUtc,
                    HasLock = receiver.HasLock,
                    SignalStrength = receiver.SignalStrength,
                    SignalQuality = receiver.SignalQuality,
                    SymbolQuality = receiver.SymbolQuality
                };
            }).ToArray();
    }

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
                Error = cached?.StatusError ?? (observedAt is null ? "Receiver status has not been observed yet." : null),
                ReceiversAreStreamSlots = status?.ReceiversAreStreamSlots == true,
                Receivers = observedAt is not null && status is not null
                    ? status.Receivers.Select(receiver => receiver with { Target = null, SourceChannelId = null, SignalError = null }).ToArray()
                    : []
            };
        }).ToArray();
    }
}
