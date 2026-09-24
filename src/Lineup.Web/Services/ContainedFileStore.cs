using Lineup.Core.Storage;

namespace Lineup.Web.Services;

/// <summary>
/// Implements shared filesystem mechanics for a normalized, contained root.
/// </summary>
internal sealed class ContainedFileStore
{
    private readonly StringComparison _pathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    /// <summary>
    /// Initializes a contained filesystem root.
    /// </summary>
    /// <param name="rootPath">Root containing every managed path.</param>
    public ContainedFileStore(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        RootPath = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(RootPath);
    }

    /// <summary>Gets the normalized storage root.</summary>
    public string RootPath { get; }

    /// <summary>Returns a normalized path beneath the root.</summary>
    public string GetPath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException("Contained paths must be relative.", nameof(relativePath));
        }

        return EnsureContainedPath(Path.Combine(RootPath, relativePath));
    }

    /// <summary>Returns whether a contained file exists.</summary>
    public bool FileExists(string path) => File.Exists(EnsureContainedPath(path));

    /// <summary>Returns whether a contained directory exists.</summary>
    public bool DirectoryExists(string path) => Directory.Exists(EnsureContainedPath(path));

    /// <summary>Creates a contained directory when needed.</summary>
    public void EnsureDirectory(string path) => Directory.CreateDirectory(EnsureContainedPath(path));

    /// <summary>Reads a contained text file.</summary>
    public string ReadAllText(string path) => File.ReadAllText(EnsureContainedPath(path));

    /// <summary>Reads a contained text file asynchronously.</summary>
    public Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default) =>
        File.ReadAllTextAsync(EnsureContainedPath(path), cancellationToken);

    /// <summary>Atomically writes a contained file.</summary>
    public async Task WriteAtomicallyAsync(
        string destinationPath,
        Func<Stream, CancellationToken, Task> writeAsync,
        string? backupPath = null,
        bool preserveBackup = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writeAsync);
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

    /// <summary>Deletes a contained file when present.</summary>
    public void DeleteFile(string path) => File.Delete(EnsureContainedPath(path));

    /// <summary>Deletes a contained directory when present.</summary>
    public void DeleteDirectory(string path, bool recursive = false)
    {
        var directory = EnsureContainedPath(path);
        if (PathsEqual(directory, RootPath))
        {
            throw new InvalidOperationException("The storage root cannot be deleted.");
        }

        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive);
        }
    }

    /// <summary>Deletes matching files directly beneath a contained directory.</summary>
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

    /// <summary>Enumerates matching files directly beneath a contained directory.</summary>
    public IReadOnlyList<string> EnumerateFiles(string directoryPath, string pattern)
    {
        var directory = EnsureContainedPath(directoryPath);
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly)
                .Select(EnsureContainedPath)
                .ToArray()
            : [];
    }

    /// <summary>Opens a contained file for shared reading.</summary>
    public Stream? OpenRead(string path)
    {
        var filePath = EnsureContainedPath(path);
        return File.Exists(filePath)
            ? new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)
            : null;
    }

    /// <summary>Stages a contained file for a later atomic commit.</summary>
    public async Task<IStagedFileWrite> StageWriteAsync(string destinationPath, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        var destination = EnsureContainedPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporaryPath = EnsureContainedPath($"{destination}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, content.ToArray(), cancellationToken);
            return new StagedFileWrite(destination, temporaryPath);
        }
        catch
        {
            File.Delete(temporaryPath);
            throw;
        }
    }

    private string EnsureContainedPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var relativePath = Path.GetRelativePath(RootPath, fullPath);
        if (Path.IsPathRooted(relativePath) ||
            relativePath == ".." ||
            relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Path '{fullPath}' is outside the storage root.");
        }

        return fullPath;
    }

    private bool PathsEqual(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(left), Path.TrimEndingDirectorySeparator(right), _pathComparison);

    private sealed class StagedFileWrite : IStagedFileWrite
    {
        private readonly string _destinationPath;
        private string? _temporaryPath;

        public StagedFileWrite(string destinationPath, string temporaryPath)
        {
            _destinationPath = destinationPath;
            _temporaryPath = temporaryPath;
        }

        public void Commit()
        {
            var temporaryPath = Volatile.Read(ref _temporaryPath);
            ObjectDisposedException.ThrowIf(temporaryPath == null, this);
            File.Move(temporaryPath, _destinationPath, overwrite: true);
            Interlocked.CompareExchange(ref _temporaryPath, null, temporaryPath);
        }

        public void Dispose()
        {
            var temporaryPath = Interlocked.Exchange(ref _temporaryPath, null);
            if (temporaryPath != null)
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
