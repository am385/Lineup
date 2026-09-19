using Lineup.Core;

namespace Lineup.Web.Services;

/// <summary>
/// Defines and safely manages Lineup-owned persistent application data.
/// </summary>
public sealed class AppDataStore
{
    private readonly StringComparison _pathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    /// <summary>
    /// Initializes a store rooted at the specified persistent application-data directory.
    /// </summary>
    /// <param name="rootPath">Persistent application-data directory.</param>
    public AppDataStore(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        RootPath = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(RootPath);
        SettingsPath = GetPath(AppConstants.SettingsFileName);
        SettingsBackupPath = $"{SettingsPath}.bak";
        DatabasePath = GetPath(AppConstants.DefaultDatabaseFileName);
        GuideCachePath = Path.ChangeExtension(DatabasePath, ".xmltv");
        ChannelLineupPath = Path.ChangeExtension(DatabasePath, ".channels.json");
        LogDirectoryPath = GetPath(AppConstants.LogDirectoryName);
        DataProtectionKeysPath = GetPath(AppConstants.DataProtectionKeysDirectoryName);
        FactoryResetRequestPath = GetPath(FactoryResetCoordinator.RequestFileName);
    }

    /// <summary>
    /// Gets the normalized persistent application-data root.
    /// </summary>
    public string RootPath { get; }

    /// <summary>
    /// Gets the primary settings document path.
    /// </summary>
    public string SettingsPath { get; }

    /// <summary>
    /// Gets the settings backup path.
    /// </summary>
    public string SettingsBackupPath { get; }

    /// <summary>
    /// Gets the SQLite guide database path.
    /// </summary>
    public string DatabasePath { get; }

    /// <summary>
    /// Gets the canonical downloaded guide cache path.
    /// </summary>
    public string GuideCachePath { get; }

    /// <summary>
    /// Gets the persisted physical tuner channel lineup path.
    /// </summary>
    public string ChannelLineupPath { get; }

    /// <summary>
    /// Gets the rolling-log directory path.
    /// </summary>
    public string LogDirectoryPath { get; }

    /// <summary>
    /// Gets the ASP.NET Core Data Protection key directory path.
    /// </summary>
    public string DataProtectionKeysPath { get; }

    /// <summary>
    /// Gets the durable Factory Reset request path.
    /// </summary>
    public string FactoryResetRequestPath { get; }

    /// <summary>
    /// Creates a store from the configured app-data override or default path.
    /// </summary>
    /// <param name="configuration">Application startup configuration.</param>
    /// <returns>The resolved singleton store.</returns>
    public static AppDataStore Create(IConfiguration configuration)
    {
        string rootPath = ResolveRootPath(configuration);
        return new AppDataStore(rootPath);
    }

    /// <summary>
    /// Resolves the configured app-data override or default path without accessing storage.
    /// </summary>
    /// <param name="configuration">Application startup configuration.</param>
    /// <returns>The configured or default application-data root.</returns>
    internal static string ResolveRootPath(IConfiguration configuration)
    {
        var configuredPath = configuration[AppConstants.AppDataPathConfigKey];
        return string.IsNullOrWhiteSpace(configuredPath) ? AppConstants.DefaultAppDataPath : configuredPath.Trim();
    }

