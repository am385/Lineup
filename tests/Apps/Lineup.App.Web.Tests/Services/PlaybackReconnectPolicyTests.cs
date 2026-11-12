using Lineup.App.Web.Components.Pages;
using Lineup.App.Web.Services;
using Xunit;

namespace Lineup.App.Web.Tests.Services;

/// <summary>Verifies finite recovery budgets, stable playback replenishment, and fail-closed startup classification.</summary>
public sealed class PlaybackReconnectPolicyTests
{
    /// <summary>Checks exact backoff delays and rejects a fourth attempt despite brief successful sessions.</summary>
    [Fact]
    public void FlappingPlayback_PreservesThreeAttemptBudget()
    {
        // Arrange
        var clock = new Clock();
        var policy = new PlaybackReconnectPolicy(clock);
        policy.MarkFailed();
        var delays = new List<TimeSpan>();

        // Act
        for (var attempt = 0; attempt < 3; attempt++)
        {
            Assert.True(policy.TryNextAttempt(out var delay));
            delays.Add(delay);
            clock.Advance(delay);
            policy.MarkPlaying();
            clock.Advance(TimeSpan.FromSeconds(1));
            policy.MarkFailed();
        }

        // Assert
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], delays);
        Assert.False(policy.TryNextAttempt(out _));
    }

    /// <summary>Checks the measured sixty-second stable interval, including a final waiting event before source loss.</summary>
    [Theory]
    [InlineData(59, false)]
    [InlineData(60, true)]
    public void StablePlayback_ReplenishesOnlyAfterSixtySeconds(int stableSeconds, bool replenished)
    {
        // Arrange
        var clock = new Clock();
        var policy = new PlaybackReconnectPolicy(clock);
        policy.MarkFailed();
        for (var attempt = 0; attempt < 3; attempt++) Assert.True(policy.TryNextAttempt(out _));
        policy.MarkPlaying();
        clock.Advance(TimeSpan.FromSeconds(stableSeconds));

        // Act
        policy.MarkUnstable();
        policy.MarkFailed();
        var allowed = policy.TryNextAttempt(out var delay);

        // Assert
        Assert.Equal(replenished, allowed);
        if (allowed) Assert.Equal(TimeSpan.FromSeconds(1), delay);
    }

    /// <summary>Time spent paused or waiting is not uninterrupted healthy playback.</summary>
    [Fact]
    public void InterruptedPlayback_DoesNotReplenishBudget()
    {
        // Arrange
        var clock = new Clock();
        var policy = new PlaybackReconnectPolicy(clock);
        policy.MarkFailed();
        Assert.True(policy.TryNextAttempt(out _));
        policy.MarkPlaying();
        clock.Advance(TimeSpan.FromSeconds(30));
        policy.MarkUnstable();
        clock.Advance(TimeSpan.FromSeconds(60));
        policy.MarkPlaying();
        clock.Advance(TimeSpan.FromSeconds(30));

        // Act
        policy.MarkFailed();

        // Assert
        Assert.False(policy.TryNextAttempt(out _));
    }

    /// <summary>Startup and backoff consume the same overall deadline; an attempt cannot start at the deadline.</summary>
    [Theory]
    [InlineData(59)]
    [InlineData(60)]
    [InlineData(600)]
    public void Deadline_IncludesAllElapsedRecoveryTime(int seconds)
    {
        // Arrange
        var clock = new Clock();
        var policy = new PlaybackReconnectPolicy(clock);
        policy.MarkFailed();

        // Act
        clock.Advance(TimeSpan.FromSeconds(seconds));

        // Assert
        Assert.False(policy.TryNextAttempt(out _));
    }

    /// <summary>Unknown, permanent, admission, and protection errors remain terminal even with an erroneous retry flag.</summary>
    [Theory]
    [InlineData("source_unavailable", true, true)]
    [InlineData("source_unavailable", false, false)]
    [InlineData("media_source_failed", true, true)]
    [InlineData("source_acquisition_timeout", true, true)]
    [InlineData("protected_content", true, false)]
    [InlineData("source_invalid", true, false)]
    [InlineData("source_not_found", true, false)]
    [InlineData("resource_limit", true, false)]
    [InlineData("media_startup_timeout", true, false)]
    [InlineData(null, true, false)]
    public void StartupClassification_RequiresExplicitTransientSourceCode(string? code, bool retryable, bool expected)
    {
        // Arrange
        var failure = new Watch.CmafStartFailure("Startup failed.", code, retryable);

        // Act
        var allowed = failure.CanRetry;

        // Assert
        Assert.Equal(expected, allowed);
    }

    private sealed class Clock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        internal void Advance(TimeSpan duration) => _timestamp += duration.Ticks;
    }
}
