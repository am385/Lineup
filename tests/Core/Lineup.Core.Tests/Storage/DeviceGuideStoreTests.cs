using Lineup.Common.Guides;
using Lineup.Extensibility.Guides;
using Lineup.Core.Storage;
using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Lineup.Core.Tests.Storage;

/// <summary>
/// Verifies guide imports and physical-channel bindings.
/// </summary>
public sealed class DeviceGuideStoreTests
{
    /// <summary>
    /// Verifies guide channels cannot bind outside their configured physical-device subscription.
    /// </summary>
    [Fact]
    public async Task SetBindingAsync_UnsubscribedPhysicalDevice_RejectsBinding()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-device-guide-");
        try
        {
            var store = new DeviceGuideStore(new TestContextFactory(Path.Combine(root.FullName, "lineup.db")));
            var sourceId = new SourceChannelId(PhysicalDeviceId.New(), "source");
            var guideId = new GuideChannelId(GuideSourceId.New(), "guide");
            var scopes = new[] { new GuideSourceScope(guideId.GuideSourceId, [PhysicalDeviceId.New()]) };

            // Act
            var exception = await Assert.ThrowsAsync<ArgumentException>(
                () => store.SetBindingAsync(sourceId, guideId, TestContext.Current.CancellationToken, scopes));

            // Assert
            Assert.Contains("not subscribed", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            root.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Verifies an import rejects programs that claim another guide source.
    /// </summary>
    [Fact]
    public async Task ImportAsync_ProgramFromDifferentGuideSource_RejectsImport()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-device-guide-");
        try
        {
            var store = new DeviceGuideStore(new TestContextFactory(Path.Combine(root.FullName, "lineup.db")));
            var owner = PhysicalDeviceId.New();
            var sourceId = GuideSourceId.New();
            var channelId = new GuideChannelId(sourceId, "7.1");
            var import = new GuideImport
            {
                SourceId = sourceId,
                Channels = [new GuideChannel { Id = channelId, Number = "7.1", Name = "News" }],
                Programs =
                [
                    new GuideProgram
                    {
                        ChannelId = new GuideChannelId(GuideSourceId.New(), "7.1"),
                        StartUtc = DateTimeOffset.UtcNow,
                        EndUtc = DateTimeOffset.UtcNow.AddHours(1),
                        Title = "Program"
                    }
                ],
                CoverageStartUtc = DateTimeOffset.UtcNow,
                CoverageEndUtc = DateTimeOffset.UtcNow.AddHours(1)
            };

            // Act
            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => store.ImportAsync(import, TestContext.Current.CancellationToken));

            // Assert
            Assert.Contains("does not belong", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            root.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Verifies ambiguous number matches remain unresolved until an authoritative guide is selected.
    /// </summary>
    [Fact]
    public async Task ReadBindingStatesAsync_AmbiguousCandidates_ReportsAndPersistsAuthoritativeSelection()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-device-guide-");
        try
        {
            var factory = new TestContextFactory(Path.Combine(root.FullName, "lineup.db"));
            var topology = new StreamingTopologyStore(factory);
            var store = new DeviceGuideStore(factory);
            var owner = PhysicalDeviceId.New();
            var sourceChannelId = new SourceChannelId(owner, "source-7");
            var guideSourceId = GuideSourceId.New();
            var firstGuideId = new GuideChannelId(guideSourceId, "guide-a");
            var secondGuideId = new GuideChannelId(guideSourceId, "guide-b");
            await topology.ReconcileAsync(
                owner,
                [new PhysicalSourceChannel { Id = sourceChannelId, ProviderNumber = "7.1", ProviderName = "News" }],
                TestContext.Current.CancellationToken);
            await store.ImportAsync(
                new GuideImport
                {
                    SourceId = guideSourceId,
                    Channels =
                    [
                        new GuideChannel { Id = firstGuideId, Number = "7.1", Name = "News East" },
                        new GuideChannel { Id = secondGuideId, Number = "7.1", Name = "News West" }
                    ],
                    Programs = [],
                    CoverageStartUtc = DateTimeOffset.UtcNow,
                    CoverageEndUtc = DateTimeOffset.UtcNow.AddDays(1)
                },
                TestContext.Current.CancellationToken);

            // Act
            var ambiguous = Assert.Single(await store.ReadBindingStatesAsync(TestContext.Current.CancellationToken));
            await store.SetBindingAsync(sourceChannelId, secondGuideId, TestContext.Current.CancellationToken);
            var selected = Assert.Single(await store.ReadBindingStatesAsync(TestContext.Current.CancellationToken));

            // Assert
            Assert.True(ambiguous.IsAmbiguous);
            Assert.Equal(GuideBindingState.NeedsGuideSelection, ambiguous.State);
            Assert.Equal(2, ambiguous.MatchingCandidates.Count);
            Assert.False(selected.IsAmbiguous);
            Assert.Equal(GuideBindingState.Manual, selected.State);
            Assert.Equal(secondGuideId, selected.SelectedGuideChannelId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            root.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Verifies a unique original provider-number match publishes automatically despite a global number override.
    /// </summary>
    [Fact]
    public async Task BuildSnapshotAsync_UniqueOriginalNumberMatch_PublishesAutomatically()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-device-guide-");
        try
        {
            var factory = new TestContextFactory(Path.Combine(root.FullName, "lineup.db"));
            var topology = new StreamingTopologyStore(factory);
            var store = new DeviceGuideStore(factory);
            var owner = PhysicalDeviceId.New();
            var sourceChannelId = new SourceChannelId(owner, "physical-station");
            var guideSourceId = GuideSourceId.New();
            var guideChannelId = new GuideChannelId(guideSourceId, "guide-station");
            var start = DateTimeOffset.Parse("2026-10-02T12:00:00Z");
            await topology.ReconcileAsync(
                owner,
                [new PhysicalSourceChannel { Id = sourceChannelId, ProviderNumber = "7.1", ProviderName = "News" }],
                TestContext.Current.CancellationToken);
            await topology.SetIdentityAsync(sourceChannelId, "107.1", null, null, TestContext.Current.CancellationToken);
            await store.ImportAsync(
                Import(
                    guideSourceId,
                    [new GuideChannel { Id = guideChannelId, Number = "7.1", Name = "News Guide" }],
                    [new GuideProgram { ChannelId = guideChannelId, StartUtc = start, EndUtc = start.AddHours(1), Title = "Noon News" }]),
                TestContext.Current.CancellationToken);
            var virtualDeviceId = VirtualDeviceId.New();

            // Act
            var binding = Assert.Single(await store.ReadBindingStatesAsync(TestContext.Current.CancellationToken));
            var snapshot = await store.BuildSnapshotAsync(
                virtualDeviceId,
                [new ResolvedVirtualChannel("107.1", "News", [sourceChannelId])],
                cancellationToken: TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal(GuideBindingState.Automatic, binding.State);
            Assert.Equal(guideChannelId, binding.SelectedGuideChannelId);
            var channel = Assert.Single(snapshot.Channels);
            Assert.Equal("Noon News", Assert.Single(channel.Programs).Title);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            root.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Verifies one imported guide channel can automatically serve channels on multiple subscribed physical devices.
    /// </summary>
    [Fact]
    public async Task ReadBindingStatesAsync_SharedGuideSource_MapsAcrossSubscribedDevices()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-device-guide-");
        try
        {
            var factory = new TestContextFactory(Path.Combine(root.FullName, "lineup.db"));
            var topology = new StreamingTopologyStore(factory);
            var store = new DeviceGuideStore(factory);
            var firstDeviceId = PhysicalDeviceId.New();
            var secondDeviceId = PhysicalDeviceId.New();
            var firstChannelId = new SourceChannelId(firstDeviceId, "first-station");
            var secondChannelId = new SourceChannelId(secondDeviceId, "second-station");
            var guideSourceId = GuideSourceId.New();
            var guideChannelId = new GuideChannelId(guideSourceId, "shared-station");
            await topology.ReconcileAsync(
                firstDeviceId,
                [new PhysicalSourceChannel { Id = firstChannelId, ProviderNumber = "7.1", ProviderName = "News" }],
                TestContext.Current.CancellationToken);
            await topology.ReconcileAsync(
                secondDeviceId,
                [new PhysicalSourceChannel { Id = secondChannelId, ProviderNumber = "7.1", ProviderName = "News" }],
                TestContext.Current.CancellationToken);
            await store.ImportAsync(
                Import(guideSourceId, [new GuideChannel { Id = guideChannelId, Number = "7.1", Name = "News Guide" }]),
                TestContext.Current.CancellationToken);
            var scopes = new[] { new GuideSourceScope(guideSourceId, [firstDeviceId, secondDeviceId]) };

            // Act
            var bindings = await store.ReadBindingStatesAsync(TestContext.Current.CancellationToken, scopes);

            // Assert
            Assert.Equal(2, bindings.Count);
            Assert.All(bindings, binding => Assert.Equal(GuideBindingState.Automatic, binding.State));
            Assert.All(bindings, binding => Assert.Equal(guideChannelId, binding.SelectedGuideChannelId));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            root.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Verifies a manual override wins and clearing it restores the unique automatic match.
    /// </summary>
    [Fact]
    public async Task ClearBindingAsync_ManualOverride_RestoresAutomaticMatch()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-device-guide-");
        try
        {
            var factory = new TestContextFactory(Path.Combine(root.FullName, "lineup.db"));
            var topology = new StreamingTopologyStore(factory);
            var store = new DeviceGuideStore(factory);
            var owner = PhysicalDeviceId.New();
            var sourceChannelId = new SourceChannelId(owner, "automatic");
            var guideSourceId = GuideSourceId.New();
            var automaticGuideId = new GuideChannelId(guideSourceId, "automatic");
            var manualGuideId = new GuideChannelId(guideSourceId, "manual");
            await topology.ReconcileAsync(
                owner,
                [new PhysicalSourceChannel { Id = sourceChannelId, ProviderNumber = "7.1", ProviderName = "News" }],
                TestContext.Current.CancellationToken);
            await store.ImportAsync(
                Import(
                    guideSourceId,
                    [
                        new GuideChannel { Id = automaticGuideId, Number = "7.1", Name = "Automatic" },
                        new GuideChannel { Id = manualGuideId, Number = "99", Name = "Manual" }
                    ]),
                TestContext.Current.CancellationToken);

            // Act
            var automatic = Assert.Single(await store.ReadBindingStatesAsync(TestContext.Current.CancellationToken));
            await store.SetBindingAsync(sourceChannelId, manualGuideId, TestContext.Current.CancellationToken);
            var manual = Assert.Single(await store.ReadBindingStatesAsync(TestContext.Current.CancellationToken));
            await store.ClearBindingAsync(sourceChannelId, TestContext.Current.CancellationToken);
            var restored = Assert.Single(await store.ReadBindingStatesAsync(TestContext.Current.CancellationToken));

            // Assert
            Assert.Equal(GuideBindingState.Automatic, automatic.State);
            Assert.Equal(automaticGuideId, automatic.SelectedGuideChannelId);
            Assert.Equal(GuideBindingState.Manual, manual.State);
            Assert.Equal(manualGuideId, manual.SelectedGuideChannelId);
            Assert.Contains(manual.MatchingCandidates, candidate => candidate.Id == manualGuideId);
            Assert.Equal(GuideBindingState.Automatic, restored.State);
            Assert.Equal(automaticGuideId, restored.SelectedGuideChannelId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            root.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Verifies an unavailable manual selection remains unresolved instead of silently switching to an automatic match.
    /// </summary>
    [Fact]
    public async Task BuildSnapshotAsync_UnavailableManualSelection_DoesNotFallBackAutomatically()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-device-guide-");
        try
        {
            var factory = new TestContextFactory(Path.Combine(root.FullName, "lineup.db"));
            var topology = new StreamingTopologyStore(factory);
            var store = new DeviceGuideStore(factory);
            var owner = PhysicalDeviceId.New();
            var sourceChannelId = new SourceChannelId(owner, "automatic");
            var guideSourceId = GuideSourceId.New();
            var automaticGuideId = new GuideChannelId(guideSourceId, "automatic");
            var manualGuideId = new GuideChannelId(guideSourceId, "manual");
            await topology.ReconcileAsync(
                owner,
                [new PhysicalSourceChannel { Id = sourceChannelId, ProviderNumber = "7.1", ProviderName = "News" }],
                TestContext.Current.CancellationToken);
            await store.ImportAsync(
                Import(
                    guideSourceId,
                    [
                        new GuideChannel { Id = automaticGuideId, Number = "7.1", Name = "Automatic" },
                        new GuideChannel { Id = manualGuideId, Number = "99", Name = "Manual" }
                    ]),
                TestContext.Current.CancellationToken);
            await store.SetBindingAsync(sourceChannelId, manualGuideId, TestContext.Current.CancellationToken);
            await store.ImportAsync(
                Import(
                    guideSourceId,
                    [new GuideChannel { Id = automaticGuideId, Number = "7.1", Name = "Automatic" }],
                    [
                        new GuideProgram
                        {
                            ChannelId = automaticGuideId,
                            StartUtc = DateTimeOffset.Parse("2026-10-02T12:00:00Z"),
                            EndUtc = DateTimeOffset.Parse("2026-10-02T13:00:00Z"),
                            Title = "Should Not Publish"
                        }
                    ]),
                TestContext.Current.CancellationToken);

            // Act
            var binding = Assert.Single(await store.ReadBindingStatesAsync(TestContext.Current.CancellationToken));
            var snapshot = await store.BuildSnapshotAsync(
                VirtualDeviceId.New(),
                [new ResolvedVirtualChannel("7.1", "News", [sourceChannelId])],
                cancellationToken: TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal(GuideBindingState.ManualUnavailable, binding.State);
            Assert.Equal(manualGuideId, binding.SelectedGuideChannelId);
            Assert.Empty(Assert.Single(snapshot.Channels).Programs);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            root.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Verifies a manual selection becomes unavailable when its guide source stops subscribing to the physical device.
    /// </summary>
    [Fact]
    public async Task ReadBindingStatesAsync_UnsubscribedManualSource_RemainsUnavailable()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-device-guide-");
        try
        {
            var factory = new TestContextFactory(Path.Combine(root.FullName, "lineup.db"));
            var topology = new StreamingTopologyStore(factory);
            var store = new DeviceGuideStore(factory);
            var physicalDeviceId = PhysicalDeviceId.New();
            var sourceChannelId = new SourceChannelId(physicalDeviceId, "station");
            var guideSourceId = GuideSourceId.New();
            var guideChannelId = new GuideChannelId(guideSourceId, "station");
            await topology.ReconcileAsync(
                physicalDeviceId,
                [new PhysicalSourceChannel { Id = sourceChannelId, ProviderNumber = "7.1", ProviderName = "News" }],
                TestContext.Current.CancellationToken);
            await store.ImportAsync(
                Import(guideSourceId, [new GuideChannel { Id = guideChannelId, Number = "7.1", Name = "News" }]),
                TestContext.Current.CancellationToken);
            var subscribedScopes = new[] { new GuideSourceScope(guideSourceId, [physicalDeviceId]) };
            await store.SetBindingAsync(
                sourceChannelId,
                guideChannelId,
                TestContext.Current.CancellationToken,
                subscribedScopes);
            var unsubscribedScopes = new[] { new GuideSourceScope(guideSourceId, []) };

            // Act
            var binding = Assert.Single(
                await store.ReadBindingStatesAsync(TestContext.Current.CancellationToken, unsubscribedScopes));

            // Assert
            Assert.Equal(GuideBindingState.ManualUnavailable, binding.State);
            Assert.Equal(guideChannelId, binding.SelectedGuideChannelId);
            Assert.Empty(binding.MatchingCandidates);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            root.Delete(recursive: true);
        }
    }

    private static GuideImport Import(GuideSourceId sourceId, IReadOnlyList<GuideChannel> channels, IReadOnlyList<GuideProgram>? programs = null)
    {
        var start = DateTimeOffset.Parse("2026-10-02T12:00:00Z");
        return new GuideImport
        {
            SourceId = sourceId,
            Channels = channels,
            Programs = programs ?? [],
            CoverageStartUtc = start,
            CoverageEndUtc = start.AddDays(1)
        };
    }

    private sealed class TestContextFactory(string path) : IDbContextFactory<EpgDbContext>
    {
        private readonly DbContextOptions<EpgDbContext> _options = new DbContextOptionsBuilder<EpgDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;

        public EpgDbContext CreateDbContext() => new(_options);
    }
}
