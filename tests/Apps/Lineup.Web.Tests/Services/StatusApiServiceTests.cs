using System.Text.Json;
using Lineup.Common.Configuration;
using Lineup.Extensibility.Configuration;
using Lineup.VirtualOutputs.Protocol;
using Lineup.VirtualOutputs.Runtime;
using Lineup.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>
/// Verifies stable status API aggregation and privacy behavior.
/// </summary>
public class StatusApiServiceTests
{
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Verifies client privacy covers the complete real flow projection independently of device and URL privacy.</summary>
    [Theory]
    [InlineData("192.0.2.20:54321", false, false, false)]
    [InlineData("192.0.2.20:54321", false, false, true)]
    [InlineData("192.0.2.20:54321", false, true, false)]
    [InlineData("192.0.2.20:54321", false, true, true)]
    [InlineData("192.0.2.20:54321", true, false, false)]
    [InlineData("192.0.2.20:54321", true, false, true)]
    [InlineData("192.0.2.20:54321", true, true, false)]
    [InlineData("192.0.2.20:54321", true, true, true)]
    [InlineData("[2001:db8::20]:54321", false, false, false)]
    [InlineData("[2001:db8::20]:54321", false, false, true)]
    [InlineData("[2001:db8::20]:54321", false, true, false)]
    [InlineData("[2001:db8::20]:54321", false, true, true)]
    [InlineData("[2001:db8::20]:54321", true, false, false)]
    [InlineData("[2001:db8::20]:54321", true, false, true)]
    [InlineData("[2001:db8::20]:54321", true, true, false)]
    [InlineData("[2001:db8::20]:54321", true, true, true)]
    public async Task GetStatusAsync_FlowClientPrivacy_RedactsEveryProjection(string clientAddress, bool redactClients, bool redactDevices, bool redactUrls)
    {
        // Arrange
        using var fixture = CreateFixture(useRealStreamingFlows: true, clientAddress: clientAddress);
        fixture.Settings.RedactApiClientAddresses = redactClients;
        fixture.Settings.RedactApiDeviceAddresses = redactDevices;
        fixture.Settings.RedactApiUrls = redactUrls;
        var snapshot = fixture.StreamingFlows.GetSnapshot();
        var originalConsumer = Assert.Single(snapshot.GetLane(StreamingFlowLane.Consumer));
        var originalPath = Assert.Single(snapshot.ActivePaths);

        // Act
        var result = await fixture.Service.GetStatusAsync(TestContext.Current.CancellationToken);
        var json = JsonSerializer.Serialize(result, WebJsonOptions);

        // Assert
        var consumer = Assert.Single(result.StreamingFlows.Nodes, node => node.Lane == "consumer");
        var path = Assert.Single(result.StreamingFlows.ActivePaths);
        var consumerStage = Assert.Single(path.Stages, stage => stage.Kind == "consumer");
        Assert.Equal(redactClients ? "Consumer" : clientAddress, consumer.Label);
        Assert.Equal(redactClients ? "Consumer" : clientAddress, consumerStage.Label);
        Assert.Equal(redactClients ? null : clientAddress, path.Endpoint);
        Assert.Equal(redactClients ? null : clientAddress, Assert.Single(result.Streams.Items).ClientAddress);
        Assert.Equal(!redactClients, consumer.Details.ContainsKey("Endpoint"));
        Assert.Equal(!redactClients, consumerStage.Details.ContainsKey("Client address"));
        if (redactClients)
        {
            Assert.DoesNotContain(clientAddress, json, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(clientAddress, consumer.Details["Endpoint"]);
            Assert.Equal(clientAddress, consumerStage.Details["Client address"]);
            Assert.Contains(clientAddress, json, StringComparison.Ordinal);
        }
        Assert.Equal(originalConsumer.Id, consumer.Id);
        Assert.Equal(originalConsumer.Subtitle, consumer.Subtitle);
        Assert.Equal(originalConsumer.ActiveChannels, consumer.ActiveChannels);
        Assert.Equal(originalConsumer.ActiveSessionCount, consumer.ActiveSessionCount);
        Assert.Equal("stream-1", consumer.Details["Session"]);
        Assert.Equal("watch-1", consumer.Details["Client ID"]);
        Assert.Equal("watch-1", consumerStage.Details["Client ID"]);
        Assert.Equal("5.1", path.Channel);
        Assert.Equal(originalPath.SessionId, path.SessionId);
        Assert.Equal(originalPath.Stages.Count, path.Stages.Count);
        Assert.Equal(snapshot.ActiveConsumerCount, result.StreamingFlows.ActiveConsumerCount);
        Assert.Equal(snapshot.ActiveProducerCount, result.StreamingFlows.ActiveProducerCount);
        Assert.Equal(snapshot.WarmProducerCount, result.StreamingFlows.WarmProducerCount);
        foreach (var node in snapshot.Nodes.Where(node => node.Lane != StreamingFlowLane.Consumer))
        {
            var projected = Assert.Single(result.StreamingFlows.Nodes, candidate => candidate.Id == node.Id);
            Assert.Equal(node.Label, projected.Label);
            Assert.Equal(node.Subtitle, projected.Subtitle);
        }
        foreach (var stage in originalPath.Stages.Where(stage => stage.Kind != StreamingFlowStageKind.Consumer))
        {
            var projected = Assert.Single(path.Stages, candidate => candidate.Kind == char.ToLowerInvariant(stage.Kind.ToString()[0]) + stage.Kind.ToString()[1..]);
            Assert.Equal(stage.Label, projected.Label);
            Assert.Equal(stage.Subtitle, projected.Subtitle);
        }
        var dashboardConsumer = Assert.Single(fixture.StreamingFlows.GetSnapshot().GetLane(StreamingFlowLane.Consumer));
        Assert.Equal(clientAddress, dashboardConsumer.Label);
        Assert.Equal(clientAddress, dashboardConsumer.Details.Single(detail => detail.Label == "Endpoint").Value);
        Assert.Equal(redactUrls ? null : "http://client.local:5000", result.Receivers[0].TargetUrl);
    }

    /// <summary>Verifies missing addresses preserve client identifiers without leaking the Dashboard fallback label.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetStatusAsync_FlowClientPrivacyWithoutAddress_PreservesIdentity(bool redactClients)
    {
        // Arrange
        using var fixture = CreateFixture(useRealStreamingFlows: true, clientAddress: null);
        fixture.Settings.RedactApiClientAddresses = redactClients;

        // Act
        var result = await fixture.Service.GetStatusAsync(TestContext.Current.CancellationToken);

        // Assert
        var consumer = Assert.Single(result.StreamingFlows.Nodes, node => node.Lane == "consumer");
        var path = Assert.Single(result.StreamingFlows.ActivePaths);
        var stage = Assert.Single(path.Stages, item => item.Kind == "consumer");
        Assert.Equal(redactClients ? "Consumer" : "watch-1", consumer.Label);
        Assert.Equal(redactClients ? "Consumer" : "watch-1", stage.Label);
        Assert.Equal(redactClients ? null : "watch-1", path.Endpoint);
        Assert.Null(Assert.Single(result.Streams.Items).ClientAddress);
        Assert.Equal("watch-1", consumer.Details["Client ID"]);
        Assert.Equal("watch-1", stage.Details["Client ID"]);
        Assert.Equal(!redactClients, consumer.Details.ContainsKey("Endpoint"));
        Assert.Equal(!redactClients, stage.Details.ContainsKey("Client address"));
    }

    /// <summary>
    /// Verifies the default privacy policy and representative state mapping.
    /// </summary>
    [Fact]
    public async Task GetStatusAsync_DefaultPrivacy_RedactsClientsAndNeverExposesSecrets()
    {
        // Arrange
        using var fixture = CreateFixture();
        fixture.Settings.RedactApiClientAddresses = true;
        fixture.Settings.RedactApiDeviceAddresses = false;
        fixture.Settings.RedactApiUrls = false;

        // Act
        var result = await fixture.Service.GetStatusAsync(TestContext.Current.CancellationToken);
        var json = JsonSerializer.Serialize(result, WebJsonOptions);

        // Assert
        Assert.Equal("3.0", result.SchemaVersion);
        Assert.Equal("available", result.Guide.Status);
        Assert.Equal(7, result.Guide.ChannelCount);
        Assert.Equal(2, result.Configuration.Core.TargetDays);
        Assert.Equal("en", result.Configuration.Web.UiCulture);
        Assert.Equal("hdhomerun", Assert.Single(result.Configuration.Streaming.PhysicalDevices).Adapter.Kind);
        Assert.Equal("http://tuner.local", result.Configuration.Streaming.PhysicalDevices[0].Adapter.Settings.Single(setting => setting.Key == "baseUrl").Value?.GetString());
        Assert.Null(result.Configuration.Streaming.PhysicalDevices[0].Adapter.Settings.Single(setting => setting.Key == "deviceAuth").Value);
        Assert.True(result.Configuration.Streaming.PhysicalDevices[0].Adapter.Settings.Single(setting => setting.Key == "deviceAuth").HasValue);
        Assert.Equal("http://tuner.local", result.PhysicalDevices[0].Details["baseUrl"]);
        Assert.Equal("http://client.local:5000", result.Receivers[0].TargetUrl);
        Assert.Null(result.Streams.Items[0].ClientAddress);
        Assert.Equal("grant-1", result.Streams.Items[0].AuthenticatedClientId);
        Assert.Equal("Remote household", result.Streams.Items[0].AuthenticatedClientName);
        Assert.Equal("broker-request-1", result.Streams.Items[0].BrokerRequestId);
        Assert.Equal("worker-media-1", result.Streams.Items[0].WorkerMediaSessionId);
        Assert.Equal("ready", result.Streams.Items[0].WorkerStartupStage);
        Assert.Equal("transform", Assert.Single(result.StreamingGraph.Nodes).Kind);
        Assert.Equal(2, result.StreamingGraph.Nodes[0].ReceiverCount);
        Assert.Equal(3, result.StreamingGraph.Nodes[0].TotalSubscriptions);
        Assert.Equal("virtual-node", result.StreamingGraph.Nodes[0].NodeId);
        Assert.Equal("source-node", result.StreamingGraph.Nodes[0].ParentNodeId);
        Assert.Equal("virtual-mpegts", result.StreamingGraph.Nodes[0].Stage);
        Assert.Equal(1, Assert.Single(result.StreamingGraph.Capacity).ActiveAcquisitions);
        Assert.Equal(1, result.StreamingFlows.ActiveConsumerCount);
        Assert.Equal("multiplexer", Assert.Single(result.StreamingFlows.Nodes).Id);
        Assert.Null(Assert.Single(result.StreamingFlows.ActivePaths).Endpoint);
        Assert.Equal("multiplexer", Assert.Single(result.StreamingFlows.ActivePaths[0].Stages).Kind);
        Assert.Equal("http://lineup.local:8080/", Assert.Single(result.VirtualOutputWorkers).Bindings[0]);
        Assert.Contains("\"clientAddress\":null", json, StringComparison.Ordinal);
        Assert.DoesNotContain("physical-secret", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies each privacy category independently nulls stable response properties.
    /// </summary>
    [Fact]
    public async Task GetStatusAsync_AllPrivacyEnabled_ReturnsNullNetworkProperties()
    {
        // Arrange
        using var fixture = CreateFixture();
        fixture.Settings.RedactApiClientAddresses = true;
        fixture.Settings.RedactApiDeviceAddresses = true;
        fixture.Settings.RedactApiUrls = true;

        // Act
        var result = await fixture.Service.GetStatusAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.DoesNotContain("baseUrl", result.PhysicalDevices[0].Details.Keys);
        Assert.DoesNotContain("lineupUrl", result.PhysicalDevices[0].Details.Keys);
        Assert.Null(result.VirtualDevices[0].PhysicalAddress);
        Assert.Null(result.VirtualDevices[0].AdvertisedUrl);
        Assert.Null(result.Receivers[0].TargetUrl);
        Assert.Null(result.Streams.Items[0].ClientAddress);
        Assert.Null(result.Guide.Xmltv.DownloadUrl);
        Assert.Null(result.Configuration.Streaming.PhysicalDevices[0].Adapter.Settings.Single(setting => setting.Key == "baseUrl").Value);
        Assert.Empty(Assert.Single(result.VirtualOutputWorkers).Bindings);
    }

    /// <summary>
    /// Verifies guide repository failures degrade only the guide section.
    /// </summary>
    [Fact]
    public async Task GetStatusAsync_GuideFailure_ReturnsUsableDegradedResponse()
    {
        // Arrange
        using var fixture = CreateFixture();
        fixture.GuideReader.GetStatisticsAsync(Arg.Any<CancellationToken>()).Returns<Task<GuideStatistics>>(_ => throw new IOException("database unavailable"));

        // Act
        var result = await fixture.Service.GetStatusAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("unavailable", result.Guide.Status);
        Assert.Equal("Guide state is unavailable.", result.Guide.Error);
        Assert.Null(result.Guide.ChannelCount);
        Assert.Equal("available", result.PhysicalDevices[0].Status);
        Assert.Single(result.Receivers);
        Assert.Equal(1, result.Streams.Count);
    }

    /// <summary>
    /// Verifies recovery status is exposed without opening the incompatible guide database.
    /// </summary>
    [Fact]
    public async Task GetStatusAsync_FactoryResetRequired_SkipsGuideDatabase()
    {
        // Arrange
        var startupState = new LineupStartupState(LineupStartupStatus.FactoryResetRequired, "The persisted settings use a legacy schema.");
        using var fixture = CreateFixture(startupState);

        // Act
        var result = await fixture.Service.GetStatusAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("factoryResetRequired", result.Application.StartupStatus);
        Assert.Contains("legacy", result.Application.StartupMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("unavailable", result.Guide.Status);
        await fixture.GuideReader.DidNotReceive().GetStatisticsAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Verifies physical channels, guide bindings, import metadata, and virtual projections are returned together.
    /// </summary>
    [Fact]
    public async Task GetStatusAsync_ChannelAndGuideState_ReturnsCompleteRemoteDiagnostics()
    {
        // Arrange
        using var fixture = CreateFixture();
        var guideSourceId = GuideSourceId.New();
        fixture.Settings.Streaming = fixture.Settings.Streaming with
        {
            GuideSources =
            [
                new GuideSourceDefinition
                {
                    Id = guideSourceId,
                    Name = "Guide",
                    Adapter = new AdapterConfiguration("guide", JsonSerializer.SerializeToElement(new { }))
                }
            ]
        };
        var guideDriver = CreateGuideDriver();
        var guideSettings = CreateEmptySettingsProvider("guide");
        fixture.Adapters.GetGuide("guide").Returns(guideDriver);
        fixture.Adapters.GetGuideSettings("guide").Returns(guideSettings);
        await fixture.TopologyStore.ReconcileAsync(
            fixture.PhysicalDeviceId,
            [
                new PhysicalSourceChannel
                {
                    Id = new SourceChannelId(fixture.PhysicalDeviceId, "channel-5"),
                    ProviderNumber = "5.1",
                    ProviderName = "News",
                    Metadata = new Dictionary<string, string> { ["videoCodec"] = "h264" }
                }
            ],
            TestContext.Current.CancellationToken);
        var guideChannelId = new GuideChannelId(guideSourceId, "guide-5");
        await fixture.GuideStore.ImportAsync(
            new GuideImport
            {
                SourceId = guideSourceId,
                Channels =
                [
                    new GuideChannel
                    {
                        Id = guideChannelId,
                        Number = "5.1",
                        Name = "News Guide",
                        Affiliate = "ABC"
                    }
                ],
                Programs =
                [
                    new GuideProgram
                    {
                        ChannelId = guideChannelId,
                        StartUtc = DateTimeOffset.UtcNow,
                        EndUtc = DateTimeOffset.UtcNow.AddHours(1),
                        Title = "News"
                    }
                ],
                CoverageStartUtc = DateTimeOffset.UtcNow,
                CoverageEndUtc = DateTimeOffset.UtcNow.AddHours(1),
                SupplementalXml = "<generator-info-name>Test</generator-info-name>"
            },
            TestContext.Current.CancellationToken);

        // Act
        var result = await fixture.Service.GetStatusAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("available", result.Channels.Status);
        var channel = Assert.Single(result.Channels.Items);
        Assert.Equal("5.1", channel.EffectiveNumber);
        Assert.Equal("h264", channel.Metadata["videoCodec"]);
        Assert.Equal("automatic", channel.GuideBinding?.State);
        Assert.Equal("ABC", channel.GuideBinding?.SelectedChannel?.Affiliate);
        Assert.Single(result.Channels.VirtualProjections[0].Channels);
        Assert.Equal("available", result.Guide.SourceImports.Status);
        var import = Assert.Single(result.Guide.SourceImports.Items);
        Assert.Equal(1, import.ChannelCount);
        Assert.Equal(1, import.ProgramCount);
        Assert.True(import.HasSupplementalMetadata);
    }

    private static StatusApiFixture CreateFixture(LineupStartupState? startupState = null, bool useRealStreamingFlows = false, string? clientAddress = "192.0.2.20:54321")
    {
        var xmltvPath = Path.Combine(Path.GetTempPath(), $"lineup-status-{Guid.NewGuid():N}.xml");
        var settings = new LineupSettings
        {
            IsSetupComplete = true,
            XmltvOutputPath = xmltvPath
        };
        var physicalDeviceId = PhysicalDeviceId.New();
        var virtualDeviceId = VirtualDeviceId.New();
        settings.Streaming = new StreamingConfiguration
        {
            PhysicalDevices =
            [
                new PhysicalDeviceDefinition
                {
                    Id = physicalDeviceId,
                    Name = "HDHomeRun FLEX",
                    Adapter = new AdapterConfiguration(
                        "hdhomerun",
                        JsonSerializer.SerializeToElement(new { baseUrl = "http://tuner.local", deviceAuth = "physical-secret" }))
                }
            ],
            VirtualDevices =
            [
                new VirtualDeviceDefinition
                {
                    Id = virtualDeviceId,
                    Name = "Lineup",
                    Adapter = new AdapterConfiguration("hdhomerun", JsonSerializer.SerializeToElement(new { friendlyName = "Lineup" }))
                }
            ]
        };
        var settingsService = Substitute.For<ILineupSettingsService>();
        settingsService.Settings.Returns(settings);
        var guideReader = Substitute.For<ILineupGuideReader>();
        guideReader.GetStatisticsAsync(Arg.Any<CancellationToken>()).Returns(new GuideStatistics(
            7,
            123,
            new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 18, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 17, 23, 0, 0, TimeSpan.Zero)));
        var device = CreatePhysicalDevice();
        var deviceState = Substitute.For<IPhysicalDeviceStatusService>();
        deviceState.Devices.Returns(
        [
            new PhysicalDeviceRuntimeStatus
            {
                Device = new PhysicalDeviceSnapshot
                {
                    Id = physicalDeviceId,
                    Kind = "hdhomerun",
                    Name = device.FriendlyName,
                    Capacity = new PhysicalTunerCapacity(device.TunerCount)
                },
                Status = new PhysicalDeviceStatusSnapshot
                {
                    Device = new PhysicalDeviceSnapshot
                    {
                        Id = physicalDeviceId,
                        Kind = "hdhomerun",
                        Name = device.FriendlyName,
                        Capacity = new PhysicalTunerCapacity(device.TunerCount)
                    },
                    ObservedAtUtc = DateTimeOffset.UtcNow,
                    Details = new Dictionary<string, string>
                    {
                        ["baseUrl"] = device.BaseURL,
                        ["lineupUrl"] = device.LineupURL
                    },
                    Receivers =
                    [
                        new PhysicalReceiverStatus
                        {
                            Index = 0,
                            Channel = "atsc3:587000000",
                            VirtualChannel = "5.1",
                            Target = "http://client.local:5000",
                            LockType = "atsc3",
                            SignalStrength = 90,
                            SignalQuality = 80,
                            SymbolQuality = 100,
                            BitsPerSecond = 18_000_000,
                            PacketsPerSecond = 1000
                        }
                    ]
                }
            }
        ]);
        var autoFetch = Substitute.For<IAutoFetchStateService>();
        autoFetch.IsEnabled.Returns(true);
        autoFetch.IsRunning.Returns(true);
        autoFetch.CurrentProgress.Returns(new FetchProgressInfo
        {
            Status = FetchStatus.Fetching,
            Message = "Fetching guide",
            FetchCount = 2,
            TotalProgramsFetched = 50,
            TotalChannelsFetched = 7
        });
        var activeStreams = Substitute.For<IActiveStreamRegistry>();
        activeStreams.GetActiveStreams().Returns(
        [
            new ActiveStreamSnapshot(
                "stream-1",
                "5.1",
                HostedStreamFormat.Cmaf,
                DateTime.UtcNow.AddMinutes(-2),
                18_000_000,
                [new ActiveStreamTrack(MediaTrackType.Audio, "ac4", "aac", 256_000, 384_000, null, null, 6, 6, 48_000)])
            {
                ClientId = "watch-1",
                AuthenticatedClientId = "grant-1",
                AuthenticatedClientName = "Remote household",
                ClientAddress = clientAddress,
                BrokerRequestId = "broker-request-1",
                WorkerMediaSessionId = "worker-media-1",
                WorkerStartupStage = nameof(VirtualOutputStreamStartupStage.Ready)
            }
        ]);
        var profiles = Substitute.For<IHdHomeRunProxyProfileProvider>();
        profiles.GetProfilesAsync(Arg.Any<CancellationToken>())
            .Returns(
            [
                new HdHomeRunProxyProfileSnapshot
                {
                    VirtualDevice = new VirtualDeviceDefinition
                    {
                        Id = virtualDeviceId,
                        Name = "Lineup",
                        Adapter = new AdapterConfiguration("hdhomerun", JsonSerializer.SerializeToElement(new { }))
                    },
                    IsPrimary = true,
                    DeviceId = uint.Parse(HdHomeRunProxyIdentity.CreateDeviceId("status-test"), System.Globalization.NumberStyles.HexNumber),
                    DeviceAuth = "virtual",
                    TunerCount = 4,
                    FriendlyName = "Lineup",
                    AdvertisedBaseUrl = "http://lineup.local:8080/"
                }
            ]);
        var graph = Substitute.For<IStreamingSessionGraph>();
        graph.GetDiagnostics().Returns(new StreamingSessionGraphDiagnostics(
            [
                new StreamNodeDiagnostics("transform", "source>virtual", 2, true, false, 3)
                {
                    NodeId = "virtual-node",
                    ParentNodeId = "source-node",
                    Stage = "virtual-mpegts"
                }
            ],
            [new PhysicalCapacityDiagnostics(physicalDeviceId, 4, 1, 2)]));
        var physicalSettings = Substitute.For<IAdapterSettingsProvider>();
        physicalSettings.SettingsDescriptor.Returns(new AdapterSettingsDescriptor
        {
            AdapterKind = "hdhomerun",
            Fields =
            [
                new AdapterSettingDescriptor { Key = "baseUrl", Label = "Base URL", Kind = AdapterSettingKind.Uri },
                new AdapterSettingDescriptor { Key = "deviceAuth", Label = "Device authentication", Kind = AdapterSettingKind.Secret }
            ]
        });
        physicalSettings.GetSettings(Arg.Any<AdapterConfiguration>()).Returns(new AdapterSettingsSnapshot
        {
            AdapterKind = "hdhomerun",
            Values =
            [
                new AdapterSettingSnapshot { Key = "baseUrl", Value = JsonSerializer.SerializeToElement("http://tuner.local"), HasValue = true },
                new AdapterSettingSnapshot { Key = "deviceAuth", HasValue = true }
            ]
        });
        var virtualSettings = Substitute.For<IAdapterSettingsProvider>();
        virtualSettings.SettingsDescriptor.Returns(new AdapterSettingsDescriptor
        {
            AdapterKind = "hdhomerun",
            Fields = [new AdapterSettingDescriptor { Key = "friendlyName", Label = "Friendly name", Kind = AdapterSettingKind.Text }]
        });
        virtualSettings.GetSettings(Arg.Any<AdapterConfiguration>()).Returns(new AdapterSettingsSnapshot
        {
            AdapterKind = "hdhomerun",
            Values = [new AdapterSettingSnapshot { Key = "friendlyName", Value = JsonSerializer.SerializeToElement("Lineup"), HasValue = true }]
        });
        var adapters = Substitute.For<IStreamingAdapterRegistry>();
        adapters.GetPhysicalSettings("hdhomerun").Returns(physicalSettings);
        adapters.GetVirtualSettings("hdhomerun").Returns(virtualSettings);
        adapters.GetVirtual("hdhomerun").Returns(Substitute.For<IVirtualTunerDriver>());
        var databasePath = Path.Combine(Path.GetTempPath(), $"lineup-status-{Guid.NewGuid():N}.db");
        var databaseOptions = new DbContextOptionsBuilder<EpgDbContext>().UseSqlite($"Data Source={databasePath};Pooling=False").Options;
        var contexts = new TestDbContextFactory(databaseOptions);
        var topologyStore = new StreamingTopologyStore(contexts);
        var guideStore = new DeviceGuideStore(contexts);
        var virtualOutputRuntime = Substitute.For<IVirtualOutputRuntimeStatus>();
        virtualOutputRuntime.GetSnapshots().Returns(
        [
            new VirtualOutputRuntimeSnapshot
            {
                VirtualDeviceId = virtualDeviceId,
                State = VirtualOutputRuntimeState.Healthy,
                Bindings = ["http://lineup.local:8080/"]
            }
        ]);
        var streamingFlows = Substitute.For<IStreamingFlowSnapshotService>();
        streamingFlows.GetSnapshot().Returns(new StreamingFlowSnapshot(
            [new StreamingFlowNode("multiplexer", StreamingFlowLane.Multiplexer, "Shared Multiplexer", "Reference-counted stream graph", StreamingFlowState.Active, [])],
            [
                new StreamingFlowPath(
                    "flow-1",
                    "stream-1",
                    "192.0.2.20:54321",
                    HostedStreamFormat.Cmaf,
                    "5.1",
                    DateTime.UtcNow,
                    [new StreamingFlowPathStage(StreamingFlowStageKind.Multiplexer, "Multiplexer", "Shared", [])],
                    false,
                    true,
                    2)
            ],
            1,
            1,
            0));
        IStreamingFlowSnapshotService flowService = useRealStreamingFlows
            ? new StreamingFlowSnapshotService(settingsService, graph, [], activeStreams, deviceState)
            : streamingFlows;
        var service = new StatusApiService(
            guideReader,
            settingsService,
            CreateWebSettingsService(settings),
            deviceState,
            autoFetch,
            activeStreams,
            new StatusApiRuntime("runtime-test"),
            NullLogger<StatusApiService>.Instance,
            new XmltvPublicationStore(),
            startupState ?? new LineupStartupState(LineupStartupStatus.Ready),
            profiles,
            graph,
            adapters,
            virtualOutputRuntime,
            topologyStore: topologyStore,
            guideStore: guideStore,
            streamingFlows: flowService);
        return new StatusApiFixture(settings, guideReader, service, adapters, topologyStore, guideStore, physicalDeviceId, databasePath, flowService);
    }

    private static IGuideSourceDriver CreateGuideDriver()
    {
        var driver = Substitute.For<IGuideSourceDriver>();
        driver.Kind.Returns("guide");
        driver.SupportsPhysicalAdapter("hdhomerun").Returns(true);
        return driver;
    }

    private static IAdapterSettingsProvider CreateEmptySettingsProvider(string kind)
    {
        var provider = Substitute.For<IAdapterSettingsProvider>();
        provider.SettingsDescriptor.Returns(new AdapterSettingsDescriptor { AdapterKind = kind });
        provider.GetSettings(Arg.Any<AdapterConfiguration>()).Returns(new AdapterSettingsSnapshot { AdapterKind = kind });
        return provider;
    }

    private static IWebSettingsService CreateWebSettingsService(LineupSettings settings)
    {
        var service = Substitute.For<IWebSettingsService>();
        service.Settings.Returns(_ => new WebSettings
        {
            IsSetupComplete = settings.IsSetupComplete,
            RedactApiClientAddresses = settings.RedactApiClientAddresses,
            RedactApiDeviceAddresses = settings.RedactApiDeviceAddresses,
            RedactApiUrls = settings.RedactApiUrls
        });
        return service;
    }

    private static HDHomeRunDeviceInfo CreatePhysicalDevice() => new()
    {
        FriendlyName = "HDHomeRun FLEX",
        ModelNumber = "HDFX-4K",
        FirmwareName = "hdhomerun_atsc3",
        FirmwareVersion = "20260901",
        DeviceID = "12345678",
        DeviceAuth = "physical-secret",
        BaseURL = "http://tuner.local",
        LineupURL = "http://tuner.local/lineup.json",
        TunerCount = 4
    };

    private sealed record StatusApiFixture(
        LineupSettings Settings,
        ILineupGuideReader GuideReader,
        StatusApiService Service,
        IStreamingAdapterRegistry Adapters,
        StreamingTopologyStore TopologyStore,
        DeviceGuideStore GuideStore,
        PhysicalDeviceId PhysicalDeviceId,
        string DatabasePath,
        IStreamingFlowSnapshotService StreamingFlows) : IDisposable
    {
        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(DatabasePath))
            {
                File.Delete(DatabasePath);
            }
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<EpgDbContext> options) : IDbContextFactory<EpgDbContext>
    {
        public EpgDbContext CreateDbContext() => new(options);
    }
}
