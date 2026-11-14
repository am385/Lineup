using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using Lineup.Core.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Lineup.Core.Tests.Guides;

/// <summary>Isolates managed-allocation measurements from other test collections.</summary>
[CollectionDefinition("Guide read measurements", DisableParallelization = true)]
public sealed class GuideReadMeasurementCollection;

/// <summary>Verifies coherent generations, scoped SQL and aggregate equivalence at realistic guide sizes.</summary>
[Collection("Guide read measurements")]
public sealed class GuideReadScalingTests(ITestOutputHelper output)
{
    /// <summary>Verifies equivalent candidates and repeated authoritative guides retain projection counting semantics.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Statistics_MatchesProjection_WithAmbiguityAndUnavailableManualBindings(bool ambiguous, bool unavailableManual)
    {
        // Arrange
        await using var fixture = new Fixture();
        await fixture.SeedAsync(3);
        var source = new SourceChannelId(fixture.DeviceId, "0");
        var guide = new GuideChannelId(fixture.SourceId, "0");
        await fixture.Guides.SetBindingAsync(source, guide, fixture.Token);
        if (unavailableManual)
        {
            await using var context = fixture.CreateDbContext();
            await context.GuideSourceChannels.Where(channel => channel.ProviderKey == "0").ExecuteUpdateAsync(
                update => update.SetProperty(channel => channel.IsActive, false), fixture.Token);
        }
        if (ambiguous)
        {
            await using var context = fixture.CreateDbContext();
            await context.GuideSourceChannels.Where(channel => channel.ProviderKey != "0").ExecuteUpdateAsync(
                update => update.SetProperty(channel => channel.Number, "1").SetProperty(channel => channel.ProviderKey, channel => "guide-" + channel.ProviderKey), fixture.Token);
        }
        IReadOnlyList<ResolvedVirtualChannel> Resolve(IReadOnlyList<PhysicalChannelIdentity> identities) =>
        [
            new("0", "Primary", [source, new SourceChannelId(fixture.DeviceId, "1")]),
            new("100", "Alias", [source]),
            new("1", "Ambiguous", [new SourceChannelId(fixture.DeviceId, "1")]),
            new("empty", "No guide", [new SourceChannelId(fixture.DeviceId, "missing")])
        ];

        // Act
        var snapshot = await fixture.Guides.BuildResolvedSnapshotAsync(fixture.VirtualId, Resolve, cancellationToken: fixture.Token);
        var statistics = await fixture.Guides.GetStatisticsAsync(Resolve, cancellationToken: fixture.Token);

        // Assert
        AssertEquivalent(snapshot, statistics);
        Assert.Equal(snapshot.Generation?.ImportRevision, statistics.Generation?.ImportRevision);
        Assert.Equal(4, statistics.ChannelCount);
        if (ambiguous && unavailableManual)
        {
            Assert.Equal(0, statistics.ProgramCount);
        }
    }

