using System.Globalization;
using Bunit;
using Lineup.App.Web.Components.Dashboard;
using Lineup.App.Web.Components.Streaming;
using Lineup.App.Web.Services;
using Lineup.Core.VirtualOutputs.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace Lineup.App.Web.Tests.Services;

/// <summary>Verifies resource measurements, bounded demand and truthful dashboard presentation.</summary>
[Collection(nameof(DashboardResourceMonitorTests))]
public sealed class DashboardResourceMonitorTests
{
    /// <summary>Verifies the platform adapter reads the original process without unavailable or fabricated resident counters.</summary>
    [Fact]
    public void ProcessCounters_CurrentOwnedProcess_ReadsNativeCpuAndResidentMemory()
    {
        // Arrange
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var registry = new ProcessResourceRegistry();
        var id = registry.Register(process.Id, "management", process.StartTime.ToUniversalTime().Ticks);

        // Act
        registry.Attach(id);
        var first = Assert.Single(registry.Sample());
        var second = Assert.Single(registry.Sample());

        // Assert
        Assert.True(first.ResidentBytes > 0);
        Assert.True(second.CpuPercent >= 0);
        Assert.Null(second.UnavailableReason);
        registry.Unregister(id);
    }
    /// <summary>Verifies a reused PID cannot be attached when its creation identity differs from the original owner.</summary>
    [Fact]
    public void ProcessCounters_ChangedCreationIdentity_RejectsReusedPid()
    {
        // Arrange
        var counter = new Counter { StartTimeUtcTicks = 2 };
        var registry = new ProcessResourceRegistry(_ => counter);
        var id = registry.Register(123, "output", 1);
        var warnings = new List<string>();
        registry.ObservationFailed += warnings.Add;

        // Act
        registry.Attach(id);
        var sample = Assert.Single(registry.Sample());

        // Assert
        Assert.True(counter.Disposed);
        Assert.Equal(0, counter.Reads);
        Assert.Null(sample.CpuPercent);
        Assert.Equal("process_unavailable", sample.UnavailableReason);
        Assert.Single(warnings);
        registry.Unregister(id);
    }

    /// <summary>Verifies short-lived processes leave an explicit CPU coverage gap instead of disappearing into a complete total.</summary>
    [Fact]
    public void ProcessCounters_ProcessEndsBetweenTicks_MarksOnlyAffectedIntervalIncomplete()
    {
        // Arrange
        var clock = new FakeTimeProvider();
        var counter = new Counter();
        var registry = new ProcessResourceRegistry(_ => counter, clock);
        var host = registry.Register(123, "management");
        registry.Attach(host);
        var settings = Substitute.For<IWebSettingsService>();
        settings.Settings.Returns(new WebSettings { MonitorResourcesOnlyWhenVisible = false });
        var workers = Substitute.For<IVirtualOutputRuntimeStatus>();
        workers.GetSnapshots().Returns([]);
        using var monitor = new DashboardResourceMonitor(settings, workers, NullLogger<DashboardResourceMonitor>.Instance, registry, clock);
        monitor.Collect();

        // Act
        var shortLived = registry.Register(456, "output");
        registry.Unregister(shortLived);
        clock.Advance(TimeSpan.FromSeconds(2));
        monitor.Collect();
        var gap = monitor.GetSnapshot().History.Last();
        clock.Advance(TimeSpan.FromSeconds(2));
        monitor.Collect();
        var recovered = monitor.GetSnapshot().History.Last();

        // Assert
        Assert.False(gap.Complete);
        Assert.Null(gap.Total.CpuPercent);
        Assert.Equal(1024, gap.Total.ResidentBytes);
        Assert.True(recovered.Complete);
        Assert.Equal(0, recovered.Total.CpuPercent);
        registry.Unregister(host);
    }

