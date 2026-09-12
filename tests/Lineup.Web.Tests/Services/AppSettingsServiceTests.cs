using System.Text.Json;
using Lineup.Core;
using Lineup.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>
/// Verifies configuration-aware application settings initialization.
/// </summary>
public class AppSettingsServiceTests
{
    /// <summary>
    /// Verifies that settings written before the global proxy switch inherit the primary profile's disabled state.
    /// </summary>
    [Fact]
    public void Constructor_LegacyDisabledPrimaryProfile_MigratesGlobalProxyState()
    {
        // Arrange
        var testDirectory = CreateTestDirectory();
        var settingsPath = Path.Combine(testDirectory, AppConstants.SettingsFileName);
        File.WriteAllText(settingsPath, """
            {
              "HdHomeRunProxyProfiles": [
                {
                  "Enabled": false,
                  "PhysicalAddress": "tuner.local"
                }
              ]
            }
            """);

        // Act
        var service = new AppSettingsService(NullLogger<AppSettingsService>.Instance, settingsPath);

        // Assert
        Assert.False(service.Settings.EnableHdHomeRunProxy);
        Assert.True(service.Settings.HdHomeRunProxyProfiles[0].Enabled);
        Directory.Delete(testDirectory, recursive: true);
    }

    /// <summary>
    /// Verifies that an explicitly disabled global proxy setting survives persistence and reload.
    /// </summary>
    [Fact]
    public async Task UpdateAsync_DisabledGlobalProxy_PersistsAcrossReload()
    {
        // Arrange
        var testDirectory = CreateTestDirectory();
        var settingsPath = Path.Combine(testDirectory, AppConstants.SettingsFileName);
        var service = new AppSettingsService(NullLogger<AppSettingsService>.Instance, settingsPath);

        // Act
        await service.UpdateAsync(settings => settings.EnableHdHomeRunProxy = false);
        var reloaded = new AppSettingsService(NullLogger<AppSettingsService>.Instance, settingsPath);

        // Assert
        Assert.False(reloaded.Settings.EnableHdHomeRunProxy);
        Directory.Delete(testDirectory, recursive: true);
    }

    /// <summary>
    /// Verifies that a configured directory contains the canonical XMLTV output file.
    /// </summary>
    [Fact]
    public async Task Constructor_ConfiguredXmltvDirectory_UsesCanonicalFileName()
    {
        // Arrange
        var testDirectory = CreateTestDirectory();
        var xmltvDirectory = Path.Combine(testDirectory, "xmltv");
        Directory.CreateDirectory(xmltvDirectory);
        var settingsPath = Path.Combine(testDirectory, AppConstants.SettingsFileName);

        // Act
        var service = new AppSettingsService(NullLogger<AppSettingsService>.Instance, settingsPath, xmltvDirectory);
        await service.SaveAsync();
        var json = await File.ReadAllTextAsync(settingsPath, TestContext.Current.CancellationToken);
        var persistedSettings = JsonSerializer.Deserialize<AppSettings>(json)!;

        // Assert
        Assert.Equal(Path.GetFullPath(Path.Combine(xmltvDirectory, AppConstants.DefaultXmltvFileName)), service.Settings.XmltvOutputPath);
        Assert.Equal(service.Settings.XmltvOutputPath, persistedSettings.XmltvOutputPath);
        Assert.Equal(service.Settings.XmltvOutputPath, service.ConfiguredXmltvOutputPath);

        Directory.Delete(testDirectory, recursive: true);
    }

    /// <summary>
    /// Verifies that an explicitly configured XMLTV file path is preserved.
    /// </summary>
    [Fact]
    public void Constructor_ConfiguredXmltvFile_PreservesFilePath()
    {
        // Arrange
        var testDirectory = CreateTestDirectory();
        var configuredFile = Path.Combine(testDirectory, "custom-guide.xml");

        // Act
        var service = new AppSettingsService(NullLogger<AppSettingsService>.Instance, Path.Combine(testDirectory, AppConstants.SettingsFileName), configuredFile);

        // Assert
        Assert.Equal(Path.GetFullPath(configuredFile), service.Settings.XmltvOutputPath);
        Assert.Equal(service.Settings.XmltvOutputPath, service.ConfiguredXmltvOutputPath);

        Directory.Delete(testDirectory, recursive: true);
    }

