using Lineup.Core;
using Lineup.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Lineup.Web.Components.Pages;

/// <summary>
/// Displays and edits persisted Lineup application settings.
/// </summary>
public partial class Settings : IDisposable
{
    [Inject]
    private IAppSettingsService SettingsService { get; set; } = default!;

    [Inject]
    private NavigationManager Navigation { get; set; } = default!;

    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    [Inject]
    private ITimeZoneService TimeZoneService { get; set; } = default!;

    [Inject]
    private IServiceProvider Services { get; set; } = default!;

    private bool _autoFetchEnabled;
    private bool _refreshChannelsBeforeGuideFetch;
    private string _deviceAddress = "";
    private int _deviceRefreshIntervalMinutes;
    private int _tunerRefreshIntervalSeconds;
    private int _activeStreamRefreshIntervalSeconds;
    private bool _autoGenerateXmltv;
    private string _xmltvOutputPath = "";
    private string _theme = "auto";
    private string _timeZoneId = "";
    private AudioTranscodeMode _audioTranscodeMode;
    private Ac4TranscodeTarget _ac4TranscodeTarget;
    private VirtualTunerVideoMode _virtualTunerVideoMode;
    private ProtectedContentMode _protectedContentMode;
    private DisabledChannelMode _disabledChannelMode;
    private WebVideoPreset _webVideoPreset;
    private int _webVideoQuality;
    private int _maximumVideoBitRateMbps;
    private int _maximumConcurrentStreams;
    private bool _redactApiClientAddresses;
    private bool _redactApiDeviceAddresses;
    private bool _redactApiUrls;
    private bool _overrideLoggingDefaults;
    private string _applicationLogLevel = "Information";
    private List<LogCategoryLevelSetting> _logCategoryOverrides = [];
    private HashSet<string> _startupLogCategories = new(StringComparer.OrdinalIgnoreCase);
    private bool _enableFileLogging;
    private string _fileLogLevel = "Information";
    private int _fileLogRetentionDays;
    private int _fileLogSizeLimitMb;
    private IReadOnlyList<LogFileInfo> _retainedLogFiles = [];
    private bool _enableHdHomeRunProxy;
    private bool _enableNetworkDiscovery;
    private List<HdHomeRunProxyProfileSettings> _proxyProfiles = [];
    private bool _isSaving;
    private string _statusMessage = "";
    private bool _isError;
    private bool _isInitialSetup;
    private SettingsTab _activeTab = SettingsTab.General;
    private bool _showDirectoryBrowser;
    private string _currentDirectory = "";
    private string[] _directories = [];
    private string? _directoryError;
    private string _selectedFilename = AppConstants.DefaultXmltvFileName;
    private bool _showFactoryResetConfirmation;
    private string _factoryResetConfirmation = "";
    private bool _isRestarting;
    private bool _isFactoryResetting;
    private bool _isDeletingLogs;

    /// <inheritdoc />
    protected override void OnInitialized()
    {
        _isInitialSetup = !SettingsService.Settings.IsSetupComplete;
        _activeTab = ParseTab(new Uri(Navigation.Uri).Fragment);
        LoadCurrentSettings();
        SettingsService.OnSettingsChanged += OnSettingsChanged;
    }

    private void OnSettingsChanged()
    {
        InvokeAsync(() =>
        {
            // Only update the theme field from external changes (header toggle).
            // Other fields are only loaded on init to avoid overwriting unsaved edits.
            _theme = SettingsService.Settings.Theme;
            StateHasChanged();
        });
    }

    /// <inheritdoc />
    public void Dispose()
    {
        SettingsService.OnSettingsChanged -= OnSettingsChanged;
    }

