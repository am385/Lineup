using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Lineup.Core.Common.Streaming;
using Lineup.Core.Settings;
using Microsoft.Data.Sqlite;

namespace Lineup.Core.Storage;

/// <summary>Describes a validated sensitive archive without returning its contents or credentials.</summary>
/// <param name="Release">Exact producing release identifier.</param>
/// <param name="CreatedAtUtc">Backup creation time.</param>
/// <param name="Files">Validated relative file names.</param>
/// <param name="ContainsSensitiveData">Whether operator-controlled secrets and keys may be present.</param>
public sealed record RecoveryPreview(string Release, DateTimeOffset CreatedAtUtc, IReadOnlyList<string> Files, bool ContainsSensitiveData = true);

/// <summary>Creates offline same-release backups and validates an entire staged restore before atomic directory installation.</summary>
public sealed class RecoveryArchiveService
{
    private const long MaximumArchiveBytes = 2L * 1024 * 1024 * 1024;
    private const int MaximumEntries = 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string Release = typeof(RecoveryArchiveService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? throw new InvalidOperationException("The recovery release identifier is unavailable.");
    private readonly AppDataStore _store;

    /// <summary>Initializes recovery for an explicit application-data root.</summary>
    public RecoveryArchiveService(string rootPath) => _store = new AppDataStore(rootPath, createRoot: false);

    /// <summary>Creates a sensitive archive outside reset-owned storage; an interrupted write never replaces the destination.</summary>
    public async Task BackupAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        using var ownership = ApplicationDataOwnership.Acquire(_store.RootPath);
        RequireCompatible(_store);
        var destination = ExternalDestination(destinationPath);
        var stage = CreateStage();
        try
        {
            foreach (var name in new[] { AppConstants.SettingsFileName, "web-settings.json" })
            {
                var source = _store.GetPath(name);
                if (File.Exists(source))
                {
                    await CopyAsync(source, Path.Combine(stage, name), cancellationToken);
                }
            }
            if (File.Exists(_store.DatabasePath))
            {
                using var source = OpenDatabase(_store.DatabasePath, SqliteOpenMode.ReadOnly);
                using var target = OpenDatabase(Path.Combine(stage, AppConstants.DefaultDatabaseFileName), SqliteOpenMode.ReadWriteCreate);
                cancellationToken.ThrowIfCancellationRequested();
                source.BackupDatabase(target);
                using var journal = target.CreateCommand();
                journal.CommandText = "PRAGMA journal_mode=DELETE;";
                if (!string.Equals(journal.ExecuteScalar() as string, "delete", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("The recovery database could not be made self-contained.");
                }
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (Directory.Exists(_store.DataProtectionKeysPath))
            {
                ApplicationDataOwnership.RejectLinks(_store.DataProtectionKeysPath);
                var keys = Directory.GetFiles(_store.DataProtectionKeysPath, "*.xml", SearchOption.TopDirectoryOnly);
                if (keys.Length > MaximumEntries - 4)
                {
                    throw new InvalidDataException("The recovery key count exceeds the supported archive limit.");
                }
                foreach (var key in keys)
                {
                    await CopyAsync(key, Path.Combine(stage, AppConstants.DataProtectionKeysDirectoryName, Path.GetFileName(key)), cancellationToken);
                }
            }
            await ValidateStateAsync(stage, cancellationToken);
            var files = new Dictionary<string, Entry>(StringComparer.Ordinal);
            long total = 0;
            foreach (var path in Directory.GetFiles(stage, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetRelativePath(stage, path).Replace('\\', '/');
                var length = new FileInfo(path).Length;
                total = checked(total + length);
                if (!Allowed(name) || total > MaximumArchiveBytes || name != AppConstants.DefaultDatabaseFileName && length > 8 * 1024 * 1024)
                {
                    throw new InvalidDataException("The recovery state exceeds the supported paths or archive/file-size limits.");
                }
                files.Add(name, new Entry(length, await HashAsync(path, cancellationToken)));
            }
            var manifest = new Manifest(1, Release, DateTimeOffset.UtcNow, files);
            var pending = destination + $".{Guid.NewGuid():N}.pending";
            try
            {
                await using (var output = CreatePrivateFile(pending))
                {
                    using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                    {
                        await using (var stream = archive.CreateEntry("manifest.json").Open())
                        {
                            await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, cancellationToken);
                        }
                        foreach (var name in files.Keys)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            await using var input = File.OpenRead(Path.Combine(stage, name.Replace('/', Path.DirectorySeparatorChar)));
                            await using var entry = archive.CreateEntry(name, CompressionLevel.Fastest).Open();
                            await input.CopyToAsync(entry, cancellationToken);
                        }
                    }
                    await output.FlushAsync(cancellationToken);
                    output.Flush(flushToDisk: true);
                }
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(pending, destination, overwrite: false);
            }
            finally
            {
                File.Delete(pending);
            }
        }
        finally
        {
            Directory.Delete(stage, recursive: true);
        }
    }

    /// <summary>Validates release, schema, integrity, paths and complete contents without modifying the target instance.</summary>
    public async Task<RecoveryPreview> PreviewAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        using var ownership = ApplicationDataOwnership.Acquire(_store.RootPath);
        var stage = CreateStage();
        try
        {
            return await StageArchiveAsync(archivePath, stage, cancellationToken);
        }
        finally
        {
            Directory.Delete(stage, recursive: true);
        }
    }