    /// <summary>
    /// Verifies that omitting the XMLTV override uses the root XMLTV directory.
    /// </summary>
    [Fact]
    public void Constructor_XmltvPathNotConfigured_UsesRootXmltvDirectory()
    {
        // Arrange
        var testDirectory = CreateTestDirectory();
        var settingsPath = Path.Combine(testDirectory, AppConstants.SettingsFileName);
        var expectedPath = Path.GetFullPath(Path.Combine(AppConstants.DefaultXmltvFilePath, AppConstants.DefaultXmltvFileName));

        // Act
        var service = new AppSettingsService(NullLogger<AppSettingsService>.Instance, settingsPath);

        // Assert
        Assert.Equal(expectedPath, service.ConfiguredXmltvOutputPath);
        Assert.Equal(expectedPath, service.Settings.XmltvOutputPath);
        Directory.Delete(testDirectory, recursive: true);
    }

    /// <summary>
    /// Verifies that an existing persisted XMLTV path remains authoritative when startup configuration differs.
    /// </summary>
    [Fact]
    public void Constructor_PersistedXmltvPathDiffersFromConfigured_PreservesPersistedPath()
    {
        // Arrange
        var testDirectory = CreateTestDirectory();
        var xmltvDirectory = Path.Combine(testDirectory, "xmltv");
        var persistedFile = Path.Combine(testDirectory, "outside", "persisted.xml");
        var settingsPath = Path.Combine(testDirectory, AppConstants.SettingsFileName);
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(new AppSettings { XmltvOutputPath = persistedFile }));

        // Act
        var service = new AppSettingsService(NullLogger<AppSettingsService>.Instance, settingsPath, xmltvDirectory);

        // Assert
        Assert.Equal(persistedFile, service.Settings.XmltvOutputPath);
        Assert.Equal(Path.Combine(xmltvDirectory, AppConstants.DefaultXmltvFileName), service.ConfiguredXmltvOutputPath);

