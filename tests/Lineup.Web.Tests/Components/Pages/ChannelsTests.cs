using Bunit;
using Lineup.Core;
using Lineup.Core.Storage;
using Lineup.HDHomeRun.Device;
using Lineup.HDHomeRun.Device.Models;
using Lineup.Web.Components.Pages;
using Lineup.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.Web.Tests.Components.Pages;

/// <summary>
/// Verifies the physical HDHomeRun channel lineup page.
/// </summary>
public class ChannelsTests
{
    /// <summary>
    /// Verifies the saved lineup renders independently of guide data.
    /// </summary>
    [Fact]
    public async Task SavedLineup_DisplaysChannelSummaryAndMetadata()
    {
        // Arrange
        using var context = new BunitContext();
        var root = Directory.CreateTempSubdirectory("lineup-channels-page-");
        try
        {
            var store = new ChannelLineupStore(Path.Combine(root.FullName, "lineup.db"));
            await store.StoreAsync(
            [
                new HDHomeRunChannel
                {
                    GuideNumber = "7.1",
                    GuideName = "Favorite Station",
                    Favorite = true,
                    HD = true,
                    VideoCodec = "MPEG2",
                    AudioCodec = "AC3",
                    SignalStrength = 95,
                    SignalQuality = 90,
                    URL = "http://device/auto/v7.1"
                }
            ], Xunit.TestContext.Current.CancellationToken);
            var settings = Substitute.For<IAppSettingsService>();
            settings.Settings.Returns(new AppSettings { IsSetupComplete = true });
            var timeZone = Substitute.For<ITimeZoneService>();
            timeZone.ConvertFromUtc(Arg.Any<DateTime>()).Returns(callInfo => callInfo.Arg<DateTime>());
            var provider = Substitute.For<IChannelLineupProvider>();
            context.Services.AddSingleton(store);
            context.Services.AddSingleton(settings);
            context.Services.AddSingleton(timeZone);
            context.Services.AddSingleton(provider);
            context.Services.AddSingleton(new ChannelLineupRefreshService(
                NullLogger<ChannelLineupRefreshService>.Instance,
                provider,
                store));
            context.Services.AddSingleton(new EpgOrchestrator(
                NullLogger<EpgOrchestrator>.Instance,
                store,
                null!,
                null!,
                new LineupXmltvWriter(),
                Substitute.For<IXmltvPublicationStore>()));
            context.Services.AddSingleton<IXmltvPublicationStore, XmltvPublicationStore>();

            // Act
            var component = context.Render<Channels>();

            // Assert
            component.WaitForAssertion(() =>
            {
                Assert.Contains("Favorite Station", component.Markup);
                Assert.Contains("MPEG2", component.Markup);
                Assert.Contains("AC3", component.Markup);
                Assert.Contains("95%", component.Markup);
                Assert.Contains("90%", component.Markup);
                Assert.Single(component.FindAll("[aria-label='Favorite channel']"));
            });

            // Act
            component.Find("[aria-label='Disable channel 7.1']").Change(false);
            component.WaitForAssertion(() => Assert.NotNull(component.Find("[aria-label='Enable channel 7.1']")));
            var updated = await store.ReadAsync(Xunit.TestContext.Current.CancellationToken);

            // Assert
            Assert.False(updated!.IsChannelEnabled("7.1"));
            Assert.DoesNotContain("Channel 7.1 is now disabled.", component.Markup);
            Assert.Contains("channel-row-disabled", component.Markup);
            Assert.DoesNotContain("table-secondary", component.Markup);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
