using Lineup.Web.Services;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>
/// Verifies transient subtitle path containment and lifecycle cleanup.
/// </summary>
public class SubtitleSidecarServiceTests
{
    /// <summary>
    /// Verifies default stores remain isolated when multiple hosts run in one process.
    /// </summary>
    [Fact]
    public void DefaultStores_MultipleInstances_UseDistinctOwnedDirectories()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-subtitle-stores-");
        var transientStore = new TransientStreamStore(root.FullName);
        var firstService = new SubtitleSidecarService(transientStore);
        var firstPath = firstService.Create("first");
        File.WriteAllText(firstPath, "first");

        // Act
        var secondService = new SubtitleSidecarService(transientStore);
        var secondPath = secondService.Create("second");
        File.WriteAllText(secondPath, "second");

        // Assert
        Assert.NotEqual(Path.GetDirectoryName(firstPath), Path.GetDirectoryName(secondPath));
        Assert.True(File.Exists(firstPath));
        Assert.True(File.Exists(secondPath));
        Directory.Delete(Assert.IsType<string>(Path.GetDirectoryName(firstPath)), recursive: true);
        Directory.Delete(Assert.IsType<string>(Path.GetDirectoryName(secondPath)), recursive: true);
        root.Delete(recursive: true);
    }

    /// <summary>
    /// Verifies active sidecars support incremental reads and are deleted on removal.
    /// </summary>
    [Fact]
    public void Sidecar_ActiveSession_SupportsSharedReadAndCleanup()
    {
        // Arrange
        var service = CreateService();
        var path = service.Create("abc123");
        File.WriteAllText(path, "WEBVTT\n\n");

        // Act
        using var stream = service.OpenRead("abc123");
        service.Remove("abc123");

        // Assert
        Assert.NotNull(stream);
        Assert.False(File.Exists(path));
        Assert.Null(service.OpenRead("abc123"));
    }

    /// <summary>
    /// Verifies offset reads return only newly appended subtitle bytes.
    /// </summary>
    [Fact]
    public void Sidecar_OffsetRead_ReturnsOnlyAppendedBytes()
    {
        // Arrange
        var service = CreateService();
        var path = service.Create("abc123");
        File.WriteAllText(path, "WEBVTT\n\nfirst\n\n");
        var initial = Assert.IsType<SubtitleSidecarChunk>(service.ReadFrom("abc123", 0));
        File.AppendAllText(path, "second\n\n");

        // Act
        var appended = service.ReadFrom("abc123", initial.NextOffset);

        // Assert
        Assert.NotNull(appended);
        Assert.Equal("second\n\n", System.Text.Encoding.UTF8.GetString(appended.Data));
        Assert.Equal(initial.NextOffset + appended.Data.Length, appended.NextOffset);
    }

    /// <summary>
    /// Verifies traversal and separator-bearing session identifiers are rejected.
    /// </summary>
    [Theory]
    [InlineData("../escape")]
    [InlineData(@"..\escape")]
    [InlineData("nested/session")]
    [InlineData("session.vtt")]
    public void Sidecar_InvalidSessionIdentifier_IsRejected(string sessionId)
    {
        // Arrange
        var service = CreateService();

        // Act
        var exception = Record.Exception(() => service.Create(sessionId));

        // Assert
        Assert.IsType<ArgumentException>(exception);
    }

    private static SubtitleSidecarService CreateService() =>
        new(Path.Combine(Environment.CurrentDirectory, $".subtitle-test-{Guid.NewGuid():N}"));
}
