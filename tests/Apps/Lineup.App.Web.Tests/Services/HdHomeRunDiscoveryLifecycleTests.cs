using System.Text.Json;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Xml.Linq;
using Lineup.App.Web.Controllers;
using Lineup.Core.Common.Configuration;
using Lineup.Core.Common.Streaming;
using Lineup.Core.Settings;
using Lineup.Core.Extensibility.Streaming;
using Lineup.Plugin.SiliconDust.HDHomeRun.DiscoveryRuntime;
using Lineup.Plugin.SiliconDust.HDHomeRun.Device.Protocol;
using Lineup.Plugin.SiliconDust.HDHomeRun.VirtualTuner;
using Lineup.Core.VirtualOutputs.Protocol;
using Lineup.Core.VirtualOutputs.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Xunit;

namespace Lineup.App.Web.Tests.Services;

/// <summary>Verifies live physical capacity reconciles shared discovery without network sockets or hardware.</summary>
public sealed class HdHomeRunDiscoveryLifecycleTests
{
    /// <summary>Verifies discovery and management responses agree on primary, secondary, and dedicated device URLs.</summary>
    [Theory]
    [InlineData("http://lineup.example:8080", false, true)]
    [InlineData("https://lineup.example/", true, true)]
    [InlineData("https://lineup.example/tv", true, true)]
    [InlineData("http://lineup.example:8080/tv/", true, false)]
    public async Task Supervisor_MultipleHostingModes_AdvertisesDeviceConsistentProtocolUrls(string managementRoot, bool dedicatedFirst, bool advertisePrimary)
    {
        // Arrange
        var primarySource = PhysicalDeviceId.New();
        var secondarySource = PhysicalDeviceId.New();
        var primary = VirtualDevice("Primary", managementRoot, primarySource) with
        {
            Adapter = new AdapterConfiguration(HdHomeRunVirtualTunerDriver.AdapterKind, JsonSerializer.SerializeToElement(new HdHomeRunVirtualTunerConfiguration
            {
                DeviceId = HdHomeRunProxyIdentity.CreateDeviceId("primary"),
                AdvertisedBaseUrl = managementRoot,
                EnableNetworkAdvertisement = advertisePrimary
            }))
        };
        var secondary = VirtualDevice("Secondary", managementRoot, secondarySource);
        var dedicated = VirtualDevice("Dedicated", "http://unused-management.example/", primarySource) with
        {
            Hosting = new VirtualOutputHostingDefinition
            {
                WorkerPolicyKey = "discovery",
                Bindings =
                [
                    new VirtualOutputBindingDefinition
                    {
                        PublicBaseUri = "https://dedicated.example:8443/player"
                    }
                ]
            }
        };
        var disabled = VirtualDevice("Disabled", managementRoot, primarySource) with { Enabled = false };
        var unrelated = VirtualDevice("Unrelated", managementRoot, primarySource) with
        {
            Adapter = new AdapterConfiguration("unrelated", JsonSerializer.SerializeToElement(new { }))
        };
        var configuration = new StreamingConfiguration
        {
            VirtualDevices = dedicatedFirst ? [disabled, unrelated, dedicated, primary, secondary] : [disabled, unrelated, primary, secondary, dedicated],
            VirtualOutputWorkerPolicies = [new VirtualOutputWorkerPolicyDefinition { Key = "discovery", Name = "Discovery" }]
        };
        var settings = Substitute.For<ILineupSettingsService>();
        settings.Settings.Returns(new LineupSettings { Streaming = configuration });
        var resolver = Substitute.For<IPhysicalDeviceResolver>();
        IReadOnlyList<IPhysicalTunerDevice> physicalDevices = [Physical(primarySource, 2), Physical(secondarySource, 4)];
        resolver.Devices.Returns(physicalDevices);
        var driver = new HdHomeRunVirtualTunerDriver();
        var profiles = new HdHomeRunProxyProfileProvider(() => configuration.VirtualDevices, resolver, driver);
        var launches = new ConcurrentQueue<VirtualOutputWorkerBootstrap>();
        var launcher = Substitute.For<IVirtualOutputWorkerLauncher>();
        launcher.StartAsync(Arg.Any<VirtualOutputWorkerPackage>(), Arg.Any<VirtualOutputWorkerBootstrap>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                launches.Enqueue(call.Arg<VirtualOutputWorkerBootstrap>());
                return Task.FromResult<IVirtualOutputWorkerProcess>(new DiscoveryProcess());
            });
        using var authority = new VirtualOutputSessionAuthority();
        using var supervisor = CreateSupervisor(settings, resolver, launcher, authority);
        var controller = new HdHomeRunProxyController(profiles, settings, null!)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.Request.Scheme = Uri.UriSchemeHttp;
        controller.Request.Host = new HostString("internal", 8080);
        var normalizedRoot = $"{managementRoot.TrimEnd('/')}/";
        var expectedUris = new Dictionary<VirtualDeviceId, string>
        {
            [primary.Id] = normalizedRoot,
            [secondary.Id] = $"{normalizedRoot}hdhomerun/{driver.GetDeviceId(secondary)}/",
            [dedicated.Id] = "https://dedicated.example:8443/player/"
        };

