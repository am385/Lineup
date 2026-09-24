namespace Lineup.Core.Storage;

/// <summary>
/// Defines Lineup-owned persistent application-data storage.
/// </summary>
public interface IAppDataStore
{
    /// <summary>Gets the normalized persistent application-data root.</summary>
    string RootPath { get; }

    /// <summary>Gets the primary settings document path.</summary>
    string SettingsPath { get; }

    /// <summary>Gets the settings backup path.</summary>
    string SettingsBackupPath { get; }

    /// <summary>Gets the SQLite guide database path.</summary>
    string DatabasePath { get; }

    /// <summary>Gets the rolling-log directory path.</summary>
    string LogDirectoryPath { get; }

    /// <summary>Gets the ASP.NET Core Data Protection key directory path.</summary>
    string DataProtectionKeysPath { get; }

    /// <summary>Gets the durable Factory Reset request path.</summary>
    string FactoryResetRequestPath { get; }

    /// <summary>Returns a normalized path beneath the app-data root.</summary>
    string GetPath(string relativePath);

    /// <summary>Returns whether a contained app-data file exists.</summary>
    bool FileExists(string path);

    /// <summary>Returns whether a contained app-data directory exists.</summary>
    bool DirectoryExists(string path);

    /// <summary>Creates a contained app-data directory when needed.</summary>
    void EnsureDirectory(string path);

    /// <summary>Reads a contained app-data text file.</summary>
    string ReadAllText(string path);

    /// <summary>Reads a contained app-data text file asynchronously.</summary>
    Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Atomically writes a contained app-data file.</summary>
    Task WriteAtomicallyAsync(
        string destinationPath,
        Func<Stream, CancellationToken, Task> writeAsync,
        string? backupPath = null,
        bool preserveBackup = false,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes a contained app-data file when present.</summary>
    void DeleteFile(string path);

    /// <summary>Deletes a contained app-data directory when present.</summary>
    void DeleteDirectory(string path, bool recursive = false);

    /// <summary>Deletes matching files directly beneath a contained directory.</summary>
    void DeleteMatchingFiles(string directoryPath, string pattern);

    /// <summary>Enumerates matching files directly beneath a contained directory.</summary>
    IReadOnlyList<string> EnumerateFiles(string directoryPath, string pattern);

    /// <summary>Opens a contained app-data file for shared reading.</summary>
    Stream? OpenRead(string path);

    /// <summary>Stages a contained file for a later atomic commit.</summary>
    Task<IStagedFileWrite> StageWriteAsync(string destinationPath, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents a fully written file awaiting atomic publication.
/// </summary>
public interface IStagedFileWrite : IDisposable
{
    /// <summary>Atomically publishes the staged file.</summary>
    void Commit();
}
