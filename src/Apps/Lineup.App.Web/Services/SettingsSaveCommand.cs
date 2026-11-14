using System.Text.Json;
using Lineup.Core.Common.Networking;
using Lineup.Plugin.GuideOutputs.Xmltv.Plugin;
using Microsoft.Extensions.Localization;

namespace Lineup.App.Web.Services;

/// <summary>Contains unnormalized operator input and the revisions accepted by an editor or another application caller.</summary>
public sealed record SettingsSaveDraft
{
    /// <summary>Gets the editable topology, including explicit adapter-secret edit actions.</summary>
    public required TopologySettingsModel Topology { get; init; }
    /// <summary>Gets the expected Core revision.</summary>
    public required string CoreRevision { get; init; }
    /// <summary>Gets the expected Web revision, excluding independently synchronized theme changes.</summary>
    public required string WebRevision { get; init; }
    /// <summary>Gets whether automatic guide fetching is enabled.</summary>
    public bool AutoFetchEnabled { get; init; }
    /// <summary>Gets whether channels refresh before fetching guides.</summary>
    public bool RefreshChannelsBeforeGuideFetch { get; init; }
    /// <summary>Gets retained ended-guide hours before normalization.</summary>
    public int EpgHistoryRetentionHours { get; init; }
    /// <summary>Gets the device refresh interval before normalization.</summary>
    public int DeviceRefreshIntervalMinutes { get; init; }
    /// <summary>Gets the tuner refresh interval before normalization.</summary>
    public int TunerRefreshIntervalSeconds { get; init; }
    /// <summary>Gets the dashboard refresh interval before normalization.</summary>
    public int ActiveStreamRefreshIntervalSeconds { get; init; }
    /// <summary>Gets whether automatic XMLTV publication is enabled.</summary>
    public bool AutoGenerateXmltv { get; init; }
    /// <summary>Gets the fallback XMLTV path.</summary>
    public string XmltvOutputPath { get; init; } = "";
    /// <summary>Gets the selected theme.</summary>
    public string Theme { get; init; } = "auto";
    /// <summary>Gets the selected display time zone.</summary>
    public string TimeZoneId { get; init; } = "";
    /// <summary>Gets the selected UI culture.</summary>
    public string UiCulture { get; init; } = UiLocalization.DefaultCulture;
    /// <summary>Gets the raw trusted-proxy input.</summary>
    public string KnownProxies { get; init; } = "";
    /// <summary>Gets the protected-content behavior.</summary>
    public ProtectedContentMode ProtectedContentMode { get; init; }
    /// <summary>Gets the disabled-channel behavior.</summary>
    public DisabledChannelMode DisabledChannelMode { get; init; }
    /// <summary>Gets the viewer admission limit.</summary>
    public int MaximumConcurrentStreams { get; init; }
    /// <summary>Gets whether client addresses are redacted.</summary>
    public bool RedactApiClientAddresses { get; init; }
    /// <summary>Gets whether device addresses are redacted.</summary>
    public bool RedactApiDeviceAddresses { get; init; }
    /// <summary>Gets whether URLs are redacted.</summary>
    public bool RedactApiUrls { get; init; }
    /// <summary>Gets whether startup logging filters are overridden.</summary>
    public bool OverrideLoggingDefaults { get; init; }
    /// <summary>Gets the application log level.</summary>
    public string ApplicationLogLevel { get; init; } = "Information";
    /// <summary>Gets application category overrides.</summary>
    public IReadOnlyList<WebLogCategoryLevelSetting> LogCategoryOverrides { get; init; } = [];
    /// <summary>Gets whether rolling files are enabled.</summary>
    public bool EnableFileLogging { get; init; }
    /// <summary>Gets the file log level.</summary>
    public string FileLogLevel { get; init; } = "Information";
    /// <summary>Gets file retention before normalization.</summary>
    public int FileLogRetentionDays { get; init; } = 7;
    /// <summary>Gets file size before normalization.</summary>
    public int FileLogSizeLimitMb { get; init; } = 50;
    /// <summary>Gets whether network discovery is enabled.</summary>
    public bool EnableNetworkDiscovery { get; init; }
    /// <summary>Gets whether setup verification must still complete after persistence.</summary>
    public bool CompletingInitialSetup { get; init; }
    /// <summary>Gets whether a previously failed file-logging application needs retrying.</summary>
    public bool FileLoggingApplyPending { get; init; }
    /// <summary>Gets whether a previously failed filter application needs retrying.</summary>
    public bool ApplicationLoggingApplyPending { get; init; }
}

