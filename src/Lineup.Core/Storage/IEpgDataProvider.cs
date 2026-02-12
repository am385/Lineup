using Lineup.HDHomeRun.Api.Models;
using Lineup.HDHomeRun.Device.Models;
using Lineup.Core.Models;

namespace Lineup.Core.Storage;

/// <summary>
/// Provides EPG data, abstracting whether it comes from the API or local cache.
/// Handles enrichment of raw API data with device channel information.
/// </summary>
public interface IEpgDataProvider
{
    /// <summary>
    /// Gets enriched EPG data for the specified time range.
    /// Uses cached data if available, enriching it with the provided device channels.
    /// </summary>
    /// <param name="deviceChannels">Device channel lineup for enrichment</param>
    /// <param name="startTimeUtc">Start of the time range</param>
    /// <param name="endTimeUtc">End of the time range</param>
    /// <returns>Enriched EPG data for the specified range</returns>
    Task<HDHomeRunEpgData> GetEnrichedEpgDataAsync(List<HDHomeRunChannel> deviceChannels, DateTime startTimeUtc, DateTime endTimeUtc);

    /// <summary>
    /// Gets cached raw EPG data within the specified time range without enrichment.
    /// </summary>
    Task<List<HDHomeRunChannelEpgSegment>> GetCachedRawDataAsync(DateTime? startTimeUtc = null, DateTime? endTimeUtc = null);

    /// <summary>
    /// Fetches raw data from the API and stores it locally.
    /// Continuously polls the API until one of the following conditions is met:
    /// 1. The API call fails
    /// 2. The API returns only data already in the cache (no new data)
    /// 3. We have targetDays worth of data past the current time
    /// </summary>
    /// <param name="targetDays">Number of days of future data to accumulate</param>
    /// <param name="force">If true, first fetch starts from now, ignoring cached data</param>
    /// <param name="progress">Optional progress reporter for fetch status updates</param>
    /// <returns>All raw EPG segments that were fetched and stored</returns>
    Task<List<HDHomeRunChannelEpgSegment>> FetchAndStoreRawDataAsync(
        int targetDays,
        bool force = false,
        IProgress<FetchProgressInfo>? progress = null);

    /// <summary>
    /// Cleans up old program data from the cache.
    /// </summary>
    Task CleanupOldDataAsync();
}
