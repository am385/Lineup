using System.Text.Json;
using Lineup.Core.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.Core.Tests.Streaming;

/// <summary>Verifies bounded refresh admission, independent cancellation and generation-safe incremental publication.</summary>
public sealed class PhysicalRefreshConcurrencyTests
{
    /// <summary>Verifies inline cancellation completion cannot race token-source disposal or leak an abandoned refresh.</summary>
    [Fact]
    public async Task Coalescing_LastCallerCancellation_AllowsInlineCleanupAndNextRefresh()
    {
        // Arrange
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var refresh = new CoalescedRefresh<int>(NullLogger.Instance);
        var completion = new TaskCompletionSource<int>();
        var cleaned = false;
        var pending = refresh.RunAsync("same", async token =>
        {
            using var registration = token.Register(() => completion.TrySetCanceled(token));
            try
            {
                return await completion.Task;
            }
            finally
            {
                cleaned = true;
            }
        }, caller.Token, waitForCancellationCleanup: true);

        // Act
        await caller.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        var next = await refresh.RunAsync("same", _ => Task.FromResult(42), TestContext.Current.CancellationToken);

        // Assert
        Assert.True(cleaned);
        Assert.Equal(42, next);
    }

    /// <summary>Verifies invalid startup refresh budgets fail before any adapter work is admitted.</summary>
    [Theory]
    [InlineData(0, 10)]
    [InlineData(-1, 10)]
    [InlineData(1025, 10)]
    [InlineData(4, 0)]
    [InlineData(4, -1)]
    [InlineData(4, 301)]
    public void Scheduler_InvalidPolicy_RejectsConfiguration(int concurrency, int seconds)
    {
        // Arrange
        Action create = () => _ = new PhysicalDeviceRefreshScheduler(concurrency, seconds);

        // Act
        var exception = Record.Exception(create);

        // Assert
        Assert.IsType<ArgumentOutOfRangeException>(exception);
    }

