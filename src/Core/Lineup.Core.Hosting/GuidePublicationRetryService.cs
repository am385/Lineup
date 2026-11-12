using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lineup.Core.Hosting;

/// <summary>Retries durable cached-guide publications independently of source downloads and automatic fetching settings.</summary>
public sealed class GuidePublicationRetryService(IServiceScopeFactory scopes, ILogger<GuidePublicationRetryService> logger, TimeProvider? timeProvider = null) : BackgroundService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(30);
            try
            {
                using var scope = scopes.CreateScope();
                var guides = scope.ServiceProvider.GetRequiredService<ITopologyGuideService>();
                var results = await guides.RetryPublicationsAsync(_clock.GetUtcNow().UtcDateTime, stoppingToken);
                foreach (var result in results.Where(result => !result.Succeeded || result.IsPartial))
                {
                    logger.LogWarning("Guide output {OutputId} failed or remains partial; cached publication will retry after one hour", result.OutputId);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Cached guide publication retry failed; retrying after one hour");
                delay = TimeSpan.FromHours(1);
            }
            await Task.Delay(delay, _clock, stoppingToken);
        }
    }
}
