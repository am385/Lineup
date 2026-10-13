using System.Text.Json;
using System.Text.Json.Serialization;
using Lineup.Core.Storage;
using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Lineup.Core.Settings;

/// <summary>
/// Controls API behavior when the tuner reports DRM-protected content.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProtectedContentMode
{
    /// <summary>
    /// Returns an explicit protected-content error.
    /// </summary>
    ReturnError,

    /// <summary>
    /// Streams a synthetic compatibility slate in the requested media format.
    /// </summary>
    StreamSlate
}

/// <summary>
/// Controls stream behavior for channels disabled in Lineup.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DisabledChannelMode
{
    /// <summary>
    /// Returns an explicit forbidden response.
    /// </summary>
    ReturnError,

    /// <summary>
    /// Streams a synthetic disabled-channel slate.
    /// </summary>
    StreamSlate
}

/// <summary>
/// Application settings that can be modified at runtime.
/// </summary>
public class LineupSettings
{
    private string? _xmltvOutputPath;
    private int _targetDays = 2;
    private int _epgHistoryRetentionHours = 24;
    private int _deviceRefreshIntervalMinutes = 10;
    private int _tunerRefreshIntervalSeconds = 30;
    private int _maximumConcurrentStreams;

    /// <summary>
    /// Gets the persisted application-settings schema version.
    /// </summary>
    public int SchemaVersion { get; init; } = StartupSchemaGate.CurrentSettingsSchemaVersion;

    /// <summary>
    /// Gets or sets the versioned provider-neutral streaming configuration.
    /// </summary>
    public StreamingConfiguration Streaming { get; set; } = new();

    /// <summary>
    /// Default number of days of EPG data to fetch/generate.
    /// </summary>
    public int TargetDays
    {
        get => _targetDays;
        set => _targetDays = Math.Max(1, value); // Minimum 1 day
    }

    /// <summary>
    /// Number of hours of ended program history retained in SQLite.
    /// </summary>
    public int EpgHistoryRetentionHours
    {
        get => _epgHistoryRetentionHours;
        set => _epgHistoryRetentionHours = Math.Clamp(value, 0, 24 * 30);
    }

