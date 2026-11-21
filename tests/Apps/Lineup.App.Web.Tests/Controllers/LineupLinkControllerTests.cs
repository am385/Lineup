using System.Text.Json;
using Lineup.Plugin.LineupLink.Protocol;
using Lineup.Plugin.LineupLink.Virtual;
using Lineup.App.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.App.Web.Tests.Controllers;

/// <summary>Verifies management-hosted Lineup Link endpoint selection.</summary>
public sealed class LineupLinkControllerTests
{
    /// <summary>Oversized management guides fail explicitly without returning a partial guide document.</summary>
    [Fact]
    public async Task GetGuideAsync_OverEnvelope_ReturnsActionableUnavailable()
    {
        // Arrange
        var guides = Substitute.For<IGuideOrchestrator>();
        var id = new VirtualDeviceId(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        var channel = new VirtualGuideChannel { ChannelId = new(id, Guid.NewGuid()), Number = "1", Name = "Example", Programs = [] };
        guides.BuildSnapshotAsync(id, Arg.Any<CancellationToken>()).Returns(new VirtualGuideSnapshot
        {
            VirtualDeviceId = id,
            Channels = Enumerable.Repeat(channel, GuideTransfer.MaximumChannels + 1).ToArray()
        });
        var controller = CreateController(null, authenticated: true, guides: guides);

        // Act
        var result = await controller.GetGuideAsync(id.Value, TestContext.Current.CancellationToken);

        // Assert
        var response = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, response.StatusCode);
        Assert.Equal("guide_limit", Assert.IsType<LineupLinkError>(response.Value).Code);
    }

    /// <summary>Verifies authorized Lineup Link streams are rejected before source work when active or startup capacity is exhausted.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetStreamAsync_AdmissionFull_DoesNotOpenSource(bool pending)
    {
        // Arrange
        var registry = new ActiveStreamRegistry(resources: new() { MaximumPendingStarts = 1 });
        using var reservation = pending ? registry.TryBeginStartup(0, () => { }) : null;
        if (!pending)
        {
            registry.Register(new ActiveStreamSnapshot("existing", "1", HostedStreamFormat.MpegTs, DateTime.UtcNow, null, []));
        }
        var streams = Substitute.For<IVirtualMpegTsOutputService>();
        var controller = CreateController(null, activeStreams: registry, streams: streams, maximum: pending ? 0 : 1, authenticated: true);

        // Act
        var result = await controller.GetStreamAsync(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), "1", TestContext.Current.CancellationToken);

