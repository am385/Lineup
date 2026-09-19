using System.Text.Json;
using Lineup.Core;

namespace Lineup.Web.Services;

/// <summary>
/// Requests a restart-safe factory reset of Lineup-owned persistent state.
/// </summary>
public interface IFactoryResetService
{
    /// <summary>
    /// Persists a reset request and begins graceful application shutdown.
    /// </summary>
    /// <param name="cancellationToken">Cancels marker creation before shutdown begins.</param>
    Task RequestResetAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Coordinates factory-reset requests with the application host lifecycle.
/// </summary>
public sealed class FactoryResetService : IFactoryResetService
{
    private readonly AppDataStore _appDataStore;
    private readonly IAppSettingsService _settings;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly ILogger<FactoryResetService> _logger;

    /// <summary>
    /// Initializes a factory-reset service.
    /// </summary>
    /// <param name="appDataStore">Lineup persistent application-data store.</param>
    /// <param name="settings">Current persisted application settings.</param>
    /// <param name="applicationLifetime">Application shutdown controller.</param>
    /// <param name="logger">Reset diagnostics logger.</param>
    public FactoryResetService(
        AppDataStore appDataStore,
        IAppSettingsService settings,
        IHostApplicationLifetime applicationLifetime,
        ILogger<FactoryResetService> logger)
    {
        _appDataStore = appDataStore;
        _settings = settings;
        _applicationLifetime = applicationLifetime;
        _logger = logger;
    }

    /// <summary>
    /// Initializes a factory-reset service from an application-data path for direct tests.
    /// </summary>
    internal FactoryResetService(string appDataPath, IAppSettingsService settings, IHostApplicationLifetime applicationLifetime, ILogger<FactoryResetService> logger)
        : this(new AppDataStore(appDataPath), settings, applicationLifetime, logger)
    {
    }

    /// <inheritdoc />
    public async Task RequestResetAsync(CancellationToken cancellationToken = default)
    {
        var request = new FactoryResetRequest
        {
            PersistedXmltvOutputPath = _settings.Settings.XmltvOutputPath
        };
        await FactoryResetCoordinator.WriteRequestAsync(_appDataStore, request, cancellationToken);
        _logger.LogWarning("Factory reset requested; Lineup will erase owned persistent state during the next startup");
        _applicationLifetime.StopApplication();
    }
}

/// <summary>
/// Applies a pending factory reset before application services open persistent files.
/// </summary>
public static class FactoryResetCoordinator
{
    /// <summary>
    /// Filename used to persist a restart-safe factory-reset request.
    /// </summary>
    public const string RequestFileName = ".lineup-factory-reset.json";