    /// <summary>Installs validated data only into an absent or empty directory; never merges or overwrites existing application data.</summary>
    public async Task RestoreAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        using var ownership = ApplicationDataOwnership.Acquire(_store.RootPath);
        RequireEmpty();
        var stage = CreateStage();
        try
        {
            _ = await StageArchiveAsync(archivePath, stage, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            RequireEmpty();
            if (Directory.Exists(_store.RootPath))
            {
                Directory.Delete(_store.RootPath, recursive: false);
            }
            Directory.Move(stage, _store.RootPath);
        }
        finally
        {
            if (Directory.Exists(stage))
            {
                Directory.Delete(stage, recursive: true);
            }
        }
    }

    /// <summary>Exports allowlisted portable settings; adapter payloads, grants, listeners, certificate references and keys are omitted.</summary>
    public async Task ExportAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        using var ownership = ApplicationDataOwnership.Acquire(_store.RootPath);
        RequireCompatible(_store);
        var settings = File.Exists(_store.SettingsPath)
            ? JsonSerializer.Deserialize<LineupSettings>(await File.ReadAllTextAsync(_store.SettingsPath, cancellationToken))
                ?? throw new InvalidDataException("The settings document is empty.")
            : new LineupSettings();
        static AdapterConfiguration Redact(AdapterConfiguration adapter) => new(adapter.Kind, JsonSerializer.SerializeToElement(new { }));
        var streaming = settings.Streaming;
        var portable = new LineupSettings
        {
            TargetDays = settings.TargetDays,
            EpgHistoryRetentionHours = settings.EpgHistoryRetentionHours,
            AutoFetchInterval = settings.AutoFetchInterval,
            RefreshChannelsBeforeGuideFetch = settings.RefreshChannelsBeforeGuideFetch,
            DeviceRefreshIntervalMinutes = settings.DeviceRefreshIntervalMinutes,
            TunerRefreshIntervalSeconds = settings.TunerRefreshIntervalSeconds,
            TimeZoneId = settings.TimeZoneId,
            ProtectedContentMode = settings.ProtectedContentMode,
            DisabledChannelMode = settings.DisabledChannelMode,
            MaximumConcurrentStreams = settings.MaximumConcurrentStreams,
            Streaming = new StreamingConfiguration
            {
                PhysicalDefaults = streaming.PhysicalDefaults,
                VirtualDefaults = streaming.VirtualDefaults,
                KeepWarmPeriodSeconds = streaming.KeepWarmPeriodSeconds,
                MaximumPluginConnections = streaming.MaximumPluginConnections,
                PhysicalDevices = streaming.PhysicalDevices.Select(device => device with { Enabled = false, Adapter = Redact(device.Adapter) }).ToArray(),
                GuideSources = streaming.GuideSources.Select(source => source with { Enabled = false, Adapter = Redact(source.Adapter) }).ToArray(),
                VirtualDevices = streaming.VirtualDevices.Select(device => device with { Enabled = false, Adapter = Redact(device.Adapter), Hosting = null }).ToArray(),
                GuideOutputs = streaming.GuideOutputs.Select(output => output with { Enabled = false, Adapter = Redact(output.Adapter) }).ToArray()
            }
        };
        var destination = ExternalDestination(destinationPath);
        var pending = destination + $".{Guid.NewGuid():N}.pending";
        try
        {
            await using (var output = CreatePrivateFile(pending))
            {
                await JsonSerializer.SerializeAsync(output, new
                {
                    Format = "lineup-portable-configuration",
                    Release,
                    RequiresSetup = true,
                    Settings = portable
                }, JsonOptions, cancellationToken);
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(pending, destination, overwrite: false);
        }
        finally
        {
            File.Delete(pending);
        }
    }

