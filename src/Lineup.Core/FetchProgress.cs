namespace Lineup.Core;

/// <summary>
/// Represents the current status of an EPG fetch operation
/// </summary>
public enum FetchStatus
{
    Initializing,
    Fetching,
    Storing,
    Completed,
    Failed
}

/// <summary>
/// Progress information for EPG fetch operations
/// </summary>
public record FetchProgressInfo
{
    /// <summary>
    /// Current status of the fetch operation
    /// </summary>
    public FetchStatus Status { get; init; }

    /// <summary>
    /// Current fetch iteration number (1-based)
    /// </summary>
    public int FetchCount { get; init; }

    /// <summary>
    /// Total programs fetched so far
    /// </summary>
    public int TotalProgramsFetched { get; init; }

    /// <summary>
    /// Total channels fetched so far
    /// </summary>
    public int TotalChannelsFetched { get; init; }

    /// <summary>
    /// Current data end time (how far into the future we have data)
    /// </summary>
    public DateTime? CurrentEndTime { get; init; }

    /// <summary>
    /// Target end time (when we'll stop fetching)
    /// </summary>
    public DateTime TargetEndTime { get; init; }

    /// <summary>
    /// Human-readable message describing current activity
    /// </summary>
    public string Message { get; init; } = "";

    /// <summary>
    /// Error message if status is Failed
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Percentage complete (0-100) based on time coverage
    /// </summary>
    public int PercentComplete
    {
        get
        {
            if (!CurrentEndTime.HasValue || CurrentEndTime.Value <= DateTime.UtcNow)
                return 0;

            var now = DateTime.UtcNow;
            var totalSpan = (TargetEndTime - now).TotalHours;
            var currentSpan = (CurrentEndTime.Value - now).TotalHours;

            if (totalSpan <= 0) return 100;

            var percent = (int)Math.Min(100, (currentSpan / totalSpan) * 100);
            return Math.Max(0, percent);
        }
    }
}
