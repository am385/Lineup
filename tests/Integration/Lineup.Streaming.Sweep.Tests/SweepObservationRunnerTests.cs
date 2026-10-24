using Lineup.Streaming.Sweep;
using Xunit;

namespace Lineup.Streaming.Sweep.Tests;

/// <summary>Exercises real observation orchestration through deterministic delegates, without Docker, browsers or tuners.</summary>
public sealed class SweepObservationRunnerTests
{
    /// <summary>Verified emergency cleanup can advance to the next channel without erasing the permanent failed result.</summary>
    [Theory]
    [InlineData("playback", false)]
    [InlineData("sync", false)]
    [InlineData("sync", true)]
    public async Task OptedInVerifiedCleanupContinuesToNextChannel(string purpose, bool resumed)
    {
        // Arrange
        var run = SweepPolicyTests.CreateRun() with { Options = new() { ContinueAfterVerifiedCleanup = true, SyncOnly = purpose == "sync" } };
        var first = CreateAttempt(run, purpose, 1, forced: true);
        var observed = 0;
        var verified = 0;
        var secondRow = new SelectedChannel("target|player|2", "target", new("2", "Two", "source", "device") { VirtualDeviceId = "player" });
        run.Selected.Add(secondRow);

        // Act
        var accepted = await SweepObservationRunner.RunAsync(run, "artifacts", _ =>
        {
            observed++;
            return Task.FromResult(first);
        }, _ =>
        {
            verified++;
            return Task.FromResult(true);
        }, _ => throw new InvalidOperationException("Verified cleanup must not restart the deployment."),
            () => Task.CompletedTask, TestContext.Current.CancellationToken, resumed ? first : null);
        run.Accepted[first.Key] = accepted.Id;
        var next = CreateAttempt(run, purpose, 1, key: secondRow.Key);
        var second = await SweepObservationRunner.RunAsync(run, "artifacts", _ => Task.FromResult(next),
            _ => throw new InvalidOperationException("Clean observations do not need continuation verification."),
            _ => throw new InvalidOperationException("Clean observations do not need recovery."),
            () => Task.CompletedTask, TestContext.Current.CancellationToken);
        run.Accepted[next.Key] = second.Id;
        if (purpose == "playback")
        {
            CreateAttempt(run, "sync", 1);
            CreateAttempt(run, "sync", 1, key: secondRow.Key);
        }
        run.State = "complete";

        // Assert
        Assert.Same(first, accepted);
        Assert.Same(next, second);
        Assert.Equal(resumed ? 0 : 1, observed);
        Assert.Equal(1, verified);
        Assert.True(first.CleanupContinuationVerified);
        Assert.Equal(2, run.Aggregates.Accepted);
        Assert.Equal(2, run.Aggregates.PlaybackPassed);
        Assert.Equal(1, run.Aggregates.ForcedCleanup);
        Assert.Equal(2, SweepPolicy.ExitCode(run));
        Assert.DoesNotContain(SweepPolicy.Evaluate(run, true), error => error.StartsWith("Invalid accepted", StringComparison.Ordinal));
        Assert.DoesNotContain(SweepPolicy.Evaluate(run, true), error => error.Contains("synchronization observation missing", StringComparison.Ordinal));
        Assert.Contains(run.Violations, error => error.Contains(first.Id, StringComparison.Ordinal));
    }

    /// <summary>Default behavior remains fail-fast for playback, fresh sync and resumed sync.</summary>
    [Theory]
    [InlineData("playback", false)]
    [InlineData("sync", false)]
    [InlineData("sync", true)]
    public async Task DefaultPolicyStopsAfterEmergencyCleanup(string purpose, bool resumed)
    {
        // Arrange
        var run = SweepPolicyTests.CreateRun();
        var attempt = CreateAttempt(run, purpose, 1, forced: true);

        // Act
        var error = await Record.ExceptionAsync(() => SweepObservationRunner.RunAsync(run, "artifacts", _ => Task.FromResult(attempt),
            _ => throw new InvalidOperationException("Verification requires explicit opt-in."),
            _ => throw new InvalidOperationException("Recovery requires explicit opt-in."),
            () => Task.CompletedTask, TestContext.Current.CancellationToken, resumed ? attempt : null));

        // Assert
        Assert.IsType<InvalidOperationException>(error);
        Assert.Contains(attempt.Id, error.Message);
        Assert.Contains("Evidence: artifacts", error.Message);
        Assert.False(attempt.CleanupContinuationVerified);
        Assert.Empty(run.Accepted);
        Assert.NotEmpty(run.Violations);
    }

