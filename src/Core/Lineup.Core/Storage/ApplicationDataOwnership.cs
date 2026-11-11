namespace Lineup.Core.Storage;

/// <summary>Excludes other updated Lineup hosts and offline recovery commands from one application-data directory.</summary>
public static class ApplicationDataOwnership
{
    /// <summary>Retains an external sibling lock so factory reset cannot remove the ownership fence.</summary>
    public static FileStream Acquire(string rootPath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var parent = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("An application-data filesystem root cannot be locked.");
        Directory.CreateDirectory(parent);
        RejectLinks(parent);
        RejectLinks(root);
        var path = Path.Combine(parent, $".{Path.GetFileName(root)}.lineup.lock");
        RejectLinks(path);
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception)
        {
            throw new IOException("Application data is in use or its ownership lock is unavailable. Stop all Lineup hosts before recovery.", exception);
        }
    }

    /// <summary>Rejects symbolic links and reparse points along an existing path.</summary>
    internal static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Recovery and application-data ownership do not support symbolic links or reparse points.");
            }
        }
    }
}
