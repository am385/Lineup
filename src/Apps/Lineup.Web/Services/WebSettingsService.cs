using System.Text.Json;
using System.Runtime.CompilerServices;

namespace Lineup.Web.Services;

/// <summary>Defines a minimum Web-host log level for a category prefix.</summary>
public sealed class WebLogCategoryLevelSetting
{
    private string _category = string.Empty;
    private string _level = "Information";

    /// <summary>Gets or sets the logger category prefix.</summary>
    public string Category
    {
        get => _category;
        set => _category = value?.Trim() ?? string.Empty;
    }

    /// <summary>Gets or sets the minimum level.</summary>
    public string Level
    {
        get => _level;
        set => _level = WebApplicationLogLevelNames.Normalize(value);
    }
}

/// <summary>Contains settings owned exclusively by the Web frontend and host.</summary>
public sealed class WebSettings
{
    /// <summary>Gets the only Web-settings schema accepted by this release.</summary>
    public const int CurrentSchemaVersion = 1;

    private int _activeStreamRefreshIntervalSeconds = 5;
    private int _fileLogRetentionDays = 7;
    private int _fileLogSizeLimitMb = 50;
    private string _fileLogLevel = "Information";
    private string _applicationLogLevel = "Information";
    private List<WebLogCategoryLevelSetting> _logCategoryOverrides = [];

    /// <summary>Gets the Web-settings schema version.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Gets or sets the browser theme.</summary>
    public string Theme { get; set; } = "auto";

    /// <summary>Gets or sets the Dashboard refresh interval.</summary>
    public int ActiveStreamRefreshIntervalSeconds
    {
        get => _activeStreamRefreshIntervalSeconds;
        set => _activeStreamRefreshIntervalSeconds = Math.Clamp(value, 0, 300);
    }

    /// <summary>Gets whether automatic Dashboard refresh is enabled.</summary>
    public bool IsActiveStreamRefreshEnabled => ActiveStreamRefreshIntervalSeconds > 0;

    /// <summary>Gets or sets whether active-stream client addresses are redacted.</summary>
    public bool RedactApiClientAddresses { get; set; } = true;

    /// <summary>Gets or sets whether device addresses are redacted.</summary>
    public bool RedactApiDeviceAddresses { get; set; }

    /// <summary>Gets or sets whether URLs are redacted.</summary>
    public bool RedactApiUrls { get; set; }

    /// <summary>Gets or sets whether the Web initial setup flow is complete.</summary>
    public bool IsSetupComplete { get; set; }

    /// <summary>Gets or sets whether the Web host emits network discovery advertisements.</summary>
    public bool EnableNetworkDiscovery { get; set; }

    /// <summary>Gets or sets whether rolling file logging is enabled.</summary>
    public bool EnableFileLogging { get; set; }

    /// <summary>Gets or sets whether runtime filters replace startup logging filters.</summary>
    public bool OverrideLoggingDefaults { get; set; }

    /// <summary>Gets or sets the default runtime application log level.</summary>
    public string ApplicationLogLevel
    {
        get => _applicationLogLevel;
        set => _applicationLogLevel = WebApplicationLogLevelNames.Normalize(value);
    }

    /// <summary>Gets or sets runtime category-prefix log levels.</summary>
    public List<WebLogCategoryLevelSetting> LogCategoryOverrides
    {
        get => _logCategoryOverrides;
        set => _logCategoryOverrides = value ?? [];
    }

    /// <summary>Gets or sets the rolling file log level.</summary>
    public string FileLogLevel
    {
        get => _fileLogLevel;
        set => _fileLogLevel = value?.ToLowerInvariant() switch
        {
            "debug" => "Debug",
            "information" => "Information",
            "warning" => "Warning",
            "error" => "Error",
            "fatal" => "Fatal",
            _ => "Information"
        };
    }

    /// <summary>Gets or sets the rolling log retention period.</summary>
    public int FileLogRetentionDays
    {
        get => _fileLogRetentionDays;
        set => _fileLogRetentionDays = Math.Clamp(value, 1, 90);
    }

    /// <summary>Gets or sets the maximum rolling log file size in megabytes.</summary>
    public int FileLogSizeLimitMb
    {
        get => _fileLogSizeLimitMb;
        set => _fileLogSizeLimitMb = Math.Clamp(value, 1, 500);
    }
}

/// <summary>Normalizes Web-host logging level names.</summary>
public static class WebApplicationLogLevelNames
{
    /// <summary>Gets supported application logging levels.</summary>
    public static readonly string[] All = ["Trace", "Debug", "Information", "Warning", "Error", "Critical", "None"];

    /// <summary>Normalizes a configured logging level.</summary>
    public static string Normalize(string? value) => value?.ToLowerInvariant() switch
    {
        "trace" or "verbose" => "Trace",
        "debug" => "Debug",
        "information" => "Information",
        "warning" => "Warning",
        "error" => "Error",
        "critical" or "fatal" => "Critical",
        "none" => "None",
        _ => "Information"
    };
}

/// <summary>Manages Web-owned settings independently from shared Lineup settings.</summary>
public interface IWebSettingsService
{
    /// <summary>Gets current Web settings.</summary>
    WebSettings Settings { get; }

    /// <summary>Occurs after settings are persisted.</summary>
    event Action? OnSettingsChanged;

    /// <summary>Updates and persists Web settings.</summary>
    Task UpdateAsync(Action<WebSettings> updateAction);
}

