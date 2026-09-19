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
            var provider = new CachedEpgDataProvider(
                NullLogger<CachedEpgDataProvider>.Instance,
                null!,
                new SiliconDustXmltvParser(),
                guideStore,
                repository,
                new GuideGenerationCoordinator());
            var orchestrator = new EpgOrchestrator(
                NullLogger<EpgOrchestrator>.Instance,
                lineupStore,
                provider,
                repository,
                guideStore);

            // Act
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => orchestrator.FetchAndStoreEpgAsync(2, cancellationToken: TestContext.Current.CancellationToken));

            // Assert
            Assert.Contains("Refresh Channels", exception.Message);
            await repository.DidNotReceive().EnsureDatabaseCreatedAsync();
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
