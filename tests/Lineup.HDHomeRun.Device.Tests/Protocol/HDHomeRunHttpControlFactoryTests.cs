using System.Net;
using System.Text;
using Lineup.HDHomeRun.Device.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lineup.HDHomeRun.Device.Tests.Protocol;

/// <summary>
/// Tests factory-managed HDHomeRun HTTP controls.
/// </summary>
public class HDHomeRunHttpControlFactoryTests
{
    /// <summary>
    /// Verifies the factory uses the named client and applies each device base address independently.
    /// </summary>
    [Fact]
    public async Task Create_ForMultipleDevices_UsesNamedClientsWithIndependentAddresses()
    {
        // Arrange
        var clientFactory = new RecordingHttpClientFactory();
        var factory = new HDHomeRunHttpControlFactory(clientFactory, NullLogger<HDHomeRunHttpControl>.Instance);
        using var first = factory.Create(IPAddress.Parse("192.0.2.10"));
        using var second = factory.Create("http://192.0.2.20:8080/");

        // Act
        var firstResult = await first.DiscoverAsync(TestContext.Current.CancellationToken);
        var secondResult = await second.DiscoverAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(firstResult);
        Assert.NotNull(secondResult);
        Assert.Equal([HDHomeRunHttpControlFactory.HttpClientName, HDHomeRunHttpControlFactory.HttpClientName], clientFactory.RequestedNames);
        Assert.Equal(
            ["http://192.0.2.10/discover.json", "http://192.0.2.20:8080/discover.json"],
            clientFactory.RequestUris.Select(uri => uri.AbsoluteUri));
    }

    private sealed class RecordingHttpClientFactory : IHttpClientFactory
    {
        public List<string> RequestedNames { get; } = [];

        public List<Uri> RequestUris { get; } = [];

        public HttpClient CreateClient(string name)
        {
            RequestedNames.Add(name);
            return new HttpClient(new RecordingHandler(RequestUris));
        }
    }

    private sealed class RecordingHandler(List<Uri> requestUris) : HttpMessageHandler
    {
        /// <inheritdoc/>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requestUris.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"DeviceID":"12345678","TunerCount":2}""", Encoding.UTF8, "application/json")
            });
        }
    }
}
