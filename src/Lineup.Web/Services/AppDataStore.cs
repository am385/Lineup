using Lineup.Core;
using Lineup.Core.Storage;

namespace Lineup.Web.Services;

/// <summary>
/// Defines and safely manages Lineup-owned persistent application data.
/// </summary>
public sealed class AppDataStore : IAppDataStore
{
    private readonly ContainedFileStore _files;

    /// <summary>
    /// Initializes a store rooted at the specified persistent application-data directory.
    /// </summary>
    /// <param name="rootPath">Persistent application-data directory.</param>
    public AppDataStore(string rootPath)
    {
        _files = new ContainedFileStore(rootPath);
        RootPath = _files.RootPath;
        SettingsPath = GetPath(AppConstants.SettingsFileName);
        SettingsBackupPath = $"{SettingsPath}.bak";
        DatabasePath = GetPath(AppConstants.DefaultDatabaseFileName);
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
        return _files.GetPath(relativePath);
    }

    /// <summary>
    /// Returns whether a contained app-data file exists.
    /// </summary>
    /// <param name="path">Absolute contained path.</param>
    public bool FileExists(string path) => _files.FileExists(path);

    /// <summary>
    /// Returns whether a contained app-data directory exists.
    /// </summary>
    /// <param name="path">Absolute contained directory path.</param>
    public bool DirectoryExists(string path) => _files.DirectoryExists(path);

    /// <summary>
    /// Creates a contained app-data directory when needed.
    /// </summary>
    /// <param name="path">Absolute contained directory path.</param>
    public void EnsureDirectory(string path)
    {
        _files.EnsureDirectory(path);
    }

    /// <summary>
    /// Reads a contained app-data text file.
    /// </summary>
    /// <param name="path">Absolute contained path.</param>
    public string ReadAllText(string path) => _files.ReadAllText(path);

    /// <summary>
    /// Reads a contained app-data text file asynchronously.
    /// </summary>
    /// <param name="path">Absolute contained path.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default) =>
        _files.ReadAllTextAsync(path, cancellationToken);

    /// <summary>
    /// Atomically writes a contained app-data file, optionally replacing it with a backup.
    /// </summary>
    /// <param name="destinationPath">Absolute contained destination.</param>
    /// <param name="writeAsync">Writes the complete temporary file content.</param>
    /// <param name="backupPath">Optional contained replacement backup.</param>
    /// <param name="preserveBackup">Whether to preserve an existing backup instead of replacing it.</param>
    /// <param name="cancellationToken">Cancels the write before replacement.</param>
    public Task WriteAtomicallyAsync(string destinationPath, Func<Stream, CancellationToken, Task> writeAsync, string? backupPath = null, bool preserveBackup = false, CancellationToken cancellationToken = default) =>
        _files.WriteAtomicallyAsync(destinationPath, writeAsync, backupPath, preserveBackup, cancellationToken);

    /// <summary>
    /// Deletes a contained app-data file when present.
    /// </summary>
    /// <param name="path">Absolute contained path.</param>
    public void DeleteFile(string path)
    {
        _files.DeleteFile(path);
    }

    /// <summary>
    /// Deletes a contained app-data directory when present.
    /// </summary>
    /// <param name="path">Absolute contained directory path.</param>
    /// <param name="recursive">Whether to recursively remove its contents.</param>
    public void DeleteDirectory(string path, bool recursive = false)
    {
        _files.DeleteDirectory(path, recursive);
    }

    /// <summary>
    /// Deletes every file and directory beneath the app-data root.
    /// </summary>
    public void DeleteContents()
    {
        _files.DeleteContents();
    }

    /// <summary>
    /// Deletes matching files directly beneath a contained directory.
    /// </summary>
    /// <param name="directoryPath">Absolute contained directory.</param>
    /// <param name="pattern">Allowlisted top-level search pattern.</param>
    public void DeleteMatchingFiles(string directoryPath, string pattern)
    {
        _files.DeleteMatchingFiles(directoryPath, pattern);
    }

    /// <summary>
    /// Enumerates matching files directly beneath a contained directory.
    /// </summary>
    /// <param name="directoryPath">Absolute contained directory.</param>
    /// <param name="pattern">Top-level search pattern.</param>
    /// <returns>A stable snapshot of contained matching paths.</returns>
    public IReadOnlyList<string> EnumerateFiles(string directoryPath, string pattern)
    {
        return _files.EnumerateFiles(directoryPath, pattern);
    }

    /// <summary>
    /// Opens a contained app-data file for shared reading.
    /// </summary>
    /// <param name="path">Absolute contained file path.</param>
    /// <returns>A readable stream, or <see langword="null"/> when the file does not exist.</returns>
    public Stream? OpenRead(string path)
    {
        return _files.OpenRead(path);
    }

    /// <inheritdoc />
    public Task<IStagedFileWrite> StageWriteAsync(string destinationPath, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default) =>
        _files.StageWriteAsync(destinationPath, content, cancellationToken);
}
