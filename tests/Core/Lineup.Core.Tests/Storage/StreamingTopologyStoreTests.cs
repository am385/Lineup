using Lineup.Common.Guides;
using Lineup.Extensibility.Guides;
using Lineup.Core.Storage;
using Lineup.Core.Storage.Entities;
using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Lineup.Core.Tests.Storage;

/// <summary>
/// Verifies provider-neutral streaming topology persistence.
/// </summary>
public sealed class StreamingTopologyStoreTests
{
    /// <summary>
    /// Verifies identity overrides survive lineup refreshes and apply to the returned channel.
    /// </summary>
    [Fact]
    public async Task ReconcileAsync_ExistingOverride_PreservesGlobalIdentity()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-topology-");
        try
        {
            var store = new StreamingTopologyStore(new TestContextFactory(Path.Combine(root.FullName, "lineup.db")));
            var deviceId = PhysicalDeviceId.New();
            var sourceId = new SourceChannelId(deviceId, "provider-7");
            await store.ReconcileAsync(deviceId, [CreateChannel(sourceId, "7.1", "News")], TestContext.Current.CancellationToken);
            await store.SetIdentityAsync(sourceId, "107.1", "Regional News", null, TestContext.Current.CancellationToken);

            // Act
            var channels = await store.ReconcileAsync(deviceId, [CreateChannel(sourceId, "7.1", "News Updated")], TestContext.Current.CancellationToken);

            // Assert
            var channel = Assert.Single(channels);
            Assert.Equal("107.1", channel.EffectiveNumber);
            Assert.Equal("Regional News", channel.EffectiveName);
            Assert.Equal("News Updated", channel.Source.ProviderName);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            root.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Verifies active reads expose global conflict-resolution overrides and equivalence groups.
    /// </summary>
    [Fact]
    public async Task ReadActiveAsync_IdentityOverride_ReturnsAuthoritativeValues()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-topology-");
        try
        {
            var store = new StreamingTopologyStore(new TestContextFactory(Path.Combine(root.FullName, "lineup.db")));
            var sourceId = new SourceChannelId(PhysicalDeviceId.New(), "provider-9");
            var equivalenceGroup = Guid.NewGuid();
            await store.ReconcileAsync(sourceId.PhysicalDeviceId, [CreateChannel(sourceId, "9.1", "News")], TestContext.Current.CancellationToken);
            await store.SetIdentityAsync(sourceId, "109.1", "Regional News", equivalenceGroup, TestContext.Current.CancellationToken);

            // Act
            var channels = await store.ReadActiveAsync(TestContext.Current.CancellationToken);

            // Assert
            var channel = Assert.Single(channels);
            Assert.Equal("109.1", channel.EffectiveNumber);
            Assert.Equal("Regional News", channel.EffectiveName);
            Assert.Equal(equivalenceGroup, channel.EquivalenceGroupId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            root.Delete(recursive: true);
        }
    }

    /// <summary>Verifies deleted physical devices remove their channels and authoritative guide bindings.</summary>
    [Fact]
    public async Task PruneDevicesAsync_DeletedDevice_RemovesOwnedChannelsAndBindings()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-topology-");
        try
        {
            var factory = new TestContextFactory(Path.Combine(root.FullName, "lineup.db"));
            var store = new StreamingTopologyStore(factory);
            var deletedDeviceId = PhysicalDeviceId.New();
            var retainedDeviceId = PhysicalDeviceId.New();
            await store.ReconcileAsync(
                deletedDeviceId,
                [CreateChannel(new SourceChannelId(deletedDeviceId, "deleted"), "7.1", "Deleted")],
                TestContext.Current.CancellationToken);
            await store.ReconcileAsync(
                retainedDeviceId,
                [CreateChannel(new SourceChannelId(retainedDeviceId, "retained"), "9.1", "Retained")],
                TestContext.Current.CancellationToken);
            await using (var context = factory.CreateDbContext())
            {
                var guideChannel = new StoredGuideSourceChannel
                {
                    GuideSourceId = GuideSourceId.New().ToString(),
                    ProviderKey = "guide",
                    Number = "7.1",
                    Name = "Guide"
                };
                context.GuideSourceChannels.Add(guideChannel);
                await context.SaveChangesAsync(TestContext.Current.CancellationToken);
                var deletedChannelId = await context.PhysicalSourceChannels
                    .Where(channel => channel.PhysicalDeviceId == deletedDeviceId.ToString())
                    .Select(channel => channel.Id)
                    .SingleAsync(TestContext.Current.CancellationToken);
                context.SourceGuideBindings.Add(new StoredSourceGuideBinding
                {
                    PhysicalSourceChannelId = deletedChannelId,
                    GuideSourceChannelId = guideChannel.Id
                });
                await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            // Act
            var removed = await store.PruneDevicesAsync([retainedDeviceId], TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal(1, removed);
            var remaining = Assert.Single(await store.ReadActiveAsync(TestContext.Current.CancellationToken));
            Assert.Equal(retainedDeviceId, remaining.Source.Id.PhysicalDeviceId);
            await using var verification = factory.CreateDbContext();
            Assert.Empty(await verification.SourceGuideBindings.ToArrayAsync(TestContext.Current.CancellationToken));
            Assert.Equal(1, await verification.PhysicalSourceChannels.CountAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            root.Delete(recursive: true);
        }
    }

    private static PhysicalSourceChannel CreateChannel(SourceChannelId id, string number, string name) => new()
    {
        Id = id,
        ProviderNumber = number,
        ProviderName = name
    };

    private sealed class TestContextFactory(string path) : IDbContextFactory<EpgDbContext>
    {
        private readonly DbContextOptions<EpgDbContext> _options = new DbContextOptionsBuilder<EpgDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;

        public EpgDbContext CreateDbContext() => new(_options);
    }
}
