using System.Text.Json;
using System.Text.Json.Serialization;
using Lineup.Core;

namespace Lineup.Web.Services;

/// <summary>
/// Controls how audio tracks are handled by the transparent MPEG-TS proxy.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AudioTranscodeMode
{
    /// <summary>
    /// Copies non-AC-4 audio tracks without re-encoding them.
    /// </summary>
    Copy,

    /// <summary>
    /// Encodes non-AC-4 audio tracks as AC-3.
    /// </summary>
    Ac3,

    /// <summary>
    /// Encodes non-AC-4 audio tracks as E-AC-3.
    /// </summary>
    Eac3
}

/// <summary>
/// Controls how AC-4 tracks are handled by the virtual tuner.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Ac4TranscodeTarget
{
    /// <summary>
    /// Preserves AC-4 audio without re-encoding it.
    /// </summary>
    Preserve,

    /// <summary>
    /// Encodes AC-4 audio tracks as AC-3.
    /// </summary>
    Ac3,

    /// <summary>
    /// Encodes AC-4 audio tracks as E-AC-3.
    /// </summary>
    Eac3
}

/// <summary>
/// Controls video conversion performed by the virtual HDHomeRun tuner.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VirtualTunerVideoMode
{
    /// <summary>
    /// Preserves the physical tuner's source video codec.
    /// </summary>
    Preserve,

    /// <summary>
    /// Converts HEVC video to H.264 for clients that cannot process HEVC Live TV.
    /// </summary>
    ConvertHevcToH264
}

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
/// Controls the CPU-versus-compression tradeoff for browser-compatible H.264 output.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WebVideoPreset
{
    /// <summary>
    /// Prioritizes lower CPU usage while substantially improving on ultrafast compression.
    /// </summary>
    VeryFast,

    /// <summary>
    /// Uses additional CPU for improved compression.
    /// </summary>
    Faster,

    /// <summary>
    /// Uses more CPU for higher compression efficiency.
    /// </summary>
    Fast,

    /// <summary>
    /// Prioritizes compression efficiency and may not sustain real-time encoding on all systems.
    /// </summary>
    Medium
}

/// <summary>
/// Defines a minimum log level for a category prefix.
/// </summary>
public sealed class LogCategoryLevelSetting
{
    private string _category = "";
    private string _level = "Information";

    /// <summary>
    /// Logger category prefix.
    /// </summary>
    public string Category
    {
        get => _category;
        set => _category = value?.Trim() ?? "";
    }

    /// <summary>
    /// Minimum level for the category and its dotted descendants.
    /// </summary>
    public string Level
    {
        get => _level;
        set => _level = ApplicationLogLevelNames.Normalize(value);
    }
}

/// <summary>
/// Application settings that can be modified at runtime.
/// </summary>
public class AppSettings
{
    private bool? _enableHdHomeRunProxy;
    private string? _xmltvOutputPath;
    private int _targetDays = 2;
    private int _deviceRefreshIntervalMinutes = 10;
    private int _tunerRefreshIntervalSeconds = 30;
    private int _activeStreamRefreshIntervalSeconds = 5;
    private int _webVideoQuality = 21;
    private int _maximumVideoBitRateMbps = 10;
    private int _maximumConcurrentStreams;
    private int _fileLogRetentionDays = 7;
    private int _fileLogSizeLimitMb = 50;
    private string _fileLogLevel = "Information";
    private string _applicationLogLevel = "Information";
    private List<LogCategoryLevelSetting> _logCategoryOverrides = [];

    /// <summary>
    /// Default number of days of EPG data to fetch/generate.
    /// </summary>
    public int TargetDays
    {
        get => _targetDays;
        set => _targetDays = Math.Max(1, value); // Minimum 1 day
    }

    /// <summary>
    /// Interval between auto-fetch operations.
    /// A positive value enables auto-fetch; SiliconDust requires the actual schedule to be randomized.
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
    /// Persisted next XMLTV refresh time selected from SiliconDust's required randomized window.
    /// </summary>
    public DateTime? NextAutoFetchTime { get; set; }

    /// <summary>
    /// HDHomeRun device address.
    /// </summary>
    public string DeviceAddress { get; set; } = AppConstants.DefaultDeviceAddress;

    /// <summary>
    /// Whether SiliconDust and SSDP network discovery advertisements are enabled.
    /// Disabled by default to avoid conflicts with other listeners on the host.
    /// </summary>
    public bool EnableNetworkDiscovery { get; set; }

    /// <summary>
    /// Whether all virtual HDHomeRun proxy endpoints, streams, and discovery behavior are enabled.
    /// Legacy settings inherit the primary profile's enabled state.
    /// </summary>
    public bool EnableHdHomeRunProxy
    {
        get => _enableHdHomeRunProxy ?? false;
        set => _enableHdHomeRunProxy = value;
    }

