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
            var lineupStore = new ChannelLineupStore(Path.Combine(root.FullName, "lineup.db"));
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
            var provider = new CachedEpgDataProvider(NullLogger<CachedEpgDataProvider>.Instance, apiClient, new SiliconDustXmltvParser(), repository, new GuideGenerationCoordinator());
            var orchestrator = new EpgOrchestrator(
                NullLogger<EpgOrchestrator>.Instance,
                lineupStore,
                provider,
                repository,
                new LineupXmltvWriter(),
                new XmltvPublicationStore());

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
            var lineupStore = new ChannelLineupStore(Path.Combine(root.FullName, "lineup.db"));
            var repository = Substitute.For<IEpgRepository>();
            var provider = new CachedEpgDataProvider(NullLogger<CachedEpgDataProvider>.Instance, null!, new SiliconDustXmltvParser(), repository, new GuideGenerationCoordinator());
            var orchestrator = new EpgOrchestrator(
                NullLogger<EpgOrchestrator>.Instance,
                lineupStore,
                provider,
                repository,
                new LineupXmltvWriter(),
                new XmltvPublicationStore());

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

    /// <summary>
    /// Verifies public XMLTV excludes disabled channels without changing authoritative guide data.
    /// </summary>
    [Fact]
    public async Task GenerateEpgFromCacheAsync_FiltersDisabledChannelsOnlyFromPublishedOutput()
    {
        // Arrange
        const string xml = """
            <tv>
              <channel id="enabled"><display-name>Enabled</display-name><lcn>7.1</lcn></channel>
              <channel id="disabled"><display-name>Disabled</display-name><lcn>9.1</lcn></channel>
              <programme start="20260914220000 +0000" stop="20260914223000 +0000" channel="enabled"><title>Enabled Show</title></programme>
              <programme start="20260914220000 +0000" stop="20260914223000 +0000" channel="disabled"><title>Disabled Show</title></programme>
            </tv>
            """;
        var root = Directory.CreateTempSubdirectory("lineup-orchestrator-");
        try
        {
            var lineupStore = new ChannelLineupStore(Path.Combine(root.FullName, "lineup.db"));
            await lineupStore.StoreAsync(
                [CreateChannel("7.1"), CreateChannel("9.1"), CreateChannel("11.1"), CreateChannel("13.1")],
                TestContext.Current.CancellationToken);
            await lineupStore.SetChannelEnabledAsync("9.1", enabled: false, TestContext.Current.CancellationToken);
            await lineupStore.SetChannelEnabledAsync("13.1", enabled: false, TestContext.Current.CancellationToken);
            var parser = new SiliconDustXmltvParser();
            var segments = parser.Parse(Encoding.UTF8.GetBytes(xml)).Segments;
            var repository = Substitute.For<IEpgRepository>();
            repository.GetGuideSnapshotAsync(cancellationToken: Arg.Any<CancellationToken>()).Returns(new XmltvGuideSnapshot { Segments = segments });
            var orchestrator = new EpgOrchestrator(
                NullLogger<EpgOrchestrator>.Instance,
                lineupStore,
                null!,
                repository,
                new LineupXmltvWriter(),
                new XmltvPublicationStore());
            var outputPath = Path.Combine(root.FullName, "published.xml");

            // Act
            await orchestrator.GenerateEpgFromCacheAsync(2, outputPath, TestContext.Current.CancellationToken);

            // Assert
            var publishedChannels = parser.Parse(await File.ReadAllBytesAsync(outputPath, TestContext.Current.CancellationToken)).Segments;
            Assert.Equal(["7.1", "11.1"], publishedChannels.Select(channel => channel.GuideNumber));
            Assert.Equal("Not Available", Assert.Single(publishedChannels.Single(channel => channel.GuideNumber == "11.1").Guide).Title);
            Assert.Equal(["7.1", "9.1"], segments.Select(channel => channel.GuideNumber));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Verifies XMLTV generation creates placeholder data when the database contains no guide.
    /// </summary>
    [Fact]
    public async Task GenerateEpgFromCacheAsync_WithoutCanonicalGuide_PublishesEnabledPlaceholders()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-orchestrator-");
        try
        {
            var lineupStore = new ChannelLineupStore(Path.Combine(root.FullName, "lineup.db"));
            await lineupStore.StoreAsync(
                [CreateChannel("7.1"), CreateChannel("9.1")],
                TestContext.Current.CancellationToken);
            await lineupStore.SetChannelEnabledAsync("9.1", enabled: false, TestContext.Current.CancellationToken);
            var parser = new SiliconDustXmltvParser();
            var repository = Substitute.For<IEpgRepository>();
            repository.GetGuideSnapshotAsync(cancellationToken: Arg.Any<CancellationToken>()).Returns(new XmltvGuideSnapshot { Segments = [] });
            var orchestrator = new EpgOrchestrator(
                NullLogger<EpgOrchestrator>.Instance,
                lineupStore,
                null!,
                repository,
                new LineupXmltvWriter(),
                new XmltvPublicationStore());
            var outputPath = Path.Combine(root.FullName, "published.xml");

            // Act
            await orchestrator.GenerateEpgFromCacheAsync(2, outputPath, TestContext.Current.CancellationToken);

            // Assert
            var channel = Assert.Single(parser.Parse(await File.ReadAllBytesAsync(outputPath, TestContext.Current.CancellationToken)).Segments);
            var programme = Assert.Single(channel.Guide);
            Assert.Equal("7.1", channel.GuideNumber);
            Assert.Equal("Not Available", programme.Title);
            Assert.Equal(TimeSpan.FromDays(2), DateTimeOffset.FromUnixTimeSeconds(programme.EndTime) - DateTimeOffset.FromUnixTimeSeconds(programme.StartTime));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static HDHomeRunChannel CreateChannel(string guideNumber) => new()
    {
        GuideNumber = guideNumber,
        GuideName = guideNumber,
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
