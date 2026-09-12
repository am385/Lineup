using Bunit;
using Lineup.Web.Components.Pages;
using Lineup.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Serilog.Events;
using Xunit;

namespace Lineup.Web.Tests.Components.Pages;

/// <summary>
/// Verifies deletion of retained file logs from Settings.
/// </summary>
public class SettingsLogDeletionTests
{
    /// <summary>
    /// Verifies the Delete Logs action removes allowlisted files and reports the result.
    /// </summary>
    [Fact]
    public void DeleteLogs_WithRetainedFile_DeletesFileAndShowsStatus()
    {
        // Arrange
        using var context = new BunitContext();
        var root = Directory.CreateTempSubdirectory("lineup-settings-log-delete-");
        var logDirectory = root.CreateSubdirectory("logs");
        var logPath = Path.Combine(logDirectory.FullName, "lineup-test.log");
        File.WriteAllText(logPath, "history");
        var settingsService = Substitute.For<IAppSettingsService>();
        settingsService.Settings.Returns(new AppSettings { IsSetupComplete = true });
        var timeZoneService = Substitute.For<ITimeZoneService>();
        timeZoneService.TimeZone.Returns(TimeZoneInfo.Utc);
        var runtime = new LoggingRuntimeState(root.FullName, new FileLoggingSettings(false, LogEventLevel.Information, 7, 50), []);
        context.Services.AddSingleton(settingsService);
        context.Services.AddSingleton(timeZoneService);
        context.Services.AddSingleton(runtime);
        context.Services.AddSingleton(new LogFileService(runtime));
        var component = context.Render<Settings>();
        component.Find("#tab-logging").Click();
        var retainedFile = component.Find("#retainedLogFiles a");
        var retainedFileText = retainedFile.TextContent;
        var retainedFileHref = retainedFile.GetAttribute("href");
        var retainedFileDownload = retainedFile.GetAttribute("download");

        // Act
        component.Find("#deleteFileLogs").Click();

        // Assert
        Assert.Empty(component.FindAll("#rollingFileLogOptions"));
        Assert.Contains("lineup-test.log", retainedFileText);
        Assert.Contains("7 B", retainedFileText);
        Assert.Equal("/api/logs/files/lineup-test.log", retainedFileHref);
        Assert.Equal("lineup-test.log", retainedFileDownload);
        Assert.False(File.Exists(logPath));
        Assert.Contains("Deleted 1 file log.", component.Markup);
        Assert.Empty(component.FindAll("#retainedLogFileManagement"));
        root.Delete(recursive: true);
    }
}