    /// <summary>Verifies worker age and generation boundaries cannot retain current-looking data or resurrect an ended owner.</summary>
    [Fact]
    public void WorkerReports_StaleOrReplacedGeneration_MakesCoverageUnavailable()
    {
        // Arrange
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var settings = Substitute.For<IWebSettingsService>();
        settings.Settings.Returns(new WebSettings { MonitorResourcesOnlyWhenVisible = false });
        var workers = new VirtualOutputRuntimeStatus();
        var device = VirtualDeviceId.New();
        workers.Set(new() { VirtualDeviceId = device, Generation = 1, State = VirtualOutputRuntimeState.Healthy });
        using var monitor = new DashboardResourceMonitor(settings, workers, NullLogger<DashboardResourceMonitor>.Instance, new(), clock);
        var sample = new ProcessResourceSample(Guid.NewGuid().ToString("N"), "worker", 123, 20, 1024, clock.GetUtcNow(), null);
        monitor.Collect();
        WorkerResourceReports.Update($"{device}:1", [sample]);

        // Act
        monitor.Collect();
        var fresh = monitor.GetSnapshot();
        clock.Advance(TimeSpan.FromSeconds(7));
        monitor.Collect();
        var stale = monitor.GetSnapshot();
        workers.Set(new() { VirtualDeviceId = device, Generation = 2, State = VirtualOutputRuntimeState.Healthy });
        WorkerResourceReports.Update($"{device}:1", [sample]);
        monitor.Collect();
        var replaced = monitor.GetSnapshot();

        // Assert
        Assert.Equal(20, fresh.History.Last().Total.CpuPercent);
        Assert.True(fresh.History.Last().Complete);
        Assert.Equal("stale", Assert.Single(stale.Processes).UnavailableReason);
        Assert.Null(stale.History.Last().Total.CpuPercent);
        Assert.Empty(replaced.Processes);
        Assert.False(replaced.History.Last().Complete);
        Assert.Empty(WorkerResourceReports.Read(new HashSet<string> { $"{device}:2" }));
        WorkerResourceReports.SetRequested(false);
    }

    /// <summary>Verifies excessive resident counters become unavailable rather than overflowing and stopping collection.</summary>
    [Fact]
    public void WorkerReports_ResidentSumOverflows_KeepsMonitoringAliveWithIncompleteTotal()
    {
        // Arrange
        var settings = Substitute.For<IWebSettingsService>();
        settings.Settings.Returns(new WebSettings { MonitorResourcesOnlyWhenVisible = false });
        var workers = new VirtualOutputRuntimeStatus();
        var device = VirtualDeviceId.New();
        workers.Set(new() { VirtualDeviceId = device, Generation = 1, State = VirtualOutputRuntimeState.Healthy });
        using var monitor = new DashboardResourceMonitor(settings, workers, NullLogger<DashboardResourceMonitor>.Instance, new());
        monitor.Collect();
        WorkerResourceReports.Update($"{device}:1",
        [
            new(Guid.NewGuid().ToString("N"), "worker", 123, 10, long.MaxValue, DateTimeOffset.UtcNow, null),
            new(Guid.NewGuid().ToString("N"), "output", 456, 20, long.MaxValue, DateTimeOffset.UtcNow, null)
        ]);

        // Act
        monitor.Collect();
        var snapshot = monitor.GetSnapshot();

        // Assert
        Assert.True(snapshot.Collecting);
        Assert.Equal(30, snapshot.History.Last().Total.CpuPercent);
        Assert.Null(snapshot.History.Last().Total.ResidentBytes);
        Assert.False(snapshot.History.Last().Complete);
        WorkerResourceReports.SetRequested(false);
    }
    /// <summary>Verifies CPU normalization and pause boundaries use elapsed counter deltas rather than machine capacity.</summary>
    [Theory]
    [InlineData(0.2, 10)]
    [InlineData(4, 200)]
    public void ProcessCounters_ElapsedDeltas_ReportLogicalCorePercent(double seconds, double expected)
    {
        // Arrange
        var clock = new FakeTimeProvider();
        var counter = new Counter();
        var registry = new ProcessResourceRegistry(_ => counter, clock);
        var id = registry.Register(123, "output");
        registry.Attach(id);
        var baseline = registry.Sample().Single();

        // Act
        clock.Advance(TimeSpan.FromSeconds(2));
        counter.Cpu = TimeSpan.FromSeconds(seconds);
        var measured = registry.Sample().Single();
        registry.ResetBaselines();
        clock.Advance(TimeSpan.FromMinutes(1));
        counter.Cpu += TimeSpan.FromSeconds(20);
        var resumed = registry.Sample().Single();

        // Assert
        Assert.Null(baseline.CpuPercent);
        Assert.Equal(expected, measured.CpuPercent);
        Assert.Equal(1024, measured.ResidentBytes);
        Assert.Null(resumed.CpuPercent);
        Assert.Equal("warming_up", resumed.UnavailableReason);
        registry.Unregister(id);
        Assert.True(counter.Disposed);
        Assert.Empty(registry.Sample());
    }

