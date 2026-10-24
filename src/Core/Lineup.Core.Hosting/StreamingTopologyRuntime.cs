using Lineup.Common.Guides;
using Lineup.Extensibility.Guides;
using Lineup.Core.Settings;
using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lineup.Core.Hosting;

/// <summary>
/// Applies persisted streaming topology at startup and after settings changes.
/// </summary>
public sealed class StreamingTopologyRuntime : BackgroundService
{
    private readonly ILineupSettingsService _settings;
    private readonly IPhysicalLineupService _lineups;
    private readonly ITopologyGuideService _guides;
    private readonly ILogger<StreamingTopologyRuntime> _logger;
    private readonly SemaphoreSlim _settingsChanged = new(0, 1);

    /// <summary>Initializes the hosted topology runtime.</summary>
    public StreamingTopologyRuntime(
        ILineupSettingsService settings,
        IPhysicalLineupService lineups,
        ITopologyGuideService guides,
        ILogger<StreamingTopologyRuntime> logger)
    {
        _settings = settings;
        _lineups = lineups;
        _guides = guides;
        _logger = logger;
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _settings.OnSettingsChanged += SettingsChanged;
        return base.StartAsync(cancellationToken);
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _settings.OnSettingsChanged -= SettingsChanged;
        await base.StopAsync(cancellationToken);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        do
        {
            await ApplyAsync(_settings.Settings.Streaming, stoppingToken);
            await _settingsChanged.WaitAsync(stoppingToken);
        }
        while (!stoppingToken.IsCancellationRequested);
    }

    private async Task ApplyAsync(StreamingConfiguration configuration, CancellationToken cancellationToken)
    {
        try
        {
            var results = await _lineups.RefreshAsync(configuration, cancellationToken);
            foreach (var result in results.Where(result => !result.Succeeded))
            {
                _logger.LogWarning("Physical lineup refresh failed for device {PhysicalDeviceId}: {Error}", result.DeviceId, result.Error);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unable to apply the configured physical streaming topology");
            return;
        }

        try
        {
            await _guides.PublishAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Unable to apply guide topology; physical streaming remains available");
        }
    }

    private void SettingsChanged()
    {
        try
        {
            _settingsChanged.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }
}
