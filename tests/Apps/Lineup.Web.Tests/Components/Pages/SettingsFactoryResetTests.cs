using Bunit;
using Lineup.Web.Components.Pages;
using Lineup.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Lineup.Web.Tests.Components.Pages;

/// <summary>
/// Verifies the Settings factory-reset confirmation flow.
/// </summary>
public class SettingsFactoryResetTests
{
    /// <summary>
    /// Verifies recovery mode opens directly to the explicit factory-reset surface.
    /// </summary>
    [Fact]
    public void FactoryResetRequired_InitialRender_ShowsRecoveryStatusAndResetSurface()
    {
        // Arrange
        using var context = new BunitContext();
        var settingsService = Substitute.For<ILineupSettingsService>();
        settingsService.Settings.Returns(new LineupSettings());
        var timeZoneService = Substitute.For<ITimeZoneService>();
        timeZoneService.TimeZone.Returns(TimeZoneInfo.Utc);
        context.Services.AddSingleton(settingsService);
        context.Services.AddSingleton(timeZoneService);
        context.Services.AddSingleton(new LineupStartupState(LineupStartupStatus.FactoryResetRequired, "Legacy state detected."));

        // Act
        var component = context.Render<Settings>();

        // Assert
        Assert.Contains("Factory reset required", component.Find("#factoryResetRequired").TextContent);
        Assert.False(component.Find("#resetSettingsTab").ClassList.Contains("d-none"));
        Assert.NotNull(component.Find("#showFactoryResetConfirmation"));
    }

    /// <summary>
    /// Verifies that restarting Lineup preserves data and watches for the replacement process.
    /// </summary>
    [Fact]
    public async Task RestartLineup_Clicked_RequestsRestartAndShowsOverlay()
    {
        // Arrange
        using var context = new BunitContext();
        var settingsService = Substitute.For<ILineupSettingsService>();
        settingsService.Settings.Returns(new LineupSettings { IsSetupComplete = true });
        var restartService = Substitute.For<IApplicationRestartService>();
        restartService.RestartAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var timeZoneService = Substitute.For<ITimeZoneService>();
        timeZoneService.TimeZone.Returns(TimeZoneInfo.Utc);
        context.Services.AddSingleton(settingsService);
        context.Services.AddSingleton(restartService);
        context.Services.AddSingleton(timeZoneService);
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var component = context.Render<Settings>();
        component.Find("#tab-reset").Click();

        // Act
        await component.Find("#restartLineup").ClickAsync(new());

        // Assert
        await restartService.Received(1).RestartAsync(Arg.Any<CancellationToken>());
        var invocation = Assert.Single(context.JSInterop.Invocations, item => item.Identifier == "beginLineupRestartWatch");
        Assert.Equal("/settings#reset", Assert.Single(invocation.Arguments));
        Assert.Contains("Lineup will return to this page automatically", component.Find("#factoryResetRestartOverlay").TextContent);
    }

    /// <summary>
    /// Verifies that factory reset requires checkbox confirmation and blocks the settings interface while restart detection runs.
    /// </summary>
    [Fact]
    public async Task FactoryReset_ConfirmationChecked_RequestsResetAndShowsRestartOverlay()
    {
        // Arrange
        using var context = new BunitContext();
        var settingsService = Substitute.For<ILineupSettingsService>();
        settingsService.Settings.Returns(new LineupSettings { IsSetupComplete = true });
        var factoryResetService = Substitute.For<IFactoryResetService>();
        var timeZoneService = Substitute.For<ITimeZoneService>();
        timeZoneService.TimeZone.Returns(TimeZoneInfo.Utc);
        context.Services.AddSingleton(settingsService);
        context.Services.AddSingleton(factoryResetService);
        context.Services.AddSingleton(timeZoneService);
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var component = context.Render<Settings>();
        IReadOnlyList<AngleSharp.Dom.IElement> tabButtons = component.FindAll("#settingsCategoryTabs button");
        var lastTabId = tabButtons[tabButtons.Count - 1].Id;
        var hiddenOutsideResetTab = component.Find("#resetSettingsTab").ClassList.Contains("d-none");
        component.Find("#tab-reset").Click();
        component.Find("#showFactoryResetConfirmation").Click();
        var confirmationWasChecked = component.Find("#factoryResetConfirmationCheck").HasAttribute("checked");
        var confirmButtonBeforeSelection = component.Find("#confirmFactoryReset");

        // Act
        component.Find("#factoryResetConfirmationCheck").Change(true);
        await component.Find("#confirmFactoryReset").ClickAsync(new());

        // Assert
        Assert.Equal("tab-reset", lastTabId);
        Assert.True(hiddenOutsideResetTab);
        Assert.False(confirmationWasChecked);
        Assert.True(confirmButtonBeforeSelection.HasAttribute("disabled"));
        await factoryResetService.Received(1).RequestResetAsync(Arg.Any<CancellationToken>());
        Assert.Single(context.JSInterop.Invocations, invocation => invocation.Identifier == "beginLineupFactoryResetWatch");
        Assert.EndsWith("/settings#reset", context.Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal);
        Assert.Contains("Restarting Lineup", component.Find("#factoryResetRestartOverlay").TextContent);
        Assert.Contains("Lineup is taking longer than expected", component.Find("#factoryResetRestartTimeout").TextContent);
        Assert.NotNull(component.Find("#retryFactoryResetRestart"));
    }

    /// <summary>
    /// Verifies that canceling confirmation leaves persistent state untouched.
    /// </summary>
    [Fact]
    public void FactoryReset_Cancel_HidesConfirmationWithoutRequestingReset()
    {
        // Arrange
        using var context = new BunitContext();
        var settingsService = Substitute.For<ILineupSettingsService>();
        settingsService.Settings.Returns(new LineupSettings { IsSetupComplete = true });
        var factoryResetService = Substitute.For<IFactoryResetService>();
        var timeZoneService = Substitute.For<ITimeZoneService>();
        timeZoneService.TimeZone.Returns(TimeZoneInfo.Utc);
        context.Services.AddSingleton(settingsService);
        context.Services.AddSingleton(factoryResetService);
        context.Services.AddSingleton(timeZoneService);
        var component = context.Render<Settings>();
        component.Find("#tab-reset").Click();
        component.Find("#showFactoryResetConfirmation").Click();

        // Act
        component.Find("#cancelFactoryReset").Click();

        // Assert
        Assert.Empty(component.FindAll("#factoryResetConfirmation"));
        Assert.NotNull(component.Find("#showFactoryResetConfirmation"));
        factoryResetService.DidNotReceive().RequestResetAsync(Arg.Any<CancellationToken>());
    }
}
