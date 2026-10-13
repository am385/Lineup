using Bunit;
using Lineup.Web.Components.Pages;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Lineup.Web.Tests.Components.Pages;

/// <summary>
/// Verifies EPG fetch settings behavior on the Settings page.
/// </summary>
public class EpgFetchSettingsTests
{
    /// <summary>
    /// Verifies the channel refresh setting defaults to enabled and persists edits.
    /// </summary>
    [Fact]
    public void Save_RefreshChannelsBeforeGuideFetch_PersistsValue()
    {
        // Arrange
        using var context = new BunitContext();
        var settings = new LineupSettings { IsSetupComplete = true };
        var settingsService = Substitute.For<ILineupSettingsService>();
        settingsService.Settings.Returns(settings);
        settingsService.UpdateAsync(Arg.Any<Action<LineupSettings>>()).Returns(callInfo =>
        {
            callInfo.Arg<Action<LineupSettings>>()(settings);
            return Task.CompletedTask;
        });
        var timeZoneService = Substitute.For<ITimeZoneService>();
        timeZoneService.TimeZone.Returns(TimeZoneInfo.Utc);
        context.Services.AddSingleton(settingsService);
        context.Services.AddSingleton(timeZoneService);
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var component = context.Render<Settings>();
        component.Find("#tab-guide").Click();
        var enabledByDefault = component.Find("#refreshChannelsBeforeGuideFetch").HasAttribute("checked");
        var historyRetention = component.Find("#epgHistoryRetentionHours");
        var historyDefaultsTo24Hours = historyRetention.GetAttribute("value") == "24";
        var guideCheckboxesUseSwitches = new[] { "autoFetchEnabled", "refreshChannelsBeforeGuideFetch" }
            .All(id => component.Find($"#{id}").ParentElement!.ClassList.Contains("form-switch"));

        // Act
        component.Find("#refreshChannelsBeforeGuideFetch").Change(false);
        historyRetention.Change("48");
        component.Find("button.btn-primary").Click();

        // Assert
        Assert.True(enabledByDefault);
        Assert.True(historyDefaultsTo24Hours);
        Assert.True(guideCheckboxesUseSwitches);
        Assert.False(settings.RefreshChannelsBeforeGuideFetch);
        Assert.Equal(48, settings.EpgHistoryRetentionHours);
        settingsService.Received(1).UpdateAsync(Arg.Any<Action<LineupSettings>>());
    }
}
