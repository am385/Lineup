using Xunit;

namespace Lineup.VirtualOutputs.Runtime.Tests;

/// <summary>Verifies trusted-host certificate material resolution.</summary>
public sealed class VirtualOutputCertificateResolverTests
{
    /// <summary>Verifies certificate bytes are transferred without rewriting the opaque policy reference.</summary>
    [Fact]
    public async Task ResolveAsync_ExistingCertificate_ReturnsActivationMaterial()
    {
        // Arrange
        var directory = Directory.CreateTempSubdirectory("lineup-output-certificate-");
        var path = Path.Combine(directory.FullName, "listener.pfx");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4], TestContext.Current.CancellationToken);
        var resolver = new FileVirtualOutputCertificateResolver();

        // Act
        var material = await resolver.ResolveAsync(path, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(path, material.Reference);
        Assert.Equal([1, 2, 3, 4], material.Pkcs12);
        Assert.Null(material.Password);
        directory.Delete(recursive: true);
    }
}
