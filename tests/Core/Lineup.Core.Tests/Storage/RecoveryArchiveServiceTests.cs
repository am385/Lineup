using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lineup.Core.Settings;
using Lineup.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Lineup.Core.Tests.Storage;

/// <summary>Verifies offline backup boundaries, integrity and non-destructive recovery.</summary>
public sealed class RecoveryArchiveServiceTests
{
    /// <summary>Exercises the shared offline command dispatcher through backup, preflight and restore into a fresh target.</summary>
    [Fact]
    public async Task RecoveryCommand_PreflightAndRestore_CompletesFreshTargetWorkflow()
    {
        // Arrange
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        var archive = Path.Combine(fixture.Root, "offline-command.zip");
        var target = Path.Combine(fixture.Root, "restored");

        // Act
        var backup = await RecoveryCommand.TryRunAsync(["--recovery", "backup", archive], fixture.Store.RootPath, cancellationToken: TestContext.Current.CancellationToken);
        var preflight = await RecoveryCommand.TryRunAsync(["--recovery", "preflight", archive], target, cancellationToken: TestContext.Current.CancellationToken);
        var existedAfterPreflight = Directory.Exists(target);
        var restore = await RecoveryCommand.TryRunAsync(["--recovery", "restore", archive], target, cancellationToken: TestContext.Current.CancellationToken);
        var occupied = await RecoveryCommand.TryRunAsync(["--recovery", "preflight", archive], target, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, backup);
        Assert.Equal(0, preflight);
        Assert.False(existedAfterPreflight);
        Assert.Equal(0, restore);
        Assert.Equal(2, occupied);
        Assert.Equal(await File.ReadAllTextAsync(fixture.Store.SettingsPath, TestContext.Current.CancellationToken),
            await File.ReadAllTextAsync(Path.Combine(target, AppConstants.SettingsFileName), TestContext.Current.CancellationToken));
        Assert.True(File.Exists(Path.Combine(target, AppConstants.DefaultDatabaseFileName)));
        Assert.True(File.Exists(Path.Combine(target, "data-protection-keys", "key-test.xml")));
    }

    /// <summary>Cancellation cannot create a target or return a successful preflight.</summary>
    [Fact]
    public async Task Preflight_Canceled_DoesNotInstallOrReportSuccess()
    {
        // Arrange
        using var fixture = new Fixture();
        var target = Path.Combine(fixture.Root, "restored");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        // Act
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RecoveryArchiveService(target).PreflightAsync("unused.zip", canceled.Token));

