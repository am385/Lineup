namespace Lineup.Core.Storage;

/// <summary>
/// Defines publication operations for a configured XMLTV output outside app-data containment.
/// </summary>
public interface IXmltvPublicationStore
{
    /// <summary>Returns whether the configured publication exists.</summary>
    bool Exists(string publicationPath);

    /// <summary>Opens a configured publication for shared reading.</summary>
    Stream? OpenRead(string publicationPath);

    /// <summary>Gets the publication modification time when available.</summary>
    DateTime? GetLastWriteTimeUtc(string publicationPath);

    /// <summary>Atomically publishes XMLTV content to the configured path.</summary>
    Task PublishAsync(string publicationPath, Func<Stream, CancellationToken, Task> writeAsync, CancellationToken cancellationToken = default);

    /// <summary>Deletes the configured publication and abandoned temporary files.</summary>
    void Delete(string publicationPath);
}
