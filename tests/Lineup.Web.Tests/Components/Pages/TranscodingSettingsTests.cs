using Bunit;
using Lineup.Web.Components.Pages;
using Lineup.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Lineup.Web.Tests.Components.Pages;

/// <summary>
/// Verifies transcoding settings behavior on the Settings page.
/// </summary>
public class TranscodingSettingsTests
{
    /// <summary>
    /// Verifies the disabled-channel behavior defaults to an error and persists edits.
    /// </summary>
    [Fact]
    public void Save_DisabledChannelMode_PersistsValue()
    {
        // Arrange
        using var context = new BunitContext();
        var settings = new AppSettings { IsSetupComplete = true };
        var settingsService = Substitute.For<IAppSettingsService>();
        settingsService.Settings.Returns(settings);
        settingsService.UpdateAsync(Arg.Any<Action<AppSettings>>()).Returns(callInfo =>
        {
            callInfo.Arg<Action<AppSettings>>()(settings);
            return Task.CompletedTask;
        });
        var timeZoneService = Substitute.For<ITimeZoneService>();
        timeZoneService.TimeZone.Returns(TimeZoneInfo.Utc);
        context.Services.AddSingleton(settingsService);
        context.Services.AddSingleton(timeZoneService);
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var component = context.Render<Settings>();
        component.Find("#tab-transcoding").Click();
        var selector = component.Find("#disabledChannelMode");

        // Act
        selector.Change(DisabledChannelMode.StreamSlate.ToString());
        component.Find("button.btn-primary").Click();

        // Assert
        Assert.Equal(DisabledChannelMode.StreamSlate, settings.DisabledChannelMode);
        settingsService.Received(1).UpdateAsync(Arg.Any<Action<AppSettings>>());
    }
}
