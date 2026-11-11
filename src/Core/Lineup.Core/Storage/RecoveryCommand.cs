using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Lineup.Core.Storage;

/// <summary>Runs operator-invoked offline workflows before either frontend starts background services.</summary>
public static class RecoveryCommand
{
    /// <summary>Handles --recovery backup|export|preview|restore path, or returns null for ordinary startup.</summary>
    public static async Task<int?> TryRunAsync(string[] args, string rootPath, string? databasePath = null, CancellationToken cancellationToken = default)
    {
        if (!args.Contains("--recovery", StringComparer.Ordinal))
        {
            return null;
        }
        if (args.Length != 3 || args[0] != "--recovery" || args[1] is not ("backup" or "export" or "preview" or "restore") || string.IsNullOrWhiteSpace(args[2]))
        {
            Console.Error.WriteLine("Usage: --recovery backup|export|preview|restore <path>. Configure the data directory through the host environment.");
            return 2;
        }
        if (databasePath is not null && !string.Equals(Path.GetFullPath(databasePath), new AppDataStore(rootPath, createRoot: false).DatabasePath,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            Console.Error.WriteLine("Recovery requires the database inside the application-data directory; external database overrides are not supported.");
            return 2;
        }
        try
        {
            var service = new RecoveryArchiveService(rootPath);
            switch (args[1])
            {
                case "backup":
                    await service.BackupAsync(args[2], cancellationToken);
                    Console.WriteLine("Sensitive recovery backup completed. Protect it like the original credentials and keys; it is not encrypted.");
                    break;
                case "export":
                    await service.ExportAsync(args[2], cancellationToken);
                    Console.WriteLine("Portable configuration exported. Adapter setup and authorization must be entered again.");
                    break;
                case "preview":
                    Console.WriteLine(JsonSerializer.Serialize(await service.PreviewAsync(args[2], cancellationToken)));
                    break;
                case "restore":
                    await service.RestoreAsync(args[2], cancellationToken);
                    Console.WriteLine("Recovery data installed into the empty directory. External certificates and host/environment configuration must be restored separately.");
                    break;
            }
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or SqliteException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine(exception is JsonException or SqliteException
                ? "Recovery failed: the archive or persisted state is invalid. No recovery archive was installed."
                : $"Recovery failed: {exception.Message}");
            return 2;
        }
    }
}
