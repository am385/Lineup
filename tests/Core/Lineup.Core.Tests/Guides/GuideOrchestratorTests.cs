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

/// <summary>
/// Verifies host-neutral guide source orchestration and virtual projection.
/// </summary>
public sealed class GuideOrchestratorTests
{
    /// <summary>
    /// Verifies one source failure does not stop another refresh or replace the failed source's last committed import.
    /// </summary>
    [Fact]
    public async Task RefreshAllAsync_PartialFailure_RefreshesIndependentlyAndPreservesCommittedImport()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-guide-orchestrator-");
        try
        {
            var factory = new TestContextFactory(Path.Combine(root.FullName, "lineup.db"));
            var guideStore = new DeviceGuideStore(factory);
            var topologyStore = new StreamingTopologyStore(factory);
            var firstDevice = PhysicalDeviceId.New();
            var secondDevice = PhysicalDeviceId.New();
            var firstSource = GuideSourceId.New();
            var secondSource = GuideSourceId.New();
            await topologyStore.ReconcileAsync(firstDevice, [PhysicalChannel(firstDevice, "7")], TestContext.Current.CancellationToken);
            await topologyStore.ReconcileAsync(secondDevice, [PhysicalChannel(secondDevice, "9")], TestContext.Current.CancellationToken);
            await guideStore.ImportAsync(Import(secondSource, "9", "Previous"), TestContext.Current.CancellationToken);
            var driver = new FakeGuideDriver(new Dictionary<GuideSourceId, Func<Task<GuideImport>>>
            {
                [firstSource] = () => Task.FromResult(Import(firstSource, "7", "Fresh")),
                [secondSource] = () => Task.FromException<GuideImport>(new IOException("Second source unavailable."))
            });
            var orchestrator = new GuideOrchestrator(new StreamingAdapterRegistry([], [], [driver]), guideStore, topologyStore);
            await orchestrator.ApplyAsync(
                Configuration(
                    [PhysicalDefinition(firstDevice, 0), PhysicalDefinition(secondDevice, 1)],
                    [GuideDefinition(firstSource, firstDevice), GuideDefinition(secondSource, secondDevice)],
                    []),
                TestContext.Current.CancellationToken);

            // Act
            var results = await orchestrator.RefreshAllAsync(TestContext.Current.CancellationToken);
            var preserved = await guideStore.GetBindingDiagnosticsAsync(
                secondSource,
                TestContext.Current.CancellationToken,
                new GuideSourceScope(secondSource, [secondDevice]));

            // Assert
            Assert.Collection(
                results,
                result => Assert.True(result.Succeeded),
                result =>
                {
                    Assert.False(result.Succeeded);
                    Assert.Contains("IOException", result.Error, StringComparison.Ordinal);
                    Assert.Contains("preserved", result.Error, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("unavailable", result.Error, StringComparison.OrdinalIgnoreCase);
                });
            Assert.Equal(2, driver.RefreshCount);
            Assert.Single(preserved);
            Assert.Equal(GuideBindingState.Automatic, preserved[0].State);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            root.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Verifies a disabled guide source is not activated or refreshed.
    /// </summary>
    [Fact]
    public async Task RefreshAllAsync_DisabledGuideSource_DoesNotRefresh()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-guide-orchestrator-");
        try
        {
            var factory = new TestContextFactory(Path.Combine(root.FullName, "lineup.db"));
            var deviceId = PhysicalDeviceId.New();
            var sourceId = GuideSourceId.New();
            var driver = new FakeGuideDriver(new Dictionary<GuideSourceId, Func<Task<GuideImport>>>
            {
                [sourceId] = () => Task.FromResult(Import(sourceId, "7", "Unexpected"))
            });
            var orchestrator = new GuideOrchestrator(
                new StreamingAdapterRegistry([], [], [driver]),
                new DeviceGuideStore(factory),
                new StreamingTopologyStore(factory));
            await orchestrator.ApplyAsync(
                Configuration(
                    [PhysicalDefinition(deviceId, 0)],
                    [GuideDefinition(sourceId, deviceId) with { Enabled = false }],
                    []),
                TestContext.Current.CancellationToken);

            // Act
            var results = await orchestrator.RefreshAllAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.Empty(results);
            Assert.Equal(0, driver.RefreshCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            root.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Verifies an enabled source that cannot activate is reported as failed instead of omitted.
    /// </summary>
    [Fact]
    public async Task RefreshAllAsync_ActivationFailure_ReturnsFailedSourceResult()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-guide-orchestrator-");
        try
        {
            var factory = new TestContextFactory(Path.Combine(root.FullName, "lineup.db"));
            var deviceId = PhysicalDeviceId.New();
            var sourceId = GuideSourceId.New();
            var driver = new FakeGuideDriver(new Dictionary<GuideSourceId, Func<Task<GuideImport>>>
            {
                [sourceId] = () => Task.FromResult(Import(sourceId, "7", "Unused"))
            });
            var orchestrator = new GuideOrchestrator(
                new StreamingAdapterRegistry([], [], [driver]),
                new DeviceGuideStore(factory),
                new StreamingTopologyStore(factory));
            await orchestrator.ApplyAsync(
                Configuration(
                    [PhysicalDefinition(deviceId, 0) with { Enabled = false }],
                    [GuideDefinition(sourceId, deviceId)],
                    []),
                TestContext.Current.CancellationToken);

            // Act
            var result = Assert.Single(await orchestrator.RefreshAllAsync(TestContext.Current.CancellationToken));

            // Assert
            Assert.False(result.Succeeded);
            Assert.Equal(sourceId, result.SourceId);
            Assert.Contains("activation failed", result.Error, StringComparison.Ordinal);
            Assert.Equal(0, driver.RefreshCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            root.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Verifies virtual snapshots include only subscribed physical devices and authoritative same-device programs.
    /// </summary>
    [Fact]
    public async Task BuildSnapshotAsync_SelectedSubscription_FiltersPhysicalDevices()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-guide-orchestrator-");
        try
        {
            var factory = new TestContextFactory(Path.Combine(root.FullName, "lineup.db"));
            var guideStore = new DeviceGuideStore(factory);
            var topologyStore = new StreamingTopologyStore(factory);
            var firstDevice = PhysicalDeviceId.New();
            var secondDevice = PhysicalDeviceId.New();
            var firstSource = GuideSourceId.New();
            var secondSource = GuideSourceId.New();
            var virtualDevice = VirtualDeviceId.New();
            await topologyStore.ReconcileAsync(firstDevice, [PhysicalChannel(firstDevice, "7")], TestContext.Current.CancellationToken);
            await topologyStore.ReconcileAsync(secondDevice, [PhysicalChannel(secondDevice, "9")], TestContext.Current.CancellationToken);
            await guideStore.ImportAsync(Import(firstSource, "7", "Included"), TestContext.Current.CancellationToken);
            await guideStore.ImportAsync(Import(secondSource, "9", "Excluded"), TestContext.Current.CancellationToken);
            await guideStore.SetBindingAsync(
                new SourceChannelId(firstDevice, "7"),
                new GuideChannelId(firstSource, "guide-7"),
                TestContext.Current.CancellationToken);
            await guideStore.SetBindingAsync(
                new SourceChannelId(secondDevice, "9"),
                new GuideChannelId(secondSource, "guide-9"),
                TestContext.Current.CancellationToken);
            var orchestrator = new GuideOrchestrator(
                new StreamingAdapterRegistry([], [new FakeVirtualDriver()], [new FakeGuideDriver(new Dictionary<GuideSourceId, Func<Task<GuideImport>>>
                {
                    [firstSource] = () => Task.FromResult(Import(firstSource, "7", "Included")),
                    [secondSource] = () => Task.FromResult(Import(secondSource, "9", "Excluded"))
                })]),
                guideStore,
                topologyStore);
            await orchestrator.ApplyAsync(
                Configuration(
                    [PhysicalDefinition(firstDevice, 0), PhysicalDefinition(secondDevice, 1)],
                    [GuideDefinition(firstSource, firstDevice), GuideDefinition(secondSource, secondDevice)],
                    [VirtualDefinition(virtualDevice, firstDevice)]),
                TestContext.Current.CancellationToken);

            // Act
            var snapshot = await orchestrator.BuildSnapshotAsync(virtualDevice, TestContext.Current.CancellationToken);

            // Assert
            var channel = Assert.Single(snapshot.Channels);
            Assert.Equal("7", channel.Number);
            Assert.Equal("Included", Assert.Single(channel.Programs).Title);
            Assert.Equal(virtualDevice, channel.ChannelId.VirtualDeviceId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            root.Delete(recursive: true);
        }
    }

    private static StreamingConfiguration Configuration(
        IReadOnlyList<PhysicalDeviceDefinition> physicalDevices,
        IReadOnlyList<GuideSourceDefinition> guideSources,
        IReadOnlyList<VirtualDeviceDefinition> virtualDevices) =>
        new()
        {
            PhysicalDevices = physicalDevices,
            GuideSources = guideSources,
            VirtualDevices = virtualDevices
        };

    private static PhysicalDeviceDefinition PhysicalDefinition(PhysicalDeviceId deviceId, int order) =>
        new()
        {
            Id = deviceId,
            Name = $"Device {order}",
            Order = order,
            Adapter = Adapter("unused-physical")
        };

    private static GuideSourceDefinition GuideDefinition(GuideSourceId sourceId, PhysicalDeviceId deviceId) =>
        new()
        {
            Id = sourceId,
            Name = $"Guide {sourceId}",
            Adapter = Adapter(FakeGuideDriver.DriverKind),
            Subscription = new PhysicalDeviceSubscription
            {
                Mode = PhysicalDeviceSubscriptionMode.Selected,
                SelectedDeviceIds = [deviceId]
            }
        };

    private static VirtualDeviceDefinition VirtualDefinition(VirtualDeviceId virtualDeviceId, PhysicalDeviceId selectedDeviceId) =>
        new()
        {
            Id = virtualDeviceId,
            Name = "Selected",
            Adapter = Adapter("unused-virtual"),
            Subscription = new PhysicalDeviceSubscription
            {
                Mode = PhysicalDeviceSubscriptionMode.Selected,
                SelectedDeviceIds = new HashSet<PhysicalDeviceId> { selectedDeviceId }
            }
        };

    private static AdapterConfiguration Adapter(string kind)
    {
        using var document = JsonDocument.Parse("{}");
        return new AdapterConfiguration(kind, document.RootElement.Clone());
    }

    private static PhysicalSourceChannel PhysicalChannel(PhysicalDeviceId deviceId, string number) =>
        new()
        {
            Id = new SourceChannelId(deviceId, number),
            ProviderNumber = number,
            ProviderName = $"Channel {number}"
        };

    private static GuideImport Import(GuideSourceId sourceId, string number, string title)
    {
        var channelId = new GuideChannelId(sourceId, $"guide-{number}");
        var start = DateTimeOffset.Parse("2026-10-02T12:00:00Z");
        return new GuideImport
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
                    Title = title
                }
            ],
            CoverageStartUtc = start,
            CoverageEndUtc = start.AddHours(1)
        };
    }

    private sealed class FakeGuideDriver(IReadOnlyDictionary<GuideSourceId, Func<Task<GuideImport>>> refreshes) : IGuideSourceDriver
    {
        public const string DriverKind = "fake-guide";
        private int _refreshCount;

        public string Kind => DriverKind;

        public string DisplayName => "Fake Guide";

        public bool SupportsUserCreation => true;

        public int RefreshCount => Volatile.Read(ref _refreshCount);

        public AdapterConfiguration CreateDefaultConfiguration(GuideSourceId sourceId) => Adapter(Kind);

        public bool SupportsPhysicalAdapter(string physicalAdapterKind) =>
            string.Equals(physicalAdapterKind, "unused-physical", StringComparison.Ordinal);

        public IReadOnlyList<string> Validate(AdapterConfiguration configuration) => [];

        public ValueTask<IGuideSource> CreateAsync(
            GuideSourceId sourceId,
            AdapterConfiguration configuration,
            IReadOnlyList<PhysicalDeviceId> physicalDeviceIds,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IGuideSource>(new FakeGuideSource(sourceId, refreshes[sourceId], () => Interlocked.Increment(ref _refreshCount)));
    }

    private sealed class FakeGuideSource(GuideSourceId id, Func<Task<GuideImport>> refresh, Action onRefresh) : IGuideSource
    {
        public GuideSourceId Id { get; } = id;

        public Task<GuideImport> RefreshAsync(CancellationToken cancellationToken = default)
        {
            onRefresh();
            return refresh();
        }
    }

    private sealed class FakeVirtualDriver : IVirtualTunerDriver
    {
        public string Kind => "unused-virtual";

        public string DisplayName => "Unused";

        public bool SupportsUserCreation => false;

        public AdapterConfiguration CreateDefaultConfiguration(VirtualDeviceId deviceId) => Adapter(Kind);

        public IReadOnlyList<string> Validate(AdapterConfiguration configuration) => [];
    }

    private sealed class TestContextFactory(string path) : IDbContextFactory<EpgDbContext>
    {
        private readonly DbContextOptions<EpgDbContext> _options = new DbContextOptionsBuilder<EpgDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;

        public EpgDbContext CreateDbContext() => new(_options);
    }
}
