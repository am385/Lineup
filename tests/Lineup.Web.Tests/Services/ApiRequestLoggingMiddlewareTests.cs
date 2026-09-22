using Lineup.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>
/// Verifies centralized API request logging.
/// </summary>
public class ApiRequestLoggingMiddlewareTests
{
    /// <summary>
    /// Verifies minimal API routes log request entry and completion.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_ApiPath_LogsRequestAndResponse()
    {
        // Arrange
        var logger = new RecordingLogger<ApiRequestLoggingMiddleware>();
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/v1/status";
        var middleware = new ApiRequestLoggingMiddleware(
            nextContext =>
            {
                nextContext.Response.StatusCode = StatusCodes.Status204NoContent;
                return Task.CompletedTask;
            },
            logger);

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        Assert.Collection(
            logger.Entries,
            entry =>
            {
                Assert.Equal(LogLevel.Debug, entry.Level);
                Assert.Equal("API endpoint hit: GET /api/v1/status", entry.Message);
            },
            entry =>
            {
                Assert.Equal(LogLevel.Debug, entry.Level);
                Assert.StartsWith("API endpoint completed: GET /api/v1/status returned 204 in ", entry.Message, StringComparison.Ordinal);
            });
    }

    /// <summary>
    /// Verifies root-level HDHomeRun API routes are recognized through controller metadata.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_ApiControllerRoute_LogsRequest()
    {
        // Arrange
        var logger = new RecordingLogger<ApiRequestLoggingMiddleware>();
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/discover.json";
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(new ApiControllerAttribute()),
            "HDHomeRun discovery"));
        var middleware = new ApiRequestLoggingMiddleware(_ => Task.CompletedTask, logger);

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Debug && entry.Message == "API endpoint hit: GET /discover.json");
    }

    /// <summary>
    /// Verifies high-frequency HLS file requests and expected missing segments use trace logging.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_HlsFileNotFound_LogsAtTrace()
    {
        // Arrange
        var logger = new RecordingLogger<ApiRequestLoggingMiddleware>();
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/stream/hls/session/segment001.ts";
        var middleware = new ApiRequestLoggingMiddleware(
            nextContext =>
            {
                nextContext.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            },
            logger);

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        Assert.All(logger.Entries, entry => Assert.Equal(LogLevel.Trace, entry.Level));
    }

    /// <summary>
    /// Verifies actionable client failures are promoted to warning while the request hit remains debug.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_ClientError_LogsCompletionAtWarning()
    {
        // Arrange
        var logger = new RecordingLogger<ApiRequestLoggingMiddleware>();
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/xmltv";
        var middleware = new ApiRequestLoggingMiddleware(
            nextContext =>
            {
                nextContext.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            },
            logger);

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        Assert.Equal([LogLevel.Debug, LogLevel.Warning], logger.Entries.Select(entry => entry.Level));
    }

    /// <summary>
    /// Verifies unhandled API exceptions are logged as errors and continue through exception handling.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_UnhandledException_LogsErrorAndRethrows()
    {
        // Arrange
        var logger = new RecordingLogger<ApiRequestLoggingMiddleware>();
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/stream/hls/start/7.1";
        var middleware = new ApiRequestLoggingMiddleware(_ => throw new InvalidOperationException("Failure"), logger);

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context));

        // Assert
        Assert.Equal("Failure", exception.Message);
        Assert.Equal([LogLevel.Debug, LogLevel.Error], logger.Entries.Select(entry => entry.Level));
    }

    /// <summary>
    /// Verifies UI routes do not produce API request log messages.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_UiRoute_DoesNotLog()
    {
        // Arrange
        var logger = new RecordingLogger<ApiRequestLoggingMiddleware>();
        var context = new DefaultHttpContext();
        context.Request.Path = "/dashboard";
        var middleware = new ApiRequestLoggingMiddleware(_ => Task.CompletedTask, logger);

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        Assert.Empty(logger.Entries);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message);
}
