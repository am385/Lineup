using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>Verifies hosted streaming topology activation.</summary>
public sealed class StreamingTopologyRuntimeTests
{
    /// <summary>Verifies topology activation republishes cached guide data without refreshing remote sources.</summary>
    [Fact]
    public async Task ExecuteAsync_StartupAndSettingsChange_ReappliesRuntime()
    {
        // Arrange
        var databasePath = Path.Combine(AppContext.BaseDirectory, $"topology-runtime-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<EpgDbContext>().UseSqlite($"Data Source={databasePath}").Options;
        var settings = Substitute.For<ILineupSettingsService>();
        var virtualDeviceId = VirtualDeviceId.New();
        var configuration = new StreamingConfiguration
        {
            VirtualDevices =
            [
                new VirtualDeviceDefinition
                {
                    Id = virtualDeviceId,
                    Name = "Web Player",
                    Adapter = new AdapterConfiguration("web-player", System.Text.Json.JsonSerializer.SerializeToElement(new { }))
                }
            ]
        };
        settings.Settings.Returns(new LineupSettings { Streaming = configuration, XmltvOutputPath = "published.xml" });
        var lineups = Substitute.For<IPhysicalLineupService>();
        lineups.RefreshAsync(Arg.Any<StreamingConfiguration>(), Arg.Any<CancellationToken>()).Returns([]);
        var guides = Substitute.For<ITopologyGuideService>();
        var runtime = new StreamingTopologyRuntime(
            settings,
            lineups,
            guides,
            NullLogger<StreamingTopologyRuntime>.Instance);

        try
        {
            // Act
            await runtime.StartAsync(TestContext.Current.CancellationToken);
            await WaitUntilAsync(() => lineups.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(IPhysicalLineupService.RefreshAsync)) >= 1);
            settings.OnSettingsChanged += Raise.Event<Action>();
            await WaitUntilAsync(() => lineups.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(IPhysicalLineupService.RefreshAsync)) >= 2);
            await runtime.StopAsync(TestContext.Current.CancellationToken);

            // Assert
            await lineups.Received(2).RefreshAsync(Arg.Any<StreamingConfiguration>(), Arg.Any<CancellationToken>());
            await guides.Received(2).PublishAsync(Arg.Any<CancellationToken>());
            await guides.DidNotReceive().RefreshAsync(Arg.Any<CancellationToken>());
            await guides.DidNotReceive().RefreshAndPublishAsync(Arg.Any<CancellationToken>());
        }
        finally
        {
            runtime.Dispose();
            File.Delete(databasePath);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        Assert.True(condition());
    }

    private sealed class TestDbContextFactory(DbContextOptions<EpgDbContext> options) : IDbContextFactory<EpgDbContext>
    {
        public EpgDbContext CreateDbContext() => new(options);

        public Task<EpgDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
