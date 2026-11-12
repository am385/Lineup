using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Lineup.Core.Tests.Architecture;

/// <summary>Verifies repository project layout and allowed production dependency directions.</summary>
public sealed class ProjectLayoutTests
{
    private static readonly IReadOnlySet<string> PluginSupportProjects =
        new HashSet<string>(
        [
            "Lineup.Plugin.SiliconDust.HDHomeRun.Api",
            "Lineup.Plugin.SiliconDust.HDHomeRun.Device",
            "Lineup.Plugin.GuideOutputs.Json",
            "Lineup.Plugin.GuideOutputs.Xmltv",
            "Lineup.Plugin.WebPlayer",
            "Lineup.Plugin.WebPlayer.FFmpeg",
            "Lineup.Plugin.LineupLink",
            "Lineup.Plugin.SiliconDust.HDHomeRun.VirtualTuner"
        ], StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlySet<string> PluginCoreDependencies =
        new HashSet<string>(
        [
            "Lineup.Core.Common",
            "Lineup.Core.Extensibility",
            "Lineup.Core.Streaming.FFmpeg",
            "Lineup.Core.VirtualOutputs.Hosting",
            "Lineup.Core.VirtualOutputs.Protocol"
        ], StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> AllowedDependencies =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Apps"] = new HashSet<string>(["Core", "Plugins"], StringComparer.OrdinalIgnoreCase),
            ["Core"] = new HashSet<string>(["Core"], StringComparer.OrdinalIgnoreCase),
            ["Plugins"] = new HashSet<string>(["Core", "Plugins"], StringComparer.OrdinalIgnoreCase)
        };

    private static readonly IReadOnlyDictionary<string, string[]> CoreProjectDependencies =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Lineup.Core.Common"] = [],
            ["Lineup.Core.Extensibility"] = ["Lineup.Core.Common"],
            ["Lineup.Core"] = ["Lineup.Core.Common", "Lineup.Core.Extensibility"],
            ["Lineup.Core.Hosting"] = ["Lineup.Core", "Lineup.Core.Streaming.FFmpeg"],
            ["Lineup.Core.Streaming.FFmpeg"] = ["Lineup.Core.Extensibility"],
            ["Lineup.Core.VirtualOutputs.Protocol"] = ["Lineup.Core.Common"],
            ["Lineup.Core.VirtualOutputs.Hosting"] = ["Lineup.Core.Common", "Lineup.Core.VirtualOutputs.Protocol"],
            ["Lineup.Core.VirtualOutputs.Runtime"] =
            [
                "Lineup.Core.Common", "Lineup.Core", "Lineup.Core.Hosting", "Lineup.Core.Extensibility", "Lineup.Core.Streaming.FFmpeg", "Lineup.Core.VirtualOutputs.Protocol"
            ]
        };

    private static readonly IReadOnlySet<string> TrustedHostProjects = new HashSet<string>(
        ["Lineup.Core", "Lineup.Core.Hosting", "Lineup.Core.VirtualOutputs.Runtime", "Lineup.Plugin.SiliconDust.HDHomeRun.DiscoveryRuntime"],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>Verifies project files, folders, namespaces, and output identities use matching role-based names.</summary>
    [Fact]
    public void ProjectIdentities_MatchRoleAndNamespace()
    {
        // Arrange
        var repositoryRoot = GetRepositoryRoot();
        var projects = new[] { "src", "tests", "scripts" }
            .SelectMany(folder => GetProjects(Path.Combine(repositoryRoot, folder)))
            .ToArray();

        // Act
        var violations = projects.SelectMany(project =>
        {
            var name = Path.GetFileNameWithoutExtension(project);
            var directory = Path.GetDirectoryName(project)!;
            var segments = Path.GetRelativePath(repositoryRoot, project).Split(Path.DirectorySeparatorChar);
            var role = segments[0] == "scripts" ? "Tools" : segments[1] switch
            {
                "Apps" => "App",
                "Plugins" => "Plugin",
                var value => value
            };
            var errors = new List<string>();
            if (Path.GetFileName(directory) != name || (name != $"Lineup.{role}" && !name.StartsWith($"Lineup.{role}.", StringComparison.Ordinal)))
            {
                errors.Add(project);
            }
            foreach (var property in XDocument.Load(project).Descendants().Where(element => element.Name.LocalName is "RootNamespace" or "AssemblyName"))
            {
                if (property.Value != name)
                {
                    errors.Add($"{name}: {property.Name} = {property.Value}");
                }
            }
            foreach (var source in Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories)
                .Where(path => !Path.GetRelativePath(directory, path).Split(Path.DirectorySeparatorChar).Any(segment => segment is "bin" or "obj")))
            {
                foreach (Match declaration in Regex.Matches(File.ReadAllText(source), @"(?m)^\s*namespace\s+([A-Za-z_][A-Za-z0-9_.]*)\s*[;{]"))
                {
                    var sourceNamespace = declaration.Groups[1].Value;
                    if (sourceNamespace != name && !sourceNamespace.StartsWith($"{name}.", StringComparison.Ordinal))
                    {
                        errors.Add($"{source}: {sourceNamespace}");
                    }
                }
            }
            return errors;
        }).ToArray();

        // Assert
        Assert.NotEmpty(projects);
        Assert.Empty(violations);
    }

