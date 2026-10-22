using Bunit;
using Lineup.Web.Components.Pages;
using Lineup.Web.Services;
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
    /// Verifies the channel refresh setting defaults to enabled and reports success only after delayed persistence completes.
    /// </summary>
    [Fact]
    public async Task Save_RefreshChannelsBeforeGuideFetch_PersistsValue()
    {
        // Arrange
        using var context = new BunitContext();
        var settings = new LineupSettings { IsSetupComplete = true };
        var settingsService = Substitute.For<ILineupSettingsService>();
        settingsService.Settings.Returns(settings);
        var persistence = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        settingsService.UpdateAsync(Arg.Any<Action<LineupSettings>>()).Returns(async callInfo =>
        {
            var update = callInfo.Arg<Action<LineupSettings>>();
            await persistence.Task;
            update(settings);
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
        var saveClick = component.Find("button.btn-primary").ClickAsync(new());

        // Assert
        try
        {
            component.WaitForAssertion(() => Assert.True(component.Find("button.btn-primary").HasAttribute("disabled")));
            Assert.False(saveClick.IsCompleted);
            Assert.True(settings.RefreshChannelsBeforeGuideFetch);
            Assert.Empty(context.Services.GetRequiredService<IStatusNotificationService>().Notifications);
        }
        finally
        {
            persistence.SetResult();
            await saveClick;
        }
        Assert.True(enabledByDefault);
        Assert.True(historyDefaultsTo24Hours);
        Assert.True(guideCheckboxesUseSwitches);
        Assert.False(settings.RefreshChannelsBeforeGuideFetch);
        Assert.Equal(48, settings.EpgHistoryRetentionHours);
        await settingsService.Received(1).UpdateAsync(Arg.Any<Action<LineupSettings>>());
        var notification = Assert.Single(context.Services.GetRequiredService<IStatusNotificationService>().Notifications);
        Assert.Equal("Settings saved successfully!", notification.Message);
        Assert.False(notification.IsError);
        Assert.False(component.Find("button.btn-primary").HasAttribute("disabled"));
    }
}
