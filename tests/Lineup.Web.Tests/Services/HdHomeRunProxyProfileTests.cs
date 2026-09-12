using System.Net;
using System.Text;
using System.Text.Json;
using Lineup.HDHomeRun.Device.Models;
using Lineup.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>
/// Verifies proxy-profile migration, isolation, route generation, and discovery snapshots.
/// </summary>
public class HdHomeRunProxyProfileTests
{
    /// <summary>
    /// Verifies that legacy single-device settings synthesize an equivalent primary profile.
    /// </summary>
    [Fact]
    public void EnsureHdHomeRunProxyProfiles_LegacySettings_SynthesizesPrimaryProfile()
    {
        // Arrange
        var settings = new AppSettings
        {
            DeviceAddress = "192.0.2.10",
            AdvertisedBaseUrl = "http://lineup.local:8080"
        };

        settings.EnsureHdHomeRunProxyProfiles();
        var originalDeviceId = settings.HdHomeRunProxyProfiles[0].VirtualDeviceId;
        // Act
        settings.EnsureHdHomeRunProxyProfiles();

        // Assert
        var profile = Assert.Single(settings.HdHomeRunProxyProfiles);
        Assert.Equal("192.0.2.10", profile.PhysicalAddress);
        Assert.Equal("http://lineup.local:8080", profile.AdvertisedBaseUrl);
        Assert.True(profile.Enabled);
        Assert.True(profile.UsesLegacyDeviceAddress);
        Assert.True(HdHomeRunProxyIdentity.IsValidDeviceId(profile.VirtualDeviceId));
        Assert.Equal(originalDeviceId, profile.VirtualDeviceId);
    }

    /// <summary>
    /// Verifies that legacy primary-profile state migrates to the global proxy switch.
    /// </summary>
    [Fact]
    public void EnsureHdHomeRunProxyProfiles_LegacyDisabledPrimary_DisablesProxyGlobally()
    {
        // Arrange
        var settings = new AppSettings
        {
            HdHomeRunProxyProfiles = [new HdHomeRunProxyProfileSettings { Enabled = false, PhysicalAddress = "tuner.local" }]
        };

        // Act
        settings.EnsureHdHomeRunProxyProfiles();

        // Assert
        Assert.False(settings.EnableHdHomeRunProxy);
        Assert.True(settings.HdHomeRunProxyProfiles[0].Enabled);
    }

    /// <summary>
    /// Verifies that configured profiles remain distinct and never expose physical authorization values.
    /// </summary>
    [Fact]
    public void CreateSnapshot_DifferentPhysicalProfiles_ProducesIsolatedVirtualIdentities()
    {
        // Arrange
        var firstSettings = new HdHomeRunProxyProfileSettings { PhysicalAddress = "192.0.2.10" };
        var secondSettings = new HdHomeRunProxyProfileSettings { PhysicalAddress = "192.0.2.11" };
        var firstDevice = CreatePhysicalDevice("First", "physical-auth-one", 4);
        var secondDevice = CreatePhysicalDevice("Second", "physical-auth-two", 2);

        // Act
        var first = HdHomeRunProxyProfileResolver.CreateSnapshot(firstSettings, firstDevice, true);
        var second = HdHomeRunProxyProfileResolver.CreateSnapshot(secondSettings, secondDevice, false);

        // Assert
        Assert.NotEqual(first.DeviceId, second.DeviceId);
        Assert.NotEqual(first.DeviceAuth, second.DeviceAuth);
        Assert.NotEqual(firstDevice.DeviceAuth, first.DeviceAuth);
        Assert.NotEqual(secondDevice.DeviceAuth, second.DeviceAuth);
        Assert.Empty(first.PhysicalDevice.DeviceAuth);
        Assert.Empty(second.PhysicalDevice.DeviceAuth);
    }

