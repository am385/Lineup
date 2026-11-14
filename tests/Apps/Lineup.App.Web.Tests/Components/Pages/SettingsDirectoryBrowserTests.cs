using Bunit;
using Lineup.App.Web.Components.Pages;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Lineup.App.Web.Tests.Components.Pages;

/// <summary>
/// Verifies the server directory browser on the Settings page.
/// </summary>
public class SettingsDirectoryBrowserTests
{
    /// <summary>
    /// Verifies that the XMLTV server directory browser opens at the saved path and can be closed.
    /// </summary>
    [Fact]
    public async Task BrowseXmltvDirectories_ExistingSavedPath_OpensAtDirectoryAndCloses()
    {
        // Arrange
        await using var context = new BunitContext();
        var temporaryDirectory = Directory.CreateTempSubdirectory("lineup-settings-browser-");
        var settingsService = Substitute.For<ILineupSettingsService>();
        settingsService.Settings.Returns(new LineupSettings { IsSetupComplete = true, XmltvOutputPath = Path.Combine(temporaryDirectory.FullName, "saved.xml") });
        settingsService.ConfiguredXmltvOutputPath.Returns(Path.Combine(temporaryDirectory.FullName, "configured.xml"));
        var timeZoneService = Substitute.For<ITimeZoneService>();
        timeZoneService.TimeZone.Returns(TimeZoneInfo.Utc);
        context.Services.AddSingleton(settingsService);
        context.Services.AddSingleton(timeZoneService);
        var component = context.Render<Settings>();
        await component.Find("#tab-guide-outputs").ClickAsync(new());

        // Act
        await component.Find("#browseXmltvDirectories").ClickAsync(new());
        var currentDirectory = component.Find("#currentXmltvDirectory").GetAttribute("value");
        await component.Find("#closeXmltvDirectoryBrowser").ClickAsync(new());

        // Assert
        Assert.Equal(temporaryDirectory.FullName, currentDirectory);
        Assert.Empty(component.FindAll("#xmltvFilename"));
        Assert.Empty(component.FindAll("#xmltvDirectoryBrowser"));
        temporaryDirectory.Delete(true);
    }

    /// <summary>
    /// Verifies that selecting a server directory updates the editable XMLTV path without persisting it.
    /// </summary>
    [Fact]
    public async Task SelectXmltvPath_ChildDirectory_UpdatesFormWithoutSaving()
    {
        // Arrange
        await using var context = new BunitContext();
        var temporaryDirectory = Directory.CreateTempSubdirectory("lineup-settings-browser-");
        var childDirectory = temporaryDirectory.CreateSubdirectory("guide-output");
        var settingsService = Substitute.For<ILineupSettingsService>();
        settingsService.Settings.Returns(new LineupSettings { IsSetupComplete = true, XmltvOutputPath = Path.Combine(temporaryDirectory.FullName, "saved.xml") });
        settingsService.ConfiguredXmltvOutputPath.Returns(Path.Combine(temporaryDirectory.FullName, "configured.xml"));
        var timeZoneService = Substitute.For<ITimeZoneService>();
        timeZoneService.TimeZone.Returns(TimeZoneInfo.Utc);
        context.Services.AddSingleton(settingsService);
        context.Services.AddSingleton(timeZoneService);
        var component = context.Render<Settings>();
        await component.Find("#tab-guide-outputs").ClickAsync(new());
        await component.Find("#browseXmltvDirectories").ClickAsync(new());

        // Act
        await component.FindAll(".xmltv-directory").Single(element => element.TextContent.Contains(childDirectory.Name, StringComparison.Ordinal)).ClickAsync(new());
        await component.Find("#selectXmltvPath").ClickAsync(new());

        // Assert
        Assert.Equal(childDirectory.FullName, component.Find("#guideOutputDirectoryPath").GetAttribute("value"));
        Assert.Contains(Path.Combine(childDirectory.FullName, "saved.xml"), component.Find("#resolvedXmltvOutputPath").TextContent);
        Assert.Empty(component.FindAll("#xmltvDirectoryBrowser"));
        await settingsService.DidNotReceive().UpdateAsync(Arg.Any<Action<LineupSettings>>());
        temporaryDirectory.Delete(true);
    }
}
