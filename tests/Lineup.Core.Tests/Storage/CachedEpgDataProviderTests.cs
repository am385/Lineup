using System.Text;
using Lineup.HDHomeRun.Api;
using Lineup.HDHomeRun.Api.Models;
using Lineup.HDHomeRun.Device.Models;
using Lineup.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.Core.Tests.Storage;

/// <summary>
/// Tests SiliconDust guide import and physical-lineup projection.
/// </summary>
public class CachedEpgDataProviderTests
{
    /// <summary>
    /// Verifies downloaded guide data is filtered against the tuner lineup before publication.
    /// </summary>
    [Fact]
    public async Task FetchAndStoreRawDataAsync_FiltersAndImportsAuthoritativeGuide()
    {
        // Arrange
        const string xml = """
            <tv source-info-name="Provider">
              <channel id="available"><display-name>Available</display-name><lcn>7.1</lcn></channel>
              <channel id="removed"><display-name>Removed</display-name><lcn>9.1</lcn></channel>
              <programme start="20260914220000 +0000" stop="20260914223000 +0000" channel="available">
                <title>Available Show</title>
              </programme>
              <programme start="20260914220000 +0000" stop="20260914223000 +0000" channel="removed">
                <title>Removed Show</title>
              </programme>
            </tv>
            """;
        var repository = Substitute.For<IEpgRepository>();
        XmltvGuideSnapshot? storedSnapshot = null;
        repository.ImportGuideAsync(
                Arg.Any<XmltvGuideSnapshot>(),
                TimeSpan.FromHours(24),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                storedSnapshot = callInfo.Arg<XmltvGuideSnapshot>();
                return Task.CompletedTask;
            });
        var authProvider = Substitute.For<IDeviceAuthProvider>();
        authProvider.GetDeviceAuthAsync().Returns("test-auth");
        var apiClient = new HDHomeRunApiClient(
            NullLogger<HDHomeRunApiClient>.Instance,
            new HttpClient(new StaticResponseHandler(xml)),
            authProvider);
        var parser = new SiliconDustXmltvParser();
        var provider = new CachedEpgDataProvider(
            NullLogger<CachedEpgDataProvider>.Instance,
            apiClient,
            parser,
            repository,
            new GuideGenerationCoordinator());

        // Act
        var result = await provider.FetchAndStoreRawDataAsync([CreateChannel("7.1")], cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("7.1", Assert.Single(result).GuideNumber);
        Assert.Equal("7.1", Assert.Single(storedSnapshot!.Segments).GuideNumber);
        Assert.NotNull(storedSnapshot.SupplementalXml);
    }

    /// <summary>
    /// Verifies a valid guide with no tuner overlap stores the tuner channel with placeholder programme data.
    /// </summary>
    [Fact]
    public async Task FetchAndStoreRawDataAsync_ZeroOverlap_StoresPlaceholderChannel()
    {
        // Arrange
        const string xml = """
            <tv>
              <channel id="other"><display-name>Other</display-name><lcn>99.1</lcn></channel>
              <programme start="20260914220000 +0000" stop="20260915223000 +0000" channel="other">
                <title>Other Show</title>
              </programme>
            </tv>
            """;
        var repository = Substitute.For<IEpgRepository>();
        XmltvGuideSnapshot? storedSnapshot = null;
        repository.ImportGuideAsync(Arg.Any<XmltvGuideSnapshot>(), TimeSpan.FromHours(24), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                storedSnapshot = callInfo.Arg<XmltvGuideSnapshot>();
                return Task.CompletedTask;
            });
        var authProvider = Substitute.For<IDeviceAuthProvider>();
        authProvider.GetDeviceAuthAsync().Returns("test-auth");
        var apiClient = new HDHomeRunApiClient(
            NullLogger<HDHomeRunApiClient>.Instance,
            new HttpClient(new StaticResponseHandler(xml)),
            authProvider);
        var provider = new CachedEpgDataProvider(
            NullLogger<CachedEpgDataProvider>.Instance,
            apiClient,
            new SiliconDustXmltvParser(),
            repository,
            new GuideGenerationCoordinator());

        // Act
        var result = await provider.FetchAndStoreRawDataAsync([CreateChannel("7.1")], cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var channel = Assert.Single(result);
        Assert.Equal("7.1", channel.GuideNumber);
        Assert.Equal("Not Available", Assert.Single(channel.Guide).Title);
        Assert.Equal("Not Available", Assert.Single(storedSnapshot!.Segments).Guide.Single().Title);
    }

    private static HDHomeRunChannel CreateChannel(string guideNumber) => new()
    {
        GuideNumber = guideNumber,
        GuideName = $"Channel {guideNumber}",
        URL = $"http://device/auto/v{guideNumber}"
    };

    private sealed class StaticResponseHandler(string content) : HttpMessageHandler
    {
        /// <inheritdoc/>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/xml")
            });
        }
    }
}