    /// <summary>Verifies counter failure is unavailable rather than a zero-valued successful observation.</summary>
    [Fact]
    public void ProcessCounters_ExitedOriginal_NeverReopensReusedPid()
    {
        // Arrange
        var clock = new FakeTimeProvider();
        var counter = new Counter();
        var opens = 0;
        var registry = new ProcessResourceRegistry(_ => { opens++; return counter; }, clock);
        var id = registry.Register(123, "source");
        registry.Attach(id);
        registry.Sample();
        counter.Failed = true;
        var warnings = new List<string>();
        registry.ObservationFailed += warnings.Add;

        // Act
        clock.Advance(TimeSpan.FromSeconds(2));
        var first = registry.Sample().Single();
        registry.Sample();
        registry.Attach(id);

        // Assert
        Assert.Equal(1, opens);
        Assert.Single(warnings);
        Assert.Null(first.CpuPercent);
        Assert.Null(first.ResidentBytes);
        Assert.Equal("process_unavailable", first.UnavailableReason);
        registry.Unregister(id);
    }

    /// <summary>Verifies multiple dashboards share one sample and expired visibility stops all process reads.</summary>
    [Fact]
    public void VisibleDashboards_ExpiredInterest_StopsSamplingAndResetsBaseline()
    {
        // Arrange
        var clock = new FakeTimeProvider();
        var counter = new Counter();
        var registry = new ProcessResourceRegistry(_ => counter, clock);
        var id = registry.Register(123, "management");
        registry.Attach(id);
        var settings = Substitute.For<IWebSettingsService>();
        settings.Settings.Returns(new WebSettings());
        var workers = Substitute.For<IVirtualOutputRuntimeStatus>();
        workers.GetSnapshots().Returns([]);
        using var monitor = new DashboardResourceMonitor(settings, workers, NullLogger<DashboardResourceMonitor>.Instance, registry, clock);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        // Act
        monitor.Collect();
        var readsWhilePaused = counter.Reads;
        monitor.SetVisible(first, true);
        monitor.SetVisible(second, true);
        monitor.Collect();
        clock.Advance(TimeSpan.FromSeconds(2));
        counter.Cpu = TimeSpan.FromSeconds(1);
        monitor.Collect();
        var active = monitor.GetSnapshot();
        monitor.SetVisible(first, false);
        clock.Advance(TimeSpan.FromSeconds(8));
        monitor.Collect();
        var readsAtExpiry = counter.Reads;
        monitor.Collect();
        var paused = monitor.GetSnapshot();

        // Assert
        Assert.Equal(0, readsWhilePaused);
        Assert.Equal(2, readsAtExpiry);
        Assert.Equal(readsAtExpiry, counter.Reads);
        Assert.Single(active.Processes);
        Assert.Equal(50, active.History.Last().Total.CpuPercent);
        Assert.False(paused.Collecting);
        Assert.Empty(paused.Processes);
        Assert.Null(paused.History.Last().Total.CpuPercent);
        registry.Unregister(id);
    }

