using System.Text.Json;
using Lineup.Web.Services;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>
/// Verifies application settings defaults, normalization, and serialization.
/// </summary>
public class AppSettingsTests
{
    /// <summary>
    /// Verifies that target days cannot be less than one.
    /// </summary>
    [Fact]
    public void TargetDays_ClampedToMinimumOfOne()
    {
        // Arrange
        // Act
        var settings = new AppSettings { TargetDays = 0 };
        // Assert
        Assert.Equal(1, settings.TargetDays);

        settings.TargetDays = -5;
        Assert.Equal(1, settings.TargetDays);
    }

    /// <summary>
    /// Verifies that valid target-day values are preserved.
    /// </summary>
    [Fact]
    public void TargetDays_AcceptsValidValues()
    {
        // Arrange
        // Act
        var settings = new AppSettings { TargetDays = 7 };
        // Assert
        Assert.Equal(7, settings.TargetDays);
    }

    /// <summary>
    /// Verifies that automatic fetching is disabled by default.
    /// </summary>
    [Fact]
    public void AutoFetchInterval_DefaultsToZero()
    {
        // Arrange
        // Act
        var settings = new AppSettings();
        // Assert
        Assert.Equal(TimeSpan.Zero, settings.AutoFetchInterval);
    }

    /// <summary>
    /// Verifies that automatic fetch intervals accept time spans.
    /// </summary>
    [Fact]
    public void AutoFetchInterval_AcceptsTimeSpan()
    {
        // Arrange
        // Act
        var settings = new AppSettings { AutoFetchInterval = TimeSpan.FromHours(2) };
        // Assert
        Assert.Equal(TimeSpan.FromHours(2), settings.AutoFetchInterval);
    }

    /// <summary>
    /// Verifies that a zero fetch interval disables automatic fetching.
    /// </summary>
    [Fact]
    public void IsAutoFetchEnabled_FalseWhenZero()
    {
        // Arrange
        // Act
        var settings = new AppSettings { AutoFetchInterval = TimeSpan.Zero };
        // Assert
        Assert.False(settings.IsAutoFetchEnabled);
    }

    /// <summary>
    /// Verifies that a positive fetch interval enables automatic fetching.
    /// </summary>
    [Fact]
    public void IsAutoFetchEnabled_TrueWhenPositive()
    {
        // Arrange
        // Act
        var settings = new AppSettings { AutoFetchInterval = TimeSpan.FromMinutes(30) };
        // Assert
        Assert.True(settings.IsAutoFetchEnabled);
    }

    /// <summary>
    /// Verifies that first-run setup is incomplete by default.
    /// </summary>
    [Fact]
    public void IsSetupComplete_DefaultsToFalse()
    {
        // Arrange
        // Act
        var settings = new AppSettings();
        // Assert
        Assert.False(settings.IsSetupComplete);
    }

    /// <summary>
    /// Verifies that the HDHomeRun proxy is disabled by default.
    /// </summary>
    [Fact]
    public void EnableHdHomeRunProxy_DefaultsToFalse()
    {
        // Arrange
        // Act
        var settings = new AppSettings();
        // Assert
        Assert.False(settings.EnableHdHomeRunProxy);
    }

    /// <summary>
    /// Verifies that device refresh intervals cannot be negative.
    /// </summary>
    [Fact]
    public void DeviceRefreshIntervalMinutes_ClampedToZero()
    {
        // Arrange
        // Act
        var settings = new AppSettings { DeviceRefreshIntervalMinutes = -10 };
        // Assert
        Assert.Equal(0, settings.DeviceRefreshIntervalMinutes);
    }

    /// <summary>
    /// Verifies that tuner refresh intervals cannot be negative.
    /// </summary>
    [Fact]
    public void TunerRefreshIntervalSeconds_ClampedToZero()
    {
        // Arrange
        // Act
        var settings = new AppSettings { TunerRefreshIntervalSeconds = -1 };
        // Assert
        Assert.Equal(0, settings.TunerRefreshIntervalSeconds);
    }

    /// <summary>
    /// Verifies that automatic fetch intervals survive JSON serialization.
    /// </summary>
    [Fact]
    public void Serialization_AutoFetchInterval_RoundTrips()
    {
        // Arrange
        var original = new AppSettings { AutoFetchInterval = new TimeSpan(1, 2, 30, 0) };

        var json = JsonSerializer.Serialize(original);
        // Act
        var deserialized = JsonSerializer.Deserialize<AppSettings>(json)!;

        // Assert
        Assert.Equal(original.AutoFetchInterval, deserialized.AutoFetchInterval);
    }

