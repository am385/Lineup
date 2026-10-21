using Lineup.Streaming.Sweep;
using Xunit;

namespace Lineup.Streaming.Sweep.Tests;

/// <summary>Deterministic verdict, selection, stability and report regressions; no live initialization.</summary>
public sealed class SweepPolicyTests
{
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
