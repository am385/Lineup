using Lineup.Integration.Streaming.Sweep;
using Xunit;

namespace Lineup.Integration.Streaming.Sweep.Tests;

/// <summary>Terminal disposition and persisted timeout/cancellation regressions without live execution.</summary>
public sealed class SweepRunLifecycleTests
{
    /// <summary>Unexpected programming failures still propagate, but cannot leave a finished checkpoint marked running.</summary>
    [Fact]
    public async Task UnexpectedFailurePersistsInconclusiveWithoutBeingSwallowed()
    {
        // Arrange
        var root = Path.Combine(Path.GetTempPath(), "lineup-lifecycle", Guid.NewGuid().ToString("N"));
        var run = SweepPolicyTests.CreateRun();
        Directory.CreateDirectory(root);
        try
        {
            // Act
            var error = await Record.ExceptionAsync(() => SweepRunLifecycle.RunAsync(run,
                () => Task.FromException(new NotSupportedException("controlled unexpected failure")),
                () => SweepStore.ReportsAsync(root, run), TestContext.Current.CancellationToken));
            var saved = await SweepStore.LoadAsync(Path.Combine(root, "sweep-run.json"));

            // Assert
            Assert.IsType<NotSupportedException>(error);
            Assert.Equal("inconclusive", saved.State);
            Assert.NotNull(saved.FinishedUtc);
            Assert.Equal(3, SweepPolicy.ExitCode(saved));
            Assert.Single(saved.Violations);
            Assert.False(File.Exists(Path.Combine(root, "completed.marker")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>HTTP timeout cancellation during teardown must remain infrastructure failure, not Ctrl+C or a running checkpoint.</summary>
    [Theory]
    [InlineData(false, "inconclusive", 3)]
    [InlineData(true, "cancelled", 130)]
    public async Task TeardownTimeoutPersistsAccurateTerminalState(bool userCancelled, string expectedState, int expectedExit)
    {
        // Arrange
        var root = Path.Combine(Path.GetTempPath(), "lineup-lifecycle", Guid.NewGuid().ToString("N"));
        var run = SweepPolicyTests.CreateRun();
        using var cancellation = new CancellationTokenSource();
        if (userCancelled)
        {
            cancellation.Cancel();
        }
        var teardownCalled = false;
        var exit = -1;
        Directory.CreateDirectory(root);
        try
        {
            // Act
            var error = await Record.ExceptionAsync(async () => exit = await SweepRunLifecycle.RunAsync(run, ExecuteAsync,
                () => SweepStore.ReportsAsync(root, run), cancellation.Token));
            var saved = await SweepStore.LoadAsync(Path.Combine(root, "sweep-run.json"));

            // Assert
            Assert.True(teardownCalled);
            Assert.Null(error);
            Assert.Equal(expectedExit, exit);
            Assert.Equal(expectedState, saved.State);
            Assert.NotNull(saved.FinishedUtc);
            Assert.Equal(!userCancelled, saved.Violations.Count > 0);
            Assert.False(File.Exists(Path.Combine(root, "completed.marker")));
            if (!userCancelled)
            {
                Assert.Contains(saved.Violations, violation => violation.Contains("controlled HTTP timeout", StringComparison.Ordinal));
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }

        async Task ExecuteAsync()
        {
            try
            {
                await Task.CompletedTask;
            }
            finally
            {
                teardownCalled = true;
                throw new TaskCanceledException("controlled HTTP timeout", new TimeoutException("HTTP deadline expired"));
            }
        }
    }
}
