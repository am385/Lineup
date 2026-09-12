using System.Text;
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
    /// Verifies startup reconciliation rebuilds the normalized database from the authoritative canonical guide.
    /// </summary>
    [Fact]
    public async Task ReconcileFromCanonicalGuideAsync_WhenGuideExists_ReplacesNormalizedCache()
    {
        // Arrange
        const string xml = """
            <tv>
              <channel id="station"><display-name>Test</display-name><lcn>7.1</lcn></channel>
              <programme start="20260914220000 +0000" stop="20260914223000 +0000" channel="station">
                <title>Test Show</title>
              </programme>
            </tv>
            """;
        var testDirectory = Path.Combine(Directory.GetCurrentDirectory(), "test-data", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);
        var guideStore = new XmltvGuideStore(Path.Combine(testDirectory, "guide.xml"));
        await guideStore.StoreAsync(Encoding.UTF8.GetBytes(xml), TestContext.Current.CancellationToken);
        var repository = Substitute.For<IEpgRepository>();
        var replacementCount = 0;
        string? replacedGuideNumber = null;
        repository.ReplaceRawEpgDataAsync(Arg.Any<IEnumerable<HDHomeRun.Api.Models.HDHomeRunChannelEpgSegment>>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            replacementCount++;
            replacedGuideNumber = callInfo.Arg<IEnumerable<HDHomeRun.Api.Models.HDHomeRunChannelEpgSegment>>().Single().GuideNumber;
            return Task.CompletedTask;
        });
        var provider = new CachedEpgDataProvider(NullLogger<CachedEpgDataProvider>.Instance, null!, new SiliconDustXmltvParser(), guideStore, repository, new GuideGenerationCoordinator());

        // Act
        await provider.ReconcileFromCanonicalGuideAsync(TestContext.Current.CancellationToken);
        await provider.ReconcileFromCanonicalGuideAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, replacementCount);
        Assert.Equal("7.1", replacedGuideNumber);
        await repository.Received(1).EnsureDatabaseCreatedAsync();

        Directory.Delete(testDirectory, recursive: true);
    }
}
