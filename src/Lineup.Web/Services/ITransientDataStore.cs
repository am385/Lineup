namespace Lineup.Web.Services;

/// <summary>
/// Defines Lineup-owned disposable data storage.
/// </summary>
public interface ITransientDataStore
{
    /// <summary>Gets the normalized transient root.</summary>
    string RootPath { get; }

    /// <summary>Gets the shared HLS process-directory root.</summary>
    string HlsRootPath { get; }

    /// <summary>Gets the shared subtitle process-directory root.</summary>
    string SubtitleRootPath { get; }

    /// <summary>Creates and returns a contained HLS session directory.</summary>
    string CreateHlsSessionDirectory(string sessionId);

    /// <summary>Creates a fresh process-owned subtitle workspace.</summary>
    string ResetSubtitleWorkspace();

    /// <summary>Returns a validated file path within a managed directory.</summary>
    string GetFilePath(string directoryPath, string fileName);

    /// <summary>Returns whether a managed file exists.</summary>
    bool FileExists(string path);

    /// <summary>Enumerates managed files matching a top-level pattern.</summary>
    IReadOnlyList<string> EnumerateFiles(string directoryPath, string pattern);

    /// <summary>Opens a managed file for shared reading.</summary>
    Stream? OpenRead(string path);

    /// <summary>Deletes a managed file when present.</summary>
    void DeleteFile(string path);

    /// <summary>Deletes every file directly inside a managed directory.</summary>
    void DeleteFiles(string directoryPath);

    /// <summary>Deletes a managed directory when present.</summary>
    void DeleteDirectory(string directoryPath);

    /// <summary>Removes inactive process-owned transient directories.</summary>
    void DeleteInactiveOwnerDirectories();
}
