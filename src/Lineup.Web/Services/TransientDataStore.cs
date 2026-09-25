using Lineup.Core;

namespace Lineup.Web.Services;

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
        HlsRootPath = Path.Combine(RootPath, TransientDirectoryOwnership.HlsDirectoryName);
        SubtitleRootPath = Path.Combine(RootPath, SubtitleSidecarService.DirectoryName);
    }

    /// <summary>Gets the normalized transient root.</summary>
    public string RootPath { get; }

    /// <summary>Gets the shared HLS process-directory root.</summary>
    public string HlsRootPath { get; }

    /// <summary>Gets the shared subtitle process-directory root.</summary>
    public string SubtitleRootPath { get; }

    /// <inheritdoc />
    public string CreateHlsSessionDirectory(string sessionId)
    {
        ValidateSessionId(sessionId);
        var processRoot = TransientDirectoryOwnership.GetCurrentDirectory(HlsRootPath);
        var directory = _files.GetPath(Path.GetRelativePath(RootPath, Path.Combine(processRoot, sessionId)));
        _files.EnsureDirectory(directory);
        return directory;
    }

    /// <inheritdoc />
    public string ResetSubtitleWorkspace()
    {
        var directory = TransientDirectoryOwnership.CreateCurrentDirectory(SubtitleRootPath);
        if (_files.DirectoryExists(directory))
        {
            _files.DeleteDirectory(directory, recursive: true);
        }
        _files.EnsureDirectory(directory);
        return directory;
    }

    /// <inheritdoc />
    public string GetFilePath(string directoryPath, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (!string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal))
        {
            throw new ArgumentException("Managed file names cannot contain a path.", nameof(fileName));
        }
        return _files.GetPath(Path.GetRelativePath(RootPath, Path.Combine(directoryPath, fileName)));
    }

    /// <inheritdoc />
    public bool FileExists(string path) => _files.FileExists(path);

    /// <inheritdoc />
    public IReadOnlyList<string> EnumerateFiles(string directoryPath, string pattern) => _files.EnumerateFiles(directoryPath, pattern);

    /// <inheritdoc />
    public Stream? OpenRead(string path) => _files.OpenRead(path);

    /// <inheritdoc />
    public void DeleteFile(string path) => _files.DeleteFile(path);

    /// <inheritdoc />
    public void DeleteFiles(string directoryPath)
    {
        foreach (var path in _files.EnumerateFiles(directoryPath, "*"))
        {
            _files.DeleteFile(path);
        }
    }

    /// <inheritdoc />
    public void DeleteDirectory(string directoryPath) => _files.DeleteDirectory(directoryPath, recursive: true);

    /// <inheritdoc />
    public void DeleteContents() => _files.DeleteContents();

    /// <inheritdoc />
    public void DeleteInactiveOwnerDirectories()
    {
        DeleteInactiveOwnerDirectories(TransientDirectoryOwnership.GetOwnerStatus);
    }

    internal void DeleteInactiveOwnerDirectories(Func<string, TransientDirectoryOwnerStatus> getOwnerStatus)
    {
        DeleteInactiveDirectories(HlsRootPath, getOwnerStatus);
        DeleteInactiveDirectories(SubtitleRootPath, getOwnerStatus);
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
    internal static TransientDataStore CreateDefault() =>
        new(AppConstants.DefaultTransientPath);

    /// <summary>
    /// Resolves the configured transient root or default path.
    /// </summary>
    /// <param name="configuration">Application configuration.</param>
    /// <returns>The configured or default transient root.</returns>
    internal static string ResolveRootPath(IConfiguration configuration)
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

    private static void ValidateSessionId(string sessionId)
    {
        if (sessionId.Length is < 1 or > 64 || sessionId.Any(character => !char.IsAsciiLetterOrDigit(character)))
        {
            throw new ArgumentException("The transient session identifier is invalid.", nameof(sessionId));
        }
    }
}
