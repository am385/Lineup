namespace Lineup.Core;

/// <summary>
/// Removes a Lineup SQLite database and its SQLite-owned sidecar files before startup.
/// </summary>
public static class DatabaseFactoryReset
{
    private static readonly string[] _sidecarSuffixes = ["-shm", "-wal", "-journal"];

    /// <summary>
    /// Deletes the identified database and its SQLite sidecar files.
    /// </summary>
    /// <param name="databasePath">Database path explicitly confirmed by the operator.</param>
    public static void Reset(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var fullPath = Path.GetFullPath(databasePath);
        File.Delete(fullPath);
        foreach (var suffix in _sidecarSuffixes)
        {
            File.Delete($"{fullPath}{suffix}");
        }
    }
}