        // Assert
        Assert.False(Directory.Exists(target));
        Assert.Empty(Directory.GetDirectories(fixture.Root, ".lineup-recovery-*"));
    }

    /// <summary>Checks a fresh destination without installing sensitive files and keeps external deployment requirements explicitly unverified.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Preflight_ValidArchive_LeavesDestinationUnchanged(bool existingDirectory)
    {
        // Arrange
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        var archive = Path.Combine(fixture.Root, "backup.zip");
        await new RecoveryArchiveService(fixture.Store.RootPath).BackupAsync(archive, TestContext.Current.CancellationToken);
        var target = Path.Combine(fixture.Root, "restored");
        if (existingDirectory)
        {
            Directory.CreateDirectory(target);
        }
        var recovery = new RecoveryArchiveService(target);

        // Act
        var report = await recovery.PreflightAsync(archive, TestContext.Current.CancellationToken);
        var serialized = JsonSerializer.Serialize(report);
        var existsAfterPreflight = Directory.Exists(target);
        var filesAfterPreflight = existsAfterPreflight ? Directory.GetFileSystemEntries(target) : [];
        await recovery.RestoreAsync(archive, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(existingDirectory, existsAfterPreflight);
        Assert.Empty(filesAfterPreflight);
        Assert.DoesNotContain("credential-sentinel", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("private-key-sentinel", serialized, StringComparison.Ordinal);
        Assert.Contains(report.ExternalRequirements, requirement => requirement.Contains("have not been verified", StringComparison.Ordinal));
        Assert.Contains(report.ExternalRequirements, requirement => requirement.Contains("Data Protection", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(target, AppConstants.SettingsFileName)));
        Assert.Empty(Directory.GetDirectories(fixture.Root, ".lineup-recovery-*"));
    }

    /// <summary>Refuses an occupied destination and repeats the empty-target check even after a previously successful preflight.</summary>
    [Fact]
    public async Task Preflight_TargetBecomesOccupied_RestoreAndLaterPreflightPreserveIt()
    {
        // Arrange
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        var archive = Path.Combine(fixture.Root, "backup.zip");
        await new RecoveryArchiveService(fixture.Store.RootPath).BackupAsync(archive, TestContext.Current.CancellationToken);
        var target = Path.Combine(fixture.Root, "restored");
        var recovery = new RecoveryArchiveService(target);
        await recovery.PreflightAsync(archive, TestContext.Current.CancellationToken);
        Directory.CreateDirectory(target);
        var sentinel = Path.Combine(target, "operator-owned.txt");
        await File.WriteAllTextAsync(sentinel, "keep", TestContext.Current.CancellationToken);

        // Act
        await Assert.ThrowsAsync<IOException>(() => recovery.RestoreAsync(archive, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<IOException>(() => recovery.PreflightAsync(archive, TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal("keep", await File.ReadAllTextAsync(sentinel, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(target));
        Assert.Empty(Directory.GetDirectories(fixture.Root, ".lineup-recovery-*"));
    }

    /// <summary>Round-trips settings, keys and a SQLite snapshot while excluding logs and transient artifacts.</summary>
    [Fact]
    public async Task BackupRestore_RoundTripsStateAndKeys_WithoutResetOwnedArtifacts()
    {
        // Arrange
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        var archive = Path.Combine(fixture.Root, "backup.zip");
        var destination = Path.Combine(fixture.Root, "restored");
        var recovery = new RecoveryArchiveService(destination);

        // Act
        await new RecoveryArchiveService(fixture.Store.RootPath).BackupAsync(archive, TestContext.Current.CancellationToken);
        var preview = await recovery.PreviewAsync(archive, TestContext.Current.CancellationToken);
        await recovery.RestoreAsync(archive, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(preview.ContainsSensitiveData);
        Assert.Contains("data-protection-keys/key-test.xml", preview.Files);
        Assert.DoesNotContain(preview.Files, name => name.Contains("logs", StringComparison.Ordinal) || name.Contains("transient", StringComparison.Ordinal));
        Assert.Equal(await File.ReadAllTextAsync(fixture.Store.SettingsPath, TestContext.Current.CancellationToken),
            await File.ReadAllTextAsync(Path.Combine(destination, AppConstants.SettingsFileName), TestContext.Current.CancellationToken));
        Assert.Equal("<key>private-key-sentinel</key>",
            await File.ReadAllTextAsync(Path.Combine(destination, "data-protection-keys", "key-test.xml"), TestContext.Current.CancellationToken));
        using var database = new SqliteConnection($"Data Source={Path.Combine(destination, AppConstants.DefaultDatabaseFileName)};Pooling=False");
        await database.OpenAsync(TestContext.Current.CancellationToken);
        using var command = database.CreateCommand();
        command.CommandText = "SELECT Value FROM RecoveryTest;";
        Assert.Equal("channel-binding-guide-sentinel", await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>Rejects replacement of existing state without altering it.</summary>
    [Fact]
    public async Task Restore_NonemptyTarget_PreservesExistingData()
    {
        // Arrange
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        var archive = Path.Combine(fixture.Root, "backup.zip");
        await new RecoveryArchiveService(fixture.Store.RootPath).BackupAsync(archive, TestContext.Current.CancellationToken);
        var before = await File.ReadAllBytesAsync(fixture.Store.SettingsPath, TestContext.Current.CancellationToken);

        // Act
        var exception = await Assert.ThrowsAsync<IOException>(() =>
            new RecoveryArchiveService(fixture.Store.RootPath).RestoreAsync(archive, TestContext.Current.CancellationToken));

        // Assert
        Assert.Contains("empty", exception.Message, StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.Store.SettingsPath, TestContext.Current.CancellationToken));
    }

    /// <summary>Rejects tampering and unsupported releases before installing any target files.</summary>
    [Theory]
    [InlineData("content")]
    [InlineData("release")]
    [InlineData("traversal")]
    [InlineData("schema")]
    [InlineData("duplicate")]
    public async Task Restore_InvalidArchive_NeverInstallsPartialState(string corruption)
    {
        // Arrange
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        var archive = Path.Combine(fixture.Root, "backup.zip");
        await new RecoveryArchiveService(fixture.Store.RootPath).BackupAsync(archive, TestContext.Current.CancellationToken);
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update))
        {
            if (corruption is "release" or "schema")
            {
                var entry = zip.GetEntry("manifest.json")!;
                JsonObject manifest;
                using (var reader = new StreamReader(entry.Open()))
                {
                    manifest = JsonNode.Parse(await reader.ReadToEndAsync(TestContext.Current.CancellationToken))!.AsObject();
                }
                entry.Delete();
                if (corruption == "release")
                {
                    manifest["Release"] = "unsupported-release";
                }
                else
                {
                    var settings = zip.GetEntry(AppConstants.SettingsFileName)!;
                    settings.Delete();
                    var content = """{"SchemaVersion":999,"Streaming":{"SchemaVersion":1}}""";
                    using (var writer = new StreamWriter(zip.CreateEntry(AppConstants.SettingsFileName).Open()))
                    {
                        await writer.WriteAsync(content);
                    }
                    manifest["Files"]![AppConstants.SettingsFileName]!["Length"] = System.Text.Encoding.UTF8.GetByteCount(content);
                    manifest["Files"]![AppConstants.SettingsFileName]!["Sha256"] =
                        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)));
                }
                using var output = new StreamWriter(zip.CreateEntry("manifest.json").Open());
                await output.WriteAsync(manifest.ToJsonString());
            }
            else
            {
                if (corruption == "content")
                {
                    zip.GetEntry(AppConstants.SettingsFileName)!.Delete();
                }
                var name = corruption == "traversal" ? "../escape.json" : AppConstants.SettingsFileName;
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                await writer.WriteAsync("corrupt");
            }
        }
        var target = Path.Combine(fixture.Root, "restored");

        // Act
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new RecoveryArchiveService(target).PreflightAsync(archive, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new RecoveryArchiveService(target).RestoreAsync(archive, TestContext.Current.CancellationToken));

        // Assert
        Assert.False(Directory.Exists(target));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "escape.json")));
        Assert.Empty(Directory.GetDirectories(fixture.Root, ".lineup-recovery-*"));
    }

    /// <summary>Never leaves a complete-looking output after cancellation or places it inside reset-owned storage.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Backup_CanceledOrUnsafeDestination_DoesNotPublish(bool canceled)
    {
        // Arrange
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        var path = Path.Combine(canceled ? fixture.Root : fixture.Store.RootPath, "backup.zip");
        using var cancellation = new CancellationTokenSource();
        if (canceled)
        {
            cancellation.Cancel();
        }

        // Act
        var error = await Record.ExceptionAsync(() => new RecoveryArchiveService(fixture.Store.RootPath).BackupAsync(path, cancellation.Token));

        // Assert
        Assert.NotNull(error);
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.pending", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetDirectories(fixture.Root, ".lineup-recovery-*"));
    }

    /// <summary>Excludes opaque adapter secrets, grants, keys and deployment bindings from portable output.</summary>
    [Fact]
    public async Task Export_StripsOpaqueSecretsAndDisablesAdapters()
    {
        // Arrange
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        var output = Path.Combine(fixture.Root, "portable.json");

        // Act
        await new RecoveryArchiveService(fixture.Store.RootPath).ExportAsync(output, TestContext.Current.CancellationToken);
        var text = await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(text);
        var device = document.RootElement.GetProperty("Settings").GetProperty("Streaming").GetProperty("PhysicalDevices")[0];

        // Assert
        Assert.DoesNotContain("credential-sentinel", text, StringComparison.Ordinal);
        Assert.DoesNotContain("private-key-sentinel", text, StringComparison.Ordinal);
        Assert.DoesNotContain("trusted-proxy-sentinel", text, StringComparison.Ordinal);
        Assert.False(device.GetProperty("Enabled").GetBoolean());
        Assert.Empty(device.GetProperty("Adapter").GetProperty("Configuration").EnumerateObject());
        Assert.True(document.RootElement.GetProperty("RequiresSetup").GetBoolean());
        Assert.Empty(document.RootElement.GetProperty("Settings").GetProperty("KnownProxies").EnumerateArray());
    }

    /// <summary>Uses the SQLite backup API so committed WAL contents are included, rather than copying the main database file.</summary>
    [Fact]
    public async Task Backup_CommittedWal_RecoversNewestCommittedRows()
    {
        // Arrange
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        using var database = new SqliteConnection($"Data Source={fixture.Store.DatabasePath};Pooling=False");
        await database.OpenAsync(TestContext.Current.CancellationToken);
        using var command = database.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; INSERT INTO RecoveryTest VALUES ('committed-wal-row');";
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        var archive = Path.Combine(fixture.Root, "wal.zip");
        var target = Path.Combine(fixture.Root, "restored");

        // Act
        await new RecoveryArchiveService(fixture.Store.RootPath).BackupAsync(archive, TestContext.Current.CancellationToken);
        await new RecoveryArchiveService(target).RestoreAsync(archive, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(File.Exists(fixture.Store.DatabasePath + "-wal"));
        using var recovered = new SqliteConnection($"Data Source={Path.Combine(target, AppConstants.DefaultDatabaseFileName)};Pooling=False");
        await recovered.OpenAsync(TestContext.Current.CancellationToken);
        using var count = recovered.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM RecoveryTest;";
        Assert.Equal(2L, await count.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>Rejects recovery while a host owns the data root, then permits it once ownership is released.</summary>
    [Fact]
    public async Task Backup_ActiveOwnership_RejectsConcurrentRecovery()
    {
        // Arrange
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        var output = Path.Combine(fixture.Root, "backup.zip");
        using var host = ApplicationDataOwnership.Acquire(fixture.Store.RootPath);

        // Act
        var error = await Assert.ThrowsAsync<IOException>(() => new RecoveryArchiveService(fixture.Store.RootPath).BackupAsync(output, TestContext.Current.CancellationToken));

        // Assert
        Assert.Contains("Stop all Lineup hosts", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
    }

    /// <summary>Rejects fallback-only settings instead of silently archiving defaults or omitting saved state.</summary>
    [Theory]
    [InlineData(AppConstants.SettingsFileName)]
    [InlineData("web-settings.json")]
    public async Task Backup_FallbackOnlySettings_RejectsIncompleteState(string name)
    {
        // Arrange
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        File.Move(fixture.Store.GetPath(name), fixture.Store.GetPath(name + ".bak"));
        var output = Path.Combine(fixture.Root, "backup.zip");

        // Act
        await Assert.ThrowsAsync<InvalidDataException>(() => new RecoveryArchiveService(fixture.Store.RootPath).BackupAsync(output, TestContext.Current.CancellationToken));

        // Assert
        Assert.False(File.Exists(output));
        Assert.True(File.Exists(fixture.Store.GetPath(name + ".bak")));
        Assert.Empty(Directory.GetDirectories(fixture.Root, ".lineup-recovery-*"));
    }

    /// <summary>Rejects invalid typed settings even when their schema metadata matches.</summary>
    [Fact]
    public async Task Backup_InvalidTypedSettings_RejectsBeforePublishing()
    {
        // Arrange
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        var settings = JsonNode.Parse(await File.ReadAllTextAsync(fixture.Store.SettingsPath, TestContext.Current.CancellationToken))!.AsObject();
        settings["MaximumConcurrentStreams"] = new JsonObject();
        await File.WriteAllTextAsync(fixture.Store.SettingsPath, settings.ToJsonString(), TestContext.Current.CancellationToken);
        var output = Path.Combine(fixture.Root, "backup.zip");

        // Act
        await Assert.ThrowsAsync<JsonException>(() => new RecoveryArchiveService(fixture.Store.RootPath).BackupAsync(output, TestContext.Current.CancellationToken));

        // Assert
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.GetDirectories(fixture.Root, ".lineup-recovery-*"));
    }

    /// <summary>Does not publish archives whose key paths or file sizes would be rejected during restore.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Backup_UnsupportedKeyInventory_RejectsBeforePublishing(bool oversized)
    {
        // Arrange
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        var key = Path.Combine(fixture.Store.DataProtectionKeysPath, oversized ? "key-large.xml" : "key with spaces.xml");
        await File.WriteAllBytesAsync(key, new byte[oversized ? 8 * 1024 * 1024 + 1 : 1], TestContext.Current.CancellationToken);
        var output = Path.Combine(fixture.Root, "backup.zip");

        // Act
        await Assert.ThrowsAsync<InvalidDataException>(() => new RecoveryArchiveService(fixture.Store.RootPath).BackupAsync(output, TestContext.Current.CancellationToken));

        // Assert
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.GetDirectories(fixture.Root, ".lineup-recovery-*"));
    }

    private sealed class Fixture : IDisposable
    {
        /// <summary>Gets the test-owned temporary parent.</summary>
        public string Root { get; } = Directory.CreateTempSubdirectory("lineup-recovery-test-").FullName;

        /// <summary>Gets the isolated source store.</summary>
        public AppDataStore Store => new(Path.Combine(Root, "source"));

        /// <summary>Creates synthetic state including explicit secret sentinels.</summary>
        public async Task SeedAsync()
        {
            Directory.CreateDirectory(Store.RootPath);
            var settings = new LineupSettings
            {
                KnownProxies = ["127.0.0.1"],
                Streaming = new StreamingConfiguration
                {
                    PhysicalDevices =
                    [
                        new()
                        {
                            Id = PhysicalDeviceId.New(),
                            Name = "Test device",
                            Adapter = new("test", JsonSerializer.SerializeToElement(new { Password = "credential-sentinel" }))
                        }
                    ]
                }
            };
            await File.WriteAllTextAsync(Store.SettingsPath, JsonSerializer.Serialize(settings));
            await File.WriteAllTextAsync(Store.GetPath("web-settings.json"), """{"SchemaVersion":1,"Theme":"dark"}""");
            Directory.CreateDirectory(Store.DataProtectionKeysPath);
            await File.WriteAllTextAsync(Path.Combine(Store.DataProtectionKeysPath, "key-test.xml"), "<key>private-key-sentinel</key>");
            Directory.CreateDirectory(Store.LogDirectoryPath);
            await File.WriteAllTextAsync(Path.Combine(Store.LogDirectoryPath, "test.log"), "log");
            Directory.CreateDirectory(Store.GetPath("transient"));
            await File.WriteAllTextAsync(Store.GetPath(Path.Combine("transient", "fragment.m4s")), "media");
            using var database = new SqliteConnection($"Data Source={Store.DatabasePath};Pooling=False");
            await database.OpenAsync();
            using var command = database.CreateCommand();
            command.CommandText = StartupSchemaGate.CreateDatabaseMetadataSql() +
                "CREATE TABLE RecoveryTest(Value TEXT); INSERT INTO RecoveryTest VALUES ('channel-binding-guide-sentinel');";
            await command.ExecuteNonQueryAsync();
        }

        /// <inheritdoc />
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
