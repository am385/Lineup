using System.Text.Json;
using Lineup.Core.Storage;
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

    /// <summary>Verifies unavailable sources are isolated consistently without resurrecting their cached programs.</summary>
    [Theory]
    [InlineData("missing-adapter", GuideSourceDiagnosticCode.AdapterUnavailable, false)]
    [InlineData("incompatible", GuideSourceDiagnosticCode.InvalidSubscription, false)]
    [InlineData("unknown-device", GuideSourceDiagnosticCode.InvalidSubscription, false)]
    [InlineData("disabled-device", GuideSourceDiagnosticCode.NoCompatibleDevices, false)]
    [InlineData("missing-adapter", GuideSourceDiagnosticCode.AdapterUnavailable, true)]
    [InlineData("incompatible", GuideSourceDiagnosticCode.InvalidSubscription, true)]
    [InlineData("unknown-device", GuideSourceDiagnosticCode.InvalidSubscription, true)]
    [InlineData("disabled-device", GuideSourceDiagnosticCode.NoCompatibleDevices, true)]
    [InlineData("all-incompatible", GuideSourceDiagnosticCode.NoCompatibleDevices, true)]
    public async Task Projections_IneligibleSources_ReturnPartialResultsWithDiagnostics(string failure, GuideSourceDiagnosticCode code, bool allExcluded)
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-partial-guide-");
        try
        {
            var factory = new TestContextFactory(Path.Combine(root.FullName, "lineup.db"));
            var topology = new StreamingTopologyStore(factory);
            var store = new DeviceGuideStore(factory);
            var healthyDevice = PhysicalDeviceId.New();
            var badDevice = PhysicalDeviceId.New();
            var healthySource = GuideSourceId.New();
            var badSource = GuideSourceId.New();
            var virtualDevice = VirtualDeviceId.New();
            await topology.ReconcileAsync(healthyDevice, [PhysicalChannel(healthyDevice, "2")], TestContext.Current.CancellationToken);
            await topology.ReconcileAsync(badDevice, [PhysicalChannel(badDevice, "4")], TestContext.Current.CancellationToken);
            await ImportAsync(store, healthySource, "2", TestContext.Current.CancellationToken);
            await ImportAsync(store, badSource, "4", TestContext.Current.CancellationToken);
            var badDefinition = GuideSource(badSource, failure == "unknown-device" ? PhysicalDeviceId.New() : badDevice) with { Name = "Degraded source" };
            if (failure == "missing-adapter")
            {
                badDefinition = badDefinition with { Adapter = Adapter("missing-private-provider-detail") };
            }
            if (failure == "all-incompatible")
            {
                badDefinition = badDefinition with { Subscription = new PhysicalDeviceSubscription { Mode = PhysicalDeviceSubscriptionMode.All } };
            }
            var configuration = new StreamingConfiguration
            {
                PhysicalDevices =
                [
                    PhysicalDevice(healthyDevice, 0) with { Adapter = Adapter(failure == "all-incompatible" ? "incompatible" : "physical") },
                    PhysicalDevice(badDevice, 1) with
                    {
                        Enabled = failure != "disabled-device",
                        Adapter = Adapter(failure is "incompatible" or "all-incompatible" ? "incompatible" : "physical")
                    }
                ],
                VirtualDevices =
                [
                    VirtualDevice(virtualDevice, healthyDevice) with { Subscription = new PhysicalDeviceSubscription { Mode = PhysicalDeviceSubscriptionMode.All } }
                ],
                GuideSources = allExcluded ? [badDefinition] : [GuideSource(healthySource, healthyDevice), badDefinition]
            };
            var adapters = new StreamingAdapterRegistry([], [new FakeVirtualDriver()], [new FakeGuideDriver()]);
            var queries = new GuideQueryService(topology, store, adapters);
            var output = new GuideOutputDefinition { Id = GuideOutputId.New(), Name = "All sources", Adapter = Adapter("xmltv") };
            var orchestrator = new GuideOrchestrator(adapters, store, topology);

            // Act
            var union = await queries.BuildUnionSnapshotAsync(configuration, cancellationToken: TestContext.Current.CancellationToken);
            var published = await queries.BuildOutputSnapshotAsync(configuration, output, cancellationToken: TestContext.Current.CancellationToken);
            var statistics = await queries.GetStatisticsAsync(configuration, TestContext.Current.CancellationToken);
            await orchestrator.ApplyAsync(configuration, TestContext.Current.CancellationToken);
            var orchestrated = await orchestrator.BuildSnapshotAsync(virtualDevice, TestContext.Current.CancellationToken);
            var refresh = await orchestrator.RefreshAllAsync(TestContext.Current.CancellationToken);

            // Assert
            foreach (var snapshot in new[] { union, published, orchestrated })
            {
                Assert.True(snapshot.IsPartial);
                var diagnostic = Assert.Single(snapshot.SourceDiagnostics);
                Assert.Equal(badSource, diagnostic.SourceId);
                Assert.Equal("Degraded source", diagnostic.SourceName);
                Assert.Equal(code, diagnostic.Code);
                Assert.Contains("excluded", diagnostic.Message, StringComparison.Ordinal);
                Assert.DoesNotContain("private-provider-detail", diagnostic.Message, StringComparison.Ordinal);
                Assert.Equal(allExcluded ? [] : ["Program 2"], snapshot.Channels.SelectMany(channel => channel.Programs).Select(program => program.Title));
            }
            Assert.Equal(union.SourceDiagnostics, queries.GetSourceDiagnostics(configuration));
            Assert.Equal(union.SourceDiagnostics, statistics.SourceDiagnostics);
            Assert.True(statistics.IsPartial);
            Assert.Equal(allExcluded ? 0 : 1, statistics.ProgramCount);
            var excluded = Assert.Single(refresh, result => result.SourceId == badSource);
            Assert.False(excluded.Succeeded);
            Assert.Equal(code, Assert.Single(excluded.SourceDiagnostics).Code);
            Assert.Equal(2, (await store.ReadImportStatesAsync(TestContext.Current.CancellationToken)).Count);
            if (failure == "unknown-device")
            {
                Assert.Contains(StreamingConfigurationValidator.Validate(configuration), error => error.Contains("unknown physical device", StringComparison.Ordinal));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            root.Delete(recursive: true);
        }
    }

    /// <summary>Verifies a source not selected by an output cannot degrade that output.</summary>
    [Fact]
    public async Task BuildOutputSnapshotAsync_UnselectedUnavailableSource_DoesNotDegradeOutput()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-selected-guide-");
        try
        {
            var factory = new TestContextFactory(Path.Combine(root.FullName, "lineup.db"));
            var topology = new StreamingTopologyStore(factory);
            var store = new DeviceGuideStore(factory);
            var device = PhysicalDeviceId.New();
            var source = GuideSourceId.New();
            await topology.ReconcileAsync(device, [PhysicalChannel(device, "2")], TestContext.Current.CancellationToken);
            await ImportAsync(store, source, "2", TestContext.Current.CancellationToken);
            var configuration = new StreamingConfiguration
            {
                PhysicalDevices = [PhysicalDevice(device, 0)],
                VirtualDevices = [VirtualDevice(VirtualDeviceId.New(), device)],
                GuideSources = [GuideSource(source, device), GuideSource(GuideSourceId.New(), device) with { Adapter = Adapter("missing") }]
            };
            var output = new GuideOutputDefinition
            {
                Id = GuideOutputId.New(),
                Name = "Healthy selection",
                Adapter = Adapter("xmltv"),
                GuideSourceSelectionMode = GuideOutputSelectionMode.Selected,
                SelectedGuideSourceIds = [source]
            };
            var queries = new GuideQueryService(topology, store, new StreamingAdapterRegistry([], [new FakeVirtualDriver()], [new FakeGuideDriver()]));

            // Act
            var snapshot = await queries.BuildOutputSnapshotAsync(configuration, output, cancellationToken: TestContext.Current.CancellationToken);

            // Assert
            Assert.False(snapshot.IsPartial);
            Assert.Empty(snapshot.SourceDiagnostics);
            Assert.Equal("Program 2", Assert.Single(Assert.Single(snapshot.Channels).Programs).Title);
            Assert.Single(queries.GetSourceDiagnostics(configuration));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            root.Delete(recursive: true);
        }
    }

    /// <summary>Verifies scope-provider defects are not converted into expected subscription diagnostics.</summary>
    [Theory]
    [InlineData("invalid-operation")]
    [InlineData("io")]
    [InlineData("cancellation")]
    public void GetSourceDiagnostics_UnexpectedProviderFailure_Propagates(string failure)
    {
        // Arrange
        Exception exception = failure switch
        {
            "io" => new IOException("Private provider detail"),
            "cancellation" => new OperationCanceledException(),
            _ => new InvalidOperationException("Private provider detail")
        };
        var factory = new ThrowingContextFactory(new IOException("Storage must not be read"));
        var device = PhysicalDeviceId.New();
        var queries = new GuideQueryService(
            new StreamingTopologyStore(factory), new DeviceGuideStore(factory),
            new StreamingAdapterRegistry([], [], [new FakeGuideDriver { ScopeException = exception }]));
        var configuration = new StreamingConfiguration { PhysicalDevices = [PhysicalDevice(device, 0)], GuideSources = [GuideSource(GuideSourceId.New(), device)] };

        // Act
        var actual = Record.Exception(() => queries.GetSourceDiagnostics(configuration));

        // Assert
        Assert.Same(exception, actual);
    }

    /// <summary>Verifies eligibility diagnostics do not require access to the persisted guide database.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GetSourceDiagnostics_HealthyOrUnavailableSource_DoesNotReadStorage(bool unavailable)
    {
        // Arrange
        var factory = new ThrowingContextFactory(new IOException("Storage must not be read"));
        var device = PhysicalDeviceId.New();
        var queries = new GuideQueryService(
            new StreamingTopologyStore(factory), new DeviceGuideStore(factory), new StreamingAdapterRegistry([], [], [new FakeGuideDriver()]));
        var configuration = new StreamingConfiguration
        {
            PhysicalDevices = [PhysicalDevice(device, 0)],
            GuideSources = [GuideSource(GuideSourceId.New(), device) with { Adapter = Adapter(unavailable ? "missing" : "guide") }]
        };

        // Act
        var diagnostics = queries.GetSourceDiagnostics(configuration);

        // Assert
        Assert.Equal(unavailable ? 1 : 0, diagnostics.Count);
    }

    /// <summary>Verifies storage failure and caller cancellation remain failures rather than partial guides.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuildUnionSnapshotAsync_StorageOrCancellationFailure_Propagates(bool cancelled)
    {
        // Arrange
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        if (cancelled)
        {
            cancellation.Cancel();
        }
        Exception exception = cancelled ? new OperationCanceledException(cancellation.Token) : new IOException("Storage unavailable");
        var factory = new ThrowingContextFactory(exception);
        var queries = new GuideQueryService(new StreamingTopologyStore(factory), new DeviceGuideStore(factory), new StreamingAdapterRegistry([], [], []));

        // Act
        var actual = await Record.ExceptionAsync(() => queries.BuildUnionSnapshotAsync(new StreamingConfiguration(), cancellationToken: cancellation.Token));

        // Assert
        Assert.NotNull(actual);
        if (cancelled)
        {
            Assert.IsAssignableFrom<OperationCanceledException>(actual);
        }
        else
        {
            Assert.Same(exception, actual);
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

    private static async Task ImportAsync(DeviceGuideStore store, GuideSourceId sourceId, string number, CancellationToken cancellationToken)
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
        /// <summary>Gets the kind.</summary>
        public string Kind => "virtual";

        /// <summary>Gets the display name.</summary>
        public string DisplayName => "Virtual";

        /// <summary>Gets the supports user creation.</summary>
        public bool SupportsUserCreation => true;

        /// <summary>Creates default configuration.</summary>
        /// <param name="deviceId">Identity of the target device.</param>
        /// <returns>The operation's result.</returns>
        public AdapterConfiguration CreateDefaultConfiguration(VirtualDeviceId deviceId) => Adapter(Kind);

        /// <summary>Validates the fake virtual driver.</summary>
        /// <param name="configuration">Configuration to apply.</param>
        /// <returns>The operation's result.</returns>
        public IReadOnlyList<string> Validate(AdapterConfiguration configuration) => [];
    }

    private sealed class FakeGuideDriver : IGuideSourceDriver
    {
        /// <summary>Gets or sets the scope exception.</summary>
        public Exception? ScopeException { get; init; }

        /// <summary>Gets the kind.</summary>
        public string Kind => "guide";

        /// <summary>Gets the display name.</summary>
        public string DisplayName => "Guide";

        /// <summary>Gets the supports user creation.</summary>
        public bool SupportsUserCreation => true;

        /// <summary>Creates default configuration.</summary>
        /// <param name="sourceId">The source ID value.</param>
        /// <returns>The operation's result.</returns>
        public AdapterConfiguration CreateDefaultConfiguration(GuideSourceId sourceId) => Adapter(Kind);

        /// <summary>Determines support for physical adapter.</summary>
        /// <param name="physicalAdapterKind">The physical adapter kind value.</param>
        /// <returns>The operation's result.</returns>
        public bool SupportsPhysicalAdapter(string physicalAdapterKind) => ScopeException is not null ? throw ScopeException : physicalAdapterKind == "physical";

        /// <summary>Validates the fake guide driver.</summary>
        /// <param name="configuration">Configuration to apply.</param>
        /// <returns>The operation's result.</returns>
        public IReadOnlyList<string> Validate(AdapterConfiguration configuration) => [];

        /// <summary>Creates the fake guide driver asynchronously.</summary>
        /// <param name="sourceId">The source ID value.</param>
        /// <param name="configuration">Configuration to apply.</param>
        /// <param name="physicalDeviceIds">The physical device IDs value.</param>
        /// <param name="cancellationToken">Token used to cancel the operation.</param>
        /// <returns>The operation's result.</returns>
        public ValueTask<IGuideSource> CreateAsync(GuideSourceId sourceId, AdapterConfiguration configuration, IReadOnlyList<PhysicalDeviceId> physicalDeviceIds, CancellationToken cancellationToken = default) =>
            throw new IOException("Projection tests use the last committed import.");
    }

    private sealed class TestContextFactory(string path) : IDbContextFactory<EpgDbContext>
    {
        private readonly DbContextOptions<EpgDbContext> _options = new DbContextOptionsBuilder<EpgDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;

        /// <summary>Creates db context.</summary>
        /// <returns>The operation's result.</returns>
        public EpgDbContext CreateDbContext() => new(_options);
    }

    private sealed class ThrowingContextFactory(Exception exception) : IDbContextFactory<EpgDbContext>
    {
        /// <summary>Creates db context.</summary>
        /// <returns>The operation's result.</returns>
        public EpgDbContext CreateDbContext() => throw exception;
    }
}
