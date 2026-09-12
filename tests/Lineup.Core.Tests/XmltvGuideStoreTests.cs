using System.Text;
using Xunit;

namespace Lineup.Core.Tests;

/// <summary>
/// Tests canonical XMLTV staging and commits.
/// </summary>
public class XmltvGuideStoreTests
{
    /// <summary>
    /// Verifies staging does not replace the canonical guide until the explicit commit.
    /// </summary>
    [Fact]
    public async Task StageAsync_BeforeCommit_PreservesExistingGuide()
    {
        // Arrange
        var testDirectory = Path.Combine(Directory.GetCurrentDirectory(), "test-data", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);
        var guidePath = Path.Combine(testDirectory, "guide.xml");
        await File.WriteAllTextAsync(guidePath, "old", TestContext.Current.CancellationToken);
        var store = new XmltvGuideStore(guidePath);

        // Act
        using var stagedGuide = await store.StageAsync(Encoding.UTF8.GetBytes("new"), TestContext.Current.CancellationToken);
        var beforeCommit = await File.ReadAllTextAsync(guidePath, TestContext.Current.CancellationToken);
        stagedGuide.Commit();
        var afterCommit = await File.ReadAllTextAsync(guidePath, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("old", beforeCommit);
        Assert.Equal("new", afterCommit);

        Directory.Delete(testDirectory, recursive: true);
    }
}
