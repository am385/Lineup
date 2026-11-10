using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Lineup.Tools.VirtualOutputWorkerPackager;

/// <summary>Publishes the shipped isolated worker executables and writes verified package manifests.</summary>
internal static class Program
{
    /// <summary>Publishes all worker packages requested by the host build.</summary>
    /// <param name="args">Command-line packaging options.</param>
    /// <returns>Zero when every package succeeds; otherwise one.</returns>
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = PackagerOptions.Parse(args);
            await PackageAsync(options);
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidOperationException or JsonException or System.Xml.XmlException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static async Task PackageAsync(PackagerOptions options)
    {
        var workers = ReadWorkers(options.RepositoryRoot);
        var cacheRoot = Path.Combine(options.RepositoryRoot, "out", "virtual-output-worker-packages");
        Directory.CreateDirectory(cacheRoot);
        await using var cacheLock = await AcquireLockAsync(Path.Combine(cacheRoot, "packaging.lock"));
        var configurationKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { options.Configuration, options.Version, options.RuntimeIdentifier, options.SelfContained, options.SourceRevisionId }))));
        var cacheDirectory = Path.Combine(cacheRoot, configurationKey[..24]);
        var stampPath = Path.Combine(cacheDirectory, "packages.json");
        var fingerprint = await FingerprintAsync(options.RepositoryRoot);
        var stamp = File.Exists(stampPath)
            ? JsonSerializer.Deserialize<PackageStamp>(await File.ReadAllTextAsync(stampPath))
            : null;
        if (stamp is null || stamp.Fingerprint != fingerprint || !await VerifyFilesAsync(cacheDirectory, stamp.Files))
        {
            Directory.CreateDirectory(cacheDirectory);
            foreach (var worker in workers)
            {
                await PublishAsync(options, worker, cacheDirectory);
            }
            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var worker in workers)
            {
                foreach (var file in Directory.EnumerateFiles(Path.Combine(cacheDirectory, worker.Id), "*", SearchOption.AllDirectories))
                {
                    files.Add(Path.GetRelativePath(cacheDirectory, file), await HashFileAsync(file));
                }
            }
            stamp = new PackageStamp(fingerprint, files);
            await File.WriteAllTextAsync(stampPath, JsonSerializer.Serialize(stamp));
        }
        Directory.CreateDirectory(options.OutputDirectory);
        foreach (var file in stamp.Files)
        {
            var destination = Path.Combine(options.OutputDirectory, file.Key);
            if (File.Exists(destination) && await HashFileAsync(destination) == file.Value)
            {
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Path.Combine(cacheDirectory, file.Key), destination, overwrite: true);
        }
        await File.WriteAllLinesAsync(Path.Combine(options.OutputDirectory, "packaging.files"), stamp.Files.Keys.Order(StringComparer.Ordinal));
        await File.WriteAllTextAsync(Path.Combine(options.OutputDirectory, "packaging.stamp"), $"{configurationKey}:{fingerprint}");
        Console.WriteLine($"Verified virtual-output worker packages in '{options.OutputDirectory}'.");
    }

    private static WorkerDefinition[] ReadWorkers(string repository)
    {
        var path = Path.Combine(repository, "build", "Lineup.VirtualOutputWorkers.props");
        var workers = XDocument.Load(path).Descendants("_LineupBuiltInWorker").Select(item => new WorkerDefinition(
            RequiredAttribute(item, "Include"),
            RequiredAttribute(item, "AdapterKind"),
            RequiredAttribute(item, "ProjectPath"),
            RequiredAttribute(item, "AssemblyName"))).ToArray();
        if (workers.Length == 0 || workers.Select(worker => worker.Id).Distinct(StringComparer.Ordinal).Count() != workers.Length)
        {
            throw new InvalidOperationException($"Worker inventory '{path}' must contain nonempty, unique worker definitions.");
        }
        return workers;
    }

    private static string RequiredAttribute(XElement item, string name) =>
        !string.IsNullOrWhiteSpace((string?)item.Attribute(name))
            ? item.Attribute(name)!.Value
            : throw new InvalidOperationException($"Worker inventory item requires '{name}'.");

    private static async Task<FileStream> AcquireLockAsync(string path)
    {
        var waiting = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException exception) when ((exception.HResult & 0xffff) is 11 or 32 or 33)
            {
                if (waiting.Elapsed > TimeSpan.FromMinutes(10))
                {
                    throw new IOException($"Timed out waiting for the virtual-output packaging lock '{path}'.", exception);
                }
                await Task.Delay(100);
            }
        }
    }

    private static async Task<string> FingerprintAsync(string repository)
    {
        var inputs = Directory.EnumerateFiles(Path.Combine(repository, "src"), "*", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(repository, "scripts", "Lineup.Tools.VirtualOutputWorkerPackager"), "*", SearchOption.AllDirectories))
            .Concat(Directory.EnumerateFiles(Path.Combine(repository, "build"), "*", SearchOption.AllDirectories))
            .Concat(Directory.EnumerateFiles(repository, "*", SearchOption.TopDirectoryOnly).Where(path =>
                Path.GetExtension(path) is ".props" or ".targets" || Path.GetFileName(path) is "global.json" or ".editorconfig" ||
                string.Equals(Path.GetFileName(path), "NuGet.Config", StringComparison.OrdinalIgnoreCase)))
            .Where(path => !Path.GetRelativePath(repository, path).Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            .Order(StringComparer.Ordinal);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var input in inputs)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(repository, input)));
            hash.AppendData(Convert.FromHexString(await HashFileAsync(input)));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static async Task<bool> VerifyFilesAsync(string root, IReadOnlyDictionary<string, string> files)
    {
        if (files.Count == 0)
        {
            return false;
        }
        foreach (var file in files)
        {
            var path = Path.Combine(root, file.Key);
            if (!File.Exists(path) || await HashFileAsync(path) != file.Value)
            {
                return false;
            }
        }
        return true;
    }

    private static async Task<string> HashFileAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private static async Task PublishAsync(PackagerOptions options, WorkerDefinition worker, string cacheDirectory)
    {
        var workerOutput = Path.Combine(cacheDirectory, worker.Id);
        if (Directory.Exists(workerOutput))
        {
            Directory.Delete(workerOutput, recursive: true);
        }
        Directory.CreateDirectory(workerOutput);

        var arguments = new List<string>
        {
            "publish",
            Path.Combine(options.RepositoryRoot, worker.ProjectPath.Replace('/', Path.DirectorySeparatorChar)),
            "--configuration",
            options.Configuration,
            "--artifacts-path",
            Path.Combine(cacheDirectory, "build"),
            "--self-contained",
            options.SelfContained.ToString().ToLowerInvariant(),
            "--output",
            workerOutput,
            $"-p:Version={options.Version}",
            "-p:IncludeSourceRevisionInInformationalVersion=false"
        };
        if (!string.IsNullOrWhiteSpace(options.RuntimeIdentifier))
        {
            arguments.Add("--runtime");
            arguments.Add(options.RuntimeIdentifier);
        }
        if (!string.IsNullOrWhiteSpace(options.SourceRevisionId))
        {
            arguments.Add($"-p:SourceRevisionId={options.SourceRevisionId}");
        }

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false
        }.AddArguments(arguments)) ?? throw new InvalidOperationException($"Unable to start dotnet publish for worker '{worker.Id}'.");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"dotnet publish failed for virtual-output worker '{worker.Id}'.");
        }

        var executableName = GetExecutableName(worker.AssemblyName, options.RuntimeIdentifier, options.SelfContained);
        var executablePath = Path.Combine(workerOutput, executableName);
        if (!File.Exists(executablePath))
        {
            throw new IOException($"Published virtual-output executable '{executablePath}' was not found.");
        }

        await using var executableStream = File.OpenRead(executablePath);
        var manifest = new
        {
            schemaVersion = 1,
            workerId = worker.Id,
            version = options.Version,
            publisher = "Lineup",
            executablePath = executableName,
            executableSha256 = Convert.ToHexString(await SHA256.HashDataAsync(executableStream)),
            adapterKinds = new[] { worker.AdapterKind },
            platforms = Array.Empty<object>(),
            minimumProtocolVersion = 1,
            maximumProtocolVersion = 1,
            minimumLineupVersion = options.Version,
            settingsDescriptor = new
            {
                adapterKind = worker.AdapterKind,
                fields = Array.Empty<object>()
            }
        };
        var manifestPath = Path.Combine(workerOutput, $"{worker.Id}.virtual-output.json");
        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Selects the framework-dependent DLL or target-platform self-contained apphost.</summary>
    /// <param name="assemblyName">Worker assembly name.</param>
    /// <param name="runtimeIdentifier">Explicit target runtime, or null for the current host.</param>
    /// <param name="selfContained">Whether the worker includes its runtime.</param>
    /// <returns>The executable filename recorded in the verified manifest.</returns>
    internal static string GetExecutableName(string assemblyName, string? runtimeIdentifier, bool selfContained)
    {
        var targetsWindows = runtimeIdentifier is null ? OperatingSystem.IsWindows() : runtimeIdentifier.StartsWith("win-", StringComparison.OrdinalIgnoreCase);
        return selfContained ? targetsWindows ? $"{assemblyName}.exe" : assemblyName : $"{assemblyName}.dll";
    }

    private static ProcessStartInfo AddArguments(this ProcessStartInfo startInfo, IEnumerable<string> arguments)
    {
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        return startInfo;
    }

    private sealed record WorkerDefinition(string Id, string AdapterKind, string ProjectPath, string AssemblyName);

    private sealed record PackageStamp(string Fingerprint, Dictionary<string, string> Files);

    private sealed record PackagerOptions(
        string RepositoryRoot,
        string OutputDirectory,
        string Configuration,
        string Version,
        string? RuntimeIdentifier,
        bool SelfContained,
        string? SourceRevisionId)
    {
        public static PackagerOptions Parse(IReadOnlyList<string> args)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var selfContained = false;
            for (var index = 0; index < args.Count; index++)
            {
                var argument = args[index];
                if (string.Equals(argument, "--self-contained", StringComparison.Ordinal))
                {
                    selfContained = true;
                    continue;
                }
                if (!argument.StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Count)
                {
                    throw new ArgumentException($"Invalid worker-packaging argument '{argument}'.");
                }
                values[argument] = args[++index];
            }

            var repositoryRoot = Require(values, "--repository-root");
            var outputDirectory = Require(values, "--output");
            var configuration = Require(values, "--configuration");
            if (configuration is not ("Debug" or "Release"))
            {
                throw new ArgumentException($"Unsupported build configuration '{configuration}'.");
            }

            return new PackagerOptions(
                Path.GetFullPath(repositoryRoot),
                Path.GetFullPath(outputDirectory),
                configuration,
                Require(values, "--version"),
                values.GetValueOrDefault("--runtime"),
                selfContained,
                values.GetValueOrDefault("--source-revision"));
        }

        private static string Require(IReadOnlyDictionary<string, string> values, string key) =>
            values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : throw new ArgumentException($"Worker packaging requires {key}.");
    }
}
