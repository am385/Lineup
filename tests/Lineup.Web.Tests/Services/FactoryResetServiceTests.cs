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
        var transientStore = new TransientStreamStore(root.CreateSubdirectory("transient").FullName);
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
        var resetApplied = FactoryResetCoordinator.ApplyPendingReset(configDirectory.FullName, xmltvDirectory.FullName, transientStore);
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
        Assert.False(FactoryResetCoordinator.ApplyPendingReset(configDirectory.FullName, xmltvDirectory.FullName, transientStore));
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
        var transientStore = new TransientStreamStore(root.CreateSubdirectory("transient").FullName);
        var persistedOutputPath = Path.Combine(xmltvDirectory.FullName, "custom.xml");
        await File.WriteAllTextAsync(persistedOutputPath, "guide", TestContext.Current.CancellationToken);
        await FactoryResetCoordinator.WriteRequestAsync(configDirectory.FullName, new FactoryResetRequest { PersistedXmltvOutputPath = persistedOutputPath }, TestContext.Current.CancellationToken);

        // Act
        var resetApplied = FactoryResetCoordinator.ApplyPendingReset(configDirectory.FullName, xmltvDirectory.FullName, transientStore);

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

    /// <summary>
    /// Verifies transient cleanup removes only directories whose process owner is proven inactive.
    /// </summary>
    [Fact]
    public void DeleteInactiveTransientDirectories_MixedOwnerStates_DeletesOnlyInactiveOwner()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-transient-cleanup-");
        var inactive = root.CreateSubdirectory("inactive");
        var active = root.CreateSubdirectory("active");
        var unknown = root.CreateSubdirectory("unknown");

        // Act
        FactoryResetCoordinator.DeleteInactiveTransientDirectories(
            root.FullName,
            name => name switch
            {
                "inactive" => TransientDirectoryOwnerStatus.Inactive,
                "active" => TransientDirectoryOwnerStatus.Active,
                _ => TransientDirectoryOwnerStatus.Unknown
            });

        // Assert
        Assert.False(inactive.Exists);
        Assert.True(active.Exists);
        Assert.True(unknown.Exists);
        root.Delete(recursive: true);
    }

    /// <summary>
    /// Verifies transient cleanup applies the same owner policy to HLS and subtitle process directories.
    /// </summary>
    [Fact]
    public void DeleteInactiveTransientState_InactiveOwners_RemovesHlsAndSubtitleDirectories()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-transient-state-");
        var hlsDirectory = root.CreateSubdirectory(TransientDirectoryOwnership.HlsDirectoryName).CreateSubdirectory("inactive");
        var subtitleDirectory = root.CreateSubdirectory(SubtitleSidecarService.DirectoryName).CreateSubdirectory("inactive");

        // Act
        FactoryResetCoordinator.DeleteInactiveTransientState(root.FullName, _ => TransientDirectoryOwnerStatus.Inactive);

        // Assert
        Assert.False(hlsDirectory.Exists);
        Assert.False(subtitleDirectory.Exists);
        root.Delete(recursive: true);
    }

    /// <summary>
    /// Verifies owner matching distinguishes active processes, exited processes, PID reuse, legacy names, and unknown names.
    /// </summary>
    [Fact]
    public void TransientDirectoryOwnership_ProcessStates_ReturnsSafeStatus()
    {
        // Arrange
        var instanceId = Guid.NewGuid().ToString("N");
        var legacyInstanceId = Guid.NewGuid().ToString("N");

        // Act
        var active = TransientDirectoryOwnership.GetOwnerStatus($"42-100-{instanceId}", _ => 100);
        var reused = TransientDirectoryOwnership.GetOwnerStatus($"42-100-{instanceId}", _ => 200);
        var inactive = TransientDirectoryOwnership.GetOwnerStatus($"42-100-{instanceId}", _ => null);
        var legacyActive = TransientDirectoryOwnership.GetOwnerStatus($"42-{legacyInstanceId}", _ => 200);
        var unknown = TransientDirectoryOwnership.GetOwnerStatus("legacy-session", _ => null);
        var inaccessible = TransientDirectoryOwnership.GetOwnerStatus($"42-100-{instanceId}", _ => throw new InvalidOperationException());

        // Assert
        Assert.Equal(TransientDirectoryOwnerStatus.Active, active);
        Assert.Equal(TransientDirectoryOwnerStatus.Inactive, reused);
        Assert.Equal(TransientDirectoryOwnerStatus.Inactive, inactive);
        Assert.Equal(TransientDirectoryOwnerStatus.Active, legacyActive);
        Assert.Equal(TransientDirectoryOwnerStatus.Unknown, unknown);
        Assert.Equal(TransientDirectoryOwnerStatus.Unknown, inaccessible);
    }
}