    private async Task<RecoveryPreview> StageArchiveAsync(string archivePath, string stage, CancellationToken cancellationToken)
    {
        ApplicationDataOwnership.RejectLinks(archivePath);
        await using var input = File.OpenRead(archivePath);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        if (archive.Entries.Count is < 1 or > MaximumEntries || archive.Entries.Select(entry => entry.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != archive.Entries.Count)
        {
            throw new InvalidDataException("The recovery archive has duplicate entries or exceeds its entry limit.");
        }
        var manifestEntry = archive.GetEntry("manifest.json") ?? throw new InvalidDataException("The recovery manifest is missing.");
        if (manifestEntry.Length > 1024 * 1024)
        {
            throw new InvalidDataException("The recovery manifest exceeds its limit.");
        }
        Manifest manifest;
        await using (var stream = manifestEntry.Open())
        {
            manifest = await JsonSerializer.DeserializeAsync<Manifest>(stream, JsonOptions, cancellationToken)
                ?? throw new InvalidDataException("The recovery manifest is empty.");
        }
        if (manifest.Version != 1 || manifest.Release != Release || manifest.Files is null || manifest.Files.Count != archive.Entries.Count - 1)
        {
            throw new InvalidDataException("The recovery archive does not match this exact release or its declared inventory.");
        }
        long total = 0;
        foreach (var entry in archive.Entries.Where(entry => entry.FullName != "manifest.json"))
        {
            var name = entry.FullName;
            if (!Allowed(name) || !manifest.Files.TryGetValue(name, out var expected) || expected is null || entry.Length != expected.Length || entry.Length < 0 ||
                (total = checked(total + entry.Length)) > MaximumArchiveBytes || name != AppConstants.DefaultDatabaseFileName && entry.Length > 8 * 1024 * 1024)
            {
                throw new InvalidDataException("The recovery archive contains an unsupported path, size or inventory.");
            }
            var path = Path.Combine(stage, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var source = entry.Open())
            await using (var target = CreatePrivateFile(path))
            {
                var buffer = new byte[64 * 1024];
                long copied = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    if ((copied += count) > expected.Length)
                    {
                        throw new InvalidDataException("The recovery entry expanded beyond its declared size.");
                    }
                    await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                }
                if (copied != expected.Length)
                {
                    throw new InvalidDataException("The recovery entry is incomplete.");
                }
                await target.FlushAsync(cancellationToken);
                target.Flush(flushToDisk: true);
            }
            if (!string.Equals(await HashAsync(path, cancellationToken), expected.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The recovery archive failed its content-integrity check.");
            }
        }
        await ValidateStateAsync(stage, cancellationToken);
        return new(manifest.Release, manifest.CreatedAtUtc, manifest.Files.Keys.Order(StringComparer.Ordinal).ToArray());
    }

    private static bool Allowed(string name) =>
        name == AppConstants.SettingsFileName || name == "web-settings.json" || name == AppConstants.DefaultDatabaseFileName ||
        name.StartsWith(AppConstants.DataProtectionKeysDirectoryName + "/", StringComparison.Ordinal) &&
        name.EndsWith(".xml", StringComparison.Ordinal) && name.Count(character => character == '/') == 1 &&
        name.All(character => char.IsAsciiLetterOrDigit(character) || character is '/' or '-' or '_' or '.');

    private static async Task ValidateStateAsync(string root, CancellationToken cancellationToken)
    {
        var store = new AppDataStore(root);
        RequireCompatible(store);
        if (File.Exists(store.SettingsPath) && JsonSerializer.Deserialize<LineupSettings>(await File.ReadAllTextAsync(store.SettingsPath, cancellationToken)) is null)
        {
            throw new InvalidDataException("The recovery settings document is empty.");
        }
        if (File.Exists(store.DatabasePath))
        {
            using var database = OpenDatabase(store.DatabasePath, SqliteOpenMode.ReadOnly);
            using var command = database.CreateCommand();
            command.CommandText = "PRAGMA quick_check;";
            if (!string.Equals(await command.ExecuteScalarAsync(cancellationToken) as string, "ok", StringComparison.Ordinal))
            {
                throw new InvalidDataException("The recovery database failed its integrity check.");
            }
        }
        var webPath = store.GetPath("web-settings.json");
        if (File.Exists(webPath))
        {
            using var web = JsonDocument.Parse(await File.ReadAllTextAsync(webPath, cancellationToken));
            if (!web.RootElement.TryGetProperty("SchemaVersion", out var schema) || !schema.TryGetInt32(out var value) || value != 1)
            {
                throw new InvalidDataException("The recovery Web settings use an unsupported schema.");
            }
        }
    }

    private static void RequireCompatible(AppDataStore store)
    {
        if (!File.Exists(store.SettingsPath) && File.Exists(store.SettingsBackupPath) ||
            !File.Exists(store.GetPath("web-settings.json")) && File.Exists(store.GetPath("web-settings.json.bak")))
        {
            throw new InvalidDataException("Recovery requires valid primary settings. Save the settings fallback as the primary document before creating an archive.");
        }
        if (StartupSchemaGate.Check(store).IsFactoryResetRequired)
        {
            throw new InvalidDataException("The recovery data does not use this release's required settings/database schemas.");
        }
        if (File.Exists(store.SettingsPath) && StartupSchemaGate.CheckSettingsContent(File.ReadAllText(store.SettingsPath)).IsFactoryResetRequired)
        {
            throw new InvalidDataException("Recovery requires valid primary settings, not a settings fallback.");
        }
    }

    private void RequireEmpty()
    {
        ApplicationDataOwnership.RejectLinks(_store.RootPath);
        if (File.Exists(_store.RootPath) || Directory.Exists(_store.RootPath) && Directory.EnumerateFileSystemEntries(_store.RootPath).Any())
        {
            throw new IOException("Restore requires an absent or empty application-data directory. Existing data was not modified.");
        }
    }

    private string ExternalDestination(string path)
    {
        var destination = Path.GetFullPath(path);
        ApplicationDataOwnership.RejectLinks(destination);
        var relative = Path.GetRelativePath(_store.RootPath, destination);
        if (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new IOException("Recovery artifacts must be saved outside application data so factory reset cannot delete them.");
        }
        if (File.Exists(destination))
        {
            throw new IOException("The recovery destination already exists.");
        }
        return destination;
    }

    private string CreateStage()
    {
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(_store.RootPath))!;
        var stage = Path.Combine(parent, $".lineup-recovery-{Guid.NewGuid():N}");
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(stage);
        }
        else
        {
            Directory.CreateDirectory(stage, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return stage;
    }

    private static FileStream CreatePrivateFile(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        return new FileStream(path, options);
    }

    private static SqliteConnection OpenDatabase(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString());
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static async Task CopyAsync(string source, string destination, CancellationToken cancellationToken)
    {
        ApplicationDataOwnership.RejectLinks(source);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using var input = File.OpenRead(source);
        await using var output = CreatePrivateFile(destination);
        await input.CopyToAsync(output, cancellationToken);
        await output.FlushAsync(cancellationToken);
        output.Flush(flushToDisk: true);
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var input = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken));
    }

    private sealed record Entry(long Length, string Sha256);
    private sealed record Manifest(int Version, string Release, DateTimeOffset CreatedAtUtc, Dictionary<string, Entry> Files);
}
