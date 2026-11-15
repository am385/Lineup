using System.Diagnostics;
using System.Text.Json;
using Lineup.Core.VirtualOutputs.Protocol;
using Microsoft.Extensions.Logging;

namespace Lineup.Core.VirtualOutputs.Runtime;

/// <summary>Represents one supervised managed worker process.</summary>
public interface IVirtualOutputWorkerProcess : IAsyncDisposable
{
    /// <summary>Gets the operating-system process identifier.</summary>
    int ProcessId { get; }

    /// <summary>Gets a task completed with the process exit code.</summary>
    Task<int> Completion { get; }

    /// <summary>Requests graceful termination followed by bounded forced termination.</summary>
    Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}

/// <summary>Starts validated managed worker executables without placing secrets in arguments or environment variables.</summary>
public interface IVirtualOutputWorkerLauncher
{
    /// <summary>Starts one worker and sends its bootstrap descriptor through redirected standard input.</summary>
    Task<IVirtualOutputWorkerProcess> StartAsync(VirtualOutputWorkerPackage package, VirtualOutputWorkerBootstrap bootstrap, CancellationToken cancellationToken = default);
}

/// <summary>Process-based managed virtual-output worker launcher with activation-scoped temporary storage.</summary>
public sealed class VirtualOutputWorkerLauncher : IVirtualOutputWorkerLauncher
{
    private readonly ILogger<VirtualOutputWorkerLauncher> _logger;

    /// <summary>Initializes a worker process launcher.</summary>
    public VirtualOutputWorkerLauncher(ILogger<VirtualOutputWorkerLauncher> logger)
    {
        _logger = logger;
    }

