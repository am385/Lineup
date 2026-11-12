using System.Diagnostics;
using Lineup.Core.Streaming.FFmpeg;
using Lineup.Plugin.WebPlayer.VirtualOutputHost;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lineup.Plugin.WebPlayer.Tests;

/// <summary>Verifies presentation readiness observes terminal upstream failures rather than waiting for an unrelated timeout.</summary>
public sealed class DedicatedMediaReadinessTests
{
    /// <summary>Only upstream EOF or read failure is classified, never cancellation or a closed processor pipe.</summary>
    [Theory]
    [InlineData("eof", "source_eof")]
    [InlineData("failure", "source_failure")]
    [InlineData("canceled", null)]
    public async Task PumpAsync_SourceTermination_IsDistinctFromCancellation(string mode, string? expectedCode)
    {
        // Arrange
        using var process = StartWaitingProcess();
        var diagnostics = MediaProcessDiagnostics.ReadTailAsync(process.StandardError, TestContext.Current.CancellationToken);
        using var input = mode == "failure" ? new ReadFailureStream() : new MemoryStream();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        if (mode == "canceled") cancellation.Cancel();
        string? code = null;

        // Act
        var pumping = MediaStreamPump.PumpAsync(input, process, NullLogger.Instance, "synthetic-source", cancellation.Token, value => code = value);
        if (mode == "failure") await Assert.ThrowsAsync<IOException>(() => pumping);
        else await pumping;
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        await diagnostics;

        // Assert
        Assert.Equal(expectedCode, code);
    }

    /// <summary>Verifies advertised audio is not considered ready while its initialization resource is still missing.</summary>
    [Fact]
    public async Task WaitAsync_AudioInitializationMissing_WaitsForInitialization()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-readiness-audio-");
        using var process = StartWaitingProcess();
        var diagnostics = MediaProcessDiagnostics.ReadTailAsync(process.StandardError, TestContext.Current.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var dashPath = Path.Combine(root.FullName, "manifest.mpd");
        var hlsPath = Path.Combine(root.FullName, "master.m3u8");
        await File.WriteAllTextAsync(dashPath, "<MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\" />", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(hlsPath, "#EXTM3U", TestContext.Current.CancellationToken);
        foreach (var representation in new[] { 0, 1 })
        {
            await File.WriteAllBytesAsync(Path.Combine(root.FullName, $"chunk-{representation}-00001.m4s"), [0], TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(Path.Combine(root.FullName, $"chunk-{representation}-00002.m4s"), [0], TestContext.Current.CancellationToken);
        }
        await File.WriteAllBytesAsync(Path.Combine(root.FullName, "init-0.mp4"), [0], TestContext.Current.CancellationToken);
        var pending = DedicatedMediaReadiness.WaitAsync(process, diagnostics, null, dashPath, hlsPath, 2, TimeSpan.FromSeconds(30), cancellation.Token);
        try
        {
            // Act
            var readyBeforeAudioInitialization = pending.IsCompleted;
            await File.WriteAllBytesAsync(Path.Combine(root.FullName, "init-1.mp4"), [0], TestContext.Current.CancellationToken);
            await pending.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

            // Assert
            Assert.False(readyBeforeAudioInitialization);
            Assert.False(process.HasExited);
        }
        finally
        {
            await cancellation.CancelAsync();
            _ = await Record.ExceptionAsync(() => pending);
            process.StandardInput.Close();
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            await diagnostics;
            root.Delete(recursive: true);
        }
    }

    /// <summary>Verifies readiness preserves a failed child's exit diagnostics, including when its input pipe is closed after exit.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WaitAsync_ProcessorExits_RetainsExitDiagnostics(bool pumpInput)
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-readiness-exit-");
        using var process = StartWaitingProcess("diagnostics", "65536", "234", "exit");
        var diagnostics = MediaProcessDiagnostics.ReadTailAsync(process.StandardError, TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        using var input = new MemoryStream(new byte[64 * 1024]);
        string? termination = null;
        var inputTask = pumpInput
            ? MediaStreamPump.PumpAsync(input, process, NullLogger.Instance, "readiness-test", TestContext.Current.CancellationToken, code => termination = code)
            : null;
        try
        {
            // Act
            var exception = await Record.ExceptionAsync(() => DedicatedMediaReadiness.WaitAsync(
                process, diagnostics, inputTask, Path.Combine(root.FullName, "manifest.mpd"), Path.Combine(root.FullName, "master.m3u8"),
                2, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

            // Assert
            var failure = Assert.IsType<MediaProcessException>(exception);
            Assert.Contains("234", failure.Message, StringComparison.Ordinal);
            Assert.Equal(234, failure.Data["ExitCode"]);
            Assert.EndsWith("final-error", Assert.IsType<string>(failure.Data["Diagnostics"]), StringComparison.Ordinal);
            Assert.Null(termination);
            if (inputTask is not null)
            {
                await inputTask;
            }
        }
        finally
        {
            await diagnostics;
            root.Delete(recursive: true);
        }
    }

    /// <summary>Verifies a failed input pump is propagated while the waiting media processor is still alive.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WaitAsync_UpstreamFails_PropagatesBeforeReadinessTimeout(bool processFailure)
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-readiness-source-");
        using var process = StartWaitingProcess();
        var diagnostics = MediaProcessDiagnostics.ReadTailAsync(process.StandardError, TestContext.Current.CancellationToken);
        Exception failure = processFailure ? new MediaProcessException("Upstream FFmpeg exited with code 234.") : new IOException("Upstream stream failed.");
        var inputTask = Task.FromException(failure);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pending = DedicatedMediaReadiness.WaitAsync(
            process, diagnostics, inputTask, Path.Combine(root.FullName, "manifest.mpd"), Path.Combine(root.FullName, "master.m3u8"), 2, TimeSpan.FromSeconds(30), cancellation.Token);
        try
        {
            // Act
            var exception = await Record.ExceptionAsync(() => pending.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));

            // Assert
            Assert.Same(failure, Assert.IsType<MediaInputException>(exception).InnerException);
            Assert.False(process.HasExited);
        }
        finally
        {
            await cancellation.CancelAsync();
            _ = await Record.ExceptionAsync(() => pending);
            process.StandardInput.Close();
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            await diagnostics;
            root.Delete(recursive: true);
        }
    }

    private sealed class ReadFailureStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("Synthetic read failure."));
    }

    private static Process StartWaitingProcess(params string[] arguments)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Name;
        var executable = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "Lineup.TestSupport.FakeFFmpeg", configuration, "Lineup.TestSupport.FakeFFmpeg.dll"));
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(executable);
        foreach (var argument in arguments.Length == 0 ? ["hold"] : arguments)
        {
            start.ArgumentList.Add(argument);
        }
        return Process.Start(start) ?? throw new InvalidOperationException("Unable to start the owned readiness test processor.");
    }
}
