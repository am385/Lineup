using System.Text.Json;
using Lineup.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>
/// Verifies bounded log storage, redaction, filtering, and retained-file safety.
/// </summary>
public class LoggingServiceTests
{
    /// <summary>
    /// Verifies that the in-memory store evicts its oldest event when capacity is reached.
    /// </summary>
    [Fact]
    public void Emit_MoreThanCapacity_RetainsNewestEvents()
    {
        // Arrange
        var store = new InMemoryLogEventStore(100);
        using var logger = new LoggerConfiguration().WriteTo.Sink(store).CreateLogger();

        // Act
        for (var index = 0; index < 101; index++)
        {
            logger.Information("Event {Index}", index);
        }
        var events = store.GetEvents(LogEventLevel.Information, null, null, 500);

        // Assert
        Assert.Equal(100, events.Count);
        Assert.Contains("100", events[0].Message);
        Assert.DoesNotContain(events, logEvent => logEvent.Message == "Event 0");
    }

    /// <summary>
    /// Verifies that sensitive structured properties are redacted before reaching the viewer.
    /// </summary>
    [Fact]
    public void Emit_SensitiveProperty_RedactsValue()
    {
        // Arrange
        var store = new InMemoryLogEventStore();
        using var logger = new LoggerConfiguration().WriteTo.Sink(store).CreateLogger();

        // Act
        logger.Information("Connected with {ApiKey}", "do-not-display");
        var logEvent = Assert.Single(store.GetEvents(LogEventLevel.Information, null, "Connected", 10));

        // Assert
        Assert.Equal("[REDACTED]", logEvent.Properties["ApiKey"]);
        Assert.DoesNotContain("do-not-display", logEvent.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("do-not-display", string.Join(' ', logEvent.Properties.Values), StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies sensitive names nested inside destructured objects are redacted.
    /// </summary>
    [Fact]
    public void Emit_NestedSensitiveProperty_RedactsValue()
    {
        // Arrange
        var store = new InMemoryLogEventStore();
        using var logger = new LoggerConfiguration().WriteTo.Sink(store).CreateLogger();

        // Act
        logger.Information("Payload {@Payload}", new { User = "viewer", Password = "nested-secret" });
        var logEvent = Assert.Single(store.GetEvents(LogEventLevel.Information, null, "Payload", 10));

        // Assert
        Assert.DoesNotContain("nested-secret", logEvent.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("nested-secret", logEvent.Properties["Payload"], StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", logEvent.Properties["Payload"], StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies category and message filters are applied case-insensitively.
    /// </summary>
    [Fact]
    public void GetEvents_WithFilters_ReturnsMatchingEvents()
    {
        // Arrange
        var store = new InMemoryLogEventStore();
        using var logger = new LoggerConfiguration().WriteTo.Sink(store).CreateLogger();
        logger.ForContext("SourceContext", "Lineup.Device").Information("Connected to tuner");
        logger.ForContext("SourceContext", "Lineup.Guide").Warning("Guide unavailable");

        // Act
        var events = store.GetEvents(LogEventLevel.Information, "device", "TUNER", 10);

        // Assert
        var logEvent = Assert.Single(events);
        Assert.Equal("Lineup.Device", logEvent.Category);
    }

    /// <summary>
    /// Verifies that retained-file access rejects traversal and non-Lineup filenames.
    /// </summary>
    [Fact]
    public void OpenRead_UnsafeName_ReturnsNull()
    {
        // Arrange
        var root = Path.Combine(Path.GetTempPath(), $"lineup-logging-{Guid.NewGuid():N}");
        var runtime = new LoggingRuntimeState(root, new FileLoggingSettings(true, LogEventLevel.Information, 7, 50), []);
        var service = new LogFileService(runtime);

        // Act
        var traversal = service.OpenRead($"..{Path.DirectorySeparatorChar}settings.json");
        var unrelated = service.OpenRead("settings.json");

        // Assert
        Assert.Null(traversal);
        Assert.Null(unrelated);
    }

    /// <summary>
    /// Verifies clearing an active file sink removes history and continues writing to a fresh file.
    /// </summary>
    [Fact]
    public void DeleteFiles_ActiveFileSink_RotatesToEmptyLog()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-log-delete-");
        var manager = new FileLogManager(root.FullName, new FileLoggingSettings(true, LogEventLevel.Information, 7, 50), "{Message:lj}{NewLine}");
        using var logger = new LoggerConfiguration().WriteTo.Sink(manager).CreateLogger();
        logger.Information("remove this event");

        // Act
        var deleted = manager.DeleteFiles();
        logger.Information("keep this event");
        manager.Dispose();
        var content = string.Join(Environment.NewLine, Directory.GetFiles(root.FullName, "lineup-*.log").Select(File.ReadAllText));

        // Assert
        Assert.Equal(1, deleted);
        Assert.DoesNotContain("remove this event", content, StringComparison.Ordinal);
        Assert.Contains("keep this event", content, StringComparison.Ordinal);
        root.Delete(recursive: true);
    }

    /// <summary>
    /// Verifies runtime settings can disable and re-enable file output with a new minimum level.
    /// </summary>
    [Fact]
    public void ApplySettings_ChangedAtRuntime_UpdatesFileOutput()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-log-reconfigure-");
        var manager = new FileLogManager(root.FullName, new FileLoggingSettings(true, LogEventLevel.Information, 7, 50), "{Message:lj}{NewLine}");
        using var logger = new LoggerConfiguration().WriteTo.Sink(manager).CreateLogger();
        logger.Information("initial event");

        // Act
        manager.ApplySettings(new FileLoggingSettings(false, LogEventLevel.Information, 7, 50));
        logger.Error("disabled event");
        manager.ApplySettings(new FileLoggingSettings(true, LogEventLevel.Warning, 14, 100));
        logger.Information("filtered event");
        logger.Warning("reconfigured event");
        manager.Dispose();
        var content = string.Join(Environment.NewLine, Directory.GetFiles(root.FullName, "lineup-*.log").Select(File.ReadAllText));

        // Assert
        Assert.Contains("initial event", content, StringComparison.Ordinal);
        Assert.DoesNotContain("disabled event", content, StringComparison.Ordinal);
        Assert.DoesNotContain("filtered event", content, StringComparison.Ordinal);
        Assert.Contains("reconfigured event", content, StringComparison.Ordinal);
        Assert.Equal(new FileLoggingSettings(true, LogEventLevel.Warning, 14, 100), manager.Settings);
        root.Delete(recursive: true);
    }

    /// <summary>
    /// Verifies file logging options survive normal settings persistence and normalization.
    /// </summary>
    [Fact]
    public async Task SaveAsync_FileLoggingOptions_RoundTripsValues()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-log-settings-");
        var settingsPath = Path.Combine(root.FullName, "settings.json");
        var service = new AppSettingsService(Microsoft.Extensions.Logging.Abstractions.NullLogger<AppSettingsService>.Instance, settingsPath);
        service.Settings.EnableFileLogging = true;
        service.Settings.FileLogLevel = "warning";
        service.Settings.FileLogRetentionDays = 14;
        service.Settings.FileLogSizeLimitMb = 100;
        service.Settings.OverrideLoggingDefaults = true;
        service.Settings.ApplicationLogLevel = "debug";
        service.Settings.LogCategoryOverrides = [new LogCategoryLevelSetting { Category = "Lineup.Noisy", Level = "none" }];

        // Act
        await service.SaveAsync();
        var reloaded = new AppSettingsService(Microsoft.Extensions.Logging.Abstractions.NullLogger<AppSettingsService>.Instance, settingsPath);

        // Assert
        Assert.True(reloaded.Settings.EnableFileLogging);
        Assert.Equal("Warning", reloaded.Settings.FileLogLevel);
        Assert.Equal(14, reloaded.Settings.FileLogRetentionDays);
        Assert.Equal(100, reloaded.Settings.FileLogSizeLimitMb);
        Assert.True(reloaded.Settings.OverrideLoggingDefaults);
        Assert.Equal("Debug", reloaded.Settings.ApplicationLogLevel);
        var category = Assert.Single(reloaded.Settings.LogCategoryOverrides);
        Assert.Equal("Lineup.Noisy", category.Category);
        Assert.Equal("None", category.Level);
        root.Delete(recursive: true);
    }

    /// <summary>
    /// Verifies enabled file settings create a rolling file and external targets remain disabled by default.
    /// </summary>
    [Fact]
    public async Task Configure_FileLoggingEnabled_WritesRollingFile()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-log-bootstrap-");
        var settings = new AppSettings { EnableFileLogging = true, FileLogLevel = "Warning", FileLogRetentionDays = 3, FileLogSizeLimitMb = 2 };
        await File.WriteAllTextAsync(Path.Combine(root.FullName, "settings.json"), JsonSerializer.Serialize(settings), TestContext.Current.CancellationToken);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root.FullName, EnvironmentName = "Production" });

        // Act
        var result = LoggingBootstrapper.Configure(builder, root.FullName);
        var app = builder.Build();
        app.Logger.LogWarning("Rolling file verification");
        await app.DisposeAsync();
        var logFile = Assert.Single(Directory.GetFiles(result.RuntimeState.LogDirectory, "lineup-*.log"));

        // Assert
        Assert.True(result.RuntimeState.FileSettings.Enabled);
        Assert.Equal(3, result.RuntimeState.FileSettings.RetentionDays);
        Assert.All(result.RuntimeState.ExternalTargets, target => Assert.Equal("Disabled", target.Status));
        Assert.Contains("Rolling file verification", await File.ReadAllTextAsync(logFile, TestContext.Current.CancellationToken));
        root.Delete(recursive: true);
    }

