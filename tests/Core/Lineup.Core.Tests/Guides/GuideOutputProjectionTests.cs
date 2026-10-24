using System.Text.Json;
using Lineup.Common.Guides;
using Lineup.Extensibility.Guides;
using Lineup.Core.Storage;
using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Lineup.Core.Tests.Guides;

/// <summary>Verifies guide-output topology projections.</summary>
public sealed class GuideOutputProjectionTests
{
    /// <summary>Verifies selected virtual devices and guide sources constrain the output projection.</summary>
    [Fact]
    public async Task BuildOutputSnapshotAsync_SelectedScopes_ExcludesOtherChannelsAndPrograms()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-guide-output-");
        try
        {
            var factory = new TestContextFactory(Path.Combine(root.FullName, "lineup.db"));
            var topology = new StreamingTopologyStore(factory);
            var guideStore = new DeviceGuideStore(factory);
            var firstPhysical = PhysicalDeviceId.New();
            var secondPhysical = PhysicalDeviceId.New();
            var firstVirtual = VirtualDeviceId.New();
            var secondVirtual = VirtualDeviceId.New();
            var firstGuide = GuideSourceId.New();
            var secondGuide = GuideSourceId.New();
            await topology.ReconcileAsync(firstPhysical, [PhysicalChannel(firstPhysical, "2")], TestContext.Current.CancellationToken);
            await topology.ReconcileAsync(secondPhysical, [PhysicalChannel(secondPhysical, "4")], TestContext.Current.CancellationToken);
            await ImportAsync(guideStore, firstGuide, "2", TestContext.Current.CancellationToken);
            await ImportAsync(guideStore, secondGuide, "4", TestContext.Current.CancellationToken);
            var configuration = new StreamingConfiguration
            {
                PhysicalDevices =
                [
                    PhysicalDevice(firstPhysical, 0),
                    PhysicalDevice(secondPhysical, 1)
                ],
                VirtualDevices =
                [
                    VirtualDevice(firstVirtual, firstPhysical),
                    VirtualDevice(secondVirtual, secondPhysical)
                ],
                GuideSources =
                [
                    GuideSource(firstGuide, firstPhysical),
                    GuideSource(secondGuide, secondPhysical)
                ]
            };
            var output = new GuideOutputDefinition
            {
                Id = GuideOutputId.New(),
                Name = "First device",
                Adapter = Adapter("xmltv"),
                VirtualDeviceSelectionMode = GuideOutputSelectionMode.Selected,
                SelectedVirtualDeviceIds = [firstVirtual],
                GuideSourceSelectionMode = GuideOutputSelectionMode.Selected,
                SelectedGuideSourceIds = [firstGuide]
            };
            var service = new GuideQueryService(
                topology,
                guideStore,
                new StreamingAdapterRegistry([], [new FakeVirtualDriver()], [new FakeGuideDriver()]));

            // Act
            var snapshot = await service.BuildOutputSnapshotAsync(
                configuration,
                output,
                cancellationToken: TestContext.Current.CancellationToken);

            // Assert
            var channel = Assert.Single(snapshot.Channels);
            Assert.Equal("2", channel.Number);
            Assert.Equal(firstGuide, Assert.Single(channel.Programs).ChannelId.GuideSourceId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            root.Delete(recursive: true);
        }
    }

    /// <summary>Verifies an empty explicit selection dynamically resolves to all enabled items.</summary>
    [Fact]
    public async Task BuildOutputSnapshotAsync_EmptySelections_UseAllEnabledItems()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-guide-output-");
        try
        {
            var factory = new TestContextFactory(Path.Combine(root.FullName, "lineup.db"));
            var topology = new StreamingTopologyStore(factory);
            var guideStore = new DeviceGuideStore(factory);
            var firstPhysical = PhysicalDeviceId.New();
            var secondPhysical = PhysicalDeviceId.New();
            var firstGuide = GuideSourceId.New();
            var secondGuide = GuideSourceId.New();
            await topology.ReconcileAsync(firstPhysical, [PhysicalChannel(firstPhysical, "2")], TestContext.Current.CancellationToken);
            await topology.ReconcileAsync(secondPhysical, [PhysicalChannel(secondPhysical, "4")], TestContext.Current.CancellationToken);
            await ImportAsync(guideStore, firstGuide, "2", TestContext.Current.CancellationToken);
            await ImportAsync(guideStore, secondGuide, "4", TestContext.Current.CancellationToken);
            var configuration = new StreamingConfiguration
            {
                PhysicalDevices = [PhysicalDevice(firstPhysical, 0), PhysicalDevice(secondPhysical, 1)],
                VirtualDevices =
                [
                    VirtualDevice(VirtualDeviceId.New(), firstPhysical),
                    VirtualDevice(VirtualDeviceId.New(), secondPhysical)
                ],
                GuideSources = [GuideSource(firstGuide, firstPhysical), GuideSource(secondGuide, secondPhysical)]
            };
            var output = new GuideOutputDefinition
            {
                Id = GuideOutputId.New(),
                Name = "Everything",
                Adapter = Adapter("xmltv"),
                VirtualDeviceSelectionMode = GuideOutputSelectionMode.Selected,
                GuideSourceSelectionMode = GuideOutputSelectionMode.Selected
            };
            var service = new GuideQueryService(
                topology,
                guideStore,
                new StreamingAdapterRegistry([], [new FakeVirtualDriver()], [new FakeGuideDriver()]));

            // Act
            var snapshot = await service.BuildOutputSnapshotAsync(
                configuration,
                output,
                cancellationToken: TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal(["2", "4"], snapshot.Channels.Select(channel => channel.Number));
            Assert.Equal(2, snapshot.Channels.Sum(channel => channel.Programs.Count));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            root.Delete(recursive: true);
        }
    }

