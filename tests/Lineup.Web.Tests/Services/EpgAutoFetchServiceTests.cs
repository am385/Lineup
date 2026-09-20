using Lineup.Web.Services;
using Lineup.Core;
using Lineup.Core.Storage;
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
        var repository = Substitute.For<IEpgRepository>();
        var guideStore = new XmltvGuideStore(Path.Combine(testDirectory, "guide.xml"));
        var provider = new CachedEpgDataProvider(NullLogger<CachedEpgDataProvider>.Instance, null!, new SiliconDustXmltvParser(), guideStore, repository, new GuideGenerationCoordinator());
        var orchestrator = new EpgOrchestrator(NullLogger<EpgOrchestrator>.Instance, null!, provider, repository, guideStore);
        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(EpgOrchestrator)).Returns(orchestrator);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(serviceProvider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);
        var settingsService = Substitute.For<IAppSettingsService>();
        settingsService.Settings.Returns(new AppSettings
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
        scopeFactory.Received(1).CreateScope();
        Assert.True(service.ExecuteTask?.IsCompletedSuccessfully);

        Directory.Delete(testDirectory, recursive: true);
    }
}
