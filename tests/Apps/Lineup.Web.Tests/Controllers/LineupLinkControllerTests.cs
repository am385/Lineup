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

    private static LineupLinkController CreateController(VirtualOutputHostingDefinition? hosting, bool requireEncryption = false)
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
                                RequireEncryption = requireEncryption,
                                PublicBaseUrl = requireEncryption ? "https://localhost:8443/" : "http://localhost:8080/"
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
