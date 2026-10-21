using Lineup.Streaming.Sweep;
using Xunit;

namespace Lineup.Streaming.Sweep.Tests;

/// <summary>Exit policy, discarded contamination, independent evidence provenance and fallback handling.</summary>
public sealed class SweepInvariantTests
{
    /// <summary>Expected protected/unavailable channels can complete without product failure limits.</summary>
    [Theory]
    [InlineData((int)SweepVerdict.SourceUnavailable)]
    [InlineData((int)SweepVerdict.ConfirmedDrm)]
    [InlineData((int)SweepVerdict.InferredDrm)]
    public void ExpectedAvailabilityCanComplete(int verdict)
    {
        // Arrange
        var run = CompletedRun((SweepVerdict)verdict);
        // Act
        var exit = SweepPolicy.ExitCode(run);
        // Assert
        Assert.Equal(0, exit);
    }

    /// <summary>Product thresholds are opt-in; infrastructure and cleanup failures never are.</summary>
    [Fact]
    public void ProductThresholdIsConfigurable()
    {
        // Arrange
        var unrestricted = CompletedRun(SweepVerdict.InitialServerFailure);
        var strict = unrestricted with { Options = unrestricted.Options with { MaximumProductFailures = 0 } };
        // Act
        var defaultExit = SweepPolicy.ExitCode(unrestricted);
        var strictExit = SweepPolicy.ExitCode(strict);
        // Assert
        Assert.Equal(0, defaultExit);
        Assert.Equal(2, strictExit);
    }

    /// <summary>A clean rerun cannot erase emergency cleanup or a capacity-release restart.</summary>
    [Fact]
    public void RecoveryRetainsPermanentViolation()
    {
        // Arrange
        var run = CompletedRun(SweepVerdict.SourceUnavailable);
        run.Attempts.Add(new SweepAttempt
        {
            Id = "discarded",
            Key = run.Selected[0].Key,
            Verdict = SweepVerdict.CapacityContamination,
            FinishedUtc = DateTimeOffset.UnixEpoch,
            Cleanup = new(true, true, true, [], [])
        });
        run.Violations.Add("Capacity-release restart required.");
        // Act
        var exit = SweepPolicy.ExitCode(run);
        // Assert
        Assert.Equal(2, exit);
        Assert.Equal(1, run.Aggregates.Accepted);
        Assert.Equal(1, run.Aggregates.ContaminatedAttempts);
    }

    /// <summary>Contaminated attempts may be retained but never accepted as channel availability.</summary>
    [Fact]
    public void ContaminationCannotBeAccepted()
    {
        // Arrange
        var run = CompletedRun(SweepVerdict.CapacityContamination);
        // Act
        var errors = SweepPolicy.Evaluate(run, true);
        // Assert
        Assert.Contains(errors, error => error.Contains("Invalid accepted", StringComparison.Ordinal));
    }

    /// <summary>A successful source followed by a failed fallback uses the later failure.</summary>
    [Fact]
    public void FallbackFailureOverridesSourceSuccess()
    {
        // Arrange
        var now = DateTimeOffset.UnixEpoch;
        var source = new StartupOutcome("success", 200, null, null, null, now, now, 0, null, true, false);
        var fallback = source with { Outcome = "server-failure", HttpStatus = 503, Code = "broker_unauthorized", Succeeded = false };
        var result = new ChannelAttemptResult("target", "1", "One", 1, false, "not-applicable", 0, "failed fallback", null, source, 1, "startup", "product-startup-defect");
        StartupExchange[] exchanges = [new("source", source, "s1", null, null, null, null), new("fallback", fallback, null, null, null, null, null)];
        // Act
        var verdict = SweepPolicy.Classify(result, exchanges);
        // Assert
        Assert.Equal(SweepVerdict.FallbackStartFailure, verdict);
    }

    /// <summary>Successful fallback cannot erase an earlier capacity contamination.</summary>
    [Fact]
    public void EarlierCapacityFailureRemainsContamination()
    {
        // Arrange
        var now = DateTimeOffset.UnixEpoch;
        var capacity = new StartupOutcome("capacity-contamination", 503, "805", null, null, now, now, 0, null, false, true);
        var success = capacity with { Outcome = "success", HttpStatus = 200, Code = null, Succeeded = true };
        var result = new ChannelAttemptResult("target", "1", "One", 1, true, "not-applicable", 0, null, null, success, 1, null, "pass");
        StartupExchange[] exchanges = [new("first", capacity, null, null, null, null, null), new("fallback", success, "s1", null, null, null, null)];
        // Act
        var verdict = SweepPolicy.Classify(result, exchanges);
        // Assert
        Assert.Equal(SweepVerdict.CapacityContamination, verdict);
    }

