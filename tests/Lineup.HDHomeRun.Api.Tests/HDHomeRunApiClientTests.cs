using Lineup.HDHomeRun.Api.Models;
using Microsoft.Extensions.Logging;
using NSubstitute;
using RichardSzalay.MockHttp;
using System.Net;
using System.Text.Json;
using Xunit;

namespace Lineup.HDHomeRun.Api.Tests;

public class HDHomeRunApiClientTests
{
    private readonly ILogger<HDHomeRunApiClient> _logger;
    private readonly IDeviceAuthProvider _deviceAuthProvider;
    private readonly MockHttpMessageHandler _mockHttp;
    private readonly HttpClient _httpClient;

    public HDHomeRunApiClientTests()
    {
        _logger = Substitute.For<ILogger<HDHomeRunApiClient>>();
        _deviceAuthProvider = Substitute.For<IDeviceAuthProvider>();
        _mockHttp = new MockHttpMessageHandler();
        _httpClient = _mockHttp.ToHttpClient();

        _deviceAuthProvider.GetDeviceAuthAsync().Returns("test-device-auth");
    }

    [Fact]
    public async Task FetchRawEpgSegmentAsync_ReturnsSegments_WhenApiReturnsValidResponse()
    {
        // Arrange
        var expectedSegments = CreateTestEpgSegments();
        _mockHttp.When("https://api.hdhomerun.com/*")
            .Respond("application/json", JsonSerializer.Serialize(expectedSegments));

        var client = new HDHomeRunApiClient(_logger, _httpClient, _deviceAuthProvider);
        var startTime = DateTime.UtcNow;

        // Act
        var result = await client.FetchRawEpgSegmentAsync(startTime);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Equal("2.1", result[0].GuideNumber);
        Assert.Equal("5.1", result[1].GuideNumber);
    }

    [Fact]
    public async Task FetchRawEpgSegmentAsync_ReturnsEmptyList_WhenApiReturnsEmptyArray()
    {
        // Arrange
        _mockHttp.When("https://api.hdhomerun.com/*")
            .Respond("application/json", "[]");

        var client = new HDHomeRunApiClient(_logger, _httpClient, _deviceAuthProvider);

        // Act
        var result = await client.FetchRawEpgSegmentAsync(DateTime.UtcNow);

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task FetchRawEpgSegmentAsync_ThrowsException_WhenApiReturnsInvalidJson()
    {
        // Arrange
        _mockHttp.When("https://api.hdhomerun.com/*")
            .Respond("application/json", "not valid json");

        var client = new HDHomeRunApiClient(_logger, _httpClient, _deviceAuthProvider);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => 
            client.FetchRawEpgSegmentAsync(DateTime.UtcNow));
    }

    [Fact]
    public async Task FetchRawEpgSegmentAsync_UsesCorrectDeviceAuth()
    {
        // Arrange
        _mockHttp.When("https://api.hdhomerun.com/*")
            .Respond("application/json", "[]");
        
        var client = new HDHomeRunApiClient(_logger, _httpClient, _deviceAuthProvider);

        // Act
        await client.FetchRawEpgSegmentAsync(DateTime.UtcNow);

        // Assert
        await _deviceAuthProvider.Received(1).GetDeviceAuthAsync();
    }

    [Fact]
    public async Task FetchRawEpgSegmentAsync_IncludesStartTimeInRequest()
    {
        // Arrange
        string? capturedUrl = null;
        _mockHttp.When("https://api.hdhomerun.com/*")
            .With(req => { capturedUrl = req.RequestUri?.ToString(); return true; })
            .Respond("application/json", "[]");

        var client = new HDHomeRunApiClient(_logger, _httpClient, _deviceAuthProvider);
        var startTime = new DateTime(2024, 1, 15, 12, 0, 0, DateTimeKind.Utc);

        // Act
        await client.FetchRawEpgSegmentAsync(startTime);

        // Assert
        Assert.NotNull(capturedUrl);
        Assert.Contains("Start=", capturedUrl);
        Assert.Contains("DeviceAuth=test-device-auth", capturedUrl);
    }

    [Fact]
    public async Task FetchRawEpgDataAsync_MergesMultipleSegments()
    {
        // Arrange
        var segment1 = new List<HDHomeRunChannelEpgSegment>
        {
            new()
            {
                GuideNumber = "2.1",
                GuideName = "Channel 2",
                Guide =
                [
                    new HDHomeRunProgram { Title = "Show 1", StartTime = 1000, EndTime = 2000 }
                ]
            }
        };

        var segment2 = new List<HDHomeRunChannelEpgSegment>
        {
            new()
            {
                GuideNumber = "2.1",
                GuideName = "Channel 2",
                Guide =
                [
                    new HDHomeRunProgram { Title = "Show 2", StartTime = 2000, EndTime = 3000 }
                ]
            }
        };

        var callCount = 0;
        _mockHttp.When("https://api.hdhomerun.com/*")
            .Respond(_ =>
            {
                var content = callCount++ == 0 
                    ? JsonSerializer.Serialize(segment1) 
                    : JsonSerializer.Serialize(segment2);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(content, System.Text.Encoding.UTF8, "application/json")
                };
            });

        var client = new HDHomeRunApiClient(_logger, _httpClient, _deviceAuthProvider);

        // Act - fetch 1 day with 24 hour intervals (should make 1 request, but let's test with smaller)
        var result = await client.FetchRawEpgDataAsync(days: 1, hours: 12);

        // Assert
        Assert.Single(result); // One channel
        Assert.Equal(2, result[0].Guide.Count); // Two programs merged
    }

    [Fact]
    public async Task FetchRawEpgDataAsync_AvoidsDuplicatePrograms()
    {
        // Arrange
        var duplicateProgram = new HDHomeRunProgram { Title = "Same Show", StartTime = 1000, EndTime = 2000 };
        
        var segment = new List<HDHomeRunChannelEpgSegment>
        {
            new()
            {
                GuideNumber = "2.1",
                GuideName = "Channel 2",
                Guide = [duplicateProgram]
            }
        };

        _mockHttp.When("https://api.hdhomerun.com/*")
            .Respond("application/json", JsonSerializer.Serialize(segment));

        var client = new HDHomeRunApiClient(_logger, _httpClient, _deviceAuthProvider);

        // Act - fetch with overlapping windows
        var result = await client.FetchRawEpgDataAsync(days: 1, hours: 6);

        // Assert - should only have one program despite multiple fetches
        Assert.Single(result);
        Assert.Single(result[0].Guide);
    }

    private static List<HDHomeRunChannelEpgSegment> CreateTestEpgSegments()
    {
        return
        [
            new HDHomeRunChannelEpgSegment
            {
                GuideNumber = "2.1",
                GuideName = "WFMY-HD",
                Affiliate = "CBS",
                ImageURL = "http://example.com/logo1.png",
                Guide =
                [
                    new HDHomeRunProgram
                    {
                        Title = "Morning News",
                        EpisodeTitle = "Episode 1",
                        StartTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        EndTime = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
                    }
                ]
            },
            new HDHomeRunChannelEpgSegment
            {
                GuideNumber = "5.1",
                GuideName = "WRAL-HD",
                Affiliate = "NBC",
                ImageURL = "http://example.com/logo2.png",
                Guide =
                [
                    new HDHomeRunProgram
                    {
                        Title = "Evening News",
                        StartTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        EndTime = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
                    }
                ]
            }
        ];
    }
}
