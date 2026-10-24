using System.Text.Json;
using Lineup.Common.Configuration;
using Lineup.Common.Networking;
using Lineup.Extensibility.Configuration;
using Lineup.LineupLink.Protocol;
using Lineup.LineupLink.Virtual;
using Lineup.GuideOutputs.Xmltv.Plugin;
using Lineup.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.JSInterop;

namespace Lineup.Web.Components.Pages;

/// <summary>
/// Displays and edits persisted Lineup application settings.
/// </summary>
public partial class Settings : IDisposable
{
    [Inject]
    private ILineupSettingsService SettingsService { get; set; } = default!;

    [Inject]
    private LineupStartupState StartupState { get; set; } = default!;

    private readonly IFileSystemBrowser FileSystem = new FileSystemBrowser();

    [Inject]
    private NavigationManager Navigation { get; set; } = default!;

    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    [Inject]
    private ITimeZoneService TimeZoneService { get; set; } = default!;

    [Inject]
    private IServiceProvider Services { get; set; } = default!;

    private IWebSettingsService WebSettingsService => Services.GetService<IWebSettingsService>() ??
        WebSettingsServiceFallback.Get(SettingsService);

    [Inject]
    private IEnumerable<IPhysicalTunerDriver> PhysicalTunerDrivers { get; set; } = default!;

    [Inject]
    private IEnumerable<IVirtualTunerDriver> VirtualTunerDrivers { get; set; } = default!;

    [Inject]
    private IEnumerable<IGuideSourceDriver> GuideSourceDrivers { get; set; } = default!;

    [Inject]
    private IEnumerable<IGuideOutputDriver> GuideOutputDrivers { get; set; } = default!;

    [Inject]
    private IStatusNotificationService Notifications { get; set; } = default!;

    private bool _autoFetchEnabled;
    private bool _refreshChannelsBeforeGuideFetch;
    private int _epgHistoryRetentionHours;
    private int _deviceRefreshIntervalMinutes;
    private int _tunerRefreshIntervalSeconds;
    private int _activeStreamRefreshIntervalSeconds;
    private bool _autoGenerateXmltv;
    private string _xmltvOutputPath = "";
    private string _theme = "auto";
    private string _timeZoneId = "";
    private string _uiCulture = UiLocalization.DefaultCulture;
    private string _knownProxies = string.Empty;
    private ProtectedContentMode _protectedContentMode;
    private DisabledChannelMode _disabledChannelMode;
    private int _maximumConcurrentStreams;
    private bool _redactApiClientAddresses;
    private bool _redactApiDeviceAddresses;
    private bool _redactApiUrls;
    private bool _overrideLoggingDefaults;
    private string _applicationLogLevel = "Information";
    private List<WebLogCategoryLevelSetting> _logCategoryOverrides = [];
    private HashSet<string> _startupLogCategories = new(StringComparer.OrdinalIgnoreCase);
    private bool _enableFileLogging;
    private string _fileLogLevel = "Information";
    private int _fileLogRetentionDays;
    private int _fileLogSizeLimitMb;
    private IReadOnlyList<LogFileInfo> _retainedLogFiles = [];
    private bool _enableNetworkDiscovery;
    private bool _isSaving;
    private bool _isInitialSetup;
    private SettingsTab _activeTab = SettingsTab.General;
    private bool _showDirectoryBrowser;
    private string _currentDirectory = "";
    private string[] _directories = [];
    private string? _directoryError;
    private bool _showFactoryResetConfirmation;
    private bool _factoryResetConfirmed;
    private bool _isRestarting;
    private bool _isFactoryResetting;
    private bool _isDeletingLogs;
    private readonly TopologySettingsEditor _topologyEditor = new();
    private TopologySettingsModel _topology = new();
    private IReadOnlyList<IPhysicalTunerDriver> _availablePhysicalTunerDrivers = [];
    private IReadOnlyList<IVirtualTunerDriver> _availableVirtualTunerDrivers = [];
    private IReadOnlyList<IGuideSourceDriver> _availableGuideSourceDrivers = [];
    private IReadOnlyList<IGuideOutputDriver> _availableGuideOutputDrivers = [];
    private bool _showPhysicalDeviceTypePicker;
    private bool _showVirtualDeviceTypePicker;
    private bool _showGuideSourceTypePicker;
    private bool _showGuideOutputTypePicker;
    private PhysicalDeviceId? _selectedPhysicalDeviceId;
    private VirtualDeviceId? _selectedVirtualDeviceId;
    private GuideSourceId? _selectedGuideSourceId;
    private GuideOutputId? _selectedGuideOutputId;
    private readonly Dictionary<PhysicalDeviceId, PhysicalDeviceProbeState> _physicalDeviceProbeStates = [];
    private CancellationTokenSource? _physicalDeviceProbeCancellation;

