using Microsoft.Extensions.Configuration;

namespace Lineup.Core.Storage;

/// <summary>
/// Provides contained roots for Lineup-owned transient data.
/// </summary>
public sealed class TransientDataStore : ITransientDataStore
{
    private readonly ContainedFileStore _files;

    /// <summary>
    /// Initializes a transient data store.
    /// </summary>
    /// <param name="rootPath">Application-owned transient root.</param>
    public TransientDataStore(string rootPath)
    {
        _files = new ContainedFileStore(rootPath);
        RootPath = _files.RootPath;
    }

    /// <summary>Gets the normalized transient root.</summary>
    public string RootPath { get; }

    /// <summary>Gets workspace root.</summary>
    /// <param name="workspaceName">The workspace name value.</param>
    /// <returns>The operation's result.</returns>
    public string GetWorkspaceRoot(string workspaceName)
    {
        ValidatePathComponent(workspaceName, nameof(workspaceName));
        return _files.GetPath(workspaceName);
    }

    /// <summary>Creates session directory.</summary>
    /// <param name="workspaceName">The workspace name value.</param>
    /// <param name="sessionId">Playback session identity.</param>
    /// <returns>The operation's result.</returns>
    public string CreateSessionDirectory(string workspaceName, string sessionId)
    {
        ValidatePathComponent(sessionId, nameof(sessionId));
        var processRoot = TransientDirectoryOwnership.GetCurrentDirectory(GetWorkspaceRoot(workspaceName));
        var directory = _files.GetPath(Path.GetRelativePath(RootPath, Path.Combine(processRoot, sessionId)));
        _files.EnsureDirectory(directory);
        return directory;
    }

    /// <summary>Resets process workspace.</summary>
    /// <param name="workspaceName">The workspace name value.</param>
    /// <returns>The operation's result.</returns>
    public string ResetProcessWorkspace(string workspaceName)
    {
        var directory = TransientDirectoryOwnership.CreateCurrentDirectory(GetWorkspaceRoot(workspaceName));
        if (_files.DirectoryExists(directory))
        {
            _files.DeleteDirectory(directory, recursive: true);
        }
        _files.EnsureDirectory(directory);
        return directory;
    }

    /// <summary>Gets file path.</summary>
    /// <param name="directoryPath">The directory path value.</param>
    /// <param name="fileName">The file name value.</param>
    /// <returns>The operation's result.</returns>
    public string GetFilePath(string directoryPath, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (!string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal))
        {
            throw new ArgumentException("Managed file names cannot contain a path.", nameof(fileName));
        }
        return _files.GetPath(Path.GetRelativePath(RootPath, Path.Combine(directoryPath, fileName)));
    }

    /// <summary>Executes file exists.</summary>
    /// <param name="path">Path used by the operation.</param>
    /// <returns>The operation's result.</returns>
    public bool FileExists(string path) => _files.FileExists(path);

    /// <summary>Executes enumerate files.</summary>
    /// <param name="directoryPath">The directory path value.</param>
    /// <param name="pattern">The pattern value.</param>
    /// <returns>The operation's result.</returns>
    public IReadOnlyList<string> EnumerateFiles(string directoryPath, string pattern) => _files.EnumerateFiles(directoryPath, pattern);

    /// <summary>Opens read.</summary>
    /// <param name="path">Path used by the operation.</param>
    /// <returns>The operation's result.</returns>
    public Stream? OpenRead(string path) => _files.OpenRead(path);

    /// <summary>Deletes file.</summary>
    /// <param name="path">Path used by the operation.</param>
    public void DeleteFile(string path) => _files.DeleteFile(path);

    /// <summary>Deletes files.</summary>
    /// <param name="directoryPath">The directory path value.</param>
    public void DeleteFiles(string directoryPath)
    {
        foreach (var path in _files.EnumerateFiles(directoryPath, "*"))
        {
            _files.DeleteFile(path);
        }
    }

    /// <summary>Deletes directory.</summary>
    /// <param name="directoryPath">The directory path value.</param>
    public void DeleteDirectory(string directoryPath) => _files.DeleteDirectory(directoryPath, recursive: true);

    /// <summary>Deletes contents.</summary>
    public void DeleteContents() => _files.DeleteContents();

    /// <summary>Deletes inactive owner directories.</summary>
    public void DeleteInactiveOwnerDirectories()
    {
        DeleteInactiveOwnerDirectories(TransientDirectoryOwnership.GetOwnerStatus);
    }

    /// <summary>
    /// Deletes transient owner directories identified as inactive by the supplied status resolver.
    /// </summary>
    /// <param name="getOwnerStatus">Resolves the status of a transient owner directory.</param>
    public void DeleteInactiveOwnerDirectories(Func<string, TransientDirectoryOwnerStatus> getOwnerStatus)
    {
        ArgumentNullException.ThrowIfNull(getOwnerStatus);
        if (!_files.DirectoryExists(RootPath))
        {
            return;
        }

        foreach (var workspaceRoot in Directory.EnumerateDirectories(RootPath))
        {
            DeleteInactiveDirectories(workspaceRoot, getOwnerStatus);
        }
    }

    /// <summary>
    /// Creates the configured transient data store.
    /// </summary>
    /// <param name="configuration">Application configuration.</param>
    /// <returns>The normalized transient data store.</returns>
    public static TransientDataStore Create(IConfiguration configuration) =>
        new(ResolveRootPath(configuration));

    /// <summary>
    /// Creates the default transient data store.
    /// </summary>
    /// <returns>The default transient data store.</returns>
    public static TransientDataStore CreateDefault() =>
        new(AppConstants.DefaultTransientPath);

    /// <summary>
    /// Resolves the configured transient root or default path.
    /// </summary>
    /// <param name="configuration">Application configuration.</param>
    /// <returns>The configured or default transient root.</returns>
    public static string ResolveRootPath(IConfiguration configuration)
    {
        var configuredPath = configuration[AppConstants.TransientPathConfigKey];
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath.Trim();
        }

        var configuredAppDataPath = configuration[AppConstants.AppDataPathConfigKey];
        return string.IsNullOrWhiteSpace(configuredAppDataPath)
            ? AppConstants.DefaultTransientPath
            : Path.Combine(Path.GetTempPath(), "lineup");
    }

    private void DeleteInactiveDirectories(string rootDirectory, Func<string, TransientDirectoryOwnerStatus> getOwnerStatus)
    {
        if (!_files.DirectoryExists(rootDirectory))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(rootDirectory))
        {
            if (getOwnerStatus(Path.GetFileName(directory)) != TransientDirectoryOwnerStatus.Inactive)
            {
                continue;
            }

            try
            {
                _files.DeleteDirectory(directory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void ValidatePathComponent(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 64 ||
            !string.Equals(value, Path.GetFileName(value), StringComparison.Ordinal) ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
        {
            throw new ArgumentException("The transient workspace or session identifier is invalid.", parameterName);
        }
    }
}
