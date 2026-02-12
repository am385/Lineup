using Lineup.HDHomeRun.Api;
using Lineup.HDHomeRun.Api.Models;
using Lineup.HDHomeRun.Device.Models;
using Lineup.Core.Models;
using Microsoft.Extensions.Logging;

namespace Lineup.Core.Storage;

/// <summary>
/// Provides EPG data using local cache with fallback to API.
/// Stores raw API data and enriches it with device channel info when reading.
/// </summary>
public class CachedEpgDataProvider : IEpgDataProvider
{
    private readonly ILogger<CachedEpgDataProvider> _logger;
    private readonly HDHomeRunApiClient _apiClient;
    private readonly IEpgRepository _repository;

    public CachedEpgDataProvider(
        ILogger<CachedEpgDataProvider> logger,
        HDHomeRunApiClient apiClient,
        IEpgRepository repository)
    {
        _logger = logger;
        _apiClient = apiClient;
        _repository = repository;
    }

    public async Task<HDHomeRunEpgData> GetEnrichedEpgDataAsync(
        List<HDHomeRunChannel> deviceChannels,
        DateTime startTimeUtc,
        DateTime endTimeUtc)
    {
        await _repository.EnsureDatabaseCreatedAsync();

        // Get raw cached data
        var rawSegments = await _repository.GetRawEpgDataAsync(startTimeUtc, endTimeUtc);

        if (rawSegments.Count == 0)
        {
            _logger.LogWarning("No cached data available for range {Start} to {End}", startTimeUtc, endTimeUtc);
            return new HDHomeRunEpgData();
        }

        // Enrich with device channel data
        return EnrichEpgData(rawSegments, deviceChannels);
    }

    public async Task<List<HDHomeRunChannelEpgSegment>> GetCachedRawDataAsync(DateTime? startTimeUtc = null, DateTime? endTimeUtc = null)
    {
        await _repository.EnsureDatabaseCreatedAsync();
        return await _repository.GetRawEpgDataAsync(startTimeUtc, endTimeUtc);
    }

    public async Task<List<HDHomeRunChannelEpgSegment>> FetchAndStoreRawDataAsync(
        int targetDays,
        bool force = false,
        IProgress<FetchProgressInfo>? progress = null)
    {
        await _repository.EnsureDatabaseCreatedAsync();

        var context = new FetchContext(targetDays, force, DateTime.UtcNow);

        _logger.LogInformation("Starting EPG fetch. Target: {TargetDays} days of data (until {TargetEnd:yyyy-MM-dd HH:mm} UTC){ForceNote}",
            targetDays, context.TargetEndTime, force ? " [FORCE MODE]" : "");

        var progressReporter = new FetchProgressReporter(progress, context.TargetEndTime);

        progressReporter.Report(FetchStatus.Initializing, "Initializing EPG fetch...", context);

        while (true)
        {
            context.FetchCount++;

            var startTime = await GetNextStartTimeAsync(context);

            // Exit condition: Target reached
            if (startTime >= context.TargetEndTime)
            {
                _logger.LogInformation("Target reached: data extends to {StartTime:yyyy-MM-dd HH:mm} UTC", startTime);
                context.FetchCount--;
                progressReporter.Report(FetchStatus.Completed, "Target reached - sufficient data collected", context, startTime);
                break;
            }

            _logger.LogInformation("Fetch #{FetchCount}: Requesting data from {StartTime:yyyy-MM-dd HH:mm} UTC", context.FetchCount, startTime);
            progressReporter.Report(FetchStatus.Fetching, $"Fetch #{context.FetchCount}: Requesting data from {startTime:MM/dd HH:mm} UTC...", context, startTime);

            var previousEndTime = context.Force ? context.SessionEndTime : await _repository.GetSafeFetchStartTimeAsync();

            // Fetch from API
            var (rawSegments, error) = await FetchFromApiAsync(startTime);

            if (error != null)
            {
                _logger.LogWarning(error, "API call failed on fetch #{FetchCount}. Stopping.", context.FetchCount);
                progressReporter.Report(FetchStatus.Failed, $"API call failed on fetch #{context.FetchCount}", context, error: error.Message);
                break;
            }

            // Exit condition: No data returned
            if (rawSegments.Count == 0)
            {
                _logger.LogInformation("API returned no data on fetch #{FetchCount}. Stopping.", context.FetchCount);
                progressReporter.Report(FetchStatus.Completed, "API returned no additional data", context);
                break;
            }

            // Store the fetched data
            var storeResult = await StoreAndUpdateContextAsync(rawSegments, startTime, context);
            progressReporter.Report(FetchStatus.Storing, $"Fetch #{context.FetchCount}: Storing {storeResult.ChannelCount} channels, {storeResult.ProgramCount:N0} programs...", context);

            _logger.LogInformation("Fetch #{FetchCount}: Stored {ChannelCount} channels and {ProgramCount} programs (ends {EndTime:yyyy-MM-dd HH:mm} UTC)",
                context.FetchCount, storeResult.ChannelCount, storeResult.ProgramCount, storeResult.ActualEndTime);

            // Exit condition: No new data (end time didn't advance)
            var newEndTime = context.Force ? context.SessionEndTime : await _repository.GetSafeFetchStartTimeAsync();
            if (previousEndTime.HasValue && newEndTime.HasValue && newEndTime.Value <= previousEndTime.Value)
            {
                _logger.LogInformation("No new data on fetch #{FetchCount}. Stopping.", context.FetchCount);
                progressReporter.Report(FetchStatus.Completed, "No new data available from API", context, newEndTime);
                break;
            }

            progressReporter.Report(FetchStatus.Fetching, $"Fetch #{context.FetchCount} complete. Data extends to {(newEndTime ?? storeResult.ActualEndTime):MM/dd HH:mm} UTC", context, newEndTime ?? storeResult.ActualEndTime);

            await Task.Delay(TimeSpan.FromMilliseconds(500)); // Rate limiting
        }

        _logger.LogInformation("EPG fetch completed. Total: {FetchCount} API calls, {ProgramCount} programs", context.FetchCount, context.TotalProgramsFetched);

        var finalEndTime = context.Force ? context.SessionEndTime : await _repository.GetSafeFetchStartTimeAsync();
        progressReporter.Report(FetchStatus.Completed, $"Completed: {context.FetchCount} fetches, {context.TotalProgramsFetched:N0} programs", context, finalEndTime);

        return context.AllFetchedSegments;
    }

