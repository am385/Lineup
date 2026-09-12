using Lineup.Web.Services;
using NSubstitute;
using System.Net;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>
/// Verifies guide authentication across configured physical tuners.
/// </summary>
public class SettingsDeviceAuthProviderTests
{
    /// <summary>
    /// Verifies that current authentication values from all enabled unique devices are concatenated.
    /// </summary>
    [Fact]
    public async Task GetDeviceAuthAsync_ConcatenatesEnabledDeviceAuthValues()
    {
        // Arrange
        var settings = Substitute.For<IAppSettingsService>();
        settings.Settings.Returns(new AppSettings
        {
            DeviceAddress = "http://device-one",
            HdHomeRunProxyProfiles =
            [
                new HdHomeRunProxyProfileSettings { PhysicalAddress = "http://device-one", Enabled = true },
                new HdHomeRunProxyProfileSettings { PhysicalAddress = "http://device-two", Enabled = true },
                new HdHomeRunProxyProfileSettings { PhysicalAddress = "http://device-three", Enabled = false }
            ]
        });
        var handler = new DeviceAuthHandler();
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("HdHomeRunProxyDevice").Returns(new HttpClient(handler));
        var provider = new SettingsDeviceAuthProvider(settings, factory);

        // Act
        var result = await provider.GetDeviceAuthAsync();

        // Assert
        Assert.Equal("firstsecond", result);
        Assert.Equal(["device-one", "device-two"], handler.RequestedHosts);
    }

    private sealed class DeviceAuthHandler : HttpMessageHandler
    {
        /// <summary>
        /// Gets requested hosts.
        /// </summary>
        public List<string> RequestedHosts { get; } = [];

        /// <summary>
        /// Performs the send operation.
        /// </summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri!.Host;
            RequestedHosts.Add(host);
            var deviceAuth = host == "device-one" ? "first" : "second";
            var content = $$"""
                {
                  "FriendlyName": "Test",
                  "ModelNumber": "Test",
                  "FirmwareName": "Test",
                  "FirmwareVersion": "1",
                  "DeviceID": "12345678",
                  "DeviceAuth": "{{deviceAuth}}",
                  "BaseURL": "http://test",
                  "LineupURL": "http://test/lineup.json",
                  "TunerCount": 4
                }
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content)
            });
        }
    }
}
