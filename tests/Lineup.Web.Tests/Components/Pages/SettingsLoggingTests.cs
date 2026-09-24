using Bunit;
using Lineup.Web.Components.Pages;
using Lineup.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Serilog.Events;
using Xunit;

namespace Lineup.Web.Tests.Components.Pages;

/// <summary>
/// Verifies persisted logging settings and startup target status.
/// </summary>
public class SettingsLoggingTests
{
    /// <summary>
    /// Verifies dependent logging controls render only when their feature switches are enabled.
    /// </summary>
    [Fact]
    public void Render_DisabledLoggingFeatures_CollapsesDependentControls()
    {
        // Arrange
        using var context = CreateContext(out _);
        var component = context.Render<Settings>();
        component.Find("#tab-logging").Click();

        // Act
        component.Find("#overrideLoggingDefaults").Change(true);
        component.Find("#enableFileLogging").Change(true);

        // Assert
        Assert.NotNull(component.Find("#applicationLogFilterOverrides"));
        Assert.NotNull(component.Find("#rollingFileLogOptions"));
        component.Find("#overrideLoggingDefaults").Change(false);
        component.Find("#enableFileLogging").Change(false);
        Assert.Empty(component.FindAll("#applicationLogFilterOverrides"));
        Assert.Empty(component.FindAll("#rollingFileLogOptions"));
    }

    /// <summary>
    /// Verifies enabled file logging displays an empty retained-files state.
    /// </summary>
    [Fact]
    public void Render_EnabledFileLoggingWithoutFiles_ShowsEmptyState()
    {
        // Arrange
        using var context = CreateContext(out var settings);
        settings.EnableFileLogging = true;

        // Act
        var component = context.Render<Settings>();
        component.Find("#tab-logging").Click();

        // Assert
        Assert.Equal("No rolling log files are available yet.", component.Find("#emptyRetainedLogFiles").TextContent);
    }

