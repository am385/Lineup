using System.Text.Json;
using Lineup.TestSupport;
using Xunit;

namespace Lineup.Tools.PerformanceCollector.Tests;

/// <summary>Checks owned WPR command construction and failure cleanup with deterministic time and explicitly fake recordings.</summary>
public sealed class WprRecorderTests
{
    /// <summary>Saves only the generated instance and reports start/save/empty-output failures rather than claiming success.</summary>
    [Theory]
    [InlineData("none", true, true, "captured")]
    [InlineData("start", false, true, "failed")]
    [InlineData("stop", false, false, "cleanup-failed")]
    [InlineData("missing", false, false, "cleanup-failed")]
    [InlineData("empty", false, false, "cleanup-failed")]
    public async Task CaptureAsync_UsesOwnedInstanceAndHonestManifest(string failure, bool success, bool cleanup, string expectedState)
    {
        // Arrange
        var root = Path.Combine(Path.GetTempPath(), $"Lineup.WprRecorder.Tests.{Guid.NewGuid():N}");
        var clock = new ControlledTimeProvider();
        List<string[]> commands = [];
        Task ExecuteAsync(IReadOnlyList<string> arguments, string directory, string logName, TimeSpan timeout)
        {
            commands.Add(arguments.ToArray());
            if (arguments[0] == "-start" && failure == "start" || arguments[0] == "-stop" && failure == "stop")
            {
                throw new IOException("Intentional fake WPR failure; no recording was started.");
            }
            if (arguments[0] == "-stop" && failure != "missing")
            {
                File.WriteAllText(arguments[1], failure == "empty" ? "" : "Fake trace: not an ETW recording.");
            }
            return Task.CompletedTask;
        }
        var recorder = new WprRecorder(clock, ExecuteAsync, _ => long.MaxValue);
        var request = new CollectorRequest { Action = "capture", DurationSeconds = 1, Gpu = true, DotNet = true, Io = true, Roots = [new(1, 1)] };
        var pending = recorder.CaptureAsync(request, root, TestContext.Current.CancellationToken);
        try
        {
            // Act
            if (failure != "start")
            {
                await clock.WaitForTimerAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
                clock.Advance(TimeSpan.FromSeconds(1));
            }
            var result = await pending;
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "capture.json"), TestContext.Current.CancellationToken));

            // Assert
            Assert.Equal(success, result.Success);
            Assert.Equal(cleanup, result.CleanupSucceeded);
            Assert.Equal(2, commands.Count);
            var start = commands[0];
            var stop = commands[1];
            Assert.Equal("-instancename", start[^2]);
            Assert.Equal("-instancename", stop[^2]);
            Assert.StartsWith("LineupProfile-", start[^1], StringComparison.Ordinal);
            Assert.Equal(start[^1], stop[^1]);
            Assert.Equal(["CPU", "GPU", "DotNET", "DiskIO", "FileIO", "Network"], start.Where((_, index) => index > 0 && start[index - 1] == "-start"));
            Assert.Equal(Path.Combine(root, "system.etl"), stop[1]);
            Assert.Equal(expectedState, manifest.RootElement.GetProperty("state").GetString());
            Assert.Equal(failure is "none" or "start", manifest.RootElement.GetProperty("traceSaved").GetBoolean());
        }
        finally
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            try
            {
                await pending;
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>Requests a named-instance save after observation cancellation and does not report a full successful capture.</summary>
    [Fact]
    public async Task CaptureAsync_CancellationStillSavesOwnedRecording()
    {
        // Arrange
        var root = Path.Combine(Path.GetTempPath(), $"Lineup.WprRecorder.Cancel.{Guid.NewGuid():N}");
        var clock = new ControlledTimeProvider();
        List<string[]> commands = [];
        Task ExecuteAsync(IReadOnlyList<string> arguments, string directory, string logName, TimeSpan timeout)
        {
            commands.Add(arguments.ToArray());
            if (arguments[0] == "-stop")
            {
                File.WriteAllText(arguments[1], "Fake partial trace: not an ETW recording.");
            }
            return Task.CompletedTask;
        }
        var recorder = new WprRecorder(clock, ExecuteAsync, _ => long.MaxValue);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var request = new CollectorRequest { Action = "capture", DurationSeconds = 300, Roots = [new(1, 1)] };
        var pending = recorder.CaptureAsync(request, root, cancel.Token);
        try
        {
            await clock.WaitForTimerAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

            // Act
            cancel.Cancel();
            var result = await pending;

            // Assert
            Assert.False(result.Success);
            Assert.True(result.CleanupSucceeded);
            Assert.Equal(2, commands.Count);
            Assert.Equal(commands[0][^1], commands[1][^1]);
            Assert.True(File.Exists(Path.Combine(root, "system.etl")));
        }
        finally
        {
            cancel.Cancel();
            await pending;
            Directory.Delete(root, recursive: true);
        }
    }
}
