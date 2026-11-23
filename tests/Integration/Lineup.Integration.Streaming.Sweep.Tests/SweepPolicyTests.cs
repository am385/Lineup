using System.Text.Json;
using Xunit;

namespace Lineup.Integration.Streaming.Sweep.Tests;

/// <summary>Deterministic verdict, selection, stability and report regressions; no live initialization.</summary>
public sealed class SweepPolicyTests
{
    /// <summary>The fresh full-sweep command persists one 10/20 observation without changing cleanup or matrix selection.</summary>
    [Fact]
    public void FixedWatchCommandPersistsCompleteMeasurementPolicy()
    {
        // Arrange
        string[] arguments = ["execute-fresh", "--startup-seconds", "10", "--watch-seconds", "20", "--stable-seconds", "20", "--observation-seconds", "20", "--attempts", "1", "--sync-only"];
        var defaults = SweepCommand.Parse(["execute-fresh"]);

        // Act
        var command = SweepCommand.Parse(arguments);
        var restored = JsonSerializer.Deserialize<SweepOptions>(JsonSerializer.Serialize(command.Options))!;

        // Assert
        Assert.Equal(10, restored.StartupSeconds);
        Assert.Equal(20, restored.WatchSeconds);
        Assert.Equal(20, restored.StableSeconds);
        Assert.Equal(1, restored.MaximumAttempts);
        Assert.True(restored.SyncOnly);
        Assert.Empty(restored.Matrices);
        Assert.Equal(defaults.Options.CleanupSeconds, restored.CleanupSeconds);
    }

    /// <summary>A fixed watch rejects policies that can repeat or exceed its single observation window.</summary>
    [Theory]
    [InlineData(20, 2, true)]
    [InlineData(20, 1, false)]
    [InlineData(19, 1, true)]
    [InlineData(double.NaN, 1, true)]
    [InlineData(double.PositiveInfinity, 1, true)]
    public void FixedWatchRejectsConflictingBudgets(double watchSeconds, int attempts, bool syncOnly)
    {
        // Arrange
        var options = new SweepOptions { StartupSeconds = 10, StableSeconds = 20, ObservationSeconds = 20, WatchSeconds = watchSeconds, MaximumAttempts = attempts, SyncOnly = syncOnly };

        // Act
        var error = Record.Exception(options.Validate);

        // Assert
        Assert.IsType<ArgumentException>(error);
    }

    /// <summary>The final deadline sample can prove the entire watch rather than expiring just before it.</summary>
    [Fact]
    public void FixedWatchIncludesFinalBoundarySample()
    {
        // Arrange
        var stable = new StablePlayback(20, 15);
        var watch = new FixedWatch(20, stable, new(1, true, false, false, 0, "a"));

        // Act
        watch.Observe(19.5, new(20.5, true, false, false, 0, "a"));
        var completeBeforeDeadline = watch.Complete;
        var finalDelay = watch.NextDelay(19.9);
        watch.Observe(20, new(21, true, false, false, 0, "a"));

        // Assert
        Assert.False(completeBeforeDeadline);
        Assert.Equal(0.1, finalDelay.TotalSeconds, 6);
        Assert.True(watch.Complete);
        Assert.True(watch.Passed);
        Assert.Equal(20, stable.StableDurationSeconds);
    }

    /// <summary>Early stability cannot shorten the requested watch and a stall cannot extend the deadline.</summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void FixedWatchKeepsDeadlineAfterEarlySuccessOrStall(bool stall, bool expectedPassed)
    {
        // Arrange
        var stable = new StablePlayback(5, 4);
        var watch = new FixedWatch(20, stable, new(1, true, false, false, 0, "a"));

        // Act
        watch.Observe(5, new(6, true, false, false, 0, "a"));
        var endedEarly = watch.Complete;
        if (stall)
        {
            watch.Observe(19, new(6, true, false, false, 1, "a"));
        }
        watch.Observe(20, new(21, true, false, false, stall ? 1 : 0, "a"));

        // Assert
        Assert.False(endedEarly);
        Assert.True(watch.Complete);
        Assert.Equal(expectedPassed, watch.Passed);
    }

    /// <summary>Safe continuation is explicit and independent of permission to restart owned deployments.</summary>
    [Theory]
    [InlineData("execute-fresh", false)]
    [InlineData("existing", true)]
    public void VerifiedCleanupContinuationRequiresExplicitFlag(string command, bool existing)
    {
        // Arrange
        string[] arguments = [command, "--continue-after-verified-cleanup"];

        // Act
        var parsed = SweepCommand.Parse(arguments);
        var defaults = SweepCommand.Parse([command]);

        // Assert
        Assert.True(parsed.Options.ContinueAfterVerifiedCleanup);
        Assert.Equal(existing, parsed.Options.ExistingInstance);
        Assert.False(parsed.Options.AllowMatrixRecovery);
        Assert.False(defaults.Options.ContinueAfterVerifiedCleanup);
    }