    /// <summary>
    /// Returns a normalized path beneath the app-data root.
    /// </summary>
    /// <param name="relativePath">Relative path beneath the root.</param>
    /// <returns>The contained absolute path.</returns>
    public string GetPath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException("App-data paths must be relative.", nameof(relativePath));
        }

        return EnsureContainedPath(Path.Combine(RootPath, relativePath));
    }

    /// <summary>
    /// Returns whether a contained app-data file exists.
    /// </summary>
    /// <param name="path">Absolute contained path.</param>
    public bool FileExists(string path) => File.Exists(EnsureContainedPath(path));

    /// <summary>
    /// Returns whether a contained app-data directory exists.
    /// </summary>
    /// <param name="path">Absolute contained directory path.</param>
    public bool DirectoryExists(string path) => Directory.Exists(EnsureContainedPath(path));

    /// <summary>
    /// Creates a contained app-data directory when needed.
    /// </summary>
    /// <param name="path">Absolute contained directory path.</param>
    public void EnsureDirectory(string path)
    {
        Directory.CreateDirectory(EnsureContainedPath(path));
    }

    /// <summary>
    /// Reads a contained app-data text file.
    /// </summary>
    /// <param name="path">Absolute contained path.</param>
    public string ReadAllText(string path) => File.ReadAllText(EnsureContainedPath(path));

    /// <summary>
    /// Reads a contained app-data text file asynchronously.
    /// </summary>
    /// <param name="path">Absolute contained path.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default) =>
        File.ReadAllTextAsync(EnsureContainedPath(path), cancellationToken);

    /// <summary>
    /// Atomically writes a contained app-data file, optionally replacing it with a backup.
    /// </summary>
    /// <param name="destinationPath">Absolute contained destination.</param>
    /// <param name="writeAsync">Writes the complete temporary file content.</param>
    /// <param name="backupPath">Optional contained replacement backup.</param>
    /// <param name="preserveBackup">Whether to preserve an existing backup instead of replacing it.</param>
    /// <param name="cancellationToken">Cancels the write before replacement.</param>
    public async Task WriteAtomicallyAsync(string destinationPath, Func<Stream, CancellationToken, Task> writeAsync, string? backupPath = null, bool preserveBackup = false, CancellationToken cancellationToken = default)
    {
        var destination = EnsureContainedPath(destinationPath);
        var backup = backupPath == null ? null : EnsureContainedPath(backupPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporaryPath = EnsureContainedPath($"{destination}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await writeAsync(stream, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            if (!File.Exists(destination))
            {
                File.Move(temporaryPath, destination);
            }
            else if (backup == null || preserveBackup)
            {
                File.Move(temporaryPath, destination, overwrite: true);
            }
            else
            {
                File.Replace(temporaryPath, destination, backup, ignoreMetadataErrors: true);
            }
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    /// <summary>
    /// Deletes a contained app-data file when present.
    /// </summary>
    /// <param name="path">Absolute contained path.</param>
    public void DeleteFile(string path)
    {
        File.Delete(EnsureContainedPath(path));
    }

    /// <summary>
    /// Deletes a contained app-data directory when present.
    /// </summary>
    /// <param name="path">Absolute contained directory path.</param>
    /// <param name="recursive">Whether to recursively remove its contents.</param>
    public void DeleteDirectory(string path, bool recursive = false)
    {
        var directory = EnsureContainedPath(path);
        if (PathsEqual(directory, RootPath))
        {
            throw new InvalidOperationException("The app-data root cannot be deleted.");
        }

        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive);
        }
    }

    /// <summary>
    /// Deletes matching files directly beneath a contained directory.
    /// </summary>
    /// <param name="directoryPath">Absolute contained directory.</param>
    /// <param name="pattern">Allowlisted top-level search pattern.</param>
    public void DeleteMatchingFiles(string directoryPath, string pattern)
    {
        var directory = EnsureContainedPath(directoryPath);
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly))
        {
            File.Delete(EnsureContainedPath(path));
        }
    }

    /// <summary>
    /// Enumerates matching files directly beneath a contained directory.
    /// </summary>
    /// <param name="directoryPath">Absolute contained directory.</param>
    /// <param name="pattern">Top-level search pattern.</param>
    /// <returns>A stable snapshot of contained matching paths.</returns>
    public IReadOnlyList<string> EnumerateFiles(string directoryPath, string pattern)
    {
        var directory = EnsureContainedPath(directoryPath);
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly)
                .Select(EnsureContainedPath)
                .ToArray()
            : [];
    }

    /// <summary>
    /// Opens a contained app-data file for shared reading.
    /// </summary>
    /// <param name="path">Absolute contained file path.</param>
    /// <returns>A readable stream, or <see langword="null"/> when the file does not exist.</returns>
    public Stream? OpenRead(string path)
    {
        var filePath = EnsureContainedPath(path);
        return File.Exists(filePath)
            ? new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)
            : null;
    }

    private string EnsureContainedPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var relativePath = Path.GetRelativePath(RootPath, fullPath);
        if (Path.IsPathRooted(relativePath) ||
            relativePath == ".." ||
            relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Path '{fullPath}' is outside the app-data root.");
        }

        return fullPath;
    }

    private bool PathsEqual(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(left), Path.TrimEndingDirectorySeparator(right), _pathComparison);

}
