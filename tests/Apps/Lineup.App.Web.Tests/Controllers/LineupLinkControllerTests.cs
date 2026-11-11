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

    private static LineupLinkController CreateController(
        VirtualOutputHostingDefinition? hosting,
        bool requireEncryption = false,
        IActiveStreamRegistry? activeStreams = null,
        IVirtualMpegTsOutputService? streams = null,
        int maximum = 0,
        bool authenticated = false
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
                VirtualDevices =
                [
                    new VirtualDeviceDefinition
                    {
                        Id = id,
                        Name = "Lineup Link",
                        Hosting = hosting,
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
            Substitute.For<IPhysicalDeviceCatalog>(),
            Substitute.For<IStreamingRouteResolver>(),
            Substitute.For<IGuideOrchestrator>(),
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