    /// <summary>Verifies narrow projections exclude other guide programs and statistics request only SQL aggregates.</summary>
    [Fact]
    public async Task Reads_RequestOnlyContributingPrograms_AndStatisticsExcludeRichColumns()
    {
        // Arrange
        await using var fixture = new Fixture();
        await fixture.SeedAsync(2);
        await using var context = fixture.CreateDbContext();
        var selectedId = await context.GuideSourceChannels.Where(channel => channel.ProviderKey == "0").Select(channel => channel.Id).SingleAsync(fixture.Token);
        var excludedId = await context.GuideSourceChannels.Where(channel => channel.ProviderKey == "1").Select(channel => channel.Id).SingleAsync(fixture.Token);
        IReadOnlyList<ResolvedVirtualChannel> Resolve(IReadOnlyList<PhysicalChannelIdentity> _) => [new("0", "Selected", [new SourceChannelId(fixture.DeviceId, "0")])];
        fixture.Commands.Reads.Clear();

        // Act
        var snapshot = await fixture.Guides.BuildResolvedSnapshotAsync(
            fixture.VirtualId, Resolve, startUtc: fixture.StartUtc, endUtc: fixture.StartUtc.AddHours(1), cancellationToken: fixture.Token);
        var statistics = await fixture.Guides.GetStatisticsAsync(Resolve, cancellationToken: fixture.Token);
        var directory = await fixture.Guides.BuildResolvedSnapshotAsync(fixture.VirtualId, Resolve, includePrograms: false, cancellationToken: fixture.Token);

        // Assert
        Assert.Equal(2, Assert.Single(snapshot.Channels).Programs.Count);
        Assert.Equal(96, statistics.ProgramCount);
        Assert.Empty(Assert.Single(directory.Channels).Programs);
        Assert.Equal(Assert.Single(snapshot.Channels).Name, Assert.Single(directory.Channels).Name);
        var reads = fixture.Commands.Reads.Where(read => read.Sql.Contains("FROM \"GuideSourcePrograms\"", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, reads.Length);
        Assert.All(reads, read =>
        {
            Assert.Contains(selectedId.ToString(), read.Parameters);
            Assert.DoesNotContain(excludedId.ToString(), read.Parameters);
        });
        var aggregate = Assert.Single(reads, read => read.Sql.Contains("GROUP BY", StringComparison.Ordinal));
        Assert.Contains("COUNT", aggregate.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MIN", aggregate.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MAX", aggregate.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MetadataJson", aggregate.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("CategoriesJson", aggregate.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("Synopsis", aggregate.Sql, StringComparison.Ordinal);
    }

    /// <summary>Verifies an intervening import and topology reconciliation cannot mix old channel identity with new programs or import metadata.</summary>
    [Fact]
    public async Task Snapshot_ConcurrentImportAndReconciliation_RetainsOneDatabaseGeneration()
    {
        // Arrange
        await using var fixture = new Fixture();
        await fixture.SeedAsync(1);
        var before = await fixture.SnapshotAsync();
        fixture.Commands.PauseNextTopologyRead = true;

        // Act
        var pending = fixture.SnapshotAsync();
        await fixture.Commands.TopologyRead.Task.WaitAsync(fixture.Token);
        VirtualGuideSnapshot during;
        try
        {
            await fixture.Topology.SetIdentityAsync(new SourceChannelId(fixture.DeviceId, "0"), "200", "Replacement", null, fixture.Token);
            await fixture.Guides.ImportAsync(fixture.Import("Replacement program", "<new />"), fixture.Token);
        }
        finally
        {
            fixture.Commands.ReleaseRead.TrySetResult();
        }
        during = await pending;
        var after = await fixture.SnapshotAsync();

        // Assert
        Assert.Equal("0", Assert.Single(during.Channels).Number);
        Assert.Equal("Program", Assert.Single(during.Channels).Programs[0].Title);
        Assert.Equal(before.Generation?.ImportRevision, during.Generation?.ImportRevision);
        Assert.Equal(before.SupplementalXml, during.SupplementalXml);
        Assert.Equal("200", Assert.Single(after.Channels).Number);
        Assert.Equal("Replacement program", Assert.Single(Assert.Single(after.Channels).Programs).Title);
        Assert.NotEqual(during.Generation?.ImportRevision, after.Generation?.ImportRevision);
        Assert.Equal(["<new />"], after.SupplementalXml);
    }

    /// <summary>Verifies aggregate values and contributing import identity share the same read transaction.</summary>
    [Fact]
    public async Task Statistics_ConcurrentImport_RetainsProgramAndImportGeneration()
    {
        // Arrange
        await using var fixture = new Fixture();
        await fixture.SeedAsync(1);
        var before = await fixture.StatisticsAsync();
        fixture.Commands.PauseNextTopologyRead = true;

        // Act
        var pending = fixture.StatisticsAsync();
        await fixture.Commands.TopologyRead.Task.WaitAsync(fixture.Token);
        try
        {
            await fixture.Guides.ImportAsync(fixture.Import("Replacement", "<new />"), fixture.Token);
        }
        finally
        {
            fixture.Commands.ReleaseRead.TrySetResult();
        }
        var during = await pending;
        var after = await fixture.StatisticsAsync();

        // Assert
        Assert.Equal(96, during.ProgramCount);
        Assert.Equal(before.LatestProgramEndUtc, during.LatestProgramEndUtc);
        Assert.Equal(before.Generation?.ImportRevision, during.Generation?.ImportRevision);
        Assert.Equal(1, after.ProgramCount);
        Assert.NotEqual(during.Generation?.ImportRevision, after.Generation?.ImportRevision);
    }

    /// <summary>Measures rich projection against aggregates without machine-sensitive timing assertions in CI.</summary>
    [Theory]
    [InlineData(100)]
    [InlineData(500)]
    [InlineData(2000)]
    public async Task Statistics_MeasuresAllocationAndLatency_WithConcurrentReaders(int channelCount)
    {
        // Arrange
        await using var fixture = new Fixture();
        await fixture.SeedAsync(channelCount);
        await fixture.SnapshotAsync();
        await fixture.StatisticsAsync();

        // Act
        var full = await MeasureAsync(async () => { Assert.Equal(channelCount * 96, (await fixture.SnapshotAsync()).Channels.Sum(channel => channel.Programs.Count)); });
        var aggregate = await MeasureAsync(async () => { Assert.Equal(channelCount * 96, (await fixture.StatisticsAsync()).ProgramCount); });
        var concurrentFull = await MeasureAsync(() => Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(fixture.SnapshotAsync))));
        var concurrentAggregate = await MeasureAsync(() => Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(fixture.StatisticsAsync))));

        // Assert
        Assert.True(full.Bytes > 0);
        Assert.True(aggregate.Bytes > 0);
        output.WriteLine($"channels={channelCount}; programs={channelCount * 96}; full={full.Milliseconds:F2}ms/{full.Bytes}B; aggregate={aggregate.Milliseconds:F2}ms/{aggregate.Bytes}B");
        output.WriteLine($"readers=3; full={concurrentFull.Milliseconds:F2}ms/{concurrentFull.Bytes}B; aggregate={concurrentAggregate.Milliseconds:F2}ms/{concurrentAggregate.Bytes}B");
        output.WriteLine($"single allocation reduction={100.0 * (1 - (double)aggregate.Bytes / full.Bytes):F2}%; speedup={full.Milliseconds / aggregate.Milliseconds:F2}x");
    }

    private static async Task<(double Milliseconds, long Bytes)> MeasureAsync(Func<Task> read)
    {
        var measurements = new List<(double Milliseconds, long Bytes)>();
        for (var index = 0; index < 3; index++)
        {
            var before = GC.GetTotalAllocatedBytes(precise: true);
            var timer = Stopwatch.StartNew();
            await read();
            measurements.Add((timer.Elapsed.TotalMilliseconds, GC.GetTotalAllocatedBytes(precise: true) - before));
        }
        return measurements.OrderBy(measurement => measurement.Milliseconds).ElementAt(1);
    }

    private static void AssertEquivalent(VirtualGuideSnapshot snapshot, GuideStatistics statistics)
    {
        var programs = snapshot.Channels.SelectMany(channel => channel.Programs).ToArray();
        var coverage = snapshot.Channels.Where(channel => channel.Programs.Count > 0).Select(channel => channel.Programs.Max(program => program.EndUtc)).ToArray();
        Assert.Equal(snapshot.Channels.Count, statistics.ChannelCount);
        Assert.Equal(programs.Length, statistics.ProgramCount);
        Assert.Equal(programs.Length == 0 ? (DateTimeOffset?)null : programs.Min(program => program.StartUtc), statistics.EarliestProgramStartUtc);
        Assert.Equal(programs.Length == 0 ? (DateTimeOffset?)null : programs.Max(program => program.EndUtc), statistics.LatestProgramEndUtc);
        Assert.Equal(coverage.Length == 0 ? (DateTimeOffset?)null : coverage.Min(), statistics.SafeCoverageEndUtc);
    }

    private sealed class Fixture : IDbContextFactory<EpgDbContext>, IAsyncDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("lineup-guide-reads-");
        private readonly CancellationTokenSource _watchdog = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        /// <summary>Initializes an isolated production SQLite store.</summary>
        public Fixture()
        {
            _watchdog.CancelAfter(TimeSpan.FromMinutes(2));
            Topology = new StreamingTopologyStore(this);
            Guides = new DeviceGuideStore(this);
        }

        /// <summary>Gets captured SQL and controlled read interleavings.</summary>
        public Commands Commands { get; } = new();
        /// <summary>Gets the physical source identity.</summary>
        public PhysicalDeviceId DeviceId { get; } = PhysicalDeviceId.New();
        /// <summary>Gets the guide source identity.</summary>
        public GuideSourceId SourceId { get; } = GuideSourceId.New();
        /// <summary>Gets the projection identity.</summary>
        public VirtualDeviceId VirtualId { get; } = VirtualDeviceId.New();
        /// <summary>Gets the synthetic coverage start.</summary>
        public DateTimeOffset StartUtc { get; } = DateTimeOffset.UtcNow;
        /// <summary>Gets cancellation bounded to this fixture.</summary>
        public CancellationToken Token => _watchdog.Token;
        /// <summary>Gets authoritative topology storage.</summary>
        public StreamingTopologyStore Topology { get; }
        /// <summary>Gets authoritative guide storage.</summary>
        public DeviceGuideStore Guides { get; }

        /// <summary>Creates db context.</summary>
        /// <returns>The operation's result.</returns>
        public EpgDbContext CreateDbContext() => new(new DbContextOptionsBuilder<EpgDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_root.FullName, "guide.db")}").AddInterceptors(Commands).Options);

        /// <summary>Seeds 48 hours of half-hour programs without constructing rich program objects.</summary>
        public async Task SeedAsync(int count)
        {
            await Topology.ReconcileAsync(DeviceId, Enumerable.Range(0, count).Select(index => new PhysicalSourceChannel
            {
                Id = new SourceChannelId(DeviceId, index.ToString()),
                ProviderNumber = index.ToString(),
                ProviderName = "Channel"
            }), Token);
            var import = Import("Program", "<old />");
            await Guides.ImportAsync(import with
            {
                Channels = Enumerable.Range(0, count).Select(index => new GuideChannel { Id = new GuideChannelId(SourceId, index.ToString()), Number = index.ToString(), Name = "Guide" }).ToArray(),
                Programs = []
            }, Token);
            await using var context = CreateDbContext();
            await using var transaction = await context.Database.BeginTransactionAsync(Token);
            var start = StartUtc.ToUnixTimeSeconds();
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                WITH RECURSIVE slots(i) AS (SELECT 0 UNION ALL SELECT i + 1 FROM slots WHERE i < 95)
                INSERT INTO GuideSourcePrograms (GuideSourceChannelId, StartTime, EndTime, Title, Synopsis, CategoriesJson, MetadataJson)
                SELECT c.Id, {start} + i * 1800, {start} + (i + 1) * 1800, 'Program', {new string('x', 256)}, '["News","Synthetic"]', {"{}"}
                FROM GuideSourceChannels c CROSS JOIN slots
                """, Token);
            await context.DeviceGuideImports.ExecuteUpdateAsync(update => update.SetProperty(importState => importState.ProgramCount, count * 96), Token);
            await transaction.CommitAsync(Token);
        }

        /// <summary>Creates a replacement import for controlled interleaving.</summary>
        public GuideImport Import(string title, string supplementalXml) => new()
        {
            SourceId = SourceId,
            Channels = [new GuideChannel { Id = new GuideChannelId(SourceId, "0"), Number = "0", Name = "Guide" }],
            Programs = [new GuideProgram { ChannelId = new GuideChannelId(SourceId, "0"), Title = title, StartUtc = StartUtc, EndUtc = StartUtc.AddHours(1) }],
            CoverageStartUtc = StartUtc,
            CoverageEndUtc = StartUtc.AddDays(2),
            SupplementalXml = supplementalXml
        };

        /// <summary>Reads the full rich reference projection.</summary>
        public Task<VirtualGuideSnapshot> SnapshotAsync() => Guides.BuildResolvedSnapshotAsync(VirtualId, Resolve, cancellationToken: Token);

        /// <summary>Reads the SQL-backed statistics.</summary>
        public Task<GuideStatistics> StatisticsAsync() => Guides.GetStatisticsAsync(Resolve, cancellationToken: Token);

        private static IReadOnlyList<ResolvedVirtualChannel> Resolve(IReadOnlyList<PhysicalChannelIdentity> identities) =>
            identities.Select(identity => new ResolvedVirtualChannel(identity.EffectiveNumber, identity.EffectiveName, [identity.Source.Id])).ToArray();

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            Commands.ReleaseRead.TrySetResult();
            _watchdog.Dispose();
            SqliteConnection.ClearAllPools();
            _root.Delete(recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Commands : DbCommandInterceptor
    {
        private int _pause;

        /// <summary>Gets executed read shapes and their parameter values.</summary>
        public ConcurrentQueue<(string Sql, string[] Parameters)> Reads { get; } = new();
        /// <summary>Gets a signal after the topology SELECT has established its read generation.</summary>
        public TaskCompletionSource TopologyRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Gets the controlled read release.</summary>
        public TaskCompletionSource ReleaseRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Sets whether the next topology read should pause.</summary>
        public bool PauseNextTopologyRead { set => Interlocked.Exchange(ref _pause, value ? 1 : 0); }

        /// <inheritdoc />
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            Reads.Enqueue((command.CommandText, command.Parameters.Cast<DbParameter>().Select(parameter => parameter.Value?.ToString() ?? "").ToArray()));
            if (command.CommandText.Contains("FROM \"PhysicalSourceChannels\"", StringComparison.Ordinal) && Interlocked.Exchange(ref _pause, 0) == 1)
            {
                TopologyRead.TrySetResult();
                await ReleaseRead.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }
}
