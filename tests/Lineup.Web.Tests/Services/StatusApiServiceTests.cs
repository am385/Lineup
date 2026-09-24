using System.Text.Json;
using Lineup.Core;
using Lineup.Core.Storage;
using Lineup.HDHomeRun.Device.Models;
using Lineup.HDHomeRun.Device.Protocol;
using Lineup.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>
/// Verifies stable status API aggregation and privacy behavior.
/// </summary>
public class StatusApiServiceTests
{
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Verifies the default privacy policy and representative state mapping.
    /// </summary>
    [Fact]
    public async Task GetStatusAsync_DefaultPrivacy_RedactsClientsAndNeverExposesSecrets()
    {
        // Arrange
        var fixture = CreateFixture();
        fixture.Settings.RedactApiClientAddresses = true;
        fixture.Settings.RedactApiDeviceAddresses = false;
        fixture.Settings.RedactApiUrls = false;

        // Act
        var result = await fixture.Service.GetStatusAsync(TestContext.Current.CancellationToken);
        var json = JsonSerializer.Serialize(result, WebJsonOptions);

        // Assert
        Assert.Equal("1.0", result.SchemaVersion);
        Assert.Equal("available", result.Guide.Status);
        Assert.Equal(7, result.Guide.ChannelCount);
        Assert.Equal("tuner.local", result.PhysicalDevice.ConfiguredAddress);
        Assert.Equal("http://tuner.local", result.PhysicalDevice.BaseUrl);
        Assert.Equal("http://client.local:5000", result.Tuners[0].TargetUrl);
        Assert.Null(result.Streams.Items[0].ClientAddress);
        Assert.Contains("\"clientAddress\":null", json, StringComparison.Ordinal);
        Assert.DoesNotContain("physical-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("DeviceAuth", json, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Verifies each privacy category independently nulls stable response properties.
    /// </summary>
    [Fact]
    public async Task GetStatusAsync_AllPrivacyEnabled_ReturnsNullNetworkProperties()
    {
        // Arrange
        var fixture = CreateFixture();
        fixture.Settings.RedactApiClientAddresses = true;
        fixture.Settings.RedactApiDeviceAddresses = true;
        fixture.Settings.RedactApiUrls = true;

        // Act
        var result = await fixture.Service.GetStatusAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(result.PhysicalDevice.ConfiguredAddress);
        Assert.Null(result.PhysicalDevice.BaseUrl);
        Assert.Null(result.PhysicalDevice.LineupUrl);
        Assert.Null(result.VirtualDevices[0].PhysicalAddress);
        Assert.Null(result.VirtualDevices[0].AdvertisedUrl);
        Assert.Null(result.Tuners[0].TargetUrl);
        Assert.Null(result.Streams.Items[0].ClientAddress);
        Assert.Null(result.Guide.Xmltv.DownloadUrl);
    }

    /// <summary>
    /// Verifies guide repository failures degrade only the guide section.
    /// </summary>
    [Fact]
    public async Task GetStatusAsync_GuideFailure_ReturnsUsableDegradedResponse()
    {
        // Arrange
        var fixture = CreateFixture();
        fixture.Repository.GetCacheStatisticsAsync().Returns<Task<CacheStatistics>>(_ => throw new IOException("database unavailable"));

        // Act
        var result = await fixture.Service.GetStatusAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("unavailable", result.Guide.Status);
        Assert.Equal("Guide state is unavailable.", result.Guide.Error);
        Assert.Null(result.Guide.ChannelCount);
        Assert.Equal("available", result.PhysicalDevice.Status);
        Assert.Single(result.Tuners);
        Assert.Equal(1, result.Streams.Count);
    }

    private static StatusApiFixture CreateFixture()
    {
        var xmltvPath = Path.Combine(Path.GetTempPath(), $"lineup-status-{Guid.NewGuid():N}.xml");
        var profile = new HdHomeRunProxyProfileSettings
        {
            PhysicalAddress = "tuner.local",
            VirtualDeviceId = HdHomeRunProxyIdentity.CreateDeviceId("status-test"),
            AdvertisedBaseUrl = "http://lineup.local:8080/"
        };
        var settings = new AppSettings
        {
            IsSetupComplete = true,
            DeviceAddress = "tuner.local",
            XmltvOutputPath = xmltvPath,
            EnableHdHomeRunProxy = true,
            HdHomeRunProxyProfiles = [profile]
        };
        var settingsService = Substitute.For<IAppSettingsService>();
        settingsService.Settings.Returns(settings);
        var repository = Substitute.For<IEpgRepository>();
        repository.EnsureDatabaseCreatedAsync().Returns(Task.CompletedTask);
        repository.GetCacheStatisticsAsync().Returns(new CacheStatistics(
            7,
            123,
            new DateTime(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 18, 0, 0, 0, DateTimeKind.Utc),
            TimeSpan.FromDays(2)));
        repository.GetSafeFetchStartTimeAsync().Returns(new DateTime(2026, 9, 17, 23, 0, 0, DateTimeKind.Utc));
        var device = CreatePhysicalDevice();
        var deviceState = Substitute.For<IDeviceStateService>();
        deviceState.DeviceInfo.Returns(device);
        deviceState.IsDiscovered.Returns(true);
        deviceState.TunerStatuses.Returns(
        [
            new TunerStatus
            {
                TunerIndex = 0,
                Channel = "atsc3:587000000",
                VirtualChannel = "5.1",
                Target = "http://client.local:5000",
                LockType = "atsc3",
                SignalStrength = 90,
                SignalToNoiseQuality = 80,
                SymbolErrorQuality = 100,
                BitsPerSecond = 18_000_000,
                PacketsPerSecond = 1000
            }
        ]);
        var autoFetch = Substitute.For<IAutoFetchStateService>();
        autoFetch.IsEnabled.Returns(true);
        autoFetch.IsRunning.Returns(true);
        autoFetch.CurrentProgress.Returns(new FetchProgressInfo
        {
            Status = FetchStatus.Fetching,
            Message = "Fetching guide",
            FetchCount = 2,
            TotalProgramsFetched = 50,
            TotalChannelsFetched = 7
        });
        var activeStreams = Substitute.For<IActiveStreamRegistry>();
        activeStreams.GetActiveStreams().Returns(
        [
            new ActiveStreamSnapshot(
                "stream-1",
                "5.1",
                HostedStreamFormat.FragmentedMp4,
                DateTime.UtcNow.AddMinutes(-2),
                18_000_000,
                [new ActiveStreamTrack(MediaTrackType.Audio, "ac4", "aac", 256_000, 384_000, null, null, 6, 6, 48_000)])
            {
                ClientId = "watch-1",
                ClientAddress = "192.0.2.20:54321"
            }
        ]);
        var virtualDeviceCache = new VirtualDeviceStatusCache();
        virtualDeviceCache.RecordSuccess(HdHomeRunProxyProfileResolver.CreateSnapshot(profile, device, true));
        var service = new StatusApiService(
            repository,
            settingsService,
            deviceState,
            autoFetch,
            activeStreams,
            virtualDeviceCache,
            new StatusApiRuntime("runtime-test"),
            NullLogger<StatusApiService>.Instance,
            new XmltvPublicationStore());
        return new StatusApiFixture(settings, repository, service);
    }

    private static HDHomeRunDeviceInfo CreatePhysicalDevice() => new()
    {
        FriendlyName = "HDHomeRun FLEX",
        ModelNumber = "HDFX-4K",
        FirmwareName = "hdhomerun_atsc3",
        FirmwareVersion = "20260901",
        DeviceID = "12345678",
        DeviceAuth = "physical-secret",
        BaseURL = "http://tuner.local",
        LineupURL = "http://tuner.local/lineup.json",
        TunerCount = 4
    };

    private sealed record StatusApiFixture(AppSettings Settings, IEpgRepository Repository, StatusApiService Service);
}