    /// <summary>
    /// Interval between auto-fetch operations.
    /// A positive value enables auto-fetch; guide-source policies may randomize the actual schedule.
    /// </summary>
    public TimeSpan AutoFetchInterval { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Whether auto-fetch is enabled (interval > 0).
    /// </summary>
    public bool IsAutoFetchEnabled => AutoFetchInterval > TimeSpan.Zero;

    /// <summary>
    /// Whether the physical tuner channel lineup is refreshed before guide data is fetched.
    /// </summary>
    public bool RefreshChannelsBeforeGuideFetch { get; set; } = true;

    /// <summary>
    /// Persisted next XMLTV refresh time selected by the active guide-source scheduling policy.
    /// </summary>
    public DateTime? NextAutoFetchTime { get; set; }

    /// <summary>
    /// Interval in minutes between device info refreshes.
    /// Set to 0 to disable automatic device info refresh.
    /// Negative values are normalized to 0.
    /// </summary>
    public int DeviceRefreshIntervalMinutes
    {
        get => _deviceRefreshIntervalMinutes;
        set => _deviceRefreshIntervalMinutes = Math.Max(0, value);
    }

    /// <summary>
    /// Interval in seconds between tuner status refreshes.
    /// Set to 0 to disable automatic tuner status refresh.
    /// Negative values are normalized to 0.
    /// </summary>
    public int TunerRefreshIntervalSeconds
    {
        get => _tunerRefreshIntervalSeconds;
        set => _tunerRefreshIntervalSeconds = Math.Max(0, value);
    }

    /// <summary>
    /// Whether automatic device info refresh is enabled.
    /// </summary>
    public bool IsDeviceRefreshEnabled => DeviceRefreshIntervalMinutes > 0;

    /// <summary>
    /// Whether automatic tuner status refresh is enabled.
    /// </summary>
    public bool IsTunerRefreshEnabled => TunerRefreshIntervalSeconds > 0;

    /// <summary>
    /// Output path for the generated XMLTV file.
    /// Can be a filename (relative to working directory) or absolute path.
    /// </summary>
    public string XmltvOutputPath
    {
        get => _xmltvOutputPath ?? Path.Combine(AppConstants.DefaultGuideOutputDirectoryPath, AppConstants.DefaultXmltvFileName);
        set => _xmltvOutputPath = value;
    }

    /// <summary>
    /// Whether to automatically generate XMLTV file after auto-fetch completes.
    /// </summary>
    public bool AutoGenerateXmltv { get; set; } = true;

    /// <summary>
    /// IANA timezone ID for display (e.g., "America/New_York").
    /// Empty/null means use the TZ environment variable or system default.
    /// </summary>
    public string TimeZoneId { get; set; } = "";

    /// <summary>
    /// API behavior when a physical tuner reports protected content.
    /// </summary>
    public ProtectedContentMode ProtectedContentMode { get; set; } = ProtectedContentMode.ReturnError;

    /// <summary>
    /// Stream behavior when a client requests a disabled channel directly.
    /// </summary>
    public DisabledChannelMode DisabledChannelMode { get; set; } = DisabledChannelMode.ReturnError;

    /// <summary>
    /// Maximum number of concurrent hosted streams, or zero for unlimited.
    /// </summary>
    public int MaximumConcurrentStreams
    {
        get => _maximumConcurrentStreams;
        set => _maximumConcurrentStreams = Math.Clamp(value, 0, 100);
    }

    /// <summary>
    /// UTC timestamp of the last successful auto-fetch.
    /// Persisted so the service can calculate remaining time on restart instead of fetching immediately.
    /// </summary>
    public DateTime? LastAutoFetchTime { get; set; }

    // Non-persisting fallback surface used by partial hosts and focused component tests.
    // These values are deliberately excluded from the shared settings document.
    /// <summary>Gets or sets the fallback Web theme.</summary>
    [JsonIgnore]
    public string Theme { get; set; } = "auto";

    /// <summary>Gets or sets the fallback Dashboard refresh interval.</summary>
    [JsonIgnore]
    public int ActiveStreamRefreshIntervalSeconds
    {
        get;
        set => field = Math.Clamp(value, 0, 300);
    } = 5;

    /// <summary>Gets whether fallback Dashboard refresh is enabled.</summary>
    [JsonIgnore]
    public bool IsActiveStreamRefreshEnabled => ActiveStreamRefreshIntervalSeconds > 0;

    /// <summary>Gets or sets fallback client-address redaction.</summary>
    [JsonIgnore]
    public bool RedactApiClientAddresses { get; set; } = true;

    /// <summary>Gets or sets fallback device-address redaction.</summary>
    [JsonIgnore]
    public bool RedactApiDeviceAddresses { get; set; }

    /// <summary>Gets or sets fallback URL redaction.</summary>
    [JsonIgnore]
    public bool RedactApiUrls { get; set; }

    /// <summary>Gets or sets fallback Web setup state.</summary>
    [JsonIgnore]
    public bool IsSetupComplete { get; set; }

    /// <summary>Gets or sets fallback Web discovery state.</summary>
    [JsonIgnore]
    public bool EnableNetworkDiscovery { get; set; }

    /// <summary>Gets or sets the fallback advertised URL.</summary>
    [JsonIgnore]
    public string AdvertisedBaseUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets fallback file logging state.</summary>
    [JsonIgnore]
    public bool EnableFileLogging { get; set; }

    /// <summary>Gets or sets fallback logging override state.</summary>
    [JsonIgnore]
    public bool OverrideLoggingDefaults { get; set; }

    /// <summary>Gets or sets the fallback application log level.</summary>
    [JsonIgnore]
    public string ApplicationLogLevel { get; set; } = "Information";

    /// <summary>Gets or sets fallback category log levels.</summary>
    [JsonIgnore]
    public List<LogCategoryLevelSetting> LogCategoryOverrides { get; set; } = [];

    /// <summary>Gets or sets the fallback file log level.</summary>
    [JsonIgnore]
    public string FileLogLevel { get; set; } = "Information";

    /// <summary>Gets or sets fallback log retention.</summary>
    [JsonIgnore]
    public int FileLogRetentionDays { get; set; } = 7;

    /// <summary>Gets or sets fallback file size limit.</summary>
    [JsonIgnore]
    public int FileLogSizeLimitMb { get; set; } = 50;

}

/// <summary>Fallback representation of a Web logging setting for partial hosts.</summary>
public sealed class LogCategoryLevelSetting
{
    /// <summary>Gets or sets the logger category prefix.</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>Gets or sets the minimum level.</summary>
    public string Level { get; set; } = "Information";
}

/// <summary>Fallback log-level names for partial hosts.</summary>
public static class ApplicationLogLevelNames
{
    /// <summary>Gets supported application logging levels.</summary>
    public static readonly string[] All = ["Trace", "Debug", "Information", "Warning", "Error", "Critical", "None"];
}

/// <summary>
/// Service for managing application settings.
/// </summary>
public interface ILineupSettingsService
{
    /// <summary>
    /// Current application settings.
    /// </summary>
    LineupSettings Settings { get; }

