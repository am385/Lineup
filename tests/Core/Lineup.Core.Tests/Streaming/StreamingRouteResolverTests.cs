using System.Text.Json;
using Lineup.Common.Streaming;
using Lineup.Core.Settings;
using Lineup.Core.Storage;
using Lineup.Extensibility.Streaming;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Lineup.Core.Tests.Streaming;

/// <summary>Verifies lazy lineup initialization recovers without caching unsuccessful refreshes.</summary>
public sealed class StreamingRouteResolverTests
{
    /// <summary>Verifies an initially empty topology recovers on the next tune, including after status polling activates the device.</summary>
    [Theory]
    [InlineData("activation", false)]
    [InlineData("activation", true)]
    [InlineData("lineup", false)]
    [InlineData("lineup", true)]
    public async Task ResolveVirtualAsync_InitialFailure_RetriesUntilLineupCommits(string failure, bool pollStatus)
    {
        // Arrange
        using var fixture = new Fixture();
        var device = fixture.AddPhysical("7.1");
        fixture.SetUnavailable(device.Id, failure, true);
        var configuration = fixture.Configure(device);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.ResolveAsync("7.1"));
        fixture.SetUnavailable(device.Id, failure, false);
        if (pollStatus)
        {
            await new PhysicalDeviceStatusService(fixture.Catalog).RefreshAsync(configuration, TestContext.Current.CancellationToken);
        }
        Assert.Empty(await fixture.Store.ReadActiveAsync(TestContext.Current.CancellationToken));

        // Act
        var recovered = await fixture.ResolveAsync("7.1");
        var repeated = await fixture.ResolveAsync("7.1");

        // Assert
        Assert.NotNull(recovered);
        Assert.NotNull(repeated);
        Assert.Same(configuration, fixture.Settings.Streaming);
        Assert.Equal(new SourceChannelId(device.Id, "7.1"), recovered.SourceChannelId);
        Assert.Equal(recovered.SourceChannelId, repeated.SourceChannelId);
        Assert.Equal(2, fixture.RefreshCount);
        Assert.Equal(failure == "activation" ? 2 : 1, fixture.ActivationCounts[device.Id]);
        Assert.Equal(failure == "activation" ? 1 : 2, fixture.LineupCounts[device.Id]);
        Assert.Single(await fixture.Store.ReadActiveAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>Verifies healthy routes remain usable while another device fails and the incomplete configuration remains retryable.</summary>
    [Theory]
    [InlineData("activation")]
    [InlineData("lineup")]
    public async Task ResolveVirtualAsync_PartialFailure_ServesHealthyRouteAndRecoversMissingRoute(string failure)
    {
        // Arrange
        using var fixture = new Fixture();
        var failed = fixture.AddPhysical("7.1");
        var healthy = fixture.AddPhysical("9.1");
        fixture.Configure(failed, healthy);
        fixture.SetUnavailable(failed.Id, failure, true);
        var firstHealthy = await fixture.ResolveAsync("9.1");
        fixture.SetUnavailable(failed.Id, failure, false);

        // Act
        var recovered = await fixture.ResolveAsync("7.1");
        var repeatedHealthy = await fixture.ResolveAsync("9.1");

        // Assert
        Assert.NotNull(firstHealthy);
        Assert.NotNull(recovered);
        Assert.NotNull(repeatedHealthy);
        Assert.Equal(healthy.Id, firstHealthy.PhysicalDevice.Snapshot.Id);
        Assert.Equal(failed.Id, recovered.PhysicalDevice.Snapshot.Id);
        Assert.Equal(healthy.Id, repeatedHealthy.PhysicalDevice.Snapshot.Id);
        Assert.Equal(2, fixture.RefreshCount);
        Assert.Equal(2, (await fixture.Resolver.GetVirtualLineupAsync(fixture.VirtualDevice.Id, TestContext.Current.CancellationToken)).Count);
    }

    /// <summary>Verifies cancellation and unexpected errors invalidate an older cached configuration after the catalog has changed.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResolveVirtualAsync_InterruptedReplacement_DoesNotReuseOldInitialization(bool cancel)
    {
        // Arrange
        using var fixture = new Fixture();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var first = fixture.AddPhysical("7.1");
        var replacement = fixture.AddPhysical("9.1");
        var original = fixture.Configure(first);
        await fixture.ResolveAsync("7.1");
        fixture.Configure(replacement);
        fixture.BeforeChannelRefresh = (id, token) =>
        {
            Assert.Equal(replacement.Id, id);
            if (cancel)
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            }
            throw new NotSupportedException("Synthetic unexpected lineup failure.");
        };
        var exception = await Record.ExceptionAsync(() => fixture.ResolveAsync("9.1", cancellation.Token));
        fixture.BeforeChannelRefresh = null;
        fixture.Settings.Streaming = original;

        // Act
        var recovered = await fixture.ResolveAsync("7.1").WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Assert
        if (cancel)
        {
            Assert.Equal(cancellation.Token, Assert.IsType<OperationCanceledException>(exception, exactMatch: false).CancellationToken);
        }
        else
        {
            Assert.IsType<NotSupportedException>(exception);
        }
        Assert.NotNull(recovered);
        Assert.Equal(first.Id, recovered.PhysicalDevice.Snapshot.Id);
        Assert.Equal(3, fixture.RefreshCount);
    }

