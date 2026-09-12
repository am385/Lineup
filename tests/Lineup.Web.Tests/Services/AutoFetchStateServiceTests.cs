using Lineup.Core;
using Lineup.Web.Services;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>
/// Tests automatic EPG fetch state transitions.
/// </summary>
public class AutoFetchStateServiceTests
{
    /// <summary>
    /// Verifies a failed fetch is no longer reported as running.
    /// </summary>
    [Fact]
    public void FailFetch_WhenRunning_RecordsFailureAndClearsRunningState()
    {
        // Arrange
        var service = new AutoFetchStateService();
        service.StartFetch();

        // Act
        service.FailFetch("Download failed");

        // Assert
        Assert.False(service.IsRunning);
        Assert.Equal(FetchStatus.Failed, service.CurrentProgress?.Status);
        Assert.Equal("Download failed", service.CurrentProgress?.ErrorMessage);
    }
}