    /// <summary>
    /// XMLTV output path supplied by startup configuration for first-run initialization and reset.
    /// </summary>
    string ConfiguredXmltvOutputPath { get; }

    /// <summary>
    /// Event fired when settings change.
    /// </summary>
    event Action? OnSettingsChanged;

    /// <summary>
    /// Saves the current settings.
    /// </summary>
    Task SaveAsync();

    /// <summary>
    /// Loads settings from storage.
    /// </summary>
    Task LoadAsync();

    /// <summary>
    /// Updates settings and saves.
    /// </summary>
    Task UpdateAsync(Action<LineupSettings> updateAction);
}

/// <summary>
/// File-based implementation of settings service.
/// </summary>
public class LineupSettingsService : ILineupSettingsService, IEpgRetentionPolicy
{
    private readonly ILogger<LineupSettingsService> _logger;
    private readonly IAppDataStore _appDataStore;
    private readonly string _configuredXmltvOutputPath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly LineupStartupState _startupState;
    private readonly IReadOnlyList<IStreamingConfigurationNormalizer> _streamingNormalizers;
    private bool _preserveBackupOnNextSave;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };
    private string SettingsPath => _appDataStore.SettingsPath;
    private string BackupPath => _appDataStore.SettingsBackupPath;

    /// <inheritdoc />
    public LineupSettings Settings { get; private set; } = new();

    /// <inheritdoc />
    public string ConfiguredXmltvOutputPath => _configuredXmltvOutputPath;

    /// <inheritdoc />
    public event Action? OnSettingsChanged;

    /// <inheritdoc />
    public TimeSpan HistoryRetention => TimeSpan.FromHours(Settings.EpgHistoryRetentionHours);

    /// <summary>
    /// Initializes a settings service backed by the specified JSON file.
    /// </summary>
    /// <param name="logger">Logger used for persistence diagnostics.</param>
    /// <param name="appDataStore">Persistent application-data store.</param>
    /// <param name="configuration">Startup configuration containing the XMLTV output seed.</param>
    /// <param name="streamingNormalizers">Plugin-owned streaming configuration normalizers.</param>
    /// <param name="startupState">Persisted-state schema result.</param>
    public LineupSettingsService(
        ILogger<LineupSettingsService> logger,
        IAppDataStore appDataStore,
        IConfiguration configuration,
        IEnumerable<IStreamingConfigurationNormalizer> streamingNormalizers,
        LineupStartupState? startupState = null)
    {
        _logger = logger;
        _appDataStore = appDataStore;
        _configuredXmltvOutputPath = ResolveConfiguredXmltvOutputPath(configuration[AppConstants.XmltvPathConfigKey]);
        _startupState = startupState ?? StartupSchemaGate.Check(appDataStore);
        _streamingNormalizers = streamingNormalizers.ToArray();
        Settings = CreateDefaultSettings();

        if (!_startupState.IsFactoryResetRequired)
        {
            LoadSettingsSync();
        }
    }