        // Act
        await supervisor.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await WaitUntilAsync(() => !launches.IsEmpty);
            var advertisements = Assert.Single(launches).Advertisements;
            var resolvedProfiles = await profiles.GetProfilesAsync(TestContext.Current.CancellationToken);
            var primaryResponse = Assert.IsType<HdHomeRunProxyDiscoverResponse>(Assert.IsType<OkObjectResult>(await controller.Discover()).Value);

            // Assert
            Assert.Equal(driver.GetDeviceId(primary), primaryResponse.DeviceId);
            Assert.Equal(normalizedRoot.TrimEnd('/'), primaryResponse.BaseUrl);
            Assert.Equal(advertisePrimary ? 3 : 2, advertisements.Count);
            Assert.DoesNotContain(advertisements, advertisement => advertisement.VirtualDeviceId == disabled.Id || advertisement.VirtualDeviceId == unrelated.Id);
            Assert.True(Assert.Single(resolvedProfiles, profile => profile.VirtualDevice.Id == primary.Id).IsPrimary);
            Assert.False(Assert.Single(resolvedProfiles, profile => profile.VirtualDevice.Id == secondary.Id).IsPrimary);
            Assert.True(Assert.Single(resolvedProfiles, profile => profile.VirtualDevice.Id == dedicated.Id).IsPrimary);
            Assert.False((await profiles.FindProfileAsync(driver.GetDeviceId(secondary).ToLowerInvariant(), TestContext.Current.CancellationToken))!.IsPrimary);