/// <summary>Reports independent commits and application outcomes; a saved Core document is never represented as rolled back.</summary>
public sealed record SettingsSaveResult
{
    /// <summary>Gets input validation failures, with no writes performed.</summary>
    public IReadOnlyList<string> ValidationErrors { get; init; } = [];
    /// <summary>Gets the committed Core revision, including when the subsequent Web write fails.</summary>
    public string? CoreRevision { get; init; }
    /// <summary>Gets the committed Web revision.</summary>
    public string? WebRevision { get; init; }
    /// <summary>Gets the detached committed topology when Core persistence succeeded.</summary>
    public StreamingConfiguration? Configuration { get; init; }
    /// <summary>Gets the conflict or persistence failure without discarding earlier commit outcomes.</summary>
    public Exception? Failure { get; init; }
    /// <summary>Gets independently failed runtime applications after both documents were saved.</summary>
    public IReadOnlyList<string> ApplyErrors { get; init; } = [];
    /// <summary>Gets whether the trusted-proxy change requires a host restart.</summary>
    public bool RestartRequired { get; init; }
    /// <summary>Gets whether the selected culture requires a page reload.</summary>
    public bool CultureChanged { get; init; }
    /// <summary>Gets whether file logging still needs application.</summary>
    public bool FileLoggingApplyPending { get; init; }
    /// <summary>Gets whether application logging still needs application.</summary>
    public bool ApplicationLoggingApplyPending { get; init; }
}

