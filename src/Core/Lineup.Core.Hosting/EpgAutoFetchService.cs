using Lineup.Common.Guides;
using Lineup.Extensibility.Guides;
using Lineup.Core.Settings;
using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lineup.Core.Hosting;

/// <summary>
/// Background service that periodically fetches EPG data.
/// </summary>
public class EpgAutoFetchService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EpgAutoFetchService> _logger;
    private readonly IAutoFetchStateService _stateService;
    private readonly ILineupSettingsService _settingsService;

    // Used to cancel the current delay when settings change
    private CancellationTokenSource? _delayCts;
    private readonly object _delayLock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="EpgAutoFetchService"/> class.
    /// </summary>
    public EpgAutoFetchService(IServiceScopeFactory scopeFactory, ILogger<EpgAutoFetchService> logger, IAutoFetchStateService stateService, ILineupSettingsService settingsService)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _stateService = stateService;
        _settingsService = settingsService;

        // Subscribe to settings changes
        _settingsService.OnSettingsChanged += OnSettingsChanged;
    }

    private bool IsEnabled => _settingsService.Settings.IsAutoFetchEnabled;

    private void OnSettingsChanged()
    {
        var nextDelay = CalculateScheduledDelay();
        _logger.LogInformation("Settings changed. Automatic XMLTV refresh enabled: {Enabled}", IsEnabled);

        // Update the state service with new schedule
        _stateService.UpdateSchedule(IsEnabled, IsEnabled ? nextDelay : null);

        // Cancel the current delay to apply new settings immediately
        lock (_delayLock)
        {
            _delayCts?.Cancel();
        }
    }

    /// <summary>
    /// Performs the execute operation.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("EPG Auto-Fetch Service started. Randomized 20-28 hour scheduling enabled: {Enabled}", IsEnabled);

        // Calculate initial delay based on persisted last fetch time
        var initialDelay = CalculateInitialDelay();
        _stateService.UpdateSchedule(IsEnabled, initialDelay);

        _logger.LogInformation("Next auto-fetch in {Delay}", initialDelay);
        while (!stoppingToken.IsCancellationRequested && !await SafeDelayAsync(initialDelay, stoppingToken))
        {
            initialDelay = CalculateScheduledDelay();
            _stateService.UpdateSchedule(IsEnabled, IsEnabled ? initialDelay : null);
            _logger.LogDebug("Restarting the initial automatic XMLTV refresh delay");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (IsEnabled)
                {
                    await FetchEpgDataAsync(stoppingToken);
                }
                else
                {
                    _logger.LogDebug("Auto-fetch is disabled, skipping");
                    _stateService.UpdateSchedule(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during automatic EPG fetch");
                _stateService.FailFetch(ex.Message);
                var retryDelay = TimeSpan.FromHours(1);
                _stateService.UpdateSchedule(true, retryDelay);
                await _settingsService.UpdateAsync(settings => settings.NextAutoFetchTime = DateTime.UtcNow.Add(retryDelay));
            }

            // Wait for the next fetch interval, restarting the delay if settings change
            while (!stoppingToken.IsCancellationRequested)
            {
                if (IsEnabled)
                {
                    var nextDelay = CalculateScheduledDelay();
                    _stateService.UpdateSchedule(true, nextDelay);
                    if (await SafeDelayAsync(nextDelay, stoppingToken))
                    {
                        break; // Delay completed normally, proceed to fetch
                    }
                    // Settings changed - loop back to re-evaluate and start a new delay
                    _logger.LogDebug("Restarting the automatic XMLTV refresh delay");
                }
                else
                {
                    // When disabled, check periodically if re-enabled
                    await SafeDelayAsync(TimeSpan.FromSeconds(10), stoppingToken);
                }
            }
        }

        _settingsService.OnSettingsChanged -= OnSettingsChanged;
        _logger.LogInformation("EPG Auto-Fetch Service stopped");
    }

    /// <summary>
    /// Calculates the initial delay from the persisted randomized schedule.
    /// </summary>
    private TimeSpan CalculateInitialDelay()
    {
        if (!IsEnabled)
        {
            return TimeSpan.FromSeconds(10);
        }

        var nextFetch = _settingsService.Settings.NextAutoFetchTime;
        if (nextFetch.HasValue)
        {
            var remaining = nextFetch.Value - DateTime.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                _logger.LogInformation("Waiting {Remaining} for the persisted XMLTV refresh time", remaining);
                return remaining;
            }
        }

        return TimeSpan.FromSeconds(30);
    }

    private TimeSpan CalculateScheduledDelay()
    {
        if (!IsEnabled)
        {
            return TimeSpan.FromSeconds(10);
        }

        var remaining = _settingsService.Settings.NextAutoFetchTime - DateTime.UtcNow;
        return remaining > TimeSpan.Zero ? remaining.Value : TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// Delays for the specified time, but can be canceled early when settings change.
    /// Returns true if the delay completed normally, false if canceled by a settings change.
    /// </summary>
    private async Task<bool> SafeDelayAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        CancellationTokenSource delayCts;

        lock (_delayLock)
        {
            _delayCts?.Dispose();
            _delayCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            delayCts = _delayCts;
        }

        try
        {
            await Task.Delay(delay, delayCts.Token);
            return true;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return true;
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            // Settings changed, delay was canceled - this is expected
            _logger.LogDebug("Delay canceled due to settings change");
            return false;
        }
    }

    private async Task FetchEpgDataAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting automatic guide refresh...");
        _stateService.StartFetch();

        using var scope = _scopeFactory.CreateScope();
        var guides = scope.ServiceProvider.GetRequiredService<ITopologyGuideService>();
        _stateService.UpdateProgress(new FetchProgressInfo
        {
            Status = FetchStatus.Fetching,
            Message = "Refreshing enabled guide sources"
        });

        if (_settingsService.Settings.RefreshChannelsBeforeGuideFetch)
        {
            _logger.LogInformation("Refreshing configured physical tuner channels before the automatic guide fetch");
            await scope.ServiceProvider.GetRequiredService<IPhysicalLineupService>()
                .RefreshAsync(_settingsService.Settings.Streaming, stoppingToken);
        }

        var result = _settingsService.Settings.AutoGenerateXmltv
            ? await guides.RefreshAndPublishAsync(stoppingToken)
            : await guides.RefreshAsync(stoppingToken);
        if (result.FailedSourceCount > 0)
        {
            throw new InvalidOperationException($"{result.FailedSourceCount} configured guide source(s) failed to refresh.");
        }

        var nextInterval = CreateRandomizedFetchInterval();
        var completedAt = DateTime.UtcNow;
        var nextFetchAt = completedAt.Add(nextInterval);
        _stateService.CompleteFetch(nextInterval);

        // Persist the selected randomized schedule so restarts do not reset it.
        await _settingsService.UpdateAsync(settings =>
        {
            settings.LastAutoFetchTime = completedAt;
            settings.NextAutoFetchTime = nextFetchAt;
        });

        _logger.LogInformation("Automatic EPG fetch completed. Next fetch at {NextFetch:HH:mm:ss}", _stateService.NextFetchTime);
    }

    private static TimeSpan CreateRandomizedFetchInterval()
    {
        return TimeSpan.FromHours(20) + TimeSpan.FromMinutes(Random.Shared.Next(0, 481));
    }
}