    /// <summary>Interrupted and pre-playback sync evidence cannot suppress the resumed fresh tune.</summary>
    [Fact]
    public void ResumeRequiresUninterruptedFreshSynchronization()
    {
        // Arrange
        var run = CompletedRun(SweepVerdict.StablePlayback);
        var playback = run.Attempts[0];
        var sync = new SweepAttempt
        {
            Id = "sync",
            Key = playback.Key,
            Purpose = "sync",
            StartedUtc = DateTimeOffset.UnixEpoch.AddSeconds(1),
            FinishedUtc = DateTimeOffset.UnixEpoch.AddSeconds(2),
            Interrupted = true,
            Synchronization = new("aligned", "v", "a", 2, 2, 0, "fixture")
        };
        run.Attempts.Add(sync);
        // Act
        var interrupted = SweepPolicy.FreshSynchronization(run, playback);
        sync.Interrupted = false;
        var completed = SweepPolicy.FreshSynchronization(run, playback);
        playback.FinishedUtc = sync.FinishedUtc;
        var stale = SweepPolicy.FreshSynchronization(run, playback);
        // Assert
        Assert.Null(interrupted);
        Assert.Same(sync, completed);
        Assert.Null(stale);
    }

    /// <summary>Checkpointed sync evidence remains authoritative before playback's derived copy is attached.</summary>
    [Fact]
    public void DetachedFreshSynchronizationStillEnforcesThreshold()
    {
        // Arrange
        var run = CompletedRun(SweepVerdict.StablePlayback) with { Options = new() { MaximumProductFailures = 0 } };
        run.Attempts.Add(new SweepAttempt
        {
            Id = "sync",
            Key = run.Selected[0].Key,
            Purpose = "sync",
            StartedUtc = DateTimeOffset.UnixEpoch.AddSeconds(1),
            FinishedUtc = DateTimeOffset.UnixEpoch.AddSeconds(2),
            Verdict = SweepVerdict.StablePlayback,
            Cleanup = new(true, false, true, [], []),
            Synchronization = new("misaligned", "v", "a", 2, 6, 4, "fixture")
        });
        // Act
        var errors = SweepPolicy.Evaluate(run, true);
        // Assert
        Assert.Contains(errors, error => error.Contains("Product failures 1", StringComparison.Ordinal));
        Assert.Null(run.Attempts[0].Synchronization);
    }

    /// <summary>Fresh-tune product failures honor strict thresholds while source unavailability remains availability.</summary>
    [Theory]
    [InlineData((int)SweepVerdict.StartupTimeout, 2)]
    [InlineData((int)SweepVerdict.InitialServerFailure, 2)]
    [InlineData((int)SweepVerdict.DecoderFailure, 2)]
    [InlineData((int)SweepVerdict.SourceUnavailable, 0)]
    public void FreshSynchronizationVerdictEnforcesProductThreshold(int verdict, int expectedExit)
    {
        // Arrange
        var run = CompletedRun(SweepVerdict.StablePlayback) with { Options = new() { MaximumProductFailures = 0 } };
        run.Attempts.Add(new SweepAttempt
        {
            Id = "sync",
            Key = run.Selected[0].Key,
            Purpose = "sync",
            StartedUtc = DateTimeOffset.UnixEpoch.AddSeconds(1),
            FinishedUtc = DateTimeOffset.UnixEpoch.AddSeconds(2),
            Verdict = (SweepVerdict)verdict,
            Cleanup = new(true, false, true, [], []),
            Synchronization = new(verdict == (int)SweepVerdict.SourceUnavailable ? "unavailable-on-retest" : "startup-failure", null, null, null, null, null, "fixture")
        });
        // Act
        var exit = SweepPolicy.ExitCode(run);
        // Assert
        Assert.Equal(expectedExit, exit);
    }

    /// <summary>Multiple product findings on one channel consume only one channel-level allowance.</summary>
    [Fact]
    public void PlaybackAndSynchronizationFindingsCountOncePerChannel()
    {
        // Arrange
        var run = CompletedRun(SweepVerdict.InitialServerFailure) with { Options = new() { MaximumProductFailures = 1 } };
        run.Attempts.Add(new SweepAttempt
        {
            Id = "sync",
            Key = run.Selected[0].Key,
            Purpose = "sync",
            StartedUtc = DateTimeOffset.UnixEpoch.AddSeconds(1),
            FinishedUtc = DateTimeOffset.UnixEpoch.AddSeconds(2),
            Verdict = SweepVerdict.DecoderFailure,
            Cleanup = new(true, false, true, [], []),
            Synchronization = new("startup-failure", null, null, null, null, null, "fixture")
        });
        // Act
        var exit = SweepPolicy.ExitCode(run);
        // Assert
        Assert.Equal(0, exit);
    }

    /// <summary>Cancellation and unresolved selected rows have distinct nonzero exit states.</summary>
    [Fact]
    public void CancellationIsNeverSuccess()
    {
        // Arrange
        var run = SweepPolicyTests.CreateRun();
        run.State = "cancelled";
        // Act
        var exit = SweepPolicy.ExitCode(run);
        // Assert
        Assert.Equal(130, exit);
    }

    private static SweepRun CompletedRun(SweepVerdict verdict)
    {
        var run = SweepPolicyTests.CreateRun();
        run.State = "complete";
        run.Attempts.Add(new SweepAttempt
        {
            Id = "accepted",
            Key = run.Selected[0].Key,
            Verdict = verdict,
            FinishedUtc = DateTimeOffset.UnixEpoch,
            Result = new ChannelAttemptResult("target", "1", "One", 1, false, "not-applicable", 0, "fixture", null, null, 1, "startup", "fixture"),
            Cleanup = new(true, false, true, [], [])
        });
        run.Accepted[run.Selected[0].Key] = "accepted";
        return run;
    }
}