    /// <summary>Verifies automated tests use their referenced subject project's name followed by Tests.</summary>
    [Fact]
    public void TestProjectNames_MatchReferencedSubject()
    {
        // Arrange
        var repositoryRoot = GetRepositoryRoot();
        var projects = GetProjects(Path.Combine(repositoryRoot, "tests"))
            .Where(project => XDocument.Load(project).Descendants().Any(element => element.Name.LocalName == "IsTestProject" && element.Value == "true"))
            .ToArray();

        // Act
        var violations = projects.Where(project =>
        {
            var name = Path.GetFileNameWithoutExtension(project);
            return !name.EndsWith(".Tests", StringComparison.Ordinal)
                || !XDocument.Load(project).Descendants()
                    .Where(element => element.Name.LocalName == "ProjectReference")
                    .Select(element => element.Attribute("Include")?.Value)
                    .OfType<string>()
                    .Select(reference => Path.GetFileNameWithoutExtension(ResolveProjectReference(project, reference)))
                    .Contains(name[..^".Tests".Length], StringComparer.Ordinal);
        }).ToArray();

        // Assert
        Assert.NotEmpty(projects);
        Assert.Empty(violations);
    }

    /// <summary>Verifies every project reference, including test helpers and build tools, resolves after renaming.</summary>
    [Fact]
    public void AllProjectReferences_ResolveToExistingProjects()
    {
        // Arrange
        var repositoryRoot = GetRepositoryRoot();
        var projects = new[] { "src", "tests", "scripts" }
            .SelectMany(folder => GetProjects(Path.Combine(repositoryRoot, folder)))
            .ToArray();

        // Act
        var violations = projects.SelectMany(project => XDocument.Load(project).Descendants()
            .Where(element => element.Name.LocalName == "ProjectReference")
            .Select(element => element.Attribute("Include")?.Value)
            .OfType<string>()
            .Where(reference => !File.Exists(ResolveProjectReference(project, reference)))
            .Select(reference => $"{project} -> {reference}")).ToArray();

        // Assert
        Assert.NotEmpty(projects);
        Assert.Empty(violations);
    }

    /// <summary>Verifies Web assets and release entrypoints use the matching stylesheet and executable identities.</summary>
    [Fact]
    public void WebDistributionReferences_MatchProjectIdentity()
    {
        // Arrange
        var repositoryRoot = GetRepositoryRoot();
        var webProject = Path.Combine(repositoryRoot, "src", "Apps", "Lineup.App.Web", "Lineup.App.Web.csproj");
        var name = Path.GetFileNameWithoutExtension(webProject);
        var app = File.ReadAllText(Path.Combine(Path.GetDirectoryName(webProject)!, "Components", "App.razor"));
        var dockerfile = File.ReadAllText(Path.Combine(repositoryRoot, "Dockerfile"));
        var launcher = File.ReadAllText(Path.Combine(repositoryRoot, "packaging", "windows", "Start-Lineup.cmd"));

        // Act
        var stylesheet = $"{name}.styles.css";
        var dockerEntrypoint = $"ENTRYPOINT [\"dotnet\", \"{name}.dll\"]";
        var executable = $"app\\{name}.exe";

        // Assert
        Assert.Contains(stylesheet, app, StringComparison.Ordinal);
        Assert.Contains(dockerEntrypoint, dockerfile, StringComparison.Ordinal);
        Assert.Contains(executable, launcher, StringComparison.Ordinal);
    }

