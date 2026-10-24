using System.Text.Json;
using Lineup.Web.Services;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>
/// Verifies application settings defaults, normalization, and serialization.
/// </summary>
public class LineupSettingsTests
{
    /// <summary>
    /// Verifies that target days cannot be less than one.
    /// </summary>
    [Fact]
    public void TargetDays_ClampedToMinimumOfOne()
    {
        // Arrange
        // Act
        var settings = new LineupSettings { TargetDays = 0 };
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
        var settings = new LineupSettings { TargetDays = 7 };
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
        var settings = new LineupSettings();
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
        var settings = new LineupSettings { AutoFetchInterval = TimeSpan.FromHours(2) };
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
        var settings = new LineupSettings { AutoFetchInterval = TimeSpan.Zero };
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
        var settings = new LineupSettings { AutoFetchInterval = TimeSpan.FromMinutes(30) };
        // Assert
        Assert.True(settings.IsAutoFetchEnabled);
    }

    /// <summary>
    /// Verifies that channel refresh before guide fetching is enabled by default.
    /// </summary>
    [Fact]
    public void RefreshChannelsBeforeGuideFetch_DefaultsToTrue()
    {
        // Arrange
        // Act
        var settings = new LineupSettings();

        // Assert
        Assert.True(settings.RefreshChannelsBeforeGuideFetch);
    }

    /// <summary>
    /// Verifies disabled direct streams return errors by default.
    /// </summary>
    [Fact]
    public void DisabledChannelMode_DefaultsToReturnError()
    {
        // Arrange
        // Act
        var settings = new LineupSettings();

        // Assert
        Assert.Equal(DisabledChannelMode.ReturnError, settings.DisabledChannelMode);
    }

    /// <summary>
    /// Verifies that first-run setup is incomplete by default.
    /// </summary>
    [Fact]
    public void IsSetupComplete_DefaultsToFalse()
    {
        // Arrange
        // Act
        var settings = new LineupSettings();
        // Assert
        Assert.False(settings.IsSetupComplete);
    }

    /// <summary>
    /// Verifies that device refresh intervals cannot be negative.
    /// </summary>
    [Fact]
    public void DeviceRefreshIntervalMinutes_ClampedToZero()
    {
        // Arrange
        // Act
        var settings = new LineupSettings { DeviceRefreshIntervalMinutes = -10 };
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
        var settings = new LineupSettings { TunerRefreshIntervalSeconds = -1 };
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
        var original = new LineupSettings { AutoFetchInterval = new TimeSpan(1, 2, 30, 0) };

        var json = JsonSerializer.Serialize(original);
        // Act
        var deserialized = JsonSerializer.Deserialize<LineupSettings>(json)!;

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
        var settings = JsonSerializer.Deserialize<LineupSettings>(json)!;

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
        var original = new WebSettings { IsSetupComplete = true };

        var json = JsonSerializer.Serialize(original);
        // Act
        var deserialized = JsonSerializer.Deserialize<WebSettings>(json)!;

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
        var original = new LineupSettings { TimeZoneId = "America/New_York" };

        var json = JsonSerializer.Serialize(original);
        // Act
        var deserialized = JsonSerializer.Deserialize<LineupSettings>(json)!;

        // Assert
        Assert.Equal("America/New_York", deserialized.TimeZoneId);
    }

    /// <summary>
    /// Verifies the topology-owned transcode defaults.
    /// </summary>
    [Fact]
    public void TranscodeSettings_DefaultToCopyWithAc3ForAc4()
    {

        // Arrange
        // Act
        var settings = new LineupSettings();

        // Assert
        Assert.Equal(OutputAudioPolicy.Copy, settings.Streaming.VirtualDefaults.Audio);
        Assert.Equal(OutputAudioPolicy.Ac3, settings.Streaming.VirtualDefaults.Ac4Audio);
        Assert.Equal(SourceDeinterlacePolicy.Preserve, settings.Streaming.PhysicalDefaults.Deinterlace);
        Assert.Equal(SourceDeinterlacePolicy.Preserve, settings.Streaming.VirtualDefaults.Deinterlace);
        Assert.Equal(ProtectedContentMode.ReturnError, settings.ProtectedContentMode);
        Assert.Equal(VideoEncoderPreset.VeryFast, settings.Streaming.VirtualDefaults.VideoPreset);
        Assert.Equal(21, settings.Streaming.VirtualDefaults.VideoQuality);
        Assert.Equal(10, settings.Streaming.VirtualDefaults.MaximumVideoBitRateMbps);
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
        var settings = JsonSerializer.Deserialize<LineupSettings>(json)!;

        // Assert
        Assert.Equal(OutputAudioPolicy.Copy, settings.Streaming.VirtualDefaults.Audio);
        Assert.Equal(OutputAudioPolicy.Ac3, settings.Streaming.VirtualDefaults.Ac4Audio);
        Assert.Equal(SourceDeinterlacePolicy.Preserve, settings.Streaming.PhysicalDefaults.Deinterlace);
        Assert.Equal(SourceDeinterlacePolicy.Preserve, settings.Streaming.VirtualDefaults.Deinterlace);
        Assert.Equal(ProtectedContentMode.ReturnError, settings.ProtectedContentMode);
        Assert.Equal(VideoEncoderPreset.VeryFast, settings.Streaming.VirtualDefaults.VideoPreset);
        Assert.Equal(21, settings.Streaming.VirtualDefaults.VideoQuality);
        Assert.Equal(10, settings.Streaming.VirtualDefaults.MaximumVideoBitRateMbps);
        Assert.Equal(0, settings.MaximumConcurrentStreams);
    }

