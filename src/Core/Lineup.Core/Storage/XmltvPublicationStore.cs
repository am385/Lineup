namespace Lineup.Core.Storage;

/// <summary>
/// Provides default atomic filesystem publication for hosts that do not register a specialized XMLTV store.
/// </summary>
public sealed class XmltvPublicationStore : IXmltvPublicationStore
{
    /// <summary>Executes exists.</summary>
    /// <param name="publicationPath">The publication path value.</param>
    /// <returns>The operation's result.</returns>
    public bool Exists(string publicationPath) => File.Exists(NormalizePath(publicationPath));

    /// <summary>Opens read.</summary>
    /// <param name="publicationPath">The publication path value.</param>
    /// <returns>The operation's result.</returns>
    public Stream? OpenRead(string publicationPath)
    {
        var path = NormalizePath(publicationPath);
        return File.Exists(path)
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)
            : null;
    }

    /// <summary>Gets last write time UTC.</summary>
    /// <param name="publicationPath">The publication path value.</param>
    /// <returns>The operation's result.</returns>
    public DateTime? GetLastWriteTimeUtc(string publicationPath)
    {
        var path = NormalizePath(publicationPath);
        return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
    }

    /// <summary>Executes publish asynchronously.</summary>
    /// <param name="publicationPath">The publication path value.</param>
    /// <param name="writeAsync">The write async value.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task that completes when the operation has finished.</returns>
    public async Task PublishAsync(string publicationPath, Func<Stream, CancellationToken, Task> writeAsync, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writeAsync);
        var destinationPath = NormalizePath(publicationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var temporaryPath = $"{destinationPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await writeAsync(stream, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    /// <summary>Deletes the XMLTV publication store.</summary>
    /// <param name="publicationPath">The publication path value.</param>
    public void Delete(string publicationPath)
    {
        var path = NormalizePath(publicationPath);
        var directory = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(directory))
        {
            return;
        }

        File.Delete(path);
        foreach (var temporaryPath in Directory.EnumerateFiles(directory, $"{Path.GetFileName(path)}.*.tmp", SearchOption.TopDirectoryOnly))
        {
            File.Delete(temporaryPath);
        }
    }

    private static string NormalizePath(string publicationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicationPath);
        return Path.GetFullPath(publicationPath);
    }
}
