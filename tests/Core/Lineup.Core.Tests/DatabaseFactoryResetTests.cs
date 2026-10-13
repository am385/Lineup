using Xunit;

namespace Lineup.Core.Tests;

/// <summary>
/// Verifies explicit SQLite factory-reset cleanup.
/// </summary>
public sealed class DatabaseFactoryResetTests
{
    /// <summary>
    /// Verifies the database and SQLite sidecars are deleted without touching neighboring files.
    /// </summary>
    [Fact]
    public async Task Reset_DatabaseAndSidecars_DeletesOnlyDatabaseArtifacts()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-database-reset-");
        var databasePath = Path.Combine(root.FullName, "lineup.db");
        var artifacts = new[] { databasePath, $"{databasePath}-shm", $"{databasePath}-wal", $"{databasePath}-journal" };
        var preservedPath = Path.Combine(root.FullName, "preserved.txt");
        foreach (var artifact in artifacts)
        {
            await File.WriteAllTextAsync(artifact, "data", TestContext.Current.CancellationToken);
        }
        await File.WriteAllTextAsync(preservedPath, "keep", TestContext.Current.CancellationToken);

        // Act
        DatabaseFactoryReset.Reset(databasePath);

        // Assert
        Assert.All(artifacts, artifact => Assert.False(File.Exists(artifact), artifact));
        Assert.True(File.Exists(preservedPath));
        root.Delete(recursive: true);
    }

    /// <summary>
    /// Verifies a missing database can be reset idempotently.
    /// </summary>
    [Fact]
    public void Reset_MissingDatabase_CompletesWithoutCreatingState()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-database-reset-");
        var databasePath = Path.Combine(root.FullName, "missing.db");

        // Act
        DatabaseFactoryReset.Reset(databasePath);

        // Assert
        Assert.Empty(Directory.EnumerateFileSystemEntries(root.FullName));
        root.Delete(recursive: true);
    }
}
