namespace Lineup.Web.Services;

/// <summary>
/// Requests a graceful Lineup application restart.
/// </summary>
public interface IApplicationRestartService
{
    /// <summary>
    /// Begins graceful application shutdown so the process supervisor can restart Lineup.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request before shutdown begins.</param>
    /// <returns>A task that completes after shutdown is requested.</returns>
    Task RestartAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Stops the current host so its configured process supervisor can restart it.
/// </summary>
public sealed class ApplicationRestartService : IApplicationRestartService
{
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly ILogger<ApplicationRestartService> _logger;

    /// <summary>
    /// Initializes the application restart service.
    /// </summary>
    /// <param name="applicationLifetime">Application shutdown controller.</param>
    /// <param name="logger">Restart diagnostics logger.</param>
    public ApplicationRestartService(IHostApplicationLifetime applicationLifetime, ILogger<ApplicationRestartService> logger)
    {
        _applicationLifetime = applicationLifetime;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task RestartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _logger.LogWarning("Lineup restart requested");
        _applicationLifetime.StopApplication();
        return Task.CompletedTask;
    }
}
