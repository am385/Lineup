namespace Lineup.App.Web.Services;

/// <summary>
/// Provides read-only server filesystem browsing.
/// </summary>
public sealed class FileSystemBrowser : IFileSystemBrowser
{
    /// <summary>Gets current directory.</summary>
    /// <returns>The operation's result.</returns>
    public string GetCurrentDirectory() => Directory.GetCurrentDirectory();

    /// <summary>Executes directory exists.</summary>
    /// <param name="path">Path used by the operation.</param>
    /// <returns>The operation's result.</returns>
    public bool DirectoryExists(string path) => Directory.Exists(path);

    /// <summary>Gets directories.</summary>
    /// <param name="path">Path used by the operation.</param>
    /// <returns>The operation's result.</returns>
    public IReadOnlyList<string> GetDirectories(string path) =>
        Directory.GetDirectories(path).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>Gets parent.</summary>
    /// <param name="path">Path used by the operation.</param>
    /// <returns>The operation's result.</returns>
    public string? GetParent(string path) => Directory.GetParent(path)?.FullName;

    /// <summary>Resolves path.</summary>
    /// <param name="path">Path used by the operation.</param>
    /// <returns>The operation's result.</returns>
    public string ResolvePath(string path) =>
        Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(GetCurrentDirectory(), path));
}
