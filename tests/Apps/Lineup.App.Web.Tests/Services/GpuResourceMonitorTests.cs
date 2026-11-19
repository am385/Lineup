using System.Text.Json;
using Bunit;
using Lineup.App.Web.Components.Dashboard;
using Lineup.App.Web.Services;
using Lineup.Core.VirtualOutputs.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.JSInterop;
using NSubstitute;
using Xunit;

namespace Lineup.App.Web.Tests.Services;

/// <summary>Verifies device-wide GPU metrics stay demand-controlled, bounded and distinct from process attribution.</summary>
[Collection(nameof(DashboardResourceMonitorTests))]
public sealed class GpuResourceMonitorTests
{
    private const string ResourceStateStorageKey = "lineup-dashboard-resource-sections-v1";

    /// <summary>Verifies Windows sums processes on one physical engine but never sums independent engines or adapters.</summary>
    [Fact]
    public void WindowsCounters_PhysicalEngines_AggregateWithoutDoubleCounting()
    {
        // Arrange
        (string, double)[] counters =
        [
            ("pid_1_luid_0x0_0x1_phys_0_eng_0_engtype_3D", 25),
            ("pid_2_luid_0x0_0x1_phys_0_eng_0_engtype_3D", 35),
            ("pid_1_luid_0x0_0x1_phys_0_eng_1_engtype_Video Decode 1", 45),
            ("pid_1_luid_0x0_0x1_phys_0_eng_2_engtype_Video Encode", 0),
            ("pid_1_luid_0x0_0x1_phys_0_eng_3_engtype_Video Codec 0", 10),
            ("pid_3_luid_0x0_0x2_phys_0_eng_0_engtype_VideoEncode", 75)
        ];

        // Act
        var devices = WindowsGpuResourceSource.Aggregate(counters);

        // Assert
        Assert.Equal(2, devices.Count);
        Assert.Equal("windows:luid_0x0_0x1_phys_0", devices[0].Id);
        Assert.Equal(60, devices[0].Utilization);
        Assert.Equal(45, devices[0].Decode);
        Assert.Equal(0, devices[0].Encode);
        Assert.Equal(10, devices[0].Video);
        Assert.Equal(75, devices[1].Utilization);
        Assert.Equal(75, devices[1].Encode);
        Assert.Null(devices[1].Decode);
        Assert.Null(devices[1].Video);
    }

    /// <summary>Verifies malformed/unavailable observations cannot become fabricated idle GPU measurements.</summary>
    [Fact]
    public void WindowsCounters_InvalidAndOverlappingValues_RejectInvalidAndCapPhysicalOccupancy()
    {
        // Arrange
        const string instance = "pid_1_luid_0x0_0x1_phys_0_eng_0_engtype_3D";
        (string, double)[] counters = [(instance, 80), (instance, 50), (instance, double.NaN), (instance, -1), ("unknown", 99)];

        // Act
        var device = Assert.Single(WindowsGpuResourceSource.Aggregate(counters));

        // Assert
        Assert.Equal(100, device.Utilization);
        Assert.Null(device.Encode);
        Assert.Null(device.Decode);
    }

    /// <summary>Verifies DXGI model names match numeric LUIDs, not enumeration order, without merging identical GPU models or physical nodes.</summary>
    [Fact]
    public void WindowsCounters_AdapterNames_MatchLuidAndPreserveDistinctDeviceIdentities()
    {
        // Arrange
        (string, double)[] counters =
        [
            ("pid_1_luid_0x00000000_0x00000001_phys_0_eng_0_engtype_3D", 10),
            ("pid_1_luid_0x00000000_0x00000002_phys_0_eng_0_engtype_3D", 20),
            ("pid_1_luid_0x00000000_0x00000002_phys_1_eng_0_engtype_3D", 30),
            ("pid_1_luid_0x00000001_0x00000001_phys_0_eng_0_engtype_3D", 40)
        ];
        IReadOnlyDictionary<ulong, string> names = new Dictionary<ulong, string>
        {
            [0x100000001] = " Integrated GPU ",
            [2] = "Discrete GPU",
            [1] = "Discrete GPU"
        };

        // Act
        var devices = WindowsGpuResourceSource.Aggregate(counters, names);

        // Assert
        Assert.Equal(new[] { "Discrete GPU", "Discrete GPU", "Discrete GPU", "Integrated GPU" }, devices.Select(device => device.Name));
        Assert.Equal(new[]
        {
            "windows:luid_0x00000000_0x00000001_phys_0",
            "windows:luid_0x00000000_0x00000002_phys_0",
            "windows:luid_0x00000000_0x00000002_phys_1",
            "windows:luid_0x00000001_0x00000001_phys_0"
        }, devices.Select(device => device.Id));
        Assert.Equal(new double?[] { 10, 20, 30, 40 }, devices.Select(device => device.Utilization));
    }

    /// <summary>Verifies missing or blank names retain an identifiable counter label rather than a mismatched model.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void WindowsCounters_AdapterNameUnavailable_PreservesCounterFallback(string? name)
    {
        // Arrange
        (string, double)[] counters = [("pid_1_luid_0x0_0x1_phys_0_eng_0_engtype_3D", 25)];
        var names = new Dictionary<ulong, string> { [2] = "Unrelated GPU" };
        if (name is not null) names[1] = name;

        // Act
        var device = Assert.Single(WindowsGpuResourceSource.Aggregate(counters, names));

        // Assert
        Assert.Equal("GPU 1 (luid_0x0_0x1_phys_0)", device.Name);
        Assert.Equal("windows:luid_0x0_0x1_phys_0", device.Id);
        Assert.Equal(25, device.Utilization);
    }