    /// <summary>
    /// Initializes a settings service without plugin normalizers.
    /// </summary>
    /// <param name="logger">Logger used for persistence diagnostics.</param>
    /// <param name="appDataStore">Persistent application-data store.</param>
    /// <param name="configuration">Startup configuration containing the XMLTV output seed.</param>
    /// <param name="startupState">Persisted-state schema result.</param>
    public LineupSettingsService(
        ILogger<LineupSettingsService> logger,
        IAppDataStore appDataStore,
        IConfiguration configuration,
        LineupStartupState? startupState = null)
        : this(logger, appDataStore, configuration, [], startupState)
    {
    }

    /// <summary>
    /// Initializes a settings service from a settings file path for direct tests.
    /// </summary>
    public LineupSettingsService(ILogger<LineupSettingsService> logger, string settingsPath, string? configuredXmltvPath = null)
        : this(
            logger,
            new AppDataStore(Path.GetDirectoryName(Path.GetFullPath(settingsPath))!),
            CreateTestConfiguration(configuredXmltvPath),
            [],
            StartupSchemaGate.Check(settingsPath, $"{settingsPath}.bak", Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settingsPath))!, AppConstants.DefaultDatabaseFileName)))
    {
        if (!string.Equals(Path.GetFullPath(settingsPath), _appDataStore.SettingsPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"The settings file must be named {AppConstants.SettingsFileName}.", nameof(settingsPath));
        }
    }

    private static IConfiguration CreateTestConfiguration(string? configuredXmltvPath)
    {
        var values = configuredXmltvPath == null
            ? null
            : new Dictionary<string, string?> { [AppConstants.XmltvPathConfigKey] = configuredXmltvPath };
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private void LoadSettingsSync()
    {
        var primaryExists = _appDataStore.FileExists(SettingsPath);
        if (TryLoadSettingsSync(SettingsPath, out var settings))
        {
            ApplyLoadedSettings(settings);
            _logger.LogInformation("Loaded settings from {Path}", SettingsPath);
            return;
        }

        if (TryLoadSettingsSync(BackupPath, out settings))
        {
            ApplyLoadedSettings(settings);
            _preserveBackupOnNextSave = primaryExists;
            _logger.LogWarning("Recovered settings from backup {Path}", BackupPath);
            return;
        }

        _logger.LogInformation("No valid settings file found, using defaults");
        Settings = CreateDefaultSettings();
    }

    private bool TryLoadSettingsSync(string path, out LineupSettings settings)
    {
        settings = null!;
        if (!_appDataStore.FileExists(path))
        {
            return false;
        }

        try
        {
            settings = DeserializeSettings(_appDataStore.ReadAllText(path));
            PrepareLoadedSettings(settings);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading settings from {Path}", path);
            return false;
        }
    }

    private static LineupSettings DeserializeSettings(string json)
    {
        var schemaState = StartupSchemaGate.CheckSettingsContent(json);
        if (schemaState.IsFactoryResetRequired)
        {
            throw new FactoryResetRequiredException(schemaState.Reason!);
        }

        return JsonSerializer.Deserialize<LineupSettings>(json) ??
            throw new JsonException("The settings document contained a null root value.");
    }

    private void ApplyLoadedSettings(LineupSettings settings)
    {
        Settings = settings;
    }

    private LineupSettings CreateDefaultSettings()
    {
        var settings = new LineupSettings { XmltvOutputPath = _configuredXmltvOutputPath };
        settings.Streaming = InitializeStreaming(settings.Streaming, settings.XmltvOutputPath);
        return settings;
    }

    /// <summary>
    /// Resolves a startup-configured XMLTV file or directory to its canonical output file.
    /// </summary>
    /// <param name="configuredXmltvPath">Configured XMLTV file or directory.</param>
    /// <returns>The absolute XMLTV output file path.</returns>
    public static string ResolveConfiguredXmltvOutputPath(string? configuredXmltvPath)
    {
        if (string.IsNullOrWhiteSpace(configuredXmltvPath))
        {
            return Path.GetFullPath(Path.Combine(AppConstants.DefaultGuideOutputDirectoryPath, AppConstants.DefaultXmltvFileName));
        }

        var path = configuredXmltvPath.Trim();
        var isDirectory = Directory.Exists(path) ||
            Path.EndsInDirectorySeparator(path) ||
            string.IsNullOrEmpty(Path.GetExtension(path));
        var outputPath = isDirectory ? Path.Combine(path, AppConstants.DefaultXmltvFileName) : path;
        return Path.GetFullPath(outputPath);
    }

    private void PrepareLoadedSettings(LineupSettings settings)
    {
        ValidateXmltvOutputPath(settings.XmltvOutputPath);
    }

    private StreamingConfiguration InitializeStreaming(StreamingConfiguration configuration, string defaultXmltvOutputPath)
    {
        var context = new StreamingConfigurationInitializationContext
        {
            DefaultXmltvOutputPath = defaultXmltvOutputPath
        };
        foreach (var normalizer in _streamingNormalizers)
        {
            configuration = normalizer.Normalize(configuration, context);
        }

        return configuration with { SchemaVersion = StreamingConfiguration.CurrentSchemaVersion };
    }

    private static void ValidateXmltvOutputPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("XMLTV output path cannot be empty.");
        }

        try
        {
            _ = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidOperationException("XMLTV output path is invalid.", ex);
        }
    }

    /// <inheritdoc />
    public async Task LoadAsync()
    {
        ThrowIfFactoryResetRequired();
        await _lock.WaitAsync();
        try
        {
            var primaryExists = _appDataStore.FileExists(SettingsPath);
            var settings = await TryLoadSettingsAsync(SettingsPath);
            if (settings != null)
            {
                ApplyLoadedSettings(settings);
                _logger.LogInformation("Loaded settings from {Path}", SettingsPath);
                return;
            }

            settings = await TryLoadSettingsAsync(BackupPath);
            if (settings != null)
            {
                ApplyLoadedSettings(settings);
                _preserveBackupOnNextSave = primaryExists;
                _logger.LogWarning("Recovered settings from backup {Path}", BackupPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading settings");
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<LineupSettings?> TryLoadSettingsAsync(string path)
    {
        if (!_appDataStore.FileExists(path))
        {
            return null;
        }

        try
        {
            var settings = DeserializeSettings(await _appDataStore.ReadAllTextAsync(path));
            PrepareLoadedSettings(settings);
            return settings;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading settings from {Path}", path);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync()
    {
        ThrowIfFactoryResetRequired();
        await _lock.WaitAsync();
        try
        {
            await PersistSettingsAsync(CloneSettings(Settings));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving settings");
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task PersistSettingsAsync(LineupSettings settings)
    {
        ValidateXmltvOutputPath(settings.XmltvOutputPath);
        await WriteSettingsAtomicallyAsync(settings);
        Settings = settings;
        _preserveBackupOnNextSave = false;
        _logger.LogInformation("Saved settings to {Path}", SettingsPath);
        OnSettingsChanged?.Invoke();
    }

    private async Task WriteSettingsAtomicallyAsync(LineupSettings settings)
    {
        await _appDataStore.WriteAtomicallyAsync(
            SettingsPath,
            (stream, cancellationToken) => JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken),
            BackupPath,
            _preserveBackupOnNextSave);
    }

    /// <inheritdoc />
    public async Task UpdateAsync(Action<LineupSettings> updateAction)
    {
        ThrowIfFactoryResetRequired();
        await _lock.WaitAsync();
        try
        {
            var updatedSettings = CloneSettings(Settings);
            updateAction(updatedSettings);
            await PersistSettingsAsync(updatedSettings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating settings");
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static LineupSettings CloneSettings(LineupSettings settings)
    {
        return DeserializeSettings(JsonSerializer.Serialize(settings, JsonOptions));
    }

    private void ThrowIfFactoryResetRequired()
    {
        if (_startupState.IsFactoryResetRequired)
        {
            throw new FactoryResetRequiredException(_startupState.Reason!);
        }
    }
}
