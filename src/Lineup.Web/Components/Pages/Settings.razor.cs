using Lineup.Core;
using Lineup.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Lineup.Web.Components.Pages;

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

    private int _targetDays;
    private int _autoFetchDays;
    private int _autoFetchHours;
    private int _autoFetchMinutes;
    private string _deviceAddress = "";
    private int _deviceRefreshIntervalMinutes;
    private int _tunerRefreshIntervalSeconds;
    private string _xmltvOutputPath = "";
    private bool _autoGenerateXmltv;
    private string _theme = "auto";
    private string _timeZoneId = "";
    private bool _isSaving;
    private string _statusMessage = "";
    private bool _isError;
    private bool _isInitialSetup;

    // Directory browser state
    private bool _showDirectoryBrowser;
    private string _currentDirectory = "";
    private string[] _directories = [];
    private string? _directoryError;
    private string _selectedFilename = "epg.xml";

    protected override void OnInitialized()
    {
        _isInitialSetup = !SettingsService.Settings.IsSetupComplete;
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

    public void Dispose()
    {
        SettingsService.OnSettingsChanged -= OnSettingsChanged;
    }

    private void LoadCurrentSettings()
    {
        _targetDays = SettingsService.Settings.TargetDays;
        var interval = SettingsService.Settings.AutoFetchInterval;
        _autoFetchDays = interval.Days;
        _autoFetchHours = interval.Hours;
        _autoFetchMinutes = interval.Minutes;
        _deviceAddress = SettingsService.Settings.DeviceAddress;
        _deviceRefreshIntervalMinutes = SettingsService.Settings.DeviceRefreshIntervalMinutes;
        _tunerRefreshIntervalSeconds = SettingsService.Settings.TunerRefreshIntervalSeconds;
        _xmltvOutputPath = SettingsService.Settings.XmltvOutputPath;
        _autoGenerateXmltv = SettingsService.Settings.AutoGenerateXmltv;
        _theme = SettingsService.Settings.Theme;
        _timeZoneId = SettingsService.Settings.TimeZoneId;
    }

    private async Task SaveSettings()
    {
        _isSaving = true;
        _statusMessage = "";
        StateHasChanged();

        try
        {
            // Validate
            if (_targetDays < 1 || _targetDays > 14)
            {
                _statusMessage = "Target Days must be between 1 and 14.";
                _isError = true;
                return;
            }

            if (_autoFetchDays < 0 || _autoFetchHours < 0 || _autoFetchMinutes < 0)
            {
                _statusMessage = "Auto-Fetch Interval values cannot be negative.";
                _isError = true;
                return;
            }

            var autoFetchInterval = new TimeSpan(_autoFetchDays, _autoFetchHours, _autoFetchMinutes, 0);
            if (autoFetchInterval > TimeSpan.Zero && autoFetchInterval < TimeSpan.FromMinutes(5))
            {
                _statusMessage = "Auto-Fetch Interval must be at least 5 minutes when enabled.";
                _isError = true;
                return;
            }

            if (string.IsNullOrWhiteSpace(_deviceAddress))
            {
                _statusMessage = "Device Address cannot be empty.";
                _isError = true;
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

            if (string.IsNullOrWhiteSpace(_xmltvOutputPath))
            {
                _statusMessage = "XMLTV Output Path cannot be empty.";
                _isError = true;
                return;
            }

            await SettingsService.UpdateAsync(settings =>
            {
                settings.TargetDays = _targetDays;
                settings.AutoFetchInterval = autoFetchInterval;
                settings.DeviceAddress = _deviceAddress.Trim();
                settings.DeviceRefreshIntervalMinutes = _deviceRefreshIntervalMinutes;
                settings.TunerRefreshIntervalSeconds = _tunerRefreshIntervalSeconds;
                settings.XmltvOutputPath = _xmltvOutputPath.Trim();
                settings.AutoGenerateXmltv = _autoGenerateXmltv;
                settings.Theme = _theme;
                settings.TimeZoneId = _timeZoneId;
                settings.IsSetupComplete = true;
            });

            // Apply theme immediately via JS
            await JS.InvokeVoidAsync("setTheme", _theme);

            _statusMessage = "Settings saved successfully!";
            _isError = false;

            if (_isInitialSetup)
            {
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
        _targetDays = defaults.TargetDays;
        _autoFetchDays = defaults.AutoFetchInterval.Days;
        _autoFetchHours = defaults.AutoFetchInterval.Hours;
        _autoFetchMinutes = defaults.AutoFetchInterval.Minutes;
        _deviceAddress = defaults.DeviceAddress;
        _deviceRefreshIntervalMinutes = defaults.DeviceRefreshIntervalMinutes;
        _tunerRefreshIntervalSeconds = defaults.TunerRefreshIntervalSeconds;
        _xmltvOutputPath = defaults.XmltvOutputPath;
        _autoGenerateXmltv = defaults.AutoGenerateXmltv;
        _theme = defaults.Theme;
        _timeZoneId = defaults.TimeZoneId;
        _statusMessage = "Settings reset to defaults. Click Save to apply.";
        _isError = false;
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

    // Directory browser methods
    private void BrowseDirectory()
    {
        _showDirectoryBrowser = true;
        _directoryError = null;

        // Start from the current path's directory, or working directory
        if (!string.IsNullOrEmpty(_xmltvOutputPath))
        {
            try
            {
                var resolvedPath = GetResolvedPath(_xmltvOutputPath);
                var dir = Path.GetDirectoryName(resolvedPath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    _currentDirectory = dir;
                    _selectedFilename = Path.GetFileName(_xmltvOutputPath);
                    LoadDirectories();
                    return;
                }
            }
            catch { }
        }

        // Default to working directory
        _currentDirectory = Directory.GetCurrentDirectory();
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
            _directories = Directory.GetDirectories(_currentDirectory)
                .OrderBy(d => Path.GetFileName(d))
                .ToArray();
        }
        catch (UnauthorizedAccessException)
        {
            _directoryError = "Access denied to this directory.";
            _directories = [];
        }
        catch (Exception ex)
        {
            _directoryError = $"Error: {ex.Message}";
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

    private bool CanNavigateUp => Directory.GetParent(_currentDirectory) != null;

    private void SelectPath()
    {
        if (string.IsNullOrWhiteSpace(_selectedFilename))
        {
            _selectedFilename = AppConstants.DefaultXmltvFileName;
        }

        _xmltvOutputPath = Path.Combine(_currentDirectory, _selectedFilename);
        _showDirectoryBrowser = false;
    }

    private static string GetResolvedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Path.Combine(AppConstants.DefaultXmltvFilePath, AppConstants.DefaultXmltvFileName);
        }

        if (Path.IsPathRooted(path))
        {
            return Path.GetFullPath(path);
        }

        return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), path));
    }
}
