using Lineup.Core;

namespace Lineup.Web.Services;

/// <summary>
/// Shared state service for auto-fetch progress that can be observed by UI components.
/// </summary>
public interface IAutoFetchStateService
{
    /// <summary>
    /// Current progress of the auto-fetch operation, or null if not running.
    /// </summary>
    FetchProgressInfo? CurrentProgress { get; }

    /// <summary>
    /// Whether an auto-fetch is currently in progress.
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// Whether auto-fetch is enabled.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// When the last auto-fetch completed.
    /// </summary>
    DateTime? LastFetchTime { get; }

    /// <summary>
    /// When the next auto-fetch is scheduled.
    /// </summary>
    DateTime? NextFetchTime { get; }

    /// <summary>
    /// Event fired when the state changes.
    /// </summary>
    event Action? OnStateChanged;

    /// <summary>
    /// Updates the current progress.
    /// </summary>
    void UpdateProgress(FetchProgressInfo? progress);

    /// <summary>
    /// Marks the fetch as started.
    /// </summary>
    void StartFetch();

    /// <summary>
    /// Marks the fetch as completed and schedules next fetch.
    /// </summary>
    void CompleteFetch(TimeSpan nextFetchIn);

    /// <summary>
    /// Marks the fetch as failed.
    /// </summary>
    /// <param name="errorMessage">The failure description.</param>
    void FailFetch(string errorMessage);

    /// <summary>
    /// Updates the enabled state and next fetch time.
    /// </summary>
    void UpdateSchedule(bool enabled, TimeSpan? nextFetchIn = null);
}

/// <summary>
/// Implementation of auto-fetch state service.
/// </summary>
public class AutoFetchStateService : IAutoFetchStateService
{
    /// <summary>
    /// Gets or sets current progress.
    /// </summary>
    public FetchProgressInfo? CurrentProgress { get; private set; }
    /// <summary>
    /// Gets or sets is running.
    /// </summary>
    public bool IsRunning { get; private set; }
    /// <summary>
    /// Gets or sets is enabled.
    /// </summary>
    public bool IsEnabled { get; private set; } = true;
    /// <summary>
    /// Gets or sets last fetch time.
    /// </summary>
    public DateTime? LastFetchTime { get; private set; }
    /// <summary>
    /// Gets or sets next fetch time.
    /// </summary>
    public DateTime? NextFetchTime { get; private set; }

    /// <summary>
    /// Occurs when on state changed.
    /// </summary>
    public event Action? OnStateChanged;

    /// <summary>
    /// Performs the update progress operation.
    /// </summary>
    public void UpdateProgress(FetchProgressInfo? progress)
    {
        CurrentProgress = progress;
        OnStateChanged?.Invoke();
    }

    /// <summary>
    /// Performs the start fetch operation.
    /// </summary>
    public void StartFetch()
    {
        IsRunning = true;
        CurrentProgress = null;
        OnStateChanged?.Invoke();
    }

    /// <summary>
    /// Performs the complete fetch operation.
    /// </summary>
    public void CompleteFetch(TimeSpan nextFetchIn)
    {
        IsRunning = false;
        LastFetchTime = DateTime.UtcNow;
        NextFetchTime = DateTime.UtcNow.Add(nextFetchIn);
        CurrentProgress = null;
        OnStateChanged?.Invoke();
    }

    /// <inheritdoc />
    public void FailFetch(string errorMessage)
    {
        IsRunning = false;
        CurrentProgress = new FetchProgressInfo
        {
            Status = FetchStatus.Failed,
            Message = "Automatic EPG fetch failed",
            ErrorMessage = errorMessage
        };
        OnStateChanged?.Invoke();
    }

    /// <summary>
    /// Performs the update schedule operation.
    /// </summary>
    public void UpdateSchedule(bool enabled, TimeSpan? nextFetchIn = null)
    {
        IsEnabled = enabled;
        if (nextFetchIn.HasValue)
        {
            NextFetchTime = DateTime.UtcNow.Add(nextFetchIn.Value);
        }
        else if (!enabled)
        {
            NextFetchTime = null;
        }
        OnStateChanged?.Invoke();
    }
}
