using Lineup.Core.Common.Guides;
using Lineup.Core.Extensibility.Guides;
using Lineup.Core.Settings;
using Lineup.Core.Common.Streaming;
using Lineup.Core.Extensibility.Streaming;
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
    private readonly IStreamingAdapterRegistry _adapters;
    private readonly TimeProvider _timeProvider;

    // Used to cancel the current delay when settings change
    private CancellationTokenSource? _delayCts;
    private readonly Lock _delayLock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="EpgAutoFetchService"/> class.
    /// </summary>
    public EpgAutoFetchService(
        IServiceScopeFactory scopeFactory,
        ILogger<EpgAutoFetchService> logger,
        IAutoFetchStateService stateService,
        ILineupSettingsService settingsService,
        IStreamingAdapterRegistry adapters,
        TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _stateService = stateService;
        _settingsService = settingsService;
        _adapters = adapters;
        _timeProvider = timeProvider ?? TimeProvider.System;

        // Subscribe to settings changes
        _settingsService.OnSettingsChanged += OnSettingsChanged;
    }

    private bool IsEnabled => _settingsService.Settings.IsAutoFetchEnabled;

    /// <inheritdoc />
    public override void Dispose()
    {
        _settingsService.OnSettingsChanged -= OnSettingsChanged;
        base.Dispose();
        lock (_delayLock)
        {
            _delayCts?.Dispose();
            _delayCts = null;
        }
    }

    private void OnSettingsChanged()
    {
        var nextDelay = CalculateScheduledDelay();
        _logger.LogDebug("Settings changed. Automatic guide refresh enabled: {Enabled}", IsEnabled);

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
        _logger.LogInformation("Guide refresh and publication retry service started. Automatic source refresh enabled: {Enabled}", IsEnabled);

        // Calculate initial delay based on persisted last fetch time
        var initialDelay = CalculateInitialDelay();
        _stateService.UpdateSchedule(IsEnabled, IsEnabled ? initialDelay : null);

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
                var now = _timeProvider.GetUtcNow().UtcDateTime;
                var retryAt = _settingsService.Settings.NextAutoFetchTime;
                if (retryAt is null || retryAt <= now)
                {
                    await _settingsService.UpdateAsync(settings => settings.NextAutoFetchTime = now.AddHours(1));
                }
                _stateService.UpdateSchedule(IsEnabled, CalculateScheduledDelay());
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
    /// Calculates the initial delay from persisted source schedules.
    /// </summary>
    private TimeSpan CalculateInitialDelay()
    {
        if (!IsEnabled)
        {
            return TimeSpan.FromSeconds(10);
        }

        return CalculateScheduledDelay();
    }

    private TimeSpan CalculateScheduledDelay()
    {
        if (!IsEnabled)
        {
            return TimeSpan.FromSeconds(10);
        }

        var settings = _settingsService.Settings;
        var dates = new List<DateTime>();
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        foreach (var source in settings.Streaming.GuideSources.Where(source => source.Enabled))
        {
            dates.Add(settings.GuideSourceSchedules.TryGetValue(source.Id.Value, out var schedule) && schedule.ConfigurationRevision == GetSourceRevision(source)
                ? schedule.NextRefreshUtc
                : settings.GuideSourceSchedules.Count == 0 ? settings.NextAutoFetchTime ?? now : now);
        }
        if (dates.Count == 0 && settings.NextAutoFetchTime is { } next)
        {
            dates.Add(next);
        }
        var remaining = dates.Count == 0 ? TimeSpan.Zero : dates.Min() - now;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.FromSeconds(30);
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
            await Task.Delay(delay, _timeProvider, delayCts.Token);
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

        var settings = _settingsService.Settings;
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var sources = settings.Streaming.GuideSources.Where(source => source.Enabled && IsEnabled).ToArray();
        var due = sources.Where(source => !settings.GuideSourceSchedules.TryGetValue(source.Id.Value, out var schedule) ||
            schedule.ConfigurationRevision != GetSourceRevision(source) || schedule.NextRefreshUtc <= now).ToArray();

        if (due.Length > 0)
        {
            await _settingsService.UpdateAsync(current =>
            {
                foreach (var source in due)
                {
                    current.GuideSourceSchedules[source.Id.Value] = new GuideSourceRefreshSchedule
                    {
                        NextRefreshUtc = now.AddHours(1),
                        ConfigurationRevision = GetSourceRevision(source)
                    };
                }
            });
        }

        if (due.Length > 0 && settings.RefreshChannelsBeforeGuideFetch)
        {
            _logger.LogInformation("Refreshing configured physical tuner channels before the automatic guide fetch");
            await scope.ServiceProvider.GetRequiredService<IPhysicalLineupService>()
                .RefreshAsync(_settingsService.Settings.Streaming, stoppingToken);
        }

        var result = due.Length == 0
            ? new TopologyGuideRefreshResult { Sources = [] }
            : await guides.RefreshSourcesAsync(settings.Streaming, due.Select(source => source.Id).ToHashSet(), false, stoppingToken);
        var completedAt = _timeProvider.GetUtcNow().UtcDateTime;
        var schedules = new Dictionary<Guid, GuideSourceRefreshSchedule>();
        foreach (var source in due)
        {
            var sourceResult = result.Sources.Single(candidate => candidate.SourceId == source.Id);
            var interval = sourceResult.Succeeded
                ? (_adapters.GetGuide(source.Adapter.Kind) as IGuideSourceRefreshPolicy)?.GetRefreshInterval(settings.AutoFetchInterval) ?? settings.AutoFetchInterval
                : TimeSpan.FromHours(1);
            if (interval <= TimeSpan.Zero)
            {
                throw new InvalidOperationException("The guide source returned a nonpositive automatic refresh interval.");
            }
            schedules[source.Id.Value] = new GuideSourceRefreshSchedule
            {
                NextRefreshUtc = completedAt.Add(interval),
                ConfigurationRevision = GetSourceRevision(source)
            };
        }
        await _settingsService.UpdateAsync(current =>
        {
            foreach (var schedule in schedules)
            {
                current.GuideSourceSchedules[schedule.Key] = schedule.Value;
            }
        });
        var outputs = due.Length > 0 && _settingsService.Settings.AutoGenerateXmltv
            ? await guides.PublishAsync(stoppingToken)
            : [];
        result = result with { Outputs = outputs };
        var failedOutputs = result.Outputs.Count(output => !output.Succeeded);
        var partialOutputs = result.Outputs.Count(output => output.Succeeded && output.IsPartial);
        await _settingsService.UpdateAsync(current =>
        {
            var dates = sources.Select(source => current.GuideSourceSchedules.TryGetValue(source.Id.Value, out var schedule)
                ? schedule.NextRefreshUtc : completedAt.AddHours(1)).ToList();
            current.NextAutoFetchTime = dates.Count > 0 ? dates.Min() : completedAt.Add(settings.AutoFetchInterval > TimeSpan.Zero ? settings.AutoFetchInterval : TimeSpan.FromHours(1));
            if (due.Length > 0 && result.FailedSourceCount == 0 && failedOutputs == 0 && partialOutputs == 0)
            {
                current.LastAutoFetchTime = completedAt;
            }
        });
        if (result.FailedSourceCount > 0 || failedOutputs > 0 || partialOutputs > 0)
        {
            throw new InvalidOperationException(
                $"{result.FailedSourceCount} configured guide source(s) failed to refresh; {failedOutputs} configured guide output(s) failed to publish; {partialOutputs} published partial guides.");
        }

        var nextInterval = CalculateScheduledDelay();
        _stateService.CompleteFetch(nextInterval);

        _logger.LogInformation("Automatic EPG fetch completed. Next fetch at {NextFetch:HH:mm:ss}", _stateService.NextFetchTime);
    }

    private static string GetSourceRevision(GuideSourceDefinition source) => ConfigurationRevision.Create(source, nameof(GuideSourceDefinition.Name));
}
