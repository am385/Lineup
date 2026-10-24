using System.Text.Json;
using Lineup.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Xunit;

namespace Lineup.Web.Tests.Controllers;

/// <summary>Verifies management-hosted virtual HDHomeRun endpoint selection.</summary>
public sealed class HdHomeRunProxyControllerTests
{
    /// <summary>Verifies a management-hosted profile is exposed on the management listener.</summary>
    [Fact]
    public async Task Discover_ManagementHostedProfile_ReturnsDiscoveryDocument()
    {
        // Arrange
        var controller = CreateController(hosting: null);

        // Act
        var result = await controller.Discover();

        // Assert
        Assert.IsType<OkObjectResult>(result);
    }

    /// <summary>Verifies a dedicated profile is not duplicated on the management listener.</summary>
    [Fact]
    public async Task Discover_DedicatedProfile_ReturnsUnavailable()
    {
        // Arrange
        var controller = CreateController(new VirtualOutputHostingDefinition());

        // Act
        var result = await controller.Discover();

        // Assert
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    /// <summary>Verifies normalized public request metadata is used in advertised HDHomeRun URLs.</summary>
    [Fact]
    public async Task Discover_PublicHttpsRequest_AdvertisesPublicRoot()
    {
        // Arrange
        var controller = CreateController(hosting: null);
        controller.Request.Scheme = Uri.UriSchemeHttps;
        controller.Request.Host = new HostString("lineup.example.com");

        // Act
        var result = await controller.Discover();

        // Assert
        var response = Assert.IsType<HdHomeRunProxyDiscoverResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal("https://lineup.example.com", response.BaseUrl);
        Assert.Equal("https://lineup.example.com/lineup.json", response.LineupUrl);
    }

    private static HdHomeRunProxyController CreateController(VirtualOutputHostingDefinition? hosting)
    {
        var device = new VirtualDeviceDefinition
        {
            Id = VirtualDeviceId.New(),
            Name = "Virtual HDHomeRun",
            Hosting = hosting,
            Adapter = new AdapterConfiguration(
                HdHomeRunVirtualTunerDriver.AdapterKind,
                JsonSerializer.SerializeToElement(new HdHomeRunVirtualTunerConfiguration()))
        };
        var profile = new HdHomeRunProxyProfileSnapshot
        {
            VirtualDevice = device,
            IsPrimary = true,
            DeviceId = 0x12345678,
            DeviceAuth = "device-auth",
            TunerCount = 4,
            FriendlyName = device.Name
        };
        var profiles = Substitute.For<IHdHomeRunProxyProfileProvider>();
        profiles.GetProfilesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<HdHomeRunProxyProfileSnapshot>>([profile]));
        var controller = new HdHomeRunProxyController(
            profiles,
            Substitute.For<ILineupSettingsService>(),
            null!)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.Request.Scheme = Uri.UriSchemeHttp;
        controller.Request.Host = new HostString("localhost", 8080);
        return controller;
    }
}