    /// <summary>
    /// Explicit externally reachable HTTP or HTTPS base URL advertised during network discovery.
    /// An empty value disables advertisements even when discovery is enabled.
    /// </summary>
    public string AdvertisedBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Virtual HDHomeRun proxy profiles. The first enabled profile is the primary profile exposed at legacy root routes.
    /// Empty legacy settings are migrated from <see cref="DeviceAddress"/>.
    /// </summary>
    public List<HdHomeRunProxyProfileSettings> HdHomeRunProxyProfiles { get; set; } = [];

    /// <summary>
    /// Ensures settings created before proxy profiles receive a primary profile based on the legacy device address.
    /// </summary>
    public void EnsureHdHomeRunProxyProfiles()
    {
        _enableHdHomeRunProxy ??= HdHomeRunProxyProfiles.Count > 0 && HdHomeRunProxyProfiles[0].Enabled;

        if (HdHomeRunProxyProfiles.Count == 0)
        {
            HdHomeRunProxyProfiles.Add(new HdHomeRunProxyProfileSettings
            {
                PhysicalAddress = DeviceAddress,
                AdvertisedBaseUrl = AdvertisedBaseUrl,
                UsesLegacyDeviceAddress = true
            });
        }

        HdHomeRunProxyProfiles[0].Enabled = true;

        var usedDeviceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < HdHomeRunProxyProfiles.Count; index++)
        {
            var profile = HdHomeRunProxyProfiles[index];
            if (profile.UsesLegacyDeviceAddress)
            {
                profile.PhysicalAddress = DeviceAddress;
            }

            if (!HdHomeRunProxyIdentity.IsValidDeviceId(profile.VirtualDeviceId) ||
                !usedDeviceIds.Add(profile.VirtualDeviceId))
            {
                profile.VirtualDeviceId = HdHomeRunProxyIdentity.CreateDeviceId($"{profile.PhysicalAddress.Trim()}#{index}");
                usedDeviceIds.Add(profile.VirtualDeviceId);
            }
        }
    }

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
    /// Interval in seconds between active stream Dashboard refreshes.
    /// Set to 0 to disable automatic refresh.
    /// </summary>
    public int ActiveStreamRefreshIntervalSeconds
    {
        get => _activeStreamRefreshIntervalSeconds;
        set => _activeStreamRefreshIntervalSeconds = Math.Clamp(value, 0, 300);
    }

    /// <summary>
    /// Whether automatic active stream refresh is enabled.
    /// </summary>
    public bool IsActiveStreamRefreshEnabled => ActiveStreamRefreshIntervalSeconds > 0;

    /// <summary>
    /// Output path for the generated XMLTV file.
    /// Can be a filename (relative to working directory) or absolute path.
    /// </summary>
    public string XmltvOutputPath
    {
        get => _xmltvOutputPath ?? Path.Combine(AppConstants.DefaultXmltvFilePath, AppConstants.DefaultXmltvFileName);
        set => _xmltvOutputPath = value;
    }

    /// <summary>
    /// Whether an XMLTV output path was explicitly supplied by persisted or runtime settings.
    /// </summary>
    internal bool HasExplicitXmltvOutputPath => _xmltvOutputPath != null;

    /// <summary>
    /// Whether to automatically generate XMLTV file after auto-fetch completes.
    /// </summary>
    public bool AutoGenerateXmltv { get; set; } = true;

    /// <summary>
    /// UI theme preference: "light", "dark", or "auto" (follows system preference).
    /// </summary>
    public string Theme { get; set; } = "auto";

    /// <summary>
    /// IANA timezone ID for display (e.g., "America/New_York").
    /// Empty/null means use the TZ environment variable or system default.
    /// </summary>
    public string TimeZoneId { get; set; } = "";

    /// <summary>
    /// Audio behavior for non-AC-4 tracks in the transparent MPEG-TS proxy.
    /// </summary>
    public AudioTranscodeMode AudioTranscodeMode { get; set; } = AudioTranscodeMode.Copy;

    /// <summary>
    /// Compatibility codec used for AC-4 tracks in the transparent MPEG-TS proxy.
    /// </summary>
    public Ac4TranscodeTarget Ac4TranscodeTarget { get; set; } = Ac4TranscodeTarget.Ac3;

    /// <summary>
    /// Video conversion applied to virtual HDHomeRun tuner streams.
    /// </summary>
    public VirtualTunerVideoMode VirtualTunerVideoMode { get; set; } = VirtualTunerVideoMode.Preserve;

    /// <summary>
    /// API behavior when the HDHomeRun reports content protection code 811.
    /// </summary>
    public ProtectedContentMode ProtectedContentMode { get; set; } = ProtectedContentMode.ReturnError;

    /// <summary>
    /// Stream behavior when a client requests a disabled channel directly.
    /// </summary>
    public DisabledChannelMode DisabledChannelMode { get; set; } = DisabledChannelMode.ReturnError;

    /// <summary>
    /// H.264 encoder preset used for browser-compatible fMP4 and HLS output.
    /// </summary>
    public WebVideoPreset WebVideoPreset { get; set; } = WebVideoPreset.VeryFast;

    /// <summary>
    /// Constant-rate-factor quality used for browser video, where lower values provide higher quality.
    /// </summary>
    public int WebVideoQuality
    {
        get => _webVideoQuality;
        set => _webVideoQuality = Math.Clamp(value, 16, 30);
    }

    /// <summary>
    /// Maximum transcoded video bitrate in megabits per second.
    /// </summary>
    public int MaximumVideoBitRateMbps
    {
        get => _maximumVideoBitRateMbps;
        set => _maximumVideoBitRateMbps = Math.Clamp(value, 2, 30);
    }

    /// <summary>
    /// Maximum number of concurrent hosted streams, or zero for unlimited.
    /// </summary>
    public int MaximumConcurrentStreams
    {
        get => _maximumConcurrentStreams;
        set => _maximumConcurrentStreams = Math.Clamp(value, 0, 100);
    }

    /// <summary>
    /// Whether active-stream client network addresses are redacted from the status API.
    /// </summary>
    public bool RedactApiClientAddresses { get; set; } = true;

    /// <summary>
    /// Whether configured and discovered device addresses are redacted from the status API.
    /// </summary>
    public bool RedactApiDeviceAddresses { get; set; }

    /// <summary>
    /// Whether device, tuner, virtual-device, and XMLTV URLs are redacted from the status API.
    /// </summary>
    public bool RedactApiUrls { get; set; }

    /// <summary>
    /// UTC timestamp of the last successful auto-fetch.
    /// Persisted so the service can calculate remaining time on restart instead of fetching immediately.
    /// </summary>
    public DateTime? LastAutoFetchTime { get; set; }

    /// <summary>
    /// Whether initial setup has been completed.
    /// False on first launch; set to true when settings are saved for the first time.
    /// </summary>
    public bool IsSetupComplete { get; set; }

    /// <summary>
    /// Whether rolling file logging is enabled.
    /// </summary>
    public bool EnableFileLogging { get; set; }

    /// <summary>
    /// Whether persisted application-level filters replace startup logging filters.
    /// </summary>
    public bool OverrideLoggingDefaults { get; set; }

    /// <summary>
    /// Default application log level used while logging overrides are enabled.
    /// </summary>
    public string ApplicationLogLevel
    {
        get => _applicationLogLevel;
        set => _applicationLogLevel = ApplicationLogLevelNames.Normalize(value);
    }

    /// <summary>
    /// Category-prefix levels used while logging overrides are enabled.
    /// </summary>
    public List<LogCategoryLevelSetting> LogCategoryOverrides
    {
        get => _logCategoryOverrides;
        set => _logCategoryOverrides = value ?? [];
    }

    /// <summary>
    /// Minimum severity written to rolling log files.
    /// </summary>
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

    /// <summary>
    /// Number of daily rolling log files retained.
    /// </summary>
    public int FileLogRetentionDays
    {
        get => _fileLogRetentionDays;
        set => _fileLogRetentionDays = Math.Clamp(value, 1, 90);
    }

    /// <summary>
    /// Maximum size of each rolling log file in megabytes.
    /// </summary>
    public int FileLogSizeLimitMb
    {
        get => _fileLogSizeLimitMb;
        set => _fileLogSizeLimitMb = Math.Clamp(value, 1, 500);
    }
}

