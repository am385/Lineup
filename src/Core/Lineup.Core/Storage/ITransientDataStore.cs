namespace Lineup.Core.Storage;

/// <summary>
/// Defines Lineup-owned disposable data storage.
/// </summary>
public interface ITransientDataStore
{
    /// <summary>Gets the normalized transient root.</summary>
    string RootPath { get; }

    /// <summary>Gets the contained root for a named transient workspace.</summary>
    string GetWorkspaceRoot(string workspaceName);

    /// <summary>Creates a contained session directory beneath a process-owned named workspace.</summary>
    string CreateSessionDirectory(string workspaceName, string sessionId);

    /// <summary>Creates a fresh process-owned named workspace.</summary>
    string ResetProcessWorkspace(string workspaceName);

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

    /// <summary>Deletes every file and directory beneath the transient root.</summary>
    void DeleteContents();

    /// <summary>Removes inactive process-owned transient directories.</summary>
    void DeleteInactiveOwnerDirectories();
}
