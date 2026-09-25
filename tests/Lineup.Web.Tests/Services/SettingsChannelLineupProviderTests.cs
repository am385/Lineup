using System.Net;
using Lineup.Web.Services;
using NSubstitute;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>
/// Verifies channel lineup aggregation across configured physical tuners.
/// </summary>
public class SettingsChannelLineupProviderTests
{
    /// <summary>
    /// Verifies enabled unique devices contribute channels to the guide filter.
    /// </summary>
    [Fact]
    public async Task FetchChannelLineupAsync_CombinesEnabledUniqueDeviceLineups()
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
                new HdHomeRunProxyProfileSettings { PhysicalAddress = "HTTP://DEVICE-TWO/", Enabled = true },
                new HdHomeRunProxyProfileSettings { PhysicalAddress = "http://device-three", Enabled = false }
            ]
        });
        var handler = new ChannelLineupHandler();
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("HdHomeRunProxyDevice").Returns(new HttpClient(handler));
        var provider = new SettingsChannelLineupProvider(settings, factory);

        // Act
        var result = await provider.FetchChannelLineupAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(["2.1", "7.1"], result.Select(channel => channel.GuideNumber));
        Assert.Equal(["device-one", "device-two"], handler.RequestedHosts);
    }

    private sealed class ChannelLineupHandler : HttpMessageHandler
    {
        public List<string> RequestedHosts { get; } = [];

        /// <inheritdoc/>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri!.Host;
            RequestedHosts.Add(host);
            var channel = host == "device-one" ? "2.1" : "7.1";
            var content = $$"""
                [{
                  "GuideNumber": "{{channel}}",
                  "GuideName": "Test {{channel}}",
                  "URL": "http://{{host}}/auto/v{{channel}}"
                }]
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content)
            });
        }
    }
}