/// <summary>
/// Normalizes supported application logging level names.
/// </summary>
internal static class ApplicationLogLevelNames
{
    /// <summary>
    /// Supported application logging levels.
    /// </summary>
    internal static readonly string[] All = ["Trace", "Debug", "Information", "Warning", "Error", "Critical", "None"];

    /// <summary>
    /// Normalizes a configured logging level to a supported name.
    /// </summary>
    internal static string Normalize(string? value) => value?.ToLowerInvariant() switch
    {
        "trace" => "Trace",
        "verbose" => "Trace",
        "debug" => "Debug",
        "information" => "Information",
        "warning" => "Warning",
        "error" => "Error",
        "critical" => "Critical",
        "fatal" => "Critical",
        "none" => "None",
        _ => "Information"
    };
}

/// <summary>
/// Service for managing application settings.
/// </summary>
public interface IAppSettingsService
{
    /// <summary>
    /// Current application settings.
    /// </summary>
    AppSettings Settings { get; }

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
    Task UpdateAsync(Action<AppSettings> updateAction);
}

/// <summary>
/// File-based implementation of settings service.
/// </summary>
public class AppSettingsService : IAppSettingsService
{
    private readonly ILogger<AppSettingsService> _logger;
    private readonly AppDataStore _appDataStore;
    private readonly string _configuredXmltvOutputPath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _preserveBackupOnNextSave;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };
    private string SettingsPath => _appDataStore.SettingsPath;
    private string BackupPath => _appDataStore.SettingsBackupPath;

    /// <inheritdoc />
    public AppSettings Settings { get; private set; } = new();

    /// <inheritdoc />
    public string ConfiguredXmltvOutputPath => _configuredXmltvOutputPath;

    /// <inheritdoc />
    public event Action? OnSettingsChanged;

    /// <summary>
    /// Initializes a settings service backed by the specified JSON file.
    /// </summary>
    /// <param name="logger">Logger used for persistence diagnostics.</param>
    /// <param name="appDataStore">Persistent application-data store.</param>
    /// <param name="configuration">Startup configuration containing the XMLTV output seed.</param>
    public AppSettingsService(ILogger<AppSettingsService> logger, AppDataStore appDataStore, IConfiguration configuration)
    {
        _logger = logger;
        _appDataStore = appDataStore;
        _configuredXmltvOutputPath = ResolveConfiguredXmltvOutputPath(configuration[AppConstants.XmltvPathConfigKey]);
        Settings = CreateDefaultSettings();

        // Load settings synchronously on startup
        LoadSettingsSync();
        Settings.EnsureHdHomeRunProxyProfiles();
    }

    /// <summary>
    /// Initializes a settings service from a settings file path for direct tests.
    /// </summary>
    internal AppSettingsService(ILogger<AppSettingsService> logger, string settingsPath, string? configuredXmltvPath = null)
        : this(logger, new AppDataStore(Path.GetDirectoryName(Path.GetFullPath(settingsPath))!), CreateTestConfiguration(configuredXmltvPath))
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

    private bool TryLoadSettingsSync(string path, out AppSettings settings)
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

    private static AppSettings DeserializeSettings(string json)
    {
        return JsonSerializer.Deserialize<AppSettings>(json) ??
            throw new JsonException("The settings document contained a null root value.");
    }

    private void ApplyLoadedSettings(AppSettings settings)
    {
        Settings = settings;
        Settings.EnsureHdHomeRunProxyProfiles();
    }

    private AppSettings CreateDefaultSettings()
    {
        return new AppSettings { XmltvOutputPath = _configuredXmltvOutputPath };
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
            return Path.GetFullPath(Path.Combine(AppConstants.DefaultXmltvFilePath, AppConstants.DefaultXmltvFileName));
        }

        var path = configuredXmltvPath.Trim();
        var isDirectory = Directory.Exists(path) ||
            Path.EndsInDirectorySeparator(path) ||
            string.IsNullOrEmpty(Path.GetExtension(path));
        var outputPath = isDirectory ? Path.Combine(path, AppConstants.DefaultXmltvFileName) : path;
        return Path.GetFullPath(outputPath);
    }

    private void PrepareLoadedSettings(AppSettings settings)
    {
        if (!settings.HasExplicitXmltvOutputPath || string.IsNullOrWhiteSpace(settings.XmltvOutputPath))
        {
            settings.XmltvOutputPath = _configuredXmltvOutputPath;
        }

        ValidateXmltvOutputPath(settings.XmltvOutputPath);
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

    private async Task<AppSettings?> TryLoadSettingsAsync(string path)
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

    private async Task PersistSettingsAsync(AppSettings settings)
    {
        ValidateXmltvOutputPath(settings.XmltvOutputPath);
        await WriteSettingsAtomicallyAsync(settings);
        Settings = settings;
        _preserveBackupOnNextSave = false;
        _logger.LogInformation("Saved settings to {Path}", SettingsPath);
        OnSettingsChanged?.Invoke();
    }

    private async Task WriteSettingsAtomicallyAsync(AppSettings settings)
    {
        await _appDataStore.WriteAtomicallyAsync(
            SettingsPath,
            (stream, cancellationToken) => JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken),
            BackupPath,
            _preserveBackupOnNextSave);
    }

    /// <inheritdoc />
    public async Task UpdateAsync(Action<AppSettings> updateAction)
    {
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

    private static AppSettings CloneSettings(AppSettings settings)
    {
        return DeserializeSettings(JsonSerializer.Serialize(settings, JsonOptions));
    }
}