    private async Task<DateTime> GetNextStartTimeAsync(FetchContext context)
    {
        if (context.Force)
        {
            if (context.FetchCount == 1)
            {
                _logger.LogInformation("Force mode: starting fresh from current time");
            }
            return context.SessionEndTime ?? context.Now;
        }

        return await DetermineNextFetchStartTimeAsync(context.Now);
    }

    private async Task<(List<HDHomeRunChannelEpgSegment> Segments, Exception? Error)> FetchFromApiAsync(DateTime startTime)
    {
        try
        {
            // Request 24 hours of data per call to reduce API calls (max allowed by API)
            var segments = await _apiClient.FetchRawEpgSegmentAsync(startTimeUtc: startTime, durationHours: 24);
            return (segments, null);
        }
        catch (Exception ex)
        {
            return ([], ex);
        }
    }

    private async Task<StoreResult> StoreAndUpdateContextAsync(List<HDHomeRunChannelEpgSegment> segments, DateTime startTime, FetchContext context)
    {
        await StoreRawDataAsync(segments);
        context.AllFetchedSegments.AddRange(segments);

        var programCount = segments.Sum(s => s.Guide.Count);
        context.TotalProgramsFetched += programCount;
        context.TotalChannelsFetched = context.AllFetchedSegments.Select(s => s.GuideNumber).Distinct().Count();

        var actualEndTime = CalculateActualEndTime(segments, startTime);
        var minEndTime = CalculateMinimumEndTime(segments, startTime);

        if (context.Force)
        {
            context.SessionEndTime = minEndTime;
            _logger.LogDebug("Force mode: session end time updated to {EndTime:yyyy-MM-dd HH:mm} UTC", minEndTime);
        }

        return new StoreResult(segments.Count, programCount, actualEndTime);
    }

    private record StoreResult(int ChannelCount, int ProgramCount, DateTime ActualEndTime);

    private sealed class FetchContext(int targetDays, bool force, DateTime now)
    {
        public DateTime Now { get; } = now;
        public DateTime TargetEndTime { get; } = now.AddDays(targetDays);
        public bool Force { get; } = force;
        public List<HDHomeRunChannelEpgSegment> AllFetchedSegments { get; } = [];
        public int TotalProgramsFetched { get; set; }
        public int TotalChannelsFetched { get; set; }
        public int FetchCount { get; set; }
        public DateTime? SessionEndTime { get; set; }
    }

    private sealed class FetchProgressReporter(IProgress<FetchProgressInfo>? progress, DateTime targetEndTime)
    {
        private FetchProgressInfo _current = new()
        {
            Status = FetchStatus.Initializing,
            TargetEndTime = targetEndTime,
            Message = ""
        };

        public void Report(FetchStatus status, string message, FetchContext context, DateTime? endTime = null, string? error = null)
        {
            _current = _current with
            {
                Status = status,
                FetchCount = context.FetchCount,
                TotalProgramsFetched = context.TotalProgramsFetched,
                TotalChannelsFetched = context.TotalChannelsFetched,
                CurrentEndTime = endTime ?? _current.CurrentEndTime,
                Message = message,
                ErrorMessage = error
            };
            progress?.Report(_current);
        }
    }

