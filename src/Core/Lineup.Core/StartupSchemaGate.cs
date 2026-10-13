using System.Text.Json;
using Lineup.Core.Storage;
using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;
using Microsoft.Data.Sqlite;

namespace Lineup.Core;

/// <summary>
/// Describes whether persisted state satisfies the current Lineup schema boundary.
/// </summary>
public enum LineupStartupStatus
{
    /// <summary>Persisted state is absent or uses the current schema.</summary>
    Ready,

    /// <summary>Persisted state is invalid or does not use the required schema.</summary>
    FactoryResetRequired
}

/// <summary>
/// Host-neutral startup schema result.
/// </summary>
/// <param name="Status">Schema status.</param>
/// <param name="Reason">Safe operator-facing explanation when startup is restricted.</param>
public sealed record LineupStartupState(LineupStartupStatus Status, string? Reason = null)
{
    /// <summary>Gets whether normal application workflows must remain disabled.</summary>
    public bool IsFactoryResetRequired => Status == LineupStartupStatus.FactoryResetRequired;
}

/// <summary>
/// Checks persisted settings and database schemas without modifying either artifact.
/// </summary>
public static class StartupSchemaGate
{
    /// <summary>Gets the only application-settings schema accepted by this release.</summary>
    public const int CurrentSettingsSchemaVersion = 1;

    private const int CurrentDatabaseSchemaVersion = 1;
    private const string MetadataTableName = "LineupMetadata";
    private const string DatabaseSchemaVersionKey = "SchemaVersion";

    /// <summary>
    /// Checks the persisted artifacts owned by an application-data store.
    /// </summary>
    /// <param name="appDataStore">Persistent application-data store.</param>
    /// <returns>The startup schema result.</returns>
    public static LineupStartupState Check(IAppDataStore appDataStore)
    {
        ArgumentNullException.ThrowIfNull(appDataStore);
        return Check(appDataStore.SettingsPath, appDataStore.SettingsBackupPath, appDataStore.DatabasePath);
    }

    /// <summary>
    /// Checks explicit settings and database paths.
    /// </summary>
    /// <param name="settingsPath">Primary settings document path, or <see langword="null"/> when the host has no settings document.</param>
    /// <param name="settingsBackupPath">Settings backup path, or <see langword="null"/> when backups are not used.</param>
    /// <param name="databasePath">SQLite database path.</param>
    /// <returns>The startup schema result.</returns>
    public static LineupStartupState Check(string? settingsPath, string? settingsBackupPath, string databasePath)
    {
        var settingsState = CheckSettings(settingsPath, settingsBackupPath);
        if (settingsState.IsFactoryResetRequired)
        {
            return settingsState;
        }

        return CheckDatabase(databasePath);
    }

    /// <summary>
    /// Checks one serialized settings document without modifying it.
    /// </summary>
    /// <param name="json">Serialized settings document.</param>
    /// <returns>The settings schema result.</returns>
    public static LineupStartupState CheckSettingsContent(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("SchemaVersion", out var settingsVersion) ||
                !settingsVersion.TryGetInt32(out var parsedSettingsVersion) ||
                parsedSettingsVersion != CurrentSettingsSchemaVersion ||
                !root.TryGetProperty("Streaming", out var streaming) ||
                streaming.ValueKind != JsonValueKind.Object ||
                !streaming.TryGetProperty("SchemaVersion", out var streamingVersion) ||
                !streamingVersion.TryGetInt32(out var parsedStreamingVersion) ||
                parsedStreamingVersion != StreamingConfiguration.CurrentSchemaVersion)
            {
                return FactoryResetRequired("The persisted settings do not use the schema required by this Lineup release.");
            }

