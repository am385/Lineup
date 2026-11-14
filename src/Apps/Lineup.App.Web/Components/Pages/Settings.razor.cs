using System.Text.Json;
using Lineup.Core.Common.Configuration;
using Lineup.Plugin.LineupLink.Protocol;
using Lineup.Plugin.LineupLink.Virtual;
using Lineup.Plugin.GuideOutputs.Xmltv.Plugin;
using Lineup.App.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.JSInterop;

namespace Lineup.App.Web.Components.Pages;

/// <summary>
/// Displays and edits persisted Lineup application settings.
/// </summary>
public partial class Settings : IDisposable
{
    [Inject]
    private ILineupSettingsService SettingsService { get; set; } = default!;

    [Inject]
    private LineupStartupState StartupState { get; set; } = default!;

    private readonly IFileSystemBrowser _fileSystem = new FileSystemBrowser();

    [Inject]
    private NavigationManager Navigation { get; set; } = default!;

    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    [Inject]
    private ITimeZoneService TimeZoneService { get; set; } = default!;

    [Inject]
    private IServiceProvider Services { get; set; } = default!;

    private IWebSettingsService? _webSettingsService;

    private IWebSettingsService WebSettingsService => _webSettingsService ??= Services.GetService<IWebSettingsService>() ??
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
    private readonly CancellationTokenSource _lifetime = new();
    private IStreamingTopologyRuntime? _topologyRuntime;
    private string _coreRevision = "";
    private string _webRevision = "";
    private bool _restartRequired;
    private bool _cultureReloadRequired;
    private bool _fileLoggingApplyPending;
    private bool _applicationLoggingApplyPending;
    private bool _disposed;

    /// <summary>Gets the topology.</summary>
    internal TopologySettingsModel Topology => _topology;
    /// <summary>Gets the available physical tuner drivers.</summary>
    internal IReadOnlyList<IPhysicalTunerDriver> AvailablePhysicalTunerDrivers => _availablePhysicalTunerDrivers;
    /// <summary>Gets the available virtual tuner drivers.</summary>
    internal IReadOnlyList<IVirtualTunerDriver> AvailableVirtualTunerDrivers => _availableVirtualTunerDrivers;
    /// <summary>Gets the available guide source drivers.</summary>
    internal IReadOnlyList<IGuideSourceDriver> AvailableGuideSourceDrivers => _availableGuideSourceDrivers;
    /// <summary>Gets the available guide output drivers.</summary>
    internal IReadOnlyList<IGuideOutputDriver> AvailableGuideOutputDrivers => _availableGuideOutputDrivers;
    /// <summary>Gets the configured XMLTV output path.</summary>
    internal string ConfiguredXmltvOutputPath => SettingsService.ConfiguredXmltvOutputPath;
    /// <summary>Gets or sets the auto fetch enabled.</summary>
    internal bool AutoFetchEnabled { get => _autoFetchEnabled; set => _autoFetchEnabled = value; }
    /// <summary>Gets or sets the refresh channels before guide fetch.</summary>
    internal bool RefreshChannelsBeforeGuideFetch { get => _refreshChannelsBeforeGuideFetch; set => _refreshChannelsBeforeGuideFetch = value; }
    /// <summary>Gets or sets the epg history retention hours.</summary>
    internal int EpgHistoryRetentionHours { get => _epgHistoryRetentionHours; set => _epgHistoryRetentionHours = value; }
    /// <summary>Gets or sets the device refresh interval minutes.</summary>
    internal int DeviceRefreshIntervalMinutes { get => _deviceRefreshIntervalMinutes; set => _deviceRefreshIntervalMinutes = value; }
    /// <summary>Gets or sets the tuner refresh interval seconds.</summary>
    internal int TunerRefreshIntervalSeconds { get => _tunerRefreshIntervalSeconds; set => _tunerRefreshIntervalSeconds = value; }
    /// <summary>Gets or sets the auto generate XMLTV.</summary>
    internal bool AutoGenerateXmltv { get => _autoGenerateXmltv; set => _autoGenerateXmltv = value; }
    /// <summary>Gets or sets the XMLTV output path.</summary>
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
    /// <summary>Gets or sets the guide output directory path.</summary>
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
                return Path.GetDirectoryName(_fileSystem.ResolvePath(path)) ?? string.Empty;
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
    /// <summary>Gets or sets the enable network discovery.</summary>
    internal bool EnableNetworkDiscovery { get => _enableNetworkDiscovery; set => _enableNetworkDiscovery = value; }
    /// <summary>Gets or sets the show directory browser.</summary>
    internal bool ShowDirectoryBrowser { get => _showDirectoryBrowser; set => _showDirectoryBrowser = value; }
    /// <summary>Gets the current directory.</summary>
    internal string CurrentDirectory => _currentDirectory;
    /// <summary>Gets the directories.</summary>
    internal IReadOnlyList<string> Directories => _directories;
    /// <summary>Gets the directory error.</summary>
    internal string? DirectoryError => _directoryError;
    /// <summary>Gets or sets the show physical device type picker value.</summary>
    internal bool ShowPhysicalDeviceTypePickerValue { get => _showPhysicalDeviceTypePicker; set => _showPhysicalDeviceTypePicker = value; }
    /// <summary>Gets or sets the show virtual device type picker value.</summary>
    internal bool ShowVirtualDeviceTypePickerValue { get => _showVirtualDeviceTypePicker; set => _showVirtualDeviceTypePicker = value; }
    /// <summary>Gets or sets the show guide source type picker value.</summary>
    internal bool ShowGuideSourceTypePickerValue { get => _showGuideSourceTypePicker; set => _showGuideSourceTypePicker = value; }
    /// <summary>Gets or sets the show guide output type picker value.</summary>
    internal bool ShowGuideOutputTypePickerValue { get => _showGuideOutputTypePicker; set => _showGuideOutputTypePicker = value; }
    /// <summary>Gets or sets the selected physical device ID.</summary>
    internal PhysicalDeviceId? SelectedPhysicalDeviceId
    {
        get => _selectedPhysicalDeviceId;
        set
        {
            _selectedPhysicalDeviceId = value;
            StateHasChanged();
        }
    }

