using Lineup.Core.Storage;
using Xunit;

namespace Lineup.Web.Tests.Components.Pages.Epg;

/// <summary>
/// Tests for the Dashboard component logic.
/// Note: Full component rendering tests would require an IEpgOrchestrator interface.
/// These tests validate the supporting types and data flows.
/// </summary>
public class DashboardTests
{
    /// <summary>
    /// Performs the cache statistics_can be created operation.
    /// </summary>
    [Fact]
    public void CacheStatistics_CanBeCreated()
    {
        // Arrange
        // Act
        var stats = new CacheStatistics(ChannelCount: 50, ProgramCount: 1000, EarliestProgramStart: DateTime.UtcNow.AddDays(-1), LatestProgramEnd: DateTime.UtcNow.AddDays(5), TotalTimeSpan: TimeSpan.FromDays(6));

        // Assert
        Assert.Equal(50, stats.ChannelCount);
        Assert.Equal(1000, stats.ProgramCount);
        Assert.NotNull(stats.EarliestProgramStart);
        Assert.NotNull(stats.LatestProgramEnd);
        Assert.NotNull(stats.TotalTimeSpan);
    }

    /// <summary>
    /// Performs the cache statistics_supports null optional values operation.
    /// </summary>
    [Fact]
    public void CacheStatistics_SupportsNullOptionalValues()
    {
        // Arrange
        // Act
        var stats = new CacheStatistics(ChannelCount: 0, ProgramCount: 0, EarliestProgramStart: null, LatestProgramEnd: null, TotalTimeSpan: null);

        // Assert
        Assert.Equal(0, stats.ChannelCount);
        Assert.Equal(0, stats.ProgramCount);
        Assert.Null(stats.EarliestProgramStart);
        Assert.Null(stats.LatestProgramEnd);
        Assert.Null(stats.TotalTimeSpan);
    }

    /// <summary>
    /// Performs the cache statistics_total time span_represents remaining coverage operation.
    /// </summary>
    [Fact]
    public void CacheStatistics_TotalTimeSpan_RepresentsRemainingCoverage()
    {
        // TotalTimeSpan should represent remaining EPG coverage from now,
        // not the full historical span from earliest to latest.
        // The repository computes this; here we verify the record accepts the value.
        // Arrange
        var now = DateTime.UtcNow;
        var earliest = now.AddDays(-1);  // started yesterday
        var latest = now.AddDays(4);     // ends 4 days from now
        var remainingSpan = latest - now; // ~4 days, not 5

        // Act
        var stats = new CacheStatistics(ChannelCount: 25, ProgramCount: 500, EarliestProgramStart: earliest, LatestProgramEnd: latest, TotalTimeSpan: remainingSpan);

        // Assert - remaining span should be approximately 4 days, not 5
        // Assert
        Assert.NotNull(stats.TotalTimeSpan);
        Assert.Equal(4, stats.TotalTimeSpan?.Days);
    }

    /// <summary>
    /// Performs the cache statistics_record equality operation.
    /// </summary>
    [Fact]
    public void CacheStatistics_RecordEquality()
    {
        // Arrange
        var earliest = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var latest = new DateTime(2024, 6, 20, 12, 0, 0, DateTimeKind.Utc);

        var stats1 = new CacheStatistics(10, 100, earliest, latest, TimeSpan.FromDays(5));
        var stats2 = new CacheStatistics(10, 100, earliest, latest, TimeSpan.FromDays(5));
        // Act
        var stats3 = new CacheStatistics(20, 100, earliest, latest, TimeSpan.FromDays(5));

        // Assert
        Assert.Equal(stats1, stats2);
        Assert.NotEqual(stats1, stats3);
    }

    /// <summary>
    /// Performs the time span_formats correctly_for dashboard display operation.
    /// </summary>
    [Fact]
    public void TimeSpan_FormatsCorrectly_ForDashboardDisplay()
    {
        // Arrange
        var timeSpan = new TimeSpan(3, 5, 30, 0); // 3 days, 5 hours, 30 minutes

        // Act
        var formatted = timeSpan.ToString(@"d' days 'h' hrs'");

        // Assert
        Assert.Equal("3 days 5 hrs", formatted);
    }

    /// <summary>
    /// Performs the date time_formats correctly_for dashboard display operation.
    /// </summary>
    [Fact]
    public void DateTime_FormatsCorrectly_ForDashboardDisplay()
    {
        // Arrange
        var dateTime = new DateTime(2024, 6, 15, 14, 30, 0, DateTimeKind.Utc);

        // Act
        var formatted = dateTime.ToString("MM/dd HH:mm");

        // Assert
        Assert.Equal("06/15 14:30", formatted);
    }
}
