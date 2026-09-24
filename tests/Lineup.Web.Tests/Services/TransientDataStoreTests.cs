using Lineup.Core;
using Lineup.Web.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>
/// Verifies transient data path configuration.
/// </summary>
public class TransientDataStoreTests
{
    /// <summary>
    /// Verifies the configured transient path controls HLS and subtitle roots.
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
        Assert.Equal(Path.Combine(store.RootPath, TransientDirectoryOwnership.HlsDirectoryName), store.HlsRootPath);
        Assert.Equal(Path.Combine(store.RootPath, SubtitleSidecarService.DirectoryName), store.SubtitleRootPath);
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
