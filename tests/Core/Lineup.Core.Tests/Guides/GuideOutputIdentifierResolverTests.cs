using System.Text.Json;
using Lineup.Common.Guides;
using Lineup.Extensibility.Guides;
using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;
using Xunit;

namespace Lineup.Core.Tests.Guides;

/// <summary>
/// Verifies custom guide-output API identifier resolution.
/// </summary>
public sealed class GuideOutputIdentifierResolverTests
{
    /// <summary>Verifies custom outputs resolve by both stable ID and code-safe key.</summary>
    [Fact]
    public void Resolve_CustomOutputIdAndKey_ReturnSameOutput()
    {
        // Arrange
        var output = CreateOutput("xmltv_2");

        // Act
        var byId = GuideOutputIdentifierResolver.Resolve([output], output.Id.ToString());
        var byKey = GuideOutputIdentifierResolver.Resolve([output], "XMLTV_2");

        // Assert
        Assert.Same(output, byId);
        Assert.Same(output, byKey);
    }

    /// <summary>Verifies the global output remains exclusive to its dedicated XMLTV endpoint.</summary>
    [Fact]
    public void Resolve_BuiltInOutput_ReturnsNull()
    {
        // Arrange
        var output = CreateOutput("global") with { IsBuiltIn = true };

        // Act
        var byId = GuideOutputIdentifierResolver.Resolve([output], output.Id.ToString());
        var byKey = GuideOutputIdentifierResolver.Resolve([output], output.Key);

        // Assert
        Assert.Null(byId);
        Assert.Null(byKey);
    }

    private static GuideOutputDefinition CreateOutput(string key) => new()
    {
        Id = GuideOutputId.New(),
        Name = "XMLTV 2",
        Key = key,
        Adapter = new AdapterConfiguration("xmltv", JsonSerializer.SerializeToElement(new { }))
    };
}