    internal TopologySettingsModel Topology => _topology;
    internal IReadOnlyList<IPhysicalTunerDriver> AvailablePhysicalTunerDrivers => _availablePhysicalTunerDrivers;
    internal IReadOnlyList<IVirtualTunerDriver> AvailableVirtualTunerDrivers => _availableVirtualTunerDrivers;
    internal IReadOnlyList<IGuideSourceDriver> AvailableGuideSourceDrivers => _availableGuideSourceDrivers;
    internal IReadOnlyList<IGuideOutputDriver> AvailableGuideOutputDrivers => _availableGuideOutputDrivers;
    internal string ConfiguredXmltvOutputPath => SettingsService.ConfiguredXmltvOutputPath;
    internal bool AutoFetchEnabled { get => _autoFetchEnabled; set => _autoFetchEnabled = value; }
    internal bool RefreshChannelsBeforeGuideFetch { get => _refreshChannelsBeforeGuideFetch; set => _refreshChannelsBeforeGuideFetch = value; }
    internal int EpgHistoryRetentionHours { get => _epgHistoryRetentionHours; set => _epgHistoryRetentionHours = value; }
    internal int DeviceRefreshIntervalMinutes { get => _deviceRefreshIntervalMinutes; set => _deviceRefreshIntervalMinutes = value; }
    internal int TunerRefreshIntervalSeconds { get => _tunerRefreshIntervalSeconds; set => _tunerRefreshIntervalSeconds = value; }
    internal bool AutoGenerateXmltv { get => _autoGenerateXmltv; set => _autoGenerateXmltv = value; }
    internal string XmltvOutputPath
    {
        get => SelectedGuideOutput?.OutputPath ?? _xmltvOutputPath;
        set
        {
            if (SelectedGuideOutput is { } output)
            {
                output.OutputPath = value;
                if (output.IsBuiltIn)
                {
                    _xmltvOutputPath = value;
                }
            }
            else
            {
                _xmltvOutputPath = value;
            }
        }
    }
    internal string GuideOutputDirectoryPath
    {
        get
        {
            var path = SelectedGuideOutput?.OutputPath ?? _xmltvOutputPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }
            try
            {
                return Path.GetDirectoryName(FileSystem.ResolvePath(path)) ?? string.Empty;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return Path.GetDirectoryName(path) ?? string.Empty;
            }
        }
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                XmltvOutputPath = string.Empty;
                return;
            }

            var output = SelectedGuideOutput;
            var fileName = output is { IsBuiltIn: false }
                ? $"lineup-{output.Key}.xml"
                : Path.GetFileName(output?.OutputPath ?? _xmltvOutputPath);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = AppConstants.DefaultXmltvFileName;
            }
            XmltvOutputPath = Path.Combine(value.Trim(), fileName);
        }
    }
    internal bool EnableNetworkDiscovery { get => _enableNetworkDiscovery; set => _enableNetworkDiscovery = value; }
    internal bool ShowDirectoryBrowser { get => _showDirectoryBrowser; set => _showDirectoryBrowser = value; }
    internal string CurrentDirectory => _currentDirectory;
    internal IReadOnlyList<string> Directories => _directories;
    internal string? DirectoryError => _directoryError;
    internal bool ShowPhysicalDeviceTypePickerValue { get => _showPhysicalDeviceTypePicker; set => _showPhysicalDeviceTypePicker = value; }
    internal bool ShowVirtualDeviceTypePickerValue { get => _showVirtualDeviceTypePicker; set => _showVirtualDeviceTypePicker = value; }
    internal bool ShowGuideSourceTypePickerValue { get => _showGuideSourceTypePicker; set => _showGuideSourceTypePicker = value; }
    internal bool ShowGuideOutputTypePickerValue { get => _showGuideOutputTypePicker; set => _showGuideOutputTypePicker = value; }
    internal PhysicalDeviceId? SelectedPhysicalDeviceId
    {
        get => _selectedPhysicalDeviceId;
        set
        {
            _selectedPhysicalDeviceId = value;
            StateHasChanged();
        }
    }

    internal VirtualDeviceId? SelectedVirtualDeviceId
    {
        get => _selectedVirtualDeviceId;
        set
        {
            _selectedVirtualDeviceId = value;
            StateHasChanged();
        }
    }

    internal GuideSourceId? SelectedGuideSourceId
    {
        get => _selectedGuideSourceId;
        set
        {
            _selectedGuideSourceId = value;
            StateHasChanged();
        }
    }

    internal GuideOutputId? SelectedGuideOutputId
    {
        get => _selectedGuideOutputId;
        set
        {
            _selectedGuideOutputId = value;
            _showDirectoryBrowser = false;
            StateHasChanged();
        }
    }

    internal GuideOutputSettingsModel? SelectedGuideOutput =>
        _topology.GuideOutputs.FirstOrDefault(output => output.Id == _selectedGuideOutputId);

    internal void NotifySettingsUiChanged() => StateHasChanged();

    /// <inheritdoc />
    protected override void OnInitialized()
    {
        _isInitialSetup = !WebSettingsService.Settings.IsSetupComplete && !StartupState.IsFactoryResetRequired;
        _activeTab = StartupState.IsFactoryResetRequired ? SettingsTab.Reset : ParseTab(new Uri(Navigation.Uri).Fragment);
        LoadCurrentSettings();
        _availablePhysicalTunerDrivers = PhysicalTunerDrivers
            .Where(driver => driver.SupportsUserCreation)
            .OrderBy(driver => driver.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _availableVirtualTunerDrivers = VirtualTunerDrivers
            .Where(driver => driver.SupportsUserCreation)
            .OrderBy(driver => driver.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _availableGuideSourceDrivers = GuideSourceDrivers
            .Where(driver => driver.SupportsUserCreation)
            .OrderBy(driver => driver.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _availableGuideOutputDrivers = GuideOutputDrivers
            .Where(driver => driver.SupportsUserCreation)
            .OrderBy(driver => IsXmltvGuideOutputKind(driver.Kind) ? 0 : 1)
            .ThenBy(driver => driver.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        SettingsService.OnSettingsChanged += OnSettingsChanged;
    }

    private void OnSettingsChanged()
    {
        InvokeAsync(() =>
        {
            // Only update the theme field from external changes (header toggle).
            // Other fields are only loaded on init to avoid overwriting unsaved edits.
            _theme = WebSettingsService.Settings.Theme;
            StateHasChanged();
        });
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _physicalDeviceProbeCancellation?.Cancel();
        _physicalDeviceProbeCancellation?.Dispose();
        SettingsService.OnSettingsChanged -= OnSettingsChanged;
    }

    private void LoadCurrentSettings()
    {
        ClearPhysicalDeviceProbeStates();
        _autoFetchEnabled = SettingsService.Settings.IsAutoFetchEnabled;
        _refreshChannelsBeforeGuideFetch = SettingsService.Settings.RefreshChannelsBeforeGuideFetch;
        _epgHistoryRetentionHours = SettingsService.Settings.EpgHistoryRetentionHours;
        _deviceRefreshIntervalMinutes = SettingsService.Settings.DeviceRefreshIntervalMinutes;
        _tunerRefreshIntervalSeconds = SettingsService.Settings.TunerRefreshIntervalSeconds;
        _activeStreamRefreshIntervalSeconds = WebSettingsService.Settings.ActiveStreamRefreshIntervalSeconds;
        _autoGenerateXmltv = SettingsService.Settings.AutoGenerateXmltv;
        _xmltvOutputPath = SettingsService.Settings.XmltvOutputPath;
        _theme = WebSettingsService.Settings.Theme;
        _timeZoneId = SettingsService.Settings.TimeZoneId;
        _uiCulture = WebSettingsService.Settings.UiCulture;
        _knownProxies = string.Join(Environment.NewLine, SettingsService.Settings.KnownProxies);
        _protectedContentMode = SettingsService.Settings.ProtectedContentMode;
        _disabledChannelMode = SettingsService.Settings.DisabledChannelMode;
        _maximumConcurrentStreams = SettingsService.Settings.MaximumConcurrentStreams;
        _redactApiClientAddresses = WebSettingsService.Settings.RedactApiClientAddresses;
        _redactApiDeviceAddresses = WebSettingsService.Settings.RedactApiDeviceAddresses;
        _redactApiUrls = WebSettingsService.Settings.RedactApiUrls;
        var startupFilter = LoggingRuntime?.StartupApplicationFilter ?? new ApplicationLogFilterSettings("Information", []);
        _overrideLoggingDefaults = WebSettingsService.Settings.OverrideLoggingDefaults;
        _applicationLogLevel = _overrideLoggingDefaults ? WebSettingsService.Settings.ApplicationLogLevel : startupFilter.DefaultLevel;
        var categoryLevels = _overrideLoggingDefaults
            ? MergeLogCategorySettings(startupFilter.CategoryLevels, WebSettingsService.Settings.LogCategoryOverrides)
            : startupFilter.CategoryLevels;
        _logCategoryOverrides = categoryLevels.Select(CloneLogCategory).ToList();
        _startupLogCategories = startupFilter.CategoryLevels.Select(level => level.Category).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _enableFileLogging = WebSettingsService.Settings.EnableFileLogging;
        _fileLogLevel = WebSettingsService.Settings.FileLogLevel;
        _fileLogRetentionDays = WebSettingsService.Settings.FileLogRetentionDays;
        _fileLogSizeLimitMb = WebSettingsService.Settings.FileLogSizeLimitMb;
        RefreshRetainedLogFiles();
        _enableNetworkDiscovery = WebSettingsService.Settings.EnableNetworkDiscovery;
        _topology = _topologyEditor.Edit(SettingsService.Settings.Streaming, SettingsService.Settings.XmltvOutputPath, GuideOutputDrivers);
        _selectedGuideOutputId = _topology.GuideOutputs.FirstOrDefault(output => output.IsBuiltIn)?.Id
            ?? _topology.GuideOutputs.FirstOrDefault()?.Id;
        _xmltvOutputPath = _topology.GuideOutputs.FirstOrDefault(output => output.IsBuiltIn)?.OutputPath
            ?? SettingsService.Settings.XmltvOutputPath;
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
        SettingsTab.Guide => "guide-sources",
        SettingsTab.GuideOutputs => "guide-outputs",
        SettingsTab.PhysicalDevices => "physical-devices",
        SettingsTab.VirtualDevices => "virtual-devices",
        SettingsTab.Streaming => "streaming",
        SettingsTab.Dashboard => "dashboard",
        SettingsTab.Api => "api",
        SettingsTab.Logging => "logging",
        SettingsTab.Reset => "reset",
        _ => "general"
    };

    private static SettingsTab ParseTab(string fragment) => fragment.TrimStart('#').ToLowerInvariant() switch
    {
        "guide" => SettingsTab.Guide,
        "guide-sources" => SettingsTab.Guide,
        "guide-outputs" => SettingsTab.GuideOutputs,
        "device" => SettingsTab.PhysicalDevices,
        "physical-devices" => SettingsTab.PhysicalDevices,
        "virtual-devices" => SettingsTab.VirtualDevices,
        "streaming" => SettingsTab.Streaming,
        "transcoding" => SettingsTab.Streaming,
        "dashboard" => SettingsTab.Dashboard,
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
        /// Represents guide outputs.
        /// </summary>
        GuideOutputs,
        /// <summary>
        /// Represents physical devices.
        /// </summary>
        PhysicalDevices,
        /// <summary>
        /// Represents virtual devices.
        /// </summary>
        VirtualDevices,
        /// <summary>
        /// Represents global streaming policies.
        /// </summary>
        Streaming,
        /// <summary>
        /// Represents Web Dashboard presentation settings.
        /// </summary>
        Dashboard,
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
        StateHasChanged();

        try
        {
            if (!ValidateGuideOutputPaths())
            {
                return;
            }

            var topologyErrors = _topologyEditor.TryBuild(
                _topology,
                out var streamingConfiguration,
                GuideSourceDrivers,
                PhysicalTunerDrivers,
                VirtualTunerDrivers,
                GuideOutputDrivers,
                GetManagementListenerPorts());
            if (topologyErrors.Count > 0)
            {
                Notifications.ShowError(string.Join(" ", topologyErrors));
                return;
            }

            if (_deviceRefreshIntervalMinutes < 0 || _deviceRefreshIntervalMinutes > 60)
            {
                Notifications.ShowError(Text["Settings_DeviceRefreshValidation"]);
                return;
            }

            if (_tunerRefreshIntervalSeconds < 0 || _tunerRefreshIntervalSeconds > 300)
            {
                Notifications.ShowError(Text["Settings_TunerRefreshValidation"]);
                return;
            }

            if (_activeStreamRefreshIntervalSeconds < 0 || _activeStreamRefreshIntervalSeconds > 300)
            {
                Notifications.ShowError(Text["Settings_StreamRefreshValidation"]);
                return;
            }

            if (_epgHistoryRetentionHours is < 0 or > 720)
            {
                Notifications.ShowError(Text["Settings_EpgRetentionValidation"]);
                return;
            }

            if (_enableFileLogging && (_fileLogRetentionDays is < 1 or > 90 || _fileLogSizeLimitMb is < 1 or > 500))
            {
                Notifications.ShowError(Text["Settings_LogRetentionValidation"]);
                return;
            }

            if (_overrideLoggingDefaults && !ValidateApplicationLogFilters())
            {
                return;
            }

            TrustedProxySet knownProxies;
            try
            {
                knownProxies = TrustedProxySet.Parse([_knownProxies], Text["General_KnownProxiesLabel"]);
            }
            catch (InvalidOperationException ex)
            {
                Notifications.ShowError(ex.Message);
                return;
            }

            var wasAutoFetchEnabled = SettingsService.Settings.IsAutoFetchEnabled;
            var knownProxiesChanged = !SettingsService.Settings.KnownProxies.SequenceEqual(knownProxies.Values, StringComparer.OrdinalIgnoreCase);
            var uiCultureChanged = !string.Equals(WebSettingsService.Settings.UiCulture, _uiCulture, StringComparison.OrdinalIgnoreCase);
            var fileLoggingChanged = FileLoggingSettingsChanged();
            var applicationLoggingChanged = ApplicationLoggingSettingsChanged();
            var completingInitialSetup = _isInitialSetup;
            await SettingsService.UpdateAsync(settings =>
            {
                settings.AutoFetchInterval = _autoFetchEnabled ? TimeSpan.FromHours(24) : TimeSpan.Zero;
                settings.RefreshChannelsBeforeGuideFetch = _refreshChannelsBeforeGuideFetch;
                settings.EpgHistoryRetentionHours = _epgHistoryRetentionHours;
                if (!_autoFetchEnabled || !wasAutoFetchEnabled)
                {
                    settings.NextAutoFetchTime = null;
                }
                settings.Streaming = streamingConfiguration;
                settings.DeviceRefreshIntervalMinutes = _deviceRefreshIntervalMinutes;
                settings.TunerRefreshIntervalSeconds = _tunerRefreshIntervalSeconds;
                settings.AutoGenerateXmltv = _autoGenerateXmltv;
                settings.XmltvOutputPath = ReadXmltvOutputPath(streamingConfiguration) ?? _xmltvOutputPath.Trim();
                settings.TimeZoneId = _timeZoneId;
                settings.ProtectedContentMode = _protectedContentMode;
                settings.DisabledChannelMode = _disabledChannelMode;
                settings.MaximumConcurrentStreams = _maximumConcurrentStreams;
                settings.KnownProxies = knownProxies.Values;
            });
            await WebSettingsService.UpdateAsync(settings =>
            {
                settings.EnableNetworkDiscovery = _enableNetworkDiscovery;
                settings.ActiveStreamRefreshIntervalSeconds = _activeStreamRefreshIntervalSeconds;
                settings.Theme = _theme;
                settings.UiCulture = _uiCulture;
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
                    Services.GetRequiredService<LoggingRuntimeState>().ApplyApplicationFilter(WebSettingsService.Settings);
                }
                catch (Exception ex)
                {
                    applyErrors.Add(Text["Settings_ApplicationLogApplyFailure", ex.Message]);
                }
            }

            if (fileLoggingChanged)
            {
                try
                {
                    Services.GetRequiredService<LogFileService>().ApplySettings(WebSettingsService.Settings);
                }
                catch (Exception ex)
                {
                    applyErrors.Add(Text["Settings_FileLogApplyFailure", ex.Message]);
                }
            }

            // Apply theme immediately via JS
            await JS.SetThemeAsync(_theme);

            if (applyErrors.Count > 0)
            {
                Notifications.ShowError(Text["Settings_PartialSaveFailure", string.Join("; ", applyErrors)]);
                return;
            }

            Notifications.ShowSuccess(Text["Settings_SaveSuccess"]);
            foreach (var output in _topology.GuideOutputs)
            {
                foreach (var secret in output.GeneratedSecrets)
                {
                    Notifications.ShowSuccess(Text["Settings_SecretShownOnce", output.Name, secret.Key, secret.Value]);
                }
                output.GeneratedSecrets = new Dictionary<string, string>(StringComparer.Ordinal);
                output.AdapterConfiguration = streamingConfiguration.GuideOutputs.Single(candidate => candidate.Id == output.Id).Adapter.Configuration.Clone();
                foreach (var setting in output.AdapterSettings)
                {
                    setting.Action = AdapterSettingEditAction.Preserve;
                    setting.OriginalHasValue = setting.HasValue;
                }
            }

            if (completingInitialSetup)
            {
                var deviceStatuses = Services.GetRequiredService<IPhysicalDeviceStatusService>();
                await deviceStatuses.RefreshAsync(SettingsService.Settings.Streaming);
                var enabledDeviceIds = SettingsService.Settings.Streaming.PhysicalDevices
                    .Where(device => device.Enabled)
                    .Select(device => device.Id)
                    .ToHashSet();
                var unavailableDevices = deviceStatuses.Devices
                    .Where(device => enabledDeviceIds.Contains(device.Device.Id) && device.Error is not null)
                    .Select(device => device.Device.Name)
                    .ToArray();
                if (unavailableDevices.Length > 0)
                {
                    throw new InvalidOperationException($"The following physical devices could not be connected: {string.Join(", ", unavailableDevices)}");
                }

                await Services.GetRequiredService<IPhysicalLineupService>().RefreshAsync(SettingsService.Settings.Streaming);
                await WebSettingsService.UpdateAsync(settings => settings.IsSetupComplete = true);
                _isInitialSetup = false;
                if (knownProxiesChanged)
                {
                    await RequestRestartAsync("/dashboard");
                    return;
                }
                Navigation.NavigateTo("/dashboard", forceLoad: uiCultureChanged);
                return;
            }

            if (knownProxiesChanged)
            {
                await RequestRestartAsync("/settings#general");
                return;
            }

            if (uiCultureChanged)
            {
                Navigation.NavigateTo(Navigation.Uri, forceLoad: true);
            }
        }
        catch (Exception ex)
        {
            Notifications.ShowError(Text["Settings_SaveFailure", ex.Message]);
        }
        finally
        {
            _isSaving = false;
        }
    }

    private IReadOnlyCollection<int> GetManagementListenerPorts()
    {
        var ports = new HashSet<int>();
        var addresses = Services.GetService<IServer>()?.Features.Get<IServerAddressesFeature>()?.Addresses;
        if (addresses is not null)
        {
            foreach (var address in addresses)
            {
                if (Uri.TryCreate(address, UriKind.Absolute, out var uri))
                {
                    ports.Add(uri.Port);
                }
            }
        }
        if (Uri.TryCreate(Navigation.BaseUri, UriKind.Absolute, out var currentUri))
        {
            ports.Add(currentUri.Port);
        }
        return ports;
    }

    private void ResetToDefaults()
    {
        var defaults = new LineupSettings();
        var webDefaults = new WebSettings();
        _autoFetchEnabled = defaults.IsAutoFetchEnabled;
        _refreshChannelsBeforeGuideFetch = defaults.RefreshChannelsBeforeGuideFetch;
        _epgHistoryRetentionHours = defaults.EpgHistoryRetentionHours;
        _deviceRefreshIntervalMinutes = defaults.DeviceRefreshIntervalMinutes;
        _tunerRefreshIntervalSeconds = defaults.TunerRefreshIntervalSeconds;
        _activeStreamRefreshIntervalSeconds = webDefaults.ActiveStreamRefreshIntervalSeconds;
        _autoGenerateXmltv = defaults.AutoGenerateXmltv;
        _xmltvOutputPath = SettingsService.ConfiguredXmltvOutputPath;
        _theme = webDefaults.Theme;
        _timeZoneId = defaults.TimeZoneId;
        _uiCulture = UiLocalization.DefaultCulture;
        _knownProxies = string.Empty;
        _protectedContentMode = defaults.ProtectedContentMode;
        _disabledChannelMode = defaults.DisabledChannelMode;
        _maximumConcurrentStreams = defaults.MaximumConcurrentStreams;
        _redactApiClientAddresses = webDefaults.RedactApiClientAddresses;
        _redactApiDeviceAddresses = webDefaults.RedactApiDeviceAddresses;
        _redactApiUrls = webDefaults.RedactApiUrls;
        var startupFilter = LoggingRuntime?.StartupApplicationFilter ?? new ApplicationLogFilterSettings("Information", []);
        _overrideLoggingDefaults = false;
        _applicationLogLevel = startupFilter.DefaultLevel;
        _logCategoryOverrides = startupFilter.CategoryLevels.Select(CloneLogCategory).ToList();
        _enableFileLogging = webDefaults.EnableFileLogging;
        _fileLogLevel = webDefaults.FileLogLevel;
        _fileLogRetentionDays = webDefaults.FileLogRetentionDays;
        _fileLogSizeLimitMb = webDefaults.FileLogSizeLimitMb;
        _enableNetworkDiscovery = webDefaults.EnableNetworkDiscovery;
        _topology = _topologyEditor.Edit(defaults.Streaming, guideOutputDrivers: GuideOutputDrivers);
        Notifications.ShowSuccess(Text["Settings_ResetDefaultsSuccess"]);
        SelectTab(SettingsTab.General);
    }

    private LoggingRuntimeState? LoggingRuntime => Services.GetService<LoggingRuntimeState>();

    private static IReadOnlyList<string> ApplicationLogLevels => WebApplicationLogLevelNames.All;

    private bool IsStartupLogCategory(string category) => _startupLogCategories.Contains(category);

    private void AddLogCategoryOverride() => _logCategoryOverrides.Add(new WebLogCategoryLevelSetting());

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
            Notifications.ShowError(Text["Settings_LogCategoryValidation"]);
            return false;
        }

        if (_logCategoryOverrides.GroupBy(level => level.Category, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
        {
            Notifications.ShowError(Text["Settings_LogCategoryUniqueValidation"]);
            return false;
        }

        return true;
    }

    private bool ApplicationLoggingSettingsChanged()
    {
        var persisted = WebSettingsService.Settings;
        if (_overrideLoggingDefaults != persisted.OverrideLoggingDefaults)
        {
            return true;
        }

        return _overrideLoggingDefaults &&
            (!string.Equals(_applicationLogLevel, persisted.ApplicationLogLevel, StringComparison.OrdinalIgnoreCase) ||
                !LogCategorySettingsEqual(_logCategoryOverrides, persisted.LogCategoryOverrides));
    }

    private static bool LogCategorySettingsEqual(IEnumerable<WebLogCategoryLevelSetting> left, IEnumerable<WebLogCategoryLevelSetting> right)
    {
        var leftLevels = left.GroupBy(level => level.Category, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.Last().Level, StringComparer.OrdinalIgnoreCase);
        var rightLevels = right.GroupBy(level => level.Category, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.Last().Level, StringComparer.OrdinalIgnoreCase);
        return leftLevels.Count == rightLevels.Count &&
            leftLevels.All(pair => rightLevels.TryGetValue(pair.Key, out var level) && string.Equals(pair.Value, level, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<WebLogCategoryLevelSetting> MergeLogCategorySettings(IEnumerable<WebLogCategoryLevelSetting> startupLevels, IEnumerable<WebLogCategoryLevelSetting> overrideLevels)
    {
        var merged = startupLevels
            .Concat(overrideLevels)
            .GroupBy(level => level.Category, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToArray();
        return merged;
    }

    private static WebLogCategoryLevelSetting CloneLogCategory(WebLogCategoryLevelSetting level) => new() { Category = level.Category, Level = level.Level };

    private void RefreshRetainedLogFiles() => _retainedLogFiles = Services.GetService<LogFileService>()?.GetFiles() ?? [];

    private bool FileLoggingSettingsChanged()
    {
        var persisted = WebSettingsService.Settings;
        return _enableFileLogging != persisted.EnableFileLogging ||
            !string.Equals(_fileLogLevel, persisted.FileLogLevel, StringComparison.OrdinalIgnoreCase) ||
            _fileLogRetentionDays != persisted.FileLogRetentionDays ||
            _fileLogSizeLimitMb != persisted.FileLogSizeLimitMb;
    }

    private void UndoUnsavedChanges()
    {
        LoadCurrentSettings();
        _showDirectoryBrowser = false;
        Notifications.ShowSuccess(Text["Settings_UndoSuccess"]);
    }

    private void ShowFactoryResetConfirmation()
    {
        _showFactoryResetConfirmation = true;
        _factoryResetConfirmed = false;
    }

    private void CancelFactoryReset()
    {
        _showFactoryResetConfirmation = false;
        _factoryResetConfirmed = false;
    }

    private bool CanFactoryReset => _factoryResetConfirmed;

    private async Task RestartLineup()
    {
        await RequestRestartAsync("/settings#reset");
    }

    private async Task RequestRestartAsync(string destination)
    {
        if (_isRestarting)
        {
            return;
        }
        _isRestarting = true;
        await InvokeAsync(StateHasChanged);
        try
        {
            await JS.BeginLineupRestartWatchAsync(destination);
            await Services.GetRequiredService<IApplicationRestartService>().RestartAsync();
        }
        catch (Exception ex)
        {
            Notifications.ShowError(Text["Settings_RestartFailure", ex.Message]);
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
        await InvokeAsync(StateHasChanged);
        try
        {
            await JS.BeginLineupFactoryResetWatchAsync();
            await Services.GetRequiredService<IFactoryResetService>().RequestResetAsync();
        }
        catch (Exception ex)
        {
            Notifications.ShowError(Text["Settings_FactoryResetFailure", ex.Message]);
            _isFactoryResetting = false;
        }
    }

    private async Task Cancel()
    {
        if (_isInitialSetup)
        {
            // Mark setup complete even if user skips — don't redirect here again
            await WebSettingsService.UpdateAsync(s => s.IsSetupComplete = true);
        }
        Navigation.NavigateTo("/dashboard");
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
            Notifications.ShowSuccess(
                deleted == 0
                    ? Text["Settings_NoFileLogs"]
                    : Text[deleted == 1 ? "Settings_DeleteFileLogSingular" : "Settings_DeleteFileLogPlural", deleted]);
        }
        catch (Exception ex)
        {
            Notifications.ShowError(Text["Settings_DeleteFileLogsFailure", ex.Message]);
        }
        finally
        {
            _isDeletingLogs = false;
        }
    }

    internal void ResetXmltvOutputPath()
    {
        if (SelectedGuideOutput is { IsBuiltIn: true } output)
        {
            output.OutputPath = SettingsService.ConfiguredXmltvOutputPath;
        }
        _xmltvOutputPath = SettingsService.ConfiguredXmltvOutputPath;
    }

    internal bool XmltvOutputPathDiffersFromConfigured()
    {
        try
        {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var path = SelectedGuideOutput?.OutputPath ?? _xmltvOutputPath;
            return !string.Equals(Path.GetFullPath(path), Path.GetFullPath(SettingsService.ConfiguredXmltvOutputPath), comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return true;
        }
    }

    private bool ValidateGuideOutputPaths()
    {
        foreach (var output in _topology.GuideOutputs.Where(IsXmltvGuideOutput))
        {
            if (string.IsNullOrWhiteSpace(output.OutputPath))
            {
                Notifications.ShowError(Text["Settings_GuideOutputDestinationRequired", output.Name]);
                return false;
            }

            try
            {
                _ = Path.GetFullPath(output.OutputPath);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                Notifications.ShowError(Text["Settings_GuideOutputDestinationInvalid", output.Name]);
                return false;
            }
        }
        return true;
    }

    internal void BrowseDirectory()
    {
        _showDirectoryBrowser = true;
        _directoryError = null;
        try
        {
            var resolvedPath = FileSystem.ResolvePath(SelectedGuideOutput?.OutputPath ?? _xmltvOutputPath);
            var directory = Path.GetDirectoryName(resolvedPath);
            if (!string.IsNullOrEmpty(directory) && FileSystem.DirectoryExists(directory))
            {
                _currentDirectory = directory;
                LoadDirectories();
                return;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            _directoryError = Text["Settings_CurrentGuidePathInvalid"];
        }

        _currentDirectory = FileSystem.GetCurrentDirectory();
        LoadDirectories();
    }

    internal void CloseDirectoryBrowser()
    {
        _showDirectoryBrowser = false;
    }

    private void LoadDirectories()
    {
        _directoryError = null;
        try
        {
            _directories = FileSystem.GetDirectories(_currentDirectory).ToArray();
        }
        catch (UnauthorizedAccessException)
        {
            _directoryError = Text["Settings_DirectoryAccessDenied"];
            _directories = [];
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            _directoryError = Text["Settings_DirectoryBrowseFailure", ex.Message];
            _directories = [];
        }
    }

    internal void NavigateToDirectory(string path)
    {
        _currentDirectory = path;
        LoadDirectories();
    }

    internal void NavigateUp()
    {
        var parent = FileSystem.GetParent(_currentDirectory);
        if (parent != null)
        {
            _currentDirectory = parent;
            LoadDirectories();
        }
    }

    internal bool CanNavigateUp => !string.IsNullOrEmpty(_currentDirectory) && FileSystem.GetParent(_currentDirectory) != null;

    internal void SelectXmltvPath()
    {
        GuideOutputDirectoryPath = _currentDirectory;
        _showDirectoryBrowser = false;
    }

    internal string? ResolvedXmltvOutputPath
    {
        get
        {
            var path = SelectedGuideOutput?.OutputPath ?? _xmltvOutputPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            try
            {
                return FileSystem.ResolvePath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }
        }
    }

    internal void ShowPhysicalDeviceTypePicker()
    {
        if (_availablePhysicalTunerDrivers.Count == 0)
        {
            Notifications.ShowError(Text["Settings_NoPhysicalDrivers"]);
            return;
        }

        _showPhysicalDeviceTypePicker = true;
    }

    internal void AddPhysicalDevice(IPhysicalTunerDriver driver)
    {
        var device = _topologyEditor.AddPhysicalDevice(_topology, driver);
        SelectedPhysicalDeviceId = device.Id;
        _showPhysicalDeviceTypePicker = false;
    }

    internal void RemovePhysicalDevice(PhysicalDeviceId id)
    {
        _topologyEditor.RemovePhysicalDevice(_topology, id);
        _physicalDeviceProbeStates.Remove(id);
        if (_selectedPhysicalDeviceId == id)
        {
            SelectedPhysicalDeviceId = null;
        }
    }

    internal string GetPhysicalDeviceTypeName(PhysicalDeviceSettingsModel device) =>
        PhysicalTunerDrivers.FirstOrDefault(driver => string.Equals(driver.Kind, device.Kind, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? device.Kind;

    internal bool SupportsPhysicalDeviceProbe(PhysicalDeviceSettingsModel device) =>
        FindPhysicalTunerDriver(device) is IPhysicalDeviceProbe;

    internal PhysicalDeviceProbeState? GetPhysicalDeviceProbeState(PhysicalDeviceSettingsModel device) =>
        _physicalDeviceProbeStates.GetValueOrDefault(device.Id);

    internal async Task TestAndIdentifyPhysicalDeviceAsync(PhysicalDeviceSettingsModel device)
    {
        var driver = FindPhysicalTunerDriver(device);
        if (driver is not IPhysicalDeviceProbe probe)
        {
            Notifications.ShowError(Text["Settings_ConnectionTestUnsupported", GetPhysicalDeviceTypeName(device)]);
            return;
        }

        CancelPhysicalDeviceProbe();
        var cancellation = new CancellationTokenSource();
        _physicalDeviceProbeCancellation = cancellation;
        _physicalDeviceProbeStates[device.Id] = new PhysicalDeviceProbeState { IsTesting = true };

        try
        {
            var configuration = _topologyEditor.BuildPhysicalAdapter(device);
            var validationErrors = driver.Validate(configuration);
            if (validationErrors.Count > 0)
            {
                throw new ArgumentException(string.Join(" ", validationErrors));
            }

            var result = await probe.ProbeAsync(configuration, cancellation.Token);
            if (!cancellation.IsCancellationRequested)
            {
                _physicalDeviceProbeStates[device.Id] = new PhysicalDeviceProbeState { Result = result };
                Notifications.ShowSuccess(Text["Settings_ConnectionTestSuccess", GetPhysicalDeviceTypeName(device)]);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _physicalDeviceProbeStates[device.Id] = new PhysicalDeviceProbeState { Error = exception.Message };
            Notifications.ShowError(Text["Settings_ConnectionTestFailure", exception.Message]);
        }
        finally
        {
            if (ReferenceEquals(_physicalDeviceProbeCancellation, cancellation))
            {
                _physicalDeviceProbeCancellation = null;
            }
            cancellation.Dispose();
        }
    }

    internal void UseDiscoveredPhysicalDeviceName(PhysicalDeviceSettingsModel device)
    {
        var suggestedName = GetPhysicalDeviceProbeState(device)?.Result?.SuggestedName;
        if (!string.IsNullOrWhiteSpace(suggestedName))
        {
            device.Name = suggestedName.Trim();
        }
    }

    internal void ClearPhysicalDeviceProbeState(PhysicalDeviceSettingsModel device)
    {
        if (GetPhysicalDeviceProbeState(device)?.IsTesting == true)
        {
            CancelPhysicalDeviceProbe();
        }
        _physicalDeviceProbeStates.Remove(device.Id);
    }

    private void ClearPhysicalDeviceProbeStates()
    {
        CancelPhysicalDeviceProbe();
        _physicalDeviceProbeStates.Clear();
    }

    private void CancelPhysicalDeviceProbe()
    {
        var cancellation = _physicalDeviceProbeCancellation;
        _physicalDeviceProbeCancellation = null;
        cancellation?.Cancel();
    }

    private IPhysicalTunerDriver? FindPhysicalTunerDriver(PhysicalDeviceSettingsModel device) =>
        PhysicalTunerDrivers.FirstOrDefault(driver => string.Equals(driver.Kind, device.Kind, StringComparison.OrdinalIgnoreCase));

    internal void ShowVirtualDeviceTypePicker()
    {
        if (_availableVirtualTunerDrivers.Count == 0)
        {
            Notifications.ShowError(Text["Settings_NoVirtualDrivers"]);
            return;
        }

        _showVirtualDeviceTypePicker = true;
    }

    internal void AddVirtualDevice(IVirtualTunerDriver driver)
    {
        var device = _topologyEditor.AddVirtualDevice(_topology, driver);
        SelectedVirtualDeviceId = device.Id;
        _showVirtualDeviceTypePicker = false;
    }

    internal string GetVirtualDeviceTypeName(VirtualDeviceSettingsModel device) =>
        VirtualTunerDrivers.FirstOrDefault(driver => string.Equals(driver.Kind, device.Kind, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? device.Kind;

    internal void ShowGuideSourceTypePicker()
    {
        if (!_availableGuideSourceDrivers.Any(CanAddGuideSource))
        {
            Notifications.ShowError(Text["Settings_NoGuideSourceDrivers"]);
            return;
        }

        _showGuideSourceTypePicker = true;
    }

    internal void AddGuideSource(IGuideSourceDriver driver)
    {
        var source = _topologyEditor.AddGuideSource(_topology, driver);
        SelectedGuideSourceId = source.Id;
        _showGuideSourceTypePicker = false;
    }

    internal void RemoveGuideSource(GuideSourceId id)
    {
        _topologyEditor.RemoveGuideSource(_topology, id);
        if (_selectedGuideSourceId == id)
        {
            SelectedGuideSourceId = null;
        }
    }

    internal string GetGuideSourceTypeName(GuideSourceSettingsModel source) =>
        GuideSourceDrivers.FirstOrDefault(driver => string.Equals(driver.Kind, source.Kind, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? source.Kind;

    internal bool IsGuideCompatible(GuideSourceSettingsModel source, PhysicalDeviceSettingsModel device) =>
        GuideSourceDrivers.FirstOrDefault(driver => string.Equals(driver.Kind, source.Kind, StringComparison.OrdinalIgnoreCase))
            ?.SupportsPhysicalAdapter(device.Kind) == true;

    internal bool CanAddGuideSource(IGuideSourceDriver driver) =>
        _topology.PhysicalDevices.Any(device => device.Enabled && driver.SupportsPhysicalAdapter(device.Kind));

    internal void ShowGuideOutputTypePicker()
    {
        if (_availableGuideOutputDrivers.Count == 0)
        {
            Notifications.ShowError(Text["Settings_NoGuideOutputDrivers"]);
            return;
        }

        _showGuideOutputTypePicker = true;
    }

    internal void AddGuideOutput(IGuideOutputDriver driver)
    {
        var defaultOutputDirectory = Path.GetDirectoryName(Path.GetFullPath(_xmltvOutputPath))
            ?? Path.GetFullPath(AppConstants.DefaultGuideOutputDirectoryPath);
        var output = _topologyEditor.AddGuideOutput(_topology, driver, defaultOutputDirectory);
        SelectedGuideOutputId = output.Id;
        _showGuideOutputTypePicker = false;
    }

    internal void UpdateGuideOutputKey(GuideOutputSettingsModel output, string value)
    {
        output.Key = string.IsNullOrWhiteSpace(value) ? string.Empty : GuideOutputKey.Create(value);
        if (IsXmltvGuideOutput(output) && !output.IsBuiltIn && !string.IsNullOrWhiteSpace(output.Key))
        {
            var directory = Path.GetDirectoryName(output.OutputPath);
            output.OutputPath = string.IsNullOrEmpty(directory)
                ? $"lineup-{output.Key}.xml"
                : Path.Combine(directory, $"lineup-{output.Key}.xml");
        }
    }

    internal void RemoveGuideOutput(GuideOutputId id)
    {
        _topologyEditor.RemoveGuideOutput(_topology, id);
        if (_selectedGuideOutputId == id)
        {
            SelectedGuideOutputId = _topology.GuideOutputs.FirstOrDefault(output => output.IsBuiltIn)?.Id
                ?? _topology.GuideOutputs.FirstOrDefault()?.Id;
        }
    }

    internal string GetGuideOutputTypeName(GuideOutputSettingsModel output) =>
        GuideOutputDrivers.FirstOrDefault(driver => string.Equals(driver.Kind, output.Kind, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? output.Kind;

    internal static bool IsXmltvGuideOutput(GuideOutputSettingsModel output) =>
        IsXmltvGuideOutputKind(output.Kind);

    private static bool IsXmltvGuideOutputKind(string kind) =>
        string.Equals(kind, XmltvGuideOutputDriver.AdapterKind, StringComparison.OrdinalIgnoreCase);

    internal static bool IsGuideOutputSettingVisible(
        GuideOutputSettingsModel output,
        AdapterSettingFieldSettingsModel setting) =>
        setting.Descriptor.VisibleWhen.All(condition =>
        {
            var actual = output.AdapterSettings
                .FirstOrDefault(candidate => string.Equals(candidate.Descriptor.Key, condition.FieldKey, StringComparison.Ordinal))
                ?.Value?.ToString();
            var equals = string.Equals(actual, condition.Value, StringComparison.OrdinalIgnoreCase);
            return condition.Operator == AdapterSettingConditionOperator.Equals ? equals : !equals;
        });

    internal static string GetGuideOutputSettingValue(AdapterSettingFieldSettingsModel setting)
    {
        if (setting.Value is not JsonElement value || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return string.Empty;
        }
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString();
    }

    internal static bool GetGuideOutputBooleanSettingValue(AdapterSettingFieldSettingsModel setting) =>
        setting.Value is JsonElement value &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        value.GetBoolean();

    internal static void UpdateGuideOutputBooleanSetting(AdapterSettingFieldSettingsModel setting, bool value)
    {
        setting.Value = JsonSerializer.SerializeToElement(value);
        setting.HasValue = true;
        setting.Action = AdapterSettingEditAction.Set;
    }

    internal static void UpdateGuideOutputSetting(AdapterSettingFieldSettingsModel setting, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (setting.Descriptor.Kind == AdapterSettingKind.Secret && setting.OriginalHasValue)
            {
                setting.Value = null;
                setting.HasValue = true;
                setting.Action = AdapterSettingEditAction.Preserve;
                return;
            }

            setting.Value = null;
            setting.HasValue = false;
            setting.Action = AdapterSettingEditAction.Clear;
            return;
        }

        setting.Value = setting.Descriptor.Kind == AdapterSettingKind.Integer && long.TryParse(value, out var integer)
            ? JsonSerializer.SerializeToElement(integer)
            : JsonSerializer.SerializeToElement(value);
        setting.HasValue = true;
        setting.Action = AdapterSettingEditAction.Set;
    }

    internal static void GenerateGuideOutputSetting(AdapterSettingFieldSettingsModel setting)
    {
        setting.Value = null;
        setting.HasValue = true;
        setting.Action = AdapterSettingEditAction.Generate;
    }

    internal static void ClearGuideOutputSetting(AdapterSettingFieldSettingsModel setting)
    {
        setting.Value = null;
        setting.HasValue = false;
        setting.Action = AdapterSettingEditAction.Clear;
    }

    private static string? ReadXmltvOutputPath(StreamingConfiguration configuration)
    {
        var builtIn = configuration.GuideOutputs.FirstOrDefault(output =>
            output.IsBuiltIn &&
            string.Equals(output.Adapter.Kind, XmltvGuideOutputDriver.AdapterKind, StringComparison.OrdinalIgnoreCase));
        return builtIn?.Adapter.Configuration.ValueKind == JsonValueKind.Object &&
            builtIn.Adapter.Configuration.TryGetProperty("OutputPath", out var path)
                ? path.GetString()
                : null;
    }

    internal void SetGuideOutputVirtualDevice(GuideOutputSettingsModel output, VirtualDeviceId id, bool selected)
    {
        if (selected)
        {
            output.SelectedVirtualDeviceIds.Add(id);
        }
        else
        {
            output.SelectedVirtualDeviceIds.Remove(id);
        }
    }

    internal void SetGuideOutputGuideSource(GuideOutputSettingsModel output, GuideSourceId id, bool selected)
    {
        if (selected)
        {
            output.SelectedGuideSourceIds.Add(id);
        }
        else
        {
            output.SelectedGuideSourceIds.Remove(id);
        }
    }

    internal bool IsVirtualCompatible(VirtualDeviceSettingsModel device, PhysicalDeviceSettingsModel physical) =>
        VirtualTunerDrivers.FirstOrDefault(driver => string.Equals(driver.Kind, device.Kind, StringComparison.OrdinalIgnoreCase))
            ?.SupportsPhysicalAdapter(physical.Kind) == true;

    internal sealed record PhysicalDeviceProbeState
    {
        public bool IsTesting { get; init; }

        public PhysicalDeviceProbeResult? Result { get; init; }

        public string? Error { get; init; }
    }

    internal void ImportLineupLinkBundle(PhysicalDeviceSettingsModel device)
    {
        try
        {
            var bundle = LineupLinkConnectionBundleCodec.Deserialize(device.LinkConnectionBundle);
            device.LinkBaseUrl = bundle.BaseUrl ?? string.Empty;
            device.LinkVirtualDeviceId = bundle.VirtualDeviceId;
            device.LinkAccessToken = bundle.AccessToken;
            device.LinkTransportSecurity = bundle.TransportSecurity;
            device.LinkCertificateSha256 = bundle.CertificateSha256 ?? string.Empty;
            ClearPhysicalDeviceProbeState(device);
            Notifications.ShowSuccess(Text["Settings_LineupLinkBundleImported"]);
        }
        catch (ArgumentException exception)
        {
            Notifications.ShowError(exception.Message);
        }
    }

    internal void CreateLineupLinkAccessGrant(VirtualDeviceSettingsModel device)
    {
        DateTime? expiration = device.LinkNewAccessGrantExpiresAtLocal.HasValue
            ? DateTime.SpecifyKind(device.LinkNewAccessGrantExpiresAtLocal.Value, DateTimeKind.Local).ToUniversalTime()
            : null;
        var mutation = LineupLinkAccessGrantManager.Create(
            BuildLineupLinkGrantConfiguration(device),
            device.LinkNewAccessGrantName,
            expiration,
            DateTime.UtcNow);
        if (mutation.Configuration is null || mutation.GeneratedToken is null)
        {
            Notifications.ShowError(string.Join(" ", mutation.Errors));
            return;
        }

        var grant = mutation.Configuration.AccessGrants.Single(candidate => candidate.Id == mutation.Grant!.Id);
        var model = LineupLinkAccessGrantSettingsModel.Create(grant, mutation.GeneratedToken);
        device.LinkAccessGrants.Add(model);
        device.LinkNewAccessGrantName = string.Empty;
        device.LinkNewAccessGrantExpiresAtLocal = null;
        SetLineupLinkBundle(device, model, redactUnavailableToken: false);
        Notifications.ShowSuccess(Text["Settings_LineupLinkGrantCreated"]);
    }

    internal void RotateLineupLinkAccessGrant(
        VirtualDeviceSettingsModel device,
        LineupLinkAccessGrantSettingsModel grant)
    {
        var mutation = LineupLinkAccessGrantManager.Rotate(
            BuildLineupLinkGrantConfiguration(device),
            grant.Id,
            DateTime.UtcNow);
        if (mutation.Configuration is null || mutation.GeneratedToken is null)
        {
            Notifications.ShowError(string.Join(" ", mutation.Errors));
            return;
        }

        ReplaceLineupLinkAccessGrant(device, mutation.Configuration.AccessGrants.Single(candidate => candidate.Id == grant.Id), mutation.GeneratedToken);
        var updated = device.LinkAccessGrants.Single(candidate => candidate.Id == grant.Id);
        SetLineupLinkBundle(device, updated, redactUnavailableToken: false);
        Notifications.ShowSuccess(Text["Settings_LineupLinkGrantRotated"]);
    }

    internal void RevokeLineupLinkAccessGrant(
        VirtualDeviceSettingsModel device,
        LineupLinkAccessGrantSettingsModel grant)
    {
        var mutation = LineupLinkAccessGrantManager.Revoke(
            BuildLineupLinkGrantConfiguration(device),
            grant.Id,
            DateTime.UtcNow);
        if (mutation.Configuration is null)
        {
            Notifications.ShowError(string.Join(" ", mutation.Errors));
            return;
        }

        ReplaceLineupLinkAccessGrant(device, mutation.Configuration.AccessGrants.Single(candidate => candidate.Id == grant.Id), null);
        Notifications.ShowSuccess(Text["Settings_LineupLinkGrantRevoked"]);
    }

    internal void PurgeLineupLinkAccessGrant(
        VirtualDeviceSettingsModel device,
        LineupLinkAccessGrantSettingsModel grant)
    {
        var mutation = LineupLinkAccessGrantManager.Purge(
            BuildLineupLinkGrantConfiguration(device),
            grant.Id);
        if (mutation.Configuration is null)
        {
            Notifications.ShowError(string.Join(" ", mutation.Errors));
            return;
        }

        device.LinkAccessGrants.RemoveAll(candidate => candidate.Id == grant.Id);
        Notifications.ShowSuccess(Text["Settings_LineupLinkGrantPurged"]);
    }

    internal async Task ExportLineupLinkBundleAsync(
        VirtualDeviceSettingsModel device,
        LineupLinkAccessGrantSettingsModel grant)
    {
        if (!TryCreateLineupLinkBundle(device, grant, redactUnavailableToken: false, out var bundle))
        {
            return;
        }

        try
        {
            grant.ConnectionBundle = bundle;
            grant.ConnectionBundleExpanded = false;
            await JS.DownloadTextFileAsync(
                $"lineup-link-{device.Id.Value:D}-{grant.Id:D}.json",
                "application/json",
                bundle);
            Notifications.ShowSuccess(Text["Settings_LineupLinkBundleDownloaded"]);
        }
        catch (JSDisconnectedException)
        {
            Notifications.ShowError(Text["Settings_LineupLinkDownloadDisconnected"]);
        }
        catch (JSException)
        {
            Notifications.ShowError(Text["Settings_LineupLinkDownloadFailure"]);
        }
    }

    internal async Task CopyLineupLinkBundleAsync(
        VirtualDeviceSettingsModel device,
        LineupLinkAccessGrantSettingsModel grant)
    {
        if (!TryCreateLineupLinkBundle(device, grant, redactUnavailableToken: false, out var bundle))
        {
            return;
        }

        try
        {
            grant.ConnectionBundle = bundle;
            await JS.CopyTextAsync(bundle);
            Notifications.ShowSuccess(Text["Settings_LineupLinkBundleCopied"]);
        }
        catch (JSDisconnectedException)
        {
            Notifications.ShowError(Text["Settings_LineupLinkCopyDisconnected"]);
        }
        catch (JSException)
        {
            Notifications.ShowError(Text["Settings_LineupLinkCopyFailure"]);
        }
    }

    internal void ToggleLineupLinkBundle(
        VirtualDeviceSettingsModel device,
        LineupLinkAccessGrantSettingsModel grant)
    {
        if (grant.ConnectionBundleExpanded)
        {
            grant.ConnectionBundleExpanded = false;
            return;
        }

        SetLineupLinkBundle(device, grant, redactUnavailableToken: true);
    }

    private static LineupLinkVirtualConfiguration BuildLineupLinkGrantConfiguration(VirtualDeviceSettingsModel device) =>
        new()
        {
            InstanceId = device.LinkInstanceId,
            AccessGrants = device.LinkAccessGrants.Select(grant => grant.ToPersisted()).ToArray()
        };

    private static void ReplaceLineupLinkAccessGrant(
        VirtualDeviceSettingsModel device,
        LineupLinkAccessGrant persisted,
        string? rawToken)
    {
        var index = device.LinkAccessGrants.FindIndex(grant => grant.Id == persisted.Id);
        device.LinkAccessGrants[index] = LineupLinkAccessGrantSettingsModel.Create(persisted, rawToken);
    }

    private void SetLineupLinkBundle(
        VirtualDeviceSettingsModel device,
        LineupLinkAccessGrantSettingsModel grant,
        bool redactUnavailableToken)
    {
        if (TryCreateLineupLinkBundle(device, grant, redactUnavailableToken, out var bundle))
        {
            grant.ConnectionBundle = bundle;
            grant.ConnectionBundleExpanded = true;
        }
    }

    private bool TryCreateLineupLinkBundle(
        VirtualDeviceSettingsModel device,
        LineupLinkAccessGrantSettingsModel grant,
        bool redactUnavailableToken,
        out string bundle)
    {
        bundle = string.Empty;
        if (string.IsNullOrWhiteSpace(grant.RawToken) && !redactUnavailableToken)
        {
            Notifications.ShowError(Text["Settings_LineupLinkRawTokenUnavailable"]);
            return false;
        }

        try
        {
            var connectionBundle = new LineupLinkConnectionBundle
            {
                BaseUrl = NullIfWhiteSpace(device.UseDedicatedListener ? device.EffectivePublicBaseUri : device.LinkPublicBaseUrl),
                VirtualDeviceId = device.Id.Value,
                AccessToken = string.IsNullOrWhiteSpace(grant.RawToken)
                    ? LineupLinkConnectionBundleCodec.RedactedAccessToken
                    : grant.RawToken,
                TransportSecurity = device.EffectiveLineupLinkEncryptionRequired ? LineupLinkTransportSecurity.Https : LineupLinkTransportSecurity.Http,
                CertificateSha256 = device.EffectiveLineupLinkEncryptionRequired && !string.IsNullOrWhiteSpace(device.LinkCertificateSha256)
                    ? device.LinkCertificateSha256
                    : null
            };
            bundle = string.IsNullOrWhiteSpace(grant.RawToken)
                ? LineupLinkConnectionBundleCodec.SerializeRedacted(connectionBundle)
                : LineupLinkConnectionBundleCodec.Serialize(connectionBundle);
            return true;
        }

        catch (ArgumentException exception)
        {
            Notifications.ShowError(exception.Message);
            return false;
        }
    }

    private static string? NullIfWhiteSpace(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    internal void OnTranscoderSettingsChanged() => StateHasChanged();

    internal void RemoveVirtualDevice(VirtualDeviceId id)
    {
        _topologyEditor.RemoveVirtualDevice(_topology, id);
        if (_selectedVirtualDeviceId == id)
        {
            SelectedVirtualDeviceId = null;
        }
    }

    internal void SetPhysicalSubscription(VirtualDeviceSettingsModel device, PhysicalDeviceId id, bool selected)
    {
        if (selected)
        {
            device.SelectedDeviceIds.Add(id);
        }
        else
        {
            device.SelectedDeviceIds.Remove(id);
        }
    }

    internal void SetGuideSourceSubscription(GuideSourceSettingsModel source, PhysicalDeviceId id, bool selected)
    {
        if (selected)
        {
            source.SelectedDeviceIds.Add(id);
        }
        else
        {
            source.SelectedDeviceIds.Remove(id);
        }
    }

}
