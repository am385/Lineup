using Lineup.Core.Storage.Entities;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Lineup.Core.Storage;

/// <summary>
/// Persists provider-neutral physical channels and their global identity choices.
/// </summary>
public sealed class StreamingTopologyStore
{
    private readonly IDbContextFactory<EpgDbContext> _contextFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Initializes a topology store.
    /// </summary>
    public StreamingTopologyStore(IDbContextFactory<EpgDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    /// <summary>
    /// Reads every active physical source channel and its global identity choices.
    /// </summary>
    public async Task<IReadOnlyList<PhysicalChannelIdentity>> ReadActiveAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await EpgDatabaseSchema.EnsureAsync(context, cancellationToken);
        return await ReadActiveAsync(context, cancellationToken);
    }

    /// <summary>Reads topology through the caller's guide read transaction.</summary>
    internal static async Task<IReadOnlyList<PhysicalChannelIdentity>> ReadActiveAsync(EpgDbContext context, CancellationToken cancellationToken, IReadOnlyCollection<PhysicalDeviceId>? physicalDeviceIds = null)
    {
        var query = context.PhysicalSourceChannels.AsNoTracking().Where(channel => channel.IsActive);
        if (physicalDeviceIds is not null)
        {
            var keys = physicalDeviceIds.Select(id => id.ToString()).ToArray();
            query = query.Where(channel => keys.Contains(channel.PhysicalDeviceId));
        }
        var channels = await query
            .OrderBy(channel => channel.PhysicalDeviceId)
            .ThenBy(channel => channel.ProviderNumber)
            .ToArrayAsync(cancellationToken);
        return channels.Select(Map).ToArray();
    }

