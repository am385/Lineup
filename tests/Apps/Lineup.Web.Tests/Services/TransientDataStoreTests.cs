using Microsoft.Extensions.Configuration;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>
/// Verifies transient data path configuration.
/// </summary>
public class TransientDataStoreTests
{
    /// <summary>Verifies an opened file remains readable after transient session cleanup removes its path.</summary>
    [Fact]
    public async Task OpenRead_FileDeletedAfterOpen_PreservesReader()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-transient-read-");
        var store = new TransientDataStore(root.FullName);
        var directory = store.CreateSessionDirectory("media", "owned");
        var path = store.GetFilePath(directory, "chunk.m4s");
        byte[] content = [0, 1, 2, 255];
        await File.WriteAllBytesAsync(path, content, TestContext.Current.CancellationToken);
        using var stream = store.OpenRead(path);

        // Act
        store.DeleteDirectory(directory);

        // Assert
        Assert.NotNull(stream);
        Assert.False(File.Exists(path));
        var actual = new byte[content.Length];
        await stream.ReadExactlyAsync(actual, TestContext.Current.CancellationToken);
        Assert.Equal(content, actual);
        root.Delete(recursive: true);
    }

    /// <summary>Verifies missing files and removed session directories are absent rather than storage failures.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OpenRead_ArtifactAbsentAtOpen_ReturnsNull(bool removeDirectory)
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-transient-missing-");
        var store = new TransientDataStore(root.FullName);
        var directory = store.CreateSessionDirectory("media", "owned");
        var path = store.GetFilePath(directory, "missing.m4s");
        if (removeDirectory)
        {
            store.DeleteDirectory(directory);
        }

        // Act
        using var stream = store.OpenRead(path);

        // Assert
        Assert.Null(stream);
        root.Delete(recursive: true);
    }

    /// <summary>Verifies opening a directory is not silently treated as a missing file.</summary>
    [Fact]
    public void OpenRead_DirectoryInsteadOfFile_PropagatesAccessFailure()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-transient-directory-");
        var store = new TransientDataStore(root.FullName);

        // Act
        var exception = Record.Exception(() => store.OpenRead(root.FullName));

        // Assert
        Assert.IsType<UnauthorizedAccessException>(exception);
        root.Delete(recursive: true);
    }

    /// <summary>
    /// Verifies the configured transient path controls named workspace roots.
    /// </summary>
    [Fact]
    public void Create_TransientPathConfigured_UsesConfiguredRoot()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-transient-store-");
        var configuredPath = Path.Combine(root.FullName, "configured");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [AppConstants.TransientPathConfigKey] = configuredPath
        }).Build();

        // Act
        var store = TransientDataStore.Create(configuration);

        // Assert
        Assert.Equal(Path.GetFullPath(configuredPath), store.RootPath);
        Assert.Equal(Path.Combine(store.RootPath, "test-workspace"), store.GetWorkspaceRoot("test-workspace"));
        Assert.True(Directory.Exists(configuredPath));
        root.Delete(recursive: true);
    }

    /// <summary>
    /// Verifies missing or empty configuration uses the default transient path.
    /// </summary>
    /// <param name="configuredPath">Missing or empty configured value.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void ResolveRootPath_TransientPathMissingOrEmpty_UsesDefault(string? configuredPath)
    {
        // Arrange
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [AppConstants.TransientPathConfigKey] = configuredPath
        }).Build();

        // Act
        var rootPath = TransientDataStore.ResolveRootPath(configuration);

        // Assert
        Assert.Equal(AppConstants.DefaultTransientPath, rootPath);
    }

    /// <summary>
    /// Verifies an existing custom app-data configuration provides a writable transient fallback during upgrade.
    /// </summary>
    [Fact]
    public void ResolveRootPath_OnlyAppDataPathConfigured_UsesSystemTemporaryDirectory()
    {
        // Arrange
        var appDataPath = Path.Combine(Path.GetTempPath(), "lineup-existing-appdata");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [AppConstants.AppDataPathConfigKey] = appDataPath
        }).Build();

        // Act
        var rootPath = TransientDataStore.ResolveRootPath(configuration);

        // Assert
        Assert.Equal(Path.Combine(Path.GetTempPath(), "lineup"), rootPath);
    }
}
