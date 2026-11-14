namespace Lineup.App.Web.Services;

/// <summary>Contains inexpensive process health and separately reported external degradation.</summary>
/// <param name="Status">Stable process/readiness state.</param>
/// <param name="Ready">Whether management requests can be served normally.</param>
/// <param name="DegradedDevices">Count of cached device failures, not a liveness condition.</param>
/// <param name="GuideAgeSeconds">Age of the last successful guide fetch, when known.</param>
public sealed record OperationalHealthSnapshot(string Status, bool Ready, int DegradedDevices = 0, double? GuideAgeSeconds = null);

/// <summary>Reads lifecycle tokens and cached summaries without querying storage or contacting devices.</summary>
public sealed class OperationalHealthService : IDisposable
{
    private readonly System.Diagnostics.Metrics.Meter _metrics = new("Lineup.Host");
    private readonly LineupStartupState _startup;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly Func<(int FailedDevices, DateTime? LastGuideFetch)> _summary;

    /// <summary>Initializes a cheap health policy with a cached-summary reader.</summary>
    public OperationalHealthService(LineupStartupState startup, IHostApplicationLifetime lifetime, Func<(int FailedDevices, DateTime? LastGuideFetch)> summary)
    {
        _startup = startup;
        _lifetime = lifetime;
        _summary = summary;
        _metrics.CreateObservableGauge("lineup.guide.age", () =>
        {
            var fetched = _startup.IsFactoryResetRequired ? null : _summary().LastGuideFetch;
            return fetched is null ? [] : new[] { new System.Diagnostics.Metrics.Measurement<double>(Math.Max(0, (DateTime.UtcNow - fetched.Value.ToUniversalTime()).TotalSeconds)) };
        }, "s", "Age of the last successful guide fetch; unknown age emits no measurement.");
    }

    /// <inheritdoc />
    public void Dispose() => _metrics.Dispose();

    /// <summary>Identifies only the two dependency-independent health paths.</summary>
    public static bool IsHealthEndpoint(PathString path) => path == "/health/live" || path == "/health/ready";

    /// <summary>Gets management readiness; first-run setup and external outages remain serviceable.</summary>
    public OperationalHealthSnapshot Readiness()
    {
        if (_lifetime.ApplicationStopping.IsCancellationRequested)
        {
            return new("stopping", false);
        }
        if (_startup.IsFactoryResetRequired)
        {
            return new("factoryResetRequired", false);
        }
        if (!_lifetime.ApplicationStarted.IsCancellationRequested)
        {
            return new("starting", false);
        }
        var (failed, fetched) = _summary();
        double? age = fetched is null ? null : Math.Max(0, (DateTime.UtcNow - fetched.Value.ToUniversalTime()).TotalSeconds);
        return new(failed > 0 ? "degraded" : "ready", true, failed, age);
    }

    /// <summary>Maps independent liveness and readiness endpoints, including recovery mode.</summary>
    public static void MapEndpoints(WebApplication app)
    {
        app.MapGet("/health/live", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { status = "alive" });
        }).DisableAntiforgery().WithTags("System").WithSummary("Check process liveness without external dependencies");
        app.MapGet("/health/ready", (HttpContext context, OperationalHealthService health) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var state = health.Readiness();
            return Results.Json(state, statusCode: state.Ready ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
        }).DisableAntiforgery().WithTags("System").WithSummary("Check cached management readiness without device or guide queries");
    }
}
