using System.Diagnostics;
using Lineup.TestSupport;
using Xunit;

namespace Lineup.Core.VirtualOutputs.Runtime.Tests;

/// <summary>Exercises shared process I/O and readiness contracts without SDK inventory, GPU hardware or scheduling thresholds.</summary>
public sealed class TestProcessSupportTests
{
    /// <summary>Large output on both pipes cannot prevent child exit when fixture consumers start before waiting.</summary>
    [Fact]
    public async Task OutputPressure_DrainsBothPipesBeforeWaitingForExit()
    {
        // Arrange
        const int length = 1024 * 1024;
        var process = Process.Start(CreateStart("pipe-pressure", length.ToString(System.Globalization.CultureInfo.InvariantCulture)))!;
        var output = TestProcessSupport.DrainOutputAsync(process.StandardOutput);
        var error = TestProcessSupport.DrainOutputAsync(process.StandardError);
        try
        {
            // Act
            await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            var contents = await Task.WhenAll(output, error);

            // Assert
            Assert.Equal(0, process.ExitCode);
            Assert.All(contents, text => Assert.Equal(length, text.Length));
        }
        finally
        {
            await TestProcessSupport.StopAndDrainAsync(process, output, error);
        }
    }

    /// <summary>A lifetime output read never takes a thread-pool worker needed by the broker or test continuations.</summary>
    [Fact]
    public async Task OutputDrain_BlocksOnlyItsDedicatedThread()
    {
        // Arrange
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var reader = new BlockingReader(entered, release);
        var drain = TestProcessSupport.DrainOutputAsync(reader);
        try
        {
            // Act
            var usedThreadPool = await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            // Assert
            Assert.False(usedThreadPool);
        }
        finally
        {
            release.Set();
            await drain.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        }
    }

    /// <summary>Pre-exited setup drains output but refuses to transfer a consumed nonempty stream to the system under test.</summary>
    [Fact]
    public async Task ExitedProbe_NonemptyOutput_FailsAsFixtureErrorInsteadOfHanging()
    {
        // Arrange
        var start = CreateStart("pipe-pressure", "65536");

        // Act
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => TestProcessSupport.StartExitedAsync(start, TestContext.Current.CancellationToken));

        // Assert
        Assert.Contains("controlled empty probe", failure.Message, StringComparison.Ordinal);
        Assert.Contains("stdout:", failure.Message, StringComparison.Ordinal);
        Assert.Contains("stderr:", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Cancellation before handoff terminates the ready child and joins the fixture-owned output consumers.</summary>
    [Fact]
    public async Task ExitedProbe_CanceledBeforeTransfer_TerminatesOwnedChild()
    {
        // Arrange
        var directory = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, $"probe-{Guid.NewGuid():N}")).FullName;
        var report = Path.Combine(directory, "activation.ready");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var setup = TestProcessSupport.StartExitedAsync(CreateStart("orphan", report), cancellation.Token);
        var observed = false;
        try
        {
            using var readiness = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            readiness.CancelAfter(TimeSpan.FromSeconds(30));
            while (!File.Exists(report))
            {
                if (setup.IsCompleted) await setup;
                await Task.Delay(20, readiness.Token);
            }
            using var observer = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(report, readiness.Token), System.Globalization.CultureInfo.InvariantCulture));
            var exited = TestProcessSupport.ObserveExit(observer);

            // Act
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => setup);
            observed = true;
            await exited.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            // Assert
            Assert.True(observer.HasExited);
        }
        finally
        {
            await cancellation.CancelAsync();
            try
            {
                if (!observed)
                {
                    try
                    {
                        await setup;
                    }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                    {
                    }
                }
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>A report's final name remains invisible until its producer finishes and its writer is closed.</summary>
    [Fact]
    public async Task ReadinessReport_RemainsInvisibleUntilCompletePublication()
    {
        // Arrange
        var directory = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, $"report-{Guid.NewGuid():N}")).FullName;
        var path = Path.Combine(directory, "activation.ready");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publication = AtomicTestReport.WriteAsync(path, async writer =>
        {
            await writer.WriteAsync("12");
            await writer.FlushAsync();
            entered.SetResult();
            await release.Task;
            await writer.WriteAsync("34");
        }, TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            // Act
            var visibleBeforeCommit = File.Exists(path);
            release.SetResult();
            await publication;

            // Assert
            Assert.False(visibleBeforeCommit);
            Assert.Equal("1234", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
            Assert.Equal(path, Assert.Single(Directory.EnumerateFiles(directory)));
        }
        finally
        {
            release.TrySetResult();
            try
            {
                await publication;
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>Failed producers cannot leave a partial readiness report or a staging artifact.</summary>
    [Fact]
    public async Task ReadinessReport_FailedProducer_DoesNotCommitPartialData()
    {
        // Arrange
        var directory = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, $"report-{Guid.NewGuid():N}")).FullName;
        var path = Path.Combine(directory, "activation.ready");
        try
        {
            // Act
            await Assert.ThrowsAsync<IOException>(() => AtomicTestReport.WriteAsync(path, async writer =>
            {
                await writer.WriteAsync("partial");
                throw new IOException("Synthetic producer failure.");
            }, TestContext.Current.CancellationToken));

            // Assert
            Assert.False(File.Exists(path));
            Assert.Empty(Directory.EnumerateFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Cancellation after writing a staged payload cannot commit readiness or leave staging artifacts.</summary>
    [Fact]
    public async Task ReadinessReport_CanceledProducer_DoesNotCommitPartialData()
    {
        // Arrange
        var directory = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, $"report-{Guid.NewGuid():N}")).FullName;
        var path = Path.Combine(directory, "activation.ready");
        using var cancellation = new CancellationTokenSource();
        try
        {
            // Act
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AtomicTestReport.WriteAsync(path, async writer =>
            {
                await writer.WriteAsync("partial");
                await cancellation.CancelAsync();
            }, cancellation.Token));

            // Assert
            Assert.False(File.Exists(path));
            Assert.Empty(Directory.EnumerateFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Reusing another activation's committed path fails without changing its payload.</summary>
    [Fact]
    public async Task ReadinessReport_ExistingActivation_IsNotOverwritten()
    {
        // Arrange
        var directory = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, $"report-{Guid.NewGuid():N}")).FullName;
        var path = Path.Combine(directory, "activation.ready");
        await AtomicTestReport.WriteAsync(path, "first", TestContext.Current.CancellationToken);
        try
        {
            // Act
            await Assert.ThrowsAsync<IOException>(() => AtomicTestReport.WriteAsync(path, "replacement", TestContext.Current.CancellationToken));

            // Assert
            Assert.Equal("first", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
            Assert.Equal(path, Assert.Single(Directory.EnumerateFiles(directory)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ProcessStartInfo CreateStart(params string[] arguments)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Name;
        var executable = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "Lineup.TestSupport.FakeFFmpeg", configuration, "Lineup.TestSupport.FakeFFmpeg.dll"));
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(executable);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    private sealed class BlockingReader(TaskCompletionSource<bool> entered, ManualResetEventSlim release) : TextReader
    {
        private bool _finished;

        /// <inheritdoc />
        public override int Read(char[] buffer, int index, int count)
        {
            if (_finished) return 0;
            entered.TrySetResult(Thread.CurrentThread.IsThreadPoolThread);
            release.Wait();
            _finished = true;
            buffer[index] = 'x';
            return 1;
        }
    }
}
