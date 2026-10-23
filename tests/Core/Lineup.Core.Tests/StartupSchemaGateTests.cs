using Lineup.Core.Settings;
using Lineup.Core.Storage;
using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
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
    /// Verifies a standalone schema-v2 streaming configuration requires a factory reset.
    /// </summary>
    [Fact]
    public void CheckStreamingConfigurationContent_SchemaVersionTwo_ReturnsFactoryResetRequired()
    {
        // Arrange
        const string settings = """{"SchemaVersion":2,"PhysicalDevices":[],"VirtualDevices":[]}""";

        // Act
        var state = StartupSchemaGate.CheckStreamingConfigurationContent(settings);

        // Assert
        Assert.Equal(LineupStartupStatus.FactoryResetRequired, state.Status);
        Assert.Contains("schema 2", state.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies checking an incompatible streaming file does not modify it before confirmation.
    /// </summary>
    [Fact]
    public async Task CheckStreamingConfiguration_SchemaVersionTwo_ReturnsFactoryResetRequiredWithoutChangingFile()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-streaming-schema-");
        var settingsPath = Path.Combine(root.FullName, "streaming.json");
        const string settings = """{"SchemaVersion":2,"PhysicalDevices":[],"VirtualDevices":[]}""";
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