    /// <summary>
    /// Verifies that additional profiles use stable device-scoped HTTP and stream routes.
    /// </summary>
    [Fact]
    public void GetHttpBaseUri_SecondaryProfile_RewritesDeviceAndStreamRoutes()
    {
        // Arrange
        var settings = new HdHomeRunProxyProfileSettings
        {
            PhysicalAddress = "192.0.2.11",
            AdvertisedBaseUrl = "https://lineup.example/proxy/"
        };
        var snapshot = HdHomeRunProxyProfileResolver.CreateSnapshot(settings, CreatePhysicalDevice("Second", "auth", 2), false);
        var channel = new HDHomeRunChannel
        {
            GuideNumber = "5.1",
            GuideName = "Five",
            URL = "http://192.0.2.11/auto/v5.1"
        };

        var baseUri = snapshot.GetHttpBaseUri(new Uri("http://unsafe-request-host/"));
        // Act
        var proxyChannel = HdHomeRunProxyChannel.Create(channel, baseUri);

        // Assert
        Assert.Equal($"https://lineup.example/proxy/hdhomerun/{snapshot.DeviceIdText}/", baseUri.AbsoluteUri);
        Assert.Equal($"https://lineup.example/proxy/hdhomerun/{snapshot.DeviceIdText}/auto/v5.1", proxyChannel.Url);
    }

    /// <summary>
    /// Verifies that discovery includes only explicitly advertised profiles and preserves profile paths and tuner caps.
    /// </summary>
    [Fact]
    public void CreateAdvertisements_MultipleProfiles_UsesProfileConsistentSnapshots()
    {
        // Arrange
        var primarySettings = new HdHomeRunProxyProfileSettings
        {
            PhysicalAddress = "192.0.2.10",
            AdvertisedBaseUrl = "http://lineup.local:8080",
            TunerCountCap = 2
        };
        var secondarySettings = new HdHomeRunProxyProfileSettings
        {
            PhysicalAddress = "192.0.2.11",
            AdvertisedBaseUrl = "http://lineup.local:8080"
        };
        var hiddenSettings = new HdHomeRunProxyProfileSettings { PhysicalAddress = "192.0.2.12" };
        var profiles = new[]
        {
            HdHomeRunProxyProfileResolver.CreateSnapshot(primarySettings, CreatePhysicalDevice("First", "auth-1", 4), true),
            HdHomeRunProxyProfileResolver.CreateSnapshot(secondarySettings, CreatePhysicalDevice("Second", "auth-2", 2), false),
            HdHomeRunProxyProfileResolver.CreateSnapshot(hiddenSettings, CreatePhysicalDevice("Hidden", "auth-3", 1), false)
        };

        // Act
        var advertisements = HdHomeRunAdvertisedDeviceFactory.Create(profiles);

        // Assert
        Assert.Equal(2, advertisements.Count);
        Assert.Equal("http://lineup.local:8080/", advertisements[0].BaseUri.AbsoluteUri);
        Assert.Equal(2, advertisements[0].TunerCount);
        Assert.Equal($"http://lineup.local:8080/hdhomerun/{profiles[1].DeviceIdText}/", advertisements[1].BaseUri.AbsoluteUri);
        Assert.DoesNotContain(advertisements, advertisement => advertisement.Profile == profiles[2]);
    }

    /// <summary>
    /// Verifies that provider selection resolves the requested enabled physical device without crossing profile state.
    /// </summary>
    [Fact]
    public async Task FindProfileAsync_MultipleProfiles_SelectsRequestedEnabledProfile()
    {
        // Arrange
        var appSettings = new AppSettings
        {
            HdHomeRunProxyProfiles =
            [
                new HdHomeRunProxyProfileSettings { PhysicalAddress = "first.local" },
                new HdHomeRunProxyProfileSettings { PhysicalAddress = "second.local" },
                new HdHomeRunProxyProfileSettings { PhysicalAddress = "disabled.local", Enabled = false }
            ]
        };
        appSettings.EnsureHdHomeRunProxyProfiles();
        var settingsService = Substitute.For<IAppSettingsService>();
        settingsService.Settings.Returns(appSettings);
        var deviceState = Substitute.For<IDeviceStateService>();
        deviceState.DeviceInfo.Returns((HDHomeRunDeviceInfo?)null);
        var handler = new ProfileHttpHandler();
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("HdHomeRunProxyDevice").Returns(new HttpClient(handler));
        var provider = new HdHomeRunProxyProfileProvider(settingsService, deviceState, httpClientFactory, NullLogger<HdHomeRunProxyProfileProvider>.Instance);
        var expectedId = appSettings.HdHomeRunProxyProfiles[1].VirtualDeviceId;

        var selected = await provider.FindProfileAsync(expectedId, TestContext.Current.CancellationToken);
        var selectedRequests = handler.RequestedHosts.ToArray();
        // Act
        var allProfiles = await provider.GetProfilesAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(selected);
        Assert.Equal("http://second.local/", selected.PhysicalBaseUri.AbsoluteUri);
        Assert.Equal("Second", selected.PhysicalDevice.FriendlyName);
        Assert.Equal(["second.local"], selectedRequests);
        Assert.Equal(2, allProfiles.Count);
        Assert.DoesNotContain(allProfiles, profile => profile.Settings.PhysicalAddress == "disabled.local");
    }

