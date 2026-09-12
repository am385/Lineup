using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using NSubstitute;
using RichardSzalay.MockHttp;
using Xunit;

namespace Lineup.HDHomeRun.Api.Tests;

/// <summary>
/// Verifies SiliconDust XMLTV API requests.
/// </summary>
public class HDHomeRunApiClientTests
{
    private readonly ILogger<HDHomeRunApiClient> _logger = Substitute.For<ILogger<HDHomeRunApiClient>>();
    private readonly IDeviceAuthProvider _deviceAuthProvider = Substitute.For<IDeviceAuthProvider>();
    private readonly MockHttpMessageHandler _mockHttp = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="HDHomeRunApiClientTests"/> class.
    /// </summary>
    public HDHomeRunApiClientTests()
    {
        _deviceAuthProvider.GetDeviceAuthAsync().Returns("test auth");
    }

    /// <summary>
    /// Verifies that the complete XMLTV document is returned unchanged.
    /// </summary>
    [Fact]
    public async Task FetchXmltvAsync_ReturnsCanonicalDocument()
    {
        // Arrange
        const string xml = """<?xml version="1.0"?><tv><channel id="station"><lcn>2.1</lcn></channel></tv>""";
        string? userAgent = null;
        _mockHttp.Expect("https://api.hdhomerun.com/api/xmltv?DeviceAuth=test%20auth")
            .WithHeaders("Accept-Encoding", "gzip")
            .With(request =>
            {
                userAgent = request.Headers.UserAgent.ToString();
                return true;
            })
            .Respond("application/xml", xml);
        var client = CreateClient();

        // Act
        var result = await client.FetchXmltvAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(xml, Encoding.UTF8.GetString(result));
        Assert.Equal("Lineup/2.0 (+https://github.com/am385/Lineup)", userAgent);
        await _deviceAuthProvider.Received(1).GetDeviceAuthAsync();
        _mockHttp.VerifyNoOutstandingExpectation();
    }

    /// <summary>
    /// Verifies that a transient forbidden response refreshes DeviceAuth and retries once.
    /// </summary>
    [Fact]
    public async Task FetchXmltvAsync_RetriesForbiddenResponseWithFreshDeviceAuth()
    {
        // Arrange
        _deviceAuthProvider.GetDeviceAuthAsync().Returns("stale", "fresh");
        _mockHttp.Expect("https://api.hdhomerun.com/api/xmltv?DeviceAuth=stale")
            .Respond(HttpStatusCode.Forbidden);
        _mockHttp.Expect("https://api.hdhomerun.com/api/xmltv?DeviceAuth=fresh")
            .Respond("application/xml", "<tv />");
        var client = CreateClient();

        // Act
        var result = await client.FetchXmltvAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("<tv />", Encoding.UTF8.GetString(result));
        await _deviceAuthProvider.Received(2).GetDeviceAuthAsync();
        _mockHttp.VerifyNoOutstandingExpectation();
    }

    /// <summary>
    /// Verifies that an empty successful response is rejected.
    /// </summary>
    [Fact]
    public async Task FetchXmltvAsync_Throws_WhenResponseIsEmpty()
    {
        // Arrange
        _mockHttp.Expect("https://api.hdhomerun.com/api/xmltv*")
            .Respond("application/xml", string.Empty);
        var client = CreateClient();

        // Act
        // Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.FetchXmltvAsync(TestContext.Current.CancellationToken));

        Assert.Contains("empty", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Verifies that HTTP failures remain visible to callers.
    /// </summary>
    [Fact]
    public async Task FetchXmltvAsync_Throws_WhenApiReturnsFailure()
    {
        // Arrange
        _mockHttp.Expect("https://api.hdhomerun.com/api/xmltv*")
            .Respond(HttpStatusCode.Unauthorized);
        var client = CreateClient();

        var action = () => client.FetchXmltvAsync(TestContext.Current.CancellationToken);

        // Act
        // Assert
        await Assert.ThrowsAsync<HttpRequestException>(action);
    }

    private HDHomeRunApiClient CreateClient()
    {
        return new HDHomeRunApiClient(_logger, _mockHttp.ToHttpClient(), _deviceAuthProvider);
    }
}
