using System.Reflection;
using System.Text.Json;
using Lineup.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.App.Web.Tests.Services;

/// <summary>
/// Tests automatic EPG fetch scheduling.
/// </summary>
public class EpgAutoFetchServiceTests
{
    /// <summary>Verifies successfully written partial guides do not advance the last fully successful automatic refresh.</summary>
    [Fact]
    public async Task FetchEpgDataAsync_PartialPublication_DoesNotRecordSuccess()
    {
        // Arrange
        using var fixture = new FetchFixture();
        fixture.Result = Result(sourceFails: false, outputFails: false) with
        {
            Outputs =
            [
                new GuideOutputPublicationResult
                {
                    OutputId = GuideOutputId.New(),
                    Name = "Partial output",
                    Succeeded = true,
                    SourceDiagnostics =
                    [
                        new GuideSourceDiagnostic
                        {
                            SourceId = GuideSourceId.New(),
                            SourceName = "Missing source",
                            Code = GuideSourceDiagnosticCode.AdapterUnavailable,
                            Message = "Install the source plugin."
                        }
                    ]
                }
            ]
        };
        var previous = fixture.Settings.LastAutoFetchTime;

        // Act
        var exception = await Record.ExceptionAsync(() => fixture.FetchAsync(TestContext.Current.CancellationToken));

        // Assert
        var failure = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("1 published partial guides", failure.Message, StringComparison.Ordinal);
        Assert.Equal(previous, fixture.Settings.LastAutoFetchTime);
        Assert.Null(fixture.State.LastFetchTime);
        Assert.Equal(fixture.Now.AddHours(24), fixture.Settings.GuideSourceSchedules[fixture.SourceId.Value].NextRefreshUtc);
    }

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
        Assert.Equal(fixture.Now.AddHours(sourceAlsoFails ? 1 : 24), fixture.Settings.GuideSourceSchedules[fixture.SourceId.Value].NextRefreshUtc);
    }

    /// <summary>Verifies successful publication and refresh-only operations retain their provider's successful schedule.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FetchEpgDataAsync_Success_RecordsCompletionAndSchedule(bool publish)
    {
        // Arrange
        using var fixture = new FetchFixture();
        fixture.Settings.AutoGenerateXmltv = publish;
        fixture.Result = Result(sourceFails: false, outputFails: false);

        // Act
        await fixture.FetchAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.False(fixture.State.IsRunning);
        Assert.Null(fixture.State.CurrentProgress);
        Assert.Equal(fixture.Now, fixture.Settings.LastAutoFetchTime);
        var interval = fixture.Settings.NextAutoFetchTime!.Value - Assert.IsType<DateTime>(fixture.Settings.LastAutoFetchTime);
        Assert.Equal(TimeSpan.FromHours(24), interval);
        await fixture.Guides.Received(1).RefreshSourcesAsync(fixture.Settings.Streaming,
            Arg.Is<IReadOnlySet<GuideSourceId>>(ids => ids.SetEquals(new[] { fixture.SourceId })), false, Arg.Any<CancellationToken>());
        await fixture.Guides.Received(publish ? 1 : 0).PublishAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>Verifies publication failure reports failure without replacing the last success or shortening a healthy source's cadence.</summary>
    [Fact]
    public async Task ExecuteAsync_PublicationFailure_ReportsFailureAndSchedulesRetry()
    {
        // Arrange
        using var fixture = new FetchFixture();
        fixture.Result = Result(sourceFails: false, outputFails: true);
        var previous = fixture.Settings.LastAutoFetchTime;
        fixture.Settings.NextAutoFetchTime = DateTime.UtcNow.AddSeconds(1);
        using var service = fixture.CreateService(TimeProvider.System);

        // Act
        await service.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await fixture.Failed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        finally
        {
            await service.StopAsync(TestContext.Current.CancellationToken);
        }

        // Assert
        Assert.False(fixture.State.IsRunning);
        Assert.Equal(FetchStatus.Failed, fixture.State.CurrentProgress?.Status);
        Assert.Contains("output", fixture.State.CurrentProgress?.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(previous, fixture.Settings.LastAutoFetchTime);
        Assert.Null(fixture.State.LastFetchTime);
        Assert.InRange(Assert.IsType<DateTime>(fixture.Settings.NextAutoFetchTime), fixture.Now.AddHours(24), DateTime.UtcNow.AddHours(24));
        Assert.InRange(fixture.Settings.GuideSourceSchedules[fixture.SourceId.Value].NextRefreshUtc, fixture.Now.AddHours(24), DateTime.UtcNow.AddHours(24));
        Assert.InRange(Assert.IsType<DateTime>(fixture.State.NextFetchTime), fixture.Now.AddHours(24), DateTime.UtcNow.AddHours(24));
        Assert.Equal(fixture.Now.AddHours(1), Assert.Single(fixture.Settings.GuidePublicationRetries).Value);
    }

    /// <summary>Verifies restarting the scheduler retries a failed output without refetching a healthy source before its persisted cadence.</summary>
    [Fact]
    public async Task FetchEpgDataAsync_OutputRetryAfterRestart_DoesNotRefetchHealthySource()
    {
        // Arrange
        using var fixture = new FetchFixture();
        fixture.Result = Result(sourceFails: false, outputFails: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.FetchAsync(TestContext.Current.CancellationToken));
        var sourceNext = fixture.Settings.GuideSourceSchedules[fixture.SourceId.Value].NextRefreshUtc;
        fixture.Now = fixture.Now.AddHours(1);
        fixture.Guides.ClearReceivedCalls();
        var retried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Guides.RetryPublicationsAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            fixture.Settings.GuidePublicationRetries.Clear();
            retried.TrySetResult();
            return Task.FromResult<IReadOnlyList<GuideOutputPublicationResult>>([]);
        });
        using var restarted = fixture.CreateService();
        using var publications = fixture.CreatePublicationWorker();

        // Act
        await restarted.StartAsync(TestContext.Current.CancellationToken);
        await publications.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await retried.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        finally
        {
            await publications.StopAsync(TestContext.Current.CancellationToken);
            await restarted.StopAsync(TestContext.Current.CancellationToken);
        }

        // Assert
        await fixture.Guides.DidNotReceive().RefreshSourcesAsync(Arg.Any<StreamingConfiguration>(), Arg.Any<IReadOnlySet<GuideSourceId>>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await fixture.Guides.Received(1).RetryPublicationsAsync(fixture.Now, Arg.Any<CancellationToken>());
        Assert.Equal(sourceNext, fixture.Settings.NextAutoFetchTime);
        Assert.Equal(sourceNext, fixture.Settings.GuideSourceSchedules[fixture.SourceId.Value].NextRefreshUtc);
    }

    /// <summary>Verifies disabled automatic source fetching does not disable durable cached-output recovery.</summary>
    [Fact]
    public async Task ExecuteAsync_AutoFetchDisabled_StillRetriesPendingPublications()
    {
        // Arrange
        using var fixture = new FetchFixture();
        fixture.Settings.AutoFetchInterval = TimeSpan.Zero;
        fixture.Settings.GuidePublicationRetries[fixture.Settings.Streaming.GuideOutputs[0].Id.Value] = fixture.Now;
        using var service = fixture.CreateService(TimeProvider.System);
        using var publications = fixture.CreatePublicationWorker();
        var retried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var schedulerReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.State.OnStateChanged += () =>
        {
            if (!fixture.State.IsEnabled)
            {
                schedulerReady.TrySetResult();
            }
        };
        fixture.Guides.RetryPublicationsAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            fixture.Settings.GuidePublicationRetries.Clear();
            retried.TrySetResult();
            return Task.FromResult<IReadOnlyList<GuideOutputPublicationResult>>([]);
        });

        // Act
        await service.StartAsync(TestContext.Current.CancellationToken);
        await publications.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await Task.WhenAll(schedulerReady.Task, retried.Task).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        finally
        {
            await publications.StopAsync(TestContext.Current.CancellationToken);
            await service.StopAsync(TestContext.Current.CancellationToken);
        }

        // Assert
        await fixture.Guides.DidNotReceive().RefreshSourcesAsync(Arg.Any<StreamingConfiguration>(), Arg.Any<IReadOnlySet<GuideSourceId>>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await fixture.Guides.Received(1).RetryPublicationsAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        Assert.False(fixture.State.IsEnabled);
    }

    /// <summary>Verifies one failed source retries independently while a healthy source retains its configured successful cadence.</summary>
    [Fact]
    public async Task FetchEpgDataAsync_MixedSourceResults_OnlyRetriesFailedSource()
    {
        // Arrange
        using var fixture = new FetchFixture();
        var otherId = GuideSourceId.New();
        fixture.Settings.AutoFetchInterval = TimeSpan.FromMinutes(90);
        fixture.Settings.Streaming = fixture.Settings.Streaming with
        {
            GuideSources = [.. fixture.Settings.Streaming.GuideSources, fixture.Settings.Streaming.GuideSources[0] with { Id = otherId, Name = "Other guide" }]
        };
        fixture.Result = new TopologyGuideRefreshResult
        {
            Sources =
            [
                new GuideRefreshResult { SourceId = fixture.SourceId, Succeeded = true },
                new GuideRefreshResult { SourceId = otherId, Succeeded = false, Error = "Source unavailable" }
            ]
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.FetchAsync(TestContext.Current.CancellationToken));
        var healthyNext = fixture.Settings.GuideSourceSchedules[fixture.SourceId.Value].NextRefreshUtc;
        Assert.Equal(fixture.Now.AddMinutes(90), healthyNext);
        Assert.Equal(fixture.Now.AddHours(1), fixture.Settings.GuideSourceSchedules[otherId.Value].NextRefreshUtc);
        fixture.Now = fixture.Now.AddHours(1);
        fixture.Guides.ClearReceivedCalls();
        fixture.Result = fixture.Result with { Sources = fixture.Result.Sources.Select(source => source with { Succeeded = true }).ToArray() };

        // Act
        await fixture.FetchAsync(TestContext.Current.CancellationToken);

        // Assert
        await fixture.Guides.Received(1).RefreshSourcesAsync(fixture.Settings.Streaming,
            Arg.Is<IReadOnlySet<GuideSourceId>>(ids => ids.Count == 1 && ids.Contains(otherId)), false, Arg.Any<CancellationToken>());
        Assert.Equal(healthyNext, fixture.Settings.GuideSourceSchedules[fixture.SourceId.Value].NextRefreshUtc);
        Assert.Equal(fixture.Now.AddMinutes(90), fixture.Settings.GuideSourceSchedules[otherId.Value].NextRefreshUtc);
        Assert.Equal(healthyNext, fixture.Settings.NextAutoFetchTime);
    }

    /// <summary>Verifies the scheduler honors provider-owned successful cadence instead of replacing it with the configured host interval.</summary>
    [Fact]
    public async Task FetchEpgDataAsync_ProviderPolicy_OverridesHostInterval()
    {
        // Arrange
        using var fixture = new FetchFixture(TimeSpan.FromHours(22));
        fixture.Settings.AutoFetchInterval = TimeSpan.FromMinutes(90);
        fixture.Result = Result(sourceFails: false, outputFails: false);

        // Act
        await fixture.FetchAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(fixture.Now.AddHours(22), fixture.Settings.GuideSourceSchedules[fixture.SourceId.Value].NextRefreshUtc);
        Assert.IsAssignableFrom<IGuideSourceRefreshPolicy>(fixture.Adapters.GetGuide("guide")).Received(1).GetRefreshInterval(TimeSpan.FromMinutes(90));
    }

    /// <summary>Verifies renaming a healthy source cannot bypass its existing successful-refresh cadence.</summary>
    [Fact]
    public async Task FetchEpgDataAsync_SourceRenamed_DoesNotRefetchBeforeSuccessfulCadence()
    {
        // Arrange
        using var fixture = new FetchFixture();
        fixture.Result = Result(sourceFails: false, outputFails: false);
        await fixture.FetchAsync(TestContext.Current.CancellationToken);
        var next = fixture.Settings.GuideSourceSchedules[fixture.SourceId.Value].NextRefreshUtc;
        fixture.Now = fixture.Now.AddHours(1);
        fixture.Settings.Streaming = fixture.Settings.Streaming with { GuideSources = [fixture.Settings.Streaming.GuideSources[0] with { Name = "Renamed guide" }] };
        fixture.Guides.ClearReceivedCalls();

        // Act
        await fixture.FetchAsync(TestContext.Current.CancellationToken);

        // Assert
        await fixture.Guides.DidNotReceive().RefreshSourcesAsync(Arg.Any<StreamingConfiguration>(), Arg.Any<IReadOnlySet<GuideSourceId>>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        Assert.Equal(next, fixture.Settings.NextAutoFetchTime);
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
        internal FetchFixture(TimeSpan? providerInterval = null)
        {
            using var adapter = JsonDocument.Parse("{}");
            Settings.Streaming = new StreamingConfiguration
            {
                GuideSources = [new GuideSourceDefinition { Id = SourceId, Name = "Guide", Adapter = new AdapterConfiguration("guide", adapter.RootElement.Clone()) }],
                GuideOutputs = [new GuideOutputDefinition { Id = GuideOutputId.New(), Name = "Output", Key = "guide", Adapter = new AdapterConfiguration("xmltv", adapter.RootElement.Clone()) }]
            };
            SettingsService.Settings.Returns(Settings);
            SettingsService.UpdateAsync(Arg.Any<Action<LineupSettings>>()).Returns(call =>
            {
                call.Arg<Action<LineupSettings>>()(Settings);
                return Task.CompletedTask;
            });
            Guides.RefreshSourcesAsync(Arg.Any<StreamingConfiguration>(), Arg.Any<IReadOnlySet<GuideSourceId>>(), false, Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult(Result with { Sources = Result.Sources.Where(source => call.Arg<IReadOnlySet<GuideSourceId>>().Contains(source.SourceId)).ToArray(), Outputs = [] }));
            Guides.PublishAsync(Arg.Any<CancellationToken>()).Returns(_ =>
            {
                if (Result.Outputs.Any(output => !output.Succeeded || output.IsPartial))
                {
                    Settings.GuidePublicationRetries[Settings.Streaming.GuideOutputs[0].Id.Value] = Now.AddHours(1);
                }
                return Task.FromResult(Result.Outputs);
            });
            Guides.RetryPublicationsAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<GuideOutputPublicationResult>());
            var scope = Substitute.For<IServiceScope>();
            var provider = Substitute.For<IServiceProvider>();
            provider.GetService(typeof(ITopologyGuideService)).Returns(Guides);
            scope.ServiceProvider.Returns(provider);
            var scopes = Substitute.For<IServiceScopeFactory>();
            scopes.CreateScope().Returns(scope);
            Scopes = scopes;
            Clock = new AdjustableTimeProvider(() => Now);
            var driver = providerInterval.HasValue ? Substitute.For<IGuideSourceDriver, IGuideSourceRefreshPolicy>() : Substitute.For<IGuideSourceDriver>();
            driver.Kind.Returns("guide");
            if (providerInterval.HasValue)
            {
                ((IGuideSourceRefreshPolicy)driver).GetRefreshInterval(Arg.Any<TimeSpan>()).Returns(providerInterval.Value);
            }
            Adapters = new StreamingAdapterRegistry([], [], [driver], []);
            Service = CreateService();
            State.OnStateChanged += () =>
            {
                if (State.CurrentProgress?.Status == FetchStatus.Failed)
                {
                    Failed.TrySetResult();
                }
            };
        }

        /// <summary>Gets the configured source identity.</summary>
        internal GuideSourceId SourceId { get; } = GuideSourceId.New();

        /// <summary>Gets or sets the deterministic successful-completion clock.</summary>
        internal DateTime Now { get; set; } = DateTime.UtcNow;

        /// <summary>Gets the injectable clock used for direct scheduling checks.</summary>
        internal TimeProvider Clock { get; }

        /// <summary>Gets the reusable guide-service scope boundary.</summary>
        internal IServiceScopeFactory Scopes { get; }

        /// <summary>Gets the provider capabilities used for scheduling.</summary>
        internal IStreamingAdapterRegistry Adapters { get; }

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

        private TopologyGuideRefreshResult _result = new() { Sources = [] };

        internal TopologyGuideRefreshResult Result
        {
            get => _result;
            set => _result = value with { Sources = value.Sources.Count == 1 ? [value.Sources[0] with { SourceId = SourceId }] : value.Sources };
        }

        /// <summary>Gets completion signaled when a running service reports its failure.</summary>
        internal TaskCompletionSource Failed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Creates a replacement scheduler using the saved operational state.</summary>
        internal EpgAutoFetchService CreateService(TimeProvider? clock = null) => new(Scopes, NullLogger<EpgAutoFetchService>.Instance, State, SettingsService, Adapters, clock ?? Clock);

        /// <summary>Creates the independent output retry worker with the same committed scheduling state.</summary>
        internal GuidePublicationRetryService CreatePublicationWorker() => new(Scopes, NullLogger<GuidePublicationRetryService>.Instance, Clock);

        internal Task FetchAsync(CancellationToken token, EpgAutoFetchService? service = null)
        {
            var method = typeof(EpgAutoFetchService).GetMethod("FetchEpgDataAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            return Assert.IsAssignableFrom<Task>(method.Invoke(service ?? Service, [token]));
        }

        private sealed class AdjustableTimeProvider(Func<DateTime> utcNow) : TimeProvider
        {
            /// <inheritdoc />
            public override DateTimeOffset GetUtcNow() => new(utcNow());
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
        var clock = new ControlledTimeProvider();
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
            NextAutoFetchTime = clock.GetUtcNow().UtcDateTime.AddHours(1)
        });
        using var service = new EpgAutoFetchService(
            scopeFactory, NullLogger<EpgAutoFetchService>.Instance, new AutoFetchStateService(), settingsService, Substitute.For<IStreamingAdapterRegistry>(), clock);
        await service.StartAsync(TestContext.Current.CancellationToken);

        // Act
        try
        {
            await clock.WaitForTimerAsync(TimeSpan.FromHours(1), TestContext.Current.CancellationToken);
            settingsService.OnSettingsChanged += Raise.Event<Action>();
            await clock.WaitForTimerAsync(TimeSpan.FromHours(1), TestContext.Current.CancellationToken);
            clock.Advance(TimeSpan.FromMinutes(59));
        }
        finally
        {
            await service.StopAsync(TestContext.Current.CancellationToken);
        }

        // Assert
        scopeFactory.DidNotReceive().CreateScope();
        Assert.True(service.ExecuteTask?.IsCompletedSuccessfully);
    }
}
