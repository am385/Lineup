using Bunit;
using Lineup.Web.Components.Pages;
using Lineup.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Lineup.Web.Tests.Components.Pages;

/// <summary>
/// Verifies the server directory browser on the Settings page.
/// </summary>
public class SettingsDirectoryBrowserTests
{
    /// <summary>
    /// Verifies that the XMLTV server directory browser opens at the saved path and can be closed.
    /// </summary>
    [Fact]
    public void BrowseXmltvDirectories_ExistingSavedPath_OpensAtDirectoryAndCloses()
    {
        // Arrange
        using var context = new BunitContext();
        var temporaryDirectory = Directory.CreateTempSubdirectory("lineup-settings-browser-");
        var settingsService = Substitute.For<IAppSettingsService>();
        settingsService.Settings.Returns(new AppSettings { IsSetupComplete = true, XmltvOutputPath = Path.Combine(temporaryDirectory.FullName, "saved.xml") });
        settingsService.ConfiguredXmltvOutputPath.Returns(Path.Combine(temporaryDirectory.FullName, "configured.xml"));
        var timeZoneService = Substitute.For<ITimeZoneService>();
        timeZoneService.TimeZone.Returns(TimeZoneInfo.Utc);
        context.Services.AddSingleton(settingsService);
        context.Services.AddSingleton(timeZoneService);
        var component = context.Render<Settings>();
        component.Find("#tab-guide").Click();

        // Act
        component.Find("#browseXmltvDirectories").Click();
        var currentDirectory = component.Find("#currentXmltvDirectory").GetAttribute("value");
        var filename = component.Find("#xmltvFilename").GetAttribute("value");
        component.Find("#closeXmltvDirectoryBrowser").Click();

        // Assert
        Assert.Equal(temporaryDirectory.FullName, currentDirectory);
        Assert.Equal("saved.xml", filename);
        Assert.Empty(component.FindAll("#xmltvDirectoryBrowser"));
        temporaryDirectory.Delete(true);
    }

    /// <summary>
    /// Verifies that selecting a server directory updates the editable XMLTV path without persisting it.
    /// </summary>
    [Fact]
    public void SelectXmltvPath_ChildDirectory_UpdatesFormWithoutSaving()
    {
        // Arrange
        using var context = new BunitContext();
        var temporaryDirectory = Directory.CreateTempSubdirectory("lineup-settings-browser-");
        var childDirectory = temporaryDirectory.CreateSubdirectory("guide-output");
        var settingsService = Substitute.For<IAppSettingsService>();
        settingsService.Settings.Returns(new AppSettings { IsSetupComplete = true, XmltvOutputPath = Path.Combine(temporaryDirectory.FullName, "saved.xml") });
        settingsService.ConfiguredXmltvOutputPath.Returns(Path.Combine(temporaryDirectory.FullName, "configured.xml"));
        var timeZoneService = Substitute.For<ITimeZoneService>();
        timeZoneService.TimeZone.Returns(TimeZoneInfo.Utc);
        context.Services.AddSingleton(settingsService);
        context.Services.AddSingleton(timeZoneService);
        var component = context.Render<Settings>();
        component.Find("#tab-guide").Click();
        component.Find("#browseXmltvDirectories").Click();

        // Act
        component.FindAll(".xmltv-directory").Single(element => element.TextContent.Contains(childDirectory.Name, StringComparison.Ordinal)).Click();
        component.Find("#xmltvFilename").Change("selected.xml");
        component.Find("#selectXmltvPath").Click();

        // Assert
        Assert.Equal(Path.Combine(childDirectory.FullName, "selected.xml"), component.Find("#xmltvOutputPath").GetAttribute("value"));
        Assert.Contains(Path.Combine(childDirectory.FullName, "selected.xml"), component.Find("#resolvedXmltvOutputPath").TextContent);
        Assert.Empty(component.FindAll("#xmltvDirectoryBrowser"));
        settingsService.DidNotReceive().UpdateAsync(Arg.Any<Action<AppSettings>>());
        temporaryDirectory.Delete(true);
    }
}