    /// <summary>Verifies driver percentages cannot silently parse invalid content or non-finite/out-of-range activity.</summary>
    [Theory]
    [InlineData("-1")]
    [InlineData("101")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("unavailable")]
    public void AmdCounter_InvalidPercentage_ReportsFailure(string text)
    {
        // Arrange
        var input = text;

        // Act
        var exception = Assert.Throws<GpuCounterException>(() => AmdGpuResourceSource.ParsePercentage(input));

        // Assert
        Assert.Contains("invalid", exception.Message);
    }

    /// <summary>Verifies the real sysfs reader distinguishes measured idle, unsupported video metrics and missing driver telemetry.</summary>
    [Fact]
    public void AmdCounter_SysfsReading_DistinguishesZeroFromUnavailable()
    {
        // Arrange
        var card = Path.Combine(Path.GetTempPath(), $"lineup-gpu-{Guid.NewGuid():N}", "card0");
        var device = Directory.CreateDirectory(Path.Combine(card, "device")).FullName;
        using var source = new AmdGpuResourceSource(card);
        try
        {
            File.WriteAllText(Path.Combine(device, "gpu_busy_percent"), "0\n");

            // Act
            var idle = Assert.Single(source.Read().Devices);
            File.Delete(Path.Combine(device, "gpu_busy_percent"));
            var unavailable = Assert.Single(source.Read().Devices);

            // Assert
            Assert.Equal(0, idle.Utilization);
            Assert.Null(idle.Encode);
            Assert.Null(idle.Decode);
            Assert.Null(idle.Video);
            Assert.Null(unavailable.Utilization);
            Assert.Equal("unsupported", unavailable.UnavailableReason);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(card)!, true);
        }
    }

    /// <summary>Verifies Intel activity uses scheduled nanoseconds and rejects reset/unscheduled intervals.</summary>
    [Theory]
    [InlineData(10UL, 100UL, 30UL, 200UL, 20d)]
    [InlineData(10UL, 100UL, 30UL, 150UL, 40d)]
    [InlineData(10UL, 100UL, 10UL, 200UL, 0d)]
    [InlineData(10UL, 100UL, 200UL, 200UL, 100d)]
    [InlineData(10UL, 100UL, 5UL, 200UL, null)]
    [InlineData(10UL, 100UL, 30UL, 100UL, null)]
    public void IntelCounter_BusyDelta_UsesScheduledTime(ulong previous, ulong previousTime, ulong current, ulong currentTime, double? expected)
    {
        // Arrange
        var before = previous;

        // Act
        var actual = IntelGpuResourceSource.Busy(before, previousTime, current, currentTime);

        // Assert
        Assert.Equal(expected, actual);
    }

    /// <summary>Verifies one denied or broken NVIDIA adapter never hides readable adapters, and known identities recover without stale measurements.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void NvidiaDevices_AdapterFailure_PreservesAccessibleMetricsAndKnownIdentities(bool observedBeforeFailure, bool denied)
    {
        // Arrange
        var fail = !observedBeforeFailure;
        var logger = Substitute.For<ILogger>();
        using var nvidia = new NvidiaGpuResourceSource(logger);
        GpuResourceSample ReadDevice(uint index)
        {
            if (index == 1 && fail)
            {
                if (denied) throw new UnauthorizedAccessException("Adapter monitoring denied.");
                throw new GpuCounterException("Adapter query failed.");
            }
            return new($"nvidia:gpu-{index}", $"GPU {index}", index == 0 ? 0 : 40, 20, 30, null);
        }
        var provider = Substitute.For<IGpuResourceSource>();
        provider.Read().Returns(_ => nvidia.ReadDevices(3, ReadDevice));
        var unsupported = Substitute.For<IGpuResourceSource>();
        unsupported.Read().Returns(new GpuResourceSnapshot([], "unsupported"));
        using var source = new NativeGpuResourceSource(NullLogger<NativeGpuResourceSource>.Instance, () => [provider, unsupported]);
        if (observedBeforeFailure) source.Read();

        // Act
        fail = true;
        var first = source.Read();
        var repeated = source.Read();

        // Assert
        var reason = denied ? "permission_denied" : "read_failed";
        foreach (var snapshot in new[] { first, repeated })
        {
            Assert.Equal(reason, snapshot.UnavailableReason);
            Assert.Equal(observedBeforeFailure ? 3 : 2, snapshot.Devices.Count);
            var idle = Assert.Single(snapshot.Devices, device => device.Id == "nvidia:gpu-0");
            Assert.Equal(0, idle.Utilization);
            Assert.Equal(20, idle.Encode);
            Assert.Equal(30, idle.Decode);
            Assert.Null(idle.UnavailableReason);
            Assert.Equal(40, Assert.Single(snapshot.Devices, device => device.Id == "nvidia:gpu-2").Utilization);
            if (observedBeforeFailure)
            {
                var unavailable = Assert.Single(snapshot.Devices, device => device.Id == "nvidia:gpu-1");
                Assert.Equal("GPU 1", unavailable.Name);
                Assert.Null(unavailable.Utilization);
                Assert.Null(unavailable.Encode);
                Assert.Null(unavailable.Decode);
                Assert.Null(unavailable.Video);
                Assert.Equal(reason, unavailable.UnavailableReason);
            }
        }
        Assert.Single(logger.ReceivedCalls(), call => call.GetMethodInfo().Name == "Log" && call.GetArguments()[0] is LogLevel.Warning);

        // Act
        fail = false;
        var recovered = source.Read();
        fail = true;
        source.Read();

        // Assert
        Assert.Null(recovered.UnavailableReason);
        Assert.Equal(3, recovered.Devices.Count);
        Assert.All(recovered.Devices, device => Assert.Null(device.UnavailableReason));
        Assert.Equal(40, Assert.Single(recovered.Devices, device => device.Id == "nvidia:gpu-1").Utilization);
        Assert.Equal(2, logger.ReceivedCalls().Count(call => call.GetMethodInfo().Name == "Log" && call.GetArguments()[0] is LogLevel.Warning));
    }

    /// <summary>Verifies inaccessible providers are explicit, warning-suppressed and isolated from working devices.</summary>
    [Fact]
    public void NativeSources_PermissionFailure_KeepsOtherDevicesAndWarnsOnce()
    {
        // Arrange
        var denied = Substitute.For<IGpuResourceSource>();
        denied.Read().Returns(_ => throw new UnauthorizedAccessException("Denied by host policy."));
        var available = Substitute.For<IGpuResourceSource>();
        available.Read().Returns(new GpuResourceSnapshot([new("gpu", "Available GPU", 10, 20, null, null)]));
        var logger = Substitute.For<ILogger<NativeGpuResourceSource>>();
        using var source = new NativeGpuResourceSource(logger, () => [denied, available]);

        // Act
        var first = source.Read();
        var second = source.Read();
        source.ResetBaseline();

        // Assert
        Assert.Equal("permission_denied", first.UnavailableReason);
        Assert.Equal(10, Assert.Single(first.Devices).Utilization);
        Assert.Equal("permission_denied", second.UnavailableReason);
        Assert.Single(logger.ReceivedCalls(), call => call.GetMethodInfo().Name == "Log");
        denied.Received(1).ResetBaseline();
        available.Received(1).ResetBaseline();
    }

    /// <summary>Verifies discovery and native counters remain lazy, and GPU failures are not success-shaped empty data.</summary>
    [Fact]
    public void NativeSources_ReadFailure_IsExplicitAndDiscoveryIsLazy()
    {
        // Arrange
        var discoveries = 0;
        var broken = Substitute.For<IGpuResourceSource>();
        broken.Read().Returns(_ => throw new GpuCounterException("Driver failed."));
        using var source = new NativeGpuResourceSource(NullLogger<NativeGpuResourceSource>.Instance, () => { discoveries++; return [broken]; });

        // Act
        source.ResetBaseline();
        var beforeRead = discoveries;
        var first = source.Read();
        var second = source.Read();

        // Assert
        Assert.Equal(0, beforeRead);
        Assert.Equal(1, discoveries);
        Assert.Equal("read_failed", first.UnavailableReason);
        Assert.Equal("read_failed", second.UnavailableReason);
        Assert.Empty(first.Devices);
    }

    /// <summary>Verifies denied device discovery is identified as a monitoring permission problem, not an idle GPU.</summary>
    [Fact]
    public void NativeSources_DiscoveryDenied_ExplainsMissingCounters()
    {
        // Arrange
        using var source = new NativeGpuResourceSource(NullLogger<NativeGpuResourceSource>.Instance,
            () => throw new UnauthorizedAccessException("Device telemetry is not accessible."));

        // Act
        var snapshot = source.Read();

        // Assert
        Assert.Empty(snapshot.Devices);
        Assert.Equal("permission_denied", snapshot.UnavailableReason);
    }

    /// <summary>Verifies one failing provider retains its known adapter as unavailable without hiding it or contaminating another adapter's readings.</summary>
    [Fact]
    public void NativeSources_TransientFailure_PreservesDeviceIdentityWithoutStaleValues()
    {
        // Arrange
        var fail = false;
        var intermittent = Substitute.For<IGpuResourceSource>();
        intermittent.Read().Returns(_ => fail
            ? throw new GpuCounterException("Driver failed.")
            : new GpuResourceSnapshot([new("one", "GPU One", 40, 50, 60, 70)]));
        var stable = Substitute.For<IGpuResourceSource>();
        stable.Read().Returns(new GpuResourceSnapshot([new("two", "GPU Two", 25, null, null, null)]));
        using var source = new NativeGpuResourceSource(NullLogger<NativeGpuResourceSource>.Instance, () => [intermittent, stable]);
        source.Read();

        // Act
        fail = true;
        var failed = source.Read();

        // Assert
        Assert.Equal(2, failed.Devices.Count);
        var unavailable = Assert.Single(failed.Devices, device => device.Id == "one");
        Assert.Null(unavailable.Utilization);
        Assert.Null(unavailable.Encode);
        Assert.Null(unavailable.Decode);
        Assert.Null(unavailable.Video);
        Assert.Equal("read_failed", unavailable.UnavailableReason);
        var available = Assert.Single(failed.Devices, device => device.Id == "two");
        Assert.Equal(25, available.Utilization);
        Assert.Null(available.UnavailableReason);
    }

    /// <summary>Verifies visibility-only collection, cache-only reads, baseline disposal and bounded shared GPU history.</summary>
    [Fact]
    public void Monitor_GpuDemandAndHistory_FollowExistingVisibilityPolicy()
    {
        // Arrange
        var clock = new FakeTimeProvider();
        var settings = Substitute.For<IWebSettingsService>();
        settings.Settings.Returns(new WebSettings { ResourceHistoryMinutes = 1 });
        var gpu = Substitute.For<IGpuResourceSource>();
        gpu.Read().Returns(new GpuResourceSnapshot([new("gpu", "Local GPU", 40, 60, 20, null)]));
        var workers = Substitute.For<IVirtualOutputRuntimeStatus>();
        using var monitor = new DashboardResourceMonitor(settings, workers, NullLogger<DashboardResourceMonitor>.Instance, new(), clock, gpu);
        monitor.Collect();
        var before = monitor.GetSnapshot();
        var dashboard = Guid.NewGuid();

        // Act
        monitor.SetVisible(dashboard, true);
        monitor.Collect();
        var sample = monitor.GetSnapshot();
        var cached = monitor.GetSnapshot();
        monitor.SetVisible(dashboard, false);
        monitor.Collect();
        var paused = monitor.GetSnapshot();
        clock.Advance(TimeSpan.FromMinutes(2));
        var trimmed = monitor.GetSnapshot();

        // Assert
        Assert.False(before.Collecting);
        Assert.Same(sample.History, cached.History);
        Assert.Equal(40, Assert.Single(sample.History).Gpus.Single().Utilization);
        Assert.Equal(0, sample.History.Single().Total.CpuPercent);
        Assert.False(paused.Collecting);
        Assert.Equal("paused", paused.Gpu!.UnavailableReason);
        Assert.Empty(paused.History.Last().Gpus);
        Assert.Empty(trimmed.History);
        gpu.Received(1).Read();
        gpu.Received(1).ResetBaseline();
    }

    /// <summary>Verifies a blocked driver read or shutdown cannot hold the dashboard's cached-read lock.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Monitor_SlowGpuDriver_DoesNotBlockCachedSnapshots(bool pausing)
    {
        // Arrange
        var settings = Substitute.For<IWebSettingsService>();
        settings.Settings.Returns(new WebSettings { MonitorResourcesOnlyWhenVisible = false });
        var gpu = Substitute.For<IGpuResourceSource>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        void Block()
        {
            entered.TrySetResult();
            release.Wait();
        }
        gpu.Read().Returns(_ =>
        {
            if (!pausing) Block();
            return new GpuResourceSnapshot([]);
        });
        gpu.When(value => value.ResetBaseline()).Do(_ => Block());
        using var monitor = new DashboardResourceMonitor(settings, Substitute.For<IVirtualOutputRuntimeStatus>(),
            NullLogger<DashboardResourceMonitor>.Instance, new(), gpu: gpu);
        if (pausing)
        {
            monitor.Collect();
            settings.Settings.MonitorResourcesOnlyWhenVisible = true;
        }
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var collection = Task.Run(monitor.Collect, cancellationToken);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

            // Act
            var snapshot = await Task.Run(monitor.GetSnapshot, cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

            // Assert
            Assert.NotNull(snapshot);
            Assert.False(collection.IsCompleted);
        }
        finally
        {
            release.Set();
            await collection;
        }
    }

    /// <summary>Verifies GPU cards reuse the fixed time axis, separate devices/engines and show gaps instead of fictitious logical cores or zeroes.</summary>
    [Fact]
    public async Task GpuChart_DeviceHistory_ShowsIndependentMetricsAndUnavailableGaps()
    {
        // Arrange
        await using var context = new LineupWebBunitContext();
        var now = DateTimeOffset.UtcNow;
        ResourceHistoryPoint Point(int offset, double? activity) => new(now.AddSeconds(offset), new(200, 1024), new(200, 1024), new(0, 0), new(0, 0), true)
        {
            Gpus = [new("one", "GPU One", activity, null, 0, 12), new("two", "GPU Two", 90, 50, null, null)]
        };
        ResourceHistoryPoint[] history = [Point(0, 25), Point(2, null), Point(4, 50)];

        // Act
        var component = context.Render<ResourceHistoryChart>(parameters => parameters
            .Add(value => value.History, history).Add(value => value.GpuId, "one").Add(value => value.GpuName, "GPU One"));

        // Assert
        Assert.Equal("GPU · GPU One", component.Find("h6").TextContent);
        Assert.Equal(new[] { "0 %", "25 %", "50 %", "75 %", "100 %" }, component.FindAll("[data-resource-y-tick]").Select(tick => tick.TextContent.Trim()));
        Assert.Equal(new[] { "5m", "3m 45s", "2m 30s", "1m 15s", "0s" }, component.FindAll("[data-resource-x-tick]").Select(tick => tick.TextContent.Trim()));
        Assert.Equal(new[] { "50 %", "N/A %", "0 %", "12 %" }, component.FindAll("dd").Select(value => value.TextContent.Trim()));
        Assert.DoesNotContain("logical cores", component.Markup);
        Assert.Equal(2, component.FindAll("polyline[stroke='#0d6efd']").Count);
        Assert.Empty(component.FindAll("polyline[stroke='#198754']"));

        // Act
        component.Render(parameters => parameters.Add(value => value.GpuId, "two").Add(value => value.GpuName, "GPU Two").Add(value => value.HistoryMinutes, 1));

        // Assert
        Assert.Equal("90 %", component.Find("dd").TextContent.Trim());
        Assert.Equal(new[] { "1m", "45s", "30s", "15s", "0s" }, component.FindAll("[data-resource-x-tick]").Select(tick => tick.TextContent.Trim()));
    }

    /// <summary>Verifies the Dashboard includes a per-device GPU card and explicitly labels readings as whole-device rather than stream costs.</summary>
    [Fact]
    public async Task ResourcesPanel_GpuReadings_AddsDeviceCardWithHonestScope()
    {
        // Arrange
        await using var context = new LineupWebBunitContext();
        var settings = Substitute.For<IWebSettingsService>();
        settings.Settings.Returns(new WebSettings { MonitorResourcesOnlyWhenVisible = false });
        var gpu = Substitute.For<IGpuResourceSource>();
        var available = true;
        gpu.Read().Returns(_ => available
            ? new GpuResourceSnapshot(WindowsGpuResourceSource.Aggregate(
                [("pid_1_luid_0x0_0x1_phys_0_eng_0_engtype_VideoEncode", 75)],
                new Dictionary<ulong, string> { [1] = "Review GPU" }))
            : new GpuResourceSnapshot([], "read_failed"));
        using var monitor = new DashboardResourceMonitor(settings, Substitute.For<IVirtualOutputRuntimeStatus>(),
            NullLogger<DashboardResourceMonitor>.Instance, new(), gpu: gpu);
        monitor.Collect();
        ConfigureResourcePanel(context, monitor);
        var component = context.Render<DashboardResourcesPanel>();

        // Act
        await component.InvokeAsync(() => component.Instance.ObserveVisibility(true));

        // Assert
        Assert.Equal(3, component.FindAll(".resource-history").Count);
        Assert.Contains("whole devices", component.Markup);
        Assert.Contains("not Lineup-only", component.Markup);
        var card = component.Find("[data-gpu-device='windows:luid_0x0_0x1_phys_0']");
        Assert.Contains("Review GPU", card.TextContent);
        Assert.DoesNotContain("luid_", card.QuerySelector("h6")!.TextContent);
        Assert.Contains("75 %", card.TextContent);
        Assert.Contains("N/A", card.TextContent);
        Assert.Null(card.QuerySelector("dd span"));
        Assert.Contains("GPU: Review GPU: 75%", component.Find("#dashboardResources > .card-header small").TextContent);
        Assert.Equal("GPU activity: 75%", card.QuerySelector(".card-header small")!.TextContent);

        // Act
        await card.QuerySelector(".card-header button")!.ClickAsync(new());

        // Assert
        Assert.True(card.QuerySelector(".card-body")!.HasAttribute("hidden"));
        Assert.Null(card.QuerySelector(".card-header small")!.Closest("[hidden]"));
        Assert.All(component.FindAll(".resource-history:not([data-gpu-device]) > .card-body"), body => Assert.False(body.HasAttribute("hidden")));
        gpu.Received(1).Read();

        // Act
        available = false;
        monitor.Collect();
        await component.InvokeAsync(() => component.Instance.ObserveVisibility(true));

        // Assert
        var unavailable = component.Find("[data-gpu-device='windows:luid_0x0_0x1_phys_0']");
        Assert.All(unavailable.QuerySelectorAll("dd"), value => Assert.Equal("N/A %", value.TextContent.Trim()));
        Assert.Contains("could not be read", unavailable.QuerySelector("[role='status']")!.TextContent);
        Assert.True(unavailable.QuerySelector(".card-body")!.HasAttribute("hidden"));
        Assert.Equal("false", unavailable.QuerySelector(".card-header button")!.GetAttribute("aria-expanded"));
        Assert.Contains("GPU: Review GPU: N/A", component.Find("#dashboardResources > .card-header small").TextContent);
        Assert.Equal("GPU activity: N/A", unavailable.QuerySelector(".card-header small")!.TextContent);
        Assert.Null(unavailable.QuerySelector(".card-header small")!.Closest("[hidden]"));
        Assert.True(monitor.GetSnapshot().History.Last().Complete);
    }

    /// <summary>Verifies the always-visible GPU summary keeps adapters separate, reports measured idle and rejects stale percentages during failures or pauses.</summary>
    [Fact]
    public async Task ResourcesPanel_GpuSummary_ShowsSeparateCurrentReadingsWhenCollapsed()
    {
        // Arrange
        await using var context = new LineupWebBunitContext();
        var settings = Substitute.For<IWebSettingsService>();
        settings.Settings.Returns(new WebSettings { MonitorResourcesOnlyWhenVisible = false });
        var gpu = Substitute.For<IGpuResourceSource>();
        var readings = new GpuResourceSnapshot(
            [new("one", "GPU One", 25, 80, 90, null), new("two", "GPU Two", 75, null, null, null), new("three", "GPU Three", null, 50, null, null)]);
        gpu.Read().Returns(_ => readings);
        var clock = new FakeTimeProvider();
        using var monitor = new DashboardResourceMonitor(settings, Substitute.For<IVirtualOutputRuntimeStatus>(),
            NullLogger<DashboardResourceMonitor>.Instance, new(), clock, gpu);
        monitor.Collect();
        ConfigureResourcePanel(context, monitor);
        var component = context.Render<DashboardResourcesPanel>();
        await component.InvokeAsync(() => component.Instance.ObserveVisibility(true));
        var bodyIds = component.FindAll("#resourceCharts .card-header button").Select(button => button.GetAttribute("aria-controls")).ToArray();

        // Act
        await component.Find("[data-gpu-device='one'] .card-header button").ClickAsync(new());

        // Assert
        Assert.Equal(5, bodyIds.Distinct().Count());
        Assert.True(component.Find("[data-gpu-device='one'] > .card-body").HasAttribute("hidden"));
        Assert.False(component.Find("[data-gpu-device='two'] > .card-body").HasAttribute("hidden"));
        Assert.Contains("GPU: GPU One: 25% · GPU Two: 75% · GPU Three: N/A", component.Find("#dashboardResources > .card-header small").TextContent);
        Assert.Equal(new[] { "GPU activity: 25%", "GPU activity: 75%", "GPU activity: N/A" },
            component.FindAll("[data-gpu-device] .card-header small").Select(summary => summary.TextContent));
        Assert.Null(component.Find("[data-gpu-device='one'] .card-header small").Closest("[hidden]"));

        // Act
        await component.Find("#toggleResourcesSection").ClickAsync(new());
        readings = new([new("one", "GPU One", 0, 80, 90, null), new("two", "GPU Two", null, null, null, null, "read_failed"), new("three", "GPU Three", 12.5, 50, null, null)]);
        clock.Advance(TimeSpan.FromSeconds(2));
        monitor.Collect();
        await component.InvokeAsync(() => component.Instance.ObserveVisibility(true));

        // Assert
        Assert.Empty(component.FindAll("#resourceCharts"));
        Assert.Contains("GPU: GPU One: 0% · GPU Two: N/A · GPU Three: 12.5%", component.Find("#dashboardResources > .card-header small").TextContent);
        Assert.Null(component.Find("#dashboardResources > .card-header small").Closest("[hidden]"));

        // Act
        await component.Find("#toggleResourcesSection").ClickAsync(new());

        // Assert
        Assert.Equal(new[] { "GPU activity: 0%", "GPU activity: N/A", "GPU activity: 12.5%" },
            component.FindAll("[data-gpu-device] .card-header small").Select(summary => summary.TextContent));
        Assert.True(component.Find("[data-gpu-device='one'] > .card-body").HasAttribute("hidden"));

        // Act
        settings.Settings.MonitorResourcesOnlyWhenVisible = true;
        await component.InvokeAsync(() => component.Instance.ObserveVisibility(false));
        monitor.Collect();
        await component.InvokeAsync(() => component.Instance.ObserveVisibility(true));

        // Assert
        Assert.Contains("GPU: GPU One: N/A · GPU Two: N/A · GPU Three: N/A", component.Find("#dashboardResources > .card-header small").TextContent);
        Assert.All(component.FindAll("[data-gpu-device] .card-header small"), summary =>
        {
            Assert.Equal("GPU activity: N/A", summary.TextContent);
            Assert.Null(summary.Closest("[hidden]"));
        });
        gpu.Received(2).Read();
    }

    /// <summary>Verifies resource preferences survive parent minimization and navigation, matching GPU identities despite renaming or reordering.</summary>
    [Fact]
    public async Task ResourcesPanel_CollapsePreferences_RestoreAcrossNavigationByDeviceIdentity()
    {
        // Arrange
        await using var context = new LineupWebBunitContext();
        var settings = Substitute.For<IWebSettingsService>();
        settings.Settings.Returns(new WebSettings { MonitorResourcesOnlyWhenVisible = false });
        var gpu = Substitute.For<IGpuResourceSource>();
        var readings = new GpuResourceSnapshot([new("one", "First GPU", 25, null, null, null), new("two", "Second GPU", 75, null, null, null)]);
        gpu.Read().Returns(_ => readings);
        using var monitor = new DashboardResourceMonitor(settings, Substitute.For<IVirtualOutputRuntimeStatus>(), NullLogger<DashboardResourceMonitor>.Instance, new(), gpu: gpu);
        monitor.Collect();
        ConfigureResourcePanel(context, monitor);
        var component = context.Render<DashboardResourcesPanel>();
        Assert.All(component.FindAll("#resourceCharts .card-header button"), button => Assert.Equal("true", button.GetAttribute("aria-expanded")));

        // Act
        await component.Find("#resourceCharts > section[aria-label='GPU'] button").ClickAsync(new());
        await component.InvokeAsync(() => component.Instance.ObserveVisibility(true));
        await component.Find("#resourceCharts > section[aria-label='CPU'] button").ClickAsync(new());
        await component.Find("#resourceCharts > section[aria-label='RAM'] button").ClickAsync(new());
        await component.Find("[data-gpu-device='one'] .card-header button").ClickAsync(new());
        await component.Find("#toggleResourcesSection").ClickAsync(new());
        await component.Find("#toggleResourcesSection").ClickAsync(new());

        // Assert
        Assert.Equal("false", component.Find("#resourceCharts > section[aria-label='CPU'] button").GetAttribute("aria-expanded"));
        Assert.Equal("false", component.Find("#resourceCharts > section[aria-label='RAM'] button").GetAttribute("aria-expanded"));
        Assert.Equal("false", component.Find("[data-gpu-device='one'] .card-header button").GetAttribute("aria-expanded"));
        Assert.Equal("true", component.Find("[data-gpu-device='two'] .card-header button").GetAttribute("aria-expanded"));
        var saved = Assert.IsType<string>(context.JSInterop.Invocations.Last(invocation => invocation.Identifier == "localStorage.setItem").Arguments[1]);
        var values = JsonSerializer.Deserialize<Dictionary<string, bool>>(saved)!;
        Assert.All(new[] { "cpu", "ram", "gpu:one", "gpu-unavailable" }, key => Assert.False(values[key]));
        Assert.All(context.JSInterop.Invocations.Where(invocation => invocation.Identifier == "localStorage.setItem"),
            invocation => Assert.Equal(ResourceStateStorageKey, invocation.Arguments[0]));

        // Act
        await context.DisposeComponentsAsync();
        readings = new([new("two", "Renamed Second GPU", 0, null, null, null), new("one", "Renamed First GPU", 50, null, null, null)]);
        monitor.Collect();
        await using var returnedContext = new LineupWebBunitContext();
        ConfigureResourcePanel(returnedContext, monitor, saved);
        var returned = returnedContext.Render<DashboardResourcesPanel>();

        // Assert
        Assert.Equal("false", returned.Find("#resourceCharts > section[aria-label='GPU'] button").GetAttribute("aria-expanded"));

        // Act
        await returned.InvokeAsync(() => returned.Instance.ObserveVisibility(true));

        // Assert
        Assert.True(returned.Find("#resourceCharts > section[aria-label='CPU'] > .card-body").HasAttribute("hidden"));
        Assert.True(returned.Find("#resourceCharts > section[aria-label='RAM'] > .card-body").HasAttribute("hidden"));
        Assert.True(returned.Find("[data-gpu-device='one'] > .card-body").HasAttribute("hidden"));
        Assert.False(returned.Find("[data-gpu-device='two'] > .card-body").HasAttribute("hidden"));
        Assert.Contains("Renamed First GPU", returned.Find("[data-gpu-device='one'] h6").TextContent);

        // Act
        await returned.Find("[data-gpu-device='one'] .card-header button").ClickAsync(new());

        // Assert
        var expanded = Assert.Single(returnedContext.JSInterop.Invocations, invocation => invocation.Identifier == "localStorage.setItem");
        var updated = JsonSerializer.Deserialize<Dictionary<string, bool>>(Assert.IsType<string>(expanded.Arguments[1]))!;
        Assert.True(updated["gpu:one"]);
        Assert.False(updated["cpu"]);
        Assert.False(updated["ram"]);
    }

    /// <summary>Verifies malformed saved preferences are logged, default to expanded and can be replaced by a working toggle.</summary>
    [Theory]
    [InlineData("{invalid")]
    [InlineData("{\"cpu\":\"invalid\"}")]
    public async Task ResourcesPanel_InvalidSavedPreferences_KeepControlsUsable(string saved)
    {
        // Arrange
        await using var context = new LineupWebBunitContext();
        var settings = Substitute.For<IWebSettingsService>();
        settings.Settings.Returns(new WebSettings());
        using var monitor = new DashboardResourceMonitor(settings, Substitute.For<IVirtualOutputRuntimeStatus>(), NullLogger<DashboardResourceMonitor>.Instance, new());
        ConfigureResourcePanel(context, monitor, saved);
        var logger = Substitute.For<ILogger<DashboardResourcesPanel>>();
        context.Services.AddSingleton(logger);

        // Act
        var component = context.Render<DashboardResourcesPanel>();

        // Assert
        Assert.All(component.FindAll("#resourceCharts .card-header button"), button => Assert.Equal("true", button.GetAttribute("aria-expanded")));
        Assert.Single(logger.ReceivedCalls(), call => call.GetMethodInfo().Name == "Log" && call.GetArguments()[0] is LogLevel.Warning);

        // Act
        await component.Find("#resourceCharts > section[aria-label='CPU'] button").ClickAsync(new());

        // Assert
        Assert.True(component.Find("#resourceCharts > section[aria-label='CPU'] > .card-body").HasAttribute("hidden"));
        var write = Assert.Single(context.JSInterop.Invocations, invocation => invocation.Identifier == "localStorage.setItem");
        Assert.False(JsonSerializer.Deserialize<Dictionary<string, bool>>(Assert.IsType<string>(write.Arguments[1]))!["cpu"]);
    }

    /// <summary>Verifies browser-storage access failures are reported without disabling in-memory minimization.</summary>
    [Fact]
    public async Task ResourcesPanel_BrowserStorageUnavailable_LogsAndKeepsControlsUsable()
    {
        // Arrange
        await using var context = new LineupWebBunitContext();
        var settings = Substitute.For<IWebSettingsService>();
        settings.Settings.Returns(new WebSettings());
        using var monitor = new DashboardResourceMonitor(settings, Substitute.For<IVirtualOutputRuntimeStatus>(), NullLogger<DashboardResourceMonitor>.Instance, new());
        ConfigureResourcePanel(context, monitor);
        var store = Substitute.For<IBrowserDataStore>();
        store.ReadAsync<Dictionary<string, bool>>(ResourceStateStorageKey, Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromException<Dictionary<string, bool>?>(new JSException("Storage denied.")));
        store.WriteAsync(ResourceStateStorageKey, Arg.Any<Dictionary<string, bool>>(), Arg.Any<CancellationToken>()).Returns(ValueTask.FromException(new JSException("Storage denied.")));
        context.Services.AddSingleton(store);
        var logger = Substitute.For<ILogger<DashboardResourcesPanel>>();
        context.Services.AddSingleton(logger);
        var component = context.Render<DashboardResourcesPanel>();

        // Act
        await component.Find("#resourceCharts > section[aria-label='CPU'] button").ClickAsync(new());

        // Assert
        Assert.True(component.Find("#resourceCharts > section[aria-label='CPU'] > .card-body").HasAttribute("hidden"));
        Assert.Equal(2, logger.ReceivedCalls().Count(call => call.GetMethodInfo().Name == "Log" && call.GetArguments()[0] is LogLevel.Warning));
    }

    /// <summary>Verifies a delayed restore cannot overwrite a newer click or discard another card's saved preference.</summary>
    [Fact]
    public async Task ResourcesPanel_RestoreInFlight_MergesSavedStateWithoutOverwritingNewerClick()
    {
        // Arrange
        await using var context = new LineupWebBunitContext();
        var settings = Substitute.For<IWebSettingsService>();
        settings.Settings.Returns(new WebSettings());
        using var monitor = new DashboardResourceMonitor(settings, Substitute.For<IVirtualOutputRuntimeStatus>(), NullLogger<DashboardResourceMonitor>.Instance, new());
        ConfigureResourcePanel(context, monitor);
        var restore = new TaskCompletionSource<Dictionary<string, bool>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = Substitute.For<IBrowserDataStore>();
        store.ReadAsync<Dictionary<string, bool>>(ResourceStateStorageKey, Arg.Any<CancellationToken>()).Returns(new ValueTask<Dictionary<string, bool>?>(restore.Task));
        context.Services.AddSingleton(store);
        var component = context.Render<DashboardResourcesPanel>();

        // Act
        var click = component.Find("#resourceCharts > section[aria-label='CPU'] button").ClickAsync(new());
        try
        {
            component.WaitForAssertion(() =>
                Assert.True(component.Find("#resourceCharts > section[aria-label='CPU'] > .card-body").HasAttribute("hidden")),
                timeout: LineupWebBunitContext.WaitTimeout);
            Assert.False(click.IsCompleted);
            restore.SetResult(new() { ["cpu"] = true, ["ram"] = false });
            await click;

            // Assert
            Assert.True(component.Find("#resourceCharts > section[aria-label='CPU'] > .card-body").HasAttribute("hidden"));
            Assert.True(component.Find("#resourceCharts > section[aria-label='RAM'] > .card-body").HasAttribute("hidden"));
            await store.Received(1).WriteAsync(ResourceStateStorageKey, Arg.Is<Dictionary<string, bool>>(state => !state["cpu"] && !state["ram"]), Arg.Any<CancellationToken>());
        }
        finally
        {
            restore.TrySetResult([]);
            await click;
        }
    }

    /// <summary>Verifies navigation drains queued writes and a pending restore without losing any final browser-local choices.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResourcesPanel_NavigationDuringPendingStorage_PersistsFinalPreferences(bool restoring)
    {
        // Arrange
        await using var context = new LineupWebBunitContext();
        var settings = Substitute.For<IWebSettingsService>();
        settings.Settings.Returns(new WebSettings());
        using var monitor = new DashboardResourceMonitor(settings, Substitute.For<IVirtualOutputRuntimeStatus>(), NullLogger<DashboardResourceMonitor>.Instance, new());
        ConfigureResourcePanel(context, monitor, configureStorage: false);
        const string saved = "{\"cpu\":true,\"ram\":true,\"gpu-unavailable\":false}";
        var restore = context.JSInterop.Setup<string?>("localStorage.getItem", ResourceStateStorageKey);
        if (!restoring) restore.SetResult(saved);
        var write = context.JSInterop.SetupVoid("localStorage.setItem", _ => true);
        var component = context.Render<DashboardResourcesPanel>();
        Task first = Task.CompletedTask;
        Task second = Task.CompletedTask;
        Task disposal = Task.CompletedTask;
        string? persisted = null;
        try
        {
            // Act
            first = component.Find("#resourceCharts > section[aria-label='CPU'] button").ClickAsync(new());
            component.WaitForAssertion(() =>
                Assert.True(component.Find("#resourceCharts > section[aria-label='CPU'] > .card-body").HasAttribute("hidden")),
                timeout: LineupWebBunitContext.WaitTimeout);
            if (!restoring)
            {
                Assert.Single(context.JSInterop.Invocations, invocation => invocation.Identifier == "localStorage.setItem");
            }
            second = component.Find("#resourceCharts > section[aria-label='RAM'] button").ClickAsync(new());
            component.WaitForAssertion(() =>
                Assert.True(component.Find("#resourceCharts > section[aria-label='RAM'] > .card-body").HasAttribute("hidden")),
                timeout: LineupWebBunitContext.WaitTimeout);
            disposal = context.DisposeComponentsAsync();

            // Assert
            Assert.False(disposal.IsCompleted);

            // Act
            if (restoring) restore.SetResult(saved);
            write.SetVoidResult();
            await Task.WhenAll(first, second, disposal).WaitAsync(LineupWebBunitContext.WaitTimeout, Xunit.TestContext.Current.CancellationToken);
            var writes = context.JSInterop.Invocations.Where(invocation => invocation.Identifier == "localStorage.setItem").ToArray();

            // Assert
            Assert.NotEmpty(writes);
            persisted = Assert.IsType<string>(writes.Last().Arguments[1]);
            var values = JsonSerializer.Deserialize<Dictionary<string, bool>>(persisted)!;
            Assert.All(new[] { "cpu", "ram", "gpu-unavailable" }, key => Assert.False(values[key]));
        }
        finally
        {
            if (restoring) restore.SetResult(saved);
            write.SetVoidResult();
            await Task.WhenAll(first, second, disposal).WaitAsync(LineupWebBunitContext.WaitTimeout, CancellationToken.None);
        }

        // Act
        await using var returnedContext = new LineupWebBunitContext();
        ConfigureResourcePanel(returnedContext, monitor, persisted);
        var returned = returnedContext.Render<DashboardResourcesPanel>();

        // Assert
        var buttons = returned.FindAll("#resourceCharts > section > .card-header button");
        Assert.Equal(3, buttons.Count);
        Assert.All(buttons, button => Assert.Equal("false", button.GetAttribute("aria-expanded")));
    }

    private static void ConfigureResourcePanel(LineupWebBunitContext context, DashboardResourceMonitor monitor, string? saved = null, bool configureStorage = true)
    {
        context.Services.AddLogging();
        context.Services.AddSingleton(monitor);
        context.Services.AddScoped<IBrowserDataStore, BrowserDataStore>();
        if (configureStorage)
        {
            context.JSInterop.Setup<string?>("localStorage.getItem", ResourceStateStorageKey).SetResult(saved);
            context.JSInterop.SetupVoid("localStorage.setItem", _ => true).SetVoidResult();
        }
        context.JSInterop.SetupVoid("lineupResources.start", _ => true).SetVoidResult();
        context.JSInterop.SetupVoid("lineupResources.stop", _ => true).SetVoidResult();
    }
}
