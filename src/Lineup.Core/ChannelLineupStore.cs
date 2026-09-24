using System.Text.Json;
using Lineup.Core.Storage;
using Lineup.Core.Storage.Entities;
using Lineup.HDHomeRun.Device.Models;
using Microsoft.EntityFrameworkCore;

namespace Lineup.Core;

/// <summary>
/// Stores the current physical tuner lineup and channel preferences in SQLite.
/// </summary>
public sealed class ChannelLineupStore
{
    private readonly IDbContextFactory<EpgDbContext> _contextFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Initializes the database-backed channel lineup store.
    /// </summary>
    /// <param name="contextFactory">Factory for isolated database operations.</param>
    public ChannelLineupStore(IDbContextFactory<EpgDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    /// <summary>
    /// Initializes a database-backed channel lineup store at the specified path.
    /// </summary>
    /// <param name="databasePath">SQLite database path.</param>
    public ChannelLineupStore(string databasePath)
        : this(new PathDbContextFactory(databasePath))
    {
    }

    /// <summary>
    /// Reads the active tuner lineup.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The saved snapshot, or <see langword="null"/> when channels have not been refreshed.</returns>
    public async Task<ChannelLineupSnapshot?> ReadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await ReadSnapshotAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Reconciles the active tuner lineup while retaining inactive channel history and preferences.
    /// </summary>
    /// <param name="channels">Combined channels from configured physical tuners.</param>
    /// <param name="cancellationToken">Cancels the transaction.</param>
    /// <returns>The saved active snapshot.</returns>
    public async Task<ChannelLineupSnapshot> StoreAsync(IEnumerable<HDHomeRunChannel> channels, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channels);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var refreshedChannels = channels
                .Where(channel => !string.IsNullOrWhiteSpace(channel.GuideNumber))
                .DistinctBy(channel => channel.GuideNumber.Trim(), StringComparer.OrdinalIgnoreCase)
                .OrderBy(channel => channel.GuideNumber, ChannelNumberComparer.Instance)
                .ToArray();
            var refreshedAt = DateTime.UtcNow;
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            await EpgDatabaseSchema.EnsureAsync(context, cancellationToken);
            var existing = await context.LineupChannels.ToDictionaryAsync(channel => channel.GuideNumber, StringComparer.OrdinalIgnoreCase, cancellationToken);
            foreach (var stored in existing.Values)
            {
                stored.IsActive = false;
            }

            foreach (var channel in refreshedChannels)
            {
                var guideNumber = channel.GuideNumber.Trim();
                if (!existing.TryGetValue(guideNumber, out var stored))
                {
                    stored = new StoredLineupChannel
                    {
                        GuideNumber = guideNumber,
                        GuideName = channel.GuideName,
                        URL = channel.URL,
                        FirstSeenUtc = refreshedAt,
                        IsEnabled = true
                    };
                    context.LineupChannels.Add(stored);
                    existing.Add(guideNumber, stored);
                }

                UpdateStoredChannel(stored, channel, refreshedAt);
            }

            await context.SaveChangesAsync(cancellationToken);
            return CreateSnapshot(refreshedAt, existing.Values.Where(channel => channel.IsActive));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Enables or disables one active channel without changing its tuner metadata.
    /// </summary>
    /// <param name="guideNumber">Logical channel number to update.</param>
    /// <param name="enabled">Whether the channel should be exposed publicly.</param>
    /// <param name="cancellationToken">Cancels the update.</param>
    /// <returns>The updated active snapshot.</returns>
    public async Task<ChannelLineupSnapshot> SetChannelEnabledAsync(string guideNumber, bool enabled, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(guideNumber);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            await EpgDatabaseSchema.EnsureAsync(context, cancellationToken);
            var normalizedGuideNumber = guideNumber.Trim();
            var channel = await context.LineupChannels.SingleOrDefaultAsync(
                stored => stored.IsActive && stored.GuideNumber == normalizedGuideNumber,
                cancellationToken)
                ?? throw new InvalidOperationException($"Channel {normalizedGuideNumber} is not present in the saved HDHomeRun lineup.");
            channel.IsEnabled = enabled;
            await context.SaveChangesAsync(cancellationToken);
            return (await ReadSnapshotAsync(cancellationToken))!;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ChannelLineupSnapshot?> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await EpgDatabaseSchema.EnsureAsync(context, cancellationToken);
        var channels = await context.LineupChannels
            .AsNoTracking()
            .Where(channel => channel.IsActive)
            .ToListAsync(cancellationToken);
        return channels.Count == 0
            ? null
            : CreateSnapshot(channels.Max(channel => channel.LastSeenUtc), channels);
    }

    private static ChannelLineupSnapshot CreateSnapshot(DateTime refreshedAtUtc, IEnumerable<StoredLineupChannel> storedChannels)
    {
        var channels = storedChannels.OrderBy(channel => channel.GuideNumber, ChannelNumberComparer.Instance).ToArray();
        return new ChannelLineupSnapshot(refreshedAtUtc, channels.Select(MapChannel).ToArray())
        {
            DisabledGuideNumbers = channels
                .Where(channel => !channel.IsEnabled)
                .Select(channel => channel.GuideNumber)
                .ToArray()
        };
    }

    private static void UpdateStoredChannel(StoredLineupChannel stored, HDHomeRunChannel channel, DateTime seenAtUtc)
    {
        stored.GuideName = channel.GuideName;
        stored.URL = channel.URL;
        stored.VideoCodec = channel.VideoCodec;
        stored.AudioCodec = channel.AudioCodec;
        stored.HD = channel.HD;
        stored.DRM = channel.DRM;
        stored.Favorite = channel.Favorite;
        stored.Tags = channel.Tags;
        stored.SignalStrength = channel.SignalStrength;
        stored.SignalQuality = channel.SignalQuality;
        stored.AdditionalPropertiesJson = channel.AdditionalProperties == null ? null : JsonSerializer.Serialize(channel.AdditionalProperties);
        stored.IsActive = true;
        stored.LastSeenUtc = seenAtUtc;
    }

    private static HDHomeRunChannel MapChannel(StoredLineupChannel stored)
    {
        return new HDHomeRunChannel
        {
            GuideNumber = stored.GuideNumber,
            GuideName = stored.GuideName,
            URL = stored.URL,
            VideoCodec = stored.VideoCodec,
            AudioCodec = stored.AudioCodec,
            HD = stored.HD,
            DRM = stored.DRM,
            Favorite = stored.Favorite,
            Tags = stored.Tags,
            SignalStrength = stored.SignalStrength,
            SignalQuality = stored.SignalQuality,
            AdditionalProperties = stored.AdditionalPropertiesJson == null
                ? null
                : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(stored.AdditionalPropertiesJson)
        };
    }

    private sealed class PathDbContextFactory : IDbContextFactory<EpgDbContext>
    {
        private readonly DbContextOptions<EpgDbContext> _options;

        internal PathDbContextFactory(string databasePath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
            var path = Path.GetFullPath(databasePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _options = new DbContextOptionsBuilder<EpgDbContext>()
                .UseSqlite($"Data Source={path};Pooling=False")
                .Options;
        }

        public EpgDbContext CreateDbContext() => new(_options);
    }
}

/// <summary>
/// Represents the active physical tuner channel lineup.
/// </summary>
/// <param name="RefreshedAtUtc">When the tuner lineup was successfully refreshed.</param>
/// <param name="Channels">The combined unique active physical tuner channels.</param>
public sealed record ChannelLineupSnapshot(DateTime RefreshedAtUtc, IReadOnlyList<HDHomeRunChannel> Channels)
{
    /// <summary>
    /// Logical channel numbers explicitly hidden from public lineups, guides, and streams.
    /// </summary>
    public IReadOnlyList<string> DisabledGuideNumbers { get; init; } = [];

    /// <summary>
    /// Gets the number of channels exposed publicly.
    /// </summary>
    public int EnabledChannelCount => Channels.Count - DisabledGuideNumbers.Count;

    /// <summary>
    /// Gets whether a logical channel is enabled.
    /// </summary>
    /// <param name="guideNumber">Logical channel number.</param>
    /// <returns><see langword="false"/> only when the channel was explicitly disabled.</returns>
    public bool IsChannelEnabled(string guideNumber)
    {
        return !DisabledGuideNumbers.Contains(guideNumber.Trim(), StringComparer.OrdinalIgnoreCase);
    }
}