    /// <summary>Gets or sets the selected virtual device ID.</summary>
    internal VirtualDeviceId? SelectedVirtualDeviceId
    {
        get => _selectedVirtualDeviceId;
        set
        {
            _selectedVirtualDeviceId = value;
            StateHasChanged();
        }
    }

    /// <summary>Gets or sets the selected guide source ID.</summary>
    internal GuideSourceId? SelectedGuideSourceId
    {
        get => _selectedGuideSourceId;
        set
        {
            _selectedGuideSourceId = value;
            StateHasChanged();
        }
    }

    /// <summary>Gets or sets the selected guide output ID.</summary>
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

    /// <summary>Gets the selected guide output.</summary>
    internal GuideOutputSettingsModel? SelectedGuideOutput =>
        _topology.GuideOutputs.FirstOrDefault(output => output.Id == _selectedGuideOutputId);

    /// <summary>Requests a render after an editor changes the pending settings.</summary>
    internal void NotifySettingsUiChanged() => StateHasChanged();

    /// <inheritdoc />
    protected override void OnInitialized()
    {
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
        WebSettingsService.OnSettingsChanged += OnWebSettingsChanged;
        _topologyRuntime = Services.GetService<IStreamingTopologyRuntime>();
        if (_topologyRuntime is not null)
        {
            _topologyRuntime.ApplicationChanged += OnTopologyApplicationChanged;
        }
    }

    private void OnTopologyApplicationChanged() => _ = InvokeAsync(StateHasChanged);

    private void RetryTopology() => _topologyRuntime?.Retry();