    /// <summary>
    /// Verifies that current JSON uses the automatic fetch interval.
    /// </summary>
    [Fact]
    public void Deserialization_NewJson_UsesAutoFetchInterval()
    {
        // Arrange
        var json = """{"AutoFetchInterval": "02:00:00"}""";

        // Act
        var settings = JsonSerializer.Deserialize<AppSettings>(json)!;

        // Assert
        Assert.Equal(TimeSpan.FromHours(2), settings.AutoFetchInterval);
    }

    /// <summary>
    /// Verifies that setup completion survives JSON serialization.
    /// </summary>
    [Fact]
    public void Serialization_IsSetupComplete_RoundTrips()
    {
        // Arrange
        var original = new AppSettings { IsSetupComplete = true };

        var json = JsonSerializer.Serialize(original);
        // Act
        var deserialized = JsonSerializer.Deserialize<AppSettings>(json)!;

        // Assert
        Assert.True(deserialized.IsSetupComplete);
    }

    /// <summary>
    /// Verifies that the selected timezone survives JSON serialization.
    /// </summary>
    [Fact]
    public void Serialization_TimeZoneId_RoundTrips()
    {
        // Arrange
        var original = new AppSettings { TimeZoneId = "America/New_York" };

        var json = JsonSerializer.Serialize(original);
        // Act
        var deserialized = JsonSerializer.Deserialize<AppSettings>(json)!;

        // Assert
        Assert.Equal("America/New_York", deserialized.TimeZoneId);
    }

    /// <summary>
    /// Verifies the compatibility-oriented audio transcode defaults.
    /// </summary>
    [Fact]
    public void TranscodeSettings_DefaultToCopyWithAc3ForAc4()
    {

        // Arrange
        // Act
        var settings = new AppSettings();

        // Assert
        Assert.Equal(AudioTranscodeMode.Copy, settings.AudioTranscodeMode);
        Assert.Equal(Ac4TranscodeTarget.Ac3, settings.Ac4TranscodeTarget);
        Assert.Equal(ProtectedContentMode.ReturnError, settings.ProtectedContentMode);
        Assert.Equal(WebVideoPreset.VeryFast, settings.WebVideoPreset);
        Assert.Equal(21, settings.WebVideoQuality);
        Assert.Equal(10, settings.MaximumVideoBitRateMbps);
        Assert.Equal(0, settings.MaximumConcurrentStreams);
    }

    /// <summary>
    /// Verifies that settings files created before audio settings use current defaults.
    /// </summary>
    [Fact]
    public void Deserialization_LegacyJson_UsesTranscodeDefaults()
    {
        // Arrange
        const string json = """{"TargetDays": 3}""";

        // Act
        var settings = JsonSerializer.Deserialize<AppSettings>(json)!;

        // Assert
        Assert.Equal(AudioTranscodeMode.Copy, settings.AudioTranscodeMode);
        Assert.Equal(Ac4TranscodeTarget.Ac3, settings.Ac4TranscodeTarget);
        Assert.Equal(ProtectedContentMode.ReturnError, settings.ProtectedContentMode);
        Assert.Equal(WebVideoPreset.VeryFast, settings.WebVideoPreset);
        Assert.Equal(21, settings.WebVideoQuality);
        Assert.Equal(10, settings.MaximumVideoBitRateMbps);
        Assert.Equal(0, settings.MaximumConcurrentStreams);
    }

    /// <summary>
    /// Verifies that audio transcode settings serialize by stable enum names.
    /// </summary>
    [Fact]
    public void Serialization_TranscodeSettings_RoundTripAsNames()
    {
        // Arrange
        var original = new AppSettings
        {
            AudioTranscodeMode = AudioTranscodeMode.Eac3,
            Ac4TranscodeTarget = Ac4TranscodeTarget.Eac3,
            ProtectedContentMode = ProtectedContentMode.StreamSlate,
            WebVideoPreset = WebVideoPreset.Faster,
            WebVideoQuality = 19,
            MaximumVideoBitRateMbps = 14,
            MaximumConcurrentStreams = 6
        };

        var json = JsonSerializer.Serialize(original);
        // Act
        var deserialized = JsonSerializer.Deserialize<AppSettings>(json)!;

        // Assert
        Assert.Contains("\"AudioTranscodeMode\":\"Eac3\"", json);
        Assert.Contains("\"Ac4TranscodeTarget\":\"Eac3\"", json);
        Assert.Contains("\"ProtectedContentMode\":\"StreamSlate\"", json);
        Assert.Contains("\"WebVideoPreset\":\"Faster\"", json);
        Assert.Equal(original.AudioTranscodeMode, deserialized.AudioTranscodeMode);
        Assert.Equal(original.Ac4TranscodeTarget, deserialized.Ac4TranscodeTarget);
        Assert.Equal(original.ProtectedContentMode, deserialized.ProtectedContentMode);
        Assert.Equal(original.WebVideoPreset, deserialized.WebVideoPreset);
        Assert.Equal(original.WebVideoQuality, deserialized.WebVideoQuality);
        Assert.Equal(original.MaximumVideoBitRateMbps, deserialized.MaximumVideoBitRateMbps);
        Assert.Equal(original.MaximumConcurrentStreams, deserialized.MaximumConcurrentStreams);
    }

