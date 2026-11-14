using Lineup.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Lineup.Core.Hosting;

/// <summary>Runs independent cached-history maintenance and publication without contacting source providers.</summary>
public sealed class GuideHistoryMaintenanceService(IServiceScopeFactory scopes, ILogger<GuideHistoryMaintenanceService> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<DeviceGuideStore>();
                var guides = scope.ServiceProvider.GetRequiredService<ITopologyGuideService>();
                var outputs = await guides.ApplyChannelChangeAsync(async token =>
                {
                    var removed = await store.PruneExpiredProgramsAsync(token);
                    logger.LogInformation("Guide history maintenance removed {ProgramCount} expired programs", removed);
                }, stoppingToken);
                foreach (var output in outputs.Where(output => !output.Succeeded || output.IsPartial))
                {
                    logger.LogWarning("Guide history publication for {OutputId} failed or is partial; a cached publication retry is pending", output.OutputId);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Guide history maintenance failed; it will be retried on the next maintenance pass");
            }
            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }
}
