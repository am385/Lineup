using Bunit;
using Lineup.Core;
using Lineup.HDHomeRun.Device;
using Lineup.HDHomeRun.Device.Models;
using Lineup.Web.Components.Pages;
using Lineup.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.Web.Tests.Components.Pages;

/// <summary>
/// Verifies first-run settings behavior.
/// </summary>
public class SettingsInitialSetupTests
{
    /// <summary>
    /// Verifies first-run setup discovers the saved device and persists its channels before opening Dashboard.
    /// </summary>
    [Fact]
    public async Task Save_InitialSetup_RefreshesChannelsBeforeNavigatingToDashboard()
    {
        // Arrange
        using var context = new BunitContext();
        var settings = new AppSettings { IsSetupComplete = false, DeviceAddress = "tuner.local" };
        var settingsService = Substitute.For<IAppSettingsService>();
        settingsService.Settings.Returns(settings);
        settingsService.ConfiguredXmltvOutputPath.Returns("/xmltv/epg.xml");
        settingsService.UpdateAsync(Arg.Any<Action<AppSettings>>()).Returns(callInfo =>
        {
            callInfo.Arg<Action<AppSettings>>()(settings);
            return Task.CompletedTask;
        });
        var timeZoneService = Substitute.For<ITimeZoneService>();
        timeZoneService.TimeZone.Returns(TimeZoneInfo.Utc);
        var deviceState = Substitute.For<IDeviceStateService>();
        deviceState.DiscoverDeviceAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            deviceState.IsDiscovered.Returns(true);
            return Task.CompletedTask;
        });
        var channelProvider = Substitute.For<IChannelLineupProvider>();
        channelProvider.FetchChannelLineupAsync(Arg.Any<CancellationToken>())
            .Returns([new HDHomeRunChannel { GuideNumber = "7.1", GuideName = "Channel", URL = "http://tuner.local/auto/v7.1" }]);
        var channelStore = new ChannelLineupStore(Path.Combine(Path.GetTempPath(), $"lineup-setup-{Guid.NewGuid():N}.db"));
        var channelRefresh = new ChannelLineupRefreshService(NullLogger<ChannelLineupRefreshService>.Instance, channelProvider, channelStore);
        context.Services.AddSingleton(settingsService);
        context.Services.AddSingleton(timeZoneService);
        context.Services.AddSingleton(deviceState);
        context.Services.AddSingleton(channelRefresh);
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        var component = context.Render<Settings>();

        // Act
        await component.Find("button.btn-primary").ClickAsync(new());

        // Assert
        await deviceState.Received(1).DiscoverDeviceAsync(Arg.Any<CancellationToken>());
        await channelProvider.Received(1).FetchChannelLineupAsync(Arg.Any<CancellationToken>());
        Assert.True(settings.IsSetupComplete);
        Assert.EndsWith("/dashboard", navigation.Uri, StringComparison.Ordinal);
        Assert.Equal(
            "7.1",
            Assert.Single((await channelStore.ReadAsync(Xunit.TestContext.Current.CancellationToken))!.Channels).GuideNumber);
    }
}
