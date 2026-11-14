using System.Text.Json;
using Lineup.Core.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Lineup.Core.Tests.Guides;

/// <summary>
/// Verifies host-neutral guide source orchestration and virtual projection.
/// </summary>
public sealed class GuideOrchestratorTests
{
    /// <summary>Verifies explicit source selection and the network-concurrency bound without dropping queued refreshes.</summary>
    [Fact]
    public async Task RefreshSourcesAsync_SelectedSources_BoundsConcurrencyAndCompletesEverySelectedSource()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-guide-concurrency-");
        try
        {
            var factory = new TestContextFactory(Path.Combine(root.FullName, "guides.db"));
            var device = PhysicalDeviceId.New();
            var ids = Enumerable.Range(0, 7).Select(_ => GuideSourceId.New()).ToArray();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstWave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var observed = new System.Collections.Concurrent.ConcurrentQueue<int>();
            var active = 0;
            var driver = new FakeGuideDriver(ids.ToDictionary(id => id, id => (Func<Task<GuideImport>>)(async () =>
            {
                var count = Interlocked.Increment(ref active);
                observed.Enqueue(count);
                if (count == 4)
                {
                    firstWave.TrySetResult();
                }
                await release.Task;
                Interlocked.Decrement(ref active);
                return Import(id, "7", "Fetched");
            })));
            var orchestrator = new GuideOrchestrator(new StreamingAdapterRegistry([], [], [driver]), new DeviceGuideStore(factory), new StreamingTopologyStore(factory));
            var configuration = Configuration([PhysicalDefinition(device, 0)], ids.Select(id => GuideDefinition(id, device)).ToArray(), []);
            var selected = ids.Take(6).ToHashSet();

            // Act
            var refresh = orchestrator.RefreshSourcesAsync(configuration, selected, TestContext.Current.CancellationToken);
            try
            {
                await firstWave.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                Assert.Equal(4, driver.RefreshCount);
            }
            finally
            {
                release.TrySetResult();
                await refresh;
            }
            var results = await refresh;

            // Assert
            Assert.Equal(6, driver.RefreshCount);
            Assert.Equal(4, observed.Max());
            Assert.Equal(selected, results.Select(result => result.SourceId).ToHashSet());
            Assert.All(results, result => Assert.True(result.Succeeded));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            root.Delete(recursive: true);
        }
    }

    /// <summary>Verifies eligible committed guide data survives source activation failures and is replaced after recovery.</summary>
    [Theory]
    [InlineData("io")]
    [InlineData("http")]
    [InlineData("unauthorized")]
    [InlineData("argument")]
    [InlineData("invalid-operation")]
    [InlineData("wrong-identity")]
    [InlineData("validation")]
    public async Task BuildSnapshotAsync_ActivationFailure_PreservesEligibleCommittedGuide(string failure)
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-guide-activation-cache-");
        try
        {
            var factory = new TestContextFactory(Path.Combine(root.FullName, "guides.db"));
            var store = new DeviceGuideStore(factory);
            var topology = new StreamingTopologyStore(factory);
            var device = PhysicalDeviceId.New();
            var source = GuideSourceId.New();
            var output = VirtualDeviceId.New();
            await topology.ReconcileAsync(device, [PhysicalChannel(device, "7")], TestContext.Current.CancellationToken);
            await store.ImportAsync(Import(source, "7", "Committed"), TestContext.Current.CancellationToken);
            var driver = new FakeGuideDriver(new Dictionary<GuideSourceId, Func<Task<GuideImport>>>
            {
                [source] = () => Task.FromResult(Import(source, "7", "Recovered"))
            })
            {
                ActivationException = failure switch
                {
                    "io" => new IOException("Private activation detail"),
                    "http" => new HttpRequestException("Private activation detail"),
                    "unauthorized" => new UnauthorizedAccessException("Private activation detail"),
                    "argument" => new ArgumentException("Private activation detail"),
                    "invalid-operation" => new InvalidOperationException("Private activation detail"),
                    _ => null
                },
                ReturnWrongIdentity = failure == "wrong-identity",
                RejectConfiguration = failure == "validation"
            };
            var adapters = new StreamingAdapterRegistry([], [new FakeVirtualDriver()], [driver]);
            var orchestrator = new GuideOrchestrator(adapters, store, topology);
            var configuration = Configuration([PhysicalDefinition(device, 0)], [GuideDefinition(source, device)], [VirtualDefinition(output, device)]);

            // Act
            await orchestrator.ApplyAsync(configuration, TestContext.Current.CancellationToken);
            var failed = Assert.Single(await orchestrator.RefreshAllAsync(TestContext.Current.CancellationToken));
            var cached = await orchestrator.BuildSnapshotAsync(output, TestContext.Current.CancellationToken);
            var union = await new GuideQueryService(topology, store, adapters).BuildUnionSnapshotAsync(configuration, cancellationToken: TestContext.Current.CancellationToken);
            driver.ActivationException = null;
            driver.RejectConfiguration = false;
            driver.ReturnWrongIdentity = false;
            await orchestrator.ApplyAsync(configuration, TestContext.Current.CancellationToken);
            var recovered = Assert.Single(await orchestrator.RefreshAllAsync(TestContext.Current.CancellationToken));
            var refreshed = await orchestrator.BuildSnapshotAsync(output, TestContext.Current.CancellationToken);

            // Assert
            Assert.False(failed.Succeeded);
            Assert.Contains("preserved", failed.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("Private activation detail", failed.Error!, StringComparison.Ordinal);
            Assert.Equal("Committed", Assert.Single(Assert.Single(cached.Channels).Programs).Title);
            Assert.Equal("Committed", Assert.Single(Assert.Single(union.Channels).Programs).Title);
            Assert.False(cached.IsPartial);
            Assert.Empty(failed.SourceDiagnostics);
            Assert.True(recovered.Succeeded);
            Assert.Equal("Recovered", Assert.Single(Assert.Single(refreshed.Channels).Programs).Title);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            root.Delete(recursive: true);
        }
    }

    /// <summary>Verifies retained imports never bypass disabled, removed, incompatible, or changed source subscriptions.</summary>
    [Theory]
    [InlineData("disabled")]
    [InlineData("removed")]
    [InlineData("unsubscribed")]
    [InlineData("incompatible")]
    [InlineData("unknown-driver")]
    public async Task BuildSnapshotAsync_IneligibleCachedSource_DoesNotPublishPrograms(string restriction)
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-guide-scope-cache-");
        try
        {
            var factory = new TestContextFactory(Path.Combine(root.FullName, "guides.db"));
            var store = new DeviceGuideStore(factory);
            var topology = new StreamingTopologyStore(factory);
            var first = PhysicalDeviceId.New();
            var second = PhysicalDeviceId.New();
            var source = GuideSourceId.New();
            var output = VirtualDeviceId.New();
            await topology.ReconcileAsync(first, [PhysicalChannel(first, "7")], TestContext.Current.CancellationToken);
            await store.ImportAsync(Import(source, "7", "Committed"), TestContext.Current.CancellationToken);
            var driver = new FakeGuideDriver(new Dictionary<GuideSourceId, Func<Task<GuideImport>>> { [source] = () => Task.FromResult(Import(source, "7", "Unused")) });
            var orchestrator = new GuideOrchestrator(new StreamingAdapterRegistry([], [new FakeVirtualDriver()], [driver]), store, topology);
            var definition = GuideDefinition(source, first);
            var configuration = Configuration(
                [PhysicalDefinition(first, 0), PhysicalDefinition(second, 1)], [definition], [VirtualDefinition(output, first)]);
            await orchestrator.ApplyAsync(configuration, TestContext.Current.CancellationToken);
            Assert.Single(Assert.Single((await orchestrator.BuildSnapshotAsync(output, TestContext.Current.CancellationToken)).Channels).Programs);
            driver.ActivationException = new IOException("Offline");
            configuration = restriction switch
            {
                "disabled" => configuration with { GuideSources = [definition with { Enabled = false }] },
                "removed" => configuration with { GuideSources = [] },
                "unsubscribed" => configuration with { GuideSources = [GuideDefinition(source, second)] },
                "incompatible" => configuration with { PhysicalDevices = [PhysicalDefinition(first, 0) with { Adapter = Adapter("incompatible") }] },
                _ => configuration with { GuideSources = [definition with { Adapter = Adapter("unknown") }] }
            };

            // Act
            await orchestrator.ApplyAsync(configuration, TestContext.Current.CancellationToken);
            var snapshot = await orchestrator.BuildSnapshotAsync(output, TestContext.Current.CancellationToken);

            // Assert
            Assert.Empty(snapshot.Channels.SelectMany(channel => channel.Programs));
            Assert.Equal(restriction is "incompatible" or "unknown-driver", snapshot.IsPartial);
            Assert.Equal(1, Assert.Single(await store.ReadImportStatesAsync(TestContext.Current.CancellationToken)).ProgramCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            root.Delete(recursive: true);
        }
    }

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
            var failure = new IOException("Second source unavailable.");
            var driver = new FakeGuideDriver(new Dictionary<GuideSourceId, Func<Task<GuideImport>>>
            {
                [firstSource] = () => Task.FromResult(Import(firstSource, "7", "Fresh")),
                [secondSource] = () => Task.FromException<GuideImport>(failure)
            });
            var logger = Substitute.For<ILogger<GuideOrchestrator>>();
            var orchestrator = new GuideOrchestrator(new StreamingAdapterRegistry([], [], [driver]), guideStore, topologyStore, logger);
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
            var warning = Assert.Single(logger.ReceivedCalls(), call => call.GetMethodInfo().Name == nameof(ILogger.Log));
            Assert.Equal(LogLevel.Warning, warning.GetArguments()[0]);
            Assert.Same(failure, warning.GetArguments()[3]);
            var values = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(warning.GetArguments()[2]).ToDictionary();
            Assert.Equal(secondSource, values["GuideSourceId"]);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            root.Delete(recursive: true);
        }
    }

    /// <summary>Verifies an invalid refresh import preserves committed data while retaining a source-correlated diagnostic.</summary>
    [Fact]
    public async Task RefreshAllAsync_WrongImportIdentity_LogsFailureAndPreservesCommittedImport()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-guide-refresh-identity-");
        try
        {
            var factory = new TestContextFactory(Path.Combine(root.FullName, "guides.db"));
            var store = new DeviceGuideStore(factory);
            var topology = new StreamingTopologyStore(factory);
            var device = PhysicalDeviceId.New();
            var source = GuideSourceId.New();
            var output = VirtualDeviceId.New();
            await topology.ReconcileAsync(device, [PhysicalChannel(device, "7")], TestContext.Current.CancellationToken);
            await store.ImportAsync(Import(source, "7", "Committed"), TestContext.Current.CancellationToken);
            var driver = new FakeGuideDriver(new Dictionary<GuideSourceId, Func<Task<GuideImport>>>
            {
                [source] = () => Task.FromResult(Import(GuideSourceId.New(), "7", "Invalid"))
            });
            var logger = Substitute.For<ILogger<GuideOrchestrator>>();
            var orchestrator = new GuideOrchestrator(new StreamingAdapterRegistry([], [new FakeVirtualDriver()], [driver]), store, topology, logger);
            await orchestrator.ApplyAsync(Configuration([PhysicalDefinition(device, 0)], [GuideDefinition(source, device)], [VirtualDefinition(output, device)]), TestContext.Current.CancellationToken);

            // Act
            var result = Assert.Single(await orchestrator.RefreshAllAsync(TestContext.Current.CancellationToken));
            var cached = await orchestrator.BuildSnapshotAsync(output, TestContext.Current.CancellationToken);

            // Assert
            Assert.False(result.Succeeded);
            Assert.Contains(nameof(InvalidDataException), result.Error, StringComparison.Ordinal);
            Assert.Equal("Committed", Assert.Single(Assert.Single(cached.Channels).Programs).Title);
            var warning = Assert.Single(logger.ReceivedCalls(), call => call.GetMethodInfo().Name == nameof(ILogger.Log));
            Assert.Equal(LogLevel.Warning, warning.GetArguments()[0]);
            Assert.IsType<InvalidDataException>(warning.GetArguments()[3]);
            var values = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(warning.GetArguments()[2]).ToDictionary();
            Assert.Equal(source, values["GuideSourceId"]);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            root.Delete(recursive: true);
        }
    }

    /// <summary>Verifies caller cancellation remains cancellation rather than a logged refresh failure.</summary>
    [Fact]
    public async Task RefreshAllAsync_CallerCancels_PropagatesWithoutFailureWarning()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-guide-refresh-cancel-");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        try
        {
            var factory = new TestContextFactory(Path.Combine(root.FullName, "guides.db"));
            var device = PhysicalDeviceId.New();
            var source = GuideSourceId.New();
            var driver = new FakeGuideDriver(new Dictionary<GuideSourceId, Func<Task<GuideImport>>>
            {
                [source] = () =>
                {
                    cancellation.Cancel();
                    return Task.FromCanceled<GuideImport>(cancellation.Token);
                }
            });
            var logger = Substitute.For<ILogger<GuideOrchestrator>>();
            var orchestrator = new GuideOrchestrator(new StreamingAdapterRegistry([], [], [driver]), new DeviceGuideStore(factory), new StreamingTopologyStore(factory), logger);
            await orchestrator.ApplyAsync(Configuration([PhysicalDefinition(device, 0)], [GuideDefinition(source, device)], []), cancellation.Token);

            // Act
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => orchestrator.RefreshAllAsync(cancellation.Token));

            // Assert
            Assert.Equal(cancellation.Token, exception.CancellationToken);
            Assert.DoesNotContain(logger.ReceivedCalls(), call => call.GetMethodInfo().Name == nameof(ILogger.Log));
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
            Assert.Equal(GuideSourceDiagnosticCode.NoCompatibleDevices, Assert.Single(result.SourceDiagnostics).Code);
            Assert.Contains("no compatible enabled physical devices", result.Error, StringComparison.Ordinal);
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

    private static StreamingConfiguration Configuration(IReadOnlyList<PhysicalDeviceDefinition> physicalDevices, IReadOnlyList<GuideSourceDefinition> guideSources, IReadOnlyList<VirtualDeviceDefinition> virtualDevices) =>
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
        var start = DateTimeOffset.UtcNow;
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
        /// <summary>Stores the driver kind.</summary>
        public const string DriverKind = "fake-guide";
        private int _refreshCount;

        /// <summary>Gets the kind.</summary>
        public string Kind => DriverKind;

        /// <summary>Gets the display name.</summary>
        public string DisplayName => "Fake Guide";

        /// <summary>Gets the supports user creation.</summary>
        public bool SupportsUserCreation => true;

        /// <summary>Gets the refresh count.</summary>
        public int RefreshCount => Volatile.Read(ref _refreshCount);

        /// <summary>Gets or sets the activation exception.</summary>
        internal Exception? ActivationException { get; set; }

        /// <summary>Gets or sets the return wrong identity.</summary>
        internal bool ReturnWrongIdentity { get; set; }

        /// <summary>Gets or sets the reject configuration.</summary>
        internal bool RejectConfiguration { get; set; }

        /// <summary>Creates default configuration.</summary>
        /// <param name="sourceId">The source ID value.</param>
        /// <returns>The operation's result.</returns>
        public AdapterConfiguration CreateDefaultConfiguration(GuideSourceId sourceId) => Adapter(Kind);

        /// <summary>Determines support for physical adapter.</summary>
        /// <param name="physicalAdapterKind">The physical adapter kind value.</param>
        /// <returns>The operation's result.</returns>
        public bool SupportsPhysicalAdapter(string physicalAdapterKind) =>
            string.Equals(physicalAdapterKind, "unused-physical", StringComparison.Ordinal);

        /// <summary>Validates the fake guide driver.</summary>
        /// <param name="configuration">Configuration to apply.</param>
        /// <returns>The operation's result.</returns>
        public IReadOnlyList<string> Validate(AdapterConfiguration configuration) => RejectConfiguration ? ["Private activation detail"] : [];

        /// <summary>Creates the fake guide driver asynchronously.</summary>
        /// <param name="sourceId">The source ID value.</param>
        /// <param name="configuration">Configuration to apply.</param>
        /// <param name="physicalDeviceIds">The physical device IDs value.</param>
        /// <param name="cancellationToken">Token used to cancel the operation.</param>
        /// <returns>The operation's result.</returns>
        public ValueTask<IGuideSource> CreateAsync(GuideSourceId sourceId, AdapterConfiguration configuration, IReadOnlyList<PhysicalDeviceId> physicalDeviceIds, CancellationToken cancellationToken = default) =>
            ActivationException is not null
                ? ValueTask.FromException<IGuideSource>(ActivationException)
                : ValueTask.FromResult<IGuideSource>(new FakeGuideSource(ReturnWrongIdentity ? GuideSourceId.New() : sourceId, refreshes[sourceId], () => Interlocked.Increment(ref _refreshCount)));
    }

    private sealed class FakeGuideSource(GuideSourceId id, Func<Task<GuideImport>> refresh, Action onRefresh) : IGuideSource
    {
        /// <summary>Gets the ID.</summary>
        public GuideSourceId Id { get; } = id;

        /// <summary>Refreshes the fake guide source asynchronously.</summary>
        /// <param name="cancellationToken">Token used to cancel the operation.</param>
        /// <returns>The operation's result.</returns>
        public Task<GuideImport> RefreshAsync(CancellationToken cancellationToken = default)
        {
            onRefresh();
            return refresh();
        }
    }

    private sealed class FakeVirtualDriver : IVirtualTunerDriver
    {
        /// <summary>Gets the kind.</summary>
        public string Kind => "unused-virtual";

        /// <summary>Gets the display name.</summary>
        public string DisplayName => "Unused";

        /// <summary>Gets the supports user creation.</summary>
        public bool SupportsUserCreation => false;

        /// <summary>Creates default configuration.</summary>
        /// <param name="deviceId">Identity of the target device.</param>
        /// <returns>The operation's result.</returns>
        public AdapterConfiguration CreateDefaultConfiguration(VirtualDeviceId deviceId) => Adapter(Kind);

        /// <summary>Validates the fake virtual driver.</summary>
        /// <param name="configuration">Configuration to apply.</param>
        /// <returns>The operation's result.</returns>
        public IReadOnlyList<string> Validate(AdapterConfiguration configuration) => [];
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
}
