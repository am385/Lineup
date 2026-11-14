using System.Text.Json;
using System.Net;
using Lineup.Core.Common.Configuration;
using Lineup.Core.Extensibility.Configuration;
using Lineup.Core.VirtualOutputs.Protocol;
using Lineup.Core.VirtualOutputs.Runtime;
using Lineup.App.Web.Services;
using Lineup.Plugin.GuideOutputs.Xmltv.Plugin;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.App.Web.Tests.Services;

/// <summary>
/// Verifies stable status API aggregation and privacy behavior.
/// </summary>
public class StatusApiServiceTests
{
    private static readonly JsonSerializerOptions _webJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Names failed workers, sources, pending publications and capacity pressure without exposing provider failure payloads or initiating work.</summary>
    [Fact]
    public async Task GetStatusAsync_Operations_ReportsSafeCachedReasonsAndRecovery()
    {
        // Arrange
        var runtime = Substitute.For<IStreamingTopologyRuntime>();
        var guides = Substitute.For<ITopologyGuideService>();
        var statuses = Substitute.For<IPhysicalDeviceStatusService>();
        var admission = new MediaWorkAdmission(new() { MaximumProducers = 1 });
        await using var producer = await admission.AcquireAsync(MediaWorkKind.Producer, cancellationToken: TestContext.Current.CancellationToken);
        using var fixture = CreateFixture(statuses: statuses, mediaWork: admission, topologyRuntime: runtime, topologyGuides: guides);
        var source = new GuideSourceDefinition { Id = GuideSourceId.New(), Name = "Household guide", Adapter = new AdapterConfiguration("guide", JsonSerializer.SerializeToElement(new { })) };
        var output = XmltvGuideOutputDriver.CreateBuiltIn(fixture.Settings.XmltvOutputPath);
        var guideDriver = CreateGuideDriver();
        var guideSettings = CreateEmptySettingsProvider("guide");
        fixture.Adapters.GetGuide("guide").Returns(guideDriver);
        fixture.Adapters.GetGuideSettings("guide").Returns(guideSettings);
        fixture.Adapters.GetGuideOutput(XmltvGuideOutputDriver.AdapterKind).Returns(new XmltvGuideOutputDriver());
        fixture.Adapters.GetGuideOutputSettings(XmltvGuideOutputDriver.AdapterKind).Returns(new XmltvGuideOutputDriver());
        fixture.Settings.Streaming = fixture.Settings.Streaming with
        {
            GuideSources = [source],
            GuideOutputs = [output],
            VirtualDevices = fixture.Settings.Streaming.VirtualDevices.Select(device => device with { Hosting = new() }).ToArray()
        };
        fixture.Settings.GuidePublicationRetries[output.Id.Value] = DateTime.UtcNow.AddMinutes(1);
        var revision = ConfigurationRevision.Create(fixture.Settings.Streaming);
        runtime.Application.Returns(new StreamingTopologyApplication { DesiredRevision = revision, AttemptedRevision = revision, AppliedRevision = "previous", Phase = TopologyApplicationPhase.Degraded });
        var observed = DateTimeOffset.UtcNow.AddMinutes(-1);
        var lastSuccess = observed.AddDays(-1);
        guides.SourceRefreshes.Returns([new(source.Id, ConfigurationRevision.Create(source), false, observed, lastSuccess)]);
        var virtualId = fixture.Settings.Streaming.VirtualDevices[0].Id;
        fixture.VirtualOutputRuntime.Set(new() { VirtualDeviceId = virtualId, State = VirtualOutputRuntimeState.Failed, LastFailure = "private-provider-failure-sentinel" });

        // Act
        var failed = await fixture.Service.GetStatusAsync(TestContext.Current.CancellationToken);
        var operations = Assert.IsType<OperationalStatusResponse>(failed.Operations);
        var serialized = JsonSerializer.Serialize(operations, _webJsonOptions);
        guides.SourceRefreshes.Returns([new(source.Id, ConfigurationRevision.Create(source), true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)]);
        fixture.VirtualOutputRuntime.Set(new() { VirtualDeviceId = virtualId, State = VirtualOutputRuntimeState.Healthy });
        var recovered = await fixture.Service.GetStatusAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("degraded", operations.Status);
        Assert.Equal("eventual", operations.Consistency);
        Assert.Equal(revision, operations.Topology?.DesiredRevision);
        Assert.Equal("previous", operations.Topology?.AppliedRevision);
        Assert.Contains(operations.Issues, issue => issue.Code == "worker_failed" && issue.ResourceName == "Lineup");
        var sourceFailure = Assert.Single(operations.Issues, issue => issue.Code == "guide_source_failed");
        Assert.Equal("Household guide", sourceFailure.ResourceName);
        Assert.Equal(observed.UtcDateTime, sourceFailure.ObservedAtUtc);
        Assert.Equal(lastSuccess.UtcDateTime, sourceFailure.LastSuccessUtc);
        Assert.Contains(operations.Issues, issue => issue.Code == "guide_publication_pending" && issue.ResourceId == output.Id.ToString());
        Assert.Contains(operations.Issues, issue => issue.Code == "media_producer_limit_reached");
        Assert.DoesNotContain("private-provider-failure-sentinel", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("physical-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("http://", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(recovered.Operations!.Issues, issue => issue.Code is "worker_failed" or "guide_source_failed");
        await statuses.DidNotReceive().RefreshAsync(Arg.Any<StreamingConfiguration>(), Arg.Any<CancellationToken>());
        await guides.DidNotReceive().RefreshAsync(Arg.Any<CancellationToken>());
        await guides.DidNotReceive().PublishAsync(Arg.Any<CancellationToken>());
        await fixture.GuideReader.Received(2).GetStatisticsAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>Distinguishes successful, pending, degraded and unobserved application state rather than treating missing observations as healthy.</summary>
    [Theory]
    [InlineData(TopologyApplicationPhase.Applied, "healthy")]
    [InlineData(TopologyApplicationPhase.Pending, "pending")]
    [InlineData(TopologyApplicationPhase.Degraded, "degraded")]
    [InlineData(null, "unknown")]
    public async Task GetStatusAsync_Operations_DistinguishesApplicationState(TopologyApplicationPhase? phase, string expected)
    {
        // Arrange
        var runtime = phase is null ? null : Substitute.For<IStreamingTopologyRuntime>();
        using var fixture = CreateFixture(topologyRuntime: runtime);
        if (runtime is not null)
        {
            var revision = ConfigurationRevision.Create(fixture.Settings.Streaming);
            runtime.Application.Returns(new StreamingTopologyApplication { DesiredRevision = revision, Phase = phase!.Value, AppliedRevision = phase == TopologyApplicationPhase.Applied ? revision : null });
        }

        // Act
        var result = await fixture.Service.GetStatusAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(expected, result.Operations?.Status);
        Assert.Equal("eventual", result.Operations?.Consistency);
        Assert.Equal(phase is null, result.Operations?.Topology is null);
    }

    /// <summary>Uses the committed source coverage boundary, not the age of a fetch or an arbitrary staleness threshold.</summary>
    [Theory]
    [InlineData(-1, true)]
    [InlineData(1, false)]
    public async Task GetStatusAsync_Operations_ReportsExpiredSourceCoverage(int hoursRemaining, bool expired)
    {
        // Arrange
        using var fixture = CreateFixture();
        var source = new GuideSourceDefinition { Id = GuideSourceId.New(), Name = "Coverage guide", Adapter = new AdapterConfiguration("guide", JsonSerializer.SerializeToElement(new { })) };
        fixture.Settings.Streaming = fixture.Settings.Streaming with { GuideSources = [source] };
        var driver = CreateGuideDriver();
        var provider = CreateEmptySettingsProvider("guide");
        fixture.Adapters.GetGuide("guide").Returns(driver);
        fixture.Adapters.GetGuideSettings("guide").Returns(provider);
        var fetched = DateTimeOffset.UtcNow.AddDays(-2);
        await fixture.GuideStore.ImportAsync(new GuideImport
        {
            SourceId = source.Id,
            Channels = [],
            Programs = [],
            FetchedAtUtc = fetched,
            CoverageStartUtc = fetched,
            CoverageEndUtc = DateTimeOffset.UtcNow.AddHours(hoursRemaining)
        }, TestContext.Current.CancellationToken);

        // Act
        var result = await fixture.Service.GetStatusAsync(TestContext.Current.CancellationToken);
        var reasons = Assert.IsType<OperationalStatusResponse>(result.Operations).Issues.Where(issue => issue.ResourceId == source.Id.ToString()).ToArray();

        // Assert
        Assert.Equal(expired, reasons.Any(issue => issue.Code == "guide_source_expired"));
        Assert.DoesNotContain(reasons, issue => issue.Code is "guide_source_failed" or "guide_source_missing");
        if (expired)
        {
            Assert.Equal(fetched.UtcDateTime, Assert.Single(reasons).LastSuccessUtc);
        }
    }

    /// <summary>Reports effective deployment-wide media usage without conflating it with viewer admission.</summary>
    [Fact]
    public async Task GetStatusAsync_MediaResources_ReportsAuthoritativeUsageAndScope()
    {
        // Arrange
        var admission = new MediaWorkAdmission(new() { MaximumProducers = 3, MaximumProcessingOperations = 2, MinimumMediaFreeSpaceMib = 512 });
        await using var producer = await admission.AcquireAsync(MediaWorkKind.Producer, cancellationToken: TestContext.Current.CancellationToken);
        await using var processing = await admission.AcquireAsync(MediaWorkKind.Processing, cancellationToken: TestContext.Current.CancellationToken);
        using var fixture = CreateFixture(mediaWork: admission);

        // Act
        var response = await fixture.Service.GetStatusAsync(TestContext.Current.CancellationToken);
        var json = JsonSerializer.SerializeToElement(response, _webJsonOptions).GetProperty("mediaResources");

        // Assert
        Assert.Equal(1, json.GetProperty("producers").GetInt32());
        Assert.Equal(3, json.GetProperty("maximumProducers").GetInt32());
        Assert.Equal(1, json.GetProperty("processing").GetInt32());
        Assert.Equal(2, json.GetProperty("maximumProcessing").GetInt32());
        Assert.Equal(512, json.GetProperty("minimumFreeSpaceMib").GetInt32());
        Assert.Equal("deployment", json.GetProperty("enforcementScope").GetString());
    }

    /// <summary>Verifies status reports independent observation intervals, guide generation and configuration drift honestly.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetStatusAsync_ExposesObservationIntervalsAndConfigurationDrift(bool changeConfiguration)
    {
        // Arrange
        using var fixture = CreateFixture();
        var generation = new GuideReadGeneration(DateTimeOffset.UtcNow, "contributing-import-revision", "guide-configuration-revision");
        fixture.GuideReader.GetStatisticsAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (changeConfiguration)
            {
                fixture.Settings.TargetDays++;
            }
            return new GuideStatistics(1, 2, null, null, null) { Generation = generation };
        });

        // Act
        var status = await fixture.Service.GetStatusAsync(TestContext.Current.CancellationToken);

        // Assert
        var observation = Assert.IsType<StatusObservationResponse>(status.Observation);
        Assert.Equal("eventual", observation.Consistency);
        Assert.Equal(changeConfiguration, observation.ConfigurationChangedDuringRead);
        Assert.NotEmpty(observation.CoreConfigurationRevision);
        Assert.NotEmpty(observation.WebConfigurationRevision);
        Assert.Same(generation, status.Guide.Generation);
        Assert.NotNull(status.Guide.SourceImportsObservedAtUtc);
        Assert.Equal(14, observation.Sections.Count);
        Assert.All(observation.Sections.Values, section =>
        {
            Assert.True(section.StartedAtUtc >= status.GeneratedAtUtc);
            Assert.True(section.CompletedAtUtc >= section.StartedAtUtc);
        });
        Assert.True(observation.Sections["channels"].StartedAtUtc >= observation.Sections["guide"].CompletedAtUtc);
    }

    /// <summary>Verifies advertised built-in and custom guide URLs against the production download endpoints, including privacy and unavailable publications.</summary>
    [Theory]
    [InlineData(true, false, true, "custom_guide")]
    [InlineData(true, false, true, "custom guide+key")]
    [InlineData(true, true, true, "custom_guide")]
    [InlineData(false, false, true, "custom_guide")]
    [InlineData(true, false, false, "custom_guide")]
    public async Task GetStatusAsync_GuideDownloads_AdvertisesWorkingProductionRoutes(bool enabled, bool redactUrls, bool published, string customKey)
    {
        // Arrange
        using var fixture = CreateFixture();
        var root = Directory.CreateTempSubdirectory("lineup-status-guides-");
        try
        {
            var builtIn = XmltvGuideOutputDriver.CreateBuiltIn(Path.Combine(root.FullName, "global.xml")) with { Enabled = enabled };
            var custom = new GuideOutputDefinition
            {
                Id = GuideOutputId.New(),
                Name = "Custom guide",
                Key = customKey,
                Enabled = enabled,
                Adapter = XmltvGuideOutputDriver.CreateBuiltIn(Path.Combine(root.FullName, "custom.xml")).Adapter
            };
            fixture.Settings.Streaming = fixture.Settings.Streaming with { GuideOutputs = [builtIn, custom] };
            fixture.Settings.XmltvOutputPath = new XmltvGuideOutputDriver().ResolveDestination(builtIn.Adapter).Path;
            fixture.WebSettings.RedactApiUrls = redactUrls;
            var driver = new XmltvGuideOutputDriver();
            fixture.Adapters.GetGuideOutput(XmltvGuideOutputDriver.AdapterKind).Returns(driver);
            fixture.Adapters.GetGuideOutputSettings(XmltvGuideOutputDriver.AdapterKind).Returns(driver);
            if (published)
            {
                foreach (var output in new[] { builtIn, custom })
                {
                    await File.WriteAllTextAsync(driver.ResolveDestination(output.Adapter).Path, $"<tv>{output.Key}</tv>", TestContext.Current.CancellationToken);
                }
            }
            var settingsService = Substitute.For<ILineupSettingsService>();
            settingsService.Settings.Returns(fixture.Settings);
            var guides = Substitute.For<ITopologyGuideService>();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root.FullName });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            builder.Services.AddSingleton(settingsService);
            builder.Services.AddSingleton(fixture.Adapters);
            builder.Services.AddSingleton(guides);
            builder.Services.AddSingleton<IXmltvPublicationStore, XmltvPublicationStore>();
            await using var app = builder.Build();
            app.MapGuideOutputEndpoints();
            await app.StartAsync(TestContext.Current.CancellationToken);
            try
            {
                using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(app.Urls)), Timeout = TimeSpan.FromSeconds(10) };

