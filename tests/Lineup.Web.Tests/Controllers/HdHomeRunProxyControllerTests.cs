using Lineup.HDHomeRun.Device.Models;
using Lineup.Web.Controllers;
using Lineup.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Xunit;

namespace Lineup.Web.Tests.Controllers;

/// <summary>
/// Verifies virtual HDHomeRun channel-list endpoints.
/// </summary>
public class HdHomeRunProxyControllerTests
{
    /// <summary>
    /// Verifies a scoped XML lineup uses the scoped virtual-device stream URL.
    /// </summary>
    [Fact]
    public async Task LineupXml_DeviceScopedProfile_ReturnsTransformedXml()
    {
        // Arrange
        var profile = CreateProfile(isPrimary: false);
        var controller = CreateController(profile, [CreateChannel()], out _);

        // Act
        var result = Assert.IsType<ContentResult>(await controller.LineupXml(profile.DeviceIdText));

        // Assert
        Assert.Equal("text/xml; charset=utf-8", result.ContentType);
        Assert.Contains($"<URL>http://lineup.local/hdhomerun/{profile.DeviceIdText}/auto/v7.1</URL>", result.Content);
        Assert.Contains("<AudioCodec>AC3</AudioCodec>", result.Content);
    }

    /// <summary>
    /// Verifies the primary M3U lineup uses legacy root stream URLs.
    /// </summary>
    [Fact]
    public async Task LineupM3u_PrimaryProfile_ReturnsNativePlaylist()
    {
        // Arrange
        var profile = CreateProfile(isPrimary: true);
        var controller = CreateController(profile, [CreateChannel()], out _);

        // Act
        var result = Assert.IsType<ContentResult>(await controller.LineupM3u());

        // Assert
        Assert.Equal("text/plain; charset=utf-8", result.ContentType);
        Assert.Contains("channel-id=\"7.1\" channel-number=\"7.1\" tvg-name=\"TEST\" group-title=\"Favorites\"", result.Content);
        Assert.EndsWith("http://lineup.local/auto/v7.1\n", result.Content);
    }

    /// <summary>
    /// Verifies a missing scoped profile retains the existing not-found behavior.
    /// </summary>
    [Fact]
    public async Task LineupM3u_MissingScopedProfile_ReturnsNotFound()
    {
        // Arrange
        var profiles = Substitute.For<IHdHomeRunProxyProfileProvider>();
        profiles.FindProfileAsync("DEADBEEF", Arg.Any<CancellationToken>()).Returns((HdHomeRunProxyProfileSnapshot?)null);
        var controller = CreateController(profiles);

        // Act
        var result = Assert.IsType<NotFoundObjectResult>(await controller.LineupM3u("DEADBEEF"));

        // Assert
        Assert.Equal(StatusCodes.Status404NotFound, result.StatusCode);
    }

    /// <summary>
    /// Verifies a physical lineup failure retains the existing service-unavailable behavior.
    /// </summary>
    [Fact]
    public async Task LineupXml_PhysicalLineupUnavailable_ReturnsServiceUnavailable()
    {
        // Arrange
        var profile = CreateProfile(isPrimary: true);
        var profiles = Substitute.For<IHdHomeRunProxyProfileProvider>();
        profiles.GetPrimaryProfileAsync(Arg.Any<CancellationToken>()).Returns(profile);
        var deviceClient = Substitute.For<IHdHomeRunProxyDeviceClient>();
        deviceClient.FetchLineupAsync(profile, Arg.Any<CancellationToken>()).Returns<Task<List<HDHomeRunChannel>>>(_ => throw new InvalidOperationException());
        var controller = CreateController(profiles, deviceClient);

        // Act
        var result = Assert.IsType<ObjectResult>(await controller.LineupXml());

        // Assert
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
    }

    private static HdHomeRunProxyController CreateController(HdHomeRunProxyProfileSnapshot profile, List<HDHomeRunChannel> channels, out IHdHomeRunProxyDeviceClient deviceClient)
    {
        var profiles = Substitute.For<IHdHomeRunProxyProfileProvider>();
        if (profile.IsPrimary)
        {
            profiles.GetPrimaryProfileAsync(Arg.Any<CancellationToken>()).Returns(profile);
        }
        else
        {
            profiles.FindProfileAsync(profile.DeviceIdText, Arg.Any<CancellationToken>()).Returns(profile);
        }

        deviceClient = Substitute.For<IHdHomeRunProxyDeviceClient>();
        deviceClient.FetchLineupAsync(profile, Arg.Any<CancellationToken>()).Returns(channels);
        return CreateController(profiles, deviceClient);
    }

    private static HdHomeRunProxyController CreateController(IHdHomeRunProxyProfileProvider profiles, IHdHomeRunProxyDeviceClient? deviceClient = null)
    {
        var settings = Substitute.For<IAppSettingsService>();
        settings.Settings.Returns(new AppSettings
        {
            VirtualTunerVideoMode = VirtualTunerVideoMode.ConvertHevcToH264
        });
        return new HdHomeRunProxyController(profiles, deviceClient ?? Substitute.For<IHdHomeRunProxyDeviceClient>(), settings)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    Request =
                    {
                        Scheme = "http",
                        Host = new HostString("lineup.local")
                    }
                }
            }
        };
    }

    private static HdHomeRunProxyProfileSnapshot CreateProfile(bool isPrimary) => new()
    {
        Settings = new HdHomeRunProxyProfileSettings(),
        PhysicalDevice = new HDHomeRunDeviceInfo
        {
            FriendlyName = "Physical",
            ModelNumber = "HDHR",
            FirmwareName = "hdhomerun",
            FirmwareVersion = "1",
            DeviceID = "12345678",
            DeviceAuth = "physical",
            BaseURL = "http://device",
            LineupURL = "http://device/lineup.json",
            TunerCount = 4
        },
        IsPrimary = isPrimary,
        DeviceId = 0x12345678,
        DeviceAuth = "virtual",
        TunerCount = 4,
        FriendlyName = "Lineup",
        PhysicalBaseUri = new Uri("http://device/")
    };

    private static HDHomeRunChannel CreateChannel() => new()
    {
        GuideNumber = "7.1",
        GuideName = "TEST",
        VideoCodec = "HEVC",
        AudioCodec = "AC4",
        Favorite = true,
        URL = "http://device/auto/v7.1"
    };
}