    /// <summary>
    /// Verifies that topology-owned transcode settings survive serialization.
    /// </summary>
    [Fact]
    public void Serialization_TranscodeSettings_RoundTrip()
    {
        // Arrange
        var original = new LineupSettings
        {
            ProtectedContentMode = ProtectedContentMode.StreamSlate,
            MaximumConcurrentStreams = 6,
            Streaming = new StreamingConfiguration
            {
                PhysicalDefaults = new PhysicalTranscodeProfile { Deinterlace = SourceDeinterlacePolicy.SourceFrameRate },
                VirtualDefaults = new VirtualTranscodeProfile
                {
                    Audio = OutputAudioPolicy.Eac3,
                    Ac4Audio = OutputAudioPolicy.Eac3,
                    Deinterlace = SourceDeinterlacePolicy.SourceFieldRate,
                    VideoPreset = VideoEncoderPreset.Faster,
                    VideoQuality = 19,
                    MaximumVideoBitRateMbps = 14
                }
            }
        };

        var json = JsonSerializer.Serialize(original);
        // Act
        var deserialized = JsonSerializer.Deserialize<LineupSettings>(json)!;

        // Assert
        Assert.Contains("\"ProtectedContentMode\":\"StreamSlate\"", json);
        Assert.Equal(original.Streaming.VirtualDefaults.Audio, deserialized.Streaming.VirtualDefaults.Audio);
        Assert.Equal(original.Streaming.VirtualDefaults.Ac4Audio, deserialized.Streaming.VirtualDefaults.Ac4Audio);
        Assert.Equal(original.Streaming.PhysicalDefaults.Deinterlace, deserialized.Streaming.PhysicalDefaults.Deinterlace);
        Assert.Equal(original.Streaming.VirtualDefaults.Deinterlace, deserialized.Streaming.VirtualDefaults.Deinterlace);
        Assert.Equal(original.ProtectedContentMode, deserialized.ProtectedContentMode);
        Assert.Equal(original.Streaming.VirtualDefaults.VideoPreset, deserialized.Streaming.VirtualDefaults.VideoPreset);
        Assert.Equal(original.Streaming.VirtualDefaults.VideoQuality, deserialized.Streaming.VirtualDefaults.VideoQuality);
        Assert.Equal(original.Streaming.VirtualDefaults.MaximumVideoBitRateMbps, deserialized.Streaming.VirtualDefaults.MaximumVideoBitRateMbps);
        Assert.Equal(original.MaximumConcurrentStreams, deserialized.MaximumConcurrentStreams);
    }

    /// <summary>
    /// Verifies that browser video quality and bitrate settings remain in supported ranges.
    /// </summary>
    [Fact]
    public void MaximumConcurrentStreams_ClampsToSupportedRange()
    {
        // Arrange
        var settings = new LineupSettings();

        // Act
        settings.MaximumConcurrentStreams = 101;

        // Assert
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
        var settings = new LineupSettings();

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
        var settings = new LineupSettings();

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
        var settings = JsonSerializer.Deserialize<WebSettings>(json)!;

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
        var settings = JsonSerializer.Deserialize<LineupSettings>(json)!;

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
        var original = new WebSettings
        {
            RedactApiClientAddresses = false,
            RedactApiDeviceAddresses = true,
            RedactApiUrls = true
        };

        var json = JsonSerializer.Serialize(original);
        // Act
        var settings = JsonSerializer.Deserialize<WebSettings>(json)!;

        // Assert
        Assert.False(settings.RedactApiClientAddresses);
        Assert.True(settings.RedactApiDeviceAddresses);
        Assert.True(settings.RedactApiUrls);
    }
}
