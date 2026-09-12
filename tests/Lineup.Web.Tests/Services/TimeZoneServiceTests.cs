using Lineup.Web.Services;
using NSubstitute;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>
/// Represents time zone service tests.
/// </summary>
public class TimeZoneServiceTests
{
    private readonly IAppSettingsService _settingsService;
    private readonly AppSettings _settings;

    /// <summary>
    /// Initializes a new instance of the <see cref="TimeZoneServiceTests"/> class.
    /// </summary>
    public TimeZoneServiceTests()
    {
        _settings = new AppSettings();
        _settingsService = Substitute.For<IAppSettingsService>();
        _settingsService.Settings.Returns(_settings);
    }

    /// <summary>
    /// Performs the time zone_when no setting configured_falls back to local operation.
    /// </summary>
    [Fact]
    public void TimeZone_WhenNoSettingConfigured_FallsBackToLocal()
    {
        // Arrange
        _settings.TimeZoneId = "";
        // Act
        var service = new TimeZoneService(_settingsService);

        // Should not throw and should return a valid timezone
        // Assert
        Assert.NotNull(service.TimeZone);
    }

    /// <summary>
    /// Performs the time zone_when valid id configured_returns matching zone operation.
    /// </summary>
    [Fact]
    public void TimeZone_WhenValidIdConfigured_ReturnsMatchingZone()
    {
        // Arrange
        _settings.TimeZoneId = "UTC";
        // Act
        var service = new TimeZoneService(_settingsService);

        // Assert
        Assert.Equal(TimeZoneInfo.Utc, service.TimeZone);
    }

    /// <summary>
    /// Performs the time zone_when invalid id configured_falls back operation.
    /// </summary>
    [Fact]
    public void TimeZone_WhenInvalidIdConfigured_FallsBack()
    {
        // Arrange
        _settings.TimeZoneId = "Invalid/Timezone_That_Does_Not_Exist";
        // Act
        var service = new TimeZoneService(_settingsService);

        // Should not throw � falls back gracefully
        // Assert
        Assert.NotNull(service.TimeZone);
    }

    /// <summary>
    /// Performs the time zone_caches result_until setting changes operation.
    /// </summary>
    [Fact]
    public void TimeZone_CachesResult_UntilSettingChanges()
    {
        // Arrange
        _settings.TimeZoneId = "UTC";
        var service = new TimeZoneService(_settingsService);

        // Act
        var first = service.TimeZone;
        var second = service.TimeZone;

        // Assert
        Assert.Same(first, second);
    }

    /// <summary>
    /// Performs the time zone_refreshes when setting id changes operation.
    /// </summary>
    [Fact]
    public void TimeZone_RefreshesWhenSettingIdChanges()
    {
        // Arrange
        _settings.TimeZoneId = "UTC";
        // Act
        var service = new TimeZoneService(_settingsService);

        // Assert
        Assert.Equal(TimeZoneInfo.Utc, service.TimeZone);

        // Change the setting � the getter should detect the mismatch and refresh
        _settings.TimeZoneId = "Pacific Standard Time";
        var tz = service.TimeZone;

        Assert.Equal("Pacific Standard Time", tz.Id);
    }

    /// <summary>
    /// Performs the convert from utc_converts correctly operation.
    /// </summary>
    [Fact]
    public void ConvertFromUtc_ConvertsCorrectly()
    {
        // Arrange
        _settings.TimeZoneId = "UTC";
        var service = new TimeZoneService(_settingsService);

        var utc = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        // Act
        var result = service.ConvertFromUtc(utc);

        // Assert
        Assert.Equal(new DateTime(2025, 6, 15, 12, 0, 0), result);
    }

    /// <summary>
    /// Performs the convert from utc_applies offset operation.
    /// </summary>
    [Fact]
    public void ConvertFromUtc_AppliesOffset()
    {
        // Eastern time is UTC-5 in winter, UTC-4 in summer (DST)
        // Arrange
        _settings.TimeZoneId = "Eastern Standard Time";
        var service = new TimeZoneService(_settingsService);

        // June 15 is DST (UTC-4)
        var utc = new DateTime(2025, 6, 15, 20, 0, 0, DateTimeKind.Utc);
        // Act
        var result = service.ConvertFromUtc(utc);

        // Assert
        Assert.Equal(new DateTime(2025, 6, 15, 16, 0, 0), result);
    }

    /// <summary>
    /// Performs the convert to utc_round trips with convert from utc operation.
    /// </summary>
    [Fact]
    public void ConvertToUtc_RoundTripsWithConvertFromUtc()
    {
        // Arrange
        _settings.TimeZoneId = "Eastern Standard Time";
        var service = new TimeZoneService(_settingsService);

        var utcOriginal = new DateTime(2025, 6, 15, 20, 0, 0, DateTimeKind.Utc);
        var local = service.ConvertFromUtc(utcOriginal);
        // Act
        var utcRoundTripped = service.ConvertToUtc(local);

        // Assert
        Assert.Equal(utcOriginal, utcRoundTripped);
    }

    /// <summary>
    /// Performs the convert to utc_converts local time to utc operation.
    /// </summary>
    [Fact]
    public void ConvertToUtc_ConvertsLocalTimeToUtc()
    {
        // Arrange
        _settings.TimeZoneId = "UTC";
        var service = new TimeZoneService(_settingsService);

        var localTime = new DateTime(2025, 6, 15, 14, 30, 0);
        // Act
        var result = service.ConvertToUtc(localTime);

        // Assert
        Assert.Equal(new DateTime(2025, 6, 15, 14, 30, 0), result);
        Assert.Equal(DateTimeKind.Utc, result.Kind);
    }

    /// <summary>
    /// Performs the convert to utc_handles date boundary correctly operation.
    /// </summary>
    [Fact]
    public void ConvertToUtc_HandlesDateBoundaryCorrectly()
    {
        // Eastern DST is UTC-4; 11 PM Eastern = 3 AM UTC next day
        // Arrange
        _settings.TimeZoneId = "Eastern Standard Time";
        var service = new TimeZoneService(_settingsService);

        var localTime = new DateTime(2025, 6, 15, 23, 0, 0); // 11 PM Eastern
        // Act
        var utcResult = service.ConvertToUtc(localTime);

        // Assert
        Assert.Equal(new DateTime(2025, 6, 16, 3, 0, 0), utcResult); // 3 AM UTC next day
    }

    /// <summary>
    /// Performs the now_returns time in configured zone operation.
    /// </summary>
    [Fact]
    public void Now_ReturnsTimeInConfiguredZone()
    {
        // Arrange
        _settings.TimeZoneId = "UTC";
        var service = new TimeZoneService(_settingsService);

        var now = service.Now;
        // Act
        var utcNow = DateTime.UtcNow;

        // Should be very close to UTC now (within a second)
        // Assert
        Assert.InRange((utcNow - now).TotalSeconds, -1, 1);
    }

    /// <summary>
    /// Performs the today_returns date only operation.
    /// </summary>
    [Fact]
    public void Today_ReturnsDateOnly()
    {
        // Arrange
        _settings.TimeZoneId = "UTC";
        var service = new TimeZoneService(_settingsService);

        // Act
        var today = service.Today;

        // Assert
        Assert.Equal(TimeSpan.Zero, today.TimeOfDay);
        Assert.Equal(DateTime.UtcNow.Date, today);
    }
}