    /// <summary>Verifies concurrent successful requests share initialization and subsequent requests do not refresh again.</summary>
    [Fact]
    public async Task ResolveVirtualAsync_ConcurrentSuccess_InitializesOnce()
    {
        // Arrange
        using var fixture = new Fixture();
        fixture.Configure(fixture.AddPhysical("7.1"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.BeforeRefresh = async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };

        // Act
        var first = fixture.ResolveAsync("7.1");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var second = fixture.ResolveAsync("7.1");
        release.SetResult();
        var routes = await Task.WhenAll(first, second);
        await fixture.ResolveAsync("7.1");

        // Assert
        Assert.All(routes, route => Assert.NotNull(route));
        Assert.Equal(1, fixture.RefreshCount);
        Assert.Equal(1, Assert.Single(fixture.ActivationCounts).Value);
        Assert.Equal(1, Assert.Single(fixture.LineupCounts).Value);
    }

    /// <summary>Verifies a successfully committed empty lineup remains cached instead of treating every missing channel as initialization failure.</summary>
    [Fact]
    public async Task ResolveVirtualAsync_SuccessfulEmptyLineup_DoesNotRetry()
    {
        // Arrange
        using var fixture = new Fixture();
        var device = fixture.AddPhysical("7.1");
        fixture.EmptyLineups.Add(device.Id);
        fixture.Configure(device);

        // Act
        await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.ResolveAsync("7.1"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.ResolveAsync("7.1"));

        // Assert
        Assert.Equal(1, fixture.RefreshCount);
        Assert.Equal(1, fixture.LineupCounts[device.Id]);
        Assert.Empty(await fixture.Store.ReadActiveAsync(TestContext.Current.CancellationToken));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("lineup-route-recovery-");
        private readonly Dictionary<PhysicalDeviceId, string> _channels = [];
        private readonly HashSet<PhysicalDeviceId> _activationFailures = [];
        private readonly HashSet<PhysicalDeviceId> _lineupFailures = [];

        internal Fixture()
        {
            var connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory.FullName, "topology.db"), Pooling = false }.ToString();
            Store = new StreamingTopologyStore(new ContextFactory(new DbContextOptionsBuilder<EpgDbContext>().UseSqlite(connectionString).Options));
            var driver = Substitute.For<IPhysicalTunerDriver>();
            driver.Kind.Returns("route-test");
            driver.Validate(Arg.Any<AdapterConfiguration>()).Returns([]);
            driver.CreateAsync(Arg.Any<PhysicalDeviceId>(), Arg.Any<string>(), Arg.Any<AdapterConfiguration>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                var id = call.Arg<PhysicalDeviceId>();
                ActivationCounts[id] = ActivationCounts.GetValueOrDefault(id) + 1;
                if (_activationFailures.Contains(id))
                {
                    return ValueTask.FromException<IPhysicalTunerDevice>(new IOException("Synthetic activation failure."));
                }
                var device = Substitute.For<IPhysicalTunerDevice>();
                device.Snapshot.Returns(new PhysicalDeviceSnapshot { Id = id, Name = call.Arg<string>(), Kind = "route-test", Capacity = new PhysicalTunerCapacity(1) });
                device.RefreshLineupAsync(Arg.Any<CancellationToken>()).Returns(refresh =>
                {
                    LineupCounts[id] = LineupCounts.GetValueOrDefault(id) + 1;
                    BeforeChannelRefresh?.Invoke(id, refresh.Arg<CancellationToken>());
                    return _lineupFailures.Contains(id)
                        ? Task.FromException<IReadOnlyList<PhysicalSourceChannel>>(new IOException("Synthetic lineup failure."))
                        : Task.FromResult<IReadOnlyList<PhysicalSourceChannel>>(EmptyLineups.Contains(id)
                            ? []
                            : [new PhysicalSourceChannel { Id = new SourceChannelId(id, _channels[id]), ProviderNumber = _channels[id], ProviderName = $"Channel {_channels[id]}" }]);
                });
                return ValueTask.FromResult(device);
            });
            Catalog = new PhysicalDeviceCatalog(new StreamingAdapterRegistry([driver], [], []));
            var lineups = new PhysicalLineupService(Catalog, Store);
            var trackedLineups = Substitute.For<IPhysicalLineupService>();
            trackedLineups.RefreshAsync(Arg.Any<StreamingConfiguration>(), Arg.Any<CancellationToken>()).Returns(async call =>
            {
                RefreshCount++;
                if (BeforeRefresh is not null)
                {
                    await BeforeRefresh(call.Arg<CancellationToken>());
                }
                return await lineups.RefreshAsync(call.Arg<StreamingConfiguration>(), call.Arg<CancellationToken>());
            });
            var settings = Substitute.For<ILineupSettingsService>();
            settings.Settings.Returns(Settings);
            Resolver = new StreamingRouteResolver(settings, Catalog, trackedLineups, Store);
        }

        internal LineupSettings Settings { get; } = new();

        internal VirtualDeviceDefinition VirtualDevice { get; } = new()
        {
            Id = VirtualDeviceId.New(),
            Name = "Recovery output",
            Adapter = new AdapterConfiguration("route-output", JsonSerializer.SerializeToElement(new { }))
        };

        internal StreamingTopologyStore Store { get; }

        internal PhysicalDeviceCatalog Catalog { get; }

        internal StreamingRouteResolver Resolver { get; }

        internal Dictionary<PhysicalDeviceId, int> ActivationCounts { get; } = [];

        internal Dictionary<PhysicalDeviceId, int> LineupCounts { get; } = [];

        internal HashSet<PhysicalDeviceId> EmptyLineups { get; } = [];

        internal Action<PhysicalDeviceId, CancellationToken>? BeforeChannelRefresh { get; set; }

        internal Func<CancellationToken, Task>? BeforeRefresh { get; set; }

        internal int RefreshCount { get; private set; }

        internal PhysicalDeviceDefinition AddPhysical(string number)
        {
            var definition = new PhysicalDeviceDefinition
            {
                Id = PhysicalDeviceId.New(),
                Name = $"Source {number}",
                Order = _channels.Count,
                Adapter = new AdapterConfiguration("route-test", JsonSerializer.SerializeToElement(new { }))
            };
            _channels.Add(definition.Id, number);
            return definition;
        }

        internal StreamingConfiguration Configure(params PhysicalDeviceDefinition[] devices) =>
            Settings.Streaming = new StreamingConfiguration { PhysicalDevices = devices, VirtualDevices = [VirtualDevice] };

        internal void SetUnavailable(PhysicalDeviceId id, string stage, bool unavailable)
        {
            var failures = stage == "activation" ? _activationFailures : _lineupFailures;
            if (unavailable)
            {
                failures.Add(id);
            }
            else
            {
                failures.Remove(id);
            }
        }

        internal Task<ResolvedStreamingRoute?> ResolveAsync(string channel, CancellationToken? cancellationToken = null) =>
            Resolver.ResolveVirtualAsync(VirtualDevice.Id, channel, cancellationToken ?? TestContext.Current.CancellationToken);

        /// <inheritdoc />
        public void Dispose() => _directory.Delete(recursive: true);
    }

    private sealed class ContextFactory(DbContextOptions<EpgDbContext> options) : IDbContextFactory<EpgDbContext>
    {
        /// <inheritdoc />
        public EpgDbContext CreateDbContext() => new(options);
    }
}