    private void LoadCurrentSettings()
    {
        _autoFetchEnabled = SettingsService.Settings.IsAutoFetchEnabled;
        _refreshChannelsBeforeGuideFetch = SettingsService.Settings.RefreshChannelsBeforeGuideFetch;
        _deviceAddress = SettingsService.Settings.DeviceAddress;
        _deviceRefreshIntervalMinutes = SettingsService.Settings.DeviceRefreshIntervalMinutes;
        _tunerRefreshIntervalSeconds = SettingsService.Settings.TunerRefreshIntervalSeconds;
        _activeStreamRefreshIntervalSeconds = SettingsService.Settings.ActiveStreamRefreshIntervalSeconds;
        _autoGenerateXmltv = SettingsService.Settings.AutoGenerateXmltv;
        _xmltvOutputPath = SettingsService.Settings.XmltvOutputPath;
        _theme = SettingsService.Settings.Theme;
        _timeZoneId = SettingsService.Settings.TimeZoneId;
        _audioTranscodeMode = SettingsService.Settings.AudioTranscodeMode;
        _ac4TranscodeTarget = SettingsService.Settings.Ac4TranscodeTarget;
        _virtualTunerVideoMode = SettingsService.Settings.VirtualTunerVideoMode;
        _protectedContentMode = SettingsService.Settings.ProtectedContentMode;
        _disabledChannelMode = SettingsService.Settings.DisabledChannelMode;
        _webVideoPreset = SettingsService.Settings.WebVideoPreset;
        _webVideoQuality = SettingsService.Settings.WebVideoQuality;
        _maximumVideoBitRateMbps = SettingsService.Settings.MaximumVideoBitRateMbps;
        _maximumConcurrentStreams = SettingsService.Settings.MaximumConcurrentStreams;
        _redactApiClientAddresses = SettingsService.Settings.RedactApiClientAddresses;
        _redactApiDeviceAddresses = SettingsService.Settings.RedactApiDeviceAddresses;
        _redactApiUrls = SettingsService.Settings.RedactApiUrls;
        var startupFilter = LoggingRuntime?.StartupApplicationFilter ?? new ApplicationLogFilterSettings("Information", []);
        _overrideLoggingDefaults = SettingsService.Settings.OverrideLoggingDefaults;
        _applicationLogLevel = _overrideLoggingDefaults ? SettingsService.Settings.ApplicationLogLevel : startupFilter.DefaultLevel;
        var categoryLevels = _overrideLoggingDefaults
            ? MergeLogCategorySettings(startupFilter.CategoryLevels, SettingsService.Settings.LogCategoryOverrides)
            : startupFilter.CategoryLevels;
        _logCategoryOverrides = categoryLevels.Select(CloneLogCategory).ToList();
        _startupLogCategories = startupFilter.CategoryLevels.Select(level => level.Category).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _enableFileLogging = SettingsService.Settings.EnableFileLogging;
        _fileLogLevel = SettingsService.Settings.FileLogLevel;
        _fileLogRetentionDays = SettingsService.Settings.FileLogRetentionDays;
        _fileLogSizeLimitMb = SettingsService.Settings.FileLogSizeLimitMb;
        RefreshRetainedLogFiles();
        SettingsService.Settings.EnsureHdHomeRunProxyProfiles();
        _enableHdHomeRunProxy = SettingsService.Settings.EnableHdHomeRunProxy;
        _enableNetworkDiscovery = SettingsService.Settings.EnableNetworkDiscovery;
        _proxyProfiles = SettingsService.Settings.HdHomeRunProxyProfiles.Select(CloneProfile).ToList();
    }

    private void SelectTab(SettingsTab tab)
    {
        _activeTab = tab;
        Navigation.NavigateTo($"/settings#{GetTabSlug(tab)}", replace: true);
    }

    private string GetTabButtonClass(SettingsTab tab) => $"nav-link{(_activeTab == tab ? " active" : "")}";

    private string GetTabPaneClass(SettingsTab tab) => _activeTab == tab ? "" : "d-none";

    private static string GetTabSlug(SettingsTab tab) => tab switch
    {
        SettingsTab.General => "general",
        SettingsTab.Guide => "guide",
        SettingsTab.Device => "device",
        SettingsTab.Transcoding => "transcoding",
        SettingsTab.Api => "api",
        SettingsTab.Logging => "logging",
        SettingsTab.Reset => "reset",
        _ => "general"
    };

