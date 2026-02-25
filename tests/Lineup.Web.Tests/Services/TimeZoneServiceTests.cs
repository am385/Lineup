using Lineup.Web.Services;
using NSubstitute;
using Xunit;

namespace Lineup.Web.Tests.Services;

public class TimeZoneServiceTests
{
    private readonly IAppSettingsService _settingsService;
    private readonly AppSettings _settings;

    public TimeZoneServiceTests()
    {
        _settings = new AppSettings();
        _settingsService = Substitute.For<IAppSettingsService>();
        _settingsService.Settings.Returns(_settings);
    }

    [Fact]
    public void TimeZone_WhenNoSettingConfigured_FallsBackToLocal()
    {
        _settings.TimeZoneId = "";
        var service = new TimeZoneService(_settingsService);

        // Should not throw and should return a valid timezone
        Assert.NotNull(service.TimeZone);
    }

    [Fact]
    public void TimeZone_WhenValidIdConfigured_ReturnsMatchingZone()
    {
        _settings.TimeZoneId = "UTC";
        var service = new TimeZoneService(_settingsService);

        Assert.Equal(TimeZoneInfo.Utc, service.TimeZone);
    }

    [Fact]
    public void TimeZone_WhenInvalidIdConfigured_FallsBack()
    {
        _settings.TimeZoneId = "Invalid/Timezone_That_Does_Not_Exist";
        var service = new TimeZoneService(_settingsService);

        // Should not throw — falls back gracefully
        Assert.NotNull(service.TimeZone);
    }

    [Fact]
    public void TimeZone_CachesResult_UntilSettingChanges()
    {
        _settings.TimeZoneId = "UTC";
        var service = new TimeZoneService(_settingsService);

        var first = service.TimeZone;
        var second = service.TimeZone;

        Assert.Same(first, second);
    }

    [Fact]
    public void TimeZone_RefreshesWhenSettingIdChanges()
    {
        _settings.TimeZoneId = "UTC";
        var service = new TimeZoneService(_settingsService);

        Assert.Equal(TimeZoneInfo.Utc, service.TimeZone);

        // Change the setting — the getter should detect the mismatch and refresh
        _settings.TimeZoneId = "Pacific Standard Time";
        var tz = service.TimeZone;

        Assert.Equal("Pacific Standard Time", tz.Id);
    }

    [Fact]
    public void ConvertFromUtc_ConvertsCorrectly()
    {
        _settings.TimeZoneId = "UTC";
        var service = new TimeZoneService(_settingsService);

        var utc = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var result = service.ConvertFromUtc(utc);

        Assert.Equal(new DateTime(2025, 6, 15, 12, 0, 0), result);
    }

    [Fact]
    public void ConvertFromUtc_AppliesOffset()
    {
        // Eastern time is UTC-5 in winter, UTC-4 in summer (DST)
        _settings.TimeZoneId = "Eastern Standard Time";
        var service = new TimeZoneService(_settingsService);

        // June 15 is DST (UTC-4)
        var utc = new DateTime(2025, 6, 15, 20, 0, 0, DateTimeKind.Utc);
        var result = service.ConvertFromUtc(utc);

        Assert.Equal(new DateTime(2025, 6, 15, 16, 0, 0), result);
    }

    [Fact]
    public void ConvertToUtc_RoundTripsWithConvertFromUtc()
    {
        _settings.TimeZoneId = "Eastern Standard Time";
        var service = new TimeZoneService(_settingsService);

        var utcOriginal = new DateTime(2025, 6, 15, 20, 0, 0, DateTimeKind.Utc);
        var local = service.ConvertFromUtc(utcOriginal);
        var utcRoundTripped = service.ConvertToUtc(local);

        Assert.Equal(utcOriginal, utcRoundTripped);
    }

    [Fact]
    public void ConvertToUtc_ConvertsLocalTimeToUtc()
    {
        _settings.TimeZoneId = "UTC";
        var service = new TimeZoneService(_settingsService);

        var localTime = new DateTime(2025, 6, 15, 14, 30, 0);
        var result = service.ConvertToUtc(localTime);

        Assert.Equal(new DateTime(2025, 6, 15, 14, 30, 0), result);
        Assert.Equal(DateTimeKind.Utc, result.Kind);
    }

    [Fact]
    public void ConvertToUtc_HandlesDateBoundaryCorrectly()
    {
        // Eastern DST is UTC-4; 11 PM Eastern = 3 AM UTC next day
        _settings.TimeZoneId = "Eastern Standard Time";
        var service = new TimeZoneService(_settingsService);

        var localTime = new DateTime(2025, 6, 15, 23, 0, 0); // 11 PM Eastern
        var utcResult = service.ConvertToUtc(localTime);

        Assert.Equal(new DateTime(2025, 6, 16, 3, 0, 0), utcResult); // 3 AM UTC next day
    }

    [Fact]
    public void Now_ReturnsTimeInConfiguredZone()
    {
        _settings.TimeZoneId = "UTC";
        var service = new TimeZoneService(_settingsService);

        var now = service.Now;
        var utcNow = DateTime.UtcNow;

        // Should be very close to UTC now (within a second)
        Assert.InRange((utcNow - now).TotalSeconds, -1, 1);
    }

    [Fact]
    public void Today_ReturnsDateOnly()
    {
        _settings.TimeZoneId = "UTC";
        var service = new TimeZoneService(_settingsService);

        var today = service.Today;

        Assert.Equal(TimeSpan.Zero, today.TimeOfDay);
        Assert.Equal(DateTime.UtcNow.Date, today);
    }
}