            return new LineupStartupState(LineupStartupStatus.Ready);
        }
        catch (JsonException)
        {
            return FactoryResetRequired("The persisted settings are invalid or cannot be read.");
        }
    }

    /// <summary>
    /// Checks a standalone streaming configuration file without modifying it.
    /// </summary>
    /// <param name="path">Streaming configuration path.</param>
    /// <returns>The streaming configuration schema result.</returns>
    public static LineupStartupState CheckStreamingConfiguration(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return new LineupStartupState(LineupStartupStatus.Ready);
        }

        try
        {
            return CheckStreamingConfigurationContent(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return FactoryResetRequired("The streaming configuration is invalid or cannot be read.");
        }
    }

    /// <summary>
    /// Checks standalone streaming configuration JSON without modifying it.
    /// </summary>
    /// <param name="json">Serialized streaming configuration.</param>
    /// <returns>The streaming configuration schema result.</returns>
    public static LineupStartupState CheckStreamingConfigurationContent(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            var configuration = JsonSerializer.Deserialize<StreamingConfiguration>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (configuration is null)
            {
                return FactoryResetRequired("The streaming configuration is empty.");
            }

            return configuration.SchemaVersion == StreamingConfiguration.CurrentSchemaVersion
                ? new LineupStartupState(LineupStartupStatus.Ready)
                : FactoryResetRequired(
                    $"Streaming settings schema {configuration.SchemaVersion} does not match required schema {StreamingConfiguration.CurrentSchemaVersion}.");
        }
        catch (JsonException)
        {
            return FactoryResetRequired("The streaming configuration is invalid or cannot be read.");
        }
    }

    internal static LineupStartupState CheckDatabase(string databasePath)
    {
        if (!File.Exists(databasePath))
        {
            return new LineupStartupState(LineupStartupStatus.Ready);
        }

        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString();
            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            using var tablesCommand = connection.CreateCommand();
            tablesCommand.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";
            var tableCount = Convert.ToInt32(tablesCommand.ExecuteScalar());
            if (tableCount == 0)
            {
                return new LineupStartupState(LineupStartupStatus.Ready);
            }

            using var versionCommand = connection.CreateCommand();
            versionCommand.CommandText = $"""SELECT "Value" FROM "{MetadataTableName}" WHERE "Key" = $key LIMIT 1;""";
            versionCommand.Parameters.AddWithValue("$key", DatabaseSchemaVersionKey);
            var value = versionCommand.ExecuteScalar() as string;
            return int.TryParse(value, out var version) && version == CurrentDatabaseSchemaVersion
                ? new LineupStartupState(LineupStartupStatus.Ready)
                : FactoryResetRequired("The persisted database does not use the schema required by this Lineup release.");
        }
        catch (SqliteException)
        {
            return FactoryResetRequired("The persisted database is invalid or does not use the required schema.");
        }
    }

    internal static string CreateDatabaseMetadataSql() => $"""
        CREATE TABLE IF NOT EXISTS "{MetadataTableName}" (
            "Key" TEXT NOT NULL CONSTRAINT "PK_{MetadataTableName}" PRIMARY KEY,
            "Value" TEXT NOT NULL
        );
        INSERT OR IGNORE INTO "{MetadataTableName}" ("Key", "Value") VALUES ('{DatabaseSchemaVersionKey}', '{CurrentDatabaseSchemaVersion}');
        """;

    private static LineupStartupState CheckSettings(string? settingsPath, string? settingsBackupPath)
    {
        var primaryExists = settingsPath != null && File.Exists(settingsPath);
        var backupExists = settingsBackupPath != null && File.Exists(settingsBackupPath);
        if (!primaryExists && !backupExists)
        {
            return new LineupStartupState(LineupStartupStatus.Ready);
        }

        var primaryState = primaryExists ? CheckSettingsDocument(settingsPath!) : null;
        if (primaryState is { IsFactoryResetRequired: false })
        {
            return primaryState;
        }

        var backupState = backupExists ? CheckSettingsDocument(settingsBackupPath!) : null;
        return backupState is { IsFactoryResetRequired: false }
            ? backupState
            : primaryState ?? backupState!;
    }

    private static LineupStartupState CheckSettingsDocument(string path)
    {
        try
        {
            return CheckSettingsContent(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return FactoryResetRequired("The persisted settings are invalid or cannot be read.");
        }
    }

    private static LineupStartupState FactoryResetRequired(string reason) => new(LineupStartupStatus.FactoryResetRequired, reason);
}

/// <summary>
/// Indicates that an operation attempted to open persisted state which requires an explicit factory reset.
/// </summary>
public sealed class FactoryResetRequiredException : InvalidOperationException
{
    /// <summary>Initializes the exception with an operator-facing reason.</summary>
    /// <param name="message">Schema failure explanation.</param>
    public FactoryResetRequiredException(string message)
        : base(message)
    {
    }
}