    private static SettingsTab ParseTab(string fragment) => fragment.TrimStart('#').ToLowerInvariant() switch
    {
        "guide" => SettingsTab.Guide,
        "device" => SettingsTab.Device,
        "transcoding" => SettingsTab.Transcoding,
        "api" => SettingsTab.Api,
        "logging" => SettingsTab.Logging,
        "reset" => SettingsTab.Reset,
        _ => SettingsTab.General
    };

    private enum SettingsTab
    {
        /// <summary>
        /// Represents general.
        /// </summary>
        General,
        /// <summary>
        /// Represents guide.
        /// </summary>
        Guide,
        /// <summary>
        /// Represents device.
        /// </summary>
        Device,
        /// <summary>
        /// Represents transcoding.
        /// </summary>
        Transcoding,
        /// <summary>
        /// Represents external API privacy settings.
        /// </summary>
        Api,
        /// <summary>
        /// Represents application logging.
        /// </summary>
        Logging,
        /// <summary>
        /// Represents destructive application recovery actions.
        /// </summary>
        Reset
    }

    private async Task SaveSettings()
    {
        _isSaving = true;
        _statusMessage = "";
        StateHasChanged();

        try
        {
            if (string.IsNullOrWhiteSpace(_deviceAddress))
            {
                _statusMessage = "Device Address cannot be empty.";
                _isError = true;
                return;
            }

            if (_enableHdHomeRunProxy && !ValidateProxyProfiles())
            {
                return;
            }

            if (_deviceRefreshIntervalMinutes < 0 || _deviceRefreshIntervalMinutes > 60)
            {
                _statusMessage = "Device Refresh Interval must be between 0 and 60 minutes.";
                _isError = true;
                return;
            }

            if (_tunerRefreshIntervalSeconds < 0 || _tunerRefreshIntervalSeconds > 300)
            {
                _statusMessage = "Tuner Refresh Interval must be between 0 and 300 seconds.";
                _isError = true;
                return;
            }

            if (_activeStreamRefreshIntervalSeconds < 0 || _activeStreamRefreshIntervalSeconds > 300)
            {
                _statusMessage = "Active Stream Refresh Interval must be between 0 and 300 seconds.";
                _isError = true;
                return;
            }

            if (!ValidateXmltvOutputPath())
            {
                return;
            }

            if (_enableFileLogging && (_fileLogRetentionDays is < 1 or > 90 || _fileLogSizeLimitMb is < 1 or > 500))
            {
                _statusMessage = "Log retention must be 1-90 days and file size must be 1-500 MB.";
                _isError = true;
                return;
            }

            if (_overrideLoggingDefaults && !ValidateApplicationLogFilters())
            {
                return;
            }

            var wasAutoFetchEnabled = SettingsService.Settings.IsAutoFetchEnabled;
            var fileLoggingChanged = FileLoggingSettingsChanged();
            var applicationLoggingChanged = ApplicationLoggingSettingsChanged();
            var completingInitialSetup = _isInitialSetup;
            await SettingsService.UpdateAsync(settings =>
            {
                settings.AutoFetchInterval = _autoFetchEnabled ? TimeSpan.FromHours(24) : TimeSpan.Zero;
                settings.RefreshChannelsBeforeGuideFetch = _refreshChannelsBeforeGuideFetch;
                if (!_autoFetchEnabled || !wasAutoFetchEnabled)
                {
                    settings.NextAutoFetchTime = null;
                }
                settings.DeviceAddress = _deviceAddress.Trim();
                settings.EnableHdHomeRunProxy = _enableHdHomeRunProxy;
                settings.EnableNetworkDiscovery = _enableNetworkDiscovery;
                settings.HdHomeRunProxyProfiles = _proxyProfiles.Select(CloneProfile).ToList();
                settings.AdvertisedBaseUrl = settings.HdHomeRunProxyProfiles[0].AdvertisedBaseUrl.Trim();
                settings.DeviceRefreshIntervalMinutes = _deviceRefreshIntervalMinutes;
                settings.TunerRefreshIntervalSeconds = _tunerRefreshIntervalSeconds;
                settings.ActiveStreamRefreshIntervalSeconds = _activeStreamRefreshIntervalSeconds;
                settings.AutoGenerateXmltv = _autoGenerateXmltv;
                settings.XmltvOutputPath = _xmltvOutputPath.Trim();
                settings.Theme = _theme;
                settings.TimeZoneId = _timeZoneId;
                settings.AudioTranscodeMode = _audioTranscodeMode;
                settings.Ac4TranscodeTarget = _ac4TranscodeTarget;
                settings.VirtualTunerVideoMode = _virtualTunerVideoMode;
                settings.ProtectedContentMode = _protectedContentMode;
                settings.DisabledChannelMode = _disabledChannelMode;
                settings.WebVideoPreset = _webVideoPreset;
                settings.WebVideoQuality = _webVideoQuality;
                settings.MaximumVideoBitRateMbps = _maximumVideoBitRateMbps;
                settings.MaximumConcurrentStreams = _maximumConcurrentStreams;
                settings.RedactApiClientAddresses = _redactApiClientAddresses;
                settings.RedactApiDeviceAddresses = _redactApiDeviceAddresses;
                settings.RedactApiUrls = _redactApiUrls;
                settings.OverrideLoggingDefaults = _overrideLoggingDefaults;
                settings.ApplicationLogLevel = _applicationLogLevel;
                settings.LogCategoryOverrides = _logCategoryOverrides.Select(CloneLogCategory).ToList();
                settings.EnableFileLogging = _enableFileLogging;
                settings.FileLogLevel = _fileLogLevel;
                settings.FileLogRetentionDays = _fileLogRetentionDays;
                settings.FileLogSizeLimitMb = _fileLogSizeLimitMb;
                settings.IsSetupComplete = !completingInitialSetup;
            });

            var applyErrors = new List<string>();
            if (applicationLoggingChanged)
            {
                try
                {
                    Services.GetRequiredService<LoggingRuntimeState>().ApplyApplicationFilter(SettingsService.Settings);
                }
                catch (Exception ex)
                {
                    applyErrors.Add($"application logging filters could not be applied: {ex.Message}");
                }
            }

            if (fileLoggingChanged)
            {
                try
                {
                    Services.GetRequiredService<LogFileService>().ApplySettings(SettingsService.Settings);
                }
                catch (Exception ex)
                {
                    applyErrors.Add($"file logging could not be applied: {ex.Message}");
                }
            }

            // Apply theme immediately via JS
            await JS.InvokeVoidAsync("setTheme", _theme);

            if (applyErrors.Count > 0)
            {
                _statusMessage = $"Settings were saved, but {string.Join("; ", applyErrors)}";
                _isError = true;
                return;
            }

            _statusMessage = "Settings saved successfully!";
            _isError = false;

            if (completingInitialSetup)
            {
                var deviceState = Services.GetRequiredService<IDeviceStateService>();
                await deviceState.DiscoverDeviceAsync();
                if (!deviceState.IsDiscovered)
                {
                    throw new InvalidOperationException($"The HDHomeRun device could not be connected: {deviceState.LastError ?? "unknown error"}");
                }

                await Services.GetRequiredService<ChannelLineupRefreshService>().RefreshAsync();
                await SettingsService.UpdateAsync(settings => settings.IsSetupComplete = true);
                _isInitialSetup = false;
                Navigation.NavigateTo("/dashboard");
            }
        }
        catch (Exception ex)
        {
            _statusMessage = $"Error saving settings: {ex.Message}";
            _isError = true;
        }
        finally
        {
            _isSaving = false;
        }
    }

