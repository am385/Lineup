using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Lineup.Core.Common.Configuration;
using Lineup.Core.Common.Streaming;
using Lineup.Core.Settings;
using Lineup.Core.VirtualOutputs.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.Core.VirtualOutputs.Runtime.Tests;

/// <summary>Verifies the real launcher supplies writable, isolated temporary storage without inheriting the host environment.</summary>
public sealed class VirtualOutputWorkerLauncherTests
{
    /// <summary>Verifies a sanitized child resolves its own temporary directory and can write media artifacts there.</summary>
    [Fact]
    public async Task StartAsync_SanitizedWorker_CreatesTemporaryArtifacts()
    {
        // Arrange
        await using var package = await WorkerProbePackage.CreateAsync();

        // Act
        await package.StartAsync();
        var report = await package.ReadReportAsync();

        // Assert
        var temporaryPath = Path.TrimEndingDirectorySeparator(report.TemporaryPath);
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetTempPath()), Path.GetDirectoryName(temporaryPath), ignoreCase: OperatingSystem.IsWindows());
        Assert.StartsWith($"lineup-worker-{package.Bootstrap.VirtualDevice.Id}-", Path.GetFileName(temporaryPath), StringComparison.Ordinal);
        Assert.Null(report.WriteError);
        Assert.True(File.Exists(Path.Combine(report.ArtifactDirectory, "fragment.m4s")));
        foreach (var variable in new[] { "TMP", "TEMP", "TMPDIR", "SystemTemp" })
        {
            Assert.Equal(temporaryPath, Path.TrimEndingDirectorySeparator(Assert.IsType<string>(report.Variables[variable])));
        }
        var allowedVariables = new[]
        {
            "PATH", "DOTNET_ROOT", "DOTNET_ROOT_X64", "SystemRoot", "WINDIR", "DOTNET_EnableDiagnostics", "LINEUP_PROCESS_PIPE_ROOT", "LINEUP_WORKER_JOB", "TMP", "TEMP", "TMPDIR", "SystemTemp",
            "LD_LIBRARY_PATH", "LIBVA_DRIVER_NAME", "LIBVA_DRIVERS_PATH", "CUDA_VISIBLE_DEVICES", "CUDA_DEVICE_ORDER", "NVIDIA_VISIBLE_DEVICES", "NVIDIA_DRIVER_CAPABILITIES",
            "ONEVPL_SEARCH_PATH", "ONEVPL_PRIORITY_PATH"
        };
        Assert.All(report.Variables.Keys, key => Assert.Contains(key, allowedVariables, StringComparer.OrdinalIgnoreCase));
        Assert.Equal("0", report.Variables["DOTNET_EnableDiagnostics"]);
        Assert.Equal(Path.GetTempPath(), report.Variables["LINEUP_PROCESS_PIPE_ROOT"]);
        Assert.DoesNotContain(report.Arguments, argument => argument.Contains(package.Bootstrap.BootstrapToken, StringComparison.Ordinal));
    }

    /// <summary>Verifies simultaneously running workers do not share temporary storage.</summary>
    [Fact]
    public async Task StartAsync_TwoWorkers_UseDistinctTemporaryDirectories()
    {
        // Arrange
        await using var first = await WorkerProbePackage.CreateAsync();
        await using var second = await WorkerProbePackage.CreateAsync();

        // Act
        await first.StartAsync();
        await second.StartAsync();
        var firstReport = await first.ReadReportAsync();
        var secondReport = await second.ReadReportAsync();

        // Assert
        Assert.NotEqual(firstReport.TemporaryPath, secondReport.TemporaryPath);
        Assert.True(File.Exists(Path.Combine(firstReport.ArtifactDirectory, "fragment.m4s")));
        Assert.True(File.Exists(Path.Combine(secondReport.ArtifactDirectory, "fragment.m4s")));
    }

    /// <summary>Verifies recovery does not overwrite a previous activation's report while it is still being inspected.</summary>
    [Fact]
    public async Task StartAsync_PreviousReportOpen_ReplacementPublishesIndependentReport()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Requires Windows file sharing semantics.");

        // Arrange
        await using var package = await WorkerProbePackage.CreateAsync();
        var first = await package.StartAsync();
        var previous = await package.ReadReportAsync();
        await first.DisposeAsync();
        using var locked = File.Open(Path.Combine(package.PackageRoot, $"worker-environment-{previous.WorkerProcessId}.json"), FileMode.Open, FileAccess.Read, FileShare.Read);

        // Act
        await package.StartAsync();
        var replacement = await package.ReadReportAsync();

        // Assert
        Assert.NotEqual(previous.WorkerProcessId, replacement.WorkerProcessId);
        Assert.Null(replacement.WriteError);
        Assert.True(File.Exists(Path.Combine(replacement.ArtifactDirectory, "fragment.m4s")));
    }

    /// <summary>Verifies disposal removes the running worker's complete temporary tree and remains idempotent.</summary>
    [Fact]
    public async Task DisposeAsync_RunningWorker_RemovesTemporaryArtifacts()
    {
        // Arrange
        await using var package = await WorkerProbePackage.CreateAsync();
        var worker = await package.StartAsync();
        var report = await package.ReadReportAsync();

        // Act
        await worker.DisposeAsync();
        await worker.DisposeAsync();

        // Assert
        Assert.False(Directory.Exists(report.TemporaryPath));
        Assert.False(Directory.Exists(report.ArtifactDirectory));
    }

    /// <summary>Verifies disposal also removes temporary storage after a worker exits on its own.</summary>
    [Fact]
    public async Task DisposeAsync_ExitedWorker_RemovesTemporaryArtifacts()
    {
        // Arrange
        await using var package = await WorkerProbePackage.CreateAsync();
        var bootstrap = package.Bootstrap with { VirtualDevice = package.Bootstrap.VirtualDevice with { Name = "exit" } };
        var worker = await package.StartAsync(bootstrap);
        var report = await package.ReadReportAsync();

        // Act
        var exitCode = await worker.Completion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        await worker.DisposeAsync();

        // Assert
        Assert.Equal(0, exitCode);
        Assert.False(Directory.Exists(report.TemporaryPath));
        Assert.False(Directory.Exists(report.ArtifactDirectory));
    }

    /// <summary>Verifies package cleanup waits for a transient Windows file lock and does not report success before deletion.</summary>
    [Fact]
    public async Task DisposeAsync_LockedPackageFile_WaitsForRelease()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Requires Windows file sharing semantics.");

        // Arrange
        var package = await WorkerProbePackage.CreateAsync();
        using var locked = File.Open(Path.Combine(package.PackageRoot, "Lineup.TestSupport.FakeFFmpeg.dll"), FileMode.Open, FileAccess.Read, FileShare.Read);

        // Act
        var observed = Record.ExceptionAsync(() => package.DisposeAsync().AsTask()).AsTask();
        try
        {
            Assert.False(observed.IsCompleted);
            locked.Dispose();
            var failure = await observed.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            // Assert
            Assert.Null(failure);
            Assert.False(Directory.Exists(package.PackageRoot));
        }
        finally
        {
            locked.Dispose();
            await observed.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            if (Directory.Exists(package.PackageRoot))
            {
                await package.DisposeAsync();
            }
        }
    }

    /// <summary>Verifies failure to start the executable does not leak a worker temporary directory.</summary>
    [Fact]
    public async Task StartAsync_InvalidExecutable_RemovesTemporaryDirectory()
    {
        // Arrange
        await using var package = await WorkerProbePackage.CreateAsync(invalidExecutable: true);

        // Act
        await Assert.ThrowsAsync<Win32Exception>(() => package.StartAsync());

        // Assert
        Assert.Empty(Directory.GetDirectories(Path.GetTempPath(), $"lineup-worker-{package.Bootstrap.VirtualDevice.Id}-*"));
    }

    /// <summary>Verifies a bootstrap serialization failure after process launch does not leak temporary storage.</summary>
    [Fact]
    public async Task StartAsync_InvalidBootstrap_RemovesTemporaryDirectory()
    {
        // Arrange
        await using var package = await WorkerProbePackage.CreateAsync();
        var configuration = JsonDocument.Parse("{}");
        var bootstrap = package.Bootstrap with
        {
            VirtualDevice = package.Bootstrap.VirtualDevice with { Adapter = new AdapterConfiguration("probe", configuration.RootElement) }
        };
        configuration.Dispose();

        // Act
        await Assert.ThrowsAsync<ObjectDisposedException>(() => package.StartAsync(bootstrap));

        // Assert
        Assert.Empty(Directory.GetDirectories(Path.GetTempPath(), $"lineup-worker-{package.Bootstrap.VirtualDevice.Id}-*"));
    }

    /// <summary>Verifies worker-only crashes and forced stops terminate noncooperative descendants before disposal completes.</summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(1024, false)]
    [InlineData(0, true)]
    [InlineData(1024, true)]
    public async Task DisposeAsync_NoncooperativeDescendant_TerminatesOwnedTree(int memoryMib, bool stopWorker)
    {
        // Arrange
        Assert.SkipWhen(memoryMib > 0 && !OperatingSystem.IsWindows(), "Requires native Windows job resource policy.");
        await using var package = await WorkerProbePackage.CreateAsync();
        var bootstrap = package.Bootstrap with
        {
            VirtualDevice = package.Bootstrap.VirtualDevice with { Name = "descendant" },
            WorkerPolicy = package.Bootstrap.WorkerPolicy with { MaximumMemoryMib = memoryMib, DrainTimeoutSeconds = 1 }
        };
        var worker = await package.StartAsync(bootstrap);
        var report = await package.ReadReportAsync();
        using var descendant = Process.GetProcessById(Assert.IsType<int>(report.DescendantProcessId));
        Task? disposal = null;
        try
        {
            // Act
            if (stopWorker)
            {
                await worker.StopAsync(TimeSpan.Zero, TestContext.Current.CancellationToken);
            }
            else
            {
                using var parent = Process.GetProcessById(worker.ProcessId);
                parent.Kill(entireProcessTree: false);
                await worker.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            }
            disposal = worker.DisposeAsync().AsTask();
            await Task.WhenAll(disposal, worker.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await descendant.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            // Assert
            Assert.True(descendant.HasExited);
            Assert.False(Directory.Exists(report.TemporaryPath));
        }
        finally
        {
            if (!descendant.HasExited)
            {
                descendant.Kill(entireProcessTree: true);
                await descendant.WaitForExitAsync(CancellationToken.None);
            }
            if (disposal is not null)
            {
                await disposal.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            }
        }
    }

    /// <summary>Verifies actual worker-only crash cleanup permits operator recovery and subsequent hosted shutdown.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1024)]
    public async Task Supervisor_WorkerOnlyCrash_CleansDescendantBeforeRecoveryAndShutdown(int memoryMib)
    {
        // Arrange
        Assert.SkipWhen(memoryMib > 0 && !OperatingSystem.IsWindows(), "Requires native Windows job resource policy.");
        await using var package = await WorkerProbePackage.CreateAsync();
        using var authority = new VirtualOutputSessionAuthority();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        var policy = package.Bootstrap.WorkerPolicy with { MaximumMemoryMib = memoryMib, MaximumRestarts = 0, DrainTimeoutSeconds = 1 };
        var device = package.Bootstrap.VirtualDevice with
        {
            Name = "descendant",
            Hosting = new VirtualOutputHostingDefinition { ExecutionMode = VirtualOutputExecutionMode.ManagedProcess, WorkerPolicyKey = policy.Key }
        };
        var settings = Substitute.For<ILineupSettingsService>();
        settings.Settings.Returns(new LineupSettings { Streaming = new StreamingConfiguration { VirtualDevices = [device], VirtualOutputWorkerPolicies = [policy] } });
        var catalog = Substitute.For<IVirtualOutputWorkerCatalog>();
        catalog.ResolveAsync("probe", Arg.Any<CancellationToken>()).Returns(Task.FromResult(package.Package));
        var launched = new System.Collections.Concurrent.ConcurrentQueue<IVirtualOutputWorkerProcess>();
        var launcher = Substitute.For<IVirtualOutputWorkerLauncher>();
        launcher.StartAsync(Arg.Any<VirtualOutputWorkerPackage>(), Arg.Any<VirtualOutputWorkerBootstrap>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var worker = await package.StartAsync(call.Arg<VirtualOutputWorkerBootstrap>());
            launched.Enqueue(worker);
            return worker;
        });
        var status = new VirtualOutputRuntimeStatus();
        using var supervisor = new VirtualOutputSupervisor(settings,
            new VirtualOutputRuntimeOptions { WorkerPackageRoot = package.PackageRoot, IpcEndpoint = package.Bootstrap.Endpoint }, catalog, launcher,
            Substitute.For<IVirtualOutputCertificateResolver>(), authority, status, new VirtualOutputExternalEnrollmentStore(), NullLogger<VirtualOutputSupervisor>.Instance);
        Process? firstDescendant = null;
        Process? replacementDescendant = null;
        try
        {
            await supervisor.StartAsync(token);
            var first = await package.ReadReportAsync();
            firstDescendant = Process.GetProcessById(Assert.IsType<int>(first.DescendantProcessId));

            // Act
            using (var parent = Process.GetProcessById(first.WorkerProcessId))
            {
                parent.Kill(entireProcessTree: false);
            }
            while (status.GetSnapshots().Single().State != VirtualOutputRuntimeState.Failed)
            {
                await Task.Delay(10, token);
            }
            Assert.True(firstDescendant.HasExited);
            var recovery = await supervisor.RetryAsync(device.Id, token).WaitAsync(TimeSpan.FromSeconds(10), token);
            while (launched.Count < 2)
            {
                await Task.Delay(10, token);
            }
            var replacement = await package.ReadReportAsync();
            replacementDescendant = Process.GetProcessById(Assert.IsType<int>(replacement.DescendantProcessId));
            await supervisor.StopAsync(token).WaitAsync(TimeSpan.FromSeconds(10), token);

            // Assert
            Assert.True(recovery.Accepted);
            Assert.NotEqual(first.WorkerProcessId, replacement.WorkerProcessId);
            Assert.True(replacementDescendant.HasExited);
            Assert.False(Directory.Exists(first.TemporaryPath));
            Assert.False(Directory.Exists(replacement.TemporaryPath));
        }
        finally
        {
            foreach (var process in new[] { firstDescendant, replacementDescendant }.OfType<Process>())
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
                process.Dispose();
            }
            await supervisor.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15), CancellationToken.None);
        }
    }

    private sealed record WorkerEnvironmentReport(
        string TemporaryPath,
        string ArtifactDirectory,
        string? WriteError,
        Dictionary<string, string?> Variables,
        string[] Arguments,
        int WorkerProcessId,
        int? DescendantProcessId
    );

    private sealed class WorkerProbePackage(DirectoryInfo directory, VirtualOutputWorkerPackage package) : IAsyncDisposable
    {
        private IVirtualOutputWorkerProcess? _worker;
        private readonly ProbeLogger _logger = new();

        /// <summary>Gets the test-owned package directory.</summary>
        internal string PackageRoot => directory.FullName;

        /// <summary>Gets the hash-verified test package used by the production supervisor.</summary>
        internal VirtualOutputWorkerPackage Package => package;

        /// <summary>Gets the unique activation descriptor supplied only through the child's standard input.</summary>
        internal VirtualOutputWorkerBootstrap Bootstrap { get; } = new()
        {
            Endpoint = new VirtualOutputIpcEndpoint { Address = "unused-probe" },
            VirtualDevice = new VirtualDeviceDefinition
            {
                Id = VirtualDeviceId.New(),
                Name = "run",
                Adapter = new AdapterConfiguration("probe", JsonSerializer.SerializeToElement(new { }))
            },
            WorkerPolicy = new VirtualOutputWorkerPolicyDefinition { Key = "probe", Name = "Probe" },
            WorkerId = "probe",
            Generation = 1,
            BootstrapToken = $"probe-{Guid.NewGuid():N}",
            BootstrapExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(1)
        };

        /// <summary>Copies the deterministic child executable into a disposable hash-verified package.</summary>
        internal static async Task<WorkerProbePackage> CreateAsync(bool invalidExecutable = false)
        {
            var directory = Directory.CreateTempSubdirectory("lineup-launcher-tests-");
            try
            {
                var executable = invalidExecutable ? "invalid.exe" : "Lineup.TestSupport.FakeFFmpeg.dll";
                if (invalidExecutable)
                {
                    await File.WriteAllBytesAsync(Path.Combine(directory.FullName, executable), [1, 2, 3, 4], TestContext.Current.CancellationToken);
                }
                else
                {
                    var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "Lineup.TestSupport.FakeFFmpeg", "release"));
                    foreach (var extension in new[] { ".dll", ".deps.json", ".runtimeconfig.json" })
                    {
                        var name = "Lineup.TestSupport.FakeFFmpeg" + extension;
                        File.Copy(Path.Combine(source, name), Path.Combine(directory.FullName, name));
                    }
                }
                await using var stream = File.OpenRead(Path.Combine(directory.FullName, executable));
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, TestContext.Current.CancellationToken)).ToLowerInvariant();
                return new WorkerProbePackage(directory, new VirtualOutputWorkerPackage
                {
                    PackageRoot = directory.FullName,
                    Manifest = new VirtualOutputWorkerManifest
                    {
                        WorkerId = "probe",
                        Version = "1.0.0",
                        Publisher = "Lineup",
                        ExecutablePath = executable,
                        ExecutableSha256 = hash,
                        AdapterKinds = ["probe"],
                        MinimumProtocolVersion = VirtualOutputProtocol.MinimumVersion,
                        MaximumProtocolVersion = VirtualOutputProtocol.MaximumVersion,
                        MinimumLineupVersion = "3.0.0",
                        SettingsDescriptor = new AdapterSettingsDescriptor { AdapterKind = "probe" }
                    }
                });
            }
            catch
            {
                directory.Delete(recursive: true);
                throw;
            }
        }

        /// <summary>Launches the package through the production launcher and retains ownership for cleanup.</summary>
        internal async Task<IVirtualOutputWorkerProcess> StartAsync(VirtualOutputWorkerBootstrap? bootstrap = null)
        {
            var launcher = new VirtualOutputWorkerLauncher(_logger);
            _worker = await launcher.StartAsync(package, bootstrap ?? Bootstrap, TestContext.Current.CancellationToken);
            return _worker;
        }

        /// <summary>Reads the child's atomically published environment and temporary-file report.</summary>
        internal async Task<WorkerEnvironmentReport> ReadReportAsync()
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            while (_worker is null)
            {
                await Task.Delay(20, timeout.Token);
            }
            var worker = _worker;
            var path = Path.Combine(directory.FullName, $"worker-environment-{worker.ProcessId}.json");
            while (true)
            {
                if (File.Exists(path))
                {
                    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var report = await JsonSerializer.DeserializeAsync<WorkerEnvironmentReport>(stream, cancellationToken: timeout.Token)
                        ?? throw new InvalidDataException("The worker probe returned an empty report.");
                    if (report.WorkerProcessId == worker.ProcessId)
                    {
                        return report;
                    }
                }
                if (worker.Completion.IsCompleted)
                {
                    var exitCode = await worker.Completion;
                    await worker.DisposeAsync();
                    throw new InvalidOperationException($"The worker probe exited with code {exitCode} before reporting its environment.{Environment.NewLine}{_logger.Messages}");
                }
                await Task.Delay(20, timeout.Token);
            }
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (_worker is not null)
            {
                await _worker.DisposeAsync();
            }
            foreach (var path in Directory.EnumerateFiles(directory.FullName, "worker-environment-*.json"))
            {
                var report = JsonSerializer.Deserialize<WorkerEnvironmentReport>(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken))
                    ?? throw new InvalidDataException("The worker probe returned an empty report.");
                var probeDirectory = Path.GetDirectoryName(report.ArtifactDirectory)!;
                if (Directory.Exists(probeDirectory))
                {
                    Directory.Delete(probeDirectory, recursive: true);
                }
            }
            var started = Stopwatch.GetTimestamp();
            while (true)
            {
                try
                {
                    directory.Delete(recursive: true);
                    return;
                }
                catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException) && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(15))
                {
                    // Windows can briefly retain worker image mappings after process exit.
                    await Task.Delay(20, TestContext.Current.CancellationToken);
                }
            }
        }
    }

    private sealed class ProbeLogger : ILogger<VirtualOutputWorkerLauncher>
    {
        private readonly ConcurrentQueue<string> _messages = new();

        /// <summary>Gets captured output after the worker's output pumps have finished.</summary>
        internal string Messages => string.Join(Environment.NewLine, _messages);

        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => true;

        /// <inheritdoc />
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _messages.Enqueue(formatter(state, exception));
    }
}