    /// <summary>Verifies continuous collection and hot history changes retain a fixed maximum number of aggregate points.</summary>
    [Theory]
    [InlineData(1, 30)]
    [InlineData(5, 150)]
    [InlineData(60, 1800)]
    public void History_ContinuousCollection_IsTimeAndCountBounded(int minutes, int expected)
    {
        // Arrange
        var clock = new FakeTimeProvider();
        var settings = Substitute.For<IWebSettingsService>();
        var values = new WebSettings { MonitorResourcesOnlyWhenVisible = false, ResourceHistoryMinutes = minutes };
        settings.Settings.Returns(values);
        var workers = Substitute.For<IVirtualOutputRuntimeStatus>();
        workers.GetSnapshots().Returns([]);
        using var monitor = new DashboardResourceMonitor(settings, workers, NullLogger<DashboardResourceMonitor>.Instance, new(), clock);

        // Act
        for (var index = 0; index < 1805; index++)
        {
            monitor.Collect();
            clock.Advance(TimeSpan.FromSeconds(2));
        }
        var history = monitor.GetSnapshot().History;
        values.ResourceHistoryMinutes = 1;
        var reduced = monitor.GetSnapshot().History;

        // Assert
        Assert.InRange(history.Count, expected - 1, expected);
        Assert.True(history.All(point => point.AtUtc > clock.GetUtcNow().AddMinutes(-minutes)));
        Assert.InRange(reduced.Count, 29, 30);
    }

    /// <summary>Verifies stage selection presents the same shared resource once instead of allocating CPU per viewer.</summary>
    [Fact]
    public async Task StageDetails_SharedProcess_ShowsWholeProcessCostAndUnavailableState()
    {
        // Arrange
        await using var context = new LineupWebBunitContext();
        var id = Guid.NewGuid().ToString("N");
        var stage = new StreamingFlowPathStage(StreamingFlowStageKind.OutputProcessing, "Output", "Shared", []) { ResourceIds = [id, id] };
        var snapshot = new DashboardResourceSnapshot(true, [new(id, "output", 123, 175, 1024 * 1024, DateTimeOffset.UtcNow, null)], []);

        // Act
        var component = context.Render<StreamResourceDetails>(parameters => parameters.Add(value => value.Stage, stage).Add(value => value.Snapshot, snapshot));

        // Assert
        Assert.Single(component.FindAll("tbody tr"));
        Assert.Contains("175%", component.Markup, StringComparison.Ordinal);
        Assert.Contains("Full process cost", component.Markup, StringComparison.Ordinal);
        Assert.Contains("not separately", component.Markup, StringComparison.Ordinal);
    }

    /// <summary>Verifies changing or unavailable observations preserve the resource table's fixed column layout.</summary>
    [Fact]
    public async Task StageDetails_ObservationChanges_KeepFixedColumnsAndUnavailableValues()
    {
        // Arrange
        await using var context = new LineupWebBunitContext();
        var id = Guid.NewGuid().ToString("N");
        var stage = new StreamingFlowPathStage(StreamingFlowStageKind.OutputProcessing, "Output", "Shared", []) { ResourceIds = [id] };
        var snapshot = new DashboardResourceSnapshot(true, [new(id, "output", 123, 12345.67, 1L << 40, DateTimeOffset.UtcNow, null)], []);
        var component = context.Render<StreamResourceDetails>(parameters => parameters.Add(value => value.Stage, stage).Add(value => value.Snapshot, snapshot));
        var widths = component.FindAll("colgroup col").Select(column => column.GetAttribute("style")).ToArray();

        // Act
        component.Render(parameters => parameters.Add(value => value.Snapshot, snapshot with { Processes = [] }));

        // Assert
        Assert.Contains("table-layout:fixed", component.Find("table").GetAttribute("style"), StringComparison.Ordinal);
        Assert.Equal(5, widths.Length);
        Assert.Equal(widths, component.FindAll("colgroup col").Select(column => column.GetAttribute("style")));
        var cells = component.FindAll("tbody td");
        Assert.Equal("N/A", cells[2].TextContent);
        Assert.Equal("N/A", cells[3].TextContent);
    }

