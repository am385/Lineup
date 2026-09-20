using System.Text;
using Lineup.HDHomeRun.Api;
using Lineup.HDHomeRun.Api.Models;
using Lineup.HDHomeRun.Device.Models;
using Lineup.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.Core.Tests;

/// <summary>
/// Verifies guide orchestration against independently persisted tuner lineups.
/// </summary>
public class EpgOrchestratorTests
{
    /// <summary>
    /// Verifies saved tuner metadata enriches a new guide without another tuner query.
    /// </summary>
    [Fact]
    public async Task FetchAndStoreEpgAsync_UsesSavedLineupMetadata()
    {
        // Arrange
        const string xml = """
            <tv>
              <channel id="station"><display-name>Guide Name</display-name><lcn>7.1</lcn></channel>
              <programme start="20260914220000 +0000" stop="20260914223000 +0000" channel="station">
                <title>Test Show</title>
              </programme>
            </tv>
            """;
        var root = Directory.CreateTempSubdirectory("lineup-orchestrator-");
        try
        {
            var lineupStore = new ChannelLineupStore(Path.Combine(root.FullName, "channels.json"));
            await lineupStore.StoreAsync(
            [
                new HDHomeRunChannel
                {
                    GuideNumber = "7.1",
                    GuideName = "Tuner Name",
                    Favorite = true,
                    URL = "http://device/auto/v7.1"
                }
            ], TestContext.Current.CancellationToken);
            var guideStore = new XmltvGuideStore(Path.Combine(root.FullName, "guide.xml"));
            var repository = Substitute.For<IEpgRepository>();
            repository.GetCacheStatisticsAsync().Returns(new CacheStatistics(1, 1, null, null, null));
            HDHomeRunChannelEpgSegment? enrichedChannel = null;
            repository.StoreChannelsAsync(Arg.Any<IEnumerable<HDHomeRunChannelEpgSegment>>())
                .Returns(callInfo =>
                {
                    enrichedChannel = callInfo.Arg<IEnumerable<HDHomeRunChannelEpgSegment>>().Single();
                    return Task.CompletedTask;
                });
            var authProvider = Substitute.For<IDeviceAuthProvider>();
            authProvider.GetDeviceAuthAsync().Returns("test-auth");
            var apiClient = new HDHomeRunApiClient(NullLogger<HDHomeRunApiClient>.Instance, new HttpClient(new StaticResponseHandler(xml)), authProvider);
            var provider = new CachedEpgDataProvider(NullLogger<CachedEpgDataProvider>.Instance, apiClient, new SiliconDustXmltvParser(), guideStore, repository, new GuideGenerationCoordinator());
            var orchestrator = new EpgOrchestrator(NullLogger<EpgOrchestrator>.Instance, lineupStore, provider, repository, guideStore);

            // Act
            await orchestrator.FetchAndStoreEpgAsync(2, cancellationToken: TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal("Tuner Name", enrichedChannel!.GuideName);
            Assert.True(enrichedChannel.Favorite);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Verifies guide fetching requires a prior channel refresh and fails before contacting SiliconDust.
    /// </summary>
    [Fact]
    public async Task FetchAndStoreEpgAsync_WithoutSavedLineup_RequiresChannelRefresh()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-orchestrator-");
        try
        {
            var lineupStore = new ChannelLineupStore(Path.Combine(root.FullName, "channels.json"));
            var guideStore = new XmltvGuideStore(Path.Combine(root.FullName, "guide.xml"));
            var repository = Substitute.For<IEpgRepository>();
            var provider = new CachedEpgDataProvider(NullLogger<CachedEpgDataProvider>.Instance, null!, new SiliconDustXmltvParser(), guideStore, repository, new GuideGenerationCoordinator());
            var orchestrator = new EpgOrchestrator(NullLogger<EpgOrchestrator>.Instance, lineupStore, provider, repository, guideStore);

            // Act
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.FetchAndStoreEpgAsync(2, cancellationToken: TestContext.Current.CancellationToken));

            // Assert
            Assert.Contains("Refresh Channels", exception.Message);
            await repository.DidNotReceive().EnsureDatabaseCreatedAsync();
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private sealed class StaticResponseHandler(string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/xml")
            });
        }
    }
}