    /// <summary>
    /// Determines the optimal start time for the next fetch iteration (non-force mode).
    /// </summary>
    private async Task<DateTime> DetermineNextFetchStartTimeAsync(DateTime now)
    {
        // Smart mode: start from the safe fetch point (earliest channel gap)
        var safeFetchStart = await _repository.GetSafeFetchStartTimeAsync();
        var absoluteLatest = await _repository.GetLatestProgramEndTimeAsync();

        if (safeFetchStart.HasValue && safeFetchStart.Value > now)
        {
            // We have future data - start from the safe point (earliest channel gap)
            if (absoluteLatest.HasValue && absoluteLatest.Value > safeFetchStart.Value)
            {
                var gap = absoluteLatest.Value - safeFetchStart.Value;
                _logger.LogDebug(
                    "Fetching from {SafeStart:yyyy-MM-dd HH:mm} UTC (some channels end at {Latest:yyyy-MM-dd HH:mm} UTC, gap: {Gap:h\\:mm})",
                    safeFetchStart.Value, absoluteLatest.Value, gap);
            }

            return safeFetchStart.Value;
        }

        // No future data or data is stale - start from now
        if (safeFetchStart.HasValue)
        {
            _logger.LogDebug("Cached data ends at {SafeStart:yyyy-MM-dd HH:mm} UTC (in the past), fetching from now", safeFetchStart.Value);
        }

        return now;
    }

    /// <summary>
    /// Calculates the minimum end time across all channels (safe point for next fetch).
    /// This ensures no channel is left behind when progressing.
    /// </summary>
    private static DateTime CalculateMinimumEndTime(List<HDHomeRunChannelEpgSegment> segments, DateTime fallbackStart)
    {
        if (segments.Count == 0)
        {
            return fallbackStart;
        }

        // For each channel, find the max end time of its programs, then take the minimum across all channels
        var channelEndTimes = segments
            .Where(s => s.Guide.Count > 0)
            .Select(s => s.Guide.Max(p => p.EndTime))
            .ToList();

        if (channelEndTimes.Count == 0)
        {
            return fallbackStart;
        }

        var minEndTime = channelEndTimes.Min();
        return DateTimeOffset.FromUnixTimeSeconds(minEndTime).UtcDateTime;
    }

    /// <summary>
    /// Calculates the actual end time from the returned program data
    /// </summary>
    private static DateTime CalculateActualEndTime(List<HDHomeRunChannelEpgSegment> segments, DateTime fallbackStart)
    {
        if (segments.Count == 0)
        {
            return fallbackStart;
        }

        // Find the maximum end time across all programs
        var maxEndTime = segments
            .Where(s => s.Guide.Count > 0)
            .SelectMany(s => s.Guide)
            .Select(p => p.EndTime)
            .DefaultIfEmpty(new DateTimeOffset(fallbackStart).ToUnixTimeSeconds())
            .Max();

        return DateTimeOffset.FromUnixTimeSeconds(maxEndTime).UtcDateTime;
    }

    public async Task CleanupOldDataAsync()
    {
        await _repository.EnsureDatabaseCreatedAsync();

        // Remove programs that ended before the start of yesterday (keep last full day)
        var startOfYesterday = DateTime.UtcNow.Date.AddDays(-1);
        await _repository.CleanupOldProgramsAsync(startOfYesterday);
    }

    /// <summary>
    /// Enriches raw API data with device channel information.
    /// Filters to only include channels that are in the device lineup.
    /// </summary>
    private HDHomeRunEpgData EnrichEpgData(List<HDHomeRunChannelEpgSegment> rawSegments, List<HDHomeRunChannel> deviceChannels)
    {
        var epgData = new HDHomeRunEpgData();

        _logger.LogDebug("Enriching {SegmentCount} raw segments with {DeviceChannelCount} device channels",
            rawSegments.Count, deviceChannels.Count);

        foreach (var segment in rawSegments)
        {
            // Find matching device channel
            var deviceChannel = deviceChannels.FirstOrDefault(dc => dc.GuideNumber == segment.GuideNumber);

            if (deviceChannel == null)
            {
                _logger.LogDebug("Skipping channel {GuideNumber} - not in device lineup", segment.GuideNumber);
                continue;
            }

            // Create enriched channel combining device and API data
            var enrichedChannel = new HDHomeRunEnrichedChannel
            {
                GuideNumber = deviceChannel.GuideNumber,
                GuideName = deviceChannel.GuideName ?? segment.GuideName,
                Affiliate = segment.Affiliate,
                ImageURL = segment.ImageURL ?? ""
            };
            epgData.Channels.Add(enrichedChannel);

            // Add programs for this channel
            foreach (var program in segment.Guide)
            {
                var enrichedProgram = program with { GuideNumber = segment.GuideNumber };
                epgData.Programs.Add(enrichedProgram);
            }
        }

        _logger.LogInformation("Enriched EPG data: {ChannelCount} channels, {ProgramCount} programs",
            epgData.Channels.Count, epgData.Programs.Count);

        return epgData;
    }

    private async Task StoreRawDataAsync(List<HDHomeRunChannelEpgSegment> segments)
    {
        await _repository.StoreRawSegmentAsync(segments);
        _logger.LogDebug("Stored raw EPG data: {ChannelCount} channels", segments.Count);
    }
}
