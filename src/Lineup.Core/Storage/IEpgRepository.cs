using Lineup.HDHomeRun.Api.Models;

namespace Lineup.Core.Storage;

/// <summary>
/// Repository interface for EPG data storage operations.
/// Stores raw API data for later enrichment.
/// </summary>
public interface IEpgRepository
{
    /// <summary>
    /// Stores raw channel data from API, updating if already exists
    /// </summary>
    Task StoreChannelAsync(HDHomeRunChannelEpgSegment channel);

    /// <summary>
    /// Stores multiple raw channels from API
    /// </summary>
    Task StoreChannelsAsync(IEnumerable<HDHomeRunChannelEpgSegment> channels);

    /// <summary>
    /// Stores a program, checking for duplicates
    /// </summary>
    Task StoreProgramAsync(HDHomeRunProgram program, string guideNumber);

    /// <summary>
    /// Stores multiple programs from a channel segment
    /// </summary>
    Task StoreProgramsAsync(HDHomeRunChannelEpgSegment channelSegment);

    /// <summary>
    /// Stores an entire raw EPG segment (channels and programs)
    /// </summary>
    Task StoreRawSegmentAsync(IEnumerable<HDHomeRunChannelEpgSegment> segments);

    /// <summary>
    /// Gets all stored raw channel data
    /// </summary>
    Task<List<HDHomeRunChannelEpgSegment>> GetChannelsAsync();

    /// <summary>
    /// Gets programs within a time range
    /// </summary>
    /// <param name="startTimeUtc">Start of the time range (UTC)</param>
    /// <param name="endTimeUtc">End of the time range (UTC)</param>
    Task<List<HDHomeRunProgram>> GetProgramsAsync(DateTime? startTimeUtc = null, DateTime? endTimeUtc = null);

    /// <summary>
    /// Gets raw EPG data (channels and programs) for a time range
    /// </summary>
    Task<List<HDHomeRunChannelEpgSegment>> GetRawEpgDataAsync(DateTime? startTimeUtc = null, DateTime? endTimeUtc = null);

    /// <summary>
    /// Removes programs that have ended before the specified time
    /// </summary>
    Task CleanupOldProgramsAsync(DateTime beforeUtc);

    /// <summary>
    /// Ensures the database is created
    /// </summary>
    Task EnsureDatabaseCreatedAsync();

    /// <summary>
    /// Gets statistics about the cached data
    /// </summary>
    Task<CacheStatistics> GetCacheStatisticsAsync();

    /// <summary>
    /// Gets the latest program end time in the database (absolute maximum), or null if no programs exist
    /// </summary>
    Task<DateTime?> GetLatestProgramEndTimeAsync();

    /// <summary>
    /// Gets the safe start time for fetching new data - the earliest point where any channel's data ends.
    /// This ensures no gaps in coverage across all channels.
    /// </summary>
    /// <remarks>
    /// For each channel, finds the latest program end time, then returns the minimum of those values.
    /// This prevents gaps where some channels have longer programs (like movies) while others end earlier.
    /// </remarks>
    Task<DateTime?> GetSafeFetchStartTimeAsync();
}

/// <summary>
/// Statistics about cached EPG data
/// </summary>
public record CacheStatistics(
    int ChannelCount,
    int ProgramCount,
    DateTime? EarliestProgramStart,
    DateTime? LatestProgramEnd,
    TimeSpan? TotalTimeSpan);
