using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Lineup.VirtualOutputWorkerPackager;

/// <summary>Publishes the shipped isolated worker executables and writes verified package manifests.</summary>
internal static class Program
{
    private static readonly WorkerDefinition[] Workers =
    [
        new(
            "lineup-link",
            "lineup-link",
            "src/Plugins/LineupLink/Lineup.LineupLink.VirtualOutputHost/Lineup.LineupLink.VirtualOutputHost.csproj",
            "Lineup.LineupLink.VirtualOutputHost"),
        new(
            "hdhomerun",
            "hdhomerun",
            "src/Plugins/SiliconDust/Lineup.SiliconDust.HDHomeRun.VirtualOutputHost/Lineup.SiliconDust.HDHomeRun.VirtualOutputHost.csproj",
            "Lineup.SiliconDust.HDHomeRun.VirtualOutputHost"),
        new(
            "hdhomerun-discovery",
            "hdhomerun-discovery",
            "src/Plugins/SiliconDust/Lineup.SiliconDust.HDHomeRun.DiscoveryHost/Lineup.SiliconDust.HDHomeRun.DiscoveryHost.csproj",
            "Lineup.SiliconDust.HDHomeRun.DiscoveryHost"),
        new(
            "web-player",
            "web-player",
            "src/Plugins/WebPlayer/Lineup.WebPlayer.VirtualOutputHost/Lineup.WebPlayer.VirtualOutputHost.csproj",
            "Lineup.WebPlayer.VirtualOutputHost")
    ];

    /// <summary>Publishes all worker packages requested by the host build.</summary>
    /// <param name="args">Command-line packaging options.</param>
    /// <returns>Zero when every package succeeds; otherwise one.</returns>
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = PackagerOptions.Parse(args);
            foreach (var worker in Workers)
            {
                await PublishAsync(options, worker);
            }
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidOperationException or JsonException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static async Task PublishAsync(PackagerOptions options, WorkerDefinition worker)
    {
        var workerOutput = Path.Combine(options.OutputDirectory, worker.Id);
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

        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false
        }.AddArguments(arguments)) ?? throw new InvalidOperationException($"Unable to start dotnet publish for worker '{worker.Id}'.");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"dotnet publish failed for virtual-output worker '{worker.Id}'.");
        }

        var targetsWindows = options.RuntimeIdentifier is null
            ? OperatingSystem.IsWindows()
            : options.RuntimeIdentifier.StartsWith("win-", StringComparison.OrdinalIgnoreCase);
        var executableName = options.SelfContained
            ? targetsWindows
                ? $"{worker.AssemblyName}.exe"
                : worker.AssemblyName
            : $"{worker.AssemblyName}.dll";
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

    private static ProcessStartInfo AddArguments(this ProcessStartInfo startInfo, IEnumerable<string> arguments)
    {
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        return startInfo;
    }

    private sealed record WorkerDefinition(string Id, string AdapterKind, string ProjectPath, string AssemblyName);

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