            foreach (var advertisement in advertisements)
            {
                var expectedUri = expectedUris[advertisement.VirtualDeviceId];
                var metadata = advertisement.Metadata.Deserialize<HdHomeRunDiscoveryAdvertisementMetadata>()!;
                var advertisedDevice = new HdHomeRunAdvertisedDevice
                {
                    DeviceId = uint.Parse(metadata.DeviceId, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    DeviceAuth = metadata.DeviceAuth,
                    TunerCount = checked((byte)advertisement.TunerCount!.Value),
                    BaseUri = advertisement.PublicBaseUri
                };
                var request = new HDHomeRunPacketBuilder()
                    .AddTag(HDHomeRunTagType.DeviceId, HDHomeRunDeviceId.Wildcard)
                    .Build(HDHomeRunPacketType.DiscoverRequest);
                Assert.True(HdHomeRunDiscoveryProtocol.TryCreateReply(request, advertisedDevice, out var reply));
                var tags = ReadReply(reply!);
                var ssdpResponses = SsdpDiscoveryProtocol.CreateSearchResponses("M-SEARCH * HTTP/1.1\r\nMAN: \"ssdp:discover\"\r\nST: ssdp:all\r\n\r\n", advertisedDevice);

                Assert.Equal(expectedUri, advertisement.PublicBaseUri.AbsoluteUri);
                Assert.Equal(metadata.DeviceId, Convert.ToHexString(tags[HDHomeRunTagType.DeviceId]));
                Assert.Equal(expectedUri.TrimEnd('/'), HDHomeRunPacketReader.ReadString(tags[HDHomeRunTagType.BaseUrl]));
                Assert.Equal($"{expectedUri}lineup.json", HDHomeRunPacketReader.ReadString(tags[HDHomeRunTagType.LineupUrl]));
                Assert.Equal(metadata.DeviceAuth, HDHomeRunPacketReader.ReadString(tags[HDHomeRunTagType.DeviceAuthStr]));
                Assert.Equal(2, ssdpResponses.Count);
                Assert.All(ssdpResponses, response => Assert.Contains($"LOCATION: {expectedUri}device.xml\r\n", response, StringComparison.Ordinal));
                Assert.Contains($"LOCATION: {expectedUri}device.xml\r\n",
                    SsdpDiscoveryProtocol.FormatNotification(SsdpDiscoveryProtocol.RootDeviceTarget, advertisedDevice, alive: true), StringComparison.Ordinal);

                if (advertisement.VirtualDeviceId == dedicated.Id)
                {
                    continue;
                }
                var profile = Assert.Single(resolvedProfiles, item => item.VirtualDevice.Id == advertisement.VirtualDeviceId);
                Assert.True(profile.TryGetAdvertisedBaseUri(out var profileUri));
                Assert.Equal(expectedUri, profileUri!.AbsoluteUri);
                var discover = Assert.IsType<HdHomeRunProxyDiscoverResponse>(Assert.IsType<OkObjectResult>(await controller.Discover(metadata.DeviceId)).Value);
                var description = XDocument.Parse(Assert.IsType<ContentResult>(await controller.DeviceDescription(metadata.DeviceId)).Content!);
                Assert.Equal(metadata.DeviceId, discover.DeviceId);
                Assert.Equal(expectedUri.TrimEnd('/'), discover.BaseUrl);
                Assert.Equal($"{expectedUri}lineup.json", discover.LineupUrl);
                Assert.Equal(expectedUri.TrimEnd('/'), description.Root!.Elements().Single(element => element.Name.LocalName == "URLBase").Value);
                Assert.Equal(metadata.DeviceId, description.Descendants().Single(element => element.Name.LocalName == "serialNumber").Value);
                Assert.Equal(advertisement.VirtualDeviceId == primary.Id ? 2 : 4, advertisement.TunerCount);
            }
        }
        finally
        {
            await supervisor.StopAsync(TestContext.Current.CancellationToken);
        }
    }

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

    private static VirtualDeviceDefinition VirtualDevice(string name, string managementRoot, PhysicalDeviceId source) => new()
    {
        Id = VirtualDeviceId.New(),
        Name = name,
        Subscription = new PhysicalDeviceSubscription { Mode = PhysicalDeviceSubscriptionMode.Selected, SelectedDeviceIds = new HashSet<PhysicalDeviceId> { source } },
        Adapter = new AdapterConfiguration(HdHomeRunVirtualTunerDriver.AdapterKind, JsonSerializer.SerializeToElement(new HdHomeRunVirtualTunerConfiguration
        {
            DeviceId = HdHomeRunProxyIdentity.CreateDeviceId(name),
            AdvertisedBaseUrl = managementRoot,
            EnableNetworkAdvertisement = true
        }))
    };

    private static HdHomeRunDiscoveryWorkerSupervisor CreateSupervisor(ILineupSettingsService settings, IPhysicalDeviceResolver resolver, IVirtualOutputWorkerLauncher launcher, IVirtualOutputSessionAuthority authority)
    {
        var catalog = Substitute.For<IVirtualOutputWorkerCatalog>();
        catalog.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new VirtualOutputWorkerPackage
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
        });
        return new HdHomeRunDiscoveryWorkerSupervisor(settings, resolver, new HdHomeRunVirtualTunerDriver(), new EnabledHdHomeRunDiscoveryPolicy(),
            new VirtualOutputRuntimeOptions { WorkerPackageRoot = AppContext.BaseDirectory, IpcEndpoint = new VirtualOutputIpcEndpoint { Address = "test-discovery" } },
            catalog, launcher, authority, NullLogger<HdHomeRunDiscoveryWorkerSupervisor>.Instance, new RefreshClock());
    }

    private static Dictionary<HDHomeRunTagType, byte[]> ReadReply(byte[] reply)
    {
        var reader = new HDHomeRunPacketReader(reply);
        var tags = new Dictionary<HDHomeRunTagType, byte[]>();
        while (reader.TryReadTag(out var tag, out var value))
        {
            tags[tag] = value.ToArray();
        }
        Assert.True(reader.IsValid);
        Assert.False(reader.HasError);
        Assert.True(HDHomeRunPacketBuilder.VerifyCrc32(reply));
        return tags;
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
