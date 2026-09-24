namespace Lineup.Web.Services;

/// <summary>
/// Provides read-only access to server directories selected by an administrator.
/// </summary>
public interface IFileSystemBrowser
{
    /// <summary>Gets the process working directory.</summary>
    string GetCurrentDirectory();

    /// <summary>Returns whether a directory exists.</summary>
    bool DirectoryExists(string path);

    /// <summary>Lists immediate child directories.</summary>
    IReadOnlyList<string> GetDirectories(string path);

    /// <summary>Gets the parent directory, when one exists.</summary>
    string? GetParent(string path);

    /// <summary>Resolves an absolute or working-directory-relative path.</summary>
    string ResolvePath(string path);
}