    /// <summary>
    /// Verifies normal profile resolution populates the passive status cache.
    /// </summary>
    [Fact]
    public async Task GetProfilesAsync_ResolvedProfile_UpdatesStatusCache()
    {
        // Arrange
        var appSettings = new AppSettings
        {
            EnableHdHomeRunProxy = true,
            HdHomeRunProxyProfiles = [new HdHomeRunProxyProfileSettings { PhysicalAddress = "first.local" }]
        };
        appSettings.EnsureHdHomeRunProxyProfiles();
        var settingsService = Substitute.For<IAppSettingsService>();
        settingsService.Settings.Returns(appSettings);
        var deviceState = Substitute.For<IDeviceStateService>();
        var handler = new ProfileHttpHandler();
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("HdHomeRunProxyDevice").Returns(new HttpClient(handler));
        var cache = new VirtualDeviceStatusCache();
        var provider = new HdHomeRunProxyProfileProvider(
            settingsService,
            deviceState,
            httpClientFactory,
            NullLogger<HdHomeRunProxyProfileProvider>.Instance,
            cache);

        // Act
        await provider.GetProfilesAsync(TestContext.Current.CancellationToken);

        // Assert
        var profile = appSettings.HdHomeRunProxyProfiles[0];
        var cached = cache.Get(profile.VirtualDeviceId, profile.PhysicalAddress);
        Assert.NotNull(cached?.LastSuccessfulSnapshot);
        Assert.Equal("Lineup (First)", cached.LastSuccessfulSnapshot.FriendlyName);
        Assert.Null(cached.LastError);
    }

    /// <summary>
    /// Verifies that the global proxy switch suppresses every configured profile.
    /// </summary>
    [Fact]
    public async Task GetProfilesAsync_GlobalProxyDisabled_ReturnsNoProfiles()
    {
        // Arrange
        var appSettings = new AppSettings
        {
            EnableHdHomeRunProxy = false,
            HdHomeRunProxyProfiles = [new HdHomeRunProxyProfileSettings { PhysicalAddress = "first.local" }]
        };
        var settingsService = Substitute.For<IAppSettingsService>();
        settingsService.Settings.Returns(appSettings);
        var deviceState = Substitute.For<IDeviceStateService>();
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var provider = new HdHomeRunProxyProfileProvider(settingsService, deviceState, httpClientFactory, NullLogger<HdHomeRunProxyProfileProvider>.Instance);

        // Act
        var profiles = await provider.GetProfilesAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(profiles);
        httpClientFactory.DidNotReceive().CreateClient(Arg.Any<string>());
    }

    private static HDHomeRunDeviceInfo CreatePhysicalDevice(string friendlyName, string deviceAuth, int tunerCount)
    {
        return new HDHomeRunDeviceInfo
        {
            FriendlyName = friendlyName,
            ModelNumber = "HDHR",
            FirmwareName = "hdhomerun",
            FirmwareVersion = "1",
            DeviceID = "12345678",
            DeviceAuth = deviceAuth,
            BaseURL = "http://physical.local",
            LineupURL = "http://physical.local/lineup.json",
            TunerCount = tunerCount
        };
    }

    private sealed class ProfileHttpHandler : HttpMessageHandler
    {
        /// <summary>
        /// Gets requested hosts.
        /// </summary>
        public List<string> RequestedHosts { get; } = [];

        /// <summary>
        /// Performs the send operation.
        /// </summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri!.Host;
            RequestedHosts.Add(host);
            var friendlyName = host.StartsWith("first", StringComparison.OrdinalIgnoreCase) ? "First" : "Second";
            var json = JsonSerializer.Serialize(CreatePhysicalDevice(friendlyName, $"physical-{friendlyName}", 4));
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }
}
