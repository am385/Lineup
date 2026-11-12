using Lineup.Core.Common.Guides;
using Lineup.Core.Extensibility.Guides;
using Lineup.Core.Storage;
using Lineup.Core.Storage.Entities;
using Lineup.Core.Common.Streaming;
using Lineup.Core.Extensibility.Streaming;
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

    /// <summary>Verifies group creation, additions, and removal preserve source identity and enabled state.</summary>
    [Fact]
    public async Task GroupChannelsAsync_CreateAddRemove_PreservesOtherIdentityChoices()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-topology-group-");
        try
        {
            var store = new StreamingTopologyStore(new TestContextFactory(Path.Combine(root.FullName, "lineup.db")));
            var device = PhysicalDeviceId.New();
            var ids = new[] { "first", "second", "third" }.Select(key => new SourceChannelId(device, key)).ToArray();
            await store.ReconcileAsync(device, ids.Select(id => CreateChannel(id, "7.1", id.ProviderKey)), TestContext.Current.CancellationToken);
            await store.SetIdentityAsync(ids[0], "7.1", "Preferred name", null, TestContext.Current.CancellationToken);
            await store.SetEnabledAsync(ids[2], false, TestContext.Current.CancellationToken);
            var original = await store.ReadActiveAsync(TestContext.Current.CancellationToken);

            // Act
            await store.GroupChannelsAsync(original, ids[..2], TestContext.Current.CancellationToken);
            var created = await store.ReadActiveAsync(TestContext.Current.CancellationToken);
            var group = created.Single(channel => channel.Source.Id == ids[0]).EquivalenceGroupId;
            await store.GroupChannelsAsync(created, ids, TestContext.Current.CancellationToken);
            var added = await store.ReadActiveAsync(TestContext.Current.CancellationToken);
            await store.RemoveFromGroupAsync(added, ids[0], TestContext.Current.CancellationToken);

            // Assert
            Assert.NotNull(group);
            Assert.All(added, channel => Assert.Equal(group, channel.EquivalenceGroupId));
            var removed = await store.ReadActiveAsync(TestContext.Current.CancellationToken);
            Assert.Null(removed.Single(channel => channel.Source.Id == ids[0]).EquivalenceGroupId);
            Assert.All(removed.Where(channel => channel.Source.Id != ids[0]), channel => Assert.Equal(group, channel.EquivalenceGroupId));
            foreach (var channel in removed)
            {
                var previous = original.Single(candidate => candidate.Source.Id == channel.Source.Id);
                Assert.Equal(previous.NumberOverride, channel.NumberOverride);
                Assert.Equal(previous.NameOverride, channel.NameOverride);
                Assert.Equal(previous.IsEnabled, channel.IsEnabled);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            root.Delete(recursive: true);
        }
    }

    /// <summary>Verifies invalid grouping cannot renumber sources, merge groups, omit members, or overwrite a stale preview.</summary>
    [Theory]
    [InlineData("different-number")]
    [InlineData("merge")]
    [InlineData("missing-member")]
    [InlineData("stale")]
    [InlineData("removed-source")]
    public async Task GroupChannelsAsync_InvalidPreview_DoesNotChangeMembership(string reason)
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-topology-group-");
        try
        {
            var store = new StreamingTopologyStore(new TestContextFactory(Path.Combine(root.FullName, "lineup.db")));
            var device = PhysicalDeviceId.New();
            var ids = new[] { "first", "second", "third", "fourth" }.Select(key => new SourceChannelId(device, key)).ToArray();
            await store.ReconcileAsync(device, ids.Select(id => CreateChannel(id, "7.1", id.ProviderKey)), TestContext.Current.CancellationToken);
            if (reason == "different-number")
            {
                await store.SetIdentityAsync(ids[1], "8.1", null, null, TestContext.Current.CancellationToken);
            }
            if (reason is "merge" or "missing-member")
            {
                await store.GroupChannelsAsync(await store.ReadActiveAsync(TestContext.Current.CancellationToken), ids[..2], TestContext.Current.CancellationToken);
            }
            if (reason == "merge")
            {
                await store.GroupChannelsAsync(await store.ReadActiveAsync(TestContext.Current.CancellationToken), ids[2..], TestContext.Current.CancellationToken);
            }
            var expected = await store.ReadActiveAsync(TestContext.Current.CancellationToken);
            if (reason == "stale")
            {
                await store.SetEnabledAsync(ids[0], false, TestContext.Current.CancellationToken);
            }
            if (reason == "removed-source")
            {
                await store.ReconcileAsync(device, ids[..3].Select(id => CreateChannel(id, "7.1", id.ProviderKey)), TestContext.Current.CancellationToken);
            }
            var before = (await store.ReadActiveAsync(TestContext.Current.CancellationToken)).ToDictionary(channel => channel.Source.Id, channel => channel.EquivalenceGroupId);
            var selected = reason == "merge" ? ids : reason == "missing-member" ? [ids[0], ids[2]] : ids[..2];

            // Act
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.GroupChannelsAsync(expected, selected, TestContext.Current.CancellationToken));

            // Assert
            var after = await store.ReadActiveAsync(TestContext.Current.CancellationToken);
            Assert.All(after, channel => Assert.Equal(before[channel.Source.Id], channel.EquivalenceGroupId));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            root.Delete(recursive: true);
        }
    }

    /// <summary>Verifies a database failure cannot commit only part of a newly confirmed group.</summary>
    [Fact]
    public async Task GroupChannelsAsync_WriteFails_RollsBackAllMembers()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-topology-group-");
        try
        {
            var factory = new TestContextFactory(Path.Combine(root.FullName, "lineup.db"));
            var store = new StreamingTopologyStore(factory);
            var device = PhysicalDeviceId.New();
            var ids = new[] { "first", "second" }.Select(key => new SourceChannelId(device, key)).ToArray();
            await store.ReconcileAsync(device, ids.Select(id => CreateChannel(id, "7.1", id.ProviderKey)), TestContext.Current.CancellationToken);
            var expected = await store.ReadActiveAsync(TestContext.Current.CancellationToken);
            await using (var context = factory.CreateDbContext())
            {
                await context.Database.ExecuteSqlRawAsync(
                    "CREATE TRIGGER RejectGroup BEFORE UPDATE OF EquivalenceGroupId ON PhysicalSourceChannels " +
                    "WHEN NEW.ProviderKey = 'second' BEGIN SELECT RAISE(ABORT, 'forced group failure'); END;",
                    TestContext.Current.CancellationToken);
            }

            // Act
            await Assert.ThrowsAsync<DbUpdateException>(() => store.GroupChannelsAsync(expected, ids, TestContext.Current.CancellationToken));

            // Assert
            Assert.All(await store.ReadActiveAsync(TestContext.Current.CancellationToken), channel => Assert.Null(channel.EquivalenceGroupId));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            root.Delete(recursive: true);
        }
    }

    /// <summary>Verifies ordinary identity edits cannot overwrite newly saved membership or renumber a grouped source.</summary>
    [Fact]
    public async Task SetDisplayIdentityAsync_SourceGroupedSinceLoad_PreservesGroupAndRejectsRenumbering()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-topology-group-");
        try
        {
            var store = new StreamingTopologyStore(new TestContextFactory(Path.Combine(root.FullName, "lineup.db")));
            var device = PhysicalDeviceId.New();
            var ids = new[] { "first", "second" }.Select(key => new SourceChannelId(device, key)).ToArray();
            await store.ReconcileAsync(device, ids.Select(id => CreateChannel(id, "7.1", id.ProviderKey)), TestContext.Current.CancellationToken);
            await store.GroupChannelsAsync(await store.ReadActiveAsync(TestContext.Current.CancellationToken), ids, TestContext.Current.CancellationToken);
            var group = (await store.ReadActiveAsync(TestContext.Current.CancellationToken))[0].EquivalenceGroupId;

            // Act
            var renamed = await store.SetDisplayIdentityAsync(ids[0], null, "Updated name", TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.SetDisplayIdentityAsync(ids[0], "8.1", "Updated name", TestContext.Current.CancellationToken));

            // Assert
            Assert.Equal(group, renamed.EquivalenceGroupId);
            Assert.Equal("Updated name", renamed.EffectiveName);
            var saved = (await store.ReadActiveAsync(TestContext.Current.CancellationToken)).Single(channel => channel.Source.Id == ids[0]);
            Assert.Equal("7.1", saved.EffectiveNumber);
            Assert.Equal(group, saved.EquivalenceGroupId);
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