    /// <summary>
    /// Gets whether an active physical source with the effective channel number exists and is enabled.
    /// </summary>
    public async Task<PhysicalChannelEnablementState> GetEnablementAsync(string channelNumber, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelNumber);
        var normalized = channelNumber.Trim();
        var matches = (await ReadActiveAsync(cancellationToken))
            .Where(channel => channel.EffectiveNumber.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length == 0)
        {
            return PhysicalChannelEnablementState.NotFound;
        }
        return matches.Any(channel => channel.IsEnabled)
            ? PhysicalChannelEnablementState.Enabled
            : PhysicalChannelEnablementState.Disabled;
    }

    /// <summary>
    /// Reconciles one physical device's latest successful lineup while preserving global overrides.
    /// </summary>
    public async Task<IReadOnlyList<PhysicalChannelIdentity>> ReconcileAsync(PhysicalDeviceId deviceId, IEnumerable<PhysicalSourceChannel> channels, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channels);
        var normalized = channels
            .Where(channel => channel.Id.PhysicalDeviceId == deviceId)
            .DistinctBy(channel => channel.Id.ProviderKey, StringComparer.Ordinal)
            .ToArray();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            await EpgDatabaseSchema.EnsureAsync(context, cancellationToken);
            var deviceKey = deviceId.ToString();
            var existing = await context.PhysicalSourceChannels
                .Where(channel => channel.PhysicalDeviceId == deviceKey)
                .ToDictionaryAsync(channel => channel.ProviderKey, StringComparer.Ordinal, cancellationToken);
            var now = DateTime.UtcNow;
            foreach (var stored in existing.Values)
            {
                stored.IsActive = false;
            }

            foreach (var channel in normalized)
            {
                if (!existing.TryGetValue(channel.Id.ProviderKey, out var stored))
                {
                    stored = new StoredPhysicalSourceChannel
                    {
                        PhysicalDeviceId = deviceKey,
                        ProviderKey = channel.Id.ProviderKey,
                        ProviderNumber = channel.ProviderNumber,
                        ProviderName = channel.ProviderName,
                        IsEnabled = true,
                        FirstSeenUtc = now,
                        LastSeenUtc = now
                    };
                    existing.Add(stored.ProviderKey, stored);
                    context.PhysicalSourceChannels.Add(stored);
                }

                stored.ProviderNumber = channel.ProviderNumber;
                stored.ProviderName = channel.ProviderName;
                stored.MetadataJson = JsonSerializer.Serialize(channel.Metadata);
                stored.IsActive = true;
                stored.LastSeenUtc = now;
            }

            await context.SaveChangesAsync(cancellationToken);
            return existing.Values.Where(channel => channel.IsActive).Select(Map).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Removes channels and authoritative guide bindings owned by physical devices that are no longer configured.</summary>
    public async Task<int> PruneDevicesAsync(IEnumerable<PhysicalDeviceId> retainedDeviceIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(retainedDeviceIds);
        var retainedKeys = retainedDeviceIds.Select(deviceId => deviceId.ToString()).ToHashSet(StringComparer.Ordinal);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            await EpgDatabaseSchema.EnsureAsync(context, cancellationToken);
            var staleChannels = retainedKeys.Count == 0
                ? context.PhysicalSourceChannels
                : context.PhysicalSourceChannels.Where(channel => !retainedKeys.Contains(channel.PhysicalDeviceId));
            var staleChannelIds = await staleChannels.Select(channel => channel.Id).ToArrayAsync(cancellationToken);
            if (staleChannelIds.Length == 0)
            {
                return 0;
            }

            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            await context.SourceGuideBindings
                .Where(binding => staleChannelIds.Contains(binding.PhysicalSourceChannelId))
                .ExecuteDeleteAsync(cancellationToken);
            await context.PhysicalSourceChannels
                .Where(channel => staleChannelIds.Contains(channel.Id))
                .ExecuteDeleteAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return staleChannelIds.Length;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Applies one global number/name/equivalence choice to a physical source channel.
    /// </summary>
    public Task<PhysicalChannelIdentity> SetIdentityAsync(SourceChannelId channelId, string? numberOverride, string? nameOverride, Guid? equivalenceGroupId, CancellationToken cancellationToken = default) =>
        SetIdentityCoreAsync(channelId, numberOverride, nameOverride, equivalenceGroupId, false, cancellationToken);

    /// <summary>Edits display identity while preserving the current saved group, including changes made since the editor loaded.</summary>
    public Task<PhysicalChannelIdentity> SetDisplayIdentityAsync(SourceChannelId channelId, string? numberOverride, string? nameOverride, CancellationToken cancellationToken = default) =>
        SetIdentityCoreAsync(channelId, numberOverride, nameOverride, null, true, cancellationToken);

    private async Task<PhysicalChannelIdentity> SetIdentityCoreAsync(SourceChannelId channelId, string? numberOverride, string? nameOverride, Guid? equivalenceGroupId, bool preserveGroup, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            await EpgDatabaseSchema.EnsureAsync(context, cancellationToken);
            var deviceKey = channelId.PhysicalDeviceId.ToString();
            var stored = await context.PhysicalSourceChannels.SingleOrDefaultAsync(
                channel => channel.PhysicalDeviceId == deviceKey && channel.ProviderKey == channelId.ProviderKey,
                cancellationToken) ?? throw new InvalidOperationException($"Physical source channel {channelId.ProviderKey} was not found.");
            var identity = Map(stored);
            if (preserveGroup)
            {
                equivalenceGroupId = identity.EquivalenceGroupId;
            }
            var number = Normalize(numberOverride) ?? stored.ProviderNumber;
            if (stored.EquivalenceGroupId is not null && equivalenceGroupId.HasValue &&
                !number.Equals(identity.EffectiveNumber, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Remove the source from its same-channel group before renumbering it.");
            }
            stored.NumberOverride = Normalize(numberOverride);
            stored.NameOverride = Normalize(nameOverride);
            stored.EquivalenceGroupId = equivalenceGroupId?.ToString("D");
            await context.SaveChangesAsync(cancellationToken);
            return Map(stored);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Atomically groups equal-number sources or adds ungrouped sources, rejecting stale previews and group merges.</summary>
    public Task GroupChannelsAsync(IReadOnlyList<PhysicalChannelIdentity> expectedTopology, IReadOnlyCollection<SourceChannelId> channelIds, CancellationToken cancellationToken = default) =>
        ApplyEquivalenceAsync(expectedTopology, channelIds, false, cancellationToken);

    /// <summary>Removes one source from its group without changing channel identity, priority, or guide bindings.</summary>
    public Task RemoveFromGroupAsync(IReadOnlyList<PhysicalChannelIdentity> expectedTopology, SourceChannelId channelId, CancellationToken cancellationToken = default) =>
        ApplyEquivalenceAsync(expectedTopology, [channelId], true, cancellationToken);

    private async Task ApplyEquivalenceAsync(IReadOnlyList<PhysicalChannelIdentity> expectedTopology, IReadOnlyCollection<SourceChannelId> channelIds, bool remove, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expectedTopology);
        ArgumentNullException.ThrowIfNull(channelIds);
        var selectedIds = channelIds.ToHashSet();
        if (selectedIds.Count != channelIds.Count || (remove ? selectedIds.Count != 1 : selectedIds.Count < 2))
        {
            throw new ArgumentException("Select distinct source channels; grouping requires at least two sources.", nameof(channelIds));
        }
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            await EpgDatabaseSchema.EnsureAsync(context, cancellationToken);
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            var stored = await context.PhysicalSourceChannels.Where(channel => channel.IsActive).ToArrayAsync(cancellationToken);
            var current = stored.Select(channel => (Stored: channel, Identity: Map(channel))).ToDictionary(channel => channel.Identity.Source.Id);
            if (current.Count != expectedTopology.Count || expectedTopology.Select(channel => channel.Source.Id).Distinct().Count() != expectedTopology.Count ||
                expectedTopology.Any(expected => !current.TryGetValue(expected.Source.Id, out var actual) || !MatchesPreview(actual.Identity, expected)))
            {
                throw new InvalidOperationException("The saved lineup changed. Reopen the same-channel preview before saving.");
            }
            if (selectedIds.Any(id => !current.ContainsKey(id)))
            {
                throw new InvalidOperationException("A selected source is no longer active. Reopen the same-channel preview.");
            }
            var selected = selectedIds.Select(id => current[id]).ToArray();
            Guid? group;
            if (remove)
            {
                if (!selected[0].Identity.EquivalenceGroupId.HasValue)
                {
                    throw new InvalidOperationException("The selected source is not in a same-channel group.");
                }
                group = null;
            }
            else
            {
                if (selected.Select(channel => channel.Identity.EffectiveNumber).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
                {
                    throw new InvalidOperationException("Same-channel sources must already have matching global numbers. Renumber them separately first.");
                }
                var groups = selected.Select(channel => channel.Identity.EquivalenceGroupId).OfType<Guid>().Distinct().ToArray();
                if (groups.Length > 1)
                {
                    throw new InvalidOperationException("Merging existing same-channel groups is not supported.");
                }
                group = groups.Length == 1 ? groups[0] : Guid.NewGuid();
                if (current.Values.Any(channel => channel.Identity.EquivalenceGroupId == group && !selectedIds.Contains(channel.Identity.Source.Id)))
                {
                    throw new InvalidOperationException("Include every active member when adding sources to an existing group.");
                }
            }
            foreach (var channel in selected)
            {
                channel.Stored.EquivalenceGroupId = group?.ToString("D");
            }
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool MatchesPreview(PhysicalChannelIdentity actual, PhysicalChannelIdentity expected) =>
        actual.LastSeenUtc == expected.LastSeenUtc &&
        actual.NumberOverride == expected.NumberOverride && actual.NameOverride == expected.NameOverride &&
        actual.EquivalenceGroupId == expected.EquivalenceGroupId && actual.IsEnabled == expected.IsEnabled &&
        actual.Source.ProviderNumber == expected.Source.ProviderNumber && actual.Source.ProviderName == expected.Source.ProviderName &&
        actual.Source.Metadata.Count == expected.Source.Metadata.Count &&
        actual.Source.Metadata.All(pair => expected.Source.Metadata.TryGetValue(pair.Key, out var value) && value == pair.Value);

    /// <summary>
    /// Enables or disables one physical source channel without affecting equivalent sources.
    /// </summary>
    public async Task<PhysicalChannelIdentity> SetEnabledAsync(SourceChannelId channelId, bool enabled, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            await EpgDatabaseSchema.EnsureAsync(context, cancellationToken);
            var deviceKey = channelId.PhysicalDeviceId.ToString();
            var stored = await context.PhysicalSourceChannels.SingleOrDefaultAsync(
                channel => channel.PhysicalDeviceId == deviceKey && channel.ProviderKey == channelId.ProviderKey,
                cancellationToken) ?? throw new InvalidOperationException($"Physical source channel {channelId.ProviderKey} was not found.");
            stored.IsEnabled = enabled;
            await context.SaveChangesAsync(cancellationToken);
            return Map(stored);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static PhysicalChannelIdentity Map(StoredPhysicalSourceChannel stored)
    {
        var physicalDeviceId = new PhysicalDeviceId(Guid.Parse(stored.PhysicalDeviceId));
        return new PhysicalChannelIdentity
        {
            Source = new PhysicalSourceChannel
            {
                Id = new SourceChannelId(physicalDeviceId, stored.ProviderKey),
                ProviderNumber = stored.ProviderNumber,
                ProviderName = stored.ProviderName,
                Metadata = string.IsNullOrWhiteSpace(stored.MetadataJson)
                    ? new Dictionary<string, string>(StringComparer.Ordinal)
                    : JsonSerializer.Deserialize<Dictionary<string, string>>(stored.MetadataJson)
                        ?? new Dictionary<string, string>(StringComparer.Ordinal)
            },
            NumberOverride = stored.NumberOverride,
            NameOverride = stored.NameOverride,
            EquivalenceGroupId = stored.EquivalenceGroupId is null ? null : Guid.Parse(stored.EquivalenceGroupId),
            IsEnabled = stored.IsEnabled,
            FirstSeenUtc = stored.FirstSeenUtc,
            LastSeenUtc = stored.LastSeenUtc
        };
    }
}

/// <summary>
/// Describes source-scoped enablement aggregated for one effective channel number.
/// </summary>
public enum PhysicalChannelEnablementState
{
    /// <summary>No active physical source uses the effective number.</summary>
    NotFound,

    /// <summary>At least one active physical source is enabled.</summary>
    Enabled,

    /// <summary>Active physical sources exist, but all are disabled.</summary>
    Disabled
}