/// <summary>Validates and saves Web settings commands without requiring a rendered component or pretending the two stores are transactional.</summary>
internal sealed class SettingsSaveCommand(
    ILineupSettingsService core,
    IWebSettingsService web,
    IStringLocalizer<UiStrings> text,
    IEnumerable<IGuideSourceDriver> guideSources,
    IEnumerable<IPhysicalTunerDriver> physicalDrivers,
    IEnumerable<IVirtualTunerDriver> virtualDrivers,
    IEnumerable<IGuideOutputDriver> guideOutputs,
    ReverseProxyConfiguration? proxyConfiguration = null,
    LoggingRuntimeState? logging = null,
    LogFileService? files = null
)
{
    /// <summary>Gets the Web edit revision; header theme changes are synchronized independently into open editors.</summary>
    public static string GetWebRevision(WebSettings settings) => ConfigurationRevision.Create(settings, nameof(WebSettings.Theme));

    /// <summary>Saves a captured draft and synchronously reports durable commit boundaries, including before an unexpected later exception can propagate.</summary>
    public async Task<SettingsSaveResult> SaveAsync(SettingsSaveDraft draft, IReadOnlyCollection<int> managementListenerPorts, Action<SettingsSaveResult>? committed = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        draft = draft with { LogCategoryOverrides = draft.LogCategoryOverrides.Select(CloneCategory).ToArray() };
        var result = new SettingsSaveResult();
        try
        {
            var errors = Validate(draft, managementListenerPorts, out var configuration, out var proxies);
            if (errors.Count > 0)
            {
                return result with { ValidationErrors = errors };
            }
            var previousWeb = web.Settings;
            var previousTheme = previousWeb.Theme;
            var applicationChanged = ApplicationLoggingChanged(draft, previousWeb);
            var fileChanged = FileLoggingChanged(draft, previousWeb);
            var restart = proxyConfiguration?.RequiresRestart(proxies)
                ?? !core.Settings.KnownProxies.SequenceEqual(proxies, StringComparer.OrdinalIgnoreCase);
            var cultureChanged = !string.Equals(previousWeb.UiCulture, draft.UiCulture, StringComparison.OrdinalIgnoreCase);
            ConfigurationRevision.RequireMatch(draft.WebRevision, GetWebRevision(previousWeb));
            var revision = "";
            await core.UpdateAsync(settings =>
            {
                ConfigurationRevision.RequireMatch(draft.CoreRevision, ConfigurationRevision.ForSettings(settings));
                var wasEnabled = settings.IsAutoFetchEnabled;
                settings.AutoFetchInterval = draft.AutoFetchEnabled
                    ? settings.AutoFetchInterval > TimeSpan.Zero ? settings.AutoFetchInterval : TimeSpan.FromHours(24)
                    : TimeSpan.Zero;
                if (!draft.AutoFetchEnabled || !wasEnabled)
                {
                    settings.NextAutoFetchTime = null;
                }
                settings.RefreshChannelsBeforeGuideFetch = draft.RefreshChannelsBeforeGuideFetch;
                settings.EpgHistoryRetentionHours = draft.EpgHistoryRetentionHours;
                settings.Streaming = configuration;
                settings.DeviceRefreshIntervalMinutes = draft.DeviceRefreshIntervalMinutes;
                settings.TunerRefreshIntervalSeconds = draft.TunerRefreshIntervalSeconds;
                settings.AutoGenerateXmltv = draft.AutoGenerateXmltv;
                settings.XmltvOutputPath = ReadXmltvOutputPath(configuration) ?? draft.XmltvOutputPath.Trim();
                settings.TimeZoneId = draft.TimeZoneId;
                settings.ProtectedContentMode = draft.ProtectedContentMode;
                settings.DisabledChannelMode = draft.DisabledChannelMode;
                settings.MaximumConcurrentStreams = draft.MaximumConcurrentStreams;
                settings.KnownProxies = proxies;
                revision = ConfigurationRevision.ForSettings(settings);
            });
            result = result with { CoreRevision = revision, Configuration = configuration, RestartRequired = restart };
            committed?.Invoke(result);
            await web.UpdateAsync(settings =>
            {
                ConfigurationRevision.RequireMatch(draft.WebRevision, GetWebRevision(settings));
                settings.EnableNetworkDiscovery = draft.EnableNetworkDiscovery;
                settings.ActiveStreamRefreshIntervalSeconds = draft.ActiveStreamRefreshIntervalSeconds;
                if (settings.Theme == previousTheme)
                {
                    settings.Theme = draft.Theme;
                }
                settings.UiCulture = draft.UiCulture;
                settings.RedactApiClientAddresses = draft.RedactApiClientAddresses;
                settings.RedactApiDeviceAddresses = draft.RedactApiDeviceAddresses;
                settings.RedactApiUrls = draft.RedactApiUrls;
                settings.OverrideLoggingDefaults = draft.OverrideLoggingDefaults;
                settings.ApplicationLogLevel = draft.ApplicationLogLevel;
                settings.LogCategoryOverrides = draft.LogCategoryOverrides.Select(CloneCategory).ToList();
                settings.EnableFileLogging = draft.EnableFileLogging;
                settings.FileLogLevel = draft.FileLogLevel;
                settings.FileLogRetentionDays = draft.FileLogRetentionDays;
                settings.FileLogSizeLimitMb = draft.FileLogSizeLimitMb;
                settings.IsSetupComplete = !draft.CompletingInitialSetup;
                revision = GetWebRevision(settings);
            });
            result = result with
            {
                WebRevision = revision,
                CultureChanged = cultureChanged,
                ApplicationLoggingApplyPending = draft.ApplicationLoggingApplyPending || applicationChanged,
                FileLoggingApplyPending = draft.FileLoggingApplyPending || fileChanged
            };
            committed?.Invoke(result);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or JsonException or NotSupportedException)
        {
            return result with { Failure = exception };
        }

        var applyErrors = new List<string>();
        if (result.ApplicationLoggingApplyPending)
        {
            try
            {
                (logging ?? throw new InvalidOperationException("The runtime application log filter is unavailable.")).ApplyApplicationFilter(web.Settings);
                result = result with { ApplicationLoggingApplyPending = false };
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
            {
                applyErrors.Add(text["Settings_ApplicationLogApplyFailure", exception.Message]);
            }
        }
        if (result.FileLoggingApplyPending)
        {
            try
            {
                (files ?? throw new InvalidOperationException("The runtime file log service is unavailable.")).ApplySettings(web.Settings);
                result = result with { FileLoggingApplyPending = false };
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
            {
                applyErrors.Add(text["Settings_FileLogApplyFailure", exception.Message]);
            }
        }
        return result with { ApplyErrors = applyErrors };
    }

    private IReadOnlyList<string> Validate(SettingsSaveDraft draft, IReadOnlyCollection<int> ports, out StreamingConfiguration configuration, out IReadOnlyList<string> proxies)
    {
        configuration = new();
        proxies = [];
        foreach (var output in draft.Topology.GuideOutputs.Where(output => string.Equals(output.Kind, XmltvGuideOutputDriver.AdapterKind, StringComparison.OrdinalIgnoreCase)))
        {
            if (string.IsNullOrWhiteSpace(output.OutputPath))
            {
                return [text["Settings_GuideOutputDestinationRequired", output.Name]];
            }
            try
            {
                _ = Path.GetFullPath(output.OutputPath);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return [text["Settings_GuideOutputDestinationInvalid", output.Name]];
            }
        }
        var errors = new TopologySettingsEditor().TryBuild(draft.Topology, out configuration, guideSources, physicalDrivers, virtualDrivers, guideOutputs, ports);
        if (errors.Count > 0)
        {
            return errors;
        }
        if (draft.DeviceRefreshIntervalMinutes is < 0 or > 60)
        {
            return [text["Settings_DeviceRefreshValidation"]];
        }
        if (draft.TunerRefreshIntervalSeconds is < 0 or > 300)
        {
            return [text["Settings_TunerRefreshValidation"]];
        }
        if (draft.ActiveStreamRefreshIntervalSeconds is < 0 or > 300)
        {
            return [text["Settings_StreamRefreshValidation"]];
        }
        if (draft.EpgHistoryRetentionHours is < 0 or > 720)
        {
            return [text["Settings_EpgRetentionValidation"]];
        }
        if (draft.EnableFileLogging && (draft.FileLogRetentionDays is < 1 or > 90 || draft.FileLogSizeLimitMb is < 1 or > 500))
        {
            return [text["Settings_LogRetentionValidation"]];
        }
        if (draft.OverrideLoggingDefaults)
        {
            if (draft.LogCategoryOverrides.Any(level => string.IsNullOrWhiteSpace(level.Category) || level.Category.Length > 200 ||
                string.Equals(level.Category, "Default", StringComparison.OrdinalIgnoreCase)))
            {
                return [text["Settings_LogCategoryValidation"]];
            }
            if (draft.LogCategoryOverrides.GroupBy(level => level.Category, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            {
                return [text["Settings_LogCategoryUniqueValidation"]];
            }
        }
        try
        {
            proxies = TrustedProxySet.Parse([draft.KnownProxies], text["General_KnownProxiesLabel"]).Values;
        }
        catch (InvalidOperationException exception)
        {
            return [exception.Message];
        }
        return [];
    }

    private bool ApplicationLoggingChanged(SettingsSaveDraft draft, WebSettings previous)
    {
        if (draft.OverrideLoggingDefaults != previous.OverrideLoggingDefaults ||
            draft.OverrideLoggingDefaults && (!string.Equals(draft.ApplicationLogLevel, previous.ApplicationLogLevel, StringComparison.OrdinalIgnoreCase) ||
                !CategoriesEqual(draft.LogCategoryOverrides, previous.LogCategoryOverrides)))
        {
            return true;
        }
        if (logging is null)
        {
            return false;
        }
        var desired = draft.OverrideLoggingDefaults ? new ApplicationLogFilterSettings(draft.ApplicationLogLevel, draft.LogCategoryOverrides) : logging.StartupApplicationFilter;
        return !string.Equals(desired.DefaultLevel, logging.ActiveApplicationFilter.DefaultLevel, StringComparison.OrdinalIgnoreCase) ||
            !CategoriesEqual(desired.CategoryLevels, logging.ActiveApplicationFilter.CategoryLevels);
    }

    private bool FileLoggingChanged(SettingsSaveDraft draft, WebSettings previous) =>
        draft.EnableFileLogging != previous.EnableFileLogging || !string.Equals(draft.FileLogLevel, previous.FileLogLevel, StringComparison.OrdinalIgnoreCase) ||
        draft.FileLogRetentionDays != previous.FileLogRetentionDays || draft.FileLogSizeLimitMb != previous.FileLogSizeLimitMb ||
        logging is not null && (draft.EnableFileLogging != logging.FileSettings.Enabled ||
            !string.Equals(draft.FileLogLevel, logging.FileSettings.MinimumLevel.ToString(), StringComparison.OrdinalIgnoreCase) ||
            draft.FileLogRetentionDays != logging.FileSettings.RetentionDays || draft.FileLogSizeLimitMb != logging.FileSettings.FileSizeLimitMb);

    private static bool CategoriesEqual(IEnumerable<WebLogCategoryLevelSetting> left, IEnumerable<WebLogCategoryLevelSetting> right)
    {
        var leftLevels = left.GroupBy(level => level.Category, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.Last().Level, StringComparer.OrdinalIgnoreCase);
        var rightLevels = right.GroupBy(level => level.Category, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.Last().Level, StringComparer.OrdinalIgnoreCase);
        return leftLevels.Count == rightLevels.Count && leftLevels.All(pair => rightLevels.TryGetValue(pair.Key, out var level) && string.Equals(pair.Value, level, StringComparison.OrdinalIgnoreCase));
    }

    private static WebLogCategoryLevelSetting CloneCategory(WebLogCategoryLevelSetting level) => new() { Category = level.Category, Level = level.Level };

    private static string? ReadXmltvOutputPath(StreamingConfiguration configuration)
    {
        var builtIn = configuration.GuideOutputs.FirstOrDefault(output => output.IsBuiltIn && string.Equals(output.Adapter.Kind, XmltvGuideOutputDriver.AdapterKind, StringComparison.OrdinalIgnoreCase));
        return builtIn?.Adapter.Configuration.ValueKind == JsonValueKind.Object && builtIn.Adapter.Configuration.TryGetProperty("OutputPath", out var path) ? path.GetString() : null;
    }
}
