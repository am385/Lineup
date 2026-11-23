using System.Text.Json;
using Lineup.Core.Common.Networking;
using Lineup.Core.Settings;
using Lineup.Core.Storage;
using Microsoft.Data.Sqlite;

namespace Lineup.Core;

/// <summary>
/// Describes whether persisted state satisfies the current Lineup schema boundary.
/// </summary>
public enum LineupStartupStatus
{
    /// <summary>Persisted state is absent or valid and uses the current schema.</summary>
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
/// Checks persisted settings readability, load-time validation and database schemas without modifying either artifact.
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
    /// Checks one serialized settings document without modifying it, returning a recovery result for invalid schemas or settings that cannot be loaded.
    /// </summary>
    /// <param name="json">Serialized settings document.</param>
    /// <returns>The settings schema result.</returns>
    public static LineupStartupState CheckSettingsContent(string json) => CheckSettingsContent(json, out _);

    /// <summary>Validates and prepares one settings document using the same reader as the settings service.</summary>
    /// <param name="json">Serialized settings document.</param>
    /// <param name="settings">Prepared settings when the document is valid; otherwise <see langword="null"/>.</param>
    /// <returns>The settings recovery result.</returns>
    internal static LineupStartupState CheckSettingsContent(string json, out LineupSettings? settings)
    {
        settings = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!HasSchemaVersion(root, CurrentSettingsSchemaVersion) ||
                !root.TryGetProperty("Streaming", out var streaming) ||
                !HasSchemaVersion(streaming, StreamingConfiguration.CurrentSchemaVersion))
            {
                return FactoryResetRequired("The persisted settings do not use the schema required by this Lineup release.");
            }

            if (root.TryGetProperty("KnownProxies", out var knownProxies))
            {
                if (knownProxies.ValueKind != JsonValueKind.Array ||
                    knownProxies.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String))
                {
                    return FactoryResetRequired("The persisted Known Proxies setting is invalid.");
                }
                try
                {
                    _ = TrustedProxySet.Parse(
                        knownProxies.EnumerateArray().Select(value => value.GetString()!),
                        "Known proxies");
                }
                catch (InvalidOperationException)
                {
                    return FactoryResetRequired("The persisted Known Proxies setting is invalid.");
                }
            }

            var loaded = JsonSerializer.Deserialize<LineupSettings>(json) ?? throw new JsonException("The settings document contained a null root value.");
            loaded.Streaming = ReadStreamingConfiguration(streaming, propertyNameCaseInsensitive: false);
            LineupSettingsService.PrepareLoadedSettings(loaded);
            settings = loaded;
            return new LineupStartupState(LineupStartupStatus.Ready);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
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
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.EnumerateObject().Any(property => string.Equals(property.Name, nameof(StreamingConfiguration.SchemaVersion), StringComparison.OrdinalIgnoreCase)))
            {
                return FactoryResetRequired("The streaming configuration does not declare the required schema version.");
            }
            var configuration = ReadStreamingConfiguration(document.RootElement, propertyNameCaseInsensitive: true);

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

    private static StreamingConfiguration ReadStreamingConfiguration(JsonElement json, bool propertyNameCaseInsensitive)
    {
        var configuration = JsonSerializer.Deserialize<StreamingConfiguration>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = propertyNameCaseInsensitive,
            RespectNullableAnnotations = true
        }) ?? throw new JsonException("The streaming configuration contained a null root value.");

        // Nullable annotation enforcement does not cover collection elements.
        if (configuration.PhysicalDevices.Any(device => device is null) ||
            configuration.GuideSources.Any(source => source is null) ||
            configuration.VirtualDevices.Any(device => device is null) ||
            configuration.GuideOutputs.Any(output => output is null) ||
            configuration.VirtualOutputSecurityPolicies.Any(policy => policy is null) ||
            configuration.VirtualOutputWorkerPolicies.Any(policy => policy is null))
        {
            throw new JsonException("The streaming topology contains a null entry.");
        }
        foreach (var device in configuration.VirtualDevices)
        {
            if (device.Hosting is { } hosting &&
                hosting.Bindings.Any(binding => binding is null || binding.Addresses.Any(address => address is null)))
            {
                throw new JsonException("Virtual-output hosting contains a null binding or address.");
            }
        }
        if (configuration.VirtualOutputSecurityPolicies.Any(policy =>
            policy.AllowedNetworks.Any(network => network is null) || policy.TrustedProxies.Any(proxy => proxy is null)))
        {
            throw new JsonException("A virtual-output security policy contains a null network or proxy.");
        }
        return configuration;
    }

    /// <summary>Executes check database.</summary>
    /// <param name="databasePath">Path to the test database.</param>
    /// <returns>The operation's result.</returns>
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

    /// <summary>Creates database metadata sql.</summary>
    /// <returns>The operation's result.</returns>
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

    private static bool HasSchemaVersion(JsonElement element, int expectedVersion) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty("SchemaVersion", out var version) &&
        version.ValueKind == JsonValueKind.Number &&
        version.TryGetInt32(out var parsedVersion) &&
        parsedVersion == expectedVersion;

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
