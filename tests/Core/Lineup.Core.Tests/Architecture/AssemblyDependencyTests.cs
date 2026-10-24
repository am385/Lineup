using Lineup.GuideOutputs.Json;
using Lineup.GuideOutputs.Json.Plugin;
using Lineup.GuideOutputs.Xmltv;
using Lineup.GuideOutputs.Xmltv.Plugin;
using Xunit;

namespace Lineup.Core.Tests.Architecture;

/// <summary>Verifies the supported dependency direction between shared models, formats, and plugins.</summary>
public sealed class AssemblyDependencyTests
{
    /// <summary>Verifies common and format assemblies remain independent from implementation and plugin layers.</summary>
    [Fact]
    public void ConsumerAssemblies_References_MatchSupportedBoundary()
    {
        // Arrange
        var common = GetLineupReferences(typeof(StreamingConfiguration).Assembly);
        var json = GetLineupReferences(typeof(JsonGuideDocument).Assembly);
        var xmltv = GetLineupReferences(typeof(XmltvGuideSerialization).Assembly);

        // Act
        var actual = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["common"] = common,
            ["json"] = json,
            ["xmltv"] = xmltv
        };

        // Assert
        Assert.Empty(actual["common"]);
        Assert.Equal(["Lineup.Common"], actual["json"]);
        Assert.Equal(["Lineup.Common"], actual["xmltv"]);
    }

    /// <summary>Verifies Guide Output plugins depend on their public format library and extension boundary.</summary>
    [Fact]
    public void PluginAssemblies_References_IncludeFormatAndExtensibility()
    {
        // Arrange
        var json = GetLineupReferences(typeof(JsonGuideOutputDriver).Assembly);
        var xmltv = GetLineupReferences(typeof(XmltvGuideOutputDriver).Assembly);

        // Act
        var actual = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["json"] = json,
            ["xmltv"] = xmltv
        };

        // Assert
        Assert.Equal(["Lineup.Common", "Lineup.Extensibility", "Lineup.GuideOutputs.Json"], actual["json"]);
        Assert.Equal(["Lineup.Common", "Lineup.Extensibility", "Lineup.GuideOutputs.Xmltv"], actual["xmltv"]);
    }

    private static IReadOnlyList<string> GetLineupReferences(System.Reflection.Assembly assembly) =>
        assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .OfType<string>()
            .Where(name => name.StartsWith("Lineup.", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
}
