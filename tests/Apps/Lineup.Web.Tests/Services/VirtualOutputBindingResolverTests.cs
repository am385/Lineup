using Lineup.Common.Streaming;
using Lineup.VirtualOutputs.Hosting;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>Verifies isolated listener binding resolution.</summary>
public sealed class VirtualOutputBindingResolverTests
{
    /// <summary>Verifies public URLs are selected from the listener that accepted the request.</summary>
    [Fact]
    public void ResolvePublicBaseUri_SecondaryListener_ReturnsMatchingPublicUri()
    {
        // Arrange
        var hosting = CreateHosting(
            new VirtualOutputBindingDefinition
            {
                Port = 8080,
                PublicBaseUri = "https://primary.example/"
            },
            new VirtualOutputBindingDefinition
            {
                Port = 9090,
                PublicBaseUri = "https://secondary.example/base"
            });
        var bindingsByPort = VirtualOutputBindingResolver.CreatePortMap(hosting);

        // Act
        var result = VirtualOutputBindingResolver.ResolvePublicBaseUri(bindingsByPort, 9090);

        // Assert
        Assert.Equal(new Uri("https://secondary.example/base/"), result);
    }

    /// <summary>Verifies ambiguous duplicate local ports fail before a worker starts accepting requests.</summary>
    [Fact]
    public void CreatePortMap_DuplicatePort_Throws()
    {
        // Arrange
        var hosting = CreateHosting(
            new VirtualOutputBindingDefinition
            {
                Addresses = ["192.0.2.10"],
                Port = 8080,
                PublicBaseUri = "https://primary.example/"
            },
            new VirtualOutputBindingDefinition
            {
                Addresses = ["192.0.2.20"],
                Port = 8080,
                PublicBaseUri = "https://secondary.example/"
            });

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() => VirtualOutputBindingResolver.CreatePortMap(hosting));

        // Assert
        Assert.Contains("port 8080 is configured more than once", exception.Message, StringComparison.Ordinal);
    }

    private static VirtualOutputHostingDefinition CreateHosting(params VirtualOutputBindingDefinition[] bindings) =>
        new()
        {
            WorkerPolicyKey = "default",
            Bindings = bindings
        };
}
