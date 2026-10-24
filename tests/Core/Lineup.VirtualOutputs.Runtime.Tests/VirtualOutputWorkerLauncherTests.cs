using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using Lineup.Common.Configuration;
using Lineup.Common.Streaming;
using Lineup.VirtualOutputs.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lineup.VirtualOutputs.Runtime.Tests;

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
        var allowedVariables = new[] { "PATH", "DOTNET_ROOT", "DOTNET_ROOT_X64", "SystemRoot", "WINDIR", "DOTNET_EnableDiagnostics", "TMP", "TEMP", "TMPDIR", "SystemTemp" };
        Assert.All(report.Variables.Keys, key => Assert.Contains(key, allowedVariables, StringComparer.OrdinalIgnoreCase));
        Assert.Equal("0", report.Variables["DOTNET_EnableDiagnostics"]);
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

    private sealed record WorkerEnvironmentReport(string TemporaryPath, string ArtifactDirectory, string? WriteError, Dictionary<string, string?> Variables, string[] Arguments);

    private sealed class WorkerProbePackage(DirectoryInfo directory, VirtualOutputWorkerPackage package) : IAsyncDisposable
    {
        private IVirtualOutputWorkerProcess? _worker;

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
                var executable = invalidExecutable ? "invalid.exe" : "Lineup.FakeFFmpeg.dll";
                if (invalidExecutable)
                {
                    await File.WriteAllBytesAsync(Path.Combine(directory.FullName, executable), [1, 2, 3, 4], TestContext.Current.CancellationToken);
                }
                else
                {
                    var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "Lineup.FakeFFmpeg", "release"));
                    foreach (var extension in new[] { ".dll", ".deps.json", ".runtimeconfig.json" })
                    {
                        var name = "Lineup.FakeFFmpeg" + extension;
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
                        MinimumProtocolVersion = 1,
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
            var launcher = new VirtualOutputWorkerLauncher(NullLogger<VirtualOutputWorkerLauncher>.Instance);
            _worker = await launcher.StartAsync(package, bootstrap ?? Bootstrap, TestContext.Current.CancellationToken);
            return _worker;
        }

        /// <summary>Reads the child's atomically published environment and temporary-file report.</summary>
        internal async Task<WorkerEnvironmentReport> ReadReportAsync()
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var path = Path.Combine(directory.FullName, "worker-environment.json");
            while (!File.Exists(path))
            {
                if (_worker?.Completion.IsCompleted == true)
                {
                    throw new InvalidOperationException($"The worker probe exited with code {await _worker.Completion} before reporting its environment.");
                }
                await Task.Delay(20, timeout.Token);
            }
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<WorkerEnvironmentReport>(stream, cancellationToken: timeout.Token)
                ?? throw new InvalidDataException("The worker probe returned an empty report.");
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (_worker is not null)
            {
                await _worker.DisposeAsync();
            }
            var path = Path.Combine(directory.FullName, "worker-environment.json");
            if (File.Exists(path))
            {
                var report = JsonSerializer.Deserialize<WorkerEnvironmentReport>(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken))
                    ?? throw new InvalidDataException("The worker probe returned an empty report.");
                var probeDirectory = Path.GetDirectoryName(report.ArtifactDirectory)!;
                if (Directory.Exists(probeDirectory))
                {
                    Directory.Delete(probeDirectory, recursive: true);
                }
            }
            directory.Delete(recursive: true);
        }
    }
}
