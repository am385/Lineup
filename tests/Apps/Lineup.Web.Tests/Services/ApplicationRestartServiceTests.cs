using Lineup.Web.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>
/// Verifies graceful application restart requests.
/// </summary>
public class ApplicationRestartServiceTests
{
    /// <summary>
    /// Verifies a restart request stops the current application host.
    /// </summary>
    [Fact]
    public async Task RestartAsync_Requested_StopsApplication()
    {
        // Arrange
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        var service = new ApplicationRestartService(lifetime, Substitute.For<ILogger<ApplicationRestartService>>());

        // Act
        await service.RestartAsync(TestContext.Current.CancellationToken);

        // Assert
        lifetime.Received(1).StopApplication();
    }
}