    private const string HlsDirectoryName = "hdhomerun-hls";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Writes a factory-reset request atomically.
    /// </summary>
    /// <param name="appDataStore">Lineup persistent application-data store.</param>
    /// <param name="request">Reset details needed for safe cleanup.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public static Task WriteRequestAsync(AppDataStore appDataStore, FactoryResetRequest request, CancellationToken cancellationToken = default)
    {
        return appDataStore.WriteAtomicallyAsync(
            appDataStore.FactoryResetRequestPath,
            (stream, token) => JsonSerializer.SerializeAsync(stream, request, JsonOptions, token),
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Writes a factory-reset request from an application-data path for direct tests.
    /// </summary>
    internal static Task WriteRequestAsync(string appDataPath, FactoryResetRequest request, CancellationToken cancellationToken = default) =>
        WriteRequestAsync(new AppDataStore(appDataPath), request, cancellationToken);

    /// <summary>
    /// Removes allowlisted Lineup-owned state when a reset request is present.
    /// </summary>
    /// <param name="appDataStore">Lineup persistent application-data store.</param>
    /// <param name="configuredXmltvPath">Startup-configured XMLTV file or directory.</param>
    /// <returns><see langword="true"/> when a pending reset was applied.</returns>
    public static bool ApplyPendingReset(AppDataStore appDataStore, string? configuredXmltvPath)
    {
        if (!appDataStore.FileExists(appDataStore.FactoryResetRequestPath))
        {
            return false;
        }

        var request = JsonSerializer.Deserialize<FactoryResetRequest>(appDataStore.ReadAllText(appDataStore.FactoryResetRequestPath), JsonOptions)
            ?? throw new InvalidOperationException("The factory-reset request is invalid.");
        var configuredOutputPath = AppSettingsService.ResolveConfiguredXmltvOutputPath(configuredXmltvPath);
        var configuredXmltvDirectory = ResolveConfiguredXmltvDirectory(configuredXmltvPath);

        DeleteSettingsState(appDataStore);
        DeleteGuideState(appDataStore);
        appDataStore.DeleteDirectory(appDataStore.LogDirectoryPath, recursive: true);
        appDataStore.DeleteDirectory(appDataStore.DataProtectionKeysPath, recursive: true);
        DeleteOutputFile(configuredOutputPath);
        if (IsOwnedPersistedOutput(request.PersistedXmltvOutputPath, appDataStore.RootPath, configuredXmltvDirectory, configuredOutputPath))
        {
            DeleteOutputFile(Path.GetFullPath(request.PersistedXmltvOutputPath!));
        }

        var hlsDirectory = Path.Combine(Path.GetTempPath(), HlsDirectoryName);
        if (Directory.Exists(hlsDirectory))
        {
            Directory.Delete(hlsDirectory, recursive: true);
        }
        var subtitleDirectory = Path.Combine(Path.GetTempPath(), SubtitleSidecarService.DirectoryName);
        if (Directory.Exists(subtitleDirectory))
        {
            Directory.Delete(subtitleDirectory, recursive: true);
        }

        appDataStore.DeleteFile(appDataStore.FactoryResetRequestPath);
        return true;
    }

    /// <summary>
    /// Applies a pending factory reset from an application-data path for direct tests.
    /// </summary>
    internal static bool ApplyPendingReset(string appDataPath, string? configuredXmltvPath) =>
        ApplyPendingReset(new AppDataStore(appDataPath), configuredXmltvPath);

    private static void DeleteSettingsState(AppDataStore appDataStore)
    {
        appDataStore.DeleteFile(appDataStore.SettingsPath);
        appDataStore.DeleteFile(appDataStore.SettingsBackupPath);
        appDataStore.DeleteMatchingFiles(appDataStore.RootPath, $"{AppConstants.SettingsFileName}.*.tmp");
    }

    private static void DeleteGuideState(AppDataStore appDataStore)
    {
        appDataStore.DeleteFile(appDataStore.DatabasePath);
        appDataStore.DeleteFile($"{appDataStore.DatabasePath}-wal");
        appDataStore.DeleteFile($"{appDataStore.DatabasePath}-shm");
        appDataStore.DeleteFile(appDataStore.GuideCachePath);
        appDataStore.DeleteFile($"{appDataStore.GuideCachePath}.generation");
        appDataStore.DeleteMatchingFiles(appDataStore.RootPath, $"{Path.GetFileName(appDataStore.GuideCachePath)}.*.tmp");
        appDataStore.DeleteFile(appDataStore.ChannelLineupPath);
        appDataStore.DeleteMatchingFiles(appDataStore.RootPath, $"{Path.GetFileName(appDataStore.ChannelLineupPath)}.*.tmp");
    }

    private static void DeleteOutputFile(string path)
    {
        DeleteFile(path);
        DeleteFile($"{path}.generation");
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
        {
            DeleteMatchingFiles(directory, $"{Path.GetFileName(path)}.*.tmp");
        }
    }

    private static void DeleteFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void DeleteMatchingFiles(string directory, string pattern)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly))
        {
            File.Delete(path);
        }
    }

    private static string? ResolveConfiguredXmltvDirectory(string? configuredXmltvPath)
    {
        if (string.IsNullOrWhiteSpace(configuredXmltvPath))
        {
            return null;
        }

        var path = configuredXmltvPath.Trim();
        var isDirectory = Directory.Exists(path) || Path.EndsInDirectorySeparator(path) || string.IsNullOrEmpty(Path.GetExtension(path));
        return isDirectory ? Path.GetFullPath(path) : null;
    }

    private static bool IsOwnedPersistedOutput(string? persistedPath, string configDirectory, string? configuredXmltvDirectory, string configuredOutputPath)
    {
        if (string.IsNullOrWhiteSpace(persistedPath))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(persistedPath);
        return PathsEqual(fullPath, configuredOutputPath) ||
            IsWithinDirectory(fullPath, configDirectory) ||
            configuredXmltvDirectory != null && IsWithinDirectory(fullPath, configuredXmltvDirectory);
    }

    private static bool IsWithinDirectory(string path, string directory)
    {
        var relativePath = Path.GetRelativePath(Path.GetFullPath(directory), Path.GetFullPath(path));
        return !Path.IsPathRooted(relativePath) && relativePath != ".." && !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static bool PathsEqual(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), comparison);
    }
}

/// <summary>
/// Describes persisted paths relevant to a factory-reset request.
/// </summary>
public sealed record FactoryResetRequest
{
    /// <summary>
    /// Gets the XMLTV output path active when reset was requested.
    /// </summary>
    public string? PersistedXmltvOutputPath { get; init; }
}
