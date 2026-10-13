using System.Text.Json;
using Lineup.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>
/// Verifies configuration-aware application settings initialization.
/// </summary>
public class LineupSettingsServiceTests
{
    /// <summary>
    /// Verifies fresh settings persist exactly one built-in Web Player virtual device.
    /// </summary>
    [Fact]
    public async Task SaveAsync_FreshSettings_PersistsBuiltInWebPlayer()
    {
        // Arrange
        var testDirectory = CreateTestDirectory();
        var settingsPath = Path.Combine(testDirectory, AppConstants.SettingsFileName);
        var store = new AppDataStore(testDirectory);
        var configuration = new ConfigurationBuilder().Build();
        var normalizers = new IStreamingConfigurationNormalizer[] { new WebPlayerVirtualTunerDriver() };
        var service = new LineupSettingsService(NullLogger<LineupSettingsService>.Instance, store, configuration, normalizers);

        // Act
        await service.SaveAsync();
        var reloaded = new LineupSettingsService(NullLogger<LineupSettingsService>.Instance, store, configuration, normalizers);

        // Assert
        var webPlayer = Assert.Single(reloaded.Settings.Streaming.VirtualDevices);
        Assert.Equal("web-player", webPlayer.Adapter.Kind);
        Assert.Equal("Web Player", webPlayer.Name);
        Directory.Delete(testDirectory, recursive: true);
    }

    /// <summary>
    /// Verifies that unversioned settings require an explicit factory reset instead of implicit migration.
    /// </summary>
    [Fact]
    public async Task Constructor_UnversionedSettings_RequiresFactoryReset()
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
        var service = new LineupSettingsService(NullLogger<LineupSettingsService>.Instance, settingsPath);

