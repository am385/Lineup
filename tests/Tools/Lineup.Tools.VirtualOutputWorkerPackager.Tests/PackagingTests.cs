using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace Lineup.Tools.VirtualOutputWorkerPackager.Tests;

/// <summary>Verifies shared packaging entrypoints, real SDK publishing, manifest contracts and verified-cache repair.</summary>
public sealed class PackagingTests
{
    /// <summary>Verifies Docker, MSBuild and Windows use the common packaging path rather than duplicate worker lists or manifests.</summary>
    [Fact]
    public void DistributionEntrypoints_ShareInventoryAndPackager()
    {
        // Arrange
        var root = RepositoryRoot();
        var docker = File.ReadAllText(Path.Combine(root, "Dockerfile"));
        var targets = XDocument.Load(Path.Combine(root, "build", "Lineup.Host.VirtualOutputWorkers.targets"));
        var windows = File.ReadAllText(Path.Combine(root, "scripts", "Build-WindowsPackage.ps1"));

        // Act
        var imports = targets.Descendants("Import").Select(element => (string?)element.Attribute("Project")).ToArray();

        // Assert
        Assert.Contains("Lineup.VirtualOutputWorkers.props", imports);
        Assert.Empty(targets.Descendants("_LineupBuiltInWorker"));
        Assert.DoesNotContain("dotnet publish src/Plugins/", docker, StringComparison.Ordinal);
        Assert.DoesNotContain("sha256sum", docker, StringComparison.Ordinal);
        Assert.DoesNotContain("printf '%s' \"{", docker, StringComparison.Ordinal);
        Assert.Contains("COPY build/", docker, StringComparison.Ordinal);
        Assert.Contains("COPY scripts/Lineup.Tools.VirtualOutputWorkerPackager/", docker, StringComparison.Ordinal);
        Assert.DoesNotContain("COPY --from=build /app/workers", docker, StringComparison.Ordinal);
        Assert.Contains("Publish-VirtualOutputWorkers.ps1", windows, StringComparison.Ordinal);
        Assert.Contains("SkipVirtualOutputWorkerPublish=true", windows, StringComparison.Ordinal);
    }

    /// <summary>Verifies one packaging invocation feeds both normal builds and standard publish-file copying, including no-build publishes.</summary>
    [Fact]
    public void HostTargets_PackageOnceAndPreserveOptOutBoundaries()
    {
        // Arrange
        var targets = XDocument.Load(Path.Combine(RepositoryRoot(), "build", "Lineup.Host.VirtualOutputWorkers.targets"));

        // Act
        var package = Assert.Single(targets.Descendants("Target"), target => target.Descendants("Exec").Any());
        var publish = targets.Descendants("Target").Single(target => target.Descendants("ResolvedFileToPublish").Any());

        // Assert
        Assert.Equal("Build", (string?)package.Attribute("AfterTargets"));
        Assert.Contains("DesignTimeBuild", (string?)package.Attribute("Condition"));
        Assert.Contains("SkipVirtualOutputWorkerPublish", (string?)package.Attribute("Condition"));
        Assert.Equal((string?)package.Attribute("Name"), (string?)publish.Attribute("DependsOnTargets"));
        Assert.Equal("ComputeFilesToPublish", (string?)publish.Attribute("BeforeTargets"));
        Assert.Contains("DesignTimeBuild", (string?)publish.Attribute("Condition"));
        Assert.Contains("SkipVirtualOutputWorkerPublish", (string?)publish.Attribute("Condition"));
        Assert.Equal("true", (string?)publish.Descendants("ExcludeFromSingleFile").Single());
    }

