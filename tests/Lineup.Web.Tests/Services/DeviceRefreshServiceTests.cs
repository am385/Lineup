using Lineup.Core;
using Lineup.HDHomeRun.Device;
using Lineup.HDHomeRun.Device.Models;
using Lineup.HDHomeRun.Device.Protocol;
using Lineup.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>
/// Verifies automatic HDHomeRun device refresh behavior.
/// </summary>
public class DeviceRefreshServiceTests
{
    /// <summary>
    /// Verifies that a new installation does not contact the placeholder device address.
    /// </summary>
    [Fact]
    public async Task DiscoverDeviceIfNeededAsync_SetupIncomplete_DoesNotDiscover()
    {
        // Arrange
        var fixture = CreateFixture(isSetupComplete: false);

        // Act
        await fixture.Service.DiscoverDeviceIfNeededAsync(TestContext.Current.CancellationToken);

        // Assert
        await fixture.Client.DidNotReceive().DiscoverDeviceAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Verifies that automatic discovery becomes available after first-run setup.
    /// </summary>
    [Fact]
    public async Task DiscoverDeviceIfNeededAsync_SetupComplete_DiscoversConfiguredDevice()
    {
        // Arrange
        var fixture = CreateFixture(isSetupComplete: true);

        // Act
        await fixture.Service.DiscoverDeviceIfNeededAsync(TestContext.Current.CancellationToken);

        // Assert
        await fixture.Client.Received(1).DiscoverDeviceAsync(Arg.Any<CancellationToken>());
        Assert.True(fixture.State.IsDiscovered);
    }

    /// <summary>
    /// Verifies that a failed discovery honors the state service's retry schedule.
    /// </summary>
    [Fact]
    public async Task DiscoverDeviceIfNeededAsync_DiscoveryFails_DoesNotRetryBeforeScheduledTime()
    {
        // Arrange
        var fixture = CreateFixture(isSetupComplete: true, discoveryFails: true);

        // Act
        await fixture.Service.DiscoverDeviceIfNeededAsync(TestContext.Current.CancellationToken);
        await fixture.Service.DiscoverDeviceIfNeededAsync(TestContext.Current.CancellationToken);

        // Assert
        await fixture.Client.Received(1).DiscoverDeviceAsync(Arg.Any<CancellationToken>());
        Assert.True(fixture.State.NextDeviceRefresh > DateTime.UtcNow);
    }

    /// <summary>
    /// Verifies startup refreshes the persisted channel lineup after connecting to a device.
    /// </summary>
    [Fact]
    public async Task InitializeDeviceAsync_DiscoveredDevice_RefreshesChannels()
    {
        // Arrange
        var fixture = CreateFixture(isSetupComplete: true);

        // Act
        await fixture.Service.InitializeDeviceAsync(TestContext.Current.CancellationToken);

        // Assert
        await fixture.ChannelProvider.Received(1).FetchChannelLineupAsync(Arg.Any<CancellationToken>());
        var snapshot = await fixture.ChannelStore.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("7.1", Assert.Single(snapshot!.Channels).GuideNumber);
    }

    /// <summary>
    /// Verifies startup does not query channels when device discovery did not connect.
    /// </summary>
    [Fact]
    public async Task InitializeDeviceAsync_UndiscoveredDevice_DoesNotRefreshChannels()
    {
        // Arrange
        var fixture = CreateFixture(isSetupComplete: false);

        // Act
        await fixture.Service.InitializeDeviceAsync(TestContext.Current.CancellationToken);

        // Assert
        await fixture.ChannelProvider.DidNotReceive().FetchChannelLineupAsync(Arg.Any<CancellationToken>());
        Assert.Null(await fixture.ChannelStore.ReadAsync(TestContext.Current.CancellationToken));
    }

    private static Fixture CreateFixture(bool isSetupComplete, bool discoveryFails = false)
    {
        var settings = Substitute.For<IAppSettingsService>();
        settings.Settings.Returns(new AppSettings
        {
            DeviceAddress = "tuner.local",
            IsSetupComplete = isSetupComplete
        });
        var client = Substitute.For<HDHomeRunDeviceClient>(
            NullLogger<HDHomeRunDeviceClient>.Instance,
            new HttpClient { BaseAddress = new Uri("http://tuner.local/") },
            null);
        if (discoveryFails)
        {
            client.DiscoverDeviceAsync(Arg.Any<CancellationToken>())
                .Returns(Task.FromException<HDHomeRunDeviceInfo>(new HttpRequestException("Unavailable")));
        }
        else
        {
            client.DiscoverDeviceAsync(Arg.Any<CancellationToken>()).Returns(new HDHomeRunDeviceInfo
            {
                FriendlyName = "Tuner",
                ModelNumber = "HDHR",
                FirmwareName = "hdhomerun",
                FirmwareVersion = "1",
                DeviceID = "12345678",
                DeviceAuth = "auth",
                BaseURL = "http://tuner.local",
                LineupURL = "http://tuner.local/lineup.json",
                TunerCount = 2
            });
        }
        var protocolService = Substitute.For<HDHomeRunService>(NullLoggerFactory.Instance, Substitute.For<IHDHomeRunHttpControlFactory>());
        protocolService.GetDeviceByIpAsync("tuner.local", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<HDHomeRunDevice?>(null));
        var state = new DeviceStateService(NullLogger<DeviceStateService>.Instance, client, protocolService, settings);
        var channelProvider = Substitute.For<IChannelLineupProvider>();
        channelProvider.FetchChannelLineupAsync(Arg.Any<CancellationToken>())
            .Returns([new HDHomeRunChannel { GuideNumber = "7.1", GuideName = "Channel", URL = "http://tuner.local/auto/v7.1" }]);
        var channelStore = new ChannelLineupStore(Path.Combine(Path.GetTempPath(), $"lineup-startup-{Guid.NewGuid():N}.db"));
        var channelRefresh = new ChannelLineupRefreshService(NullLogger<ChannelLineupRefreshService>.Instance, channelProvider, channelStore);
        var scopedProvider = Substitute.For<IServiceProvider>();
        scopedProvider.GetService(typeof(ChannelLineupRefreshService)).Returns(channelRefresh);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(scopedProvider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);
        var service = new DeviceRefreshService(NullLogger<DeviceRefreshService>.Instance, state, settings, scopeFactory);
        return new Fixture(service, state, client, channelProvider, channelStore);
    }

    private sealed record Fixture(DeviceRefreshService Service, DeviceStateService State, HDHomeRunDeviceClient Client, IChannelLineupProvider ChannelProvider, ChannelLineupStore ChannelStore);
}
