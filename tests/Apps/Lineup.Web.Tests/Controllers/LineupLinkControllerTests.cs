using System.Text.Json;
using Lineup.LineupLink.Protocol;
using Lineup.LineupLink.Virtual;
using Lineup.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.Web.Tests.Controllers;

/// <summary>Verifies management-hosted Lineup Link endpoint selection.</summary>
public sealed class LineupLinkControllerTests
{
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

    private static LineupLinkController CreateController(VirtualOutputHostingDefinition? hosting)
    {
        var id = new VirtualDeviceId(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        var driver = new LineupLinkVirtualTunerDriver();
        var settings = Substitute.For<ILineupSettingsService>();
        settings.Settings.Returns(new LineupSettings
        {
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
                            JsonSerializer.SerializeToElement(new LineupLinkVirtualConfiguration
                            {
                                InstanceId = Guid.NewGuid(),
                                RequireEncryption = false,
                                PublicBaseUrl = "http://localhost:8080/"
                            }))
                    }
                ]
            }
        });
        var adapters = Substitute.For<IStreamingAdapterRegistry>();
        adapters.GetVirtual(LineupLinkProtocol.AdapterKind).Returns(driver);
        return new LineupLinkController(
            settings,
            adapters,
            Substitute.For<IPhysicalDeviceCatalog>(),
            Substitute.For<IStreamingRouteResolver>(),
            Substitute.For<IGuideOrchestrator>(),
            Substitute.For<IVirtualMpegTsOutputService>(),
            new ActiveStreamRegistry(),
            NullLogger<LineupLinkController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }
}
