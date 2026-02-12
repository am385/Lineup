using System.Text.Json;
using Lineup.Core;

namespace Lineup.Web.Services;

/// <summary>
/// Application settings that can be modified at runtime.
/// </summary>
public class AppSettings
{
    private int _targetDays = 3;
    private int _autoFetchIntervalMinutes = 120;
    private int _deviceRefreshIntervalMinutes = 10;
    private int _tunerRefreshIntervalSeconds = 30;

    /// <summary>
    /// Default number of days of EPG data to fetch/generate.
    /// </summary>
    public int TargetDays
    {
        get => _targetDays;
        set => _targetDays = Math.Max(1, value); // Minimum 1 day
    }

    /// <summary>
    /// Interval in minutes between auto-fetch operations.
    /// Set to 0 to disable auto-fetch.
    /// Negative values are normalized to 0.
    /// </summary>
    public int AutoFetchIntervalMinutes
    {
        get => _autoFetchIntervalMinutes;
        set => _autoFetchIntervalMinutes = Math.Max(0, value);
    }

    /// <summary>
    /// Whether auto-fetch is enabled (interval > 0).
    /// </summary>
    public bool IsAutoFetchEnabled => AutoFetchIntervalMinutes > 0;

    /// <summary>
    /// HDHomeRun device address.
    /// </summary>
    public string DeviceAddress { get; set; } = AppConstants.DefaultDeviceAddress;

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
    public string XmltvOutputPath { get; set; } = AppConstants.DefaultXmltvFileName;

    /// <summary>
    /// Whether to automatically generate XMLTV file after auto-fetch completes.
    /// </summary>
    public bool AutoGenerateXmltv { get; set; } = true;

    /// <summary>
    /// UI theme preference: "light", "dark", or "auto" (follows system preference).
    /// </summary>
    public string Theme { get; set; } = "auto";

    /// <summary>
    /// UTC timestamp of the last successful auto-fetch.
    /// Persisted so the service can calculate remaining time on restart instead of fetching immediately.
    /// </summary>
    public DateTime? LastAutoFetchTime { get; set; }
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
    private readonly string _settingsPath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public AppSettings Settings { get; private set; } = new();
    public event Action? OnSettingsChanged;

    public AppSettingsService(ILogger<AppSettingsService> logger, string settingsPath)
    {
        _logger = logger;
        _settingsPath = settingsPath;

        // Load settings synchronously on startup
        LoadSettingsSync();
    }

    private void LoadSettingsSync()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                Settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                _logger.LogInformation("Loaded settings from {Path}", _settingsPath);
            }
            else
            {
                _logger.LogInformation("No settings file found, using defaults");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading settings, using defaults");
            Settings = new AppSettings();
        }
    }

    public async Task LoadAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = await File.ReadAllTextAsync(_settingsPath);
                Settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                _logger.LogInformation("Loaded settings from {Path}", _settingsPath);
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

    public async Task SaveAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var json = JsonSerializer.Serialize(Settings, JsonOptions);
            await File.WriteAllTextAsync(_settingsPath, json);
            _logger.LogInformation("Saved settings to {Path}", _settingsPath);
            OnSettingsChanged?.Invoke();
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

    public async Task UpdateAsync(Action<AppSettings> updateAction)
    {
        updateAction(Settings);
        await SaveAsync();
    }
}