    /// <summary>Command aliases remain case insensitive without accidentally selecting Docker mode.</summary>
    [Fact]
    public void ExistingCommandIsCaseInsensitive()
    {
        // Arrange
        string[] arguments = ["EXISTING", "--include", "1,2", "--attempts", "2"];
        // Act
        var command = SweepCommand.Parse(arguments);
        // Assert
        Assert.True(command.Options.ExistingInstance);
        Assert.Equal(["1", "2"], command.Options.Include);
        Assert.Equal(2, command.Options.MaximumAttempts);
    }

    /// <summary>Terminal protection and explicit retryability precede status-based retry defaults.</summary>
    [Theory]
    [InlineData("protected-content", 503, true, false)]
    [InlineData("server-failure", 503, false, false)]
    [InlineData("source-unavailable", 404, true, true)]
    public void RetryPolicyHonorsStructuredEvidence(string outcome, int status, bool retryable, bool expected)
    {
        // Arrange
        bool? explicitRetryability = retryable;
        // Act
        var actual = SweepPolicy.Retryable(outcome, status, explicitRetryability);
        // Assert
        Assert.Equal(expected, actual);
    }

    /// <summary>Existing-instance flags cannot accidentally run a deployment regression.</summary>
    [Theory]
    [InlineData("verify-issue1")]
    [InlineData("generate")]
    [InlineData("execute-local-channel")]
    public void ExistingModeRejectsDeploymentCommands(string command)
    {
        // Arrange
        string[] arguments = [command, "--existing-instance"];
        // Act
        var error = Record.Exception(() => SweepCommand.Parse(arguments));
        // Assert
        Assert.IsType<ArgumentException>(error);
    }

    /// <summary>Structured codes take precedence over generic server status.</summary>
    [Theory]
    [InlineData(503, "protected_content", "protected-content")]
    [InlineData(503, "source_no_video_data", "source-unavailable")]
    [InlineData(503, "source_acquisition_timeout", "source-unavailable")]
    [InlineData(502, null, "server-failure")]
    [InlineData(503, null, "server-failure")]
    [InlineData(503, "805", "capacity-contamination")]
    [InlineData(504, "cmaf_startup_timeout", "startup-timeout")]
    public void StartupClassificationHonorsStructuredCode(int status, string? code, string expected)
    {
        // Arrange
        var actualCode = code;
        // Act
        var actual = SweepPolicy.StartupCategory(status, actualCode);
        // Assert
        Assert.Equal(expected, actual);
    }

    /// <summary>Empty selection and absent requested numbers fail before testing.</summary>
    [Fact]
    public void SelectionRejectsMissingAndDuplicateInventory()
    {
        // Arrange
        var channel = new ChannelDefinition("1", "One", "source", "device") { VirtualDeviceId = "player" };
        var options = new SweepOptions { Include = ["2"] };
        // Act
        var missing = Record.Exception(() => options.Select([channel]));
        var duplicate = Record.Exception(() => new SweepOptions().Select([channel, channel]));
        var empty = Record.Exception(() => new SweepOptions { Exclude = ["1"] }.Select([channel]));
        // Assert
        Assert.IsType<InvalidDataException>(missing);
        Assert.IsType<InvalidDataException>(duplicate);
        Assert.IsType<InvalidDataException>(empty);
    }

    /// <summary>Existing instances cannot opt into deployment operations.</summary>
    [Fact]
    public void ExistingInstanceRejectsRecovery()
    {
        // Arrange
        var options = new SweepOptions { ExistingInstance = true, AllowMatrixRecovery = true };
        // Act
        var exception = Record.Exception(options.Validate);
        // Assert
        Assert.IsType<ArgumentException>(exception);
    }

    /// <summary>Stability resets after stalls instead of accumulating wall time.</summary>
    [Fact]
    public void StabilityRequiresContinuousProgress()
    {
        // Arrange
        var stability = new StablePlayback(3, 2);
        // Act
        var initial = stability.Observe(0, new(1, true, false, false, 0, "a"));
        stability.Observe(1, new(2, true, false, false, 0, "a"));
        var stalled = stability.Observe(3, new(2, true, false, false, 1, "a"));
        stability.Observe(4, new(3, true, false, false, 1, "a"));
        var recovered = stability.Observe(7, new(6, true, false, false, 1, "a"));
        // Assert
        Assert.False(initial);
        Assert.False(stalled);
        Assert.True(recovered);
    }

    /// <summary>Pending rows appear exactly once and cannot yield a successful exit.</summary>
    [Fact]
    public void ReportsIncludePendingSelectedRows()
    {
        // Arrange
        var run = CreateRun();
        // Act
        var markdown = SweepStore.Markdown(run);
        var exit = SweepPolicy.ExitCode(run);
        // Assert
        Assert.Single(markdown.Split('\n'), line => line.StartsWith("| target|", StringComparison.Ordinal) || line.StartsWith("| target\\|", StringComparison.Ordinal));
        Assert.Contains("pending", markdown);
        Assert.Equal(3, exit);
    }

    /// <summary>Creates one pending selected projection without live resources.</summary>
    internal static SweepRun CreateRun() => new()
    {
        Id = "test",
        Options = new(),
        Selected = [new("target|player|1", "target", new("1", "One", "source", "device") { VirtualDeviceId = "player" })]
    };
}