/// <summary>Persists Web-owned settings beneath the Web app-data root.</summary>
public sealed class WebSettingsService : IWebSettingsService
{
    private const string FileName = "web-settings.json";
    private readonly IAppDataStore _appDataStore;
    private readonly ILogger<WebSettingsService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _path;
    private readonly string _backupPath;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Initializes a Web settings service.</summary>
    public WebSettingsService(IAppDataStore appDataStore, ILogger<WebSettingsService> logger)
    {
        _appDataStore = appDataStore;
        _logger = logger;
        _path = appDataStore.GetPath(FileName);
        _backupPath = $"{_path}.bak";
        Settings = Load();
    }

    /// <inheritdoc />
    public WebSettings Settings { get; private set; }

    /// <inheritdoc />
    public event Action? OnSettingsChanged;

    /// <inheritdoc />
    public async Task UpdateAsync(Action<WebSettings> updateAction)
    {
        ArgumentNullException.ThrowIfNull(updateAction);
        await _lock.WaitAsync();
        try
        {
            updateAction(Settings);
            await _appDataStore.WriteAtomicallyAsync(
                _path,
                (stream, cancellationToken) => JsonSerializer.SerializeAsync(stream, Settings, JsonOptions, cancellationToken),
                _backupPath);
        }
        finally
        {
            _lock.Release();
        }
        OnSettingsChanged?.Invoke();
    }

    private WebSettings Load()
    {
        foreach (var path in new[] { _path, _backupPath })
        {
            try
            {
                if (!_appDataStore.FileExists(path))
                {
                    continue;
                }
                var settings = JsonSerializer.Deserialize<WebSettings>(_appDataStore.ReadAllText(path));
                if (settings is { SchemaVersion: WebSettings.CurrentSchemaVersion })
                {
                    return settings;
                }
            }

            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                _logger.LogError(ex, "Unable to load Web settings from {Path}", path);
            }
        }

        return new WebSettings();
    }

}

/// <summary>Provides a non-persisting Web-settings adapter for partial hosts.</summary>
internal static class WebSettingsServiceFallback
{
    private static readonly ConditionalWeakTable<ILineupSettingsService, FallbackWebSettingsService> Services = new();

    /// <summary>Gets the non-persisting fallback service associated with shared settings.</summary>
    public static IWebSettingsService Get(ILineupSettingsService settings) =>
        Services.GetValue(settings, static value => new FallbackWebSettingsService(value));

    private sealed class FallbackWebSettingsService : IWebSettingsService
    {
        private readonly ILineupSettingsService _lineupSettings;

        public FallbackWebSettingsService(ILineupSettingsService lineupSettings)
        {
            _lineupSettings = lineupSettings;
            _lineupSettings.OnSettingsChanged += () => OnSettingsChanged?.Invoke();
        }

        public WebSettings Settings => FromLineup(_lineupSettings.Settings);

        public event Action? OnSettingsChanged;

        public Task UpdateAsync(Action<WebSettings> updateAction)
        {
            var settings = Settings;
            updateAction(settings);
            Apply(_lineupSettings.Settings, settings);
            OnSettingsChanged?.Invoke();
            return Task.CompletedTask;
        }

        private static WebSettings FromLineup(LineupSettings settings) => new()
        {
            Theme = settings.Theme,
            ActiveStreamRefreshIntervalSeconds = settings.ActiveStreamRefreshIntervalSeconds,
            RedactApiClientAddresses = settings.RedactApiClientAddresses,
            RedactApiDeviceAddresses = settings.RedactApiDeviceAddresses,
            RedactApiUrls = settings.RedactApiUrls,
            IsSetupComplete = settings.IsSetupComplete,
            EnableNetworkDiscovery = settings.EnableNetworkDiscovery,
            EnableFileLogging = settings.EnableFileLogging,
            OverrideLoggingDefaults = settings.OverrideLoggingDefaults,
            ApplicationLogLevel = settings.ApplicationLogLevel,
            LogCategoryOverrides = settings.LogCategoryOverrides.Select(level => new WebLogCategoryLevelSetting { Category = level.Category, Level = level.Level }).ToList(),
            FileLogLevel = settings.FileLogLevel,
            FileLogRetentionDays = settings.FileLogRetentionDays,
            FileLogSizeLimitMb = settings.FileLogSizeLimitMb
        };

        private static void Apply(LineupSettings target, WebSettings source)
        {
            target.Theme = source.Theme;
            target.ActiveStreamRefreshIntervalSeconds = source.ActiveStreamRefreshIntervalSeconds;
            target.RedactApiClientAddresses = source.RedactApiClientAddresses;
            target.RedactApiDeviceAddresses = source.RedactApiDeviceAddresses;
            target.RedactApiUrls = source.RedactApiUrls;
            target.IsSetupComplete = source.IsSetupComplete;
            target.EnableNetworkDiscovery = source.EnableNetworkDiscovery;
            target.EnableFileLogging = source.EnableFileLogging;
            target.OverrideLoggingDefaults = source.OverrideLoggingDefaults;
            target.ApplicationLogLevel = source.ApplicationLogLevel;
            target.LogCategoryOverrides = source.LogCategoryOverrides.Select(level => new LogCategoryLevelSetting { Category = level.Category, Level = level.Level }).ToList();
            target.FileLogLevel = source.FileLogLevel;
            target.FileLogRetentionDays = source.FileLogRetentionDays;
            target.FileLogSizeLimitMb = source.FileLogSizeLimitMb;
        }
    }
}