    /// <summary>Verifies every source project belongs to one recognized architectural layer.</summary>
    [Fact]
    public void SourceProjects_AreNestedUnderRecognizedLayers()
    {
        // Arrange
        var repositoryRoot = GetRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "src");
        var projects = GetProjects(sourceRoot);

        // Act
        var violations = projects
            .Select(path => Path.GetRelativePath(sourceRoot, path))
            .Where(path => path.Split(Path.DirectorySeparatorChar).Length < 3
                || !AllowedDependencies.ContainsKey(path.Split(Path.DirectorySeparatorChar)[0]))
            .ToArray();

        // Assert
        Assert.NotEmpty(projects);
        Assert.Empty(violations);
    }

    /// <summary>Verifies production project references follow the architecture dependency rules.</summary>
    [Fact]
    public void ProjectReferences_FollowLayerDependencyRules()
    {
        // Arrange
        var repositoryRoot = GetRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "src");
        var projects = GetProjects(sourceRoot);

        // Act
        var violations = projects.SelectMany(project =>
        {
            var sourceLayer = GetLayer(sourceRoot, project);
            var document = XDocument.Load(project);
            return document
                .Descendants()
                .Where(element => element.Name.LocalName == "ProjectReference")
                .Select(element => element.Attribute("Include")?.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => ResolveProjectReference(project, value!))
                .Select(target => new
                {
                    Source = Path.GetRelativePath(repositoryRoot, project),
                    Target = Path.GetRelativePath(repositoryRoot, target),
                    TargetPath = target
                })
                .Where(reference => !IsAllowedReference(project, sourceLayer, sourceRoot, reference.TargetPath))
                .Select(reference => $"{reference.Source} -> {reference.Target}");
        }).ToArray();

        // Assert
        Assert.Empty(violations);
    }

    /// <summary>Verifies portable foundations, plugins, and workers have no transitive application or trusted-runtime dependencies.</summary>
    [Fact]
    public void PortableProjects_TransitiveReferences_ExcludeTrustedImplementations()
    {
        // Arrange
        var repositoryRoot = GetRepositoryRoot();
        var projects = GetProjects(Path.Combine(repositoryRoot, "src"));
        var graph = projects.ToDictionary(project => project, GetProjectReferences, StringComparer.OrdinalIgnoreCase);
        var portableProjects = projects.Where(project => IsPortableProject(Path.GetFileNameWithoutExtension(project))).ToArray();

        // Act
        var actual = portableProjects.SelectMany(project => GetDependencyClosure(project, graph)
            .Select(dependency => Path.GetFileNameWithoutExtension(dependency))
            .Where(name => !IsPortableProject(name))
            .Select(name => $"{Path.GetFileNameWithoutExtension(project)} -> {name}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        // Assert
        Assert.NotEmpty(portableProjects);
        Assert.Empty(actual);
    }

    /// <summary>Verifies portable projects cannot bypass project boundaries by adding direct database implementation packages.</summary>
    [Fact]
    public void PortableProjects_Packages_DoNotReferenceDatabaseImplementations()
    {
        // Arrange
        var projects = GetProjects(Path.Combine(GetRepositoryRoot(), "src"))
            .Where(project => IsPortableProject(Path.GetFileNameWithoutExtension(project)))
            .ToArray();
        var databasePackages = new[] { "Microsoft.EntityFrameworkCore", "Microsoft.Data.Sqlite", "SQLitePCLRaw" };

        // Act
        var violations = projects.SelectMany(project => XDocument.Load(project).Descendants()
            .Where(element => element.Name.LocalName == "PackageReference")
            .Select(element => element.Attribute("Include")?.Value)
            .OfType<string>()
            .Where(package => databasePackages.Any(prefix => package.Equals(prefix, StringComparison.OrdinalIgnoreCase) || package.StartsWith($"{prefix}.", StringComparison.OrdinalIgnoreCase)))
            .Select(package => $"{Path.GetFileNameWithoutExtension(project)} -> {package}"))
            .ToArray();

        // Assert
        Assert.NotEmpty(projects);
        Assert.Empty(violations);
    }

    /// <summary>Verifies shared model and extensibility projects remain free of package dependencies.</summary>
    [Theory]
    [InlineData("Lineup.Core.Common")]
    [InlineData("Lineup.Core.Extensibility")]
    public void ContractProjects_DoNotReferencePackages(string name)
    {
        // Arrange
        var project = Path.Combine(GetRepositoryRoot(), "src", "Core", name, $"{name}.csproj");

        // Act
        var packages = XDocument.Load(project).Descendants().Where(element => element.Name.LocalName == "PackageReference").ToArray();

        // Assert
        Assert.Empty(packages);
    }

    /// <summary>Verifies Windows-style MSBuild project-reference paths resolve on every host platform.</summary>
    [Fact]
    public void ProjectReferencePaths_WindowsSeparatorsResolveOnCurrentPlatform()
    {
        // Arrange
        var repositoryRoot = GetRepositoryRoot();
        var webProject = Path.Combine(repositoryRoot, "src", "Apps", "Lineup.App.Web", "Lineup.App.Web.csproj");
        var reference = XDocument.Load(webProject)
            .Descendants()
            .Where(element => element.Name.LocalName == "ProjectReference")
            .Select(element => element.Attribute("Include")?.Value)
            .First(value => value?.Contains('\\', StringComparison.Ordinal) == true)!;

        // Act
        var resolved = ResolveProjectReference(webProject, reference);

        // Assert
        Assert.True(File.Exists(resolved), resolved);
    }

    /// <summary>Verifies the CI solution contains every source, automated-test, and build-tool project exactly once.</summary>
    [Fact]
    public void SolutionProjects_MatchRepositoryProjects()
    {
        // Arrange
        var repositoryRoot = GetRepositoryRoot();
        var solution = XDocument.Load(Path.Combine(repositoryRoot, "Lineup.slnx"));
        var expected = GetProjects(Path.Combine(repositoryRoot, "src"))
            .Concat(GetProjects(Path.Combine(repositoryRoot, "tests")).Where(path => !IsOptInHardwareIntegrationProject(repositoryRoot, path)))
            .Concat(GetProjects(Path.Combine(repositoryRoot, "scripts")))
            .Select(path => Normalize(Path.GetRelativePath(repositoryRoot, path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Act
        var actual = solution
            .Descendants()
            .Where(element => element.Name.LocalName == "Project")
            .Select(element => element.Attribute("Path")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Normalize(value!))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Assert
        Assert.Equal(expected, actual, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(actual.Length, actual.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>Verifies hardware integration projects remain outside the CI solution.</summary>
    [Fact]
    public void HardwareIntegrationProjects_AreExcludedFromSolution()
    {
        // Arrange
        var repositoryRoot = GetRepositoryRoot();
        var solution = XDocument.Load(Path.Combine(repositoryRoot, "Lineup.slnx"));
        var integrationProjects = GetProjects(Path.Combine(repositoryRoot, "tests", "Integration"))
            .Where(path => IsOptInHardwareIntegrationProject(repositoryRoot, path))
            .Select(path => Normalize(Path.GetRelativePath(repositoryRoot, path)))
            .ToArray();

        // Act
        var solutionProjects = solution
            .Descendants()
            .Where(element => element.Name.LocalName == "Project")
            .Select(element => Normalize(element.Attribute("Path")?.Value ?? string.Empty))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Assert
        Assert.NotEmpty(integrationProjects);
        Assert.All(integrationProjects, project => Assert.DoesNotContain(project, solutionProjects));
        Assert.Contains("tests/Integration/Lineup.Integration.Streaming.Sweep.Tests/Lineup.Integration.Streaming.Sweep.Tests.csproj", solutionProjects);
    }

    /// <summary>Verifies network-facing worker executables do not directly reference trusted implementation layers or applications.</summary>
    [Fact]
    public void VirtualOutputWorkers_DoNotReferenceTrustedImplementationProjects()
    {
        // Arrange
        var repositoryRoot = GetRepositoryRoot();
        var workerProjects = GetProjects(Path.Combine(repositoryRoot, "src"))
            .Where(path =>
                (Path.GetFileNameWithoutExtension(path).EndsWith("Host", StringComparison.Ordinal) &&
                    path.Contains("VirtualOutput", StringComparison.OrdinalIgnoreCase)) ||
                Path.GetFileNameWithoutExtension(path).EndsWith(".DiscoveryHost", StringComparison.Ordinal))
            .ToArray();

        // Act
        var violations = workerProjects.SelectMany(project =>
            XDocument.Load(project)
                .Descendants()
                .Where(element => element.Name.LocalName == "ProjectReference")
                .Select(element => element.Attribute("Include")?.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => Path.GetFileNameWithoutExtension(ResolveProjectReference(project, value!)))
                .Where(name => TrustedHostProjects.Contains(name) || name == "Lineup.Core.Streaming.FFmpeg" || name.StartsWith("Lineup.App.", StringComparison.Ordinal))
                .Select(name => $"{Path.GetFileName(project)} -> {name}"))
            .ToArray();

        // Assert
        Assert.NotEmpty(workerProjects);
        Assert.Empty(violations);
    }

    /// <summary>Verifies presentation projects no longer own provider discovery socket services.</summary>
    [Fact]
    public void PresentationProjects_DoNotContainProviderDiscoverySocketServices()
    {
        // Arrange
        var repositoryRoot = GetRepositoryRoot();
        var webServices = Path.Combine(repositoryRoot, "src", "Apps", "Lineup.App.Web", "Services");

        // Act
        var violations = new[]
        {
            "HdHomeRunDiscoveryService.cs",
            "SsdpDiscoveryService.cs",
            "SsdpDiscoverySocket.cs"
        }.Where(file => File.Exists(Path.Combine(webServices, file))).ToArray();

        // Assert
        Assert.Empty(violations);
    }

    /// <summary>Verifies both application hosts package isolated workers for source runs and published distributions.</summary>
    [Fact]
    public void ApplicationHosts_ImportVirtualOutputWorkerPackagingTargets()
    {
        // Arrange
        var repositoryRoot = GetRepositoryRoot();
        var targetPath = Path.Combine(repositoryRoot, "build", "Lineup.Host.VirtualOutputWorkers.targets");
        var packagerProject = Path.Combine(repositoryRoot, "scripts", "Lineup.Tools.VirtualOutputWorkerPackager", "Lineup.Tools.VirtualOutputWorkerPackager.csproj");
        var expectedImport = @"..\..\..\build\Lineup.Host.VirtualOutputWorkers.targets";
        var hostProjects = new[]
        {
            Path.Combine(repositoryRoot, "src", "Apps", "Lineup.App.Web", "Lineup.App.Web.csproj"),
            Path.Combine(repositoryRoot, "src", "Apps", "Lineup.App.Tui", "Lineup.App.Tui.csproj")
        };

        // Act
        var imports = hostProjects.Select(project =>
            XDocument.Load(project)
                .Descendants()
                .Single(element => element.Name.LocalName == "Import" && element.Attribute("Project")?.Value == expectedImport)
                .Attribute("Project")
                ?.Value).ToArray();
        var targets = XDocument.Load(targetPath)
            .Descendants()
            .Where(element => element.Name.LocalName == "Target")
            .ToDictionary(
                element => element.Attribute("Name")?.Value ?? string.Empty,
                element => new
                {
                    BeforeTargets = element.Attribute("BeforeTargets")?.Value,
                    AfterTargets = element.Attribute("AfterTargets")?.Value,
                    DependsOnTargets = element.Attribute("DependsOnTargets")?.Value,
                    Condition = element.Attribute("Condition")?.Value,
                    Command = element.Descendants().SingleOrDefault(child => child.Name.LocalName == "Exec")?.Attribute("Command")?.Value
                },
                StringComparer.Ordinal);

        // Assert
        Assert.All(imports, import => Assert.Equal(expectedImport, import));
        Assert.Equal("Build", targets["PrepareVirtualOutputWorkersForBuild"].AfterTargets);
        Assert.Equal("ComputeFilesToPublish", targets["PublishVirtualOutputWorkersWithHost"].BeforeTargets);
        Assert.Equal("PrepareVirtualOutputWorkersForBuild", targets["PublishVirtualOutputWorkersWithHost"].DependsOnTargets);
        Assert.Null(targets["PublishVirtualOutputWorkersWithHost"].Command);
        var packageCommand = Assert.IsType<string>(targets["PrepareVirtualOutputWorkersForBuild"].Command);
        Assert.Contains("$(LineupWorkerPackagerRuntimeArguments)", packageCommand, StringComparison.Ordinal);
        Assert.Contains("$(LineupWorkerPackagerSelfContainedArgument)", packageCommand, StringComparison.Ordinal);
        Assert.Contains("--artifacts-path", packageCommand, StringComparison.Ordinal);
        Assert.All(targets.Values, target =>
        {
            Assert.Contains("$(DesignTimeBuild)", target.Condition, StringComparison.Ordinal);
            Assert.Contains("$(SkipVirtualOutputWorkerPublish)", target.Condition, StringComparison.Ordinal);
        });
        var publishFiles = XDocument.Load(targetPath).Descendants().Where(element => element.Name.LocalName == "ResolvedFileToPublish").ToArray();
        Assert.Single(publishFiles);
        Assert.Equal("true", publishFiles[0].Element("ExcludeFromSingleFile")?.Value);
        var inventory = Assert.Single(XDocument.Load(targetPath).Descendants(), element => element.Name.LocalName == "ReadLinesFromFile");
        Assert.Equal(@"$(TargetDir)virtual-output-workers\packaging.files", inventory.Attribute("File")?.Value);
        var upToDateOutputs = XDocument.Load(targetPath).Descendants().Where(element => element.Name.LocalName == "UpToDateCheckOutput").ToArray();
        Assert.Contains(upToDateOutputs, element => element.Attribute("Set")?.Value == "VirtualOutputPackaging" &&
            element.Attribute("Include")?.Value == @"$(OutputPath)virtual-output-workers\packaging.stamp;$(OutputPath)virtual-output-workers\packaging.files");
        Assert.Contains(upToDateOutputs, element => element.Attribute("Set")?.Value == "VirtualOutputPackageFiles");
        Assert.True(File.Exists(packagerProject));
        Assert.True(File.Exists(Path.Combine(repositoryRoot, "scripts", "Publish-VirtualOutputWorkers.ps1")));
    }

    /// <summary>Verifies release packaging uses the canonical guide-output directory setting and location.</summary>
    [Fact]
    public void ReleasePackaging_UsesCanonicalGuideOutputDirectory()
    {
        // Arrange
        var repositoryRoot = GetRepositoryRoot();
        var launcher = File.ReadAllText(Path.Combine(repositoryRoot, "packaging", "windows", "Start-Lineup.cmd"));
        var windowsPackageScript = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "Build-WindowsPackage.ps1"));
        var windowsReadme = File.ReadAllText(Path.Combine(repositoryRoot, "packaging", "windows", "WINDOWS-README.txt"));
        var dockerfile = File.ReadAllText(Path.Combine(repositoryRoot, "Dockerfile"));
        var readme = File.ReadAllText(Path.Combine(repositoryRoot, "README.md"));

        // Act
        var releaseConfiguration = string.Join(Environment.NewLine, launcher, dockerfile);

        // Assert
        Assert.Contains(@"if not exist ""%LINEUP_DATA_ROOT%\guide"" mkdir ""%LINEUP_DATA_ROOT%\guide""", launcher, StringComparison.Ordinal);
        Assert.Contains(@"set ""Lineup__GuideOutputDir=%LINEUP_DATA_ROOT%\guide""", launcher, StringComparison.Ordinal);
        Assert.Contains(@"if /I ""%~1""==""--validate-configuration"" exit /b 0", launcher, StringComparison.Ordinal);
        Assert.Contains("""call `"$launcherSourcePath`" --validate-configuration""", windowsPackageScript, StringComparison.Ordinal);
        Assert.Contains("@('data', 'guide', 'transient')", windowsPackageScript, StringComparison.Ordinal);
        Assert.Contains(@"%LOCALAPPDATA%\Lineup\guide\epg.xml", windowsReadme, StringComparison.Ordinal);
        Assert.Contains("RUN mkdir -p /appdata /guide /transient", dockerfile, StringComparison.Ordinal);
        Assert.Contains("ENV Lineup__GuideOutputDir=/guide", dockerfile, StringComparison.Ordinal);
        Assert.DoesNotContain("Lineup__XmltvPath", releaseConfiguration, StringComparison.Ordinal);
        Assert.DoesNotContain("/xmltv", dockerfile, StringComparison.Ordinal);
        Assert.DoesNotContain("guideOutput", readme, StringComparison.Ordinal);
    }

    /// <summary>Verifies trusted proxy metadata is normalized before any request consumer runs.</summary>
    [Fact]
    public void WebRequestPipeline_AppliesForwardedHeadersBeforeRequestConsumers()
    {
        // Arrange
        var repositoryRoot = GetRepositoryRoot();
        var program = File.ReadAllText(Path.Combine(repositoryRoot, "src", "Apps", "Lineup.App.Web", "Program.cs"));

        // Act
        var forwardedHeadersIndex = program.IndexOf("app.UseForwardedHeaders", StringComparison.Ordinal);
        var hstsIndex = program.IndexOf("app.UseHsts", StringComparison.Ordinal);
        var routingIndex = program.IndexOf("app.UseRouting", StringComparison.Ordinal);
        var requestLoggingIndex = program.IndexOf("app.UseMiddleware<ApiRequestLoggingMiddleware>", StringComparison.Ordinal);

        // Assert
        Assert.True(forwardedHeadersIndex >= 0);
        Assert.True(forwardedHeadersIndex < hstsIndex);
        Assert.True(forwardedHeadersIndex < routingIndex);
        Assert.True(forwardedHeadersIndex < requestLoggingIndex);
    }

    private static string[] GetProjects(string root) =>
        Directory.GetFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(segment => segment is "bin" or "obj"))
            .ToArray();

    private static string ResolveProjectReference(string sourceProject, string reference) =>
        Path.GetFullPath(
            Path.Combine(
                Path.GetDirectoryName(sourceProject)!,
                reference.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));

    private static string[] GetProjectReferences(string project) =>
        XDocument.Load(project).Descendants()
            .Where(element => element.Name.LocalName == "ProjectReference")
            .Select(element => element.Attribute("Include")?.Value)
            .OfType<string>()
            .Select(reference => ResolveProjectReference(project, reference))
            .ToArray();

    private static IReadOnlySet<string> GetDependencyClosure(string project, IReadOnlyDictionary<string, string[]> graph)
    {
        var dependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(graph[project]);
        while (pending.TryPop(out var dependency))
        {
            if (!dependencies.Add(dependency))
            {
                continue;
            }
            foreach (var target in graph[dependency])
            {
                pending.Push(target);
            }
        }
        return dependencies;
    }

    private static bool IsPortableProject(string name) =>
        !TrustedHostProjects.Contains(name) && !name.StartsWith("Lineup.App.", StringComparison.OrdinalIgnoreCase);

    private static string GetLayer(string sourceRoot, string projectPath)
    {
        var relativePath = Path.GetRelativePath(sourceRoot, projectPath);
        return relativePath.Split(Path.DirectorySeparatorChar)[0];
    }

    private static bool IsAllowedReference(string sourceProject, string sourceLayer, string sourceRoot, string targetPath)
    {
        if (!File.Exists(targetPath))
        {
            return false;
        }

        var sourceProjectName = Path.GetFileNameWithoutExtension(sourceProject);
        var targetProjectName = Path.GetFileNameWithoutExtension(targetPath);
        if (string.Equals(sourceLayer, "Core", StringComparison.OrdinalIgnoreCase))
        {
            return CoreProjectDependencies.TryGetValue(sourceProjectName, out var allowed)
                && allowed.Contains(targetProjectName, StringComparer.OrdinalIgnoreCase);
        }

        var targetLayer = GetLayer(sourceRoot, targetPath);
        if (!AllowedDependencies[sourceLayer].Contains(targetLayer))
        {
            return false;
        }

        if (!string.Equals(sourceLayer, "Plugins", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(targetLayer, "Core", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(sourceProjectName, "Lineup.Plugin.SiliconDust.HDHomeRun.DiscoveryRuntime", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(targetProjectName, "Lineup.Core.VirtualOutputs.Runtime", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return PluginCoreDependencies.Contains(targetProjectName);
        }

        return string.Equals(GetPluginFamily(sourceRoot, sourceProject), GetPluginFamily(sourceRoot, targetPath), StringComparison.OrdinalIgnoreCase)
            && PluginSupportProjects.Contains(targetProjectName);
    }

    private static string GetPluginFamily(string sourceRoot, string projectPath) =>
        Path.GetRelativePath(sourceRoot, projectPath).Split(Path.DirectorySeparatorChar)[1];

    private static bool IsOptInHardwareIntegrationProject(string repositoryRoot, string projectPath) =>
        Normalize(Path.GetRelativePath(repositoryRoot, projectPath)).StartsWith("tests/Integration/", StringComparison.OrdinalIgnoreCase)
        && !Normalize(Path.GetRelativePath(repositoryRoot, projectPath))
            .Equals("tests/Integration/Lineup.Integration.Streaming.Sweep.Tests/Lineup.Integration.Streaming.Sweep.Tests.csproj", StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path) => path.Replace('\\', '/');

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Lineup.slnx")))
        {
            directory = directory.Parent;
        }
        return Assert.IsType<DirectoryInfo>(directory).FullName;
    }
}