    /// <summary>
    /// Verifies Logging appears immediately before Reset and displays non-sensitive target status.
    /// </summary>
    [Fact]
    public void Render_ConfiguredInstallation_ShowsLoggingBeforeReset()
    {
        // Arrange
        using var context = CreateContext(out _);
        var runtime = new LoggingRuntimeState("config", new FileLoggingSettings(false, LogEventLevel.Information, 7, 50),
            [new ExternalLogTargetStatus("OpenTelemetry (OTLP)", "Enabled", "Configured at startup.")]);
        context.Services.AddSingleton(runtime);

        // Act
        var component = context.Render<Settings>();
        var tabs = component.FindAll("ul.nav-tabs button").Select(button => button.Id).ToArray();
        component.Find("#tab-logging").Click();

        // Assert
        Assert.Equal("tab-logging", tabs[^2]);
        Assert.Equal("tab-reset", tabs[^1]);
        Assert.Contains("OpenTelemetry (OTLP)", component.Find("#loggingSettingsTab").TextContent);
        Assert.DoesNotContain("Authorization", component.Markup, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Verifies saved file options are persisted and applied to the running process.
    /// </summary>
    [Fact]
    public void Save_ChangedFileLogging_PersistsAndAppliesImmediately()
    {
        // Arrange
        using var context = CreateContext(out var settings);
        var root = Directory.CreateTempSubdirectory("lineup-settings-logging-");
        using var manager = new FileLogManager(Path.Combine(root.FullName, "logs"), new FileLoggingSettings(false, LogEventLevel.Information, 7, 50), "{Message:lj}{NewLine}");
        var runtime = new LoggingRuntimeState(root.FullName, new FileLoggingSettings(false, LogEventLevel.Information, 7, 50), [], manager);
        context.Services.AddSingleton(runtime);
        context.Services.AddSingleton(new LogFileService(runtime));
        var component = context.Render<Settings>();
        component.Find("#tab-logging").Click();

        // Act
        component.Find("#enableFileLogging").Change(true);
        component.Find("#fileLogLevel").Change("Warning");
        component.Find("#fileLogRetentionDays").Change("14");
        component.Find("#fileLogSizeLimitMb").Change("100");
        component.Find("button.btn-primary").Click();

        // Assert
        Assert.True(settings.EnableFileLogging);
        Assert.Equal("Warning", settings.FileLogLevel);
        Assert.Equal(14, settings.FileLogRetentionDays);
        Assert.Equal(100, settings.FileLogSizeLimitMb);
        Assert.True(runtime.FileSettings.Enabled);
        Assert.Equal(LogEventLevel.Warning, runtime.FileSettings.MinimumLevel);
        Assert.True(Directory.Exists(runtime.LogDirectory));
        Assert.Empty(component.FindAll("#loggingSettingsTab #restartLineup"));
        var notifications = context.Services.GetRequiredService<IStatusNotificationService>();
        var notification = Assert.Single(notifications.Notifications);
        Assert.Equal("Settings saved successfully!", notification.Message);
        Assert.False(notification.IsError);
        manager.Dispose();
        root.Delete(recursive: true);
    }

    /// <summary>
    /// Verifies startup categories are discovered and a complete runtime override is persisted and applied.
    /// </summary>
    [Fact]
    public void Save_ApplicationFilterOverride_PersistsAndAppliesImmediately()
    {
        // Arrange
        using var context = CreateContext(out var settings);
        var configuration = new ConfigurationManager();
        configuration["Logging:LogLevel:Default"] = "Information";
        configuration["Logging:LogLevel:Microsoft.AspNetCore"] = "Warning";
        configuration["Logging:LogLevel:System.Net.Http.HttpClient"] = "Warning";
        var filter = ApplicationLogFilter.FromConfiguration(configuration);
        var runtime = new LoggingRuntimeState("config", new FileLoggingSettings(false, LogEventLevel.Information, 7, 50), [], applicationFilter: filter);
        context.Services.AddSingleton(runtime);
        var component = context.Render<Settings>();
        component.Find("#tab-logging").Click();

        // Act
        component.Find("#overrideLoggingDefaults").Change(true);
        component.Find("#applicationLogLevel").Change("Debug");
        component.FindAll(".log-category-level")[0].Change("Error");
        component.Find("#addLogCategoryOverride").Click();
        component.FindAll(".log-category-prefix")[^1].Change("Lineup.Noisy");
        component.FindAll(".log-category-level")[^1].Change("None");
        component.Find("button.btn-primary").Click();

        // Assert
        Assert.True(settings.OverrideLoggingDefaults);
        Assert.Equal("Debug", settings.ApplicationLogLevel);
        Assert.Equal(3, settings.LogCategoryOverrides.Count);
        Assert.Contains(settings.LogCategoryOverrides, level => level.Category == "Lineup.Noisy" && level.Level == "None");
        Assert.Equal("Debug", filter.ActiveSettings.DefaultLevel);
        Assert.Contains(filter.ActiveSettings.CategoryLevels, level => level.Category == "Lineup.Noisy" && level.Level == "None");
        var notifications = context.Services.GetRequiredService<IStatusNotificationService>();
        var notification = Assert.Single(notifications.Notifications);
        Assert.Equal("Settings saved successfully!", notification.Message);
        Assert.False(notification.IsError);
    }

    /// <summary>
    /// Verifies startup categories added after an override was saved remain visible and effective.
    /// </summary>
    [Fact]
    public void Save_ExistingApplicationFilterOverride_MergesNewStartupCategories()
    {
        // Arrange
        using var context = CreateContext(out var settings);
        settings.OverrideLoggingDefaults = true;
        settings.ApplicationLogLevel = "Debug";
        settings.LogCategoryOverrides = [new LogCategoryLevelSetting { Category = "Lineup.Noisy", Level = "None" }];
        var configuration = new ConfigurationManager();
        configuration["Logging:LogLevel:Default"] = "Information";
        configuration["Logging:LogLevel:Microsoft.AspNetCore"] = "Warning";
        var filter = ApplicationLogFilter.FromConfiguration(configuration);
        var runtime = new LoggingRuntimeState("config", new FileLoggingSettings(false, LogEventLevel.Information, 7, 50), [], applicationFilter: filter);
        context.Services.AddSingleton(runtime);
        var component = context.Render<Settings>();
        component.Find("#tab-logging").Click();

        // Act
        component.Find("button.btn-primary").Click();

        // Assert
        Assert.Equal(2, settings.LogCategoryOverrides.Count);
        Assert.Contains(settings.LogCategoryOverrides, level => level.Category == "Microsoft.AspNetCore" && level.Level == "Warning");
        Assert.Contains(settings.LogCategoryOverrides, level => level.Category == "Lineup.Noisy" && level.Level == "None");
        Assert.Contains(filter.ActiveSettings.CategoryLevels, level => level.Category == "Microsoft.AspNetCore" && level.Level == "Warning");
    }

    /// <summary>
    /// Verifies disabling UI overrides restores the complete startup filter set immediately.
    /// </summary>
    [Fact]
    public void Save_DisabledApplicationFilterOverride_RestoresStartupFilters()
    {
        // Arrange
        using var context = CreateContext(out var settings);
        settings.OverrideLoggingDefaults = true;
        settings.ApplicationLogLevel = "Debug";
        settings.LogCategoryOverrides = [new LogCategoryLevelSetting { Category = "Lineup.Noisy", Level = "None" }];
        var configuration = new ConfigurationManager();
        configuration["Logging:LogLevel:Default"] = "Warning";
        configuration["Logging:LogLevel:Microsoft.AspNetCore"] = "Error";
        var filter = ApplicationLogFilter.FromConfiguration(configuration);
        var runtime = new LoggingRuntimeState("config", new FileLoggingSettings(false, LogEventLevel.Information, 7, 50), [], applicationFilter: filter);
        runtime.ApplyApplicationFilter(settings);
        context.Services.AddSingleton(runtime);
        var component = context.Render<Settings>();
        component.Find("#tab-logging").Click();

        // Act
        component.Find("#overrideLoggingDefaults").Change(false);
        component.Find("button.btn-primary").Click();

        // Assert
        Assert.False(settings.OverrideLoggingDefaults);
        Assert.Equal("Warning", filter.ActiveSettings.DefaultLevel);
        var category = Assert.Single(filter.ActiveSettings.CategoryLevels);
        Assert.Equal("Microsoft.AspNetCore", category.Category);
        Assert.Equal("Error", category.Level);
    }

    /// <summary>
    /// Verifies duplicate category prefixes are rejected before settings are persisted.
    /// </summary>
    [Fact]
    public void Save_DuplicateApplicationFilterCategory_ShowsValidationError()
    {
        // Arrange
        using var context = CreateContext(out var settings);
        var configuration = new ConfigurationManager();
        configuration["Logging:LogLevel:Default"] = "Information";
        configuration["Logging:LogLevel:Microsoft.AspNetCore"] = "Warning";
        var filter = ApplicationLogFilter.FromConfiguration(configuration);
        var runtime = new LoggingRuntimeState("config", new FileLoggingSettings(false, LogEventLevel.Information, 7, 50), [], applicationFilter: filter);
        context.Services.AddSingleton(runtime);
        var component = context.Render<Settings>();
        component.Find("#tab-logging").Click();

        // Act
        component.Find("#overrideLoggingDefaults").Change(true);
        component.Find("#addLogCategoryOverride").Click();
        component.FindAll(".log-category-prefix")[^1].Change("microsoft.aspnetcore");
        component.Find("button.btn-primary").Click();

        // Assert
        Assert.False(settings.OverrideLoggingDefaults);
        var notifications = context.Services.GetRequiredService<IStatusNotificationService>();
        var notification = Assert.Single(notifications.Notifications);
        Assert.Equal("Logging category prefixes must be unique.", notification.Message);
        Assert.True(notification.IsError);
    }

    private static BunitContext CreateContext(out AppSettings settings)
    {
        var context = new BunitContext();
        var currentSettings = new AppSettings { IsSetupComplete = true };
        settings = currentSettings;
        var settingsService = Substitute.For<IAppSettingsService>();
        settingsService.Settings.Returns(currentSettings);
        settingsService.ConfiguredXmltvOutputPath.Returns(currentSettings.XmltvOutputPath);
        settingsService.UpdateAsync(Arg.Any<Action<AppSettings>>()).Returns(callInfo =>
        {
            callInfo.Arg<Action<AppSettings>>()(currentSettings);
            return Task.CompletedTask;
        });
        var timeZoneService = Substitute.For<ITimeZoneService>();
        timeZoneService.TimeZone.Returns(TimeZoneInfo.Utc);
        context.Services.AddSingleton(settingsService);
        context.Services.AddSingleton(timeZoneService);
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        return context;
    }
}