        Directory.Delete(testDirectory, recursive: true);
    }

    /// <summary>
    /// Verifies that a valid relative XMLTV path persists across save and reload.
    /// </summary>
    [Fact]
    public async Task UpdateAsync_RelativeXmltvPath_PersistsAcrossReload()
    {
        // Arrange
        var testDirectory = CreateTestDirectory();
        var xmltvDirectory = Path.Combine(testDirectory, "xmltv");
        var settingsPath = Path.Combine(testDirectory, AppConstants.SettingsFileName);
        var service = new AppSettingsService(NullLogger<AppSettingsService>.Instance, settingsPath, xmltvDirectory);

        // Act
        await service.UpdateAsync(settings => settings.XmltvOutputPath = Path.Combine("..", "outside.xml"));
        var json = await File.ReadAllTextAsync(settingsPath, TestContext.Current.CancellationToken);
        var persistedSettings = JsonSerializer.Deserialize<AppSettings>(json)!;
        var reloaded = new AppSettingsService(NullLogger<AppSettingsService>.Instance, settingsPath, xmltvDirectory);

        // Assert
        Assert.Equal(Path.Combine("..", "outside.xml"), service.Settings.XmltvOutputPath);
        Assert.Equal(service.Settings.XmltvOutputPath, persistedSettings.XmltvOutputPath);
        Assert.Equal(service.Settings.XmltvOutputPath, reloaded.Settings.XmltvOutputPath);

        Directory.Delete(testDirectory, recursive: true);
    }

    /// <summary>
    /// Verifies that an empty legacy XMLTV value is initialized from startup configuration.
    /// </summary>
    [Fact]
    public void Constructor_NullLegacyXmltvPath_UsesConfiguredDefault()
    {
        // Arrange
        var testDirectory = CreateTestDirectory();
        var xmltvDirectory = Path.Combine(testDirectory, "xmltv");
        var settingsPath = Path.Combine(testDirectory, AppConstants.SettingsFileName);
        File.WriteAllText(settingsPath, """{"XmltvOutputPath":null}""");

        // Act
        var service = new AppSettingsService(NullLogger<AppSettingsService>.Instance, settingsPath, xmltvDirectory);

        // Assert
        Assert.Equal(service.ConfiguredXmltvOutputPath, service.Settings.XmltvOutputPath);

        Directory.Delete(testDirectory, recursive: true);
    }

    /// <summary>
    /// Verifies that a settings document without an XMLTV path is initialized from startup configuration.
    /// </summary>
    [Fact]
    public void Constructor_MissingLegacyXmltvPath_UsesConfiguredDefault()
    {
        // Arrange
        var testDirectory = CreateTestDirectory();
        var xmltvDirectory = Path.Combine(testDirectory, "xmltv");
        var settingsPath = Path.Combine(testDirectory, AppConstants.SettingsFileName);
        File.WriteAllText(settingsPath, """{"TargetDays":2}""");

        // Act
        var service = new AppSettingsService(NullLogger<AppSettingsService>.Instance, settingsPath, xmltvDirectory);

        // Assert
        Assert.Equal(service.ConfiguredXmltvOutputPath, service.Settings.XmltvOutputPath);
        Directory.Delete(testDirectory, recursive: true);
    }

    /// <summary>
    /// Verifies a corrupt primary settings file recovers from the last atomically replaced version.
    /// </summary>
    [Fact]
    public async Task Constructor_CorruptPrimarySettings_RecoversBackup()
    {
        // Arrange
        var testDirectory = CreateTestDirectory();
        var settingsPath = Path.Combine(testDirectory, AppConstants.SettingsFileName);
        var service = new AppSettingsService(NullLogger<AppSettingsService>.Instance, settingsPath, testDirectory);
        await service.UpdateAsync(settings => settings.TargetDays = 5);
        await service.UpdateAsync(settings => settings.TargetDays = 7);
        await service.UpdateAsync(settings => settings.TargetDays = 9);
        await File.WriteAllTextAsync(settingsPath, """{"TargetDays":""", TestContext.Current.CancellationToken);

        // Act
        var recovered = new AppSettingsService(NullLogger<AppSettingsService>.Instance, settingsPath, testDirectory);
        await recovered.UpdateAsync(settings => settings.TargetDays = 11);
        var backupJson = await File.ReadAllTextAsync($"{settingsPath}.bak", TestContext.Current.CancellationToken);
        var backupSettings = JsonSerializer.Deserialize<AppSettings>(backupJson)!;

        // Assert
        Assert.Equal(11, recovered.Settings.TargetDays);
        Assert.Equal(7, backupSettings.TargetDays);
        Assert.True(File.Exists($"{settingsPath}.bak"));

        Directory.Delete(testDirectory, recursive: true);
    }

    /// <summary>
    /// Verifies concurrent updates serialize their mutations and persist the combined state.
    /// </summary>
    [Fact]
    public async Task UpdateAsync_ConcurrentUpdates_SerializesMutations()
    {
        // Arrange
        var testDirectory = CreateTestDirectory();
        var settingsPath = Path.Combine(testDirectory, AppConstants.SettingsFileName);
        var service = new AppSettingsService(NullLogger<AppSettingsService>.Instance, settingsPath, testDirectory);
        using var firstEntered = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        var firstUpdate = Task.Run(() => service.UpdateAsync(settings =>
        {
            firstEntered.Set();
            releaseFirst.Wait(TestContext.Current.CancellationToken);
            settings.TargetDays = 5;
        }), TestContext.Current.CancellationToken);
        Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        var secondCallbackEntered = false;

        // Act
        var secondUpdate = Task.Run(() => service.UpdateAsync(settings =>
        {
            secondCallbackEntered = true;
            settings.AutoGenerateXmltv = false;
        }), TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        var secondWaitedForFirst = !secondCallbackEntered;
        releaseFirst.Set();
        await Task.WhenAll(firstUpdate, secondUpdate);
        var persisted = JsonSerializer.Deserialize<AppSettings>(await File.ReadAllTextAsync(settingsPath, TestContext.Current.CancellationToken))!;

        // Assert
        Assert.True(secondWaitedForFirst);
        Assert.Equal(5, persisted.TargetDays);
        Assert.False(persisted.AutoGenerateXmltv);

        Directory.Delete(testDirectory, recursive: true);
    }

    private static string CreateTestDirectory()
    {
        var path = Path.Combine(Directory.GetCurrentDirectory(), "test-data", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
