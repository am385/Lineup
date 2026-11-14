using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.App.Web.Tests.Services;

/// <summary>Verifies hosted streaming topology activation.</summary>
public sealed class StreamingTopologyRuntimeTests
{
    /// <summary>Verifies failed publication results are logged independently and do not prevent later topology applications.</summary>
    [Fact]
    public async Task ExecuteAsync_PublicationResultsContainFailures_LogsFailuresAndContinues()
    {
        // Arrange
        var settings = Substitute.For<ILineupSettingsService>();
        settings.Settings.Returns(new LineupSettings());
        var lineups = Substitute.For<IPhysicalLineupService>();
        lineups.RefreshAsync(Arg.Any<StreamingConfiguration>(), Arg.Any<CancellationToken>()).Returns([]);
        var guides = Substitute.For<ITopologyGuideService>();
        var firstFailure = new GuideOutputPublicationResult { OutputId = GuideOutputId.New(), Name = "First output", Succeeded = false, Error = "Output storage is unavailable." };
        var success = new GuideOutputPublicationResult { OutputId = GuideOutputId.New(), Name = "Healthy output", Succeeded = true };
        var secondFailure = new GuideOutputPublicationResult { OutputId = GuideOutputId.New(), Name = "Second output", Succeeded = false, Error = "Publisher is unavailable." };
        var publicationCount = 0;
        guides.PublishConfigurationAsync(Arg.Any<StreamingConfiguration>(), Arg.Any<CancellationToken>()).Returns(_ =>
            Task.FromResult<IReadOnlyList<GuideOutputPublicationResult>>(Interlocked.Increment(ref publicationCount) == 1 ? [firstFailure, success, secondFailure] : []));
        var logger = Substitute.For<ILogger<StreamingTopologyRuntime>>();
        using var runtime = new StreamingTopologyRuntime(settings, lineups, guides, logger);

        try
        {
            // Act
            await runtime.StartAsync(TestContext.Current.CancellationToken);
            await WaitUntilAsync(() => logger.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(ILogger.Log)) == 2);
            runtime.Retry();
            await WaitUntilAsync(() => Volatile.Read(ref publicationCount) >= 2);
            await runtime.StopAsync(TestContext.Current.CancellationToken);

            // Assert
            var warnings = logger.ReceivedCalls().Where(call => call.GetMethodInfo().Name == nameof(ILogger.Log)).ToArray();
            Assert.Collection(warnings,
                call =>
                {
                    Assert.Equal(LogLevel.Warning, call.GetArguments()[0]);
                    var values = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(call.GetArguments()[2]).ToDictionary();
                    Assert.Equal(firstFailure.OutputId, values["GuideOutputId"]);
                    Assert.Equal(firstFailure.Error, values["Error"]);
                },
                call =>
                {
                    Assert.Equal(LogLevel.Warning, call.GetArguments()[0]);
                    var values = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(call.GetArguments()[2]).ToDictionary();
                    Assert.Equal(secondFailure.OutputId, values["GuideOutputId"]);
                    Assert.Equal(secondFailure.Error, values["Error"]);
                });
            await lineups.Received(2).RefreshAsync(Arg.Any<StreamingConfiguration>(), Arg.Any<CancellationToken>());
            await guides.Received(2).PublishConfigurationAsync(Arg.Any<StreamingConfiguration>(), Arg.Any<CancellationToken>());
            await guides.DidNotReceive().RefreshAsync(Arg.Any<CancellationToken>());
        }
        finally
        {
            await runtime.StopAsync(CancellationToken.None);
        }
    }

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
        var savedSettings = new LineupSettings { Streaming = configuration, XmltvOutputPath = "published.xml" };
        settings.Settings.Returns(savedSettings);
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
            savedSettings.Streaming = configuration with
            {
                VirtualDevices = [configuration.VirtualDevices[0] with { Subscription = new PhysicalDeviceSubscription { Mode = PhysicalDeviceSubscriptionMode.Selected } }]
            };
            settings.OnSettingsChanged += Raise.Event<Action>();
            var application = await runtime.WaitForApplicationAsync(ConfigurationRevision.Create(savedSettings.Streaming), TestContext.Current.CancellationToken);
            await runtime.StopAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal(TopologyApplicationPhase.Applied, application.Phase);
            Assert.Equal(application.DesiredRevision, application.AppliedRevision);
            await lineups.Received(2).RefreshAsync(Arg.Any<StreamingConfiguration>(), Arg.Any<CancellationToken>());
            await lineups.Received(1).RefreshAsync(savedSettings.Streaming, Arg.Any<CancellationToken>());
            await guides.Received(2).PublishConfigurationAsync(Arg.Any<StreamingConfiguration>(), Arg.Any<CancellationToken>());
            await guides.DidNotReceive().RefreshAsync(Arg.Any<CancellationToken>());
            await guides.DidNotReceive().RefreshAndPublishAsync(Arg.Any<CancellationToken>());
        }
        finally
        {
            runtime.Dispose();
            File.Delete(databasePath);
        }
    }

    /// <summary>Verifies unchanged topology ignores timestamp, scheduler, and display-setting notifications.</summary>
    [Fact]
    public async Task ExecuteAsync_UnrelatedSettingsChange_DoesNotReapplyTopology()
    {
        // Arrange
        var saved = new LineupSettings();
        var settings = Substitute.For<ILineupSettingsService>();
        settings.Settings.Returns(saved);
        var lineups = Substitute.For<IPhysicalLineupService>();
        lineups.RefreshAsync(Arg.Any<StreamingConfiguration>(), Arg.Any<CancellationToken>()).Returns([]);
        var guides = Substitute.For<ITopologyGuideService>();
        guides.PublishConfigurationAsync(Arg.Any<StreamingConfiguration>(), Arg.Any<CancellationToken>()).Returns([]);
        using var runtime = new StreamingTopologyRuntime(settings, lineups, guides, NullLogger<StreamingTopologyRuntime>.Instance);
        await runtime.StartAsync(TestContext.Current.CancellationToken);
        var applied = await runtime.WaitForApplicationAsync(ConfigurationRevision.Create(saved.Streaming), TestContext.Current.CancellationToken);
        var notifications = 0;
        runtime.ApplicationChanged += () => notifications++;

        // Act
        saved.NextAutoFetchTime = DateTime.UtcNow.AddDays(1);
        saved.LastAutoFetchTime = DateTime.UtcNow;
        saved.AutoFetchInterval = TimeSpan.FromHours(24);
        saved.DeviceRefreshIntervalMinutes = 20;
        settings.OnSettingsChanged += Raise.Event<Action>();
        await runtime.StopAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, notifications);
        Assert.Same(applied, runtime.Application);
        await lineups.Received(1).RefreshAsync(Arg.Any<StreamingConfiguration>(), Arg.Any<CancellationToken>());
        await guides.Received(1).PublishConfigurationAsync(Arg.Any<StreamingConfiguration>(), Arg.Any<CancellationToken>());
    }

    /// <summary>Verifies a failed desired revision retains the last successful revision and can be retried without saving.</summary>
    [Fact]
    public async Task ExecuteAsync_PublicationFails_RetainsAppliedRevisionAndRetries()
    {
        // Arrange
        var saved = new LineupSettings();
        var settings = Substitute.For<ILineupSettingsService>();
        settings.Settings.Returns(saved);
        var lineups = Substitute.For<IPhysicalLineupService>();
        lineups.RefreshAsync(Arg.Any<StreamingConfiguration>(), Arg.Any<CancellationToken>()).Returns([]);
        var guides = Substitute.For<ITopologyGuideService>();
        guides.PublishConfigurationAsync(Arg.Any<StreamingConfiguration>(), Arg.Any<CancellationToken>()).Returns([]);
        using var runtime = new StreamingTopologyRuntime(settings, lineups, guides, NullLogger<StreamingTopologyRuntime>.Instance);
        await runtime.StartAsync(TestContext.Current.CancellationToken);
        var originalRevision = ConfigurationRevision.Create(saved.Streaming);
        await runtime.WaitForApplicationAsync(originalRevision, TestContext.Current.CancellationToken);
        var failure = new GuideOutputPublicationResult { OutputId = GuideOutputId.New(), Name = "Unavailable output", Succeeded = false, Error = "Storage unavailable." };
        guides.PublishConfigurationAsync(Arg.Any<StreamingConfiguration>(), Arg.Any<CancellationToken>()).Returns([failure]);

        // Act
        saved.Streaming = saved.Streaming with { MaximumPluginConnections = 12 };
        var desiredRevision = ConfigurationRevision.Create(saved.Streaming);
        settings.OnSettingsChanged += Raise.Event<Action>();
        var degraded = await runtime.WaitForApplicationAsync(desiredRevision, TestContext.Current.CancellationToken);
        guides.PublishConfigurationAsync(Arg.Any<StreamingConfiguration>(), Arg.Any<CancellationToken>()).Returns([]);
        runtime.Retry();
        var retried = await runtime.WaitForApplicationAsync(desiredRevision, TestContext.Current.CancellationToken);
        await runtime.StopAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(TopologyApplicationPhase.Degraded, degraded.Phase);
        Assert.Equal(desiredRevision, degraded.AttemptedRevision);
        Assert.Equal(originalRevision, degraded.AppliedRevision);
        Assert.Same(failure, Assert.Single(degraded.Outputs));
        Assert.Equal(TopologyApplicationPhase.Applied, retried.Phase);
        Assert.Equal(desiredRevision, retried.AppliedRevision);
        await settings.DidNotReceive().UpdateAsync(Arg.Any<Action<LineupSettings>>());
        await lineups.Received(3).RefreshAsync(Arg.Any<StreamingConfiguration>(), Arg.Any<CancellationToken>());
    }

    /// <summary>Verifies a concurrent settings save cannot mix physical and guide application generations.</summary>
    [Fact]
    public async Task ExecuteAsync_SettingsChangeDuringApply_UsesCapturedConfigurationsAndRejectsSupersededWaiter()
    {
        // Arrange
        var original = new StreamingConfiguration();
        var latest = original with { MaximumPluginConnections = 12 };
        var saved = new LineupSettings { Streaming = original };
        var settings = Substitute.For<ILineupSettingsService>();
        settings.Settings.Returns(saved);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lineups = Substitute.For<IPhysicalLineupService>();
        lineups.RefreshAsync(Arg.Any<StreamingConfiguration>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            if (ReferenceEquals(call.Arg<StreamingConfiguration>(), original))
            {
                entered.SetResult();
                await release.Task.WaitAsync(call.Arg<CancellationToken>());
            }
            IReadOnlyList<PhysicalLineupRefreshResult> results = [];
            return results;
        });
        var guides = Substitute.For<ITopologyGuideService>();
        guides.PublishConfigurationAsync(Arg.Any<StreamingConfiguration>(), Arg.Any<CancellationToken>()).Returns([]);
        using var runtime = new StreamingTopologyRuntime(settings, lineups, guides, NullLogger<StreamingTopologyRuntime>.Instance);
        await runtime.StartAsync(TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        // Act
        saved.Streaming = latest;
        settings.OnSettingsChanged += Raise.Event<Action>();
        await Assert.ThrowsAsync<SettingsConflictException>(() => runtime.WaitForApplicationAsync(ConfigurationRevision.Create(original), TestContext.Current.CancellationToken));
        release.SetResult();
        var applied = await runtime.WaitForApplicationAsync(ConfigurationRevision.Create(latest), TestContext.Current.CancellationToken);
        await runtime.StopAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(TopologyApplicationPhase.Applied, applied.Phase);
        Assert.Equal(ConfigurationRevision.Create(latest), applied.AppliedRevision);
        Received.InOrder(() =>
        {
            guides.PublishConfigurationAsync(original, Arg.Any<CancellationToken>());
            guides.PublishConfigurationAsync(latest, Arg.Any<CancellationToken>());
        });
        await lineups.Received(1).RefreshAsync(original, Arg.Any<CancellationToken>());
        await lineups.Received(1).RefreshAsync(latest, Arg.Any<CancellationToken>());
    }

    /// <summary>Verifies a coalesced configuration round trip cannot leave the desired revision permanently pending.</summary>
    [Fact]
    public async Task ExecuteAsync_DesiredRevisionReturnsToPreviousValue_CompletesPendingApplication()
    {
        // Arrange
        var saved = new LineupSettings();
        var original = saved.Streaming;
        var settings = Substitute.For<ILineupSettingsService>();
        settings.Settings.Returns(saved);
        var lineups = Substitute.For<IPhysicalLineupService>();
        lineups.RefreshAsync(Arg.Any<StreamingConfiguration>(), Arg.Any<CancellationToken>()).Returns([]);
        var guides = Substitute.For<ITopologyGuideService>();
        guides.PublishConfigurationAsync(Arg.Any<StreamingConfiguration>(), Arg.Any<CancellationToken>()).Returns([]);
        using var runtime = new StreamingTopologyRuntime(settings, lineups, guides, NullLogger<StreamingTopologyRuntime>.Instance);
        var roundTrip = false;
        runtime.ApplicationChanged += () =>
        {
            if (!roundTrip && runtime.Application.Phase == TopologyApplicationPhase.Applied)
            {
                roundTrip = true;
                saved.Streaming = original with { MaximumPluginConnections = 12 };
                settings.OnSettingsChanged += Raise.Event<Action>();
                saved.Streaming = original;
                settings.OnSettingsChanged += Raise.Event<Action>();
            }
        };

        // Act
        await runtime.StartAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => roundTrip && runtime.Application.Phase == TopologyApplicationPhase.Applied);
        await runtime.StopAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ConfigurationRevision.Create(original), runtime.Application.AppliedRevision);
        await lineups.Received(2).RefreshAsync(original, Arg.Any<CancellationToken>());
        await guides.Received(2).PublishConfigurationAsync(original, Arg.Any<CancellationToken>());
    }

    /// <summary>Verifies shutdown releases application waiters and rejects requests that cannot be executed.</summary>
    [Fact]
    public async Task StopAsync_ApplicationPending_CancelsWaitersAndRejectsRetry()
    {
        // Arrange
        var saved = new LineupSettings();
        var settings = Substitute.For<ILineupSettingsService>();
        settings.Settings.Returns(saved);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lineups = Substitute.For<IPhysicalLineupService>();
        lineups.RefreshAsync(Arg.Any<StreamingConfiguration>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>());
            IReadOnlyList<PhysicalLineupRefreshResult> results = [];
            return results;
        });
        using var runtime = new StreamingTopologyRuntime(settings, lineups, Substitute.For<ITopologyGuideService>(), NullLogger<StreamingTopologyRuntime>.Instance);
        await runtime.StartAsync(TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var waiting = runtime.WaitForApplicationAsync(ConfigurationRevision.Create(saved.Streaming), TestContext.Current.CancellationToken);

        // Act
        await runtime.StopAsync(TestContext.Current.CancellationToken);

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Throws<InvalidOperationException>(runtime.Retry);
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
        /// <summary>Creates db context.</summary>
        /// <returns>The operation's result.</returns>
        public EpgDbContext CreateDbContext() => new(options);

        /// <summary>Creates db context asynchronously.</summary>
        /// <param name="cancellationToken">Token used to cancel the operation.</param>
        /// <returns>The operation's result.</returns>
        public Task<EpgDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
