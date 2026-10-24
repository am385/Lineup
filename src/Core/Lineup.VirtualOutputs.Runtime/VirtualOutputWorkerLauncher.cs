using System.Diagnostics;
using System.Text.Json;
using Lineup.Common.Streaming;
using Lineup.VirtualOutputs.Protocol;
using Microsoft.Extensions.Logging;

namespace Lineup.VirtualOutputs.Runtime;

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
    Task<IVirtualOutputWorkerProcess> StartAsync(
        VirtualOutputWorkerPackage package,
        VirtualOutputWorkerBootstrap bootstrap,
        CancellationToken cancellationToken = default);
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

    /// <inheritdoc />
    public async Task<IVirtualOutputWorkerProcess> StartAsync(
        VirtualOutputWorkerPackage package,
        VirtualOutputWorkerBootstrap bootstrap,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(bootstrap);
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
            processLimits = VirtualOutputProcessLimits.Apply(process, bootstrap.WorkerPolicy);
            var bootstrapJson = JsonSerializer.Serialize(bootstrap, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await process.StandardInput.WriteLineAsync(bootstrapJson.AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
            process.StandardInput.Close();
            var managed = new ManagedVirtualOutputWorkerProcess(
                process,
                processLimits,
                temporaryDirectory,
                package.Manifest.WorkerId,
                _logger);
            processLimits = null;
            managed.StartOutputPumps();
            return managed;
        }
        catch
        {
            try
            {
                if (processStarted && !process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
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
        private readonly IDisposable? _processLimits;
        private readonly DirectoryInfo _temporaryDirectory;
        private readonly string _workerId;
        private readonly ILogger _logger;
        private readonly TaskCompletionSource<int> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task? _stdout;
        private Task? _stderr;
        private int _disposed;

        internal ManagedVirtualOutputWorkerProcess(Process process, IDisposable? processLimits, DirectoryInfo temporaryDirectory, string workerId, ILogger logger)
        {
            _process = process;
            _processLimits = processLimits;
            _temporaryDirectory = temporaryDirectory;
            _workerId = workerId;
            _logger = logger;
            _process.Exited += ProcessExited;
            if (_process.HasExited)
            {
                Complete();
            }
        }

        /// <inheritdoc />
        public int ProcessId => _process.Id;

        /// <inheritdoc />
        public Task<int> Completion => _completion.Task;

        internal void StartOutputPumps()
        {
            _stdout = PumpAsync(_process.StandardOutput, LogLevel.Information);
            _stderr = PumpAsync(_process.StandardError, LogLevel.Warning);
        }

        /// <inheritdoc />
        public async Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            if (_process.HasExited)
            {
                return;
            }
            _process.CloseMainWindow();
            try
            {
                await Completion.WaitAsync(timeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
                await Completion.WaitAsync(cancellationToken);
            }
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            _process.Exited -= ProcessExited;
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync();
                }
                if (_stdout is not null)
                {
                    await _stdout;
                }
                if (_stderr is not null)
                {
                    await _stderr;
                }
            }
            finally
            {
                _process.Dispose();
                _processLimits?.Dispose();
                DeleteTemporaryDirectory(_temporaryDirectory, _workerId, _logger);
            }
        }

        private async Task PumpAsync(StreamReader reader, LogLevel level)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                if (line.Length > 4096)
                {
                    line = line[..4096];
                }
                _logger.Log(level, "Virtual-output worker {WorkerId}: {WorkerMessage}", _workerId, line);
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
