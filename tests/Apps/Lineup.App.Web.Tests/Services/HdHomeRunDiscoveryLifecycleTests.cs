using System.Text.Json;
using System.Collections.Concurrent;
using System.Diagnostics;
using Lineup.Core.Common.Configuration;
using Lineup.Core.Common.Streaming;
using Lineup.Core.Settings;
using Lineup.Core.Extensibility.Streaming;
using Lineup.Plugin.SiliconDust.HDHomeRun.DiscoveryRuntime;
using Lineup.Plugin.SiliconDust.HDHomeRun.VirtualTuner;
using Lineup.Core.VirtualOutputs.Protocol;
using Lineup.Core.VirtualOutputs.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.App.Web.Tests.Services;

/// <summary>Verifies live physical capacity reconciles shared discovery without network sockets or hardware.</summary>
public sealed class HdHomeRunDiscoveryLifecycleTests
{
    /// <summary>Verifies discovery reconciles live capacity without settings events and ignores unchanged effective advertisements.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Supervisor_PhysicalCapacityChanges_ReplacesOnlyChangedAdvertisements(bool selectedSubscription)
    {
        // Arrange
        var id = PhysicalDeviceId.New();
        var unrelatedId = PhysicalDeviceId.New();
        var settings = Substitute.For<ILineupSettingsService>();
        settings.Settings.Returns(new LineupSettings
        {
            Streaming = new StreamingConfiguration
            {
                VirtualDevices =
                [
                    new VirtualDeviceDefinition
                    {
                        Id = VirtualDeviceId.New(),
                        Name = "Discovery",
                        Subscription = selectedSubscription
                            ? new PhysicalDeviceSubscription { Mode = PhysicalDeviceSubscriptionMode.Selected, SelectedDeviceIds = new HashSet<PhysicalDeviceId> { id } }
                            : new PhysicalDeviceSubscription(),
                        Adapter = new AdapterConfiguration(HdHomeRunVirtualTunerDriver.AdapterKind, JsonSerializer.SerializeToElement(new HdHomeRunVirtualTunerConfiguration
                        {
                            AdvertisedBaseUrl = "http://localhost:8080/",
                            EnableNetworkAdvertisement = true
                        }))
                    }
                ]
            }
        });
        var resolver = Substitute.For<IPhysicalDeviceResolver>();
        IReadOnlyList<IPhysicalTunerDevice> physical = [];
        var reads = 0;
        resolver.Devices.Returns(_ =>
        {
            Interlocked.Increment(ref reads);
            return physical;
        });
        var package = new VirtualOutputWorkerPackage
        {
            PackageRoot = AppContext.BaseDirectory,
            Manifest = new VirtualOutputWorkerManifest
            {
                WorkerId = "hdhomerun-discovery",
                Version = "3.0.0",
                Publisher = "Lineup",
                ExecutablePath = "unused.dll",
                ExecutableSha256 = new string('0', 64),
                MinimumLineupVersion = "3.0.0",
                SettingsDescriptor = new AdapterSettingsDescriptor { AdapterKind = "hdhomerun-discovery" }
            }
        };
        var catalog = Substitute.For<IVirtualOutputWorkerCatalog>();
        catalog.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(package);
        var launches = new ConcurrentQueue<(VirtualOutputWorkerBootstrap Bootstrap, DiscoveryProcess Process)>();
        var launcher = Substitute.For<IVirtualOutputWorkerLauncher>();
        launcher.StartAsync(Arg.Any<VirtualOutputWorkerPackage>(), Arg.Any<VirtualOutputWorkerBootstrap>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                Assert.All(launches, launch => Assert.True(launch.Process.Completion.IsCompleted));
                var process = new DiscoveryProcess();
                launches.Enqueue((call.Arg<VirtualOutputWorkerBootstrap>(), process));
                return Task.FromResult<IVirtualOutputWorkerProcess>(process);
            });
        var clock = new RefreshClock();
        using var authority = new VirtualOutputSessionAuthority();
        using var supervisor = new HdHomeRunDiscoveryWorkerSupervisor(settings, resolver, new HdHomeRunVirtualTunerDriver(), new EnabledHdHomeRunDiscoveryPolicy(),
            new VirtualOutputRuntimeOptions { WorkerPackageRoot = AppContext.BaseDirectory, IpcEndpoint = new VirtualOutputIpcEndpoint { Address = "test-discovery" } },
            catalog, launcher, authority, NullLogger<HdHomeRunDiscoveryWorkerSupervisor>.Instance, clock);
        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await WaitUntilAsync(() => launches.Count == 1);
            Assert.Equal(0, Assert.Single(launches).Bootstrap.Advertisements.Single().TunerCount);