    /// <summary>
    /// Verifies invalid external configuration is reported without exposing supplied values.
    /// </summary>
    [Fact]
    public async Task Configure_InvalidExternalTargets_ReportsSafeStatuses()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-log-targets-");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root.FullName, EnvironmentName = "Production" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Lineup:Logging:OpenTelemetry:Endpoint"] = "also-not-a-url"
        });

        // Act
        var result = LoggingBootstrapper.Configure(builder, root.FullName);
        var combinedStatus = string.Join(' ', result.RuntimeState.ExternalTargets.Select(target => $"{target.Name} {target.Status} {target.Message}"));
        var app = builder.Build();
        await app.DisposeAsync();

        // Assert
        Assert.Single(result.RuntimeState.ExternalTargets);
        Assert.All(result.RuntimeState.ExternalTargets, target => Assert.Equal("Invalid", target.Status));
        Assert.DoesNotContain("also-not-a-url", combinedStatus, StringComparison.Ordinal);
        root.Delete(recursive: true);
    }

    /// <summary>
    /// Verifies the permissive Serilog floor allows the standard logging configuration to enable debug events.
    /// </summary>
    [Fact]
    public async Task Configure_StandardLoggingAllowsDebug_CapturesDebugEvent()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-log-filter-");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root.FullName, EnvironmentName = "Production" });
        builder.Configuration["Logging:LogLevel:Default"] = "Debug";

        // Act
        var result = LoggingBootstrapper.Configure(builder, root.FullName);
        var app = builder.Build();
        app.Logger.LogDebug("Upstream debug verification");
        var events = result.EventStore.GetEvents(LogEventLevel.Debug, null, null, 10);
        await app.DisposeAsync();

        // Assert
        Assert.Contains(events, logEvent => logEvent.Message == "Upstream debug verification");
        root.Delete(recursive: true);
    }

    /// <summary>
    /// Verifies standard default and category filters are enforced before events reach any Serilog sink.
    /// </summary>
    [Fact]
    public async Task Configure_StandardLoggingFilters_SuppressesEventsBelowConfiguredLevels()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-log-filter-");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root.FullName, EnvironmentName = "Production" });
        builder.Configuration["Logging:LogLevel:Default"] = "Information";
        builder.Configuration["Logging:LogLevel:Microsoft.AspNetCore"] = "Warning";
        var result = LoggingBootstrapper.Configure(builder, root.FullName);
        var app = builder.Build();

        // Act
        app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Lineup.Tests").LogDebug("suppressed application debug");
        app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Microsoft.AspNetCore.Components.RenderTree.Renderer").LogDebug("suppressed framework debug");
        app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Microsoft.AspNetCore.Components.RenderTree.Renderer").LogWarning("retained framework warning");
        var events = result.EventStore.GetEvents(LogEventLevel.Debug, null, null, 10);
        await app.DisposeAsync();

        // Assert
        Assert.DoesNotContain(events, logEvent => logEvent.Message.Contains("suppressed", StringComparison.Ordinal));
        Assert.Contains(events, logEvent => logEvent.Message == "retained framework warning");
        root.Delete(recursive: true);
    }

    /// <summary>
    /// Verifies runtime overrides use longest-prefix precedence and disabling them restores startup filters.
    /// </summary>
    [Fact]
    public async Task ApplyApplicationFilter_RuntimeOverrideAndDisable_UpdatesEffectiveFilters()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-log-runtime-filter-");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root.FullName, EnvironmentName = "Production" });
        builder.Configuration["Logging:LogLevel:Default"] = "Information";
        builder.Configuration["Logging:LogLevel:Microsoft.AspNetCore"] = "Warning";
        var result = LoggingBootstrapper.Configure(builder, root.FullName);
        var app = builder.Build();
        var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();

        // Act
        result.RuntimeState.ApplyApplicationFilter(new AppSettings
        {
            OverrideLoggingDefaults = true,
            ApplicationLogLevel = "Debug",
            LogCategoryOverrides =
            [
                new LogCategoryLevelSetting { Category = "Microsoft.AspNetCore", Level = "Error" },
                new LogCategoryLevelSetting { Category = "Microsoft.AspNetCore.Components", Level = "Warning" },
                new LogCategoryLevelSetting { Category = "Lineup.Noisy", Level = "None" }
            ]
        });
        loggerFactory.CreateLogger("Lineup.Tests").LogDebug("override application debug");
        loggerFactory.CreateLogger("Microsoft.AspNetCore.Components.RenderTree.Renderer").LogWarning("longest prefix warning");
        loggerFactory.CreateLogger("Microsoft.AspNetCore.Mvc").LogWarning("suppressed parent warning");
        loggerFactory.CreateLogger("Lineup.Noisy.Worker").LogCritical("suppressed none event");
        result.RuntimeState.ApplyApplicationFilter(new AppSettings { OverrideLoggingDefaults = false });
        loggerFactory.CreateLogger("Lineup.Tests").LogDebug("restored application debug");
        loggerFactory.CreateLogger("Microsoft.AspNetCore.Mvc").LogWarning("restored framework warning");
        var events = result.EventStore.GetEvents(LogEventLevel.Verbose, null, null, 20);
        await app.DisposeAsync();

        // Assert
        Assert.Contains(events, logEvent => logEvent.Message == "override application debug");
        Assert.Contains(events, logEvent => logEvent.Message == "longest prefix warning");
        Assert.Contains(events, logEvent => logEvent.Message == "restored framework warning");
        Assert.DoesNotContain(events, logEvent => logEvent.Message.Contains("suppressed", StringComparison.Ordinal));
        Assert.DoesNotContain(events, logEvent => logEvent.Message == "restored application debug");
        Assert.Equal("Information", result.RuntimeState.ActiveApplicationFilter.DefaultLevel);
        root.Delete(recursive: true);
    }
}
