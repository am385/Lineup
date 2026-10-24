using System.Text.Json;
using Lineup.Core.Settings;

namespace Lineup.Core.Storage;

/// <summary>
/// Applies a pending factory reset before application services open persistent files.
/// </summary>
public static class FactoryResetCoordinator
{
    /// <summary>Filename used to persist a restart-safe factory-reset request.</summary>
    public const string RequestFileName = AppConstants.FactoryResetRequestFileName;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Writes a factory-reset request atomically.</summary>
    public static Task WriteRequestAsync(IAppDataStore appDataStore, FactoryResetRequest request, CancellationToken cancellationToken = default)
    {
        return appDataStore.WriteAtomicallyAsync(
            appDataStore.FactoryResetRequestPath,
            (stream, token) => JsonSerializer.SerializeAsync(stream, request, JsonOptions, token),
            cancellationToken: cancellationToken);
    }

    /// <summary>Writes a factory-reset request from an application-data path.</summary>
    public static Task WriteRequestAsync(string appDataPath, FactoryResetRequest request, CancellationToken cancellationToken = default) =>
        WriteRequestAsync(new AppDataStore(appDataPath), request, cancellationToken);

    /// <summary>Removes all Lineup-owned application and transient data when a reset request is present.</summary>
    public static bool ApplyPendingReset(
        IAppDataStore appDataStore,
        string? configuredXmltvPath,
        ITransientDataStore transientDataStore,
        IXmltvPublicationStore publicationStore)
    {
        if (!appDataStore.FileExists(appDataStore.FactoryResetRequestPath))
        {
            return false;
        }

        var request = JsonSerializer.Deserialize<FactoryResetRequest>(appDataStore.ReadAllText(appDataStore.FactoryResetRequestPath), JsonOptions)
            ?? throw new InvalidOperationException("The factory-reset request is invalid.");
        var configuredOutputPath = LineupSettingsService.ResolveConfiguredXmltvOutputPath(configuredXmltvPath);
        var configuredXmltvDirectory = ResolveConfiguredXmltvDirectory(configuredXmltvPath);

        publicationStore.Delete(configuredOutputPath);
        if (IsOwnedPersistedOutput(request.PersistedXmltvOutputPath, appDataStore.RootPath, configuredXmltvDirectory, configuredOutputPath))
        {
            publicationStore.Delete(Path.GetFullPath(request.PersistedXmltvOutputPath!));
        }
        foreach (var outputPath in request.PersistedGuideOutputPaths)
        {
            if (IsOwnedPersistedOutput(outputPath, appDataStore.RootPath, configuredXmltvDirectory, configuredOutputPath))
            {
                publicationStore.Delete(Path.GetFullPath(outputPath));
            }
        }

        transientDataStore.DeleteContents();
        appDataStore.DeleteContents();
        return true;
    }

    /// <summary>Applies a pending factory reset from an application-data path.</summary>
    public static bool ApplyPendingReset(string appDataPath, string? configuredXmltvPath, ITransientDataStore transientData) =>
        ApplyPendingReset(new AppDataStore(appDataPath), configuredXmltvPath, transientData, new XmltvPublicationStore());

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
    /// <summary>Gets the XMLTV output path active when reset was requested.</summary>
    public string? PersistedXmltvOutputPath { get; init; }

    /// <summary>Gets configured guide-output paths active when reset was requested.</summary>
    public IReadOnlyList<string> PersistedGuideOutputPaths { get; init; } = [];
}
