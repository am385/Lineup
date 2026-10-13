using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>
/// Tests automatic EPG fetch scheduling.
/// </summary>
public class EpgAutoFetchServiceTests
{
    /// <summary>
    /// Verifies a settings change during the initial delay recalculates instead of fetching immediately.
    /// </summary>
    [Fact]
    public async Task SettingsChange_DuringInitialDelay_DoesNotFetchBeforePersistedSchedule()
    {
        // Arrange
        var testDirectory = Path.Combine(Directory.GetCurrentDirectory(), "test-data", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);
        var guides = Substitute.For<ITopologyGuideService>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(ITopologyGuideService)).Returns(guides);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(serviceProvider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);
        var settingsService = Substitute.For<ILineupSettingsService>();
        settingsService.Settings.Returns(new LineupSettings
        {
            AutoFetchInterval = TimeSpan.FromHours(24),
            NextAutoFetchTime = DateTime.UtcNow.AddHours(1)
        });
        using var service = new EpgAutoFetchService(scopeFactory, NullLogger<EpgAutoFetchService>.Instance, new AutoFetchStateService(), settingsService);
        await service.StartAsync(TestContext.Current.CancellationToken);

        // Act
        settingsService.OnSettingsChanged += Raise.Event<Action>();
        await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);

        // Assert
        scopeFactory.DidNotReceive().CreateScope();
        Assert.True(service.ExecuteTask?.IsCompletedSuccessfully);

        Directory.Delete(testDirectory, recursive: true);
    }
}
