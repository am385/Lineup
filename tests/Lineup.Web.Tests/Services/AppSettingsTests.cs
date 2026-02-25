using System.Text.Json;
using Lineup.Web.Services;
using Xunit;

namespace Lineup.Web.Tests.Services;

public class AppSettingsTests
{
    [Fact]
    public void TargetDays_ClampedToMinimumOfOne()
    {
        var settings = new AppSettings { TargetDays = 0 };
        Assert.Equal(1, settings.TargetDays);

        settings.TargetDays = -5;
        Assert.Equal(1, settings.TargetDays);
    }

    [Fact]
    public void TargetDays_AcceptsValidValues()
    {
        var settings = new AppSettings { TargetDays = 7 };
        Assert.Equal(7, settings.TargetDays);
    }

    [Fact]
    public void AutoFetchInterval_DefaultsToZero()
    {
        var settings = new AppSettings();
        Assert.Equal(TimeSpan.Zero, settings.AutoFetchInterval);
    }

    [Fact]
    public void AutoFetchInterval_AcceptsTimeSpan()
    {
        var settings = new AppSettings { AutoFetchInterval = TimeSpan.FromHours(2) };
        Assert.Equal(TimeSpan.FromHours(2), settings.AutoFetchInterval);
    }

    [Fact]
    public void IsAutoFetchEnabled_FalseWhenZero()
    {
        var settings = new AppSettings { AutoFetchInterval = TimeSpan.Zero };
        Assert.False(settings.IsAutoFetchEnabled);
    }

    [Fact]
    public void IsAutoFetchEnabled_TrueWhenPositive()
    {
        var settings = new AppSettings { AutoFetchInterval = TimeSpan.FromMinutes(30) };
        Assert.True(settings.IsAutoFetchEnabled);
    }

    [Fact]
    public void IsSetupComplete_DefaultsToFalse()
    {
        var settings = new AppSettings();
        Assert.False(settings.IsSetupComplete);
    }

    [Fact]
    public void DeviceRefreshIntervalMinutes_ClampedToZero()
    {
        var settings = new AppSettings { DeviceRefreshIntervalMinutes = -10 };
        Assert.Equal(0, settings.DeviceRefreshIntervalMinutes);
    }

    [Fact]
    public void TunerRefreshIntervalSeconds_ClampedToZero()
    {
        var settings = new AppSettings { TunerRefreshIntervalSeconds = -1 };
        Assert.Equal(0, settings.TunerRefreshIntervalSeconds);
    }

    [Fact]
    public void Serialization_AutoFetchInterval_RoundTrips()
    {
        var original = new AppSettings { AutoFetchInterval = new TimeSpan(1, 2, 30, 0) };

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<AppSettings>(json)!;

        Assert.Equal(original.AutoFetchInterval, deserialized.AutoFetchInterval);
    }

    [Fact]
    public void Deserialization_NewJson_UsesAutoFetchInterval()
    {
        var json = """{"AutoFetchInterval": "02:00:00"}""";

        var settings = JsonSerializer.Deserialize<AppSettings>(json)!;

        Assert.Equal(TimeSpan.FromHours(2), settings.AutoFetchInterval);
    }

    [Fact]
    public void Serialization_IsSetupComplete_RoundTrips()
    {
        var original = new AppSettings { IsSetupComplete = true };

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<AppSettings>(json)!;

        Assert.True(deserialized.IsSetupComplete);
    }

    [Fact]
    public void Serialization_TimeZoneId_RoundTrips()
    {
        var original = new AppSettings { TimeZoneId = "America/New_York" };

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<AppSettings>(json)!;

        Assert.Equal("America/New_York", deserialized.TimeZoneId);
    }
}
