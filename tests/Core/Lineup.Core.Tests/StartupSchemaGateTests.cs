using Lineup.Core.Settings;
using Lineup.Core.Storage;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.Core.Tests;

/// <summary>
/// Verifies non-destructive persisted-state schema checks.
/// </summary>
public sealed class StartupSchemaGateTests
{
    /// <summary>Gets invalid schema values at both the application and nested streaming boundaries.</summary>
    public static TheoryData<string, bool> InvalidSchemaVersions
    {
        get
        {
            var cases = new TheoryData<string, bool>();
            foreach (var value in new[] { "\"1\"", "null", "true", "false", "{}", "[]", "1.5", "2147483648" })
            {
                cases.Add(value, false);
                cases.Add(value, true);
            }
            return cases;
        }
    }

    /// <summary>Verifies malformed schema values return a recovery result rather than throwing from JSON number access.</summary>
    [Theory]
    [MemberData(nameof(InvalidSchemaVersions))]
    public void CheckSettingsContent_InvalidSchemaValue_ReturnsFactoryResetRequired(string schemaValue, bool streamingSchema)
    {
        // Arrange
        var settings = CreateSettingsWithSchemaValue(schemaValue, streamingSchema);

        // Act
        var state = StartupSchemaGate.CheckSettingsContent(settings);

        // Assert
        Assert.Equal(LineupStartupStatus.FactoryResetRequired, state.Status);
        Assert.Contains("schema", state.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Verifies malformed primary schema values permit healthy backup recovery without modifying persisted files.</summary>
    [Theory]
    [MemberData(nameof(InvalidSchemaVersions))]
    public async Task Check_InvalidSchemaValue_RecoversHealthyBackupWithoutChangingFiles(string schemaValue, bool streamingSchema)
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-schema-value-");
        try
        {
            var settingsPath = Path.Combine(root.FullName, AppConstants.SettingsFileName);
            var backupPath = $"{settingsPath}.bak";
            var settings = CreateSettingsWithSchemaValue(schemaValue, streamingSchema);
            var backup = JsonSerializer.Serialize(new LineupSettings { TargetDays = 7 });
            await File.WriteAllTextAsync(settingsPath, settings, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(backupPath, backup, TestContext.Current.CancellationToken);

            // Act
            var state = StartupSchemaGate.Check(settingsPath, backupPath, Path.Combine(root.FullName, AppConstants.DefaultDatabaseFileName));
            var service = new LineupSettingsService(NullLogger<LineupSettingsService>.Instance, settingsPath, root.FullName);

            // Assert
            Assert.Equal(LineupStartupStatus.Ready, state.Status);
            Assert.Null(state.Reason);
            Assert.Equal(7, service.Settings.TargetDays);
            Assert.Equal(settings, await File.ReadAllTextAsync(settingsPath, TestContext.Current.CancellationToken));
            Assert.Equal(backup, await File.ReadAllTextAsync(backupPath, TestContext.Current.CancellationToken));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>Verifies invalid schemas without a healthy backup keep recovery available and prohibit overwriting persisted state.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Check_InvalidSchemaValueWithoutHealthyBackup_RequiresResetWithoutChangingFiles(bool streamingSchema, bool backupExists)
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-schema-value-");
        try
        {
            var settingsPath = Path.Combine(root.FullName, AppConstants.SettingsFileName);
            var backupPath = $"{settingsPath}.bak";
            var settings = CreateSettingsWithSchemaValue("null", streamingSchema);
            await File.WriteAllTextAsync(settingsPath, settings, TestContext.Current.CancellationToken);
            if (backupExists)
            {
                await File.WriteAllTextAsync(backupPath, settings, TestContext.Current.CancellationToken);
            }

            // Act
            var state = StartupSchemaGate.Check(settingsPath, backupPath, Path.Combine(root.FullName, AppConstants.DefaultDatabaseFileName));
            var service = new LineupSettingsService(NullLogger<LineupSettingsService>.Instance, settingsPath, root.FullName);
            var exception = await Assert.ThrowsAsync<FactoryResetRequiredException>(() => service.SaveAsync());

            // Assert
            Assert.Equal(LineupStartupStatus.FactoryResetRequired, state.Status);
            Assert.Equal(state.Reason, exception.Message);
            Assert.Equal(settings, await File.ReadAllTextAsync(settingsPath, TestContext.Current.CancellationToken));
            Assert.Equal(backupExists, File.Exists(backupPath));
            if (backupExists)
            {
                Assert.Equal(settings, await File.ReadAllTextAsync(backupPath, TestContext.Current.CancellationToken));
            }
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>Verifies current integer settings and streaming schema versions remain accepted.</summary>
    [Fact]
    public void CheckSettingsContent_CurrentSchemas_ReturnsReady()
    {
        // Arrange
        var settings = JsonSerializer.Serialize(new LineupSettings());

        // Act
        var state = StartupSchemaGate.CheckSettingsContent(settings);

        // Assert
        Assert.Equal(LineupStartupStatus.Ready, state.Status);
        Assert.Null(state.Reason);
    }

    /// <summary>Verifies valid hardware configuration remains readable without initializing a device or changing the schema.</summary>
    [Fact]
    public void CheckSettingsContent_HardwareEncoderConfigured_DoesNotRequireGpu()
    {
        // Arrange
        var settings = new LineupSettings
        {
            Streaming = new StreamingConfiguration
            {
                PhysicalDefaults = new PhysicalTranscodeProfile { HardwareEncoding = new() { Encoder = HardwareVideoEncoder.VideoToolbox } },
                VirtualDefaults = new VirtualTranscodeProfile { HardwareEncoding = new() { Encoder = HardwareVideoEncoder.Vaapi, Device = "/dev/dri/renderD129" } }
            }
        };

        // Act
        var state = StartupSchemaGate.CheckSettingsContent(JsonSerializer.Serialize(settings));
        var legacy = JsonSerializer.Deserialize<PhysicalTranscodeProfile>("{}");

        // Assert
        Assert.Equal(LineupStartupStatus.Ready, state.Status);
        Assert.Equal(HardwareVideoEncoder.Software, legacy!.HardwareEncoding.Encoder);
    }

    /// <summary>Verifies current schema numbers cannot hide settings deserialization or preparation failures.</summary>
    [Theory]
    [InlineData("AutoFetchInterval", "\"invalid\"")]
    [InlineData("NextAutoFetchTime", "\"invalid\"")]
    [InlineData("ProtectedContentMode", "{}")]
    [InlineData("XmltvOutputPath", "\"\"")]
    public void CheckSettingsContent_UnreadableCurrentSettings_RequiresRecovery(string property, string value)
    {
        // Arrange
        var document = JsonSerializer.SerializeToNode(new LineupSettings())!.AsObject();
        document[property] = JsonNode.Parse(value);

        // Act
        var state = StartupSchemaGate.CheckSettingsContent(document.ToJsonString());

        // Assert
        Assert.True(state.IsFactoryResetRequired);
        Assert.NotEmpty(state.Reason!);
    }

    /// <summary>Verifies unreadable current settings cannot prune saved channels or bindings, while a validated backup restores the configured devices.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Check_UnreadableCurrentSettings_PreservesTopologyAndRecoversOnlyValidBackup(bool validBackup, bool backupExists)
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-settings-recovery-");
        try
        {
            var app = new AppDataStore(root.FullName);
            var options = new DbContextOptionsBuilder<EpgDbContext>().UseSqlite($"Data Source={app.DatabasePath};Pooling=False").Options;
            var contexts = new PooledDbContextFactory<EpgDbContext>(options);
            var topology = new StreamingTopologyStore(contexts);
            var guides = new DeviceGuideStore(contexts);
            var deviceId = PhysicalDeviceId.New();
            var sourceId = new SourceChannelId(deviceId, "saved-channel");
            var guideId = new GuideChannelId(GuideSourceId.New(), "saved-guide");
            var token = TestContext.Current.CancellationToken;
            await topology.ReconcileAsync(deviceId, [new PhysicalSourceChannel { Id = sourceId, ProviderNumber = "7", ProviderName = "Saved channel" }], token);
            await guides.ImportAsync(new GuideImport
            {
                SourceId = guideId.GuideSourceId,
                Channels = [new GuideChannel { Id = guideId, Number = "7", Name = "Saved guide" }],
                Programs = [],
                CoverageStartUtc = DateTimeOffset.UtcNow,
                CoverageEndUtc = DateTimeOffset.UtcNow.AddHours(1)
            }, token);
            await guides.SetBindingAsync(sourceId, guideId, token);
            var intended = new LineupSettings
            {
                Streaming = new StreamingConfiguration
                {
                    PhysicalDevices = [new PhysicalDeviceDefinition
                    {
                        Id = deviceId,
                        Name = "Saved device",
                        Enabled = false,
                        Adapter = new AdapterConfiguration("test-no-hardware", JsonSerializer.SerializeToElement(new { }))
                    }]
                },
                AutoFetchInterval = TimeSpan.FromHours(24)
            };
            var document = JsonSerializer.SerializeToNode(intended)!.AsObject();
            document[nameof(LineupSettings.AutoFetchInterval)] = "invalid";
            var primary = document.ToJsonString();
            var backup = validBackup ? JsonSerializer.Serialize(intended) : primary;
            await File.WriteAllTextAsync(app.SettingsPath, primary, token);
            if (backupExists)
            {
                await File.WriteAllTextAsync(app.SettingsBackupPath, backup, token);
            }

            // Act
            var state = StartupSchemaGate.Check(app);
            var service = new LineupSettingsService(NullLogger<LineupSettingsService>.Instance, app, new ConfigurationBuilder().Build(), state);
            var authority = (IPhysicalConfigurationAuthority)service;
            var published = await authority.TryPublishAsync(service.Settings.Streaming, _ => Task.CompletedTask, token);
            var catalog = new PhysicalDeviceCatalog(new StreamingAdapterRegistry([], [], []), new PhysicalDeviceRuntimeState(), authority: authority);
            await new PhysicalLineupService(catalog, topology).RefreshAsync(service.Settings.Streaming, token);

            // Assert
            Assert.Equal(!validBackup, state.IsFactoryResetRequired);
            Assert.Equal(validBackup, published);
            await using (var context = await contexts.CreateDbContextAsync(token))
            {
                Assert.Single(await context.PhysicalSourceChannels.ToArrayAsync(token));
                Assert.Single(await context.SourceGuideBindings.ToArrayAsync(token));
            }
            Assert.Equal(primary, await File.ReadAllTextAsync(app.SettingsPath, token));
            Assert.Equal(backupExists, File.Exists(app.SettingsBackupPath));
            if (backupExists)
            {
                Assert.Equal(backup, await File.ReadAllTextAsync(app.SettingsBackupPath, token));
            }
            if (validBackup)
            {
                Assert.Equal(deviceId, Assert.Single(service.Settings.Streaming.PhysicalDevices).Id);
                Assert.Equal(intended.AutoFetchInterval, service.Settings.AutoFetchInterval);
                await service.UpdateAsync(settings => settings.TargetDays = 7);
                Assert.Equal(backup, await File.ReadAllTextAsync(app.SettingsBackupPath, token));
                var restored = JsonSerializer.Deserialize<LineupSettings>(await File.ReadAllTextAsync(app.SettingsPath, token))!;
                Assert.Equal(deviceId, Assert.Single(restored.Streaming.PhysicalDevices).Id);
            }
            else
            {
                await Assert.ThrowsAsync<FactoryResetRequiredException>(() => service.SaveAsync());
                await Assert.ThrowsAsync<FactoryResetRequiredException>(() => service.UpdateAsync(settings => settings.Streaming = new()));
                Assert.Equal(primary, await File.ReadAllTextAsync(app.SettingsPath, token));
            }
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>Verifies corruption after the startup check cannot make the constructor publish default settings.</summary>
    [Fact]
    public async Task Constructor_SettingsBecomeUnreadableAfterStartupCheck_RejectsDefaults()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-settings-race-");
        try
        {
            var app = new AppDataStore(root.FullName);
            await File.WriteAllTextAsync(app.SettingsPath, JsonSerializer.Serialize(new LineupSettings()), TestContext.Current.CancellationToken);
            var state = StartupSchemaGate.Check(app);
            var document = JsonSerializer.SerializeToNode(new LineupSettings())!.AsObject();
            document[nameof(LineupSettings.AutoFetchInterval)] = "invalid";
            var primary = document.ToJsonString();
            await File.WriteAllTextAsync(app.SettingsPath, primary, TestContext.Current.CancellationToken);

            // Act
            var failure = Assert.Throws<FactoryResetRequiredException>(() =>
                new LineupSettingsService(NullLogger<LineupSettingsService>.Instance, app, new ConfigurationBuilder().Build(), state));

            // Assert
            Assert.False(state.IsFactoryResetRequired);
            Assert.NotEmpty(failure.Message);
            Assert.Equal(primary, await File.ReadAllTextAsync(app.SettingsPath, TestContext.Current.CancellationToken));
            Assert.False(File.Exists(app.SettingsBackupPath));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>Verifies a failed reload surfaces recovery without replacing previously loaded settings or overwriting the unreadable document.</summary>
    [Fact]
    public async Task LoadAsync_UnreadableSettingsWithoutBackup_ThrowsAndPreservesLoadedSettings()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-settings-reload-");
        try
        {
            var app = new AppDataStore(root.FullName);
            var saved = new LineupSettings { TargetDays = 7 };
            await File.WriteAllTextAsync(app.SettingsPath, JsonSerializer.Serialize(saved), TestContext.Current.CancellationToken);
            var service = new LineupSettingsService(NullLogger<LineupSettingsService>.Instance, app, new ConfigurationBuilder().Build());
            var loaded = service.Settings;
            var document = JsonSerializer.SerializeToNode(saved)!.AsObject();
            document[nameof(LineupSettings.AutoFetchInterval)] = "invalid";
            var primary = document.ToJsonString();
            await File.WriteAllTextAsync(app.SettingsPath, primary, TestContext.Current.CancellationToken);

            // Act
            await Assert.ThrowsAsync<FactoryResetRequiredException>(() => service.LoadAsync());

            // Assert
            Assert.Same(loaded, service.Settings);
            Assert.Equal(7, service.Settings.TargetDays);
            await Assert.ThrowsAsync<FactoryResetRequiredException>(() => service.SaveAsync());
            await Assert.ThrowsAsync<FactoryResetRequiredException>(() => service.UpdateAsync(settings => settings.TargetDays = 9));
            Assert.False(((IPhysicalConfigurationAuthority)service).IsDesired(service.Settings.Streaming));
            Assert.Equal(primary, await File.ReadAllTextAsync(app.SettingsPath, TestContext.Current.CancellationToken));
            Assert.False(File.Exists(app.SettingsBackupPath));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>Verifies writes queued behind a failing reload cannot overwrite unreadable persisted settings.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoadAsync_FailsWhileWriteIsQueued_BlocksQueuedWrite(bool directSave)
    {
        // Arrange
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = Substitute.For<IAppDataStore>();
        store.SettingsPath.Returns("settings.json");
        store.SettingsBackupPath.Returns("settings.json.bak");
        store.FileExists(store.SettingsPath).Returns(true);
        store.ReadAllText(store.SettingsPath).Returns(JsonSerializer.Serialize(new LineupSettings { TargetDays = 7 }));
        store.ReadAllTextAsync(store.SettingsPath, Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            entered.TrySetResult();
            await release.Task;
            return """{"SchemaVersion":1,"Streaming":{"SchemaVersion":1},"AutoFetchInterval":"invalid"}""";
        });
        var state = new LineupStartupState(LineupStartupStatus.Ready);
        var service = new LineupSettingsService(NullLogger<LineupSettingsService>.Instance, store, new ConfigurationBuilder().Build(), state);
        var mutationInvoked = false;
        var reload = service.LoadAsync();
        Task? write = null;
        try
        {
            // Act
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            write = directSave ? service.SaveAsync() : service.UpdateAsync(settings =>
            {
                mutationInvoked = true;
                settings.TargetDays = 9;
            });
        }
        finally
        {
            release.TrySetResult();
            try
            {
                await Assert.ThrowsAsync<FactoryResetRequiredException>(() => reload);
            }
            finally
            {
                if (write is not null)
                {
                    await Assert.ThrowsAsync<FactoryResetRequiredException>(() => write);
                }
            }
        }

        // Assert
        Assert.False(mutationInvoked);
        Assert.Equal(7, service.Settings.TargetDays);
        await store.DidNotReceive().WriteAtomicallyAsync(Arg.Any<string>(), Arg.Any<Func<Stream, CancellationToken, Task>>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Verifies an unsupported application and streaming schema requires the documented factory reset.
    /// </summary>
    [Fact]
    public void CheckSettingsContent_SchemaVersionTwo_ReturnsFactoryResetRequired()
    {
        // Arrange
        const string settings = """{"SchemaVersion":2,"Streaming":{"SchemaVersion":2}}""";

        // Act
        var state = StartupSchemaGate.CheckSettingsContent(settings);

        // Assert
        Assert.Equal(LineupStartupStatus.FactoryResetRequired, state.Status);
    }

    /// <summary>
    /// Verifies a future streaming schema requires a factory reset.
    /// </summary>
    [Fact]
    public void CheckStreamingConfigurationContent_FutureSchema_ReturnsFactoryResetRequired()
    {
        // Arrange
        var settings = $$"""{"SchemaVersion":{{StreamingConfiguration.CurrentSchemaVersion + 1}},"PhysicalDevices":[],"VirtualDevices":[]}""";

        // Act
        var state = StartupSchemaGate.CheckStreamingConfigurationContent(settings);

        // Assert
        Assert.Equal(LineupStartupStatus.FactoryResetRequired, state.Status);
        Assert.Contains($"schema {StreamingConfiguration.CurrentSchemaVersion}", state.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies checking an incompatible streaming file does not modify it before confirmation.
    /// </summary>
    [Fact]
    public async Task CheckStreamingConfiguration_FutureSchema_ReturnsFactoryResetRequiredWithoutChangingFile()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-streaming-schema-");
        var settingsPath = Path.Combine(root.FullName, "streaming.json");
        var settings = $$"""{"SchemaVersion":{{StreamingConfiguration.CurrentSchemaVersion + 1}},"PhysicalDevices":[],"VirtualDevices":[]}""";
        await File.WriteAllTextAsync(settingsPath, settings, TestContext.Current.CancellationToken);

        // Act
        var state = StartupSchemaGate.CheckStreamingConfiguration(settingsPath);

        // Assert
        Assert.Equal(LineupStartupStatus.FactoryResetRequired, state.Status);
        Assert.Equal(settings, await File.ReadAllTextAsync(settingsPath, TestContext.Current.CancellationToken));
        root.Delete(recursive: true);
    }

    /// <summary>
    /// Verifies a standalone current streaming configuration is accepted.
    /// </summary>
    [Fact]
    public void CheckStreamingConfigurationContent_CurrentSchema_ReturnsReady()
    {
        // Arrange
        var settings = $$"""{"SchemaVersion":{{StreamingConfiguration.CurrentSchemaVersion}},"PhysicalDevices":[],"VirtualDevices":[]}""";

        // Act
        var state = StartupSchemaGate.CheckStreamingConfigurationContent(settings);

        // Assert
        Assert.Equal(LineupStartupStatus.Ready, state.Status);
    }

    /// <summary>Verifies the immediately preceding streaming schema requires a factory reset.</summary>
    [Fact]
    public void CheckStreamingConfigurationContent_PreviousSchema_ReturnsFactoryResetRequired()
    {
        // Arrange
        var settings = $$"""{"SchemaVersion":{{StreamingConfiguration.CurrentSchemaVersion - 1}},"PhysicalDevices":[],"VirtualDevices":[]}""";

        // Act
        var state = StartupSchemaGate.CheckStreamingConfigurationContent(settings);

        // Assert
        Assert.Equal(LineupStartupStatus.FactoryResetRequired, state.Status);
    }

    /// <summary>Verifies unversioned standalone settings cannot adopt the new connection-budget schema implicitly.</summary>
    [Fact]
    public void CheckStreamingConfigurationContent_MissingSchema_ReturnsFactoryResetRequired()
    {
        // Arrange
        const string settings = """{"PhysicalDevices":[],"VirtualDevices":[]}""";

        // Act
        var state = StartupSchemaGate.CheckStreamingConfigurationContent(settings);

        // Assert
        Assert.Equal(LineupStartupStatus.FactoryResetRequired, state.Status);
    }

    /// <summary>
    /// Verifies invalid standalone streaming JSON requires an explicit factory reset.
    /// </summary>
    [Fact]
    public void CheckStreamingConfigurationContent_InvalidJson_ReturnsFactoryResetRequired()
    {
        // Arrange
        const string settings = """{"SchemaVersion":3""";

        // Act
        var state = StartupSchemaGate.CheckStreamingConfigurationContent(settings);

        // Assert
        Assert.Equal(LineupStartupStatus.FactoryResetRequired, state.Status);
    }

    /// <summary>
    /// Verifies a streaming factory reset writes a valid current configuration.
    /// </summary>
    [Fact]
    public async Task StreamingConfigurationFactoryReset_Reset_ReplacesIncompatibleConfiguration()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-streaming-reset-");
        var settingsPath = Path.Combine(root.FullName, "streaming.json");
        await File.WriteAllTextAsync(settingsPath, """{"SchemaVersion":2}""", TestContext.Current.CancellationToken);

        // Act
        StreamingConfigurationFactoryReset.Reset(settingsPath);

        // Assert
        var content = await File.ReadAllTextAsync(settingsPath, TestContext.Current.CancellationToken);
        Assert.Equal(LineupStartupStatus.Ready, StartupSchemaGate.CheckStreamingConfigurationContent(content).Status);
        root.Delete(recursive: true);
    }

    /// <summary>
    /// Verifies unversioned settings are preserved and require an explicit reset.
    /// </summary>
    [Fact]
    public async Task Check_UnversionedSettings_ReturnsFactoryResetRequiredWithoutChangingFile()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-schema-");
        var settingsPath = Path.Combine(root.FullName, AppConstants.SettingsFileName);
        const string legacySettings = """{"DeviceAddress":"legacy-tuner"}""";
        await File.WriteAllTextAsync(settingsPath, legacySettings, TestContext.Current.CancellationToken);

        // Act
        var state = StartupSchemaGate.Check(settingsPath, $"{settingsPath}.bak", Path.Combine(root.FullName, AppConstants.DefaultDatabaseFileName));

        // Assert
        Assert.Equal(LineupStartupStatus.FactoryResetRequired, state.Status);
        Assert.Equal(legacySettings, await File.ReadAllTextAsync(settingsPath, TestContext.Current.CancellationToken));
        root.Delete(recursive: true);
    }

    /// <summary>
    /// Verifies an unversioned database is neither upgraded nor deleted by the compatibility check.
    /// </summary>
    [Fact]
    public async Task Check_LegacyDatabase_ReturnsFactoryResetRequiredWithoutChangingSchema()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-schema-");
        var databasePath = Path.Combine(root.FullName, AppConstants.DefaultDatabaseFileName);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false
        }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE LegacyState (Id INTEGER PRIMARY KEY);";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        // Act
        var state = StartupSchemaGate.Check(null, null, databasePath);

        // Assert
        Assert.Equal(LineupStartupStatus.FactoryResetRequired, state.Status);
        await using (var verifyConnection = new SqliteConnection(connectionString))
        {
            await verifyConnection.OpenAsync(TestContext.Current.CancellationToken);
            await using var verifyCommand = verifyConnection.CreateCommand();
            verifyCommand.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'LineupMetadata';";
            Assert.Equal(0L, (long)(await verifyCommand.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
        }
        root.Delete(recursive: true);
    }

    /// <summary>
    /// Verifies a schema-v2 database is preserved and requires the documented factory reset.
    /// </summary>
    [Fact]
    public async Task Check_SchemaVersionTwoDatabase_ReturnsFactoryResetRequiredWithoutChangingVersion()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-schema-");
        var databasePath = Path.Combine(root.FullName, AppConstants.DefaultDatabaseFileName);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false
        }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE LineupMetadata ("Key" TEXT NOT NULL PRIMARY KEY, "Value" TEXT NOT NULL);
                INSERT INTO LineupMetadata ("Key", "Value") VALUES ('SchemaVersion', '2');
                """;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        // Act
        var state = StartupSchemaGate.Check(null, null, databasePath);

        // Assert
        Assert.Equal(LineupStartupStatus.FactoryResetRequired, state.Status);
        await using (var verifyConnection = new SqliteConnection(connectionString))
        {
            await verifyConnection.OpenAsync(TestContext.Current.CancellationToken);
            await using var verifyCommand = verifyConnection.CreateCommand();
            verifyCommand.CommandText = """SELECT "Value" FROM LineupMetadata WHERE "Key" = 'SchemaVersion';""";
            Assert.Equal("2", (string)(await verifyCommand.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
        }
        root.Delete(recursive: true);
    }

    /// <summary>
    /// Verifies a newly created database is marked with the current schema and remains compatible.
    /// </summary>
    [Fact]
    public async Task EnsureDatabaseCreatedAsync_NewDatabase_WritesCurrentSchemaVersion()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-schema-");
        var databasePath = Path.Combine(root.FullName, AppConstants.DefaultDatabaseFileName);
        var options = new DbContextOptionsBuilder<EpgDbContext>().UseSqlite($"Data Source={databasePath}").Options;
        var contexts = new PooledDbContextFactory<EpgDbContext>(options);
        var store = new StreamingTopologyStore(contexts);

        // Act
        _ = await store.ReadActiveAsync(TestContext.Current.CancellationToken);
        var state = StartupSchemaGate.Check(null, null, databasePath);

        // Assert
        Assert.Equal(LineupStartupStatus.Ready, state.Status);
        SqliteConnection.ClearAllPools();
        root.Delete(recursive: true);
    }

    private static string CreateSettingsWithSchemaValue(string schemaValue, bool streamingSchema) =>
        streamingSchema
            ? $$$"""{"SchemaVersion":{{{StartupSchemaGate.CurrentSettingsSchemaVersion}}},"Streaming":{"SchemaVersion":{{{schemaValue}}}}}"""
            : $$$"""{"SchemaVersion":{{{schemaValue}}},"Streaming":{"SchemaVersion":{{{StreamingConfiguration.CurrentSchemaVersion}}}}}""";
}