    private void ResetToDefaults()
    {
        var defaults = new AppSettings();
        _autoFetchEnabled = defaults.IsAutoFetchEnabled;
        _refreshChannelsBeforeGuideFetch = defaults.RefreshChannelsBeforeGuideFetch;
        _deviceAddress = defaults.DeviceAddress;
        _deviceRefreshIntervalMinutes = defaults.DeviceRefreshIntervalMinutes;
        _tunerRefreshIntervalSeconds = defaults.TunerRefreshIntervalSeconds;
        _activeStreamRefreshIntervalSeconds = defaults.ActiveStreamRefreshIntervalSeconds;
        _autoGenerateXmltv = defaults.AutoGenerateXmltv;
        _xmltvOutputPath = SettingsService.ConfiguredXmltvOutputPath;
        _theme = defaults.Theme;
        _timeZoneId = defaults.TimeZoneId;
        _audioTranscodeMode = defaults.AudioTranscodeMode;
        _ac4TranscodeTarget = defaults.Ac4TranscodeTarget;
        _virtualTunerVideoMode = defaults.VirtualTunerVideoMode;
        _protectedContentMode = defaults.ProtectedContentMode;
        _disabledChannelMode = defaults.DisabledChannelMode;
        _webVideoPreset = defaults.WebVideoPreset;
        _webVideoQuality = defaults.WebVideoQuality;
        _maximumVideoBitRateMbps = defaults.MaximumVideoBitRateMbps;
        _maximumConcurrentStreams = defaults.MaximumConcurrentStreams;
        _redactApiClientAddresses = defaults.RedactApiClientAddresses;
        _redactApiDeviceAddresses = defaults.RedactApiDeviceAddresses;
        _redactApiUrls = defaults.RedactApiUrls;
        var startupFilter = LoggingRuntime?.StartupApplicationFilter ?? new ApplicationLogFilterSettings("Information", []);
        _overrideLoggingDefaults = false;
        _applicationLogLevel = startupFilter.DefaultLevel;
        _logCategoryOverrides = startupFilter.CategoryLevels.Select(CloneLogCategory).ToList();
        _enableFileLogging = defaults.EnableFileLogging;
        _fileLogLevel = defaults.FileLogLevel;
        _fileLogRetentionDays = defaults.FileLogRetentionDays;
        _fileLogSizeLimitMb = defaults.FileLogSizeLimitMb;
        defaults.EnsureHdHomeRunProxyProfiles();
        _enableHdHomeRunProxy = defaults.EnableHdHomeRunProxy;
        _enableNetworkDiscovery = defaults.EnableNetworkDiscovery;
        _proxyProfiles = defaults.HdHomeRunProxyProfiles.Select(CloneProfile).ToList();
        _statusMessage = "Settings reset to defaults. Click Save to apply.";
        _isError = false;
        SelectTab(SettingsTab.General);
    }

