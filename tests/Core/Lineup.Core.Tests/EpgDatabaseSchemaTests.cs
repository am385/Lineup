using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using Lineup.Core.Storage;
using Lineup.Core.Storage.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Lineup.Core.Tests;

/// <summary>Verifies atomic first-time database initialization and strict persisted-schema boundaries.</summary>
public sealed class EpgDatabaseSchemaTests
{
    private const string ProcessRoleVariable = "LINEUP_SCHEMA_TEST_ROLE";
    private const string ProcessDatabaseVariable = "LINEUP_SCHEMA_TEST_DATABASE";
    private const string ProcessStageVariable = "LINEUP_SCHEMA_TEST_STAGE";

    /// <summary>Verifies failures and cancellation cannot leave model tables or metadata committed independently.</summary>
    [Theory]
    [InlineData("ddl", false)]
    [InlineData("ddl", true)]
    [InlineData("before-metadata", false)]
    [InlineData("before-metadata", true)]
    [InlineData("after-metadata", false)]
    [InlineData("after-metadata", true)]
    public async Task EnsureAsync_InterruptedInitialization_RollsBackAndAllowsFreshContextRetry(string stage, bool cancel)
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-atomic-schema-");
        try
        {
            var path = Path.Combine(root.FullName, "guide.db");
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var interceptor = new InitializationInterceptor(stage, (_, _) =>
            {
                if (cancel)
                {
                    cancellation.Cancel();
                    cancellation.Token.ThrowIfCancellationRequested();
                }
                throw new InitializationFailureException();
            });
            await using var context = new EpgDbContext(CreateOptions(path, interceptor));

            // Act
            if (cancel)
            {
                var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EpgDatabaseSchema.EnsureAsync(context, cancellation.Token));
                Assert.Equal(cancellation.Token, failure.CancellationToken);
            }
            else
            {
                await Assert.ThrowsAsync<InitializationFailureException>(() => EpgDatabaseSchema.EnsureAsync(context, cancellation.Token));
            }

            // Assert
            Assert.Equal(1, interceptor.Interruptions);
            Assert.Empty(ReadTableNames(path));
            Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
            Assert.Equal(LineupStartupStatus.Ready, StartupSchemaGate.Check(null, null, path).Status);
            var transaction = Assert.IsAssignableFrom<DbTransaction>(interceptor.Transactions[0]);
            Assert.All(interceptor.Transactions, observed => Assert.Same(transaction, observed));

            // Act
            await using var retry = new EpgDbContext(CreateOptions(path));
            await EpgDatabaseSchema.EnsureAsync(retry, TestContext.Current.CancellationToken);