    /// <summary>Verifies real SDK packages retain manifest/version/hash contracts, avoid unchanged rewrites and repair installed/cache corruption.</summary>
    [Theory]
    [InlineData("3.0.0")]
    [InlineData("3.0.0-dev.42+packaging")]
    public async Task Main_RealPublish_ReusesAndRepairsVerifiedPackages(string version)
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-packager-");
        try
        {
            Directory.CreateDirectory(Path.Combine(root.FullName, "src", "Fixture"));
            Directory.CreateDirectory(Path.Combine(root.FullName, "scripts", "Lineup.Tools.VirtualOutputWorkerPackager"));
            Directory.CreateDirectory(Path.Combine(root.FullName, "build"));
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "src", "Fixture", "Fixture.csproj"),
                """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType></PropertyGroup></Project>""", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "src", "Fixture", "Program.cs"), """System.Console.WriteLine("fixture");""", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "build", "Lineup.VirtualOutputWorkers.props"),
                """<Project><ItemGroup><_LineupBuiltInWorker Include="fixture" AdapterKind="fixture-adapter" ProjectPath="src/Fixture/Fixture.csproj" AssemblyName="Fixture" /></ItemGroup></Project>""",
                TestContext.Current.CancellationToken);
            var output = Path.Combine(root.FullName, "installed");
            var arguments = new[]
            {
                "--repository-root", root.FullName, "--output", output, "--configuration", "Release", "--version", version, "--source-revision", "packaging-test"
            };

            // Act
            Assert.Equal(0, await Program.Main(arguments));

            // Assert
            var worker = Path.Combine(output, "fixture");
            var manifestPath = Path.Combine(worker, "fixture.virtual-output.json");
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, TestContext.Current.CancellationToken));
            var json = manifest.RootElement;
            Assert.Equal(1, json.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("fixture", json.GetProperty("workerId").GetString());
            Assert.Equal(version, json.GetProperty("version").GetString());
            Assert.Equal(version, json.GetProperty("minimumLineupVersion").GetString());
            Assert.Equal("Lineup", json.GetProperty("publisher").GetString());
            Assert.Equal("Fixture.dll", json.GetProperty("executablePath").GetString());
            Assert.Equal(["fixture-adapter"], json.GetProperty("adapterKinds").EnumerateArray().Select(value => value.GetString()).ToArray());
            Assert.Equal("fixture-adapter", json.GetProperty("settingsDescriptor").GetProperty("adapterKind").GetString());
            Assert.Empty(json.GetProperty("settingsDescriptor").GetProperty("fields").EnumerateArray());
            Assert.Empty(json.GetProperty("platforms").EnumerateArray());
            Assert.Equal(1, json.GetProperty("minimumProtocolVersion").GetInt32());
            Assert.Equal(1, json.GetProperty("maximumProtocolVersion").GetInt32());
            var dll = Path.Combine(worker, "Fixture.dll");
            var deps = Path.Combine(worker, "Fixture.deps.json");
            var executableBytes = await File.ReadAllBytesAsync(dll, TestContext.Current.CancellationToken);
            Assert.Equal(json.GetProperty("executableSha256").GetString(), Convert.ToHexString(SHA256.HashData(executableBytes)));
            Assert.True(File.Exists(deps));
            Assert.True(File.Exists(Path.Combine(worker, "Fixture.runtimeconfig.json")));
            var cache = Directory.GetDirectories(Path.Combine(root.FullName, "out", "virtual-output-worker-packages")).Single();
            var cachedDll = Path.Combine(cache, "fixture", "Fixture.dll");
            var cachedBytes = await File.ReadAllBytesAsync(cachedDll, TestContext.Current.CancellationToken);
            var installedTime = File.GetLastWriteTimeUtc(dll);
            var cachedTime = File.GetLastWriteTimeUtc(cachedDll);
            var stamp = await File.ReadAllTextAsync(Path.Combine(output, "packaging.stamp"), TestContext.Current.CancellationToken);

            // Act
            Assert.Equal(0, await Program.Main(arguments));

            // Assert
            Assert.Equal(installedTime, File.GetLastWriteTimeUtc(dll));
            Assert.Equal(cachedTime, File.GetLastWriteTimeUtc(cachedDll));
            Assert.Equal(stamp, await File.ReadAllTextAsync(Path.Combine(output, "packaging.stamp"), TestContext.Current.CancellationToken));

            // Act
            await File.WriteAllTextAsync(dll, "modified installed executable", TestContext.Current.CancellationToken);
            File.Delete(deps);
            Assert.Equal(0, await Program.Main(arguments));

            // Assert
            Assert.Equal(cachedBytes, await File.ReadAllBytesAsync(dll, TestContext.Current.CancellationToken));
            Assert.True(File.Exists(deps));
            Assert.Equal(cachedTime, File.GetLastWriteTimeUtc(cachedDll));

            // Act
            await File.WriteAllTextAsync(cachedDll, "corrupt cached executable", TestContext.Current.CancellationToken);
            File.SetLastWriteTimeUtc(cachedDll, DateTime.UnixEpoch);
            Assert.Equal(0, await Program.Main(arguments));

            // Assert
            Assert.Equal(cachedBytes, await File.ReadAllBytesAsync(cachedDll, TestContext.Current.CancellationToken));
            Assert.Equal(cachedBytes, await File.ReadAllBytesAsync(dll, TestContext.Current.CancellationToken));
            Assert.Equal(cachedTime, File.GetLastWriteTimeUtc(cachedDll));

            // Arrange
            var repository = RepositoryRoot();
            foreach (var name in new[] { "Program.cs", "Lineup.Tools.VirtualOutputWorkerPackager.csproj" })
            {
                var relative = Path.Combine("scripts", "Lineup.Tools.VirtualOutputWorkerPackager", name);
                File.Copy(Path.Combine(repository, relative), Path.Combine(root.FullName, relative));
            }
            File.Copy(Path.Combine(repository, "build", "Lineup.Host.VirtualOutputWorkers.targets"), Path.Combine(root.FullName, "build", "Lineup.Host.VirtualOutputWorkers.targets"));
            var host = Directory.CreateDirectory(Path.Combine(root.FullName, "src", "Host"));
            await File.WriteAllTextAsync(Path.Combine(host.FullName, "Host.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType></PropertyGroup>
                  <Import Project="../../build/Lineup.Host.VirtualOutputWorkers.targets" />
                </Project>
                """, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(host.FullName, "Program.cs"), """System.Console.WriteLine("host");""", TestContext.Current.CancellationToken);
            var published = Path.Combine(root.FullName, "published");
            string[] publish = ["publish", Path.Combine(host.FullName, "Host.csproj"), "-c", "Release", $"-p:Version={version}", "-p:SourceRevisionId=packaging-test", "-o", published];

            // Act
            var first = await RunDotnetAsync(root.FullName, publish);

            // Assert
            Assert.Equal(1, first.Split("Verified virtual-output worker packages", StringSplitOptions.None).Length - 1);
            var publishedDll = Path.Combine(published, "virtual-output-workers", "fixture", "Fixture.dll");
            Assert.Equal(cachedBytes, await File.ReadAllBytesAsync(publishedDll, TestContext.Current.CancellationToken));
            Assert.True(File.Exists(Path.Combine(published, "virtual-output-workers", "fixture", "Fixture.deps.json")));
            var publishedTime = File.GetLastWriteTimeUtc(publishedDll);
            var installedRoot = Path.GetDirectoryName(Directory.GetFiles(Path.Combine(host.FullName, "bin"), "packaging.stamp", SearchOption.AllDirectories).Single())!;
            await File.WriteAllTextAsync(Path.Combine(installedRoot, "unverified-stale.dll"), "must not publish", TestContext.Current.CancellationToken);

            // Act
            var repeated = await RunDotnetAsync(root.FullName, [.. publish, "--no-build", "--no-restore"]);

            // Assert
            Assert.Equal(1, repeated.Split("Verified virtual-output worker packages", StringSplitOptions.None).Length - 1);
            Assert.Equal(publishedTime, File.GetLastWriteTimeUtc(publishedDll));
            Assert.False(File.Exists(Path.Combine(published, "virtual-output-workers", "unverified-stale.dll")));

            // Act
            var optedOut = Path.Combine(root.FullName, "opted-out");
            var skipped = await RunDotnetAsync(root.FullName, [.. publish[..^2], "-p:SkipVirtualOutputWorkerPublish=true", "-o", optedOut, "--no-build", "--no-restore"]);

            // Assert
            Assert.DoesNotContain("Verified virtual-output worker packages", skipped, StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(optedOut, "virtual-output-workers")));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static async Task<string> RunDotnetAsync(string workingDirectory, string[] arguments)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start the owned packaging regression process.");
        var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var error = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            var stdout = await output;
            var stderr = await error;
            Assert.True(process.ExitCode == 0, $"dotnet {string.Join(' ', arguments)} failed.{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
            return stdout;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    /// <summary>Verifies target runtime, rather than the packaging host, selects the manifest executable.</summary>
    [Theory]
    [InlineData("win-x64", false, "Worker.dll")]
    [InlineData("win-arm64", false, "Worker.dll")]
    [InlineData("linux-x64", false, "Worker.dll")]
    [InlineData("linux-arm64", false, "Worker.dll")]
    [InlineData("win-x64", true, "Worker.exe")]
    [InlineData("win-arm64", true, "Worker.exe")]
    [InlineData("linux-x64", true, "Worker")]
    [InlineData("linux-arm64", true, "Worker")]
    [InlineData("osx-arm64", true, "Worker")]
    public void ExecutableName_UsesDeploymentModeAndTargetPlatform(string runtime, bool selfContained, string expected)
    {
        // Arrange
        const string assembly = "Worker";

        // Act
        var executable = Program.GetExecutableName(assembly, runtime, selfContained);

        // Assert
        Assert.Equal(expected, executable);
    }

    /// <summary>Verifies the authoritative inventory preserves all four shipped workers and literal MSBuild/tool metadata.</summary>
    [Fact]
    public void SharedInventory_PreservesShippedWorkerContracts()
    {
        // Arrange
        var root = RepositoryRoot();
        var inventory = XDocument.Load(Path.Combine(root, "build", "Lineup.VirtualOutputWorkers.props"));

        // Act
        var workers = inventory.Descendants("_LineupBuiltInWorker").ToArray();

        // Assert
        Assert.Equal(["lineup-link", "hdhomerun", "hdhomerun-discovery", "web-player"], workers.Select(worker => (string?)worker.Attribute("Include")).ToArray());
        foreach (var worker in workers)
        {
            Assert.Equal((string?)worker.Attribute("Include"), (string?)worker.Attribute("AdapterKind"));
            var project = Assert.IsType<string>((string?)worker.Attribute("ProjectPath"));
            var assembly = Assert.IsType<string>((string?)worker.Attribute("AssemblyName"));
            Assert.Equal(assembly, Path.GetFileNameWithoutExtension(project));
            Assert.True(File.Exists(Path.Combine(root, project.Replace('/', Path.DirectorySeparatorChar))));
            Assert.All(worker.Attributes(), attribute => Assert.DoesNotContain("$(", attribute.Value, StringComparison.Ordinal));
            Assert.All(worker.Attributes(), attribute => Assert.DoesNotContain("%(", attribute.Value, StringComparison.Ordinal));
        }
    }

    /// <summary>Verifies invalid shared inventories fail before creating cache/output state or launching publishers.</summary>
    [Theory]
    [InlineData("empty")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("malformed")]
    public async Task Main_InvalidInventory_FailsBeforePackaging(string state)
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-invalid-inventory-");
        try
        {
            Directory.CreateDirectory(Path.Combine(root.FullName, "build"));
            var item = new XElement("_LineupBuiltInWorker", new XAttribute("Include", "fixture"), new XAttribute("AdapterKind", "fixture"),
                new XAttribute("ProjectPath", "unused.csproj"), new XAttribute("AssemblyName", "Fixture"));
            var group = new XElement("ItemGroup", item);
            if (state == "empty")
            {
                group.RemoveNodes();
            }
            else if (state == "missing")
            {
                item.Attribute("AssemblyName")!.Remove();
            }
            else if (state == "duplicate")
            {
                group.Add(new XElement(item));
            }
            var path = Path.Combine(root.FullName, "build", "Lineup.VirtualOutputWorkers.props");
            if (state == "malformed")
            {
                await File.WriteAllTextAsync(path, "<Project>", TestContext.Current.CancellationToken);
            }
            else
            {
                new XDocument(new XElement("Project", group)).Save(path);
            }

            // Act
            var result = await Program.Main(["--repository-root", root.FullName, "--output", Path.Combine(root.FullName, "installed"), "--configuration", "Release", "--version", "3.0.0"]);

            // Assert
            Assert.Equal(1, result);
            Assert.False(Directory.Exists(Path.Combine(root.FullName, "out")));
            Assert.False(Directory.Exists(Path.Combine(root.FullName, "installed")));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Lineup.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
