namespace Lineup.Web.Services;

/// <summary>
/// Provides read-only server filesystem browsing.
/// </summary>
public sealed class FileSystemBrowser : IFileSystemBrowser
{
    /// <inheritdoc />
    public string GetCurrentDirectory() => Directory.GetCurrentDirectory();

    /// <inheritdoc />
    public bool DirectoryExists(string path) => Directory.Exists(path);

    /// <inheritdoc />
    public IReadOnlyList<string> GetDirectories(string path) =>
        Directory.GetDirectories(path).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToArray();

    /// <inheritdoc />
    public string? GetParent(string path) => Directory.GetParent(path)?.FullName;

    /// <inheritdoc />
    public string ResolvePath(string path) =>
        Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(GetCurrentDirectory(), path));
}
