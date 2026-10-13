using Lineup.Core.Storage.Entities;
using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;
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
        var channels = await context.PhysicalSourceChannels
            .AsNoTracking()
            .Where(channel => channel.IsActive)
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
    public async Task<IReadOnlyList<PhysicalChannelIdentity>> ReconcileAsync(
        PhysicalDeviceId deviceId,
        IEnumerable<PhysicalSourceChannel> channels,
        CancellationToken cancellationToken = default)
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

            await context.SourceGuideBindings
                .Where(binding => staleChannelIds.Contains(binding.PhysicalSourceChannelId))
                .ExecuteDeleteAsync(cancellationToken);
            await context.PhysicalSourceChannels
                .Where(channel => staleChannelIds.Contains(channel.Id))
                .ExecuteDeleteAsync(cancellationToken);
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
    public async Task<PhysicalChannelIdentity> SetIdentityAsync(
        SourceChannelId channelId,
        string? numberOverride,
        string? nameOverride,
        Guid? equivalenceGroupId,
        CancellationToken cancellationToken = default)
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

    /// <summary>
    /// Enables or disables one physical source channel without affecting equivalent sources.
    /// </summary>
    public async Task<PhysicalChannelIdentity> SetEnabledAsync(
        SourceChannelId channelId,
        bool enabled,
        CancellationToken cancellationToken = default)
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
