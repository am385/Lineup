using Lineup.HDHomeRun.Device.Models;
using Microsoft.Extensions.Logging;
using NSubstitute;
using RichardSzalay.MockHttp;
using System.Text.Json;
using Xunit;

namespace Lineup.HDHomeRun.Device.Tests;

public class HDHomeRunDeviceClientTests
{
    private readonly ILogger<HDHomeRunDeviceClient> _logger;
    private readonly MockHttpMessageHandler _mockHttp;
    private readonly HttpClient _httpClient;

    public HDHomeRunDeviceClientTests()
    {
        _logger = Substitute.For<ILogger<HDHomeRunDeviceClient>>();
        _mockHttp = new MockHttpMessageHandler();
        _httpClient = _mockHttp.ToHttpClient();
        _httpClient.BaseAddress = new Uri("http://hdhomerun.local/");
    }

    [Fact]
    public async Task DiscoverDeviceAsync_ReturnsDeviceInfo_WhenApiReturnsValidResponse()
    {
        // Arrange
        var expectedDeviceInfo = new HDHomeRunDeviceInfo
        {
            FriendlyName = "HDHomeRun FLEX 4K",
            ModelNumber = "HDFX-4K",
            FirmwareName = "hdhomerun_dvr",
            FirmwareVersion = "20231015",
            DeviceID = "12345678",
            DeviceAuth = "test-device-auth-token",
            BaseURL = "http://10.0.0.10",
            LineupURL = "http://10.0.0.10/lineup.json",
            TunerCount = 4
        };

        _mockHttp.When("http://hdhomerun.local/discover.json")
            .Respond("application/json", JsonSerializer.Serialize(expectedDeviceInfo));

        var client = new HDHomeRunDeviceClient(_logger, _httpClient);

        // Act
        var result = await client.DiscoverDeviceAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Equal(expectedDeviceInfo.FriendlyName, result.FriendlyName);
        Assert.Equal(expectedDeviceInfo.DeviceAuth, result.DeviceAuth);
    }

    [Fact]
    public async Task DiscoverDeviceAsync_ThrowsException_WhenApiReturnsInvalidJson()
    {
        // Arrange
        _mockHttp.When("http://hdhomerun.local/discover.json")
            .Respond("application/json", "invalid json content");

        var client = new HDHomeRunDeviceClient(_logger, _httpClient);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.DiscoverDeviceAsync());
    }

    [Fact]
    public async Task FetchChannelLineupAsync_ReturnsChannels_WhenApiReturnsValidResponse()
    {
        // Arrange
        var expectedChannels = new List<HDHomeRunChannel>
        {
            new() { GuideNumber = "2.1", GuideName = "WFMY-HD", URL = "http://device/auto/v2.1" },
            new() { GuideNumber = "5.1", GuideName = "WRAL-HD", URL = "http://device/auto/v5.1" },
            new() { GuideNumber = "11.1", GuideName = "WTVD-HD", URL = "http://device/auto/v11.1" }
        };

        _mockHttp.When("http://hdhomerun.local/lineup.json")
            .Respond("application/json", JsonSerializer.Serialize(expectedChannels));

        var client = new HDHomeRunDeviceClient(_logger, _httpClient);

        // Act
        var result = await client.FetchChannelLineupAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Equal(3, result.Count);
        Assert.Equal("2.1", result[0].GuideNumber);
        Assert.Equal("WFMY-HD", result[0].GuideName);
    }

    [Fact]
    public async Task FetchChannelLineupAsync_ReturnsEmptyList_WhenApiReturnsEmptyArray()
    {
        // Arrange
        _mockHttp.When("http://hdhomerun.local/lineup.json")
            .Respond("application/json", "[]");

        var client = new HDHomeRunDeviceClient(_logger, _httpClient);

        // Act
        var result = await client.FetchChannelLineupAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task FetchChannelLineupAsync_ThrowsException_WhenApiReturnsInvalidJson()
    {
        // Arrange
        _mockHttp.When("http://hdhomerun.local/lineup.json")
            .Respond("application/json", "not valid json");

        var client = new HDHomeRunDeviceClient(_logger, _httpClient);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.FetchChannelLineupAsync());
    }

    [Fact]
    public async Task DiscoverDeviceAuthAsync_ReturnsDeviceAuth_WhenDeviceInfoIsValid()
    {
        // Arrange
        var deviceInfo = new HDHomeRunDeviceInfo
        {
            FriendlyName = "Test Device",
            ModelNumber = "TEST-1",
            FirmwareName = "test",
            FirmwareVersion = "1.0",
            DeviceID = "ABCD1234",
            DeviceAuth = "expected-auth-token",
            BaseURL = "http://10.0.0.1",
            LineupURL = "http://10.0.0.1/lineup.json",
            TunerCount = 2
        };

        _mockHttp.When("http://hdhomerun.local/discover.json")
            .Respond("application/json", JsonSerializer.Serialize(deviceInfo));

        var client = new HDHomeRunDeviceClient(_logger, _httpClient);

        // Act
        var result = await client.DiscoverDeviceAuthAsync();

        // Assert
        Assert.Equal("expected-auth-token", result);
    }
}