                // Act
                var status = await fixture.Service.GetStatusAsync(TestContext.Current.CancellationToken);

                // Assert
                foreach (var output in new[] { builtIn, custom })
                {
                    var projected = Assert.Single(status.Guide.Outputs, candidate => candidate.Key == output.Key);
                    var expectedUrl = output.IsBuiltIn ? "/api/xmltv" : $"/api/guide-outputs/{Uri.EscapeDataString(output.Key)}";
                    Assert.Equal(enabled && published, projected.Available);
                    using var response = await client.GetAsync(projected.DownloadUrl ?? expectedUrl, TestContext.Current.CancellationToken);
                    Assert.Equal(enabled && published ? HttpStatusCode.OK : HttpStatusCode.NotFound, response.StatusCode);
                    Assert.Equal(enabled && !redactUrls ? expectedUrl : null, projected.DownloadUrl);
                    if (enabled && published)
                    {
                        Assert.Equal($"<tv>{output.Key}</tv>", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
                        Assert.Equal("application/xml", response.Content.Headers.ContentType?.MediaType);
                        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
                    }
                    if (enabled && !published)
                    {
                        await guides.Received(1).PublishAsync(output.Id, Arg.Any<CancellationToken>());
                    }
                }
                if (!enabled || published)
                {
                    await guides.DidNotReceive().PublishAsync(Arg.Any<GuideOutputId>(), Arg.Any<CancellationToken>());
                }
            }
            finally
            {
                await app.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>Verifies the public API reports authoritative over-limit draining rather than inferred worker-health counters.</summary>
    [Fact]
    public async Task GetStatusAsync_PluginConnectionBudget_ReportsDrainingAndOwnership()
    {
        // Arrange
        var budget = Substitute.For<IPluginConnectionBudget>();
        var worker = VirtualDeviceId.New();
        budget.GetSnapshot().Returns(new PluginConnectionBudgetSnapshot(1, 2, 1, new Dictionary<VirtualDeviceId, int> { [worker] = 1 }));
        using var fixture = CreateFixture(pluginConnections: budget);

        // Act
        var status = await fixture.Service.GetStatusAsync(TestContext.Current.CancellationToken);
        var serialized = JsonSerializer.SerializeToElement(status, _webJsonOptions);

        // Assert
        var usage = serialized.GetProperty("pluginConnections");
        Assert.Equal(1, usage.GetProperty("maximumConnections").GetInt32());
        Assert.Equal(2, usage.GetProperty("activeConnections").GetInt32());
        Assert.Equal(1, usage.GetProperty("managementHostedConnections").GetInt32());
        Assert.True(usage.GetProperty("isDraining").GetBoolean());
        Assert.Equal(1, usage.GetProperty("workerConnections").GetProperty(worker.ToString()).GetInt32());
    }

    /// <summary>Verifies the public device and worker projections agree on external-worker availability and recovery.</summary>
    [Theory]
    [InlineData(VirtualOutputRuntimeState.Healthy, true)]
    [InlineData(VirtualOutputRuntimeState.Failed, false)]
    [InlineData(VirtualOutputRuntimeState.Starting, false)]
    [InlineData(VirtualOutputRuntimeState.Degraded, false)]
    public async Task GetStatusAsync_ExternalWorkerState_ControlsPublicAvailability(VirtualOutputRuntimeState state, bool available)
    {
        // Arrange
        using var fixture = CreateFixture();
        var device = Assert.Single(fixture.Settings.Streaming.VirtualDevices);
        fixture.Settings.Streaming = fixture.Settings.Streaming with
        {
            VirtualDevices = [device with { Hosting = new VirtualOutputHostingDefinition { ExecutionMode = VirtualOutputExecutionMode.ExternalProcess, WorkerPolicyKey = "external" } }]
        };
        var failure = state == VirtualOutputRuntimeState.Failed ? "The worker lease has expired." : null;
        fixture.VirtualOutputRuntime.Update(device.Id, current => current! with { State = state, Generation = 2, LastFailure = failure });

        // Act
        var result = await fixture.Service.GetStatusAsync(TestContext.Current.CancellationToken);

        // Assert
        var output = Assert.Single(result.VirtualDevices);
        var worker = Assert.Single(result.VirtualOutputWorkers);
        Assert.Equal(available, output.Available);
        Assert.Equal(failure, output.Error);
        Assert.Equal(state.ToString(), worker.State);
        Assert.Equal(2, worker.Generation);
        Assert.Equal(failure, worker.LastFailure);
    }

    /// <summary>Verifies failed activation appears in the runtime API projection rather than only in configured-device metadata.</summary>
    [Fact]
    public async Task GetStatusAsync_PhysicalActivationFailure_ReportsUnavailableDevice()
    {
        // Arrange
        var catalog = Substitute.For<IPhysicalDeviceCatalog>();
        catalog.TryPublishAsync(Arg.Any<StreamingConfiguration>(), Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            await call.Arg<Func<CancellationToken, Task>>()(call.Arg<CancellationToken>());
            return true;
        });
        var statuses = new PhysicalDeviceStatusService(catalog);
        using var fixture = CreateFixture(statuses: statuses);
        var healthy = Assert.Single(fixture.Settings.Streaming.PhysicalDevices);
        var failed = healthy with { Id = PhysicalDeviceId.New(), Name = "Unavailable" };
        fixture.Settings.Streaming = fixture.Settings.Streaming with { PhysicalDevices = [healthy, failed] };
        var live = Substitute.For<IPhysicalTunerDevice>();
        live.Snapshot.Returns(new PhysicalDeviceSnapshot { Id = healthy.Id, Name = healthy.Name, Kind = healthy.Adapter.Kind, Capacity = new PhysicalTunerCapacity(1) });
        catalog.Devices.Returns([live]);
        await statuses.RefreshAsync(fixture.Settings.Streaming, TestContext.Current.CancellationToken);

        // Act
        var result = await fixture.Service.GetStatusAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, result.Configuration.Streaming.PhysicalDevices.Count);
        Assert.Equal(2, result.PhysicalDevices.Count);
        Assert.Equal("available", Assert.Single(result.PhysicalDevices, device => device.PhysicalDeviceId == healthy.Id.ToString()).Status);
        var unavailable = Assert.Single(result.PhysicalDevices, device => device.PhysicalDeviceId == failed.Id.ToString());
        Assert.Equal(failed.Name, unavailable.Name);
        Assert.Equal("error", unavailable.Status);
        Assert.Contains("activation failed", unavailable.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, unavailable.Capacity);
        Assert.False(unavailable.SupportsReceiverControl);
        Assert.False(unavailable.SupportsRestart);
        Assert.Empty(unavailable.Details);
        Assert.Null(unavailable.ObservedAtUtc);
    }

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
        fixture.WebSettings.RedactApiClientAddresses = redactClients;
        fixture.WebSettings.RedactApiDeviceAddresses = redactDevices;
        fixture.WebSettings.RedactApiUrls = redactUrls;
        var snapshot = fixture.StreamingFlows.GetSnapshot();
        var originalConsumer = Assert.Single(snapshot.GetLane(StreamingFlowLane.Consumer));
        var originalPath = Assert.Single(snapshot.ActivePaths);

        // Act
        var result = await fixture.Service.GetStatusAsync(TestContext.Current.CancellationToken);
        var json = JsonSerializer.Serialize(result, _webJsonOptions);

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
        fixture.WebSettings.RedactApiClientAddresses = redactClients;

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
        fixture.WebSettings.RedactApiClientAddresses = true;
        fixture.WebSettings.RedactApiDeviceAddresses = false;
        fixture.WebSettings.RedactApiUrls = false;

        // Act
        var result = await fixture.Service.GetStatusAsync(TestContext.Current.CancellationToken);
        var json = JsonSerializer.Serialize(result, _webJsonOptions);

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

    /// <summary>Verifies partial guides retain healthy counts while reporting safe structured source exclusions.</summary>
    [Fact]
    public async Task GetStatusAsync_PartialGuide_ReportsDegradedSourcesAlongsideCounts()
    {
        // Arrange
        using var fixture = CreateFixture();
        var sourceId = GuideSourceId.New();
        fixture.GuideReader.GetStatisticsAsync(Arg.Any<CancellationToken>()).Returns(new GuideStatistics(7, 123, null, null, null)
        {
            SourceDiagnostics =
            [
                new GuideSourceDiagnostic
                {
                    SourceId = sourceId,
                    SourceName = "Missing source",
                    Code = GuideSourceDiagnosticCode.AdapterUnavailable,
                    Message = "Install the source plugin."
                }
            ]
        });

        // Act
        var result = await fixture.Service.GetStatusAsync(TestContext.Current.CancellationToken);
        var json = JsonSerializer.Serialize(result, _webJsonOptions);

        // Assert
        Assert.Equal("degraded", result.Guide.Status);
        Assert.Contains("partial", result.Guide.Error, StringComparison.Ordinal);
        Assert.Equal(7, result.Guide.ChannelCount);
        Assert.Equal(123, result.Guide.ProgramCount);
        var diagnostic = Assert.Single(result.Guide.SourceDiagnostics);
        Assert.Equal(sourceId.ToString(), diagnostic.SourceId);
        Assert.Equal("Missing source", diagnostic.SourceName);
        Assert.Equal("AdapterUnavailable", diagnostic.Code);
        Assert.Contains("\"sourceDiagnostics\":[", json, StringComparison.Ordinal);
        Assert.DoesNotContain("physical-secret", json, StringComparison.Ordinal);
        Assert.Equal("available", result.PhysicalDevices[0].Status);
    }

    /// <summary>
    /// Verifies each privacy category independently nulls stable response properties.
    /// </summary>
    [Fact]
    public async Task GetStatusAsync_AllPrivacyEnabled_ReturnsNullNetworkProperties()
    {
        // Arrange
        using var fixture = CreateFixture();
        fixture.WebSettings.RedactApiClientAddresses = true;
        fixture.WebSettings.RedactApiDeviceAddresses = true;
        fixture.WebSettings.RedactApiUrls = true;

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

    private static StatusApiFixture CreateFixture(
        LineupStartupState? startupState = null,
        bool useRealStreamingFlows = false,
        string? clientAddress = "192.0.2.20:54321",
        IPhysicalDeviceStatusService? statuses = null,
        IPluginConnectionBudget? pluginConnections = null,
        MediaWorkAdmission? mediaWork = null,
        IStreamingTopologyRuntime? topologyRuntime = null,
        ITopologyGuideService? topologyGuides = null
    )
    {
        var xmltvPath = Path.Combine(Path.GetTempPath(), $"lineup-status-{Guid.NewGuid():N}.xml");
        var settings = new LineupSettings
        {
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
        var virtualOutputRuntime = new VirtualOutputRuntimeStatus();
        virtualOutputRuntime.Set(new VirtualOutputRuntimeSnapshot
        {
            VirtualDeviceId = virtualDeviceId,
            State = VirtualOutputRuntimeState.Healthy,
            Bindings = ["http://lineup.local:8080/"]
        });
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
            ? new StreamingFlowSnapshotService(settingsService, graph, [], activeStreams, statuses ?? deviceState)
            : streamingFlows;
        var webSettings = new WebSettings { IsSetupComplete = true };
        var webSettingsService = Substitute.For<IWebSettingsService>();
        webSettingsService.Settings.Returns(webSettings);
        var service = new StatusApiService(
            guideReader,
            settingsService,
            webSettingsService,
            statuses ?? deviceState,
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
            streamingFlows: flowService,
            pluginConnections: pluginConnections,
            mediaWork: mediaWork,
            topologyRuntime: topologyRuntime,
            topologyGuides: topologyGuides);
        return new StatusApiFixture(settings, webSettings, guideReader, service, adapters, topologyStore, guideStore, physicalDeviceId, databasePath, flowService, virtualOutputRuntime);
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
        WebSettings WebSettings,
        ILineupGuideReader GuideReader,
        StatusApiService Service,
        IStreamingAdapterRegistry Adapters,
        StreamingTopologyStore TopologyStore,
        DeviceGuideStore GuideStore,
        PhysicalDeviceId PhysicalDeviceId,
        string DatabasePath,
        IStreamingFlowSnapshotService StreamingFlows,
        VirtualOutputRuntimeStatus VirtualOutputRuntime
    )
        : IDisposable
    {
        /// <inheritdoc />
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
        /// <summary>Creates db context.</summary>
        /// <returns>The operation's result.</returns>
        public EpgDbContext CreateDbContext() => new(options);
    }
}
