using Lineup.Core;

namespace Lineup.Web.Services;

/// <summary>
/// Background service that periodically fetches EPG data.
/// </summary>
public class EpgAutoFetchService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EpgAutoFetchService> _logger;
    private readonly IAutoFetchStateService _stateService;
    private readonly IAppSettingsService _settingsService;

    // Used to cancel the current delay when settings change
    private CancellationTokenSource? _delayCts;
    private readonly object _delayLock = new();

    public EpgAutoFetchService(
        IServiceScopeFactory scopeFactory,
        ILogger<EpgAutoFetchService> logger,
        IAutoFetchStateService stateService,
        IAppSettingsService settingsService)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _stateService = stateService;
        _settingsService = settingsService;

        // Subscribe to settings changes
        _settingsService.OnSettingsChanged += OnSettingsChanged;
    }

    private TimeSpan FetchInterval => TimeSpan.FromMinutes(_settingsService.Settings.AutoFetchIntervalMinutes);
    private int TargetDays => _settingsService.Settings.TargetDays;
    private bool IsEnabled => _settingsService.Settings.IsAutoFetchEnabled;

    private void OnSettingsChanged()
    {
        _logger.LogInformation("Settings changed. Auto-fetch enabled: {Enabled}, Interval: {Interval} min",
            IsEnabled, _settingsService.Settings.AutoFetchIntervalMinutes);

        // Update the state service with new schedule
        _stateService.UpdateSchedule(IsEnabled, IsEnabled ? FetchInterval : null);

        // Cancel the current delay to apply new settings immediately
        lock (_delayLock)
        {
            _delayCts?.Cancel();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("EPG Auto-Fetch Service started. Interval: {Interval} min, Target: {Days} days, Enabled: {Enabled}",
            _settingsService.Settings.AutoFetchIntervalMinutes, TargetDays, IsEnabled);

        // Calculate initial delay based on persisted last fetch time
        var initialDelay = CalculateInitialDelay();
        _stateService.UpdateSchedule(IsEnabled, initialDelay);

        _logger.LogInformation("Next auto-fetch in {Delay}", initialDelay);
        await SafeDelayAsync(initialDelay, stoppingToken);

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
            }

            // Schedule and wait for next fetch
            if (IsEnabled)
            {
                _stateService.UpdateSchedule(true, FetchInterval);
                await SafeDelayAsync(FetchInterval, stoppingToken);
            }
            else
            {
                // When disabled, check periodically if re-enabled
                await SafeDelayAsync(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }

        _settingsService.OnSettingsChanged -= OnSettingsChanged;
        _logger.LogInformation("EPG Auto-Fetch Service stopped");
    }

    /// <summary>
    /// Calculates the initial delay before the first fetch based on persisted last fetch time.
    /// If a previous fetch happened recently enough, waits the remaining interval.
    /// Otherwise, waits 30 seconds (startup grace period).
    /// </summary>
    private TimeSpan CalculateInitialDelay()
    {
        if (!IsEnabled)
        {
            return TimeSpan.FromSeconds(10);
        }

        var lastFetch = _settingsService.Settings.LastAutoFetchTime;
        if (lastFetch.HasValue)
        {
            var elapsed = DateTime.UtcNow - lastFetch.Value;
            var remaining = FetchInterval - elapsed;

            if (remaining > TimeSpan.Zero)
            {
                _logger.LogInformation(
                    "Last auto-fetch was {Elapsed:hh\\:mm\\:ss} ago. Waiting {Remaining:hh\\:mm\\:ss} before next fetch",
                    elapsed, remaining);
                return remaining;
            }

            _logger.LogInformation("Last auto-fetch was {Elapsed:hh\\:mm\\:ss} ago (overdue). Fetching after startup delay",
                elapsed);
        }

        return TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// Delays for the specified time, but can be cancelled early when settings change.
    /// </summary>
    private async Task SafeDelayAsync(TimeSpan delay, CancellationToken stoppingToken)
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
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            // Settings changed, delay was cancelled - this is expected
            _logger.LogDebug("Delay cancelled due to settings change");
        }
    }

    private async Task FetchEpgDataAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting automatic EPG fetch (Target: {Days} days)...", TargetDays);
        _stateService.StartFetch();

        using var scope = _scopeFactory.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<EpgOrchestrator>();

        var progress = new Progress<FetchProgressInfo>(info =>
        {
            _stateService.UpdateProgress(info);

            if (info.Status == FetchStatus.Completed || info.Status == FetchStatus.Failed)
            {
                _logger.LogInformation("Auto-fetch: {Message}", info.Message);
            }
        });

        await orchestrator.FetchAndStoreEpgAsync(TargetDays, force: false, progress);

        // Auto-generate XMLTV file if enabled
        if (_settingsService.Settings.AutoGenerateXmltv)
        {
            var outputPath = _settingsService.Settings.XmltvOutputPath;
            _logger.LogInformation("Auto-generating XMLTV file to {OutputPath}...", outputPath);
            
            try
            {
                // Ensure directory exists if path contains directories
                var directory = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                
                await orchestrator.GenerateEpgFromCacheAsync(TargetDays, outputPath);
                _logger.LogInformation("XMLTV file generated successfully to {OutputPath}", outputPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to auto-generate XMLTV file to {OutputPath}", outputPath);
            }
        }

        _stateService.CompleteFetch(FetchInterval);

        // Persist the last fetch time so we can resume correctly after restart
        await _settingsService.UpdateAsync(s => s.LastAutoFetchTime = DateTime.UtcNow);

        _logger.LogInformation("Automatic EPG fetch completed. Next fetch at {NextFetch:HH:mm:ss}",
            _stateService.NextFetchTime);
    }
}