    private static PhysicalDeviceDefinition PhysicalDevice(PhysicalDeviceId id, int order) => new()
    {
        Id = id,
        Name = id.ToString(),
        Order = order,
        Adapter = Adapter("physical")
    };

    private static VirtualDeviceDefinition VirtualDevice(VirtualDeviceId id, PhysicalDeviceId physicalId) => new()
    {
        Id = id,
        Name = id.ToString(),
        Adapter = Adapter("virtual"),
        Subscription = new PhysicalDeviceSubscription
        {
            Mode = PhysicalDeviceSubscriptionMode.Selected,
            SelectedDeviceIds = [physicalId]
        }
    };

    private static GuideSourceDefinition GuideSource(GuideSourceId id, PhysicalDeviceId physicalId) => new()
    {
        Id = id,
        Name = id.ToString(),
        Adapter = Adapter("guide"),
        Subscription = new PhysicalDeviceSubscription
        {
            Mode = PhysicalDeviceSubscriptionMode.Selected,
            SelectedDeviceIds = [physicalId]
        }
    };

    private static PhysicalSourceChannel PhysicalChannel(PhysicalDeviceId id, string number) => new()
    {
        Id = new SourceChannelId(id, number),
        ProviderNumber = number,
        ProviderName = $"Channel {number}"
    };

    private static async Task ImportAsync(
        DeviceGuideStore store,
        GuideSourceId sourceId,
        string number,
        CancellationToken cancellationToken)
    {
        var channelId = new GuideChannelId(sourceId, number);
        var start = DateTimeOffset.UtcNow;
        await store.ImportAsync(
            new GuideImport
            {
                SourceId = sourceId,
                Channels = [new GuideChannel { Id = channelId, Number = number, Name = $"Guide {number}" }],
                Programs =
                [
                    new GuideProgram
                    {
                        ChannelId = channelId,
                        StartUtc = start,
                        EndUtc = start.AddHours(1),
                        Title = $"Program {number}"
                    }
                ],
                CoverageStartUtc = start,
                CoverageEndUtc = start.AddHours(1)
            },
            cancellationToken);
    }

    private static AdapterConfiguration Adapter(string kind) =>
        new(kind, JsonSerializer.SerializeToElement(new { }));

    private sealed class FakeVirtualDriver : IVirtualTunerDriver
    {
        public string Kind => "virtual";

        public string DisplayName => "Virtual";

        public bool SupportsUserCreation => true;

        public AdapterConfiguration CreateDefaultConfiguration(VirtualDeviceId deviceId) => Adapter(Kind);

        public IReadOnlyList<string> Validate(AdapterConfiguration configuration) => [];
    }

    private sealed class FakeGuideDriver : IGuideSourceDriver
    {
        public string Kind => "guide";

        public string DisplayName => "Guide";

        public bool SupportsUserCreation => true;

        public AdapterConfiguration CreateDefaultConfiguration(GuideSourceId sourceId) => Adapter(Kind);

        public bool SupportsPhysicalAdapter(string physicalAdapterKind) => true;

        public IReadOnlyList<string> Validate(AdapterConfiguration configuration) => [];

        public ValueTask<IGuideSource> CreateAsync(
            GuideSourceId sourceId,
            AdapterConfiguration configuration,
            IReadOnlyList<PhysicalDeviceId> physicalDeviceIds,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class TestContextFactory(string path) : IDbContextFactory<EpgDbContext>
    {
        private readonly DbContextOptions<EpgDbContext> _options = new DbContextOptionsBuilder<EpgDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;

        public EpgDbContext CreateDbContext() => new(_options);
    }
}
