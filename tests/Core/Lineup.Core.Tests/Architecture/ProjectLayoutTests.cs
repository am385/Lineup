using System.Xml.Linq;
using Xunit;

namespace Lineup.Core.Tests.Architecture;

/// <summary>Verifies repository project layout and allowed production dependency directions.</summary>
public sealed class ProjectLayoutTests
{
    private static readonly IReadOnlySet<string> PluginSupportProjects =
        new HashSet<string>(
        [
            "Lineup.SiliconDust.HDHomeRun.Api",
            "Lineup.SiliconDust.HDHomeRun.Device",
            "Lineup.GuideOutputs.Json",
            "Lineup.GuideOutputs.Xmltv",
            "Lineup.WebPlayer",
            "Lineup.WebPlayer.FFmpeg",
            "Lineup.LineupLink",
            "Lineup.SiliconDust.HDHomeRun.VirtualTuner"
        ], StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlySet<string> PluginCoreDependencies =
        new HashSet<string>(
        [
            "Lineup.Common",
            "Lineup.Extensibility",
            "Lineup.Streaming.FFmpeg",
            "Lineup.VirtualOutputs.Hosting",
            "Lineup.VirtualOutputs.Protocol"
        ], StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> AllowedDependencies =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Apps"] = new HashSet<string>(["Core", "Plugins"], StringComparer.OrdinalIgnoreCase),
            ["Core"] = new HashSet<string>(["Core"], StringComparer.OrdinalIgnoreCase),
            ["Plugins"] = new HashSet<string>(["Core", "Plugins"], StringComparer.OrdinalIgnoreCase)
        };

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

    /// <summary>Verifies Windows-style MSBuild project-reference paths resolve on every host platform.</summary>
    [Fact]
    public void ProjectReferencePaths_WindowsSeparatorsResolveOnCurrentPlatform()
    {
        // Arrange
        var repositoryRoot = GetRepositoryRoot();
        var webProject = Path.Combine(repositoryRoot, "src", "Apps", "Lineup.Web", "Lineup.Web.csproj");
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

    /// <summary>Verifies the solution contains every source, test, and build-tool project exactly once.</summary>
    [Fact]
    public void SolutionProjects_MatchRepositoryProjects()
    {
        // Arrange
        var repositoryRoot = GetRepositoryRoot();
        var solution = XDocument.Load(Path.Combine(repositoryRoot, "Lineup.slnx"));
        var expected = GetProjects(Path.Combine(repositoryRoot, "src"))
            .Concat(GetProjects(Path.Combine(repositoryRoot, "tests")))
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
                .Select(value => Path.GetFileNameWithoutExtension(value))
                .Where(name => name is "Lineup.Core" or "Lineup.Core.Hosting" or "Lineup.Streaming.FFmpeg" or "Lineup.Web" or "Lineup.Tui")
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
        var webServices = Path.Combine(repositoryRoot, "src", "Apps", "Lineup.Web", "Services");

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
        var packagerProject = Path.Combine(repositoryRoot, "scripts", "Lineup.VirtualOutputWorkerPackager", "Lineup.VirtualOutputWorkerPackager.csproj");
        var expectedImport = @"..\..\..\build\Lineup.Host.VirtualOutputWorkers.targets";
        var hostProjects = new[]
        {
            Path.Combine(repositoryRoot, "src", "Apps", "Lineup.Web", "Lineup.Web.csproj"),
            Path.Combine(repositoryRoot, "src", "Apps", "Lineup.Tui", "Lineup.Tui.csproj")
        };

        // Act
        var imports = hostProjects.Select(project =>
            XDocument.Load(project)
                .Descendants()
                .Single(element => element.Name.LocalName == "Import")
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
                    Command = element.Descendants().Single(child => child.Name.LocalName == "Exec").Attribute("Command")?.Value
                },
                StringComparer.Ordinal);

        // Assert
        Assert.All(imports, import => Assert.Equal(expectedImport, import));
        Assert.Equal("Run", targets["PrepareVirtualOutputWorkersForRun"].BeforeTargets);
        Assert.Equal("Publish", targets["PublishVirtualOutputWorkersWithHost"].AfterTargets);
        var publishCommand = Assert.IsType<string>(targets["PublishVirtualOutputWorkersWithHost"].Command);
        Assert.Contains("$(LineupWorkerPackagerRuntimeArguments)", publishCommand, StringComparison.Ordinal);
        Assert.Contains("$(LineupWorkerPackagerSelfContainedArgument)", publishCommand, StringComparison.Ordinal);
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
        var program = File.ReadAllText(Path.Combine(repositoryRoot, "src", "Apps", "Lineup.Web", "Program.cs"));

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
        if (string.Equals(sourceProjectName, "Lineup.Common", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (string.Equals(sourceProjectName, "Lineup.Extensibility", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(targetProjectName, "Lineup.Common", StringComparison.OrdinalIgnoreCase);
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
            if (string.Equals(sourceProjectName, "Lineup.SiliconDust.HDHomeRun.DiscoveryRuntime", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(targetProjectName, "Lineup.VirtualOutputs.Runtime", StringComparison.OrdinalIgnoreCase))
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
