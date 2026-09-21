using Lineup.Core;
using Lineup.Web.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>
/// Verifies restart-safe factory-reset behavior.
/// </summary>
public class FactoryResetServiceTests
{
    /// <summary>
    /// Verifies that reset removes Lineup-owned state while preserving an external XMLTV destination.
    /// </summary>
    [Fact]
    public async Task ApplyPendingReset_OwnedStateAndExternalOutput_RemovesOnlyOwnedState()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-factory-reset-");
        var configDirectory = root.CreateSubdirectory("config");
        var xmltvDirectory = root.CreateSubdirectory("xmltv");
        var externalDirectory = root.CreateSubdirectory("external");
        var settingsPath = Path.Combine(configDirectory.FullName, AppConstants.SettingsFileName);
        var databasePath = Path.Combine(configDirectory.FullName, AppConstants.DefaultDatabaseFileName);
        var cachePath = Path.ChangeExtension(databasePath, ".xmltv");
        var channelLineupPath = Path.ChangeExtension(databasePath, ".channels.json");
        var configuredOutputPath = Path.Combine(xmltvDirectory.FullName, AppConstants.DefaultXmltvFileName);
        var externalOutputPath = Path.Combine(externalDirectory.FullName, "keep.xml");
        var logDirectory = configDirectory.CreateSubdirectory(AppConstants.LogDirectoryName);
        var dataProtectionDirectory = configDirectory.CreateSubdirectory(AppConstants.DataProtectionKeysDirectoryName);
        await File.WriteAllTextAsync(Path.Combine(logDirectory.FullName, "lineup-test.log"), "log", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(dataProtectionDirectory.FullName, "key-test.xml"), "key", TestContext.Current.CancellationToken);
        var ownedFiles = new[]
        {
            settingsPath,
            $"{settingsPath}.bak",
            $"{settingsPath}.pending.tmp",
            databasePath,
            $"{databasePath}-wal",
            $"{databasePath}-shm",
            cachePath,
            $"{cachePath}.generation",
            channelLineupPath,
            configuredOutputPath,
            $"{configuredOutputPath}.generation"
        };
        foreach (var path in ownedFiles)
        {
            await File.WriteAllTextAsync(path, "test", TestContext.Current.CancellationToken);
        }
        await File.WriteAllTextAsync(externalOutputPath, "external", TestContext.Current.CancellationToken);
        await FactoryResetCoordinator.WriteRequestAsync(configDirectory.FullName, new FactoryResetRequest { PersistedXmltvOutputPath = externalOutputPath }, TestContext.Current.CancellationToken);

        // Act
        var resetApplied = FactoryResetCoordinator.ApplyPendingReset(configDirectory.FullName, xmltvDirectory.FullName);
        var resetSettings = new AppSettingsService(NullLogger<AppSettingsService>.Instance, settingsPath, xmltvDirectory.FullName);

        // Assert
        Assert.True(resetApplied);
        Assert.All(ownedFiles, path => Assert.False(File.Exists(path), path));
        Assert.True(File.Exists(externalOutputPath));
        Assert.False(logDirectory.Exists);
        Assert.False(dataProtectionDirectory.Exists);
        Assert.False(resetSettings.Settings.IsSetupComplete);
        Assert.Equal(configuredOutputPath, resetSettings.Settings.XmltvOutputPath);
        Assert.False(File.Exists(Path.Combine(configDirectory.FullName, FactoryResetCoordinator.RequestFileName)));
        Assert.False(FactoryResetCoordinator.ApplyPendingReset(configDirectory.FullName, xmltvDirectory.FullName));
        root.Delete(recursive: true);
    }

    /// <summary>
    /// Verifies that reset removes a custom XMLTV output located in the configured Lineup-owned directory.
    /// </summary>
    [Fact]
    public async Task ApplyPendingReset_CustomOutputInConfiguredDirectory_RemovesOutput()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-factory-reset-");
        var configDirectory = root.CreateSubdirectory("config");
        var xmltvDirectory = root.CreateSubdirectory("xmltv");
        var persistedOutputPath = Path.Combine(xmltvDirectory.FullName, "custom.xml");
        await File.WriteAllTextAsync(persistedOutputPath, "guide", TestContext.Current.CancellationToken);
        await FactoryResetCoordinator.WriteRequestAsync(configDirectory.FullName, new FactoryResetRequest { PersistedXmltvOutputPath = persistedOutputPath }, TestContext.Current.CancellationToken);

        // Act
        var resetApplied = FactoryResetCoordinator.ApplyPendingReset(configDirectory.FullName, xmltvDirectory.FullName);

        // Assert
        Assert.True(resetApplied);
        Assert.False(File.Exists(persistedOutputPath));
        root.Delete(recursive: true);
    }

    /// <summary>
    /// Verifies that a reset request is durable before graceful shutdown starts.
    /// </summary>
    [Fact]
    public async Task RequestResetAsync_CurrentSettings_WritesMarkerThenStopsApplication()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-factory-reset-");
        var settingsService = Substitute.For<IAppSettingsService>();
        settingsService.Settings.Returns(new AppSettings { XmltvOutputPath = Path.Combine(root.FullName, "guide.xml") });
        var applicationLifetime = Substitute.For<IHostApplicationLifetime>();
        var service = new FactoryResetService(root.FullName, settingsService, applicationLifetime, NullLogger<FactoryResetService>.Instance);

        // Act
        await service.RequestResetAsync(TestContext.Current.CancellationToken);

        // Assert
        var requestPath = Path.Combine(root.FullName, FactoryResetCoordinator.RequestFileName);
        Assert.True(File.Exists(requestPath));
        Assert.Contains("guide.xml", await File.ReadAllTextAsync(requestPath, TestContext.Current.CancellationToken));
        applicationLifetime.Received(1).StopApplication();
        root.Delete(recursive: true);
    }
}
