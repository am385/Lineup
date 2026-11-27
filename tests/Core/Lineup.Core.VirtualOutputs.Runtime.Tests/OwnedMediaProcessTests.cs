using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Lineup.Core.Common.Streaming;
using Lineup.Core.Streaming.FFmpeg;
using Lineup.TestSupport;
using NSubstitute;
using Xunit;

namespace Lineup.Core.VirtualOutputs.Runtime.Tests;

/// <summary>Exercises the packaged launch helper, redirected streams, launch barriers, and actual owner death without resource limits or GPU hardware.</summary>
public sealed class OwnedMediaProcessTests
{
    /// <summary>Verifies abrupt supervisor death still terminates media and retains processing admission until the actual media process exits.</summary>
    [Fact]
    public async Task SupervisorDeath_TerminatesMediaAndReleasesAdmission()
    {
        // Arrange
        var directory = CreateDirectory();
        var report = Path.Combine(directory, "media.ready");
        var reservation = Substitute.For<IAsyncDisposable>();
        reservation.DisposeAsync().Returns(ValueTask.CompletedTask);
        var admission = Substitute.For<IMediaWorkAdmission>();
        admission.AcquireAsync(MediaWorkKind.Processing, null, Arg.Any<CancellationToken>()).Returns(new ValueTask<IAsyncDisposable>(reservation));
        using var supervisor = new MediaProcessFactory(admission).Start(new MediaProcessRequest
        {
            FileName = "dotnet",
            Arguments = [SupportAssembly("FakeFFmpeg"), "orphan", report],
            RedirectStandardInput = true
        });
        Process? media = null;
        try
        {
            media = Process.GetProcessById(int.Parse(await ReadReportAsync(report, supervisor), System.Globalization.CultureInfo.InvariantCulture));

            // Act
            supervisor.Kill(entireProcessTree: false);
            await supervisor.WaitForExitAsync(TestContext.Current.CancellationToken);
            await media.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            await MediaProcessAdmission.WaitForReleaseAsync(supervisor).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

            // Assert
            Assert.True(media.HasExited);
            await reservation.Received(1).DisposeAsync();
        }
        finally
        {
            await KillAsync(supervisor);
            if (media is not null)
            {
                await KillAsync(media);
                media.Dispose();
            }
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Verifies stdin closure, stdout/stderr forwarding, target exit status, and admission release use the real media lifetime.</summary>
    [Fact]
    public async Task Start_RedirectedStreams_PreservesExitAndReleasesAdmission()
    {
        // Arrange
        var reservation = Substitute.For<IAsyncDisposable>();
        reservation.DisposeAsync().Returns(ValueTask.CompletedTask);
        var admission = Substitute.For<IMediaWorkAdmission>();
        admission.AcquireAsync(MediaWorkKind.Processing, null, Arg.Any<CancellationToken>()).Returns(new ValueTask<IAsyncDisposable>(reservation));
        using var process = new MediaProcessFactory(admission).Start(new MediaProcessRequest
        {
            FileName = "dotnet",
            Arguments = [SupportAssembly("FakeFFmpeg"), "diagnostics", "2048", "17", "hold"],
            RedirectStandardInput = true,
            RedirectStandardOutput = true
        });
        var diagnostics = TestProcessSupport.DrainOutputAsync(process.StandardError);
        try
        {
            // Act
            var output = new byte[1];
            await process.StandardOutput.BaseStream.ReadExactlyAsync(output, TestContext.Current.CancellationToken);
            await process.StandardInput.BaseStream.WriteAsync(new byte[] { 1, 2, 3 }, TestContext.Current.CancellationToken);
            process.StandardInput.Close();
            await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            await MediaProcessAdmission.WaitForReleaseAsync(process).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal(0x47, output[0]);
            Assert.Equal(17, process.ExitCode);
            Assert.Contains("final-error", await diagnostics, StringComparison.Ordinal);
            await reservation.Received(1).DisposeAsync();
        }
        finally
        {
            await TestProcessSupport.StopAndDrainAsync(process, diagnostics);
        }
    }

    /// <summary>Verifies executable failures preserve their native cause and do not consume processing admission.</summary>
    [Fact]
    public async Task Start_MissingExecutable_ReleasesAdmission()
    {
        // Arrange
        var reservation = Substitute.For<IAsyncDisposable>();
        reservation.DisposeAsync().Returns(ValueTask.CompletedTask);
        var admission = Substitute.For<IMediaWorkAdmission>();
        admission.AcquireAsync(MediaWorkKind.Processing, null, Arg.Any<CancellationToken>()).Returns(new ValueTask<IAsyncDisposable>(reservation));
        var factory = new MediaProcessFactory(admission);

        // Act
        var failure = Assert.Throws<MediaProcessException>(() => factory.Start(new MediaProcessRequest { FileName = $"missing-{Guid.NewGuid():N}", Arguments = [] }));

        // Assert
        Assert.IsType<Win32Exception>(failure.InnerException);
        await reservation.Received(1).DisposeAsync();
    }

    /// <summary>Verifies abrupt host death terminates already-running host-side media even with no CPU/memory enforcement.</summary>
    [Fact]
    public async Task OwnerDeath_HostMedia_TerminatesRealChild()
    {
        // Arrange
        var directory = CreateDirectory();
        var report = Path.Combine(directory, "media.ready");
        using var owner = StartOwner("media", SupportAssembly("FakeFFmpeg"), report, "run");
        Process? media = null;
        try
        {
            media = Process.GetProcessById(int.Parse(await ReadReportAsync(report, owner), System.Globalization.CultureInfo.InvariantCulture));

            // Act
            owner.Kill(entireProcessTree: false);
            await owner.WaitForExitAsync(TestContext.Current.CancellationToken);
            await media.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

            // Assert
            Assert.True(media.HasExited);
        }
        finally
        {
            await KillAsync(owner);
            if (media is not null)
            {
                await KillAsync(media);
                media.Dispose();
            }
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Verifies a host dying before the launch barrier opens cannot execute media or leave a waiting launcher behind.</summary>
    [Fact]
    public async Task OwnerDeath_BeforeOwnershipTransfer_DoesNotExecuteTarget()
    {
        // Arrange
        var directory = CreateDirectory();
        var report = Path.Combine(directory, "media.ready");
        using var owner = StartOwner("media", SupportAssembly("FakeFFmpeg"), report, "barrier");
        Process? launcher = null;
        try
        {
            launcher = Process.GetProcessById(int.Parse(await ReadReportAsync(report + ".barrier", owner), System.Globalization.CultureInfo.InvariantCulture));

            // Act
            owner.Kill(entireProcessTree: false);
            await owner.WaitForExitAsync(TestContext.Current.CancellationToken);
            await launcher.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(40), TestContext.Current.CancellationToken);

            // Assert
            Assert.False(File.Exists(report));
            Assert.True(launcher.HasExited);
        }
        finally
        {
            await KillAsync(owner);
            if (launcher is not null)
            {
                await KillAsync(launcher);
                launcher.Dispose();
            }
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Verifies the sanitized GPU allowlist and host-loss cleanup of unlimited workers without a surviving host issuing process-group termination.</summary>
    [Fact]
    public async Task OwnerDeath_UnlimitedWorker_TerminatesWorkerAndDescendant()
    {
        // Arrange
        var directory = CreateDirectory();
        foreach (var extension in new[] { ".dll", ".deps.json", ".runtimeconfig.json" })
        {
            var source = Path.ChangeExtension(SupportAssembly("FakeFFmpeg"), null) + extension;
            File.Copy(source, Path.Combine(directory, Path.GetFileName(source)));
        }
        var gpuEnvironment = new Dictionary<string, string?>
        {
            ["LD_LIBRARY_PATH"] = "lineup-gpu-test-libraries",
            ["LIBVA_DRIVER_NAME"] = "iHD",
            ["LIBVA_DRIVERS_PATH"] = "lineup-gpu-test-drivers",
            ["CUDA_VISIBLE_DEVICES"] = "0",
            ["CUDA_DEVICE_ORDER"] = "PCI_BUS_ID",
            ["NVIDIA_VISIBLE_DEVICES"] = "GPU-lineup-test",
            ["NVIDIA_DRIVER_CAPABILITIES"] = "video,compute,utility",
            ["ONEVPL_SEARCH_PATH"] = "lineup-gpu-test-vpl",
            ["ONEVPL_PRIORITY_PATH"] = "lineup-gpu-test-priority"
        };
        var environment = new Dictionary<string, string?>(gpuEnvironment) { ["LINEUP_TEST_UNRELATED_SECRET"] = "must-not-reach-worker" };
        using var owner = StartOwner(environment, "worker", directory);
        Process? worker = null;
        Process? descendant = null;
        string? temporaryDirectory = null;
        try
        {
            var workerId = int.Parse(await ReadReportAsync(Path.Combine(directory, "worker.pid"), owner), System.Globalization.CultureInfo.InvariantCulture);
            using var report = JsonDocument.Parse(await ReadReportAsync(Path.Combine(directory, $"worker-environment-{workerId}.json"), owner));
            worker = Process.GetProcessById(report.RootElement.GetProperty("WorkerProcessId").GetInt32());
            descendant = Process.GetProcessById(report.RootElement.GetProperty("DescendantProcessId").GetInt32());
            temporaryDirectory = report.RootElement.GetProperty("TemporaryPath").GetString();

            // Act
            owner.Kill(entireProcessTree: false);
            await owner.WaitForExitAsync(TestContext.Current.CancellationToken);
            await Task.WhenAll(worker.WaitForExitAsync(TestContext.Current.CancellationToken), descendant.WaitForExitAsync(TestContext.Current.CancellationToken))
                .WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

            // Assert
            Assert.True(worker.HasExited);
            Assert.True(descendant.HasExited);
            var variables = report.RootElement.GetProperty("Variables");
            foreach (var item in gpuEnvironment)
            {
                Assert.Equal(item.Value, variables.GetProperty(item.Key).GetString());
            }
            Assert.False(variables.TryGetProperty("LINEUP_TEST_UNRELATED_SECRET", out _));
        }
        finally
        {
            await KillAsync(owner);
            foreach (var process in new[] { worker, descendant }.OfType<Process>())
            {
                await KillAsync(process);
                process.Dispose();
            }
            if (temporaryDirectory is not null && Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateDirectory() => Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, $"owned-process-{Guid.NewGuid():N}")).FullName;

    private static string SupportAssembly(string project) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", $"Lineup.TestSupport.{project}", "release", $"Lineup.TestSupport.{project}.dll"));

    private static Process StartOwner(params string[] arguments) => StartOwner(null, arguments);

    private static Process StartOwner(IReadOnlyDictionary<string, string?>? environment, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        startInfo.ArgumentList.Add(SupportAssembly("OwnedProcessHost"));
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        if (environment is not null)
        {
            foreach (var item in environment)
            {
                startInfo.Environment[item.Key] = item.Value;
            }
        }
        return Process.Start(startInfo) ?? throw new InvalidOperationException("The lifetime-test owner could not start.");
    }

    private static async Task<string> ReadReportAsync(string path, Process owner)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        while (!File.Exists(path))
        {
            if (owner.HasExited)
            {
                throw new InvalidOperationException($"The lifetime-test owner exited with code {owner.ExitCode} before reporting readiness.");
            }
            await Task.Delay(20, deadline.Token);
        }
        return await File.ReadAllTextAsync(path, deadline.Token);
    }

    private static async Task KillAsync(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15), CancellationToken.None);
    }
}