    private LoggingRuntimeState? LoggingRuntime => Services.GetService<LoggingRuntimeState>();

    private static IReadOnlyList<string> ApplicationLogLevels => ApplicationLogLevelNames.All;

    private bool IsStartupLogCategory(string category) => _startupLogCategories.Contains(category);

    private void AddLogCategoryOverride() => _logCategoryOverrides.Add(new LogCategoryLevelSetting());

    private void RemoveLogCategoryOverride(int index)
    {
        if (index >= 0 && index < _logCategoryOverrides.Count && !IsStartupLogCategory(_logCategoryOverrides[index].Category))
        {
            _logCategoryOverrides.RemoveAt(index);
        }
    }

    private bool ValidateApplicationLogFilters()
    {
        if (_logCategoryOverrides.Any(level =>
            string.IsNullOrWhiteSpace(level.Category) ||
            level.Category.Length > 200 ||
            string.Equals(level.Category, "Default", StringComparison.OrdinalIgnoreCase)))
        {
            _statusMessage = "Logging category prefixes must contain 1-200 characters and cannot be named Default.";
            _isError = true;
            return false;
        }

        if (_logCategoryOverrides.GroupBy(level => level.Category, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
        {
            _statusMessage = "Logging category prefixes must be unique.";
            _isError = true;
            return false;
        }

        return true;
    }

    private bool ApplicationLoggingSettingsChanged()
    {
        var persisted = SettingsService.Settings;
        if (_overrideLoggingDefaults != persisted.OverrideLoggingDefaults)
        {
            return true;
        }

        return _overrideLoggingDefaults &&
            (!string.Equals(_applicationLogLevel, persisted.ApplicationLogLevel, StringComparison.OrdinalIgnoreCase) ||
                !LogCategorySettingsEqual(_logCategoryOverrides, persisted.LogCategoryOverrides));
    }

    private static bool LogCategorySettingsEqual(IEnumerable<LogCategoryLevelSetting> left, IEnumerable<LogCategoryLevelSetting> right)
    {
        var leftLevels = left.GroupBy(level => level.Category, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.Last().Level, StringComparer.OrdinalIgnoreCase);
        var rightLevels = right.GroupBy(level => level.Category, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.Last().Level, StringComparer.OrdinalIgnoreCase);
        return leftLevels.Count == rightLevels.Count &&
            leftLevels.All(pair => rightLevels.TryGetValue(pair.Key, out var level) && string.Equals(pair.Value, level, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<LogCategoryLevelSetting> MergeLogCategorySettings(IEnumerable<LogCategoryLevelSetting> startupLevels, IEnumerable<LogCategoryLevelSetting> overrideLevels)
    {
        var merged = startupLevels
            .Concat(overrideLevels)
            .GroupBy(level => level.Category, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToArray();
        return merged;
    }

    private static LogCategoryLevelSetting CloneLogCategory(LogCategoryLevelSetting level) => new() { Category = level.Category, Level = level.Level };

    private void RefreshRetainedLogFiles() => _retainedLogFiles = Services.GetService<LogFileService>()?.GetFiles() ?? [];

    private static string FormatFileSize(long bytes)
    {
        if (bytes >= 1024 * 1024)
        {
            return $"{bytes / (1024d * 1024d):0.0} MB";
        }

        return bytes >= 1024 ? $"{bytes / 1024d:0.0} KB" : $"{bytes} B";
    }

    private bool FileLoggingSettingsChanged()
    {
        var persisted = SettingsService.Settings;
        return _enableFileLogging != persisted.EnableFileLogging ||
            !string.Equals(_fileLogLevel, persisted.FileLogLevel, StringComparison.OrdinalIgnoreCase) ||
            _fileLogRetentionDays != persisted.FileLogRetentionDays ||
            _fileLogSizeLimitMb != persisted.FileLogSizeLimitMb;
    }

    private static string GetLoggingStatusBadge(string status) => status switch
    {
        "Enabled" => "text-bg-success",
        "Invalid" => "text-bg-danger",
        _ => "text-bg-secondary"
    };

    private void UndoUnsavedChanges()
    {
        LoadCurrentSettings();
        _showDirectoryBrowser = false;
        _statusMessage = "Unsaved settings changes reverted.";
        _isError = false;
    }

    private void ShowFactoryResetConfirmation()
    {
        _showFactoryResetConfirmation = true;
        _factoryResetConfirmation = "";
        ClearStatus();
    }

    private void CancelFactoryReset()
    {
        _showFactoryResetConfirmation = false;
        _factoryResetConfirmation = "";
    }

    private bool CanFactoryReset => string.Equals(_factoryResetConfirmation, "RESET", StringComparison.Ordinal);

    private async Task RestartLineup()
    {
        if (_isRestarting)
        {
            return;
        }

        _isRestarting = true;
        ClearStatus();
        await InvokeAsync(StateHasChanged);
        try
        {
            await JS.InvokeVoidAsync("beginLineupRestartWatch", "/settings#reset");
            await Services.GetRequiredService<IApplicationRestartService>().RestartAsync();
        }
        catch (Exception ex)
        {
            _statusMessage = $"Lineup could not be restarted: {ex.Message}";
            _isError = true;
            _isRestarting = false;
        }
    }

    private async Task FactoryReset()
    {
        if (!CanFactoryReset || _isFactoryResetting)
        {
            return;
        }

        _isFactoryResetting = true;
        _statusMessage = "";
        _isError = false;
        await InvokeAsync(StateHasChanged);
        try
        {
            await JS.InvokeVoidAsync("beginLineupFactoryResetWatch");
            await Services.GetRequiredService<IFactoryResetService>().RequestResetAsync();
        }
        catch (Exception ex)
        {
            _statusMessage = $"Factory reset could not be started: {ex.Message}";
            _isError = true;
            _isFactoryResetting = false;
        }
    }

    private async Task Cancel()
    {
        if (_isInitialSetup)
        {
            // Mark setup complete even if user skips — don't redirect here again
            await SettingsService.UpdateAsync(s => s.IsSetupComplete = true);
        }
        Navigation.NavigateTo("/dashboard");
    }

    private void ClearStatus()
    {
        _statusMessage = "";
    }

    private void DeleteLogs()
    {
        if (_isDeletingLogs)
        {
            return;
        }

        _isDeletingLogs = true;
        try
        {
            var deleted = Services.GetRequiredService<LogFileService>().DeleteFiles();
            RefreshRetainedLogFiles();
            _statusMessage = deleted == 0 ? "No file logs were found." : $"Deleted {deleted} file log{(deleted == 1 ? "" : "s")}.";
            _isError = false;
        }
        catch (Exception ex)
        {
            _statusMessage = $"File logs could not be deleted: {ex.Message}";
            _isError = true;
        }
        finally
        {
            _isDeletingLogs = false;
        }
    }

    private void ResetXmltvOutputPath()
    {
        _xmltvOutputPath = SettingsService.ConfiguredXmltvOutputPath;
        ClearStatus();
    }

    private bool XmltvOutputPathDiffersFromConfigured()
    {
        try
        {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return !string.Equals(Path.GetFullPath(_xmltvOutputPath), Path.GetFullPath(SettingsService.ConfiguredXmltvOutputPath), comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return true;
        }
    }

    private bool ValidateXmltvOutputPath()
    {
        if (string.IsNullOrWhiteSpace(_xmltvOutputPath))
        {
            _statusMessage = "XMLTV Output Destination cannot be empty.";
            _isError = true;
            return false;
        }

        try
        {
            _ = Path.GetFullPath(_xmltvOutputPath);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            _statusMessage = "XMLTV Output Destination is invalid.";
            _isError = true;
            return false;
        }
    }

    private void BrowseDirectory()
    {
        _showDirectoryBrowser = true;
        _directoryError = null;
        try
        {
            var resolvedPath = GetResolvedPath(_xmltvOutputPath);
            var directory = Path.GetDirectoryName(resolvedPath);
            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
            {
                _currentDirectory = directory;
                _selectedFilename = Path.GetFileName(resolvedPath);
                LoadDirectories();
                return;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            _directoryError = "The current XMLTV path is invalid. Select a server directory and filename.";
        }

        _currentDirectory = Directory.GetCurrentDirectory();
        _selectedFilename = AppConstants.DefaultXmltvFileName;
        LoadDirectories();
    }

    private void CloseDirectoryBrowser()
    {
        _showDirectoryBrowser = false;
    }

    private void LoadDirectories()
    {
        _directoryError = null;
        try
        {
            _directories = Directory.GetDirectories(_currentDirectory).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (UnauthorizedAccessException)
        {
            _directoryError = "Access denied to this directory.";
            _directories = [];
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            _directoryError = $"Unable to browse this directory: {ex.Message}";
            _directories = [];
        }
    }

    private void NavigateToDirectory(string path)
    {
        _currentDirectory = path;
        LoadDirectories();
    }

    private void NavigateUp()
    {
        var parent = Directory.GetParent(_currentDirectory);
        if (parent != null)
        {
            _currentDirectory = parent.FullName;
            LoadDirectories();
        }
    }

    private bool CanNavigateUp => !string.IsNullOrEmpty(_currentDirectory) && Directory.GetParent(_currentDirectory) != null;

    private void SelectXmltvPath()
    {
        if (string.IsNullOrWhiteSpace(_selectedFilename))
        {
            _selectedFilename = AppConstants.DefaultXmltvFileName;
        }

        _xmltvOutputPath = Path.Combine(_currentDirectory, _selectedFilename.Trim());
        _showDirectoryBrowser = false;
        ClearStatus();
    }

    private string? ResolvedXmltvOutputPath
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_xmltvOutputPath))
            {
                return null;
            }

            try
            {
                return GetResolvedPath(_xmltvOutputPath);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }
        }
    }

    private static string GetResolvedPath(string path)
    {
        return Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), path));
    }

    private bool ValidateProxyProfiles()
    {
        if (_proxyProfiles.Count == 0)
        {
            _statusMessage = "At least one HDHomeRun proxy profile is required.";
            _isError = true;
            return false;
        }

        _proxyProfiles[0].PhysicalAddress = _deviceAddress.Trim();
        var deviceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var physicalAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in _proxyProfiles)
        {
            if (profile.Enabled && string.IsNullOrWhiteSpace(profile.PhysicalAddress))
            {
                _statusMessage = "Enabled proxy profiles require a physical device address.";
                _isError = true;
                return false;
            }

            if (profile.Enabled && !physicalAddresses.Add(profile.PhysicalAddress.Trim()))
            {
                _statusMessage = "Enabled proxy profiles must use distinct physical device addresses.";
                _isError = true;
                return false;
            }

            if (!HdHomeRunProxyIdentity.IsValidDeviceId(profile.VirtualDeviceId) ||
                !deviceIds.Add(profile.VirtualDeviceId))
            {
                _statusMessage = "Each proxy profile must have a unique, valid virtual DeviceID.";
                _isError = true;
                return false;
            }

            if (!string.IsNullOrWhiteSpace(profile.AdvertisedBaseUrl) &&
                !HdHomeRunProxyProfileResolver.TryGetHttpRoot(profile.AdvertisedBaseUrl, out _))
            {
                _statusMessage = "Advertised base URLs must be absolute HTTP or HTTPS URLs.";
                _isError = true;
                return false;
            }

            if (_enableNetworkDiscovery && profile.Enabled &&
                !HdHomeRunProxyProfileResolver.TryGetHttpRoot(profile.AdvertisedBaseUrl, out _))
            {
                _statusMessage = "Every enabled profile requires an advertised base URL when network discovery is enabled.";
                _isError = true;
                return false;
            }
        }

        return true;
    }

    private void AddProxyProfile()
    {
        var profile = new HdHomeRunProxyProfileSettings
        {
            Enabled = true,
            VirtualDeviceId = HdHomeRunProxyIdentity.CreateDeviceId($"lineup-profile:{Guid.NewGuid():N}")
        };
        _proxyProfiles.Add(profile);
        ClearStatus();
    }

    private void RemoveProxyProfile(int index)
    {
        if (index <= 0 || index >= _proxyProfiles.Count)
        {
            return;
        }

        _proxyProfiles.RemoveAt(index);
        ClearStatus();
    }

    private Uri GetProfileSetupBaseUri(HdHomeRunProxyProfileSettings profile, int index)
    {
        var root = HdHomeRunProxyProfileResolver.TryGetHttpRoot(profile.AdvertisedBaseUrl, out var advertisedRoot)
            ? advertisedRoot!
            : new Uri(Navigation.BaseUri);
        return index == 0 ? root : new Uri(root, $"hdhomerun/{profile.VirtualDeviceId}/");
    }

    private string GetProfileSetupUrl(HdHomeRunProxyProfileSettings profile, int index, string path)
    {
        return new Uri(GetProfileSetupBaseUri(profile, index), path).AbsoluteUri;
    }

    private async Task CopyUrl(string value)
    {
        await JS.InvokeVoidAsync("navigator.clipboard.writeText", value);
    }

    private static HdHomeRunProxyProfileSettings CloneProfile(HdHomeRunProxyProfileSettings profile)
    {
        return new HdHomeRunProxyProfileSettings
        {
            PhysicalAddress = profile.PhysicalAddress?.Trim() ?? string.Empty,
            UsesLegacyDeviceAddress = profile.UsesLegacyDeviceAddress,
            Enabled = profile.Enabled,
            FriendlyName = string.IsNullOrWhiteSpace(profile.FriendlyName) ? null : profile.FriendlyName.Trim(),
            VirtualDeviceId = profile.VirtualDeviceId?.Trim().ToUpperInvariant() ?? string.Empty,
            AdvertisedBaseUrl = profile.AdvertisedBaseUrl?.Trim() ?? string.Empty,
            TunerCountCap = profile.TunerCountCap
        };
    }

}