    /// <summary>Verifies in-process and external stages retain truthful resource details even without separate input-media panels.</summary>
    [Theory]
    [InlineData(StreamingFlowStageKind.Multiplexer, "shared host")]
    [InlineData(StreamingFlowStageKind.Consumer, "outside Lineup")]
    public async Task StageSelection_NoSeparateProcess_ShowsResourceExplanation(StreamingFlowStageKind kind, string expected)
    {
        // Arrange
        await using var context = new LineupWebBunitContext();
        var stage = new StreamingFlowPathStage(kind, "Stage", "Shared", []);
        var path = new StreamingFlowPath("path", "session", "local", HostedStreamFormat.MpegTs, "1", DateTime.UtcNow, [stage], false, true, 2);
        var component = context.Render<StreamingFlowPaths>(parameters => parameters
            .Add(value => value.Snapshot, new StreamingFlowSnapshot([], [path], 1, 1, 0))
            .Add(value => value.Resources, new DashboardResourceSnapshot(true, [], [])));

        // Act
        await component.Find($"[data-flow-stage='{kind}']").ClickAsync(new());

        // Assert
        Assert.Contains(expected, component.Find($"[data-resource-stage='{kind}']").TextContent, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Verifies resource visibility uses the dashboard's chevron toggle while retaining its icon, current summary and accessible expansion state.</summary>
    [Fact]
    public async Task ResourcesPanel_Toggle_UsesChevronAndAccessibleLabel()
    {
        // Arrange
        await using var context = new LineupWebBunitContext();
        var settings = Substitute.For<IWebSettingsService>();
        settings.Settings.Returns(new WebSettings { MonitorResourcesOnlyWhenVisible = false });
        var workers = Substitute.For<IVirtualOutputRuntimeStatus>();
        var clock = new FakeTimeProvider();
        var counter = new Counter { ResidentBytes = 256 * 1024 * 1024 };
        var registry = new ProcessResourceRegistry(_ => counter, clock);
        var host = registry.Register(123, "management");
        registry.Attach(host);
        using var monitor = new DashboardResourceMonitor(settings, workers, NullLogger<DashboardResourceMonitor>.Instance, registry, clock);
        monitor.Collect();
        counter.Cpu = TimeSpan.FromSeconds(1.5);
        clock.Advance(TimeSpan.FromSeconds(2));
        monitor.Collect();
        context.Services.AddLogging();
        context.Services.AddSingleton(monitor);
        context.Services.AddScoped<IBrowserDataStore, BrowserDataStore>();
        context.JSInterop.Setup<string?>("localStorage.getItem", "lineup-dashboard-resource-sections-v1").SetResult(null);
        context.JSInterop.SetupVoid("localStorage.setItem", _ => true).SetVoidResult();
        context.JSInterop.SetupVoid("lineupResources.start", _ => true).SetVoidResult();
        context.JSInterop.SetupVoid("lineupResources.stop", _ => true).SetVoidResult();
        var component = context.Render<DashboardResourcesPanel>(parameters => parameters.Add(value => value.HistoryMinutes, 2));

        // Act
        await component.InvokeAsync(() => component.Instance.ObserveVisibility(true));
        await component.Find("#toggleResourcesSection").ClickAsync(new());

        // Assert
        var collapsed = component.Find("#toggleResourcesSection");
        Assert.Equal("false", collapsed.GetAttribute("aria-expanded"));
        Assert.NotNull(collapsed.QuerySelector(".bi-chevron-down[aria-hidden='true']"));
        Assert.Equal("Show / hide graphs", collapsed.QuerySelector(".visually-hidden")!.TextContent);
        Assert.Empty(component.FindAll("#resourceCharts"));
        var header = collapsed.Closest(".card-header")!;
        Assert.NotNull(header.QuerySelector("h5 .bi-speedometer2"));
        Assert.Equal("CPU: 75% · RAM: 256 MiB · GPU: N/A", header.QuerySelector("small")!.TextContent);
        Assert.Null(header.QuerySelector("small")!.Closest("[hidden]"));

        // Act
        await collapsed.ClickAsync(new());

        // Assert
        var expanded = component.Find("#toggleResourcesSection");
        Assert.Equal("true", expanded.GetAttribute("aria-expanded"));
        Assert.NotNull(expanded.QuerySelector(".bi-chevron-up[aria-hidden='true']"));
        Assert.Single(component.FindAll("#resourceCharts"));
        Assert.Equal("CPU: 75% · RAM: 256 MiB · GPU: N/A", expanded.Closest(".card-header")!.QuerySelector("small")!.TextContent);
        Assert.All(component.FindAll(".resource-history"), chart => Assert.Equal(
            new[] { "2m", "1m 30s", "1m", "30s", "0s" },
            chart.QuerySelectorAll("[data-resource-x-tick]").Select(tick => tick.TextContent.Trim())));

        // Act
        component.Render(parameters => parameters.Add(value => value.HistoryMinutes, 1));

        // Assert
        Assert.All(component.FindAll(".resource-history"), chart =>
        {
            Assert.Equal(new[] { "1m", "45s", "30s", "15s", "0s" },
                chart.QuerySelectorAll("[data-resource-x-tick]").Select(tick => tick.TextContent.Trim()));
            Assert.Equal("translate(900 0) scale(15 1)", chart.QuerySelector("[data-resource-series]")!.GetAttribute("transform"));
        });

        // Act
        await component.Find("#resourceCharts > section[aria-label='GPU'] button").ClickAsync(new());

        // Assert
        var placeholder = component.Find("#resourceCharts > section[aria-label='GPU']");
        var placeholderToggle = component.Find("#resourceCharts > section[aria-label='GPU'] button");
        Assert.Equal("false", placeholderToggle.GetAttribute("aria-expanded"));
        Assert.NotNull(placeholderToggle.QuerySelector(".bi-chevron-down"));
        Assert.True(placeholder.QuerySelector(".card-body")!.HasAttribute("hidden"));
        Assert.Equal(placeholder.QuerySelector(".card-body")!.Id, placeholderToggle.GetAttribute("aria-controls"));
        Assert.Equal("GPU", placeholder.QuerySelector("h6")!.TextContent);

        // Act
        await placeholderToggle.ClickAsync(new());

        // Assert
        Assert.Equal("true", component.Find("#resourceCharts > section[aria-label='GPU'] button").GetAttribute("aria-expanded"));
        Assert.False(component.Find("#resourceCharts > section[aria-label='GPU'] > .card-body").HasAttribute("hidden"));
        registry.Unregister(host);
    }

    /// <summary>Verifies resource subcards retain their title, accessible controls and independent collapse state while observations update.</summary>
    [Theory]
    [InlineData(false, null, "CPU", "50 %")]
    [InlineData(true, null, "RAM", "2 MiB")]
    [InlineData(false, "gpu", "GPU · Test GPU", "50 %")]
    public async Task Chart_Toggle_KeepsContentCollapsedAcrossObservationUpdates(bool memory, string? gpuId, string title, string expected)
    {
        // Arrange
        await using var context = new LineupWebBunitContext();
        var now = DateTimeOffset.UtcNow;
        var usage = new ResourceUsage(25, 1024 * 1024);
        var first = new ResourceHistoryPoint(now, usage, usage, usage, usage, true)
        {
            Gpus = [new("gpu", "Test GPU", 25, null, null, null)]
        };
        var updated = first with
        {
            AtUtc = now.AddSeconds(2),
            Total = new(50, 2 * 1024 * 1024),
            Gpus = [new("gpu", "Test GPU", 50, null, null, null)]
        };
        var component = context.Render<ResourceHistoryChart>(parameters => parameters
            .Add(value => value.History, [first]).Add(value => value.Memory, memory)
            .Add(value => value.GpuId, gpuId).Add(value => value.GpuName, "Test GPU"));
        var bodyId = component.Find(".card-body").Id;

        // Act
        await component.Find(".card-header button").ClickAsync(new());
        component.Render(parameters => parameters.Add(value => value.History, [first, updated]));

        // Assert
        var collapsed = component.Find(".card-header button");
        Assert.Equal("false", collapsed.GetAttribute("aria-expanded"));
        Assert.Equal(bodyId, collapsed.GetAttribute("aria-controls"));
        Assert.NotNull(collapsed.QuerySelector(".bi-chevron-down[aria-hidden='true']"));
        Assert.Contains(title, collapsed.QuerySelector(".visually-hidden")!.TextContent);
        Assert.Equal(title, component.Find("h6").TextContent);
        Assert.Null(component.Find(".card-header").Closest("[hidden]"));
        Assert.True(component.Find(".card-body").HasAttribute("hidden"));

        // Act
        await collapsed.ClickAsync(new());

        // Assert
        Assert.Equal("true", component.Find(".card-header button").GetAttribute("aria-expanded"));
        Assert.NotNull(component.Find(".card-header .bi-chevron-up"));
        Assert.False(component.Find(".card-body").HasAttribute("hidden"));
        Assert.StartsWith(expected, component.Find("dd").TextContent.Trim());
    }

    /// <summary>Verifies graph axes admit multi-core percentages and gaps are not joined across missing observations.</summary>
    [Fact]
    public async Task Chart_MissingObservation_BreaksLineWithoutCappingCpu()
    {
        // Arrange
        await using var context = new LineupWebBunitContext();
        var now = DateTimeOffset.UtcNow;
        ResourceHistoryPoint Point(int offset, double? cpu) => new(now.AddSeconds(offset), new(cpu, 1024), new(cpu, 1024), new(0, 0), new(0, 0), cpu is not null);
        ResourceHistoryPoint[] history = [Point(0, 175), Point(2, null), Point(4, 200)];

        // Act
        var component = context.Render<ResourceHistoryChart>(parameters => parameters.Add(value => value.History, history));

        // Assert
        Assert.Contains("200%", component.Markup.Replace(" ", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Equal(2, component.FindAll("polyline[stroke='#0d6efd']").Count);
        Assert.Contains("2 logical cores", component.Markup, StringComparison.Ordinal);
    }

    /// <summary>Verifies long histories retain peak values while bounding SVG geometry independently of retained sample count.</summary>
    [Fact]
    public async Task Chart_FullHistory_PreservesPeakWithBoundedGeometry()
    {
        // Arrange
        await using var context = new LineupWebBunitContext();
        var now = DateTimeOffset.UtcNow;
        var history = Enumerable.Range(0, 1800).Select(index =>
        {
            var usage = new ResourceUsage(index == 901 ? 500 : 25, 1024);
            return new ResourceHistoryPoint(now.AddSeconds(index * 2), usage, usage, usage, usage, true);
        }).ToArray();

        // Act
        var component = context.Render<ResourceHistoryChart>(parameters => parameters.Add(value => value.History, history));
        var points = component.Find("polyline[stroke='#0d6efd']").GetAttribute("points")!.Split(' ');

        // Assert
        Assert.InRange(points.Length, 2, 302);
        Assert.Contains(points, point => point.EndsWith(",4", StringComparison.Ordinal));
        Assert.Contains("500", component.Markup, StringComparison.Ordinal);
    }

    /// <summary>Verifies each chart is a nested card with readable intermediate values, times and aligned grid lines.</summary>
    [Theory]
    [InlineData(false, "CPU", new[] { "0 %", "50 %", "100 %", "150 %", "200 %" })]
    [InlineData(true, "RAM", new[] { "0 MiB", "64 MiB", "128 MiB", "192 MiB", "256 MiB" })]
    public async Task Chart_Axes_ShowUnitsIntermediateTicksAndGrid(bool memory, string title, string[] expectedValues)
    {
        // Arrange
        await using var context = new LineupWebBunitContext();
        var start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var usage = new ResourceUsage(200, 256 * 1024 * 1024);
        ResourceHistoryPoint[] history =
        [
            new(start, usage, usage, usage, usage, true),
            new(start.AddMinutes(4), usage, usage, usage, usage, true)
        ];

        // Act
        var component = context.Render<ResourceHistoryChart>(parameters => parameters.Add(value => value.History, history).Add(value => value.Memory, memory));

        // Assert
        Assert.Equal(title, component.Find("section.card.mb-3 > .card-header h6").TextContent);
        Assert.NotNull(component.Find("section.card.mb-3 > .card-body"));
        var legend = component.Find("dl.row.row-cols-1.row-cols-md-2.row-cols-xl-4");
        Assert.Equal(4, legend.QuerySelectorAll(":scope > .col").Length);
        Assert.Equal(expectedValues, component.FindAll("[data-resource-y-tick]").Select(element => element.TextContent.Trim()));
        Assert.Equal(new[] { "5m", "3m 45s", "2m 30s", "1m 15s", "0s" },
            component.FindAll("[data-resource-x-tick]").Select(element => element.TextContent.Trim()));
        Assert.Equal(new[] { "149", "112.75", "76.5", "40.25", "4" },
            component.FindAll("[data-resource-gridline]").Select(element => element.GetAttribute("y1")));
        Assert.Contains("Time ago", component.Markup, StringComparison.Ordinal);
    }

    /// <summary>Verifies fixed-window labels use compact seconds and total minutes regardless of available history.</summary>
    [Theory]
    [InlineData(1, new[] { "1m", "45s", "30s", "15s", "0s" })]
    [InlineData(2, new[] { "2m", "1m 30s", "1m", "30s", "0s" })]
    [InlineData(60, new[] { "60m", "45m", "30m", "15m", "0s" })]
    public async Task Chart_TimeAxis_UsesConfiguredWindow(int minutes, string[] expected)
    {
        // Arrange
        await using var context = new LineupWebBunitContext();
        var start = new DateTimeOffset(2026, 1, 1, 23, 59, 45, TimeSpan.Zero);
        var usage = new ResourceUsage(25, 1024);
        var first = new ResourceHistoryPoint(start, usage, usage, usage, usage, true);
        ResourceHistoryPoint[] history = [first, first with { AtUtc = start.AddSeconds(44) }];

        // Act
        var component = context.Render<ResourceHistoryChart>(parameters => parameters.Add(value => value.History, history).Add(value => value.HistoryMinutes, minutes));

        // Assert
        Assert.Equal(expected, component.FindAll("[data-resource-x-tick]").Select(element => element.TextContent.Trim()));
    }

    /// <summary>Verifies growing history leaves unobserved time empty rather than stretching samples across the configured window.</summary>
    [Fact]
    public async Task Chart_TimeAxis_GrowingHistory_KeepsScaleAndPositionsSamplesByAge()
    {
        // Arrange
        await using var context = new LineupWebBunitContext();
        var start = new DateTimeOffset(2026, 1, 1, 23, 59, 45, TimeSpan.Zero);
        var usage = new ResourceUsage(25, 1024);
        var component = context.Render<ResourceHistoryChart>();
        string[] labels = ["5m", "3m 45s", "2m 30s", "1m 15s", "0s"];
        foreach (var seconds in new[] { 0, 44, 124, 300 })
        {
            var history = Enumerable.Range(0, seconds / 2 + 1)
                .Select(index => new ResourceHistoryPoint(start.AddSeconds(index * 2), usage, usage, usage, usage, true)).ToArray();

            // Act
            component.Render(parameters => parameters.Add(value => value.History, history));

            // Assert
            Assert.Equal(labels, component.FindAll("[data-resource-x-tick]").Select(tick => tick.TextContent.Trim()));
            Assert.Equal("translate(900 0) scale(3 1)", component.Find("[data-resource-series]").GetAttribute("transform"));
            var points = component.Find("polyline[stroke='#0d6efd']").GetAttribute("points")!.Split(' ');
            Assert.Equal(-seconds, double.Parse(points[0].Split(',')[0], CultureInfo.InvariantCulture));
            Assert.Equal(0, double.Parse(points[^1].Split(',')[0], CultureInfo.InvariantCulture));
        }
    }

    /// <summary>Verifies an empty history retains the configured axis without fabricating plotted observations.</summary>
    [Fact]
    public async Task Chart_TimeAxis_EmptyHistory_KeepsConfiguredWindow()
    {
        // Arrange
        await using var context = new LineupWebBunitContext();

        // Act
        var component = context.Render<ResourceHistoryChart>();

        // Assert
        Assert.Equal(new[] { "5m", "3m 45s", "2m 30s", "1m 15s", "0s" },
            component.FindAll("[data-resource-x-tick]").Select(tick => tick.TextContent.Trim()));
        Assert.Empty(component.FindAll("polyline"));
    }

    private sealed class Counter : IProcessResourceCounter
    {
        public long? StartTimeUtcTicks { get; set; }
        public TimeSpan Cpu { get; set; }
        public long ResidentBytes { get; set; } = 1024;
        public int Reads { get; private set; }
        public bool Failed { get; set; }
        public bool Disposed { get; private set; }

        public (TimeSpan Cpu, long ResidentBytes) Read()
        {
            Reads++;
            return Failed ? throw new InvalidOperationException("The original process ended.") : (Cpu, ResidentBytes);
        }

        public void Dispose() => Disposed = true;
    }
}
