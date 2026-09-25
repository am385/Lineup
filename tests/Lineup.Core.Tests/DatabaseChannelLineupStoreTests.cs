using Lineup.Core.Storage;
using Lineup.HDHomeRun.Device.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Lineup.Core.Tests;

/// <summary>
/// Verifies authoritative SQLite physical-lineup persistence.
/// </summary>
public sealed class DatabaseChannelLineupStoreTests
{
    /// <summary>
    /// Verifies a temporarily missing channel becomes inactive and keeps its disabled preference when it returns.
    /// </summary>
    [Fact]
    public async Task StoreAsync_MissingDisabledChannel_PreservesPreferenceWhenChannelReturns()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-channel-db-");
        try
        {
            var factory = new TestContextFactory(Path.Combine(root.FullName, "lineup.db"));
            var store = new ChannelLineupStore(factory);
            await store.StoreAsync([CreateChannel("7.1"), CreateChannel("9.1")], TestContext.Current.CancellationToken);
            await store.SetChannelEnabledAsync("9.1", enabled: false, TestContext.Current.CancellationToken);

            // Act
            var missingSnapshot = await store.StoreAsync([CreateChannel("7.1")], TestContext.Current.CancellationToken);
            var returnedSnapshot = await store.StoreAsync([CreateChannel("7.1"), CreateChannel("9.1")], TestContext.Current.CancellationToken);

            // Assert
            Assert.DoesNotContain(missingSnapshot.Channels, channel => channel.GuideNumber == "9.1");
            Assert.Contains(returnedSnapshot.Channels, channel => channel.GuideNumber == "9.1");
            Assert.False(returnedSnapshot.IsChannelEnabled("9.1"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            root.Delete(recursive: true);
        }
    }

    private static HDHomeRunChannel CreateChannel(string guideNumber) => new()
    {
        GuideNumber = guideNumber,
        GuideName = $"Channel {guideNumber}",
        URL = $"http://device/auto/v{guideNumber}"
    };

    private sealed class TestContextFactory(string path) : IDbContextFactory<EpgDbContext>
    {
        private readonly DbContextOptions<EpgDbContext> _options = new DbContextOptionsBuilder<EpgDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;

        public EpgDbContext CreateDbContext() => new(_options);
    }
}