    private void OnWebSettingsChanged()
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
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        _physicalDeviceProbeCancellation?.Cancel();
        _physicalDeviceProbeCancellation?.Dispose();
        if (_webSettingsService is { } webSettings)
        {
            webSettings.OnSettingsChanged -= OnWebSettingsChanged;
        }
        if (_topologyRuntime is not null)
        {
            _topologyRuntime.ApplicationChanged -= OnTopologyApplicationChanged;
        }
    }

    private void LoadCurrentSettings()
    {
        ClearPhysicalDeviceProbeStates();
        var core = SettingsService.Settings;
        var web = WebSettingsService.Settings;
        _coreRevision = ConfigurationRevision.ForSettings(core);
        _webRevision = GetWebRevision(web);
        _isInitialSetup = !web.IsSetupComplete && !StartupState.IsFactoryResetRequired;
        _autoFetchEnabled = core.IsAutoFetchEnabled;
        _refreshChannelsBeforeGuideFetch = core.RefreshChannelsBeforeGuideFetch;
        _epgHistoryRetentionHours = core.EpgHistoryRetentionHours;
        _deviceRefreshIntervalMinutes = core.DeviceRefreshIntervalMinutes;
        _tunerRefreshIntervalSeconds = core.TunerRefreshIntervalSeconds;
        _activeStreamRefreshIntervalSeconds = web.ActiveStreamRefreshIntervalSeconds;
        _autoGenerateXmltv = core.AutoGenerateXmltv;
        _xmltvOutputPath = core.XmltvOutputPath;
        _theme = web.Theme;
        _timeZoneId = core.TimeZoneId;
        _uiCulture = web.UiCulture;
        _knownProxies = string.Join(Environment.NewLine, core.KnownProxies);
        _protectedContentMode = core.ProtectedContentMode;
        _disabledChannelMode = core.DisabledChannelMode;
        _maximumConcurrentStreams = core.MaximumConcurrentStreams;
        _redactApiClientAddresses = web.RedactApiClientAddresses;
        _redactApiDeviceAddresses = web.RedactApiDeviceAddresses;
        _redactApiUrls = web.RedactApiUrls;
        var startupFilter = LoggingRuntime?.StartupApplicationFilter ?? new ApplicationLogFilterSettings("Information", []);
        _overrideLoggingDefaults = web.OverrideLoggingDefaults;
        _applicationLogLevel = _overrideLoggingDefaults ? web.ApplicationLogLevel : startupFilter.DefaultLevel;
        var categoryLevels = _overrideLoggingDefaults
            ? MergeLogCategorySettings(startupFilter.CategoryLevels, web.LogCategoryOverrides)
            : startupFilter.CategoryLevels;
        _logCategoryOverrides = categoryLevels.Select(CloneLogCategory).ToList();
        _startupLogCategories = startupFilter.CategoryLevels.Select(level => level.Category).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _enableFileLogging = web.EnableFileLogging;
        _fileLogLevel = web.FileLogLevel;
        _fileLogRetentionDays = web.FileLogRetentionDays;
        _fileLogSizeLimitMb = web.FileLogSizeLimitMb;
        RefreshRetainedLogFiles();
        _enableNetworkDiscovery = web.EnableNetworkDiscovery;
        _topology = _topologyEditor.Edit(core.Streaming, core.XmltvOutputPath, GuideOutputDrivers);
        _selectedGuideOutputId = _topology.GuideOutputs.FirstOrDefault(output => output.IsBuiltIn)?.Id
            ?? _topology.GuideOutputs.FirstOrDefault()?.Id;
        _xmltvOutputPath = _topology.GuideOutputs.FirstOrDefault(output => output.IsBuiltIn)?.OutputPath
            ?? core.XmltvOutputPath;
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
        if (_isSaving)
        {
            return;
        }
        _isSaving = true;
        StateHasChanged();
        var coreSaved = false;
        var webSaved = false;
        void AcceptCommit(SettingsSaveResult result)
        {
            if (!coreSaved && result.CoreRevision is { } coreRevision)
            {
                coreSaved = true;
                _coreRevision = coreRevision;
                _restartRequired = Services.GetService<ReverseProxyConfiguration>() is null ? _restartRequired || result.RestartRequired : result.RestartRequired;
                AcceptCommittedTopology(result.Configuration ?? throw new InvalidOperationException("The saved topology is unavailable."));
            }
            if (result.WebRevision is { } webRevision)
            {
                webSaved = true;
                _webRevision = webRevision;
                _cultureReloadRequired |= result.CultureChanged;
                _fileLoggingApplyPending = result.FileLoggingApplyPending;
                _applicationLoggingApplyPending = result.ApplicationLoggingApplyPending;
            }
        }

        try
        {
            var command = Services.GetService<SettingsSaveCommand>() ?? new SettingsSaveCommand(
                SettingsService, WebSettingsService, Text, GuideSourceDrivers, PhysicalTunerDrivers, VirtualTunerDrivers, GuideOutputDrivers,
                Services.GetService<ReverseProxyConfiguration>(), LoggingRuntime, Services.GetService<LogFileService>());
            var completingInitialSetup = _isInitialSetup;
            var result = await command.SaveAsync(new SettingsSaveDraft
            {
                Topology = _topology,
                CoreRevision = _coreRevision,
                WebRevision = _webRevision,
                AutoFetchEnabled = _autoFetchEnabled,
                RefreshChannelsBeforeGuideFetch = _refreshChannelsBeforeGuideFetch,
                EpgHistoryRetentionHours = _epgHistoryRetentionHours,
                DeviceRefreshIntervalMinutes = _deviceRefreshIntervalMinutes,
                TunerRefreshIntervalSeconds = _tunerRefreshIntervalSeconds,
                ActiveStreamRefreshIntervalSeconds = _activeStreamRefreshIntervalSeconds,
                AutoGenerateXmltv = _autoGenerateXmltv,
                XmltvOutputPath = _xmltvOutputPath,
                Theme = _theme,
                TimeZoneId = _timeZoneId,
                UiCulture = _uiCulture,
                KnownProxies = _knownProxies,
                ProtectedContentMode = _protectedContentMode,
                DisabledChannelMode = _disabledChannelMode,
                MaximumConcurrentStreams = _maximumConcurrentStreams,
                RedactApiClientAddresses = _redactApiClientAddresses,
                RedactApiDeviceAddresses = _redactApiDeviceAddresses,
                RedactApiUrls = _redactApiUrls,
                OverrideLoggingDefaults = _overrideLoggingDefaults,
                ApplicationLogLevel = _applicationLogLevel,
                LogCategoryOverrides = _logCategoryOverrides.Select(CloneLogCategory).ToArray(),
                EnableFileLogging = _enableFileLogging,
                FileLogLevel = _fileLogLevel,
                FileLogRetentionDays = _fileLogRetentionDays,
                FileLogSizeLimitMb = _fileLogSizeLimitMb,
                EnableNetworkDiscovery = _enableNetworkDiscovery,
                CompletingInitialSetup = completingInitialSetup,
                FileLoggingApplyPending = _fileLoggingApplyPending,
                ApplicationLoggingApplyPending = _applicationLoggingApplyPending
            }, GetManagementListenerPorts(), AcceptCommit);
            AcceptCommit(result);
            if (result.ValidationErrors.Count > 0)
            {
                Notifications.ShowError(string.Join(" ", result.ValidationErrors));
                return;
            }
            if (result.Failure is { } failure)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            }
            await JS.SetThemeAsync(_theme);
            if (result.ApplyErrors.Count > 0)
            {
                Notifications.ShowError(Text["Settings_PartialSaveFailure", string.Join("; ", result.ApplyErrors)]);
                return;
            }
            var streamingConfiguration = result.Configuration ?? throw new InvalidOperationException("The saved topology is unavailable.");
            if (completingInitialSetup)
            {
                var deviceStatuses = Services.GetRequiredService<IPhysicalDeviceStatusService>();
                await deviceStatuses.RefreshAsync(streamingConfiguration, _lifetime.Token);
                var unavailableDevices = streamingConfiguration.PhysicalDevices
                    .Where(device => device.Enabled && !deviceStatuses.Devices.Any(status => status.Device.Id == device.Id && status.Error is null))
                    .Select(device => device.Name)
                    .ToArray();
                if (unavailableDevices.Length > 0)
                {
                    throw new InvalidOperationException($"The following physical devices could not be connected: {string.Join(", ", unavailableDevices)}");
                }

                if (_topologyRuntime is not null)
                {
                    if (_topologyRuntime.Application.Phase == TopologyApplicationPhase.Degraded)
                    {
                        _topologyRuntime.Retry();
                    }
                    var application = await _topologyRuntime.WaitForApplicationAsync(ConfigurationRevision.Create(streamingConfiguration), _lifetime.Token);
                    if (application.Phase != TopologyApplicationPhase.Applied)
                    {
                        throw new InvalidOperationException(Text["Settings_TopologyDegraded"]);
                    }
                }
                else
                {
                    var results = await Services.GetRequiredService<IPhysicalLineupService>().RefreshAsync(streamingConfiguration, _lifetime.Token);
                    if (results.Any(result => !result.Succeeded))
                    {
                        throw new InvalidOperationException(Text["Settings_TopologyDegraded"]);
                    }
                }
                ConfigurationRevision.RequireMatch(_coreRevision, ConfigurationRevision.ForSettings(SettingsService.Settings));
                var savedWebRevision = "";
                await WebSettingsService.UpdateAsync(settings =>
                {
                    ConfigurationRevision.RequireMatch(_webRevision, GetWebRevision(settings));
                    settings.IsSetupComplete = true;
                    savedWebRevision = GetWebRevision(settings);
                });
                _webRevision = savedWebRevision;
                _isInitialSetup = false;
                Notifications.ShowSuccess(Text["Settings_SaveSuccess"]);
                if (_restartRequired)
                {
                    await RequestRestartAsync("/dashboard");
                    return;
                }
                Navigation.NavigateTo("/dashboard", forceLoad: _cultureReloadRequired);
                return;
            }

            Notifications.ShowSuccess(Text["Settings_SaveSuccess"]);
            if (_restartRequired)
            {
                await RequestRestartAsync("/settings#general");
                return;
            }

            if (_cultureReloadRequired)
            {
                Navigation.NavigateTo(Navigation.Uri, forceLoad: true);
            }
        }
        catch (SettingsConflictException)
        {
            Notifications.ShowError(Text[coreSaved ? webSaved ? "Settings_SavedApplyConflict" : "Settings_CoreSavedWebConflict" : "Settings_Conflict"]);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Notifications.ShowError(Text[coreSaved ? webSaved ? "Settings_PartialSaveFailure" : "Settings_CoreSavedWebFailure" : "Settings_SaveFailure", ex.Message]);
        }
        finally
        {
            _isSaving = false;
        }
    }

    private static string GetWebRevision(WebSettings settings) =>
        SettingsSaveCommand.GetWebRevision(settings);

    private void AcceptCommittedTopology(StreamingConfiguration configuration)
    {
        foreach (var output in _topology.GuideOutputs)
        {
            foreach (var secret in output.GeneratedSecrets)
            {
                Notifications.ShowSuccess(Text["Settings_SecretShownOnce", output.Name, secret.Key, secret.Value]);
            }
            output.GeneratedSecrets = new Dictionary<string, string>(StringComparer.Ordinal);
            output.AdapterConfiguration = configuration.GuideOutputs.Single(candidate => candidate.Id == output.Id).Adapter.Configuration.Clone();
            foreach (var setting in output.AdapterSettings)
            {
                setting.Action = AdapterSettingEditAction.Preserve;
                setting.OriginalHasValue = setting.HasValue;
            }
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
        _topology = _topologyEditor.Edit(defaults.Streaming, SettingsService.ConfiguredXmltvOutputPath, GuideOutputDrivers);
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

    /// <summary>Resets XMLTV output path.</summary>
    internal void ResetXmltvOutputPath()
    {
        if (SelectedGuideOutput is { IsBuiltIn: true } output)
        {
            output.OutputPath = SettingsService.ConfiguredXmltvOutputPath;
        }
        _xmltvOutputPath = SettingsService.ConfiguredXmltvOutputPath;
    }

    /// <summary>Determines whether the edited XMLTV destination differs from the persisted output path.</summary>
    /// <returns>The operation's result.</returns>
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

    /// <summary>Browses directory.</summary>
    internal void BrowseDirectory()
    {
        _showDirectoryBrowser = true;
        _directoryError = null;
        try
        {
            var resolvedPath = _fileSystem.ResolvePath(SelectedGuideOutput?.OutputPath ?? _xmltvOutputPath);
            var directory = Path.GetDirectoryName(resolvedPath);
            if (!string.IsNullOrEmpty(directory) && _fileSystem.DirectoryExists(directory))
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

        _currentDirectory = _fileSystem.GetCurrentDirectory();
        LoadDirectories();
    }

    /// <summary>Closes directory browser.</summary>
    internal void CloseDirectoryBrowser()
    {
        _showDirectoryBrowser = false;
    }

    private void LoadDirectories()
    {
        _directoryError = null;
        try
        {
            _directories = _fileSystem.GetDirectories(_currentDirectory).ToArray();
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

    /// <summary>Navigates to directory.</summary>
    /// <param name="path">Path used by the operation.</param>
    internal void NavigateToDirectory(string path)
    {
        _currentDirectory = path;
        LoadDirectories();
    }

    /// <summary>Navigates up.</summary>
    internal void NavigateUp()
    {
        var parent = _fileSystem.GetParent(_currentDirectory);
        if (parent != null)
        {
            _currentDirectory = parent;
            LoadDirectories();
        }
    }

    /// <summary>Gets the can navigate up.</summary>
    internal bool CanNavigateUp => !string.IsNullOrEmpty(_currentDirectory) && _fileSystem.GetParent(_currentDirectory) != null;

    /// <summary>Selects XMLTV path.</summary>
    internal void SelectXmltvPath()
    {
        GuideOutputDirectoryPath = _currentDirectory;
        _showDirectoryBrowser = false;
    }

    /// <summary>Gets the resolved XMLTV output path.</summary>
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
                return _fileSystem.ResolvePath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }
        }
    }

    /// <summary>Shows physical device type picker.</summary>
    internal void ShowPhysicalDeviceTypePicker()
    {
        if (_availablePhysicalTunerDrivers.Count == 0)
        {
            Notifications.ShowError(Text["Settings_NoPhysicalDrivers"]);
            return;
        }

        _showPhysicalDeviceTypePicker = true;
    }

    /// <summary>Adds physical device.</summary>
    /// <param name="driver">The driver value.</param>
    internal void AddPhysicalDevice(IPhysicalTunerDriver driver)
    {
        var device = _topologyEditor.AddPhysicalDevice(_topology, driver);
        SelectedPhysicalDeviceId = device.Id;
        _showPhysicalDeviceTypePicker = false;
    }

    /// <summary>Removes physical device.</summary>
    /// <param name="id">The ID value.</param>
    internal void RemovePhysicalDevice(PhysicalDeviceId id)
    {
        _topologyEditor.RemovePhysicalDevice(_topology, id);
        _physicalDeviceProbeStates.Remove(id);
        if (_selectedPhysicalDeviceId == id)
        {
            SelectedPhysicalDeviceId = null;
        }
    }

    /// <summary>Gets physical device type name.</summary>
    /// <param name="device">The device value.</param>
    /// <returns>The operation's result.</returns>
    internal string GetPhysicalDeviceTypeName(PhysicalDeviceSettingsModel device) =>
        PhysicalTunerDrivers.FirstOrDefault(driver => string.Equals(driver.Kind, device.Kind, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? device.Kind;

    /// <summary>Determines support for physical device probe.</summary>
    /// <param name="device">The device value.</param>
    /// <returns>The operation's result.</returns>
    internal bool SupportsPhysicalDeviceProbe(PhysicalDeviceSettingsModel device) =>
        FindPhysicalTunerDriver(device) is IPhysicalDeviceProbe;

    /// <summary>Gets physical device probe state.</summary>
    /// <param name="device">The device value.</param>
    /// <returns>The operation's result.</returns>
    internal PhysicalDeviceProbeState? GetPhysicalDeviceProbeState(PhysicalDeviceSettingsModel device) =>
        _physicalDeviceProbeStates.GetValueOrDefault(device.Id);

    /// <summary>Tests and identify physical device asynchronously.</summary>
    /// <param name="device">The device value.</param>
    /// <returns>A task that completes when the operation has finished.</returns>
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

    /// <summary>Uses discovered physical device name.</summary>
    /// <param name="device">The device value.</param>
    internal void UseDiscoveredPhysicalDeviceName(PhysicalDeviceSettingsModel device)
    {
        var suggestedName = GetPhysicalDeviceProbeState(device)?.Result?.SuggestedName;
        if (!string.IsNullOrWhiteSpace(suggestedName))
        {
            device.Name = suggestedName.Trim();
        }
    }

    /// <summary>Clears physical device probe state.</summary>
    /// <param name="device">The device value.</param>
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

    /// <summary>Shows virtual device type picker.</summary>
    internal void ShowVirtualDeviceTypePicker()
    {
        if (_availableVirtualTunerDrivers.Count == 0)
        {
            Notifications.ShowError(Text["Settings_NoVirtualDrivers"]);
            return;
        }

        _showVirtualDeviceTypePicker = true;
    }

    /// <summary>Adds virtual device.</summary>
    /// <param name="driver">The driver value.</param>
    internal void AddVirtualDevice(IVirtualTunerDriver driver)
    {
        var device = _topologyEditor.AddVirtualDevice(_topology, driver);
        SelectedVirtualDeviceId = device.Id;
        _showVirtualDeviceTypePicker = false;
    }

    /// <summary>Gets virtual device type name.</summary>
    /// <param name="device">The device value.</param>
    /// <returns>The operation's result.</returns>
    internal string GetVirtualDeviceTypeName(VirtualDeviceSettingsModel device) =>
        VirtualTunerDrivers.FirstOrDefault(driver => string.Equals(driver.Kind, device.Kind, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? device.Kind;

    /// <summary>Shows guide source type picker.</summary>
    internal void ShowGuideSourceTypePicker()
    {
        if (!_availableGuideSourceDrivers.Any(CanAddGuideSource))
        {
            Notifications.ShowError(Text["Settings_NoGuideSourceDrivers"]);
            return;
        }

        _showGuideSourceTypePicker = true;
    }

    /// <summary>Adds guide source.</summary>
    /// <param name="driver">The driver value.</param>
    internal void AddGuideSource(IGuideSourceDriver driver)
    {
        var source = _topologyEditor.AddGuideSource(_topology, driver);
        SelectedGuideSourceId = source.Id;
        _showGuideSourceTypePicker = false;
    }

    /// <summary>Removes guide source.</summary>
    /// <param name="id">The ID value.</param>
    internal void RemoveGuideSource(GuideSourceId id)
    {
        _topologyEditor.RemoveGuideSource(_topology, id);
        if (_selectedGuideSourceId == id)
        {
            SelectedGuideSourceId = null;
        }
    }

    /// <summary>Gets guide source type name.</summary>
    /// <param name="source">The source value.</param>
    /// <returns>The operation's result.</returns>
    internal string GetGuideSourceTypeName(GuideSourceSettingsModel source) =>
        GuideSourceDrivers.FirstOrDefault(driver => string.Equals(driver.Kind, source.Kind, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? source.Kind;

    /// <summary>Determines whether guide compatible.</summary>
    /// <param name="source">The source value.</param>
    /// <param name="device">The device value.</param>
    /// <returns>The operation's result.</returns>
    internal bool IsGuideCompatible(GuideSourceSettingsModel source, PhysicalDeviceSettingsModel device) =>
        GuideSourceDrivers.FirstOrDefault(driver => string.Equals(driver.Kind, source.Kind, StringComparison.OrdinalIgnoreCase))
            ?.SupportsPhysicalAdapter(device.Kind) == true;

    /// <summary>Determines whether add guide source.</summary>
    /// <param name="driver">The driver value.</param>
    /// <returns>The operation's result.</returns>
    internal bool CanAddGuideSource(IGuideSourceDriver driver) =>
        _topology.PhysicalDevices.Any(device => device.Enabled && driver.SupportsPhysicalAdapter(device.Kind));

    /// <summary>Shows guide output type picker.</summary>
    internal void ShowGuideOutputTypePicker()
    {
        if (_availableGuideOutputDrivers.Count == 0)
        {
            Notifications.ShowError(Text["Settings_NoGuideOutputDrivers"]);
            return;
        }

        _showGuideOutputTypePicker = true;
    }

    /// <summary>Adds guide output.</summary>
    /// <param name="driver">The driver value.</param>
    internal void AddGuideOutput(IGuideOutputDriver driver)
    {
        var defaultOutputDirectory = Path.GetDirectoryName(Path.GetFullPath(_xmltvOutputPath))
            ?? Path.GetFullPath(AppConstants.DefaultGuideOutputDirectoryPath);
        var output = _topologyEditor.AddGuideOutput(_topology, driver, defaultOutputDirectory);
        SelectedGuideOutputId = output.Id;
        _showGuideOutputTypePicker = false;
    }

    /// <summary>Updates guide output key.</summary>
    /// <param name="output">The output value.</param>
    /// <param name="value">The value value.</param>
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

    /// <summary>Removes guide output.</summary>
    /// <param name="id">The ID value.</param>
    internal void RemoveGuideOutput(GuideOutputId id)
    {
        _topologyEditor.RemoveGuideOutput(_topology, id);
        if (_selectedGuideOutputId == id)
        {
            SelectedGuideOutputId = _topology.GuideOutputs.FirstOrDefault(output => output.IsBuiltIn)?.Id
                ?? _topology.GuideOutputs.FirstOrDefault()?.Id;
        }
    }

    /// <summary>Gets guide output type name.</summary>
    /// <param name="output">The output value.</param>
    /// <returns>The operation's result.</returns>
    internal string GetGuideOutputTypeName(GuideOutputSettingsModel output) =>
        GuideOutputDrivers.FirstOrDefault(driver => string.Equals(driver.Kind, output.Kind, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? output.Kind;

    /// <summary>Determines whether XMLTV guide output.</summary>
    /// <param name="output">The output value.</param>
    /// <returns>The operation's result.</returns>
    internal static bool IsXmltvGuideOutput(GuideOutputSettingsModel output) =>
        IsXmltvGuideOutputKind(output.Kind);

    private static bool IsXmltvGuideOutputKind(string kind) =>
        string.Equals(kind, XmltvGuideOutputDriver.AdapterKind, StringComparison.OrdinalIgnoreCase);

    /// <summary>Determines whether guide output setting visible.</summary>
    /// <param name="output">The output value.</param>
    /// <param name="setting">The setting value.</param>
    /// <returns>The operation's result.</returns>
    internal static bool IsGuideOutputSettingVisible(GuideOutputSettingsModel output, AdapterSettingFieldSettingsModel setting) =>
        setting.Descriptor.VisibleWhen.All(condition =>
        {
            var actual = output.AdapterSettings
                .FirstOrDefault(candidate => string.Equals(candidate.Descriptor.Key, condition.FieldKey, StringComparison.Ordinal))
                ?.Value?.ToString();
            var equals = string.Equals(actual, condition.Value, StringComparison.OrdinalIgnoreCase);
            return condition.Operator == AdapterSettingConditionOperator.Equals ? equals : !equals;
        });

    /// <summary>Gets guide output setting value.</summary>
    /// <param name="setting">The setting value.</param>
    /// <returns>The operation's result.</returns>
    internal static string GetGuideOutputSettingValue(AdapterSettingFieldSettingsModel setting)
    {
        if (setting.Value is not JsonElement value || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return string.Empty;
        }
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString();
    }

    /// <summary>Gets guide output boolean setting value.</summary>
    /// <param name="setting">The setting value.</param>
    /// <returns>The operation's result.</returns>
    internal static bool GetGuideOutputBooleanSettingValue(AdapterSettingFieldSettingsModel setting) =>
        setting.Value is JsonElement value &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        value.GetBoolean();

    /// <summary>Updates guide output boolean setting.</summary>
    /// <param name="setting">The setting value.</param>
    /// <param name="value">The value value.</param>
    internal static void UpdateGuideOutputBooleanSetting(AdapterSettingFieldSettingsModel setting, bool value)
    {
        setting.Value = JsonSerializer.SerializeToElement(value);
        setting.HasValue = true;
        setting.Action = AdapterSettingEditAction.Set;
    }

    /// <summary>Updates guide output setting.</summary>
    /// <param name="setting">The setting value.</param>
    /// <param name="value">The value value.</param>
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

    /// <summary>Generates guide output setting.</summary>
    /// <param name="setting">The setting value.</param>
    internal static void GenerateGuideOutputSetting(AdapterSettingFieldSettingsModel setting)
    {
        setting.Value = null;
        setting.HasValue = true;
        setting.Action = AdapterSettingEditAction.Generate;
    }

    /// <summary>Clears guide output setting.</summary>
    /// <param name="setting">The setting value.</param>
    internal static void ClearGuideOutputSetting(AdapterSettingFieldSettingsModel setting)
    {
        setting.Value = null;
        setting.HasValue = false;
        setting.Action = AdapterSettingEditAction.Clear;
    }

    /// <summary>Sets guide output virtual device.</summary>
    /// <param name="output">The output value.</param>
    /// <param name="id">The ID value.</param>
    /// <param name="selected">The selected value.</param>
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

    /// <summary>Sets guide output guide source.</summary>
    /// <param name="output">The output value.</param>
    /// <param name="id">The ID value.</param>
    /// <param name="selected">The selected value.</param>
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

    /// <summary>Determines whether virtual compatible.</summary>
    /// <param name="device">The device value.</param>
    /// <param name="physical">The physical value.</param>
    /// <returns>The operation's result.</returns>
    internal bool IsVirtualCompatible(VirtualDeviceSettingsModel device, PhysicalDeviceSettingsModel physical) =>
        VirtualTunerDrivers.FirstOrDefault(driver => string.Equals(driver.Kind, device.Kind, StringComparison.OrdinalIgnoreCase))
            ?.SupportsPhysicalAdapter(physical.Kind) == true;

    /// <summary>Represents the physical device probe state.</summary>
    internal sealed record PhysicalDeviceProbeState
    {
        /// <summary>Gets or sets the is testing.</summary>
        public bool IsTesting { get; init; }

        /// <summary>Gets or sets the result.</summary>
        public PhysicalDeviceProbeResult? Result { get; init; }

        /// <summary>Gets or sets the error.</summary>
        public string? Error { get; init; }
    }

    /// <summary>Imports Lineup Link bundle.</summary>
    /// <param name="device">The device value.</param>
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

    /// <summary>Creates Lineup Link access grant.</summary>
    /// <param name="device">The device value.</param>
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

    /// <summary>Rotates Lineup Link access grant.</summary>
    /// <param name="device">The device value.</param>
    /// <param name="grant">The grant value.</param>
    internal void RotateLineupLinkAccessGrant(VirtualDeviceSettingsModel device, LineupLinkAccessGrantSettingsModel grant)
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

    /// <summary>Revokes Lineup Link access grant.</summary>
    /// <param name="device">The device value.</param>
    /// <param name="grant">The grant value.</param>
    internal void RevokeLineupLinkAccessGrant(VirtualDeviceSettingsModel device, LineupLinkAccessGrantSettingsModel grant)
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

    /// <summary>Purges Lineup Link access grant.</summary>
    /// <param name="device">The device value.</param>
    /// <param name="grant">The grant value.</param>
    internal void PurgeLineupLinkAccessGrant(VirtualDeviceSettingsModel device, LineupLinkAccessGrantSettingsModel grant)
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

    /// <summary>Exports Lineup Link bundle asynchronously.</summary>
    /// <param name="device">The device value.</param>
    /// <param name="grant">The grant value.</param>
    /// <returns>A task that completes when the operation has finished.</returns>
    internal async Task ExportLineupLinkBundleAsync(VirtualDeviceSettingsModel device, LineupLinkAccessGrantSettingsModel grant)
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

    /// <summary>Copies Lineup Link bundle asynchronously.</summary>
    /// <param name="device">The device value.</param>
    /// <param name="grant">The grant value.</param>
    /// <returns>A task that completes when the operation has finished.</returns>
    internal async Task CopyLineupLinkBundleAsync(VirtualDeviceSettingsModel device, LineupLinkAccessGrantSettingsModel grant)
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

    /// <summary>Toggles Lineup Link bundle.</summary>
    /// <param name="device">The device value.</param>
    /// <param name="grant">The grant value.</param>
    internal void ToggleLineupLinkBundle(VirtualDeviceSettingsModel device, LineupLinkAccessGrantSettingsModel grant)
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

    private static void ReplaceLineupLinkAccessGrant(VirtualDeviceSettingsModel device, LineupLinkAccessGrant persisted, string? rawToken)
    {
        var index = device.LinkAccessGrants.FindIndex(grant => grant.Id == persisted.Id);
        device.LinkAccessGrants[index] = LineupLinkAccessGrantSettingsModel.Create(persisted, rawToken);
    }

    private void SetLineupLinkBundle(VirtualDeviceSettingsModel device, LineupLinkAccessGrantSettingsModel grant, bool redactUnavailableToken)
    {
        if (TryCreateLineupLinkBundle(device, grant, redactUnavailableToken, out var bundle))
        {
            grant.ConnectionBundle = bundle;
            grant.ConnectionBundleExpanded = true;
        }
    }

    private bool TryCreateLineupLinkBundle(VirtualDeviceSettingsModel device, LineupLinkAccessGrantSettingsModel grant, bool redactUnavailableToken, out string bundle)
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

    /// <summary>Handles changes to transcoder settings changed.</summary>
    internal void OnTranscoderSettingsChanged() => StateHasChanged();

    /// <summary>Removes virtual device.</summary>
    /// <param name="id">The ID value.</param>
    internal void RemoveVirtualDevice(VirtualDeviceId id)
    {
        _topologyEditor.RemoveVirtualDevice(_topology, id);
        if (_selectedVirtualDeviceId == id)
        {
            SelectedVirtualDeviceId = null;
        }
    }

    /// <summary>Sets physical subscription.</summary>
    /// <param name="device">The device value.</param>
    /// <param name="id">The ID value.</param>
    /// <param name="selected">The selected value.</param>
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

    /// <summary>Sets guide source subscription.</summary>
    /// <param name="source">The source value.</param>
    /// <param name="id">The ID value.</param>
    /// <param name="selected">The selected value.</param>
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
