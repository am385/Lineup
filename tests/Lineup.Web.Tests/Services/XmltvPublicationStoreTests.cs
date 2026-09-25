using Lineup.Core.Storage;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>
/// Verifies external XMLTV publication operations.
/// </summary>
public class XmltvPublicationStoreTests
{
    /// <summary>
    /// Verifies publication replaces existing output without leaving temporary files.
    /// </summary>
    [Fact]
    public async Task PublishAsync_ExistingPublication_ReplacesContentAtomically()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-xmltv-publication-");
        var path = Path.Combine(root.FullName, "guide.xml");
        await File.WriteAllTextAsync(path, "old", TestContext.Current.CancellationToken);
        var store = new XmltvPublicationStore();

        // Act
        await store.PublishAsync(
            path,
            async (stream, cancellationToken) =>
            {
                await using var writer = new StreamWriter(stream, leaveOpen: true);
                await writer.WriteAsync("new".AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
            },
            TestContext.Current.CancellationToken);

        // Assert
        Assert.True(store.Exists(path));
        using var published = store.OpenRead(path);
        Assert.NotNull(published);
        using var reader = new StreamReader(published);
        Assert.Equal("new", await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFiles(root.FullName, "*.tmp", SearchOption.TopDirectoryOnly));
        root.Delete(recursive: true);
    }

    /// <summary>
    /// Verifies deletion removes the publication and abandoned temporary files.
    /// </summary>
    [Fact]
    public async Task Delete_PublicationArtifacts_RemovesOwnedFiles()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-xmltv-publication-");
        var path = Path.Combine(root.FullName, "guide.xml");
        await File.WriteAllTextAsync(path, "guide", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync($"{path}.abandoned.tmp", "temporary", TestContext.Current.CancellationToken);
        var unrelatedPath = Path.Combine(root.FullName, "unrelated.tmp");
        await File.WriteAllTextAsync(unrelatedPath, "keep", TestContext.Current.CancellationToken);
        var store = new XmltvPublicationStore();

        // Act
        store.Delete(path);

        // Assert
        Assert.False(File.Exists(path));
        Assert.False(File.Exists($"{path}.abandoned.tmp"));
        Assert.True(File.Exists(unrelatedPath));
        root.Delete(recursive: true);
    }

    /// <summary>
    /// Verifies deletion is idempotent when the publication directory no longer exists.
    /// </summary>
    [Fact]
    public void Delete_MissingPublicationDirectory_DoesNotThrow()
    {
        // Arrange
        var path = Path.Combine(Path.GetTempPath(), $"lineup-missing-{Guid.NewGuid():N}", "guide.xml");
        var store = new XmltvPublicationStore();

        // Act
        var exception = Record.Exception(() => store.Delete(path));

        // Assert
        Assert.Null(exception);
    }
}
