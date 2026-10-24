using System.Xml.Linq;
using Xunit;

namespace Lineup.Core.Tests.Architecture;

/// <summary>
/// Verifies that the Core project remains independent of concrete tuner and guide providers.
/// </summary>
public sealed class CoreProjectBoundaryTests
{
    private static readonly string[] ProviderNames = ["HDHomeRun", "SiliconDust"];
    private static readonly string[] WebForbiddenProviderTypes =
    [
        "HdHomeRunAdvertisedDevice",
        "HdHomeRunDiscoveryProtocol",
        "HdHomeRunProxyDiscoverResponse",
        "HdHomeRunProxyLineupStatus",
        "HdHomeRunProxyProfileSnapshot",
        "SsdpDiscoveryProtocol"
    ];

    /// <summary>
    /// Verifies Core does not reference provider-specific projects or packages.
    /// </summary>
    [Fact]
    public void ProjectDependencies_DoNotReferenceProviderImplementations()
    {
        // Arrange
        var projectPath = Path.Combine(GetRepositoryRoot(), "src", "Core", "Lineup.Core", "Lineup.Core.csproj");
        var project = XDocument.Load(projectPath);

        // Act
        var dependencies = project
            .Descendants()
            .Where(element => element.Name.LocalName is "ProjectReference" or "PackageReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(value => value is not null)
            .Cast<string>()
            .ToArray();

        // Assert
        Assert.DoesNotContain(dependencies, dependency => ProviderNames.Any(
            provider => dependency.Contains(provider, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Verifies Core source does not contain provider-specific implementation symbols.
    /// </summary>
    [Fact]
    public void SourceFiles_DoNotContainProviderImplementationNames()
    {
        // Arrange
        var sourceDirectory = Path.Combine(GetRepositoryRoot(), "src", "Core", "Lineup.Core");
        var sourceFiles = Directory.GetFiles(sourceDirectory, "*.cs", SearchOption.AllDirectories);

        // Act
        var violations = sourceFiles
            .SelectMany(path => ProviderNames
                .Where(provider => File.ReadAllText(path).Contains(provider, StringComparison.OrdinalIgnoreCase))
                .Select(provider => $"{Path.GetRelativePath(sourceDirectory, path)}: {provider}"))
            .ToArray();

        // Assert
        Assert.Empty(violations);
    }

    /// <summary>
    /// Verifies the Web presentation project does not redefine virtual HDHomeRun protocol and runtime types.
    /// </summary>
    [Fact]
    public void WebSource_DoesNotDeclareVirtualHdHomeRunProviderTypes()
    {
        // Arrange
        var sourceDirectory = Path.Combine(GetRepositoryRoot(), "src", "Apps", "Lineup.Web");
        var sourceFiles = Directory.GetFiles(sourceDirectory, "*.cs", SearchOption.AllDirectories);

        // Act
        var violations = sourceFiles
            .SelectMany(path => WebForbiddenProviderTypes
                .Where(type => File.ReadAllText(path).Contains($"class {type}", StringComparison.Ordinal)
                    || File.ReadAllText(path).Contains($"record {type}", StringComparison.Ordinal)
                    || File.ReadAllText(path).Contains($"interface {type}", StringComparison.Ordinal))
                .Select(type => $"{Path.GetRelativePath(sourceDirectory, path)}: {type}"))
            .ToArray();

        // Assert
        Assert.Empty(violations);
    }

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
