using Lineup.Common.Streaming;
using Xunit;

namespace Lineup.VirtualOutputs.Runtime.Tests;

/// <summary>Verifies bounded worker retry accounting and backoff.</summary>
public sealed class VirtualOutputWorkerRestartBudgetTests
{
    /// <summary>Verifies failures use exponential backoff and stop after the configured restart budget.</summary>
    [Fact]
    public void RecordFailure_RepeatedFailures_BackoffThenExhaustBudget()
    {
        // Arrange
        var budget = new VirtualOutputWorkerRestartBudget();
        var policy = CreatePolicy(maximumRestarts: 3);

        // Act
        var first = budget.RecordFailure(policy);
        var second = budget.RecordFailure(policy);
        var third = budget.RecordFailure(policy);
        var fourth = budget.RecordFailure(policy);

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(1), first.RetryDelay);
        Assert.Equal(TimeSpan.FromSeconds(2), second.RetryDelay);
        Assert.Equal(TimeSpan.FromSeconds(4), third.RetryDelay);
        Assert.False(first.Exhausted);
        Assert.False(second.Exhausted);
        Assert.False(third.Exhausted);
        Assert.True(fourth.Exhausted);
        Assert.Equal(TimeSpan.Zero, fourth.RetryDelay);
    }

    /// <summary>Verifies failures outside the restart window no longer consume the retry budget.</summary>
    [Fact]
    public void RecordFailure_ExpiredWindow_ResetsBackoff()
    {
        // Arrange
        var timeProvider = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        var budget = new VirtualOutputWorkerRestartBudget(timeProvider);
        var policy = CreatePolicy(maximumRestarts: 1);
        _ = budget.RecordFailure(policy);
        timeProvider.Advance(TimeSpan.FromSeconds(policy.RestartWindowSeconds + 1));

        // Act
        var decision = budget.RecordFailure(policy);

        // Assert
        Assert.Equal(1, decision.FailureCount);
        Assert.False(decision.Exhausted);
        Assert.Equal(TimeSpan.FromSeconds(1), decision.RetryDelay);
    }

    /// <summary>Verifies an explicit reset clears quarantine accounting after a configuration change.</summary>
    [Fact]
    public void Reset_ExhaustedBudget_AllowsFreshRetrySequence()
    {
        // Arrange
        var budget = new VirtualOutputWorkerRestartBudget();
        var policy = CreatePolicy(maximumRestarts: 0);
        var exhausted = budget.RecordFailure(policy);

        // Act
        budget.Reset();
        var resetPolicy = CreatePolicy(maximumRestarts: 1);
        var decision = budget.RecordFailure(resetPolicy);

        // Assert
        Assert.True(exhausted.Exhausted);
        Assert.False(decision.Exhausted);
        Assert.Equal(1, decision.FailureCount);
    }

    private static VirtualOutputWorkerPolicyDefinition CreatePolicy(int maximumRestarts) =>
        new()
        {
            Key = "test",
            Name = "Test",
            MaximumRestarts = maximumRestarts,
            RestartWindowSeconds = 60
        };

    private sealed class AdjustableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
    }
}
