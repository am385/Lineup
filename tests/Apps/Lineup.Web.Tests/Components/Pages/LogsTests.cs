using Bunit;
using Lineup.Web.Components.Pages;
using Lineup.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Events;
using Xunit;

namespace Lineup.Web.Tests.Components.Pages;

/// <summary>
/// Verifies the Web log viewer.
/// </summary>
public class LogsTests
{
    /// <summary>
    /// Verifies the live viewer can display Trace events enabled by application filters.
    /// </summary>
    [Fact]
    public void Render_MinimumLevelOptions_IncludeTrace()
    {
        // Arrange
        using var context = new BunitContext();
        var root = Path.Combine(Path.GetTempPath(), $"lineup-logs-page-{Guid.NewGuid():N}");
        var store = new InMemoryLogEventStore();
        var runtime = new LoggingRuntimeState(root, new FileLoggingSettings(false, LogEventLevel.Information, 7, 50), []);
        context.Services.AddSingleton<ILogEventStore>(store);
        context.Services.AddSingleton(runtime);
        context.Services.AddSingleton(new LogFileService(runtime));

        // Act
        var component = context.Render<Logs>();
        var options = component.FindAll("#minimumLogLevel option");

        // Assert
        Assert.Contains(options, option => option.TextContent == "Trace");
    }

    /// <summary>
    /// Verifies that recent events render with their redacted structured details.
    /// </summary>
    [Fact]
    public void Render_WithCurrentEvents_DisplaysSafeLogDetails()
    {
        // Arrange
        using var context = new BunitContext();
        var root = Path.Combine(Path.GetTempPath(), $"lineup-logs-page-{Guid.NewGuid():N}");
        var store = new InMemoryLogEventStore();
        using var logger = new LoggerConfiguration().WriteTo.Sink(store).CreateLogger();
        logger.ForContext("SourceContext", "Lineup.Tests").ForContext("ApiKey", "hidden").Warning("Viewer event");
        var runtime = new LoggingRuntimeState(root, new FileLoggingSettings(false, LogEventLevel.Information, 7, 50), []);
        context.Services.AddSingleton<ILogEventStore>(store);
        context.Services.AddSingleton(runtime);
        context.Services.AddSingleton(new LogFileService(runtime));

        // Act
        var component = context.Render<Logs>();

        // Assert
        Assert.Contains("Viewer event", component.Markup);
        Assert.Contains("Lineup.Tests", component.Markup);
        Assert.Contains("[REDACTED]", component.Markup);
        Assert.DoesNotContain("hidden", component.Markup);
        Assert.Contains("File logging is disabled", component.Markup);
    }

    /// <summary>
    /// Verifies that the viewer presents a clear empty state.
    /// </summary>
    [Fact]
    public void Render_WithoutEvents_DisplaysEmptyState()
    {
        // Arrange
        using var context = new BunitContext();
        var root = Path.Combine(Path.GetTempPath(), $"lineup-logs-page-{Guid.NewGuid():N}");
        var store = new InMemoryLogEventStore();
        var runtime = new LoggingRuntimeState(root, new FileLoggingSettings(false, LogEventLevel.Information, 7, 50), []);
        context.Services.AddSingleton<ILogEventStore>(store);
        context.Services.AddSingleton(runtime);
        context.Services.AddSingleton(new LogFileService(runtime));

        // Act
        var component = context.Render<Logs>();

        // Assert
        Assert.Equal("No log events match the current filters.", component.Find("#emptyLogs").TextContent);
    }

    /// <summary>
    /// Verifies that clearing the viewer removes all retained in-memory events.
    /// </summary>
    [Fact]
    public void Clear_WithCurrentEvents_RemovesInMemoryHistory()
    {
        // Arrange
        using var context = new BunitContext();
        var root = Path.Combine(Path.GetTempPath(), $"lineup-logs-page-{Guid.NewGuid():N}");
        var store = new InMemoryLogEventStore();
        using var logger = new LoggerConfiguration().WriteTo.Sink(store).CreateLogger();
        logger.Information("Clear this event");
        var runtime = new LoggingRuntimeState(root, new FileLoggingSettings(false, LogEventLevel.Information, 7, 50), []);
        context.Services.AddSingleton<ILogEventStore>(store);
        context.Services.AddSingleton(runtime);
        context.Services.AddSingleton(new LogFileService(runtime));
        var component = context.Render<Logs>();

        // Act
        component.Find("#clearLogs").Click();

        // Assert
        Assert.Equal("No log events match the current filters.", component.Find("#emptyLogs").TextContent);
        Assert.Empty(store.GetEvents(LogEventLevel.Debug, null, null, 500));
    }
}
