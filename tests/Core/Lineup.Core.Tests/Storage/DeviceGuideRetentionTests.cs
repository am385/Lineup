using Lineup.Common.Guides;
using Lineup.Common.Streaming;
using Lineup.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Lineup.Core.Tests.Storage;

/// <summary>Verifies source-scoped guide coverage replacement and cached history retention.</summary>
public sealed class DeviceGuideRetentionTests
{
    /// <summary>Verifies overlapping historical imports retain ended programs, update repeated records, and do not reintroduce expired data.</summary>
    [Fact]
    public async Task ImportAsync_HistoricalCoverage_RetainsHistoryAndPrunesExpiredIncomingPrograms()
    {
        // Arrange
        using var fixture = new Fixture(24);
        var repeated = fixture.Program("Repeated", fixture.Now.AddHours(-4), fixture.Now.AddHours(-3)) with { Synopsis = "Original" };
        await fixture.Store.ImportAsync(fixture.Import(
        [
            fixture.Program("History", fixture.Now.AddHours(-2), fixture.Now.AddHours(-1)),
            repeated,
            fixture.Program("Stale", fixture.Now, fixture.Now.AddHours(1)),
            fixture.Program("Future", fixture.Now.AddHours(10), fixture.Now.AddHours(11))
        ], fixture.Now.AddHours(-5), fixture.Now.AddHours(12)), TestContext.Current.CancellationToken);
        var replacement = fixture.Import(
        [
            repeated with { Synopsis = "Updated" },
            fixture.Program("Expired incoming", fixture.Now.AddHours(-30), fixture.Now.AddHours(-29)),
            fixture.Program("Fresh", fixture.Now, fixture.Now.AddHours(2))
        ], fixture.Now.AddHours(-31), fixture.Now.AddHours(2));

        // Act
        await fixture.Store.ImportAsync(replacement, TestContext.Current.CancellationToken);
        await fixture.Store.ImportAsync(replacement, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(["Fresh", "Future", "History", "Repeated"], await fixture.ReadTitlesAsync());
        await using var context = fixture.Factory.CreateDbContext();
        Assert.Equal("Updated", (await context.GuideSourcePrograms.SingleAsync(program => program.Title == "Repeated", TestContext.Current.CancellationToken)).Synopsis);
    }

    /// <summary>Verifies narrow imports preserve retained history and future coverage while pruning expired cached programs.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(24)]
    [InlineData(720)]
    [InlineData(null)]
    public async Task ImportAsync_NarrowCoverage_MergesAndPrunesUsingRegisteredRetention(int? retentionHours)
    {
        // Arrange
        using var fixture = new Fixture(retentionHours);
        var cutoff = fixture.Now.AddHours(-(retentionHours ?? 24));
        var historyEnd = fixture.Now.AddHours(-Math.Max(1, (retentionHours ?? 24) / 2));
        await fixture.Store.ImportAsync(fixture.Import(
        [
            fixture.Program("Expired", cutoff.AddHours(-2), cutoff.AddHours(-1)),
            fixture.Program("At cutoff", cutoff.AddHours(-1), cutoff),
            fixture.Program("History", historyEnd.AddHours(-1), historyEnd),
            fixture.Program("Stale", fixture.Now, fixture.Now.AddHours(1)),
            fixture.Program("Future", fixture.Now.AddHours(10), fixture.Now.AddHours(11))
        ], fixture.Now.AddHours(-1000), fixture.Now.AddHours(12)), TestContext.Current.CancellationToken);
        var replacement = fixture.Import(
            [fixture.Program("Fresh", fixture.Now, fixture.Now.AddHours(2))], fixture.Now, fixture.Now.AddHours(2));

        // Act
        await fixture.Store.ImportAsync(replacement, TestContext.Current.CancellationToken);
        await fixture.Store.ImportAsync(replacement, TestContext.Current.CancellationToken);

        // Assert
        var expected = retentionHours == 0 ? new[] { "Fresh", "Future" } : ["Fresh", "Future", "History"];
        Assert.Equal(expected, await fixture.ReadTitlesAsync());
        var state = Assert.Single(await fixture.Store.ReadImportStatesAsync(TestContext.Current.CancellationToken));
        Assert.Equal(replacement.CoverageStartUtc, state.CoverageStartUtc);
        Assert.Equal(replacement.CoverageEndUtc, state.CoverageEndUtc);
        Assert.Equal(1, state.ProgramCount);
    }

    /// <summary>Verifies empty coverage removes overlapping stale programs without removing programs touching either boundary.</summary>
    [Fact]
    public async Task ImportAsync_EmptyCoverage_ReplacesOverlapsAndPreservesBoundaryPrograms()
    {
        // Arrange
        using var fixture = new Fixture(24);
        await fixture.Store.ImportAsync(fixture.Import(
        [
            fixture.Program("Before", fixture.Now.AddHours(-1), fixture.Now),
            fixture.Program("Inside", fixture.Now, fixture.Now.AddHours(1)),
            fixture.Program("Crosses start", fixture.Now.AddMinutes(-30), fixture.Now.AddMinutes(30)),
            fixture.Program("Crosses end", fixture.Now.AddMinutes(90), fixture.Now.AddMinutes(150)),
            fixture.Program("Crosses both", fixture.Now.AddHours(-2), fixture.Now.AddHours(3)),
            fixture.Program("After", fixture.Now.AddHours(2), fixture.Now.AddHours(3))
        ], fixture.Now.AddHours(-2), fixture.Now.AddHours(3)), TestContext.Current.CancellationToken);

        // Act
        await fixture.Store.ImportAsync(fixture.Import([], fixture.Now, fixture.Now.AddHours(2)), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(["After", "Before"], await fixture.ReadTitlesAsync());
        Assert.Equal(0, Assert.Single(await fixture.Store.ReadImportStatesAsync(TestContext.Current.CancellationToken)).ProgramCount);
    }

    /// <summary>Verifies pruning and replacement do not modify another source, even with the same channel key and expired programs.</summary>
    [Fact]
    public async Task ImportAsync_AnotherSource_IsNotPrunedOrReplaced()
    {
        // Arrange
        using var fixture = new Fixture(24);
        var initial = fixture.Import(
            [fixture.Program("Owner", fixture.Now.AddHours(-3), fixture.Now.AddHours(-2))], fixture.Now.AddDays(-1), fixture.Now.AddDays(1));
        await fixture.Store.ImportAsync(initial, TestContext.Current.CancellationToken);
        var otherId = GuideSourceId.New();
        var otherChannel = new GuideChannelId(otherId, fixture.ChannelId.ProviderKey);
        var other = initial with
        {
            SourceId = otherId,
            Channels = [initial.Channels[0] with { Id = otherChannel }],
            Programs = [initial.Programs[0] with { ChannelId = otherChannel, Title = "Other" }]
        };
        await fixture.Store.ImportAsync(other, TestContext.Current.CancellationToken);
        var prior = Assert.Single(await fixture.Store.ReadImportStatesAsync(TestContext.Current.CancellationToken), item => item.GuideSourceId == otherId);
        fixture.Policy.HistoryRetention.Returns(TimeSpan.Zero);

        // Act
        await fixture.Store.ImportAsync(fixture.Import([], fixture.Now, fixture.Now.AddHours(1)), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(["Other"], await fixture.ReadTitlesAsync());
        var current = Assert.Single(await fixture.Store.ReadImportStatesAsync(TestContext.Current.CancellationToken), item => item.GuideSourceId == otherId);
        Assert.Equal(prior, current);
    }

    /// <summary>Verifies an invalid replacement preserves committed programs, channel details, and import metadata together.</summary>
    [Fact]
    public async Task ImportAsync_InvalidProgram_RollsBackCoverageReplacement()
    {
        // Arrange
        using var fixture = new Fixture(0);
        var initial = fixture.Import(
            [fixture.Program("Committed", fixture.Now, fixture.Now.AddHours(1))], fixture.Now, fixture.Now.AddHours(1));
        await fixture.Store.ImportAsync(initial, TestContext.Current.CancellationToken);
        var prior = Assert.Single(await fixture.Store.ReadImportStatesAsync(TestContext.Current.CancellationToken));
        var invalid = initial with
        {
            Channels = [initial.Channels[0] with { Number = "Changed" }],
            Programs = [initial.Programs[0] with { ChannelId = new GuideChannelId(fixture.SourceId, "missing") }]
        };

        // Act
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Store.ImportAsync(invalid, TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(["Committed"], await fixture.ReadTitlesAsync());
        Assert.Equal(prior, Assert.Single(await fixture.Store.ReadImportStatesAsync(TestContext.Current.CancellationToken)));
        await using var context = fixture.Factory.CreateDbContext();
        Assert.Equal("7", (await context.GuideSourceChannels.SingleAsync(TestContext.Current.CancellationToken)).Number);
    }

    /// <summary>Verifies metadata persistence failure rolls back completed merging and pruning within the same source transaction.</summary>
    [Fact]
    public async Task ImportAsync_MetadataWriteFails_RollsBackMergedProgramsAndPruning()
    {
        // Arrange
        using var fixture = new Fixture(24);
        var initial = fixture.Import(
        [
            fixture.Program("History", fixture.Now.AddHours(-3), fixture.Now.AddHours(-2)),
            fixture.Program("Original", fixture.Now, fixture.Now.AddHours(1))
        ], fixture.Now.AddHours(-4), fixture.Now.AddHours(1));
        await fixture.Store.ImportAsync(initial, TestContext.Current.CancellationToken);
        var prior = Assert.Single(await fixture.Store.ReadImportStatesAsync(TestContext.Current.CancellationToken));
        await using (var setup = fixture.Factory.CreateDbContext())
        {
            await setup.Database.ExecuteSqlRawAsync(
                "CREATE TRIGGER RejectGuideImportUpdate BEFORE UPDATE ON DeviceGuideImports BEGIN SELECT RAISE(ABORT, 'Synthetic metadata failure'); END;",
                TestContext.Current.CancellationToken);
        }
        fixture.Policy.HistoryRetention.Returns(TimeSpan.Zero);
        var replacement = fixture.Import(
            [fixture.Program("Replacement", fixture.Now, fixture.Now.AddHours(2))], fixture.Now, fixture.Now.AddHours(2)) with
        {
            Channels = [initial.Channels[0] with { Number = "Changed" }]
        };

        // Act
        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Store.ImportAsync(replacement, TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(["History", "Original"], await fixture.ReadTitlesAsync());
        Assert.Equal(prior, Assert.Single(await fixture.Store.ReadImportStatesAsync(TestContext.Current.CancellationToken)));
        await using var context = fixture.Factory.CreateDbContext();
        Assert.Equal("7", (await context.GuideSourceChannels.SingleAsync(TestContext.Current.CancellationToken)).Number);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("lineup-guide-retention-");
        private readonly ServiceProvider _services;

        internal Fixture(int? retentionHours)
        {
            var path = Path.Combine(_directory.FullName, "guides.db");
            Factory = new ContextFactory(path);
            var services = new ServiceCollection();
            services.AddEpgCore(path);
            services.AddSingleton<IDbContextFactory<EpgDbContext>>(Factory);
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
            if (retentionHours.HasValue)
            {
                Policy.HistoryRetention.Returns(TimeSpan.FromHours(retentionHours.Value));
                services.AddSingleton(Policy);
            }
            _services = services.BuildServiceProvider();
            Store = _services.GetRequiredService<DeviceGuideStore>();
        }

        internal DateTimeOffset Now { get; } = DateTimeOffset.Parse("2026-10-23T12:00:00Z");

        internal GuideSourceId SourceId { get; } = GuideSourceId.New();

        internal GuideChannelId ChannelId => new(SourceId, "station");

        internal DeviceGuideStore Store { get; }

        internal IEpgRetentionPolicy Policy { get; } = Substitute.For<IEpgRetentionPolicy>();

        internal ContextFactory Factory { get; }

        internal GuideProgram Program(string title, DateTimeOffset start, DateTimeOffset end) =>
            new() { ChannelId = ChannelId, Title = title, StartUtc = start, EndUtc = end };

        internal GuideImport Import(IReadOnlyList<GuideProgram> programs, DateTimeOffset start, DateTimeOffset end) => new()
        {
            SourceId = SourceId,
            Channels = [new GuideChannel { Id = ChannelId, Number = "7", Name = "News" }],
            Programs = programs,
            CoverageStartUtc = start,
            CoverageEndUtc = end,
            FetchedAtUtc = Now
        };

        internal async Task<string[]> ReadTitlesAsync()
        {
            await using var context = Factory.CreateDbContext();
            return await context.GuideSourcePrograms.OrderBy(program => program.Title).Select(program => program.Title).ToArrayAsync(TestContext.Current.CancellationToken);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _services.Dispose();
            _directory.Delete(recursive: true);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class ContextFactory(string path) : IDbContextFactory<EpgDbContext>
    {
        /// <inheritdoc />
        public EpgDbContext CreateDbContext() => new(new DbContextOptionsBuilder<EpgDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);
    }
}