        // Assert
        var response = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status429TooManyRequests, response.StatusCode);
        Assert.Equal("stream_limit", Assert.IsType<LineupLinkError>(response.Value).Code);
        Assert.Empty(streams.ReceivedCalls());
        Assert.Equal(pending ? 0 : 1, registry.GetActiveStreams().Count);
    }

    /// <summary>Verifies a management-hosted output reaches normal bearer authentication.</summary>
    [Fact]
    public void GetManifest_ManagementHostedOutput_RequiresBearerGrant()
    {
        // Arrange
        var controller = CreateController(hosting: null);

        // Act
        var result = controller.GetManifest(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));

        // Assert
        Assert.IsType<UnauthorizedObjectResult>(result.Result);
    }

    /// <summary>Verifies a dedicated output is not also exposed by the management host.</summary>
    [Fact]
    public void GetManifest_DedicatedOutput_ReturnsNotFound()
    {
        // Arrange
        var controller = CreateController(new VirtualOutputHostingDefinition());

        // Act
        var result = controller.GetManifest(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));

        // Assert
        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    /// <summary>Verifies encryption-required management outputs reject direct HTTP requests.</summary>
    [Fact]
    public void GetManifest_EncryptionRequiredOverHttp_ReturnsUpgradeRequired()
    {
        // Arrange
        var controller = CreateController(hosting: null, requireEncryption: true);

        // Act
        var result = controller.GetManifest(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));

        // Assert
        Assert.Equal(StatusCodes.Status426UpgradeRequired, Assert.IsType<ObjectResult>(result.Result).StatusCode);
    }

    /// <summary>Verifies normalized HTTPS requests proceed to bearer authentication.</summary>
    [Fact]
    public void GetManifest_EncryptionRequiredOverHttps_RequiresBearerGrant()
    {
        // Arrange
        var controller = CreateController(hosting: null, requireEncryption: true);
        controller.Request.Scheme = Uri.UriSchemeHttps;

        // Act
        var result = controller.GetManifest(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));

        // Assert
        Assert.IsType<UnauthorizedObjectResult>(result.Result);
    }

    /// <summary>Publishes every observed tuner of subscribed sources without polling, exposing targets, or treating information-only refreshes as tuner observations.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GetManifest_Subscription_ReturnsCompleteSanitizedCachedStatus(bool secondObserved)
    {
        // Arrange
        var definitions = Enumerable.Range(0, 4).Select(index => new PhysicalDeviceDefinition
        {
            Id = PhysicalDeviceId.New(),
            Name = $"Source {index}",
            Order = index,
            Enabled = index != 3,
            Adapter = new AdapterConfiguration("test", JsonSerializer.SerializeToElement(new { }))
        }).ToArray();
        var observedAt = new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
        var snapshots = definitions.Select(definition => new PhysicalDeviceSnapshot
        {
            Id = definition.Id,
            Name = definition.Name,
            Kind = definition.Adapter.Kind,
            Capacity = new PhysicalTunerCapacity(4)
        }).ToArray();
        var statuses = Substitute.For<IPhysicalDeviceStatusService>();
        statuses.Devices.Returns(snapshots.Select((snapshot, index) => new PhysicalDeviceRuntimeStatus
        {
            Device = snapshot,
            Status = new PhysicalDeviceStatusSnapshot
            {
                Device = snapshot,
                Details = new Dictionary<string, string> { ["Private information"] = "not-published" },
                Receivers = Enumerable.Range(0, 4).Select(receiver => new PhysicalReceiverStatus
                {
                    Index = receiver,
                    IsActive = true,
                    IsStreaming = true,
                    HasLock = true,
                    VirtualChannel = $"2.{receiver + 1}",
                    SignalStrength = 98,
                    SignalQuality = 87,
                    SymbolQuality = 100,
                    Target = "udp://192.0.2.1:5000"
                }).ToArray()
            },
            StatusObservedAtUtc = index == 1 && !secondObserved ? null : observedAt,
            InformationObservedAtUtc = observedAt.AddMinutes(1),
            Error = index == 1 && secondObserved ? "Status refresh failed (IOException)." : null
        }).ToArray());
        var physicalDevices = snapshots.Select(snapshot =>
        {
            var device = Substitute.For<IPhysicalTunerDevice>();
            device.Snapshot.Returns(snapshot);
            return device;
        }).ToArray();
        var catalog = Substitute.For<IPhysicalDeviceCatalog>();
        catalog.Devices.Returns(physicalDevices);
        var controller = CreateController(null, authenticated: true, physicalDevices: definitions, physicalCatalog: catalog, deviceStatuses: statuses,
            subscription: new PhysicalDeviceSubscription { Mode = PhysicalDeviceSubscriptionMode.Selected, SelectedDeviceIds = [definitions[0].Id, definitions[1].Id, definitions[3].Id] });

        // Act
        var manifest = Assert.IsType<LineupLinkManifest>(controller.GetManifest(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")).Value);

        // Assert
        Assert.Equal(8, manifest.TunerCount);
        var upstream = Assert.IsAssignableFrom<IReadOnlyList<UpstreamPhysicalDeviceStatus>>(manifest.UpstreamDevices);
        Assert.Equal(definitions.Take(2).Select(device => device.Id.Value), upstream.Select(device => device.DeviceId));
        Assert.Equal(observedAt, upstream[0].ObservedAtUtc);
        Assert.Equal(4, upstream[0].Receivers.Count);
        Assert.All(upstream[0].Receivers, receiver =>
        {
            Assert.True(receiver.IsStreaming);
            Assert.Equal(98, receiver.SignalStrength);
            Assert.Equal(87, receiver.SignalQuality);
            Assert.Equal(100, receiver.SymbolQuality);
            Assert.Null(receiver.Target);
        });
        Assert.Equal(secondObserved ? observedAt : (DateTimeOffset?)null, upstream[1].ObservedAtUtc);
        Assert.Equal(secondObserved ? 4 : 0, upstream[1].Receivers.Count);
        Assert.NotNull(upstream[1].Error);
        Assert.DoesNotContain("192.0.2.1", JsonSerializer.Serialize(manifest), StringComparison.Ordinal);
        Assert.DoesNotContain("not-published", JsonSerializer.Serialize(manifest), StringComparison.Ordinal);
        Assert.All(statuses.ReceivedCalls(), call => Assert.Equal("get_Devices", call.GetMethodInfo().Name));
    }

    private static LineupLinkController CreateController(
        VirtualOutputHostingDefinition? hosting,
        bool requireEncryption = false,
        IActiveStreamRegistry? activeStreams = null,
        IVirtualMpegTsOutputService? streams = null,
        int maximum = 0,
        bool authenticated = false,
        IGuideOrchestrator? guides = null,
        IReadOnlyList<PhysicalDeviceDefinition>? physicalDevices = null,
        PhysicalDeviceSubscription? subscription = null,
        IPhysicalDeviceCatalog? physicalCatalog = null,
        IPhysicalDeviceStatusService? deviceStatuses = null
    )
    {
        var id = new VirtualDeviceId(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        var driver = new LineupLinkVirtualTunerDriver();
        var grant = LineupLinkAccessGrantManager.Create(new LineupLinkVirtualConfiguration
        {
            InstanceId = Guid.NewGuid(),
            RequireEncryption = requireEncryption,
            PublicBaseUrl = requireEncryption ? "https://localhost:8443/" : "http://localhost:8080/"
        }, "test", null, DateTime.UtcNow);
        var settings = Substitute.For<ILineupSettingsService>();
        settings.Settings.Returns(new LineupSettings
        {
            MaximumConcurrentStreams = maximum,
            Streaming = new StreamingConfiguration
            {
                PhysicalDevices = physicalDevices ?? [],
                VirtualDevices =
                [
                    new VirtualDeviceDefinition
                    {
                        Id = id,
                        Name = "Lineup Link",
                        Hosting = hosting,
                        Subscription = subscription ?? new PhysicalDeviceSubscription(),
                        Adapter = new AdapterConfiguration(
                            LineupLinkProtocol.AdapterKind,
                            JsonSerializer.SerializeToElement(grant.Configuration))
                    }
                ]
            }
        });
        var adapters = Substitute.For<IStreamingAdapterRegistry>();
        adapters.GetVirtual(LineupLinkProtocol.AdapterKind).Returns(driver);
        var controller = new LineupLinkController(
            settings,
            adapters,
            physicalCatalog ?? Substitute.For<IPhysicalDeviceCatalog>(),
            deviceStatuses ?? Substitute.For<IPhysicalDeviceStatusService>(),
            Substitute.For<IStreamingRouteResolver>(),
            guides ?? Substitute.For<IGuideOrchestrator>(),
            streams ?? Substitute.For<IVirtualMpegTsOutputService>(),
            activeStreams ?? new ActiveStreamRegistry(),
            NullLogger<LineupLinkController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        if (authenticated)
        {
            controller.Request.Headers.Authorization = "Bearer " + grant.GeneratedToken;
        }
        return controller;
    }
}
