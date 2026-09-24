using Lineup.Core.Storage;
using Lineup.Core.Storage.Entities;
using Lineup.HDHomeRun.Api.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lineup.Core.Tests.Storage;

/// <summary>
/// Verifies cached channel metadata persistence.
/// </summary>
public class EpgRepositoryChannelTests
{
    /// <summary>
    /// Verifies that tuner-provided channel flags survive a cache round trip.
    /// </summary>
    [Fact]
    public async Task StoreAndLoadChannel_PreservesTunerFlags()
    {
        // Arrange
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<EpgDbContext>().UseSqlite(connection).Options;
        await using var context = new EpgDbContext(options);
        var repository = new EpgRepository(context, NullLogger<EpgRepository>.Instance);
        await repository.EnsureDatabaseCreatedAsync();
        var channel = new HDHomeRunChannelEpgSegment
        {
            GuideNumber = "117.1",
            GuideName = "Protected",
            DRM = true,
            Favorite = true
        };

        // Act
        await repository.StoreChannelAsync(channel);
        // Assert
        var cachedChannel = Assert.Single(await repository.GetChannelsAsync());

        Assert.True(cachedChannel.DRM);
        Assert.True(cachedChannel.Favorite);
    }

    /// <summary>
    /// Verifies existing databases receive newly introduced tuner metadata columns.
    /// </summary>
    [Fact]
    public async Task EnsureDatabaseCreatedAsync_ExistingChannelTable_AddsTunerMetadataColumns()
    {
        // Arrange
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE Channels (
                    Id INTEGER NOT NULL CONSTRAINT PK_Channels PRIMARY KEY AUTOINCREMENT,
                    GuideNumber TEXT NOT NULL,
                    GuideName TEXT NULL,
                    Affiliate TEXT NULL,
                    ImageURL TEXT NULL,
                    LastUpdatedUtc TEXT NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        var options = new DbContextOptionsBuilder<EpgDbContext>().UseSqlite(connection).Options;
        await using var context = new EpgDbContext(options);
        var repository = new EpgRepository(context, NullLogger<EpgRepository>.Instance);

        // Act
        await repository.EnsureDatabaseCreatedAsync();

        // Assert
        var columns = new List<string>();
        await using var schemaCommand = connection.CreateCommand();
        schemaCommand.CommandText = "PRAGMA table_info('Channels')";
        await using var reader = await schemaCommand.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            columns.Add(reader.GetString(1));
        }
        Assert.Contains(nameof(StoredChannel.DRM), columns);
        Assert.Contains(nameof(StoredChannel.Favorite), columns);
    }

    /// <summary>
    /// Verifies that importing a complete guide snapshot removes stale cache records.
    /// </summary>
    [Fact]
    public async Task ReplaceRawEpgDataAsync_ReplacesExistingGuide()
    {
        // Arrange
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<EpgDbContext>().UseSqlite(connection).Options;
        await using var context = new EpgDbContext(options);
        var repository = new EpgRepository(context, NullLogger<EpgRepository>.Instance);
        await repository.EnsureDatabaseCreatedAsync();
        await repository.StoreRawSegmentAsync(
        [
            new HDHomeRunChannelEpgSegment
            {
                GuideNumber = "2.1",
                GuideName = "Old",
                Guide = [new HDHomeRunProgram { Title = "Old Show", StartTime = 100, EndTime = 200 }]
            }
        ]);
        var replacement = new HDHomeRunChannelEpgSegment
        {
            GuideNumber = "7.1",
            GuideName = "New",
            Guide = [new HDHomeRunProgram { Title = "New Show", StartTime = 300, EndTime = 400 }]
        };

        // Act
        await repository.ReplaceRawEpgDataAsync([replacement], TestContext.Current.CancellationToken);

        // Assert
        var channel = Assert.Single(await repository.GetChannelsAsync());
        var programme = Assert.Single(await repository.GetProgramsAsync());
        Assert.Equal("7.1", channel.GuideNumber);
        Assert.Equal("New Show", programme.Title);
    }

    /// <summary>
    /// Verifies cancellation rolls back a guide replacement before its commit point.
    /// </summary>
    [Fact]
    public async Task ReplaceRawEpgDataAsync_WhenCancelled_PreservesExistingGuide()
    {
        // Arrange
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<EpgDbContext>().UseSqlite(connection).Options;
        await using var context = new EpgDbContext(options);
        var repository = new EpgRepository(context, NullLogger<EpgRepository>.Instance);
        await repository.EnsureDatabaseCreatedAsync();
        await repository.StoreRawSegmentAsync(
        [
            new HDHomeRunChannelEpgSegment
            {
                GuideNumber = "2.1",
                GuideName = "Old"
            }
        ]);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();

        // Act
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            repository.ReplaceRawEpgDataAsync([new HDHomeRunChannelEpgSegment { GuideNumber = "7.1", GuideName = "New" }], cancellation.Token));

        // Assert
        var channel = Assert.Single(await repository.GetChannelsAsync());
        Assert.Equal("2.1", channel.GuideNumber);
    }

    /// <summary>
    /// Verifies authoritative imports retain recent history, prune expired history, and replace stale future schedules.
    /// </summary>
    [Fact]
    public async Task ImportGuideAsync_ReconcilesFutureAndRetainsConfiguredHistory()
    {
        // Arrange
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<EpgDbContext>().UseSqlite(connection).Options;
        await using var context = new EpgDbContext(options);
        var repository = new EpgRepository(context, NullLogger<EpgRepository>.Instance);
        await repository.EnsureDatabaseCreatedAsync();
        var now = DateTimeOffset.UtcNow;
        await repository.StoreRawSegmentAsync(
        [
            new HDHomeRunChannelEpgSegment
            {
                GuideNumber = "7.1",
                GuideName = "Channel",
                Guide =
                [
                    CreateProgram("Expired", now.AddHours(-30), now.AddHours(-29)),
                    CreateProgram("Recent", now.AddHours(-2), now.AddHours(-1)),
                    CreateProgram("Stale Future", now.AddHours(1), now.AddHours(2))
                ]
            }
        ]);
        var replacement = new HDHomeRunChannelEpgSegment
        {
            GuideNumber = "7.1",
            GuideName = "Channel",
            Guide = [CreateProgram("Updated Future", now.AddHours(1), now.AddHours(2))]
        };

        // Act
        await repository.ImportGuideAsync([replacement], TimeSpan.FromHours(24), TestContext.Current.CancellationToken);

        // Assert
        var titles = (await repository.GetProgramsAsync()).Select(program => program.Title).ToArray();
        Assert.Contains("Recent", titles);
        Assert.Contains("Updated Future", titles);
        Assert.DoesNotContain("Expired", titles);
        Assert.DoesNotContain("Stale Future", titles);
        Assert.Single(await context.GuideImports.Where(import => import.CompletedUtc != null).ToListAsync(TestContext.Current.CancellationToken));
    }

    private static HDHomeRunProgram CreateProgram(string title, DateTimeOffset start, DateTimeOffset end) => new()
    {
        Title = title,
        StartTime = start.ToUnixTimeSeconds(),
        EndTime = end.ToUnixTimeSeconds()
    };
}
