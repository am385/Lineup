using Lineup.Core;
using Lineup.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>
/// Verifies persistent application-data path resolution and safe file operations.
/// </summary>
public class AppDataStoreTests
{
    /// <summary>
    /// Verifies the app-data key selects the persistent root.
    /// </summary>
    [Fact]
    public void Create_AppDataPathConfigured_UsesConfiguredPath()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-app-data-");
        var configuredPath = Path.Combine(root.FullName, "configured");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [AppConstants.AppDataPathConfigKey] = configuredPath
        }).Build();

        // Act
        var store = AppDataStore.Create(configuration);

        // Assert
        Assert.Equal(Path.GetFullPath(configuredPath), store.RootPath);
        Assert.True(Directory.Exists(configuredPath));
        root.Delete(recursive: true);
    }

    /// <summary>
    /// Verifies missing or empty configuration uses the persistent application-data default.
    /// </summary>
    /// <param name="configuredPath">Missing or empty configured value.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void ResolveRootPath_AppDataPathMissingOrEmpty_UsesDefault(string? configuredPath)
    {
        // Arrange
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [AppConstants.AppDataPathConfigKey] = configuredPath
        }).Build();

        // Act
        var rootPath = AppDataStore.ResolveRootPath(configuration);

        // Assert
        Assert.Equal(AppConstants.DefaultAppDataPath, rootPath);
    }

    /// <summary>
    /// Verifies every persistent path is derived from one normalized root.
    /// </summary>
    [Fact]
    public void Constructor_AppDataRoot_DerivesKnownPersistentPaths()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-app-data-");

        // Act
        var store = new AppDataStore(root.FullName);

        // Assert
        Assert.Equal(Path.Combine(root.FullName, AppConstants.SettingsFileName), store.SettingsPath);
        Assert.Equal($"{store.SettingsPath}.bak", store.SettingsBackupPath);
        Assert.Equal(Path.Combine(root.FullName, AppConstants.DefaultDatabaseFileName), store.DatabasePath);
        Assert.Equal(Path.ChangeExtension(store.DatabasePath, ".xmltv"), store.GuideCachePath);
        Assert.Equal(Path.ChangeExtension(store.DatabasePath, ".channels.json"), store.ChannelLineupPath);
        Assert.Equal(Path.Combine(root.FullName, AppConstants.LogDirectoryName), store.LogDirectoryPath);
        Assert.Equal(Path.Combine(root.FullName, AppConstants.DataProtectionKeysDirectoryName), store.DataProtectionKeysPath);
        Assert.Equal(Path.Combine(root.FullName, FactoryResetCoordinator.RequestFileName), store.FactoryResetRequestPath);
        root.Delete(recursive: true);
    }

    /// <summary>
    /// Verifies app-data operations cannot escape or delete the persistent root.
    /// </summary>
    [Fact]
    public void Containment_OutsideOrRootPath_RejectsDestructiveOperations()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-app-data-");
        var store = new AppDataStore(root.FullName);
        var outside = Path.Combine(root.Parent!.FullName, "outside.txt");

        // Act
        var pathError = Record.Exception(() => store.DeleteFile(outside));
        var rootError = Record.Exception(() => store.DeleteDirectory(root.FullName, recursive: true));

        // Assert
        Assert.IsType<InvalidOperationException>(pathError);
        Assert.IsType<InvalidOperationException>(rootError);
        Assert.True(root.Exists);
        root.Delete(recursive: true);
    }

    /// <summary>
    /// Verifies atomic replacement preserves the previous complete file as a backup.
    /// </summary>
    [Fact]
    public async Task WriteAtomicallyAsync_ExistingFile_ReplacesAndBacksUpContent()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-app-data-");
        var store = new AppDataStore(root.FullName);
        await File.WriteAllTextAsync(store.SettingsPath, "old", TestContext.Current.CancellationToken);

        // Act
        await store.WriteAtomicallyAsync(
            store.SettingsPath,
            async (stream, cancellationToken) =>
            {
                await using var writer = new StreamWriter(stream, leaveOpen: true);
                await writer.WriteAsync("new".AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
            },
            store.SettingsBackupPath,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("new", await File.ReadAllTextAsync(store.SettingsPath, TestContext.Current.CancellationToken));
        Assert.Equal("old", await File.ReadAllTextAsync(store.SettingsBackupPath, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFiles(root.FullName, "*.tmp", SearchOption.TopDirectoryOnly));
        root.Delete(recursive: true);
    }

    /// <summary>
    /// Verifies store-based service registrations construct without primitive-path factories.
    /// </summary>
    [Fact]
    public void DependencyInjection_NormalRegistrations_PassScopeValidation()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-app-data-");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new AppDataStore(root.FullName));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton(Substitute.For<IHostApplicationLifetime>());
        services.AddSingleton<IAppSettingsService, AppSettingsService>();
        services.AddSingleton<IFactoryResetService, FactoryResetService>();

        // Act
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        // Assert
        Assert.IsType<AppSettingsService>(provider.GetRequiredService<IAppSettingsService>());
        Assert.IsType<FactoryResetService>(provider.GetRequiredService<IFactoryResetService>());
        root.Delete(recursive: true);
    }
}