        // Assert
        await Assert.ThrowsAsync<FactoryResetRequiredException>(() => service.SaveAsync());
        Assert.False(service.Settings.IsSetupComplete);
        Assert.Contains("schema", StartupSchemaGate.Check(settingsPath, $"{settingsPath}.bak", Path.Combine(testDirectory, AppConstants.DefaultDatabaseFileName)).Reason, StringComparison.OrdinalIgnoreCase);
        Directory.Delete(testDirectory, recursive: true);
    }

    /// <summary>
    /// Verifies legacy proxy state is never persisted beside the streaming topology.
    /// </summary>
    [Fact]
    public async Task UpdateAsync_LegacyProxyState_IsNotPersisted()
    {
        // Arrange
        var testDirectory = CreateTestDirectory();
        var settingsPath = Path.Combine(testDirectory, AppConstants.SettingsFileName);
        var service = new LineupSettingsService(NullLogger<LineupSettingsService>.Instance, settingsPath);

        // Act
        await service.SaveAsync();
        var persisted = await File.ReadAllTextAsync(settingsPath, TestContext.Current.CancellationToken);

        // Assert
        Assert.DoesNotContain("EnableHdHomeRunProxy", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("HdHomeRunProxyProfiles", persisted, StringComparison.Ordinal);
        Directory.Delete(testDirectory, recursive: true);
    }

    /// <summary>
    /// Verifies that the configured guide-output directory contains the canonical XMLTV output file.
    /// </summary>
    [Fact]
    public async Task Constructor_ConfiguredGuideOutputDirectory_UsesCanonicalFileName()
    {
        // Arrange
        var testDirectory = CreateTestDirectory();
        var xmltvDirectory = Path.Combine(testDirectory, "xmltv");
        Directory.CreateDirectory(xmltvDirectory);
        var store = new AppDataStore(testDirectory);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [AppConstants.GuideOutputDirConfigKey] = xmltvDirectory
            })
            .Build();

        // Act
        var service = new LineupSettingsService(NullLogger<LineupSettingsService>.Instance, store, configuration, []);
        await service.SaveAsync();
        var settingsPath = Path.Combine(testDirectory, AppConstants.SettingsFileName);
        var json = await File.ReadAllTextAsync(settingsPath, TestContext.Current.CancellationToken);
        var persistedSettings = JsonSerializer.Deserialize<LineupSettings>(json)!;

        // Assert
        Assert.Equal("Lineup:GuideOutputDir", AppConstants.GuideOutputDirConfigKey);
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
        var service = new LineupSettingsService(NullLogger<LineupSettingsService>.Instance, Path.Combine(testDirectory, AppConstants.SettingsFileName), configuredFile);

        // Assert
        Assert.Equal(Path.GetFullPath(configuredFile), service.Settings.XmltvOutputPath);
        Assert.Equal(service.Settings.XmltvOutputPath, service.ConfiguredXmltvOutputPath);

        Directory.Delete(testDirectory, recursive: true);
    }

    /// <summary>
    /// Verifies that omitting the guide-output override uses the root guide directory.
    /// </summary>
    [Fact]
    public void Constructor_GuideOutputPathNotConfigured_UsesRootGuideDirectory()
    {
        // Arrange
        var testDirectory = CreateTestDirectory();
        var settingsPath = Path.Combine(testDirectory, AppConstants.SettingsFileName);
        var expectedPath = Path.GetFullPath(Path.Combine(AppConstants.DefaultGuideOutputDirectoryPath, AppConstants.DefaultXmltvFileName));

        // Act
        var service = new LineupSettingsService(NullLogger<LineupSettingsService>.Instance, settingsPath);

        // Assert
        Assert.Equal("/guide", AppConstants.DefaultGuideOutputDirectoryPath);
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
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(new LineupSettings { XmltvOutputPath = persistedFile }));

        // Act
        var service = new LineupSettingsService(NullLogger<LineupSettingsService>.Instance, settingsPath, xmltvDirectory);

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
        var service = new LineupSettingsService(NullLogger<LineupSettingsService>.Instance, settingsPath, xmltvDirectory);

        // Act
        await service.UpdateAsync(settings => settings.XmltvOutputPath = Path.Combine("..", "outside.xml"));
        var json = await File.ReadAllTextAsync(settingsPath, TestContext.Current.CancellationToken);
        var persistedSettings = JsonSerializer.Deserialize<LineupSettings>(json)!;
        var reloaded = new LineupSettingsService(NullLogger<LineupSettingsService>.Instance, settingsPath, xmltvDirectory);

        // Assert
        Assert.Equal(Path.Combine("..", "outside.xml"), service.Settings.XmltvOutputPath);
        Assert.Equal(service.Settings.XmltvOutputPath, persistedSettings.XmltvOutputPath);
        Assert.Equal(service.Settings.XmltvOutputPath, reloaded.Settings.XmltvOutputPath);

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
        var service = new LineupSettingsService(NullLogger<LineupSettingsService>.Instance, settingsPath, testDirectory);
        await service.UpdateAsync(settings => settings.TargetDays = 5);
        await service.UpdateAsync(settings => settings.TargetDays = 7);
        await service.UpdateAsync(settings => settings.TargetDays = 9);
        await File.WriteAllTextAsync(settingsPath, """{"TargetDays":""", TestContext.Current.CancellationToken);

        // Act
        var recovered = new LineupSettingsService(NullLogger<LineupSettingsService>.Instance, settingsPath, testDirectory);
        await recovered.UpdateAsync(settings => settings.TargetDays = 11);
        var backupJson = await File.ReadAllTextAsync($"{settingsPath}.bak", TestContext.Current.CancellationToken);
        var backupSettings = JsonSerializer.Deserialize<LineupSettings>(backupJson)!;

        // Assert
        Assert.Equal(11, recovered.Settings.TargetDays);
        Assert.Equal(7, backupSettings.TargetDays);
        Assert.True(File.Exists($"{settingsPath}.bak"));

        Directory.Delete(testDirectory, recursive: true);
    }

    /// <summary>Verifies malformed Known Proxies in both persisted documents require explicit recovery instead of replacing settings with defaults.</summary>
    [Fact]
    public async Task Constructor_InvalidKnownProxiesWithoutValidBackup_RequiresFactoryReset()
    {
        // Arrange
        var testDirectory = CreateTestDirectory();
        var settingsPath = Path.Combine(testDirectory, AppConstants.SettingsFileName);
        var invalidJson = JsonSerializer.Serialize(new LineupSettings { KnownProxies = ["proxy.internal"] });
        await File.WriteAllTextAsync(settingsPath, invalidJson, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync($"{settingsPath}.bak", invalidJson, TestContext.Current.CancellationToken);

        // Act
        var service = new LineupSettingsService(NullLogger<LineupSettingsService>.Instance, settingsPath, testDirectory);
        var exception = await Assert.ThrowsAsync<FactoryResetRequiredException>(() => service.SaveAsync());

        // Assert
        Assert.Contains("Known Proxies", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(invalidJson, await File.ReadAllTextAsync(settingsPath, TestContext.Current.CancellationToken));
        Assert.Equal(invalidJson, await File.ReadAllTextAsync($"{settingsPath}.bak", TestContext.Current.CancellationToken));

        Directory.Delete(testDirectory, recursive: true);
    }

    /// <summary>Verifies a valid backup remains authoritative when the primary Known Proxies setting is malformed.</summary>
    [Fact]
    public async Task Constructor_InvalidPrimaryKnownProxies_RecoversValidBackup()
    {
        // Arrange
        var testDirectory = CreateTestDirectory();
        var settingsPath = Path.Combine(testDirectory, AppConstants.SettingsFileName);
        await File.WriteAllTextAsync(
            settingsPath,
            JsonSerializer.Serialize(new LineupSettings { KnownProxies = ["proxy.internal"] }),
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            $"{settingsPath}.bak",
            JsonSerializer.Serialize(new LineupSettings { TargetDays = 7, KnownProxies = ["10.0.0.2"] }),
            TestContext.Current.CancellationToken);

        // Act
        var service = new LineupSettingsService(NullLogger<LineupSettingsService>.Instance, settingsPath, testDirectory);

        // Assert
        Assert.Equal(7, service.Settings.TargetDays);
        Assert.Equal(["10.0.0.2"], service.Settings.KnownProxies);

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
        var service = new LineupSettingsService(NullLogger<LineupSettingsService>.Instance, settingsPath, testDirectory);
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
        var persisted = JsonSerializer.Deserialize<LineupSettings>(await File.ReadAllTextAsync(settingsPath, TestContext.Current.CancellationToken))!;

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