    /// <summary>
    /// Verifies that browser video quality and bitrate settings remain in supported ranges.
    /// </summary>
    [Fact]
    public void WebVideoSettings_ClampToSupportedRanges()
    {
        // Arrange
        var settings = new AppSettings();

        settings.WebVideoQuality = 5;
        settings.MaximumVideoBitRateMbps = 100;
        // Act
        settings.MaximumConcurrentStreams = 101;

        // Assert
        Assert.Equal(16, settings.WebVideoQuality);
        Assert.Equal(30, settings.MaximumVideoBitRateMbps);
        Assert.Equal(100, settings.MaximumConcurrentStreams);
    }

    /// <summary>
    /// Verifies the default active stream refresh interval.
    /// </summary>
    [Fact]
    public void ActiveStreamRefreshInterval_DefaultsToFiveSeconds()
    {

        // Arrange
        // Act
        var settings = new AppSettings();

        // Assert
        Assert.Equal(5, settings.ActiveStreamRefreshIntervalSeconds);
        Assert.True(settings.IsActiveStreamRefreshEnabled);
    }

    /// <summary>
    /// Verifies active stream refresh interval bounds and disabled state.
    /// </summary>
    [Fact]
    public void ActiveStreamRefreshInterval_ClampsToSupportedRange()
    {
        // Arrange
        var settings = new AppSettings();

        settings.ActiveStreamRefreshIntervalSeconds = -1;
        var disabledInterval = settings.ActiveStreamRefreshIntervalSeconds;
        var disabled = settings.IsActiveStreamRefreshEnabled;
        // Act
        settings.ActiveStreamRefreshIntervalSeconds = 301;

        // Assert
        Assert.Equal(0, disabledInterval);
        Assert.False(disabled);
        Assert.Equal(300, settings.ActiveStreamRefreshIntervalSeconds);
    }

    /// <summary>
    /// Verifies that legacy settings files receive the active stream refresh default.
    /// </summary>
    [Fact]
    public void Deserialization_LegacyJson_UsesActiveStreamRefreshDefault()
    {
        // Arrange
        const string json = """{"TargetDays": 3}""";

        // Act
        var settings = JsonSerializer.Deserialize<AppSettings>(json)!;

        // Assert
        Assert.Equal(5, settings.ActiveStreamRefreshIntervalSeconds);
    }

    /// <summary>
    /// Verifies backward-compatible status API privacy defaults.
    /// </summary>
    [Fact]
    public void Deserialization_LegacyJson_UsesStatusApiPrivacyDefaults()
    {
        // Arrange
        const string json = """{"TargetDays": 3}""";

        // Act
        var settings = JsonSerializer.Deserialize<AppSettings>(json)!;

        // Assert
        Assert.True(settings.RedactApiClientAddresses);
        Assert.False(settings.RedactApiDeviceAddresses);
        Assert.False(settings.RedactApiUrls);
    }

    /// <summary>
    /// Verifies status API privacy settings survive serialization.
    /// </summary>
    [Fact]
    public void Serialization_StatusApiPrivacy_RoundTrips()
    {
        // Arrange
        var original = new AppSettings
        {
            RedactApiClientAddresses = false,
            RedactApiDeviceAddresses = true,
            RedactApiUrls = true
        };

        var json = JsonSerializer.Serialize(original);
        // Act
        var settings = JsonSerializer.Deserialize<AppSettings>(json)!;

        // Assert
        Assert.False(settings.RedactApiClientAddresses);
        Assert.True(settings.RedactApiDeviceAddresses);
        Assert.True(settings.RedactApiUrls);
    }
}
