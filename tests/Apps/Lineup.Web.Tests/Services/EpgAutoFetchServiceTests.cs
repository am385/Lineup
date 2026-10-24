using System.Reflection;
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
    /// <summary>Verifies failed publication is not recorded as a successful automatic fetch.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FetchEpgDataAsync_PublicationFailure_DoesNotRecordSuccess(bool sourceAlsoFails)
    {
        // Arrange
        using var fixture = new FetchFixture();
        fixture.Result = Result(sourceAlsoFails, outputFails: true);
        var previous = fixture.Settings.LastAutoFetchTime;

        // Act
        var exception = await Record.ExceptionAsync(() => fixture.FetchAsync(TestContext.Current.CancellationToken));

        // Assert
        var failure = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("output", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(previous, fixture.Settings.LastAutoFetchTime);
        Assert.Null(fixture.State.LastFetchTime);
        await fixture.SettingsService.DidNotReceive().UpdateAsync(Arg.Any<Action<LineupSettings>>());
    }

    /// <summary>Verifies successful publication and refresh-only operations retain their successful randomized schedule.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FetchEpgDataAsync_Success_RecordsCompletionAndSchedule(bool publish)
    {
        // Arrange
        using var fixture = new FetchFixture();
        fixture.Settings.AutoGenerateXmltv = publish;
        fixture.Result = Result(sourceFails: false, outputFails: false);
        var before = DateTime.UtcNow;

        // Act
        await fixture.FetchAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.False(fixture.State.IsRunning);
        Assert.Null(fixture.State.CurrentProgress);
        Assert.InRange(fixture.Settings.LastAutoFetchTime!.Value, before, DateTime.UtcNow);
        var interval = fixture.Settings.NextAutoFetchTime!.Value - fixture.Settings.LastAutoFetchTime.Value;
        Assert.InRange(interval, TimeSpan.FromHours(20), TimeSpan.FromHours(28));
        await fixture.Guides.Received(publish ? 1 : 0).RefreshAndPublishAsync(Arg.Any<CancellationToken>());
        await fixture.Guides.Received(publish ? 0 : 1).RefreshAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>Verifies publication failure reaches observable failure state and the existing one-hour retry without replacing the last success.</summary>
    [Fact]
    public async Task ExecuteAsync_PublicationFailure_ReportsFailureAndSchedulesRetry()
    {
        // Arrange
        using var fixture = new FetchFixture();
        fixture.Result = Result(sourceFails: false, outputFails: true);
        var previous = fixture.Settings.LastAutoFetchTime;
        fixture.Settings.NextAutoFetchTime = DateTime.UtcNow.AddSeconds(1);
        var before = DateTime.UtcNow;

        // Act
        await fixture.Service.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await fixture.Updated.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        finally
        {
            await fixture.Service.StopAsync(TestContext.Current.CancellationToken);
        }

        // Assert
        Assert.False(fixture.State.IsRunning);
        Assert.Equal(FetchStatus.Failed, fixture.State.CurrentProgress?.Status);
        Assert.Contains("output", fixture.State.CurrentProgress?.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(previous, fixture.Settings.LastAutoFetchTime);
        Assert.Null(fixture.State.LastFetchTime);
        Assert.InRange(fixture.Settings.NextAutoFetchTime!.Value, before.AddHours(1), DateTime.UtcNow.AddHours(1));
        Assert.InRange(fixture.State.NextFetchTime!.Value, before.AddHours(1), DateTime.UtcNow.AddHours(1));
    }

    private static TopologyGuideRefreshResult Result(bool sourceFails, bool outputFails) => new()
    {
        Sources = [new GuideRefreshResult { SourceId = GuideSourceId.New(), Succeeded = !sourceFails }],
        Outputs =
        [
            new GuideOutputPublicationResult { OutputId = GuideOutputId.New(), Name = "Successful output", Succeeded = true },
            new GuideOutputPublicationResult { OutputId = GuideOutputId.New(), Name = "Other output", Succeeded = !outputFails, Error = outputFails ? "Publication unavailable" : null }
        ]
    };

    private sealed class FetchFixture : IDisposable
    {
        internal FetchFixture()
        {
            SettingsService.Settings.Returns(Settings);
            SettingsService.UpdateAsync(Arg.Any<Action<LineupSettings>>()).Returns(call =>
            {
                call.Arg<Action<LineupSettings>>()(Settings);
                Updated.TrySetResult();
                return Task.CompletedTask;
            });
            Guides.RefreshAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(Result));
            Guides.RefreshAndPublishAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(Result));
            var scope = Substitute.For<IServiceScope>();
            var provider = Substitute.For<IServiceProvider>();
            provider.GetService(typeof(ITopologyGuideService)).Returns(Guides);
            scope.ServiceProvider.Returns(provider);
            var scopes = Substitute.For<IServiceScopeFactory>();
            scopes.CreateScope().Returns(scope);
            Service = new EpgAutoFetchService(scopes, NullLogger<EpgAutoFetchService>.Instance, State, SettingsService);
        }

        internal LineupSettings Settings { get; } = new()
        {
            AutoFetchInterval = TimeSpan.FromHours(24),
            LastAutoFetchTime = DateTime.UtcNow.AddDays(-1),
            RefreshChannelsBeforeGuideFetch = false
        };

        internal ILineupSettingsService SettingsService { get; } = Substitute.For<ILineupSettingsService>();

        internal ITopologyGuideService Guides { get; } = Substitute.For<ITopologyGuideService>();

        internal AutoFetchStateService State { get; } = new();

        internal EpgAutoFetchService Service { get; }

        internal TopologyGuideRefreshResult Result { get; set; } = new() { Sources = [] };

        internal TaskCompletionSource Updated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task FetchAsync(CancellationToken token)
        {
            var method = typeof(EpgAutoFetchService).GetMethod("FetchEpgDataAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            return Assert.IsAssignableFrom<Task>(method.Invoke(Service, [token]));
        }

        /// <inheritdoc />
        public void Dispose() => Service.Dispose();
    }

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
