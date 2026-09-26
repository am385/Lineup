using Bunit;
using Lineup.Core;
using Lineup.Core.Storage;
using Lineup.HDHomeRun.Api.Models;
using Lineup.Web.Components.Pages;
using Lineup.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Lineup.Web.Tests.Components.Pages;

/// <summary>
/// Verifies Guide channel metadata markers.
/// </summary>
public class GuideTests
{
    /// <summary>
    /// Verifies that cached DRM channels display an accessible protection marker.
    /// </summary>
    [Fact]
    public void DrmChannel_DisplaysProtectionMarker()
    {
        // Arrange
        using var context = new BunitContext();
        var repository = Substitute.For<IEpgRepository>();
        repository.GetChannelsAsync().Returns(
        [
            new HDHomeRunChannelEpgSegment { GuideNumber = "117.1", GuideName = "Protected", DRM = true }
        ]);
        repository.GetProgramsAsync(Arg.Any<DateTime?>(), Arg.Any<DateTime?>()).Returns([]);
        var timeZoneService = Substitute.For<ITimeZoneService>();
        timeZoneService.Today.Returns(DateTime.Today);
        timeZoneService.Now.Returns(DateTime.Now);
        context.Services.AddSingleton(repository);
        context.Services.AddSingleton(timeZoneService);
        context.Services.AddSingleton(new ChannelLineupStore(Path.Combine(Path.GetTempPath(), $"lineup-guide-{Guid.NewGuid():N}.db")));
        context.JSInterop.Mode = JSRuntimeMode.Loose;

        // Act
        var component = context.Render<Guide>();

        // Assert
        component.WaitForAssertion(() =>
        {
            var marker = component.Find("[aria-label='DRM-protected channel']");
            Assert.Contains("bi-shield-lock-fill", marker.ClassList);
        });
    }

    /// <summary>
    /// Verifies that cached favorite channels display an accessible favorite marker.
    /// </summary>
    [Fact]
    public void FavoriteChannel_DisplaysFavoriteMarker()
    {
        // Arrange
        using var context = new BunitContext();
        var repository = Substitute.For<IEpgRepository>();
        repository.GetChannelsAsync().Returns([new HDHomeRunChannelEpgSegment { GuideNumber = "7.1", GuideName = "Favorite", Favorite = true }]);
        repository.GetProgramsAsync(Arg.Any<DateTime?>(), Arg.Any<DateTime?>()).Returns([]);
        var timeZoneService = Substitute.For<ITimeZoneService>();
        timeZoneService.Today.Returns(DateTime.Today);
        timeZoneService.Now.Returns(DateTime.Now);
        context.Services.AddSingleton(repository);
        context.Services.AddSingleton(timeZoneService);
        context.Services.AddSingleton(new ChannelLineupStore(Path.Combine(Path.GetTempPath(), $"lineup-guide-{Guid.NewGuid():N}.db")));
        context.JSInterop.Mode = JSRuntimeMode.Loose;

        // Act
        var component = context.Render<Guide>();

        // Assert
        component.WaitForAssertion(() =>
        {
            var marker = component.Find("[aria-label='Favorite channel']");
            Assert.Contains("bi-star-fill", marker.ClassList);
        });
    }

    /// <summary>
    /// Verifies disabled channels remain visible with an accessible state marker.
    /// </summary>
    [Fact]
    public async Task DisabledChannel_RemainsVisibleWithDisabledMarker()
    {
        // Arrange
        using var context = new BunitContext();
        var repository = Substitute.For<IEpgRepository>();
        repository.GetChannelsAsync().Returns([new HDHomeRunChannelEpgSegment { GuideNumber = "9.1", GuideName = "Disabled" }]);
        repository.GetProgramsAsync(Arg.Any<DateTime?>(), Arg.Any<DateTime?>()).Returns([]);
        var timeZoneService = Substitute.For<ITimeZoneService>();
        timeZoneService.Today.Returns(DateTime.Today);
        timeZoneService.Now.Returns(DateTime.Now);
        var store = new ChannelLineupStore(Path.Combine(Path.GetTempPath(), $"lineup-guide-{Guid.NewGuid():N}.db"));
        await store.StoreAsync(
            [new Lineup.HDHomeRun.Device.Models.HDHomeRunChannel { GuideNumber = "9.1", GuideName = "Disabled", URL = "http://device/auto/v9.1" }],
            Xunit.TestContext.Current.CancellationToken);
        await store.SetChannelEnabledAsync("9.1", enabled: false, Xunit.TestContext.Current.CancellationToken);
        context.Services.AddSingleton(repository);
        context.Services.AddSingleton(timeZoneService);
        context.Services.AddSingleton(store);
        context.JSInterop.Mode = JSRuntimeMode.Loose;

        // Act
        var component = context.Render<Guide>();

        // Assert
        component.WaitForAssertion(() =>
        {
            Assert.Contains("Disabled", component.Markup);
            Assert.NotNull(component.Find("[aria-label='Disabled channel']"));
        });
    }

    /// <summary>
    /// Verifies selecting a programme displays its structured XMLTV metadata in the details modal.
    /// </summary>
    [Fact]
    public void ProgrammeWithMetadata_Selected_DisplaysMetadataDetails()
    {
        // Arrange
        using var context = new BunitContext();
        var repository = Substitute.For<IEpgRepository>();
        repository.GetChannelsAsync().Returns([new HDHomeRunChannelEpgSegment { GuideNumber = "7.1", GuideName = "Metadata" }]);
        var start = new DateTimeOffset(2026, 9, 14, 10, 0, 0, TimeSpan.Zero);
        repository.GetProgramsAsync(Arg.Any<DateTime?>(), Arg.Any<DateTime?>()).Returns(
        [
            new HDHomeRunProgram
            {
                GuideNumber = "7.1",
                Title = "Metadata Show",
                StartTime = start.ToUnixTimeSeconds(),
                EndTime = start.AddMinutes(30).ToUnixTimeSeconds(),
                Metadata = new XmltvProgrammeMetadata
                {
                    Ratings = [new XmltvRating("TV-14", "MPAA", [])]
                }
            }
        ]);
        var timeZoneService = Substitute.For<ITimeZoneService>();
        timeZoneService.Today.Returns(start.Date);
        timeZoneService.Now.Returns(start.DateTime);
        context.Services.AddSingleton(repository);
        context.Services.AddSingleton(timeZoneService);
        context.Services.AddSingleton(new ChannelLineupStore(Path.Combine(Path.GetTempPath(), $"lineup-guide-{Guid.NewGuid():N}.db")));
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var component = context.Render<Guide>();

        // Act
        component.WaitForElement(".guide-program").Click();

        // Assert
        component.WaitForAssertion(() =>
        {
            Assert.Contains("Metadata Show", component.Find(".modal-title").TextContent);
            Assert.Contains("MPAA: TV-14", component.Find(".programme-metadata").TextContent);
        });
    }
}