    /// <summary>Explicit continuation cannot waive unresolved ownership, capacity or mandatory browser/evidence failures.</summary>
    [Theory]
    [InlineData("missing-cleanup")]
    [InlineData("unverified-absence")]
    [InlineData("baseline-not-restored")]
    [InlineData("capacity")]
    [InlineData("earlier-capacity")]
    [InlineData("browser")]
    [InlineData("evidence")]
    public async Task UnsafeCleanupCannotContinue(string scenario)
    {
        // Arrange
        var run = SweepPolicyTests.CreateRun() with { Options = new() { ContinueAfterVerifiedCleanup = true } };
        var attempt = CreateAttempt(run, "sync", 1, forced: true);
        var verifications = 0;
        if (scenario == "missing-cleanup")
        {
            attempt.Cleanup = null;
        }
        if (scenario == "unverified-absence")
        {
            attempt.Cleanup = attempt.Cleanup! with { VerifiedAbsent = false };
        }
        if (scenario == "capacity")
        {
            attempt.Verdict = SweepVerdict.CapacityContamination;
        }
        if (scenario == "earlier-capacity")
        {
            attempt.Starts.Insert(0, attempt.Starts[0] with { Id = "capacity", Outcome = attempt.Starts[0].Outcome with { Outcome = "capacity-contamination" } });
        }
        if (scenario == "browser")
        {
            attempt.BrowserFailures.Add("Browser crashed.");
        }
        if (scenario == "evidence")
        {
            attempt.EvidenceFailures.Add("Ownership capture incomplete.");
        }

        // Act
        var error = await Record.ExceptionAsync(() => SweepObservationRunner.RunAsync(run, "artifacts", _ => Task.FromResult(attempt), _ =>
        {
            verifications++;
            return Task.FromResult(false);
        }, _ => throw new InvalidOperationException("Recovery was not requested."),
            () => Task.CompletedTask, TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<InvalidOperationException>(error);
        Assert.Equal(scenario == "baseline-not-restored" ? 1 : 0, verifications);
        Assert.False(attempt.CleanupContinuationVerified);
        Assert.Empty(run.Accepted);
    }

    /// <summary>All observation paths share opt-in recovery, retain the first failure and use the next bounded attempt.</summary>
    [Theory]
    [InlineData("playback", false)]
    [InlineData("sync", false)]
    [InlineData("sync", true)]
    public async Task RecoveryRetriesEachObservationPath(string purpose, bool resumed)
    {
        // Arrange
        var run = SweepPolicyTests.CreateRun() with { Options = new() { AllowMatrixRecovery = true, MaximumAttempts = 2 } };
        var failed = CreateAttempt(run, purpose, 1, forced: true, absent: false);
        var recovered = CreateAttempt(run, purpose, 2);
        var recoveries = 0;
        var numbers = new List<int>();

        // Act
        var accepted = await SweepObservationRunner.RunAsync(run, "artifacts", number =>
        {
            numbers.Add(number);
            return Task.FromResult(number == 1 ? failed : recovered);
        }, _ => throw new InvalidOperationException("Continuation was not requested."), attempt =>
        {
            Assert.Same(failed, attempt);
            Assert.Contains(run.Violations, error => error.Contains("recovery required", StringComparison.Ordinal));
            recoveries++;
            return Task.CompletedTask;
        }, () => Task.CompletedTask, TestContext.Current.CancellationToken, resumed ? failed : null);

        // Assert
        Assert.Same(recovered, accepted);
        Assert.Equal(resumed ? [2] : new[] { 1, 2 }, numbers);
        Assert.Equal(1, recoveries);
        Assert.Contains(failed, run.Attempts);
        Assert.Contains(run.Violations, error => error.Contains(failed.Id, StringComparison.Ordinal));
    }

    /// <summary>Repeated lifecycle failure never causes more recovery or observations than the configured attempt budget.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryBudgetIsBounded(bool resumed)
    {
        // Arrange
        var run = SweepPolicyTests.CreateRun() with { Options = new() { AllowMatrixRecovery = true, MaximumAttempts = 2 } };
        var first = CreateAttempt(run, "sync", 1, forced: true);
        var last = CreateAttempt(run, "sync", 2, forced: true);
        var observed = 0;
        var recoveries = 0;

        // Act
        var error = await Record.ExceptionAsync(() => SweepObservationRunner.RunAsync(run, "artifacts", number =>
        {
            observed++;
            return Task.FromResult(number == 1 ? first : last);
        }, _ => Task.FromResult(false), _ =>
        {
            recoveries++;
            return Task.CompletedTask;
        }, () => Task.CompletedTask, TestContext.Current.CancellationToken, resumed ? last : null));

        // Assert
        Assert.IsType<InvalidOperationException>(error);
        Assert.Equal(resumed ? 0 : 2, observed);
        Assert.Equal(resumed ? 0 : 1, recoveries);
        Assert.Empty(run.Accepted);
    }

    /// <summary>Existing-instance observations cannot invoke Compose recovery even if invalid options reach the runner.</summary>
    [Fact]
    public async Task ExistingInstanceNeverRestartsDeployment()
    {
        // Arrange
        var run = SweepPolicyTests.CreateRun() with { Options = new() { ExistingInstance = true, AllowMatrixRecovery = true } };
        var attempt = CreateAttempt(run, "sync", 1, forced: true);
        var recoveries = 0;

        // Act
        var error = await Record.ExceptionAsync(() => SweepObservationRunner.RunAsync(run, "artifacts", _ => Task.FromResult(attempt),
            _ => Task.FromResult(false), _ =>
            {
                recoveries++;
                return Task.CompletedTask;
            }, () => Task.CompletedTask, TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<InvalidOperationException>(error);
        Assert.Equal(0, recoveries);
    }

    /// <summary>A failed generation/baseline verification is propagated rather than silently restarting or accepting the channel.</summary>
    [Fact]
    public async Task VerificationFailureStopsWithoutRecovery()
    {
        // Arrange
        var run = SweepPolicyTests.CreateRun() with { Options = new() { ContinueAfterVerifiedCleanup = true, AllowMatrixRecovery = true } };
        var attempt = CreateAttempt(run, "sync", 1, forced: true);
        var failure = new InvalidDataException("Application generation changed.");
        var recoveries = 0;

        // Act
        var error = await Record.ExceptionAsync(() => SweepObservationRunner.RunAsync(run, "artifacts", _ => Task.FromResult(attempt),
            _ => throw failure, _ =>
            {
                recoveries++;
                return Task.CompletedTask;
            }, () => Task.CompletedTask, TestContext.Current.CancellationToken));

        // Assert
        Assert.Same(failure, error);
        Assert.False(attempt.CleanupContinuationVerified);
        Assert.Equal(0, recoveries);
    }

    /// <summary>A failed deployment teardown or tuner-idle check cannot advance to another observation.</summary>
    [Fact]
    public async Task RecoveryFailureStopsBeforeRetry()
    {
        // Arrange
        var run = SweepPolicyTests.CreateRun() with { Options = new() { AllowMatrixRecovery = true } };
        var attempt = CreateAttempt(run, "sync", 1, forced: true);
        var observed = 0;
        var failure = new TimeoutException("Tuners did not return to idle.");

        // Act
        var error = await Record.ExceptionAsync(() => SweepObservationRunner.RunAsync(run, "artifacts", _ =>
        {
            observed++;
            return Task.FromResult(attempt);
        }, _ => Task.FromResult(false), _ => throw failure, () => Task.CompletedTask, TestContext.Current.CancellationToken));

        // Assert
        Assert.Same(failure, error);
        Assert.Equal(1, observed);
        Assert.Empty(run.Accepted);
    }

    /// <summary>Cancellation after the failed observation is checkpointed cannot start recovery.</summary>
    [Fact]
    public async Task CancellationPreventsRecovery()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var run = SweepPolicyTests.CreateRun() with { Options = new() { AllowMatrixRecovery = true, ContinueAfterVerifiedCleanup = true } };
        var attempt = CreateAttempt(run, "sync", 1, forced: true);
        var recoveries = 0;

        // Act
        var error = await Record.ExceptionAsync(() => SweepObservationRunner.RunAsync(run, "artifacts", _ => Task.FromResult(attempt),
            _ => throw new InvalidOperationException("Cancelled observations cannot be verified."), _ =>
            {
                recoveries++;
                return Task.CompletedTask;
            }, () =>
            {
                cancellation.Cancel();
                return Task.CompletedTask;
            }, cancellation.Token));

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.Equal(0, recoveries);
        Assert.NotEmpty(run.Violations);
    }

    private static SweepAttempt CreateAttempt(SweepRun run, string purpose, int number, bool forced = false, bool absent = true, string? key = null)
    {
        var now = DateTimeOffset.UnixEpoch.AddMinutes(run.Attempts.Count + 1);
        var startup = new StartupOutcome("success", 200, null, null, null, now, now, 0, null, true, false);
        var attempt = new SweepAttempt
        {
            Id = Guid.NewGuid().ToString("N"),
            Key = key ?? run.Selected[0].Key,
            Purpose = purpose,
            StartedUtc = now,
            FinishedUtc = now.AddSeconds(30),
            Verdict = SweepVerdict.StablePlayback,
            Starts = [new("start", startup, "session", null, null, null, null)],
            Result = new("target", "1", "One", number, true, "not-applicable", 30, null, null, startup, number, null, "pass"),
            Cleanup = new(true, forced, absent, [], []),
            Synchronization = purpose == "sync" ? new("aligned", "v", "a", 2, 2, 0, "fixture") : null
        };
        run.Attempts.Add(attempt);
        return attempt;
    }
}
