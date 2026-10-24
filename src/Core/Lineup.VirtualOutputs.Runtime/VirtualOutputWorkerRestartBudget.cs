using Lineup.Common.Streaming;

namespace Lineup.VirtualOutputs.Runtime;

/// <summary>Describes the retry decision after one worker startup or runtime failure.</summary>
public sealed record VirtualOutputWorkerRestartDecision
{
    /// <summary>Gets the number of failures within the configured restart window.</summary>
    public required int FailureCount { get; init; }

    /// <summary>Gets whether the configured restart budget has been exceeded.</summary>
    public required bool Exhausted { get; init; }

    /// <summary>Gets the delay before another activation attempt when the budget remains available.</summary>
    public required TimeSpan RetryDelay { get; init; }
}

/// <summary>Tracks bounded worker failures and calculates retry backoff within one restart policy.</summary>
public sealed class VirtualOutputWorkerRestartBudget
{
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(30);
    private readonly TimeProvider _timeProvider;
    private readonly List<DateTimeOffset> _failures = [];

    /// <summary>Initializes a worker restart budget.</summary>
    public VirtualOutputWorkerRestartBudget(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Gets the current failure count after removing entries outside the configured restart window.</summary>
    public int GetRecentFailureCount(VirtualOutputWorkerPolicyDefinition policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        Prune(policy);
        return _failures.Count;
    }

    /// <summary>Records one failure and returns whether and when another activation should be attempted.</summary>
    public VirtualOutputWorkerRestartDecision RecordFailure(VirtualOutputWorkerPolicyDefinition policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        Prune(policy);
        _failures.Add(_timeProvider.GetUtcNow());
        var failureCount = _failures.Count;
        var exhausted = failureCount > policy.MaximumRestarts;
        return new VirtualOutputWorkerRestartDecision
        {
            FailureCount = failureCount,
            Exhausted = exhausted,
            RetryDelay = exhausted ? TimeSpan.Zero : CalculateRetryDelay(failureCount)
        };
    }

    /// <summary>Clears all recorded failures.</summary>
    public void Reset() => _failures.Clear();

    private static TimeSpan CalculateRetryDelay(int failureCount)
    {
        var seconds = Math.Pow(2, Math.Max(0, failureCount - 1));
        return TimeSpan.FromSeconds(Math.Min(MaximumRetryDelay.TotalSeconds, seconds));
    }

    private void Prune(VirtualOutputWorkerPolicyDefinition policy)
    {
        var cutoff = _timeProvider.GetUtcNow().AddSeconds(-policy.RestartWindowSeconds);
        _failures.RemoveAll(timestamp => timestamp < cutoff);
    }
}