            // Act
            physical = [Physical(id, 4)];
            clock.Tick();
            await WaitUntilAsync(() => launches.Count == 2);

            // Assert
            Assert.Equal(4, launches.Last().Bootstrap.Advertisements.Single().TunerCount);
            Assert.True(launches.First().Process.Completion.IsCompleted);

            // Act
            var previousReads = Volatile.Read(ref reads);
            clock.Tick();
            await WaitUntilAsync(() => Volatile.Read(ref reads) > previousReads);
            physical = [Physical(id, 2), Physical(unrelatedId, 3)];
            clock.Tick();
            await WaitUntilAsync(() => launches.Count == 3);

            // Assert
            Assert.Equal(3, launches.Count);
            Assert.Equal(selectedSubscription ? 2 : 5, launches.Last().Bootstrap.Advertisements.Single().TunerCount);
            Assert.InRange(clock.Period, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(5));

            // Act
            previousReads = Volatile.Read(ref reads);
            physical = [Physical(id, 2), Physical(unrelatedId, 8)];
            clock.Tick();
            await WaitUntilAsync(() => Volatile.Read(ref reads) > previousReads);
            if (!selectedSubscription)
            {
                await WaitUntilAsync(() => launches.Count == 4);
                Assert.Equal(10, launches.Last().Bootstrap.Advertisements.Single().TunerCount);
            }
            physical = [];
            clock.Tick();
            await WaitUntilAsync(() => launches.Count == (selectedSubscription ? 4 : 5));

            // Assert
            Assert.Equal(0, launches.Last().Bootstrap.Advertisements.Single().TunerCount);
        }
        finally
        {
            await supervisor.StopAsync(TestContext.Current.CancellationToken);
        }

        // Assert
        Assert.All(launches, launch => Assert.True(launch.Process.Completion.IsCompleted));
        Assert.Equal(selectedSubscription ? 4 : 5, launches.Count);
        Assert.True(clock.Disposed);
    }

    private static IPhysicalTunerDevice Physical(PhysicalDeviceId id, int capacity)
    {
        var device = Substitute.For<IPhysicalTunerDevice>();
        device.Snapshot.Returns(new PhysicalDeviceSnapshot { Id = id, Name = "Source", Kind = "test", Capacity = new PhysicalTunerCapacity(capacity) });
        return device;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(5))
            {
                throw new TimeoutException("Discovery did not reconcile the changed physical capacity.");
            }
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private sealed class RefreshClock : TimeProvider
    {
        private Action? _tick;

        /// <summary>Gets the reconciliation timer period.</summary>
        internal TimeSpan Period { get; private set; }

        /// <summary>Gets whether the reconciliation timer was disposed.</summary>
        internal bool Disposed { get; private set; }

        /// <inheritdoc />
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Period = period;
            _tick = () => callback(state);
            var timer = Substitute.For<ITimer>();
            timer.When(item => item.Dispose()).Do(_ => Disposed = true);
            return timer;
        }

        /// <summary>Signals one deterministic physical-capacity reconciliation interval.</summary>
        internal void Tick() => _tick?.Invoke();
    }

    private sealed class DiscoveryProcess : IVirtualOutputWorkerProcess
    {
        private readonly TaskCompletionSource<int> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <inheritdoc />
        public int ProcessId => 1234;

        /// <inheritdoc />
        public Task<int> Completion => _completion.Task;

        /// <inheritdoc />
        public Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            _completion.TrySetResult(0);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            _completion.TrySetResult(0);
            return ValueTask.CompletedTask;
        }
    }
}
