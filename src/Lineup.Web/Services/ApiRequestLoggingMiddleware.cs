using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Lineup.Web.Services;

/// <summary>
/// Logs requests that target Lineup API endpoints.
/// </summary>
public sealed class ApiRequestLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ApiRequestLoggingMiddleware> _logger;

    /// <summary>
    /// Initializes API request logging middleware.
    /// </summary>
    /// <param name="next">The next request pipeline component.</param>
    /// <param name="logger">The request logger.</param>
    public ApiRequestLoggingMiddleware(RequestDelegate next, ILogger<ApiRequestLoggingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    /// <summary>
    /// Logs an API request before and after endpoint execution.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsApiRequest(context))
        {
            await _next(context);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        var requestLevel = IsHighFrequencyRequest(context) ? LogLevel.Trace : LogLevel.Debug;
        _logger.Log(requestLevel, "API endpoint hit: {Method} {Path}", context.Request.Method, context.Request.Path);

        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "API endpoint failed: {Method} {Path} after {ElapsedMilliseconds:F1} ms",
                context.Request.Method,
                context.Request.Path,
                stopwatch.Elapsed.TotalMilliseconds);
            throw;
        }

        var completionLevel = GetCompletionLevel(requestLevel, context.Response.StatusCode);
        _logger.Log(
            completionLevel,
            "API endpoint completed: {Method} {Path} returned {StatusCode} in {ElapsedMilliseconds:F1} ms",
            context.Request.Method,
            context.Request.Path,
            context.Response.StatusCode,
            stopwatch.Elapsed.TotalMilliseconds);
    }

    private static bool IsApiRequest(HttpContext context)
    {
        return context.Request.Path.StartsWithSegments("/api") ||
               context.GetEndpoint()?.Metadata.GetMetadata<ApiControllerAttribute>() != null;
    }

    private static bool IsHighFrequencyRequest(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            return false;
        }

        if (context.Request.Path.Value?.EndsWith("/subtitles.vtt", StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        return context.Request.Path.StartsWithSegments("/api/stream/hls", out var remaining) &&
               remaining.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries).Length >= 2;
    }

    private static LogLevel GetCompletionLevel(LogLevel requestLevel, int statusCode)
    {
        if (statusCode >= StatusCodes.Status500InternalServerError)
        {
            return LogLevel.Error;
        }

        if (statusCode >= StatusCodes.Status400BadRequest &&
            !(requestLevel == LogLevel.Trace && statusCode == StatusCodes.Status404NotFound))
        {
            return LogLevel.Warning;
        }

        return requestLevel;
    }
}