    /// <summary>Starts the virtual output worker launcher asynchronously.</summary>
    /// <param name="package">The package value.</param>
    /// <param name="bootstrap">The bootstrap value.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The operation's result.</returns>
    public async Task<IVirtualOutputWorkerProcess> StartAsync(VirtualOutputWorkerPackage package, VirtualOutputWorkerBootstrap bootstrap, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(bootstrap);
        VirtualOutputProcessLimits.Preflight(bootstrap.WorkerPolicy);
        if (!await VirtualOutputWorkerManifestValidator.VerifyExecutableAsync(package.Manifest, package.PackageRoot, cancellationToken))
        {
            throw new InvalidOperationException($"Virtual-output worker '{package.Manifest.WorkerId}' failed executable provenance verification.");
        }

        var executablePath = Path.GetFullPath(Path.Combine(package.PackageRoot, package.Manifest.ExecutablePath));
        var isManagedAssembly = string.Equals(Path.GetExtension(executablePath), ".dll", StringComparison.OrdinalIgnoreCase);
        var startInfo = new ProcessStartInfo
        {
            FileName = isManagedAssembly ? "dotnet" : executablePath,
            WorkingDirectory = package.PackageRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (isManagedAssembly)
        {
            startInfo.ArgumentList.Add(executablePath);
        }
        VirtualOutputProcessLimits.ConfigureLifetimeContainment(startInfo);
        var preservedEnvironment = new[] { "PATH", "DOTNET_ROOT", "DOTNET_ROOT_X64", "SystemRoot", "WINDIR" }
            .Select(key => new KeyValuePair<string, string?>(key, Environment.GetEnvironmentVariable(key)))
            .Where(item => !string.IsNullOrEmpty(item.Value))
            .ToArray();
        startInfo.Environment.Clear();
        foreach (var item in preservedEnvironment)
        {
            startInfo.Environment[item.Key] = item.Value;
        }
        startInfo.Environment["DOTNET_EnableDiagnostics"] = "0";
        var temporaryDirectory = Directory.CreateTempSubdirectory($"lineup-worker-{bootstrap.VirtualDevice.Id}-");
        foreach (var variable in new[] { "TMP", "TEMP", "TMPDIR", "SystemTemp" })
        {
            startInfo.Environment[variable] = temporaryDirectory.FullName;
        }
        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
        var processStarted = false;
        IDisposable? processLimits = null;
        try
        {
            processStarted = process.Start();
            if (!processStarted)
            {
                throw new InvalidOperationException($"Virtual-output worker '{package.Manifest.WorkerId}' could not be started.");
            }
            try
            {
                processLimits = VirtualOutputProcessLimits.Apply(process, bootstrap.WorkerPolicy);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or System.ComponentModel.Win32Exception)
            {
                throw new VirtualOutputResourcePolicyException("The worker could not be attached to its requested CPU/memory enforcement scope. Correct the deployment permissions before retrying.", exception);
            }
            var bootstrapJson = JsonSerializer.Serialize(bootstrap, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await process.StandardInput.WriteLineAsync(bootstrapJson.AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
            process.StandardInput.Close();
            var managed = new ManagedVirtualOutputWorkerProcess(
                process,
                processLimits,
                temporaryDirectory,
                package.Manifest.WorkerId,
                TimeSpan.FromSeconds(Math.Max(1, bootstrap.WorkerPolicy.DrainTimeoutSeconds)),
                _logger);
            processLimits = null;
            managed.StartOutputPumps();
            return managed;
        }
        catch
        {
            try
            {
                if (processStarted)
                {
                    VirtualOutputProcessLimits.Terminate(process, processLimits);
                    KillRunningProcessTree(process);
                    await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(Math.Max(1, bootstrap.WorkerPolicy.DrainTimeoutSeconds)));
                }
            }
            finally
            {
                processLimits?.Dispose();
                process.Dispose();
                DeleteTemporaryDirectory(temporaryDirectory, package.Manifest.WorkerId, _logger);
            }
            throw;
        }
    }

    private static void KillRunningProcessTree(Process process)
    {
        if (process.HasExited)
        {
            return;
        }
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when ((exception is InvalidOperationException or System.ComponentModel.Win32Exception) && process.HasExited)
        {
        }
    }

    private static void DeleteTemporaryDirectory(DirectoryInfo directory, string workerId, ILogger logger)
    {
        try
        {
            directory.Delete(recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not remove virtual-output worker {WorkerId} temporary directory {TemporaryDirectory}.", workerId, directory.FullName);
        }
    }

    private sealed class ManagedVirtualOutputWorkerProcess : IVirtualOutputWorkerProcess
    {
        private readonly Process _process;
        private readonly int _processId;
        private readonly IDisposable? _processLimits;
        private readonly DirectoryInfo _temporaryDirectory;
        private readonly string _workerId;
        private readonly TimeSpan _terminationTimeout;
        private readonly ILogger _logger;
        private readonly object _disposeLock = new();
        private readonly object _terminationLock = new();
        private readonly CancellationTokenSource _outputCancellation = new();
        private readonly TaskCompletionSource<int> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task? _stdout;
        private Task? _stderr;
        private Task? _disposal;
        private bool _terminationRequested;

        /// <summary>Initializes the managed virtual output worker process.</summary>
        /// <param name="process">The process value.</param>
        /// <param name="processLimits">The process limits value.</param>
        /// <param name="temporaryDirectory">The temporary directory value.</param>
        /// <param name="workerId">The worker ID value.</param>
        /// <param name="terminationTimeout">Bounded forced-termination and output-join budget.</param>
        /// <param name="logger">The logger value.</param>
        internal ManagedVirtualOutputWorkerProcess(Process process, IDisposable? processLimits, DirectoryInfo temporaryDirectory, string workerId, TimeSpan terminationTimeout, ILogger logger)
        {
            _process = process;
            _processId = process.Id;
            _processLimits = processLimits;
            _temporaryDirectory = temporaryDirectory;
            _workerId = workerId;
            _terminationTimeout = terminationTimeout;
            _logger = logger;
            _process.Exited += ProcessExited;
            if (_process.HasExited)
            {
                Complete();
            }
        }

        /// <summary>Gets the process ID.</summary>
        public int ProcessId => _processId;

        /// <summary>Gets the completion.</summary>
        public Task<int> Completion => _completion.Task;

        /// <summary>Starts output pumps.</summary>
        internal void StartOutputPumps()
        {
            _stdout = PumpAsync(_process.StandardOutput, LogLevel.Information);
            _stderr = PumpAsync(_process.StandardError, LogLevel.Warning);
        }

        /// <summary>Stops the managed virtual output worker process asynchronously.</summary>
        /// <param name="timeout">The timeout value.</param>
        /// <param name="cancellationToken">Token used to cancel the operation.</param>
        /// <returns>A task that completes when the operation has finished.</returns>
        public async Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            if (_process.HasExited)
            {
                TerminateOwnedProcesses();
                return;
            }
            _process.CloseMainWindow();
            try
            {
                await Completion.WaitAsync(timeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                TerminateOwnedProcesses();
                await Completion.WaitAsync(_terminationTimeout, cancellationToken);
            }
            TerminateOwnedProcesses();
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            lock (_disposeLock)
            {
                return new ValueTask(_disposal ??= DisposeCoreAsync());
            }
        }

        private async Task DisposeCoreAsync()
        {
            _process.Exited -= ProcessExited;
            using var deadline = new CancellationTokenSource(_terminationTimeout);
            try
            {
                TerminateOwnedProcesses();
                if (!_process.HasExited)
                {
                    await _process.WaitForExitAsync(deadline.Token);
                }
                await Task.WhenAll(_stdout ?? Task.CompletedTask, _stderr ?? Task.CompletedTask).WaitAsync(deadline.Token);
                Complete();
            }
            catch (OperationCanceledException exception) when (deadline.IsCancellationRequested)
            {
                _logger.LogError(exception, "Virtual-output worker {WorkerId} did not finish owned process cleanup within {Timeout}.", _workerId, _terminationTimeout);
                throw new TimeoutException($"Virtual-output worker '{_workerId}' did not finish owned process cleanup within {_terminationTimeout}.", exception);
            }
            finally
            {
                _outputCancellation.Cancel();
                _process.Dispose();
                _processLimits?.Dispose();
                _outputCancellation.Dispose();
                DeleteTemporaryDirectory(_temporaryDirectory, _workerId, _logger);
            }
        }

        private void TerminateOwnedProcesses()
        {
            lock (_terminationLock)
            {
                if (_terminationRequested)
                {
                    return;
                }
                VirtualOutputProcessLimits.Terminate(_process, _processLimits);
                KillRunningProcessTree(_process);
                _terminationRequested = true;
            }
        }

        private async Task PumpAsync(StreamReader reader, LogLevel level)
        {
            var cancellationToken = _outputCancellation.Token;
            try
            {
                while (await reader.ReadLineAsync(cancellationToken) is { } line)
                {
                    if (line.Length > 4096)
                    {
                        line = line[..4096];
                    }
                    _logger.Log(level, "Virtual-output worker {WorkerId}: {WorkerMessage}", _workerId, line);
                }
            }
            catch (Exception exception) when (cancellationToken.IsCancellationRequested && (exception is OperationCanceledException or ObjectDisposedException or IOException))
            {
            }
        }

        private void ProcessExited(object? sender, EventArgs e) => Complete();

        private void Complete()
        {
            try
            {
                _completion.TrySetResult(_process.ExitCode);
            }
            catch (InvalidOperationException)
            {
            }
        }
    }
}