            // Assert
            AssertCurrentSchema(path, retry);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>Verifies missing files, zero-length files and empty SQLite databases initialize once and retain the current schema version.</summary>
    [Theory]
    [InlineData("absent")]
    [InlineData("zero-length")]
    [InlineData("empty")]
    public async Task EnsureAsync_UninitializedDatabase_CommitsCompleteSchemaAndCachesSuccess(string initialState)
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-atomic-schema-");
        try
        {
            var path = Path.Combine(root.FullName, "guide.db");
            if (initialState == "zero-length")
            {
                await File.WriteAllBytesAsync(path, [], TestContext.Current.CancellationToken);
            }
            else if (initialState == "empty")
            {
                using var connection = new SqliteConnection(ConnectionString(path));
                connection.Open();
            }
            var interceptor = new InitializationInterceptor("before-metadata", (_, _) => Task.CompletedTask);
            await using var context = new EpgDbContext(CreateOptions(path, interceptor));

            // Act
            await EpgDatabaseSchema.EnsureAsync(context, TestContext.Current.CancellationToken);
            await using var second = new EpgDbContext(CreateOptions(path, interceptor));
            await EpgDatabaseSchema.EnsureAsync(second, TestContext.Current.CancellationToken);

            // Assert
            AssertCurrentSchema(path, second);
            Assert.Equal(1, interceptor.Interruptions);
            var transaction = Assert.IsAssignableFrom<DbTransaction>(interceptor.Transactions[0]);
            Assert.All(interceptor.Transactions, observed => Assert.Same(transaction, observed));
            Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
            Assert.Equal(ConnectionState.Closed, second.Database.GetDbConnection().State);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>Verifies pre-canceled initialization has no database side effects.</summary>
    [Fact]
    public async Task EnsureAsync_AlreadyCanceled_DoesNotCreateDatabase()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-atomic-schema-");
        try
        {
            var path = Path.Combine(root.FullName, "guide.db");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await using var context = new EpgDbContext(CreateOptions(path));

            // Act
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EpgDatabaseSchema.EnsureAsync(context, cancellation.Token));

            // Assert
            Assert.False(File.Exists(path));
            Assert.Empty(root.EnumerateFiles());
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>Verifies failed initialization rolls back without closing a caller-owned connection, including in-memory databases.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnsureAsync_OpenConnection_RetainsOwnershipAndAllowsRetry(bool inMemory)
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-atomic-schema-");
        try
        {
            var path = inMemory ? ":memory:" : Path.Combine(root.FullName, "guide.db");
            await using var connection = new SqliteConnection(ConnectionString(path));
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            var interceptor = new InitializationInterceptor("before-metadata", (_, _) => throw new InitializationFailureException());
            var options = new DbContextOptionsBuilder<EpgDbContext>().UseSqlite(connection).AddInterceptors(interceptor).Options;
            await using var context = new EpgDbContext(options);

            // Act
            await Assert.ThrowsAsync<InitializationFailureException>(() => EpgDatabaseSchema.EnsureAsync(context, TestContext.Current.CancellationToken));

            // Assert
            Assert.Equal(ConnectionState.Open, connection.State);
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";
                Assert.Equal(0L, command.ExecuteScalar());
            }

            // Act
            await EpgDatabaseSchema.EnsureAsync(context, TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal(ConnectionState.Open, connection.State);
            Assert.Empty(await context.PhysicalSourceChannels.ToArrayAsync(TestContext.Current.CancellationToken));
            using var version = connection.CreateCommand();
            version.CommandText = """SELECT "Value" FROM LineupMetadata WHERE "Key" = 'SchemaVersion';""";
            Assert.Equal("1", version.ExecuteScalar());
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>Verifies incompatible or stranded populated databases are rejected before initialization without changing their bytes.</summary>
    [Theory]
    [InlineData("legacy")]
    [InlineData("stranded-model")]
    [InlineData("future")]
    [InlineData("previous")]
    [InlineData("invalid-version")]
    [InlineData("missing-version")]
    [InlineData("corrupt")]
    public async Task EnsureAsync_IncompatibleDatabase_RejectsWithoutMutation(string state)
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-atomic-schema-");
        try
        {
            var path = Path.Combine(root.FullName, "guide.db");
            if (state == "corrupt")
            {
                await File.WriteAllTextAsync(path, "not a SQLite database", TestContext.Current.CancellationToken);
            }
            else if (state == "stranded-model")
            {
                await using var original = new EpgDbContext(CreateOptions(path));
                await original.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            }
            else
            {
                using var connection = new SqliteConnection(ConnectionString(path));
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE LegacyState (Value TEXT NOT NULL); INSERT INTO LegacyState VALUES ('preserve');";
                command.ExecuteNonQuery();
                if (state != "legacy")
                {
                    command.CommandText = """CREATE TABLE LineupMetadata ("Key" TEXT NOT NULL PRIMARY KEY, "Value" TEXT NOT NULL);""";
                    command.ExecuteNonQuery();
                    if (state != "missing-version")
                    {
                        command.CommandText = """INSERT INTO LineupMetadata VALUES ('SchemaVersion', $version);""";
                        command.Parameters.AddWithValue("$version", state == "future" ? "2" : state == "previous" ? "0" : "invalid");
                        command.ExecuteNonQuery();
                    }
                }
            }
            var originalBytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
            await using var context = new EpgDbContext(CreateOptions(path));

            // Act
            var startup = StartupSchemaGate.Check(null, null, path);
            await Assert.ThrowsAsync<FactoryResetRequiredException>(() => EpgDatabaseSchema.EnsureAsync(context, TestContext.Current.CancellationToken));

            // Assert
            Assert.Equal(LineupStartupStatus.FactoryResetRequired, startup.Status);
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
            Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>Verifies cancellation at the commit boundary rolls back metadata and does not populate the successful-initialization cache.</summary>
    [Fact]
    public async Task EnsureAsync_CanceledBeforeCommit_RollsBackAndRetries()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-atomic-schema-");
        try
        {
            var path = Path.Combine(root.FullName, "guide.db");
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var interceptor = new InitializationInterceptor("after-metadata", (_, _) =>
            {
                cancellation.Cancel();
                return Task.CompletedTask;
            });
            await using var context = new EpgDbContext(CreateOptions(path, interceptor));

            // Act
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EpgDatabaseSchema.EnsureAsync(context, cancellation.Token));

            // Assert
            Assert.Empty(ReadTableNames(path));
            Assert.Equal(LineupStartupStatus.Ready, StartupSchemaGate.Check(null, null, path).Status);

            // Act
            await using var retry = new EpgDbContext(CreateOptions(path));
            await EpgDatabaseSchema.EnsureAsync(retry, TestContext.Current.CancellationToken);

            // Assert
            AssertCurrentSchema(path, retry);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>Verifies current-version data is preserved without re-creating model tables.</summary>
    [Fact]
    public async Task EnsureAsync_ExistingCurrentDatabase_PreservesDataAndSchema()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-atomic-schema-");
        try
        {
            var path = Path.Combine(root.FullName, "guide.db");
            var existing = new StoredPhysicalSourceChannel
            {
                PhysicalDeviceId = "existing-device",
                ProviderKey = "channel-7",
                ProviderNumber = "7.1",
                ProviderName = "Existing channel",
                NameOverride = "Preserved channel override",
                MetadataJson = """{"preserved":true}"""
            };
            await using (var original = new EpgDbContext(CreateOptions(path)))
            {
                await original.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
                await original.Database.ExecuteSqlRawAsync(StartupSchemaGate.CreateDatabaseMetadataSql(), TestContext.Current.CancellationToken);
                await original.Database.ExecuteSqlRawAsync("""INSERT INTO LineupMetadata VALUES ('Preserved', 'existing-data');""", TestContext.Current.CancellationToken);
                original.PhysicalSourceChannels.Add(existing);
                await original.SaveChangesAsync(TestContext.Current.CancellationToken);
            }
            var interceptor = new InitializationInterceptor("ddl", (_, _) => throw new InitializationFailureException());
            await using var context = new EpgDbContext(CreateOptions(path, interceptor));

            // Act
            await EpgDatabaseSchema.EnsureAsync(context, TestContext.Current.CancellationToken);

            // Assert
            AssertCurrentSchema(path, context);
            Assert.Equivalent(existing, await context.PhysicalSourceChannels.SingleAsync(TestContext.Current.CancellationToken), strict: true);
            Assert.Equal(0, interceptor.Interruptions);
            Assert.NotNull(Assert.Single(interceptor.Transactions));
            using var connection = new SqliteConnection(ConnectionString(path));
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """SELECT "Value" FROM LineupMetadata WHERE "Key" = 'Preserved';""";
            Assert.Equal("existing-data", command.ExecuteScalar());
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>Verifies other contexts cannot observe a cached success or committed partial schema while initialization is in flight.</summary>
    [Fact]
    public async Task EnsureAsync_ConcurrentContexts_WaitForCommittedInitialization()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-atomic-schema-");
        try
        {
            var path = Path.Combine(root.FullName, "guide.db");
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var interceptor = new InitializationInterceptor("before-metadata", (_, token) =>
            {
                entered.SetResult();
                return release.Task.WaitAsync(token);
            });
            await using var context = new EpgDbContext(CreateOptions(path, interceptor));
            var owner = EpgDatabaseSchema.EnsureAsync(context, TestContext.Current.CancellationToken);
            var waiters = new List<Task>();
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

                // Act
                waiters.AddRange(Enumerable.Range(0, 8).Select(async _ =>
                {
                    await using var waiter = new EpgDbContext(CreateOptions(path, interceptor));
                    await EpgDatabaseSchema.EnsureAsync(waiter, TestContext.Current.CancellationToken);
                }));

                // Assert
                Assert.All(waiters, waiter => Assert.False(waiter.IsCompleted));
                Assert.Empty(ReadTableNames(path));
                Assert.Equal(LineupStartupStatus.Ready, StartupSchemaGate.Check(null, null, path).Status);
            }
            finally
            {
                release.TrySetResult();
                await Task.WhenAll(waiters.Append(owner));
            }

            // Assert
            AssertCurrentSchema(path, context);
            Assert.Equal(1, interceptor.Interruptions);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>Verifies abrupt termination before or after metadata writing leaves a database that a completely new process can initialize without reset.</summary>
    [Fact]
    public async Task EnsureAsync_AbruptProcessInterruption_AllowsFreshProcessRestart()
    {
        // Arrange
        var role = Environment.GetEnvironmentVariable(ProcessRoleVariable);
        if (role is not null)
        {
            await RunProcessRoleAsync(role);
            return;
        }
        var root = Directory.CreateTempSubdirectory("lineup-schema-process-");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            foreach (var stage in new[] { "before-metadata", "after-metadata" })
            {
                var path = Path.Combine(root.FullName, $"{stage}.db");

                // Act
                await RunSchemaProcessAsync(path, stage, interrupt: true, deadline.Token);

                // Assert
                Assert.Empty(await ReadTableNamesAfterProcessExitAsync(path, deadline.Token));
                Assert.Equal(LineupStartupStatus.Ready, StartupSchemaGate.Check(null, null, path).Status);

                // Act
                await RunSchemaProcessAsync(path, stage, interrupt: false, deadline.Token);

                // Assert
                Assert.Equal("1", await File.ReadAllTextAsync($"{path}.complete", deadline.Token));
                await using var verify = new EpgDbContext(CreateOptions(path));
                AssertCurrentSchema(path, verify);
            }
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>Verifies crash inspection can recover after Windows releases a lingering WAL-index mapping without deleting database files.</summary>
    [Fact]
    public async Task ReadTableNamesAfterProcessExitAsync_LingeringWalMapping_AllowsRecoveryWhenReleased()
    {
        // Arrange
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Requires Windows WAL-index memory-mapping semantics.");
        var root = Directory.CreateTempSubdirectory("lineup-schema-mapping-");
        try
        {
            var path = Path.Combine(root.FullName, "guide.db");
            using (var connection = new SqliteConnection(ConnectionString(path)))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA journal_mode=WAL;";
                Assert.Equal("wal", command.ExecuteScalar());
            }
            Task<string[]> inspection;
            using (var file = new FileStream($"{path}-shm", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
            {
                file.SetLength(65_536);
                using var mapping = MemoryMappedFile.CreateFromFile(file, null, file.Length, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: true);
                using var view = mapping.CreateViewAccessor();

                // Act
                inspection = ReadTableNamesAfterProcessExitAsync(path, TestContext.Current.CancellationToken);
            }
            var tables = await inspection.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await using var context = new EpgDbContext(CreateOptions(path));
            await EpgDatabaseSchema.EnsureAsync(context, TestContext.Current.CancellationToken);

            // Assert
            Assert.Empty(tables);
            AssertCurrentSchema(path, context);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static async Task<string[]> ReadTableNamesAfterProcessExitAsync(string path, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return ReadTableNames(path);
            }
            catch (SqliteException exception) when (OperatingSystem.IsWindows() && exception.SqliteExtendedErrorCode == 1546 && elapsed.Elapsed < TimeSpan.FromSeconds(5))
            {
                // Windows can release a terminated writer's WAL locks before unmapping its shared-memory view (SQLITE_IOERR_TRUNCATE).
                // https://sqlite.org/src/info/7c103a0b7d
                TestContext.Current.TestOutputHelper?.WriteLine($"Waiting for Windows WAL-index mapping cleanup (SQLite extended error {exception.SqliteExtendedErrorCode}).");
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
            }
        }
    }

    private static async Task RunProcessRoleAsync(string role)
    {
        var path = Environment.GetEnvironmentVariable(ProcessDatabaseVariable) ?? throw new InvalidOperationException("The child database path is required.");
        var stage = Environment.GetEnvironmentVariable(ProcessStageVariable) ?? throw new InvalidOperationException("The child interruption stage is required.");
        Assert.Equal(LineupStartupStatus.Ready, StartupSchemaGate.Check(null, null, path).Status);
        if (role == "interrupt")
        {
            var interceptor = new InitializationInterceptor(stage, async (_, token) =>
            {
                await File.WriteAllTextAsync($"{path}.ready", stage, token);
                await Task.Delay(Timeout.Infinite, token);
            });
            await using var context = new EpgDbContext(CreateOptions(path, interceptor));
            await EpgDatabaseSchema.EnsureAsync(context, TestContext.Current.CancellationToken);
            Assert.Fail("The interrupted initializer must remain paused until its parent terminates it.");
        }
        else
        {
            Assert.Equal("restart", role);
            await using var context = new EpgDbContext(CreateOptions(path));
            await EpgDatabaseSchema.EnsureAsync(context, TestContext.Current.CancellationToken);
            AssertCurrentSchema(path, context);
            await File.WriteAllTextAsync($"{path}.complete", "1", TestContext.Current.CancellationToken);
        }
    }

    private static async Task RunSchemaProcessAsync(string path, string stage, bool interrupt, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(path)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in new[] { "exec", typeof(EpgDatabaseSchemaTests).Assembly.Location, "-method", $"*{nameof(EnsureAsync_AbruptProcessInterruption_AllowsFreshProcessRestart)}" })
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment[ProcessRoleVariable] = interrupt ? "interrupt" : "restart";
        start.Environment[ProcessDatabaseVariable] = path;
        start.Environment[ProcessStageVariable] = stage;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start the owned schema test process.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            if (interrupt)
            {
                while (!File.Exists($"{path}.ready"))
                {
                    if (process.HasExited)
                    {
                        Assert.Fail($"Schema probe exited before reaching {stage}.{Environment.NewLine}{await output}{Environment.NewLine}{await error}");
                    }
                    await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
                }
                Assert.Empty(ReadTableNames(path));
                Assert.Equal(LineupStartupStatus.Ready, StartupSchemaGate.Check(null, null, path).Status);
                process.Kill(entireProcessTree: true);
            }
            await process.WaitForExitAsync(cancellationToken);
            var stdout = await output;
            var stderr = await error;
            if (interrupt)
            {
                Assert.NotEqual(0, process.ExitCode);
            }
            else
            {
                Assert.True(process.ExitCode == 0, $"Fresh-process restart failed.{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    private static DbContextOptions<EpgDbContext> CreateOptions(string path, IInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<EpgDbContext>().UseSqlite(ConnectionString(path));
        if (interceptor is not null)
        {
            builder.AddInterceptors(interceptor);
        }
        return builder.Options;
    }

    private static string ConnectionString(string path) => new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();

    private static string[] ReadTableNames(string path)
    {
        using var connection = new SqliteConnection(ConnectionString(path));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }
        return names.ToArray();
    }

    private static void AssertCurrentSchema(string path, EpgDbContext context)
    {
        var expected = context.Model.GetEntityTypes().Select(entity => entity.GetTableName()).Append("LineupMetadata").Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, ReadTableNames(path));
        Assert.Equal(LineupStartupStatus.Ready, StartupSchemaGate.Check(null, null, path).Status);
        using var connection = new SqliteConnection(ConnectionString(path));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """SELECT "Value" FROM LineupMetadata WHERE "Key" = 'SchemaVersion';""";
        Assert.Equal("1", command.ExecuteScalar());
    }

    private sealed class InitializationFailureException : Exception;

    private sealed class InitializationInterceptor(string stage, Func<DbCommand, CancellationToken, Task> interrupt) : DbCommandInterceptor
    {
        /// <summary>Gets the number of reached interruption points.</summary>
        public int Interruptions { get; private set; }

        /// <summary>Gets the transaction observed for each model or metadata creation command.</summary>
        public List<DbTransaction?> Transactions { get; } = [];

        /// <inheritdoc />
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("CREATE TABLE", StringComparison.Ordinal))
            {
                Transactions.Add(command.Transaction);
            }
            if (stage == "before-metadata" && IsMetadata(command))
            {
                await InterruptAsync(command, cancellationToken);
            }
            return result;
        }

        /// <inheritdoc />
        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if ((stage == "after-metadata" && IsMetadata(command)) ||
                (stage == "ddl" && command.CommandText.Contains("CREATE TABLE", StringComparison.Ordinal) && !IsMetadata(command)))
            {
                await InterruptAsync(command, cancellationToken);
            }
            return result;
        }

        private static bool IsMetadata(DbCommand command) => command.CommandText.Contains("\"LineupMetadata\"", StringComparison.Ordinal);

        private async Task InterruptAsync(DbCommand command, CancellationToken cancellationToken)
        {
            if (Interruptions++ == 0)
            {
                await interrupt(command, cancellationToken);
            }
        }
    }
}
