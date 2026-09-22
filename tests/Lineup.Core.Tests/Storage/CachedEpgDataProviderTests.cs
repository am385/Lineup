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
/// Tests canonical guide reconciliation.
/// </summary>
public class CachedEpgDataProviderTests
{
    /// <summary>
    /// Verifies downloaded guide data is filtered against the tuner lineup before publication.
    /// </summary>
    [Fact]
    public async Task FetchAndStoreRawDataAsync_FiltersCacheAndCanonicalGuide()
    {
        // Arrange
        const string xml = """
            <tv>
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
        var testDirectory = Path.Combine(Directory.GetCurrentDirectory(), "test-data", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);
        try
        {
            var guideStore = new XmltvGuideStore(Path.Combine(testDirectory, "guide.xml"));
            var repository = Substitute.For<IEpgRepository>();
            IReadOnlyList<HDHomeRunChannelEpgSegment>? storedSegments = null;
            repository.ReplaceRawEpgDataAsync(
                    Arg.Any<IEnumerable<HDHomeRunChannelEpgSegment>>(),
                    Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    storedSegments = callInfo.Arg<IEnumerable<HDHomeRunChannelEpgSegment>>().ToArray();
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
                guideStore,
                repository,
                new GuideGenerationCoordinator());

            // Act
            var result = await provider.FetchAndStoreRawDataAsync([CreateChannel("7.1")], cancellationToken: TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal("7.1", Assert.Single(result).GuideNumber);
            Assert.Equal("7.1", Assert.Single(storedSegments!).GuideNumber);
            Assert.Equal("7.1", Assert.Single(parser.Parse((await guideStore.ReadAsync(TestContext.Current.CancellationToken))!)).GuideNumber);
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
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
        var testDirectory = Path.Combine(Directory.GetCurrentDirectory(), "test-data", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);
        try
        {
            var guideStore = new XmltvGuideStore(Path.Combine(testDirectory, "guide.xml"));
            var repository = Substitute.For<IEpgRepository>();
            IReadOnlyList<HDHomeRunChannelEpgSegment>? storedSegments = null;
            repository.ReplaceRawEpgDataAsync(Arg.Any<IEnumerable<HDHomeRunChannelEpgSegment>>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    storedSegments = callInfo.Arg<IEnumerable<HDHomeRunChannelEpgSegment>>().ToArray();
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
                guideStore,
                repository,
                new GuideGenerationCoordinator());

            // Act
            var result = await provider.FetchAndStoreRawDataAsync([CreateChannel("7.1")], cancellationToken: TestContext.Current.CancellationToken);

            // Assert
            var channel = Assert.Single(result);
            Assert.Equal("7.1", channel.GuideNumber);
            Assert.Equal("Not Available", Assert.Single(channel.Guide).Title);
            Assert.Equal("Not Available", Assert.Single(storedSegments!).Guide.Single().Title);
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    /// <summary>
    /// Verifies startup reconciliation rebuilds placeholder data from the authoritative canonical guide.
    /// </summary>
    [Fact]
    public async Task ReconcileFromCanonicalGuideAsync_WhenGuideExists_ReplacesNormalizedCache()
    {
        // Arrange
        const string xml = """
            <tv>
              <channel id="other"><display-name>Other</display-name><lcn>99.1</lcn></channel>
              <programme start="20260914220000 +0000" stop="20260914223000 +0000" channel="other">
                <title>Other Show</title>
              </programme>
            </tv>
            """;
        var testDirectory = Path.Combine(Directory.GetCurrentDirectory(), "test-data", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);
        var guideStore = new XmltvGuideStore(Path.Combine(testDirectory, "guide.xml"));
        var parser = new SiliconDustXmltvParser();
        var projectedGuide = parser.FilterByChannels(Encoding.UTF8.GetBytes(xml), [CreateChannel("7.1")]);
        await guideStore.StoreAsync(projectedGuide, TestContext.Current.CancellationToken);
        var repository = Substitute.For<IEpgRepository>();
        var replacementCount = 0;
        string? replacedGuideNumber = null;
        string? replacedTitle = null;
        repository.ReplaceRawEpgDataAsync(Arg.Any<IEnumerable<HDHomeRun.Api.Models.HDHomeRunChannelEpgSegment>>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            replacementCount++;
            var segment = callInfo.Arg<IEnumerable<HDHomeRun.Api.Models.HDHomeRunChannelEpgSegment>>().Single();
            replacedGuideNumber = segment.GuideNumber;
            replacedTitle = Assert.Single(segment.Guide).Title;
            return Task.CompletedTask;
        });
        var provider = new CachedEpgDataProvider(NullLogger<CachedEpgDataProvider>.Instance, null!, parser, guideStore, repository, new GuideGenerationCoordinator());

        // Act
        await provider.ReconcileFromCanonicalGuideAsync(TestContext.Current.CancellationToken);
        await provider.ReconcileFromCanonicalGuideAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, replacementCount);
        Assert.Equal("7.1", replacedGuideNumber);
        Assert.Equal("Not Available", replacedTitle);
        await repository.Received(1).EnsureDatabaseCreatedAsync();

        Directory.Delete(testDirectory, recursive: true);
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