    /// <summary>Verifies healthy lineup commits precede a blocked peer, concurrent triggers coalesce and obsolete results never reach storage.</summary>
    [Fact]
    public async Task Lineup_HealthyDeviceCommitsIndependently_AndObsoleteResultIsDiscarded()
    {
        // Arrange
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        watchdog.CancelAfter(TimeSpan.FromSeconds(15));
        var root = Directory.CreateTempSubdirectory("lineup-refresh-concurrency-");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var factory = new ContextFactory(Path.Combine(root.FullName, "lineup.db"), committed);
            var topology = new StreamingTopologyStore(factory);
            var blocked = Definition("Blocked", 0);
            var healthy = Definition("Healthy", 1);
            var oldRuntime = Runtime(blocked);
            var healthyRuntime = Runtime(healthy);
            oldRuntime.RefreshLineupAsync(Arg.Any<CancellationToken>()).Returns(async call =>
            {
                await release.Task.WaitAsync(call.Arg<CancellationToken>());
                IReadOnlyList<PhysicalSourceChannel> channels = [new PhysicalSourceChannel { Id = new SourceChannelId(blocked.Id, "old"), ProviderNumber = "1", ProviderName = "Obsolete" }];
                return channels;
            });
            healthyRuntime.RefreshLineupAsync(Arg.Any<CancellationToken>()).Returns(
                new[] { new PhysicalSourceChannel { Id = new SourceChannelId(healthy.Id, "healthy"), ProviderNumber = "2", ProviderName = "Healthy" } });
            var replacementDefinition = blocked with
            {
                Name = "Replacement",
                Adapter = new AdapterConfiguration("refresh-test", JsonSerializer.SerializeToElement(new { generation = 2 }))
            };
            var replacementRuntime = Runtime(replacementDefinition);
            var driver = Driver();
            driver.CreateAsync(Arg.Any<PhysicalDeviceId>(), Arg.Any<string>(), Arg.Any<AdapterConfiguration>(), Arg.Any<CancellationToken>()).Returns(call =>
                ValueTask.FromResult(call.Arg<string>() == "Replacement" ? replacementRuntime : call.Arg<PhysicalDeviceId>() == blocked.Id ? oldRuntime : healthyRuntime));
            var scheduler = new PhysicalDeviceRefreshScheduler();
            var catalog = new PhysicalDeviceCatalog(new StreamingAdapterRegistry([driver], [], []), refresh: scheduler);
            var service = new PhysicalLineupService(catalog, topology, refresh: scheduler);
            var configuration = new StreamingConfiguration { PhysicalDevices = [blocked, healthy] };

            // Act
            var first = service.RefreshAsync(configuration, watchdog.Token);
            await committed.Task.WaitAsync(watchdog.Token);
            var joined = service.RefreshAsync(configuration, watchdog.Token);
            var fresh = await topology.ReadActiveAsync(watchdog.Token);
            await catalog.ApplyAsync(configuration with { PhysicalDevices = [replacementDefinition, healthy] }, watchdog.Token);
            release.TrySetResult();
            var results = await first;
            var joinedResults = await joined;

            // Assert
            Assert.Equal(healthy.Id, Assert.Single(fresh).Source.Id.PhysicalDeviceId);
            Assert.Same(results, joinedResults);
            Assert.False(Assert.Single(results, result => result.DeviceId == blocked.Id).Succeeded);
            Assert.True(Assert.Single(results, result => result.DeviceId == healthy.Id).Succeeded);
            Assert.Equal(healthy.Id, Assert.Single(await topology.ReadActiveAsync(watchdog.Token)).Source.Id.PhysicalDeviceId);
            await oldRuntime.Received(1).RefreshLineupAsync(Arg.Any<CancellationToken>());
            await healthyRuntime.Received(1).RefreshLineupAsync(Arg.Any<CancellationToken>());
        }
        finally
        {
            release.TrySetResult();
            SqliteConnection.ClearAllPools();
            root.Delete(recursive: true);
        }
    }

    /// <summary>Verifies a value created concurrently with cancellation is released instead of escaping admission ownership.</summary>
    [Fact]
    public async Task Scheduler_CanceledLateResult_ReleasesUnclaimedOwnership()
    {
        // Arrange
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var scheduler = new PhysicalDeviceRefreshScheduler();
        var released = false;

        // Act
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scheduler.RunAsync(_ =>
        {
            cancellation.Cancel();
            return Task.FromResult(new object());
        }, cancellation.Token, _ =>
        {
            released = true;
            return ValueTask.CompletedTask;
        }));

        // Assert
        Assert.True(released);
    }

    /// <summary>Verifies healthy observations publish while another device stalls, duplicate callers share work and one caller can cancel independently.</summary>
    [Fact]
    public async Task Status_BlockedDevice_DoesNotDelayHealthyOrDuplicateNetworkWork()
    {
        // Arrange
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        watchdog.CancelAfter(TimeSpan.FromSeconds(15));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(watchdog.Token);
        var blocked = Definition("Blocked", 0);
        var healthy = Definition("Healthy", 1);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockedRuntime = Runtime(blocked);
        var capability = (IPhysicalDeviceStatusCapability)blockedRuntime;
        capability.RefreshStatusAsync(Arg.Any<CancellationToken>()).Returns(async call =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(call.Arg<CancellationToken>());
            return new PhysicalDeviceStatusSnapshot { Device = blockedRuntime.Snapshot };
        });
        var healthyRuntime = Runtime(healthy);
        var scheduler = new PhysicalDeviceRefreshScheduler();
        var catalog = Catalog([blockedRuntime, healthyRuntime], scheduler);
        var service = new PhysicalDeviceStatusService(catalog, refresh: scheduler);
        service.StateChanged += () =>
        {
            if (service.Devices.Any(status => status.Device.Id == healthy.Id && status.StatusObservedAtUtc is not null))
            {
                observed.TrySetResult();
            }
        };
        var configuration = new StreamingConfiguration { PhysicalDevices = [blocked, healthy] };

        // Act
        var first = service.RefreshAsync(configuration, caller.Token);
        await started.Task.WaitAsync(watchdog.Token);
        var second = service.RefreshAsync(configuration, watchdog.Token);
        await caller.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await observed.Task.WaitAsync(watchdog.Token);

        // Assert
        Assert.False(second.IsCompleted);
        Assert.NotNull(Assert.Single(service.Devices, status => status.Device.Id == healthy.Id).StatusObservedAtUtc);
        await capability.Received(1).RefreshStatusAsync(Arg.Any<CancellationToken>());
        release.TrySetResult();
        await second;
        Assert.All(service.Devices, status => Assert.Null(status.Error));
    }

    /// <summary>Verifies a late old-runtime observation cannot overwrite the replacement topology generation.</summary>
    [Fact]
    public async Task Status_ReplacedRuntime_DiscardsLateResult()
    {
        // Arrange
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        watchdog.CancelAfter(TimeSpan.FromSeconds(15));
        var original = Definition("Original", 0);
        var replacement = original with { Name = "Replacement", Adapter = new AdapterConfiguration("refresh-test", JsonSerializer.SerializeToElement(new { generation = 2 })) };
        var oldRuntime = Runtime(original);
        var newRuntime = Runtime(replacement);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ((IPhysicalDeviceStatusCapability)oldRuntime).RefreshStatusAsync(Arg.Any<CancellationToken>()).Returns(async call =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(call.Arg<CancellationToken>());
            return new PhysicalDeviceStatusSnapshot { Device = oldRuntime.Snapshot };
        });
        var driver = Driver();
        driver.CreateAsync(Arg.Any<PhysicalDeviceId>(), Arg.Any<string>(), Arg.Any<AdapterConfiguration>(), Arg.Any<CancellationToken>())
            .Returns(call => ValueTask.FromResult(call.Arg<string>() == original.Name ? oldRuntime : newRuntime));
        var service = new PhysicalDeviceStatusService(new PhysicalDeviceCatalog(new StreamingAdapterRegistry([driver], [], [])));

        // Act
        var pending = service.RefreshAsync(new StreamingConfiguration { PhysicalDevices = [original] }, watchdog.Token);
        await entered.Task.WaitAsync(watchdog.Token);
        await service.RefreshAsync(new StreamingConfiguration { PhysicalDevices = [replacement] }, watchdog.Token);
        release.TrySetResult();
        await pending;

        // Assert
        var current = Assert.Single(service.Devices);
        Assert.Equal("Replacement", current.Device.Name);
        Assert.Equal("Replacement", current.Status?.Device.Name);
        Assert.NotNull(current.StatusObservedAtUtc);
        Assert.Null(current.Error);
    }

    /// <summary>Verifies timeout errors preserve the last successful observation and do not claim new successful freshness.</summary>
    [Fact]
    public async Task Status_Deadline_PreservesLastSuccessfulData()
    {
        // Arrange
        var definition = Definition("Device", 0);
        var runtime = Runtime(definition);
        var scheduler = new PhysicalDeviceRefreshScheduler(operationTimeoutSeconds: 1);
        var service = new PhysicalDeviceStatusService(Catalog([runtime], scheduler), refresh: scheduler);
        var configuration = new StreamingConfiguration { PhysicalDevices = [definition] };
        await service.RefreshAsync(configuration, TestContext.Current.CancellationToken);
        var before = Assert.Single(service.Devices);
        ((IPhysicalDeviceStatusCapability)runtime).RefreshStatusAsync(Arg.Any<CancellationToken>()).Returns(async call =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>());
            return new PhysicalDeviceStatusSnapshot { Device = runtime.Snapshot };
        });

        // Act
        await service.RefreshAsync(configuration, TestContext.Current.CancellationToken);

        // Assert
        var after = Assert.Single(service.Devices);
        Assert.Same(before.Status, after.Status);
        Assert.Equal(before.StatusObservedAtUtc, after.StatusObservedAtUtc);
        Assert.True(after.LastAttemptAtUtc > before.LastAttemptAtUtc);
        Assert.Contains("TimeoutException", after.Error, StringComparison.Ordinal);
    }

    /// <summary>Verifies shared admission bounds independent operation kinds rather than multiplying the allowance per service.</summary>
    [Fact]
    public async Task Scheduler_ConcurrentOperations_EnforcesSharedLimit()
    {
        // Arrange
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        watchdog.CancelAfter(TimeSpan.FromSeconds(15));
        var scheduler = new PhysicalDeviceRefreshScheduler(2);
        var admitted = 0;
        var active = 0;
        var peak = 0;
        var full = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<int> Operation(CancellationToken token)
        {
            var count = Interlocked.Increment(ref active);
            Interlocked.Increment(ref admitted);
            Interlocked.Exchange(ref peak, Math.Max(peak, count));
            if (count == 2)
            {
                full.TrySetResult();
            }
            await release.Task.WaitAsync(token);
            Interlocked.Decrement(ref active);
            return count;
        }

        // Act
        var tasks = Enumerable.Range(0, 6).Select(_ => scheduler.RunAsync(Operation, watchdog.Token)).ToArray();
        await full.Task.WaitAsync(watchdog.Token);

        // Assert
        Assert.Equal(2, Volatile.Read(ref admitted));
        Assert.Equal(2, Volatile.Read(ref active));
        release.TrySetResult();
        await Task.WhenAll(tasks);
        Assert.Equal(2, peak);
        Assert.Equal(6, admitted);
    }

    /// <summary>Verifies later information refresh merges current receiver observations instead of restoring stale receivers captured at request start.</summary>
    [Fact]
    public async Task Information_ConcurrentStatus_PreservesLatestReceiverObservation()
    {
        // Arrange
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        watchdog.CancelAfter(TimeSpan.FromSeconds(15));
        var definition = Definition("Device", 0);
        var runtime = Runtime(definition);
        var service = new PhysicalDeviceStatusService(Catalog([runtime], new PhysicalDeviceRefreshScheduler()));
        var configuration = new StreamingConfiguration { PhysicalDevices = [definition] };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ((IPhysicalDeviceInformationCapability)runtime).RefreshDeviceInformationAsync(Arg.Any<CancellationToken>()).Returns(async call =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(call.Arg<CancellationToken>());
            return new PhysicalDeviceStatusSnapshot { Device = runtime.Snapshot with { Name = "Information" } };
        });

        // Act
        var information = service.RefreshDeviceInformationAsync(configuration, watchdog.Token);
        await entered.Task.WaitAsync(watchdog.Token);
        await service.RefreshAsync(configuration, watchdog.Token);
        var receivers = Assert.Single(service.Devices).Status?.Receivers;
        Assert.Equal(7, Assert.Single(receivers!).Index);
        release.TrySetResult();
        await information;

        // Assert
        var current = Assert.Single(service.Devices);
        Assert.Same(receivers, current.Status?.Receivers);
        Assert.NotNull(current.StatusObservedAtUtc);
        Assert.NotNull(current.InformationObservedAtUtc);
    }

    private static PhysicalDeviceDefinition Definition(string name, int order) => new()
    {
        Id = PhysicalDeviceId.New(),
        Name = name,
        Order = order,
        Adapter = new AdapterConfiguration("refresh-test", JsonSerializer.SerializeToElement(new { generation = 1 }))
    };

    private static IPhysicalTunerDriver Driver()
    {
        var driver = Substitute.For<IPhysicalTunerDriver>();
        driver.Kind.Returns("refresh-test");
        driver.Validate(Arg.Any<AdapterConfiguration>()).Returns([]);
        return driver;
    }

    private static PhysicalDeviceCatalog Catalog(IReadOnlyList<IPhysicalTunerDevice> devices, PhysicalDeviceRefreshScheduler scheduler)
    {
        var driver = Driver();
        driver.CreateAsync(Arg.Any<PhysicalDeviceId>(), Arg.Any<string>(), Arg.Any<AdapterConfiguration>(), Arg.Any<CancellationToken>())
            .Returns(call => ValueTask.FromResult(devices.Single(device => device.Snapshot.Id == call.Arg<PhysicalDeviceId>())));
        return new PhysicalDeviceCatalog(new StreamingAdapterRegistry([driver], [], []), refresh: scheduler);
    }

    private static IPhysicalTunerDevice Runtime(PhysicalDeviceDefinition definition)
    {
        var runtime = Substitute.For<IPhysicalTunerDevice, IPhysicalDeviceStatusCapability, IPhysicalDeviceInformationCapability>();
        runtime.Snapshot.Returns(new PhysicalDeviceSnapshot { Id = definition.Id, Kind = "refresh-test", Name = definition.Name, Capacity = new PhysicalTunerCapacity(1) });
        ((IPhysicalDeviceStatusCapability)runtime).RefreshStatusAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new PhysicalDeviceStatusSnapshot { Device = runtime.Snapshot, Receivers = [new PhysicalReceiverStatus { Index = 7 }] }));
        return runtime;
    }

    private sealed class ContextFactory(string path, TaskCompletionSource committed) : IDbContextFactory<EpgDbContext>
    {
        /// <summary>Creates db context.</summary>
        /// <returns>The operation's result.</returns>
        public EpgDbContext CreateDbContext() => new SignalingContext(new DbContextOptionsBuilder<EpgDbContext>().UseSqlite($"Data Source={path}").Options, committed);
    }

    private sealed class SignalingContext(DbContextOptions<EpgDbContext> options, TaskCompletionSource committed) : EpgDbContext(options)
    {
        /// <inheritdoc />
        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            var result = await base.SaveChangesAsync(cancellationToken);
            committed.TrySetResult();
            return result;
        }
    }
}
