namespace Lineup.Core.Streaming;

/// <summary>
/// Persisted global identity choices for one physical source channel.
/// </summary>
public sealed record PhysicalChannelIdentity
{
    /// <summary>Gets the source channel.</summary>
    public required PhysicalSourceChannel Source { get; init; }

    /// <summary>Gets a globally applied number override.</summary>
    public string? NumberOverride { get; init; }

    /// <summary>Gets a globally applied name override.</summary>
    public string? NameOverride { get; init; }

    /// <summary>Gets an optional equivalence group used for priority/failover.</summary>
    public Guid? EquivalenceGroupId { get; init; }

    /// <summary>Gets whether this physical source channel is enabled for routing and publication.</summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>Gets when the channel was first observed.</summary>
    public DateTime FirstSeenUtc { get; init; }

    /// <summary>Gets when the channel was most recently observed.</summary>
    public DateTime LastSeenUtc { get; init; }

    /// <summary>Gets the effective number used by every virtual output and guide publication.</summary>
    public string EffectiveNumber => string.IsNullOrWhiteSpace(NumberOverride) ? Source.ProviderNumber : NumberOverride.Trim();

    /// <summary>Gets the effective name used by every virtual output and guide publication.</summary>
    public string EffectiveName => string.IsNullOrWhiteSpace(NameOverride) ? Source.ProviderName : NameOverride.Trim();
}

/// <summary>
/// Describes a physical channel excluded because its effective number conflicts.
/// </summary>
public sealed record ChannelNumberConflict(string Number, SourceChannelId PublishedSource, SourceChannelId ConflictingSource);

/// <summary>
/// Result of projecting physical source channels into one virtual device.
/// </summary>
public sealed record VirtualChannelResolution(IReadOnlyList<ResolvedVirtualChannel> Channels, IReadOnlyList<ChannelNumberConflict> Conflicts);

/// <summary>
/// Projects globally identified physical channels through one virtual subscription.
/// </summary>
public static class ChannelTopologyResolver
{
    /// <summary>
    /// Resolves publishable channels and deterministic conflicts.
    /// </summary>
    public static VirtualChannelResolution Resolve(
        VirtualDeviceDefinition virtualDevice,
        IReadOnlyList<PhysicalDeviceDefinition> physicalDevices,
        IReadOnlyList<PhysicalChannelIdentity> channels)
    {
        var subscribed = physicalDevices
            .Where(device => device.Enabled)
            .Where(device => virtualDevice.Subscription.Mode == PhysicalDeviceSubscriptionMode.All ||
                virtualDevice.Subscription.SelectedDeviceIds.Contains(device.Id))
            .OrderBy(device => device.Order)
            .ThenBy(device => device.Id.Value)
            .Select(device => device.Id)
            .ToArray();
        var deviceOrder = subscribed.Select((id, index) => (id, index)).ToDictionary(item => item.id, item => item.index);
        var candidates = channels
            .Where(channel => channel.IsEnabled)
            .Where(channel => deviceOrder.ContainsKey(channel.Source.Id.PhysicalDeviceId))
            .OrderBy(channel => deviceOrder[channel.Source.Id.PhysicalDeviceId])
            .ThenBy(channel => channel.EffectiveNumber, StringComparer.OrdinalIgnoreCase)
            .ThenBy(channel => channel.Source.Id.ProviderKey, StringComparer.Ordinal)
            .ToArray();
        var published = new Dictionary<string, PhysicalChannelIdentity>(StringComparer.OrdinalIgnoreCase);
        var resolved = new List<ResolvedVirtualChannel>();
        var conflicts = new List<ChannelNumberConflict>();

        foreach (var candidate in candidates)
        {
            if (!published.TryGetValue(candidate.EffectiveNumber, out var existing))
            {
                published.Add(candidate.EffectiveNumber, candidate);
                resolved.Add(new ResolvedVirtualChannel(candidate.EffectiveNumber, candidate.EffectiveName, [candidate.Source.Id])
                {
                    Metadata = candidate.Source.Metadata
                });
                continue;
            }

            if (candidate.EquivalenceGroupId.HasValue && candidate.EquivalenceGroupId == existing.EquivalenceGroupId)
            {
                var index = resolved.FindIndex(channel => channel.Number.Equals(candidate.EffectiveNumber, StringComparison.OrdinalIgnoreCase));
                var current = resolved[index];
                resolved[index] = current with { Sources = current.Sources.Append(candidate.Source.Id).ToArray() };
                continue;
            }

            conflicts.Add(new ChannelNumberConflict(candidate.EffectiveNumber, existing.Source.Id, candidate.Source.Id));
        }

        return new VirtualChannelResolution(resolved, conflicts);
    }
}
