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

    /// <summary>Verifies the solution contains every source and test project exactly once.</summary>
    [Fact]
    public void SolutionProjects_MatchRepositoryProjects()
    {
        // Arrange
        var repositoryRoot = GetRepositoryRoot();
        var solution = XDocument.Load(Path.Combine(repositoryRoot, "Lineup.slnx"));
        var expected = GetProjects(Path.Combine(repositoryRoot, "src"))
            .Concat(GetProjects(Path.Combine(repositoryRoot, "tests")))
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
