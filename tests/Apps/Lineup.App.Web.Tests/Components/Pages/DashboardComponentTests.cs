using System.Reflection;
using System.Globalization;
using System.Text.Json;
using Bunit;
using Lineup.App.Web.Components.Dashboard;
using Lineup.App.Web.Components.Pages;
using Lineup.App.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using NSubstitute;
using Xunit;

namespace Lineup.App.Web.Tests.Components.Pages;

/// <summary>
/// Verifies dashboard section presentation and browser-persisted collapse state.
/// </summary>
public class DashboardComponentTests
{
    private const string StorageKey = "lineup-dashboard-sections-v1";
    private const string DeviceStorageKey = "lineup-dashboard-physical-device-sections-v1";

    /// <summary>Verifies refresh actions follow all content at the bottom of their collapsible card bodies, not the primary headers.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RefreshButtons_ExpandedOrCollapsed_StayAtBottomOfCardBodies(bool expanded)
    {
        // Arrange
        await using var context = CreateContext();
        context.JSInterop.Setup<string?>("localStorage.getItem", StorageKey).SetResult(JsonSerializer.Serialize(new
        {
            ChannelsExpanded = expanded,
            DeviceExpanded = expanded,
            ActiveStreamsExpanded = expanded
        }));

        // Act
        var component = context.Render<Dashboard>();

        // Assert
        component.WaitForAssertion(() =>
        {
            foreach (var id in new[] { "channelsSection", "deviceSection", "activeStreamsSection" })
            {
                var body = component.Find($"#{id}");
                var refresh = Assert.Single(body.QuerySelectorAll(":scope > .d-flex > button.btn-outline-primary"));
                Assert.Same(body.LastElementChild, refresh.ParentElement);
                Assert.Null(refresh.Closest(".card-header"));
                Assert.Equal(!expanded, body.HasAttribute("hidden"));
                var header = body.ParentElement!.QuerySelector(":scope > .card-header")!;
                Assert.Empty(header.QuerySelectorAll("button.btn-outline-primary"));
                Assert.Single(header.QuerySelectorAll("button"));
            }
            Assert.Single(component.FindAll("#activeStreamsSection > #streamingFlowDiagram"));
            Assert.Empty(component.FindAll("#activeStreamsSection > .card"));
        }, timeout: LineupWebBunitContext.WaitTimeout);
    }

    /// <summary>Verifies primary card headers retain their icons and summaries in both expansion states.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Headers_ExpandedOrCollapsed_KeepIconsAndSummariesVisible(bool expanded)
    {
        // Arrange
        await using var context = CreateContext();
        context.JSInterop.Setup<string?>("localStorage.getItem", StorageKey).SetResult(JsonSerializer.Serialize(new
        {
            ChannelsExpanded = expanded,
            GuideExpanded = expanded,
            DeviceExpanded = expanded,
            ActiveStreamsExpanded = expanded
        }));

        // Act
        var component = context.Render<Dashboard>();

        // Assert
        component.WaitForAssertion(() =>
        {
            foreach (var id in new[] { "toggleChannelsSection", "toggleGuideSection", "toggleDeviceSection", "toggleActiveStreamsSection" })
            {
                var button = component.Find($"#{id}");
                Assert.Equal(expanded ? "true" : "false", button.GetAttribute("aria-expanded"));
                var header = button.Closest(".card-header")!;
                Assert.NotNull(header.QuerySelector("h5 .bi"));
                var summary = header.QuerySelector("small")!;
                Assert.False(string.IsNullOrWhiteSpace(summary.TextContent));
                Assert.Null(summary.Closest("[hidden]"));
            }
        }, timeout: LineupWebBunitContext.WaitTimeout);
    }

    /// <summary>Verifies populated localized guide statistics render duration summaries and channel gaps even when the guide panel starts collapsed.</summary>
    [Theory]
    [InlineData("pl", false)]
    [InlineData("pl", true)]
    [InlineData("zh-Hans", false)]
    [InlineData("zh-Hans", true)]
    [InlineData("it", false)]
    [InlineData("it", true)]
    public async Task GuidePanel_LocalizedDurations_RendersStatistics(string culture, bool collapsed)
    {
        // Arrange
        var originalCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            await using var context = CreateContext();
            context.JSInterop.Setup<string?>("localStorage.getItem", StorageKey)
                .SetResult(collapsed ? """{"GuideExpanded":false}""" : null);
            var start = DateTimeOffset.UtcNow;
            context.Services.GetRequiredService<ILineupGuideReader>().GetStatisticsAsync(Arg.Any<CancellationToken>())
                .Returns(new GuideStatistics(12, 345, start, start.AddDays(3).AddHours(4), start.AddDays(3).AddHours(2)));

            // Act
            var component = context.Render<Dashboard>();

            // Assert
            Assert.Equal(collapsed, component.Find("#guideSection").HasAttribute("hidden"));
            Assert.Contains("345", component.Find("#guideSection").TextContent, StringComparison.Ordinal);
            Assert.Contains("2", component.Find("#guideSection .text-warning").TextContent, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalCulture;
        }
    }

    /// <summary>Verifies source warnings are visible even when the guide panel is collapsed and do not replace healthy statistics.</summary>
    [Fact]
    public async Task GuidePanel_PartialResults_ShowsWarningOutsideCollapsedContent()
    {
        // Arrange
        await using var context = CreateContext();
        context.JSInterop.Setup<string?>("localStorage.getItem", StorageKey).SetResult(null);
        context.JSInterop.SetupVoid("localStorage.setItem", _ => true).SetVoidResult();
        context.Services.GetRequiredService<ILineupGuideReader>().GetStatisticsAsync(Arg.Any<CancellationToken>()).Returns(new GuideStatistics(12, 345, null, null, null)
        {
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
        });

        // Act
        var component = context.Render<Dashboard>();
        await component.Find("#toggleGuideSection").ClickAsync(new());

        // Assert
        Assert.True(component.Find("#guideSection").HasAttribute("hidden"));
        Assert.Contains("12", component.Find("#guideSection").TextContent, StringComparison.Ordinal);
        Assert.Contains("345", component.Find("#guideSection").TextContent, StringComparison.Ordinal);
        var warning = component.Find("[data-guide-source-warnings]");
        Assert.Contains("Missing source: Install the source plugin.", warning.TextContent, StringComparison.Ordinal);
        Assert.Null(warning.Closest("[hidden]"));
    }

    /// <summary>Verifies failed activation remains visible in the device panel and increases the unavailable summary count.</summary>
    [Fact]
    public async Task PhysicalDevices_ActivationFailure_ShowsUnavailableDeviceAndCount()
    {
        // Arrange
        var catalog = Substitute.For<IPhysicalDeviceCatalog>();
        catalog.TryPublishAsync(Arg.Any<StreamingConfiguration>(), Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            await call.Arg<Func<CancellationToken, Task>>()(call.Arg<CancellationToken>());
            return true;
        });
        var statuses = new PhysicalDeviceStatusService(catalog);
        await using var context = CreateContext(deviceStatuses: statuses);
        context.JSInterop.Setup<string?>("localStorage.getItem", StorageKey).SetResult(null);
        var healthy = new PhysicalDeviceDefinition
        {
            Id = PhysicalDeviceId.New(),
            Name = "Healthy source",
            Adapter = new AdapterConfiguration("dashboard-test", JsonSerializer.SerializeToElement(new { }))
        };
        var failed = healthy with { Id = PhysicalDeviceId.New(), Name = "Unavailable source" };
        var configuration = new StreamingConfiguration { PhysicalDevices = [healthy, failed] };
        context.Services.GetRequiredService<ILineupSettingsService>().Settings.Streaming = configuration;
        var live = Substitute.For<IPhysicalTunerDevice>();
        live.Snapshot.Returns(new PhysicalDeviceSnapshot { Id = healthy.Id, Name = healthy.Name, Kind = healthy.Adapter.Kind, Capacity = new PhysicalTunerCapacity(1) });
        catalog.Devices.Returns([live]);
        await statuses.RefreshAsync(configuration, Xunit.TestContext.Current.CancellationToken);

        // Act
        var component = context.Render<Dashboard>();

        // Assert
        component.WaitForAssertion(() =>
        {
            Assert.Contains("2 devices", component.Markup, StringComparison.Ordinal);
            Assert.Contains("1 unavailable", component.Markup, StringComparison.Ordinal);
            Assert.Contains(healthy.Name, component.Find("#deviceSection").TextContent, StringComparison.Ordinal);
            Assert.Contains(failed.Name, component.Find("#deviceSection").TextContent, StringComparison.Ordinal);
            Assert.Contains("activation failed", Assert.Single(component.FindAll("#deviceSection .alert-danger")).TextContent, StringComparison.OrdinalIgnoreCase);
        }, timeout: LineupWebBunitContext.WaitTimeout);
    }

    /// <summary>
    /// Verifies dashboard disposal does not resolve services after the host service provider has shut down.
    /// </summary>
    [Fact]
    public async Task Dispose_ServiceProviderUnavailable_DoesNotResolveServices()
    {
        // Arrange
        await using var context = CreateContext();
        context.JSInterop.Setup<string?>("localStorage.getItem", StorageKey).SetResult(null);
        var component = context.Render<Dashboard>();
        var servicesProperty = typeof(Dashboard).GetProperty("Services", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(servicesProperty);
        servicesProperty.SetValue(component.Instance, new DisposedServiceProvider());

        // Act
        var exception = await Record.ExceptionAsync(() => component.Instance.DisposeAsync().AsTask());

        // Assert
        Assert.Null(exception);
    }

    /// <summary>
    /// Verifies dashboard cleanup joins an admitted refresh before the context releases scoped services.
    /// </summary>
    [Fact]
    public async Task Dispose_PendingStatisticsRefresh_WaitsForOwnedUpdate()
    {
        // Arrange
        await using var context = CreateContext();
        context.JSInterop.Setup<string?>("localStorage.getItem", StorageKey).SetResult(null);
        context.Render<Dashboard>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<GuideStatistics>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = context.Services.GetRequiredService<ILineupGuideReader>();
        reader.GetStatisticsAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            started.TrySetResult();
            return completion.Task;
        });
        var autoFetch = context.Services.GetRequiredService<IAutoFetchStateService>();
        autoFetch.LastFetchTime.Returns(DateTime.UtcNow);
        Task? disposal = null;
        try
        {
            // Act
            autoFetch.OnStateChanged += Raise.Event<Action>();
            await started.Task.WaitAsync(LineupWebBunitContext.WaitTimeout, Xunit.TestContext.Current.CancellationToken);
            disposal = context.DisposeComponentsAsync();

            // Assert
            Assert.False(disposal.IsCompleted);
        }
        finally
        {
            completion.TrySetResult(new GuideStatistics(0, 0, null, null, null));
            if (disposal is not null)
            {
                await disposal.WaitAsync(LineupWebBunitContext.WaitTimeout, CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// Verifies a browser without saved preferences sees every persistent section expanded.
    /// </summary>
    [Fact]
    public async Task Sections_NoSavedState_StartExpanded()
    {
        // Arrange
        await using var context = CreateContext();
        context.JSInterop.Setup<string?>("localStorage.getItem", StorageKey).SetResult(null);

        // Act
        var component = context.Render<Dashboard>();

        // Assert
        Assert.All(
            ["toggleChannelsSection", "toggleGuideSection", "toggleDeviceSection", "toggleActiveStreamsSection"],
            id => Assert.Equal("true", component.Find($"#{id}").GetAttribute("aria-expanded")));
        Assert.False(component.Find("#channelsSection").HasAttribute("hidden"));
        Assert.False(component.Find("#guideSection").HasAttribute("hidden"));
        Assert.False(component.Find("#deviceSection").HasAttribute("hidden"));
        Assert.False(component.Find("#activeStreamsSection").HasAttribute("hidden"));
        Assert.Contains(component.FindAll("h5"), heading => heading.TextContent.Trim() == "Channels");
        Assert.DoesNotContain(component.FindAll("h5"), heading => heading.TextContent.Trim() == "HDHomeRun Channels");
    }

    /// <summary>
    /// Verifies independently saved collapsed sections restore with live header summaries.
    /// </summary>
    [Fact]
    public async Task Sections_SavedCollapsedState_RestoresSummaries()
    {
        // Arrange
        await using var context = CreateContext();
        var savedState = JsonSerializer.Serialize(new
        {
            ChannelsExpanded = false,
            GuideExpanded = false,
            DeviceExpanded = false,
            ActiveStreamsExpanded = false
        });
        context.JSInterop.Setup<string?>("localStorage.getItem", StorageKey).SetResult(savedState);

        // Act
        var component = context.Render<Dashboard>();

        // Assert
        component.WaitForAssertion(() =>
        {
            Assert.True(component.Find("#channelsSection").HasAttribute("hidden"));
            Assert.True(component.Find("#guideSection").HasAttribute("hidden"));
            Assert.True(component.Find("#deviceSection").HasAttribute("hidden"));
            Assert.True(component.Find("#activeStreamsSection").HasAttribute("hidden"));
            Assert.Contains("0 enabled · 0 disabled · never refreshed", component.Markup);
            Assert.Contains("12 channels · 345 programs · 2 days 3 hrs remaining", component.Markup);
            Assert.Contains("0 devices", component.Markup);
            Assert.Contains("0 active ·", component.Markup);
        }, timeout: LineupWebBunitContext.WaitTimeout);
    }

    /// <summary>
    /// Verifies each restored preference is mapped to its corresponding section.
    /// </summary>
    [Fact]
    public async Task Sections_MixedSavedState_RestoresIndependently()
    {
        // Arrange
        await using var context = CreateContext();
        var savedState = JsonSerializer.Serialize(new
        {
            ChannelsExpanded = false,
            GuideExpanded = true,
            DeviceExpanded = false,
            ActiveStreamsExpanded = true
        });
        context.JSInterop.Setup<string?>("localStorage.getItem", StorageKey).SetResult(savedState);

        // Act
        var component = context.Render<Dashboard>();

        // Assert
        component.WaitForAssertion(() =>
        {
            Assert.True(component.Find("#channelsSection").HasAttribute("hidden"));
            Assert.False(component.Find("#guideSection").HasAttribute("hidden"));
            Assert.True(component.Find("#deviceSection").HasAttribute("hidden"));
            Assert.False(component.Find("#activeStreamsSection").HasAttribute("hidden"));
        }, timeout: LineupWebBunitContext.WaitTimeout);
    }

    /// <summary>
    /// Verifies a section toggle persists the complete current preference set.
    /// </summary>
    [Theory]
    [InlineData("toggleChannelsSection", "ChannelsExpanded")]
    [InlineData("toggleGuideSection", "GuideExpanded")]
    [InlineData("toggleDeviceSection", "DeviceExpanded")]
    [InlineData("toggleActiveStreamsSection", "ActiveStreamsExpanded")]
    public async Task ToggleSection_PersistsUpdatedState(string toggleId, string collapsedProperty)
    {
        // Arrange
        await using var context = CreateContext();
        context.JSInterop.Setup<string?>("localStorage.getItem", StorageKey).SetResult(null);
        context.JSInterop.SetupVoid("localStorage.setItem", _ => true).SetVoidResult();
        var component = context.Render<Dashboard>();

        // Act
        await component.Find($"#{toggleId}").ClickAsync(new());

        // Assert
        component.WaitForAssertion(() => Assert.Equal("false", component.Find($"#{toggleId}").GetAttribute("aria-expanded")), timeout: LineupWebBunitContext.WaitTimeout);
        var invocation = Assert.Single(context.JSInterop.Invocations, item => item.Identifier == "localStorage.setItem");
        var state = JsonDocument.Parse(Assert.IsType<string>(invocation.Arguments[1])).RootElement;
        foreach (var property in new[] { "ChannelsExpanded", "GuideExpanded", "DeviceExpanded", "ActiveStreamsExpanded" })
        {
            Assert.Equal(property != collapsedProperty, state.GetProperty(property).GetBoolean());
        }
    }

    /// <summary>
    /// Verifies corrupt browser state does not prevent the expanded default dashboard.
    /// </summary>
    [Fact]
    public async Task Sections_InvalidSavedState_FallBackToExpanded()
    {
        // Arrange
        await using var context = CreateContext();
        context.JSInterop.Setup<string?>("localStorage.getItem", StorageKey).SetResult("{invalid");

        // Act
        var component = context.Render<Dashboard>();

        // Assert
        Assert.False(component.Find("#channelsSection").HasAttribute("hidden"));
        Assert.False(component.Find("#guideSection").HasAttribute("hidden"));
        Assert.False(component.Find("#deviceSection").HasAttribute("hidden"));
        Assert.False(component.Find("#activeStreamsSection").HasAttribute("hidden"));
    }

    /// <summary>
    /// Verifies the dashboard fetch action uses the topology-aware guide service.
    /// </summary>
    [Fact]
    public async Task FetchGuide_UsesConfiguredDeviceOwnedSources()
    {
        // Arrange
        var guides = Substitute.For<ITopologyGuideService>();
        guides.RefreshAndPublishAsync(Arg.Any<CancellationToken>())
            .Returns(new TopologyGuideRefreshResult { Sources = [] });
        await using var context = CreateContext(guides: guides);
        context.JSInterop.Setup<string?>("localStorage.getItem", StorageKey).SetResult(null);
        var component = context.Render<Dashboard>();
        var notifications = context.Services.GetRequiredService<IStatusNotificationService>();

        // Act
        await component.Find("#fetchGuide").ClickAsync(new());

        // Assert
        component.WaitForAssertion(() =>
        {
            guides.Received(1).RefreshAndPublishAsync(Arg.Any<CancellationToken>());
            Assert.Contains("No guide sources are enabled", Assert.Single(notifications.Notifications).Message, StringComparison.Ordinal);
        }, timeout: LineupWebBunitContext.WaitTimeout);
    }

    /// <summary>Verifies manual refresh and generation never announce full success for partial publications.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GuidePublication_PartialResults_ReportsWarning(bool refresh)
    {
        // Arrange
        var guides = Substitute.For<ITopologyGuideService>();
        IReadOnlyList<GuideOutputPublicationResult> outputs =
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
        ];
        guides.RefreshAndPublishAsync(Arg.Any<CancellationToken>()).Returns(new TopologyGuideRefreshResult { Sources = [], Outputs = outputs });
        guides.PublishAsync(Arg.Any<CancellationToken>()).Returns(outputs);
        await using var context = CreateContext(guides: guides);
        context.JSInterop.Setup<string?>("localStorage.getItem", StorageKey).SetResult(null);
        var component = context.Render<Dashboard>();
        var notifications = context.Services.GetRequiredService<IStatusNotificationService>();

        // Act
        await component.Find(refresh ? "#fetchGuide" : "#generateXmltv").ClickAsync(new());

        // Assert
        Assert.Contains("Partial guide results", Assert.Single(notifications.Notifications).Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies shared CMAF presentations are not mislabeled as HLS-only sessions.
    /// </summary>
    [Fact]
    public async Task ActiveStreams_CmafPresentation_UsesCmafLabel()
    {
        // Arrange
        var activeStreams = Substitute.For<IActiveStreamRegistry>();
        var startedAtUtc = DateTime.UtcNow.AddMinutes(-2);
        activeStreams.GetActiveStreams().Returns(
        [
            new ActiveStreamSnapshot("session", "2.1", HostedStreamFormat.Cmaf, startedAtUtc, null, [])
        ]);
        var snapshot = new StreamingFlowSnapshot(
            [
                new StreamingFlowNode("consumer:session", StreamingFlowLane.Consumer, "192.0.2.10:5000", "CMAF", StreamingFlowState.Active, [])
            ],
            [
                new StreamingFlowPath(
                    "path:one",
                    "session",
                    "192.0.2.10:5000",
                    HostedStreamFormat.Cmaf,
                    "2.1",
                    startedAtUtc,
                    [
                        new StreamingFlowPathStage(StreamingFlowStageKind.PhysicalSource, "Roof Antenna", "HDHomeRun", []),
                        new StreamingFlowPathStage(StreamingFlowStageKind.SourceProcessing, "Pass-through", "Roof Antenna", []),
                        new StreamingFlowPathStage(StreamingFlowStageKind.Multiplexer, "Shared Multiplexer", "Session-owned producer", []),
                        new StreamingFlowPathStage(StreamingFlowStageKind.VirtualOutput, "Web Player", "Web Player", []),
                        new StreamingFlowPathStage(StreamingFlowStageKind.OutputProcessing, "CMAF transcode", "Web Player", []),
                        new StreamingFlowPathStage(StreamingFlowStageKind.Consumer, "192.0.2.10:5000", "CMAF", [])
                    ],
                    false,
                    false,
                    1)
            ],
            1,
            1,
            0);
        await using var context = CreateContext(activeStreams: activeStreams, streamingFlow: snapshot);
        context.JSInterop.Setup<string?>("localStorage.getItem", StorageKey).SetResult(null);

        // Act
        var component = context.Render<Dashboard>();
        await component.InvokeAsync(() => component.Find("[data-flow-node-id='consumer:session']").ClickAsync(new()));

        // Assert
        Assert.Contains("CMAF", component.Find("#activeStreamsSection").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("HLS", component.Find("#activeStreamsSection").TextContent, StringComparison.Ordinal);
        Assert.Contains("session", component.Find("#streamingFlowDetails").TextContent, StringComparison.Ordinal);
        Assert.Single(component.FindAll("#activeStreamsSection > #streamingFlowDiagram"));
        Assert.Empty(component.FindAll("#activeStreamsSection > .card"));
        Assert.DoesNotContain("Active Paths", component.Find("#activeStreamsSection").TextContent, StringComparison.Ordinal);
        Assert.Empty(component.FindAll("#activeStreamsSection table"));
    }

    /// <summary>Complete upstream tuner state is separate from local Link slots, preserves measured zero and missing signal, and never exposes remote controls.</summary>
    [Fact]
    public async Task PhysicalDevices_LineupLink_ShowsFourRemoteTunersAndSeparateLocalSlots()
    {
        // Arrange
        await using var context = CreateContext();
        var upstream = new UpstreamPhysicalDeviceStatus
        {
            DeviceId = Guid.NewGuid(),
            Name = "Shared HDHomeRun",
            Kind = "hdhomerun",
            ObservedAtUtc = DateTimeOffset.UtcNow,
            Receivers = Enumerable.Range(0, 4).Select(index => new PhysicalReceiverStatus
            {
                Index = index,
                IsActive = true,
                IsStreaming = true,
                HasLock = true,
                VirtualChannel = $"2.{index + 1}",
                SignalStrength = index switch { 0 => 98, 1 => 0, 2 => null, _ => 87 }
            }).ToArray()
        };
        var second = upstream with
        {
            DeviceId = Guid.NewGuid(),
            Name = "Second upstream",
            Receivers = [new PhysicalReceiverStatus { Index = 0, IsActive = true, HasLock = true, Channel = "auto:507000000", SignalStrength = 55 }]
        };

        // Act
        var component = context.Render<Lineup.App.Web.Components.Dashboard.DashboardPhysicalDevicesPanel>(parameters => parameters
            .Add(panel => panel.Expanded, true)
            .Add(panel => panel.Summary, "Lineup Link")
            .Add(panel => panel.Devices, [LinkStatus([upstream, second])]));

        // Assert
        var localRows = component.FindAll("#deviceSection > .card > .card-body > .table-responsive tbody tr");
        Assert.Equal(4, localRows.Count);
        Assert.Equal(3, localRows.Count(row => row.Children[1].TextContent == "Streaming"));
        Assert.Equal("Unused", localRows[3].Children[1].TextContent);
        var remoteRows = component.FindAll($"[data-upstream-device='{upstream.DeviceId}'] tbody tr");
        Assert.Equal(4, remoteRows.Count);
        Assert.All(remoteRows, row => Assert.Equal("Streaming", row.Children[1].TextContent));
        Assert.Equal("98%", remoteRows[0].Children[3].TextContent);
        Assert.Equal("0%", remoteRows[1].Children[3].TextContent);
        Assert.Equal("N/A", remoteRows[2].Children[3].TextContent);
        Assert.Equal("2.4", remoteRows[3].Children[2].TextContent);
        var secondRow = component.Find($"[data-upstream-device='{second.DeviceId}'] tbody tr");
        Assert.Equal("Tuned", secondRow.Children[1].TextContent);
        Assert.Equal("auto:507000000", secondRow.Children[2].TextContent);
        Assert.Empty(component.FindAll("[data-upstream-tuner-status] button"));
        Assert.DoesNotContain("Target", component.Find("[data-upstream-tuner-status]").TextContent, StringComparison.Ordinal);
        Assert.Contains("not physical tuners", component.Find("#deviceSection").TextContent, StringComparison.Ordinal);
    }

    /// <summary>Unexposed inventory is hidden and active stream signal remains in the main table, including zero and unknown measurements.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PhysicalDevices_LineupLink_WithoutUpstreamData_DoesNotInventTuners(bool supported)
    {
        // Arrange
        await using var context = CreateContext();

        // Act
        var component = context.Render<Lineup.App.Web.Components.Dashboard.DashboardPhysicalDevicesPanel>(parameters => parameters
            .Add(panel => panel.Expanded, true)
            .Add(panel => panel.Summary, "Lineup Link")
            .Add(panel => panel.Devices, [LinkStatus(supported ? [] : null)]));

        // Assert
        if (supported)
        {
            var remote = component.Find("[data-upstream-tuner-status]");
            Assert.Contains("No upstream devices are subscribed.", remote.TextContent, StringComparison.Ordinal);
            Assert.Empty(remote.QuerySelectorAll("table"));
            Assert.Empty(remote.QuerySelectorAll("button"));
        }
        else
        {
            Assert.Empty(component.FindAll("[data-upstream-tuner-status]"));
        }
        var rows = component.FindAll("#deviceSection > .card > .card-body > .table-responsive tbody tr");
        Assert.Equal("99%", rows[0].Children[3].TextContent);
        Assert.Equal("0%", rows[1].Children[3].TextContent);
        Assert.Equal("N/A", rows[2].Children[3].TextContent);
        Assert.Equal("-", rows[3].Children[3].TextContent);
    }

    /// <summary>Failed upstream refreshes retain explicitly labeled observations, unknown status has no rows, and recovered status updates without affecting local slots.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PhysicalDevices_UpstreamRefresh_ShowsFreshnessAndRecovery(bool observed)
    {
        // Arrange
        await using var context = CreateContext();
        var upstream = new UpstreamPhysicalDeviceStatus
        {
            DeviceId = Guid.NewGuid(),
            Name = "Upstream source",
            Kind = "hdhomerun",
            ObservedAtUtc = observed ? new DateTimeOffset(2026, 7, 1, 14, 0, 0, TimeSpan.FromHours(2)) : null,
            Error = "Status refresh failed (IOException).",
            Receivers = observed ? [new PhysicalReceiverStatus { Index = 3, IsStreaming = true, HasLock = true, SignalStrength = 98 }] : []
        };
        var local = LinkStatus([upstream]);
        var component = context.Render<Lineup.App.Web.Components.Dashboard.DashboardPhysicalDevicesPanel>(parameters => parameters
            .Add(panel => panel.Expanded, true)
            .Add(panel => panel.Summary, "Lineup Link")
            .Add(panel => panel.Devices, [local]));
        var initial = component.Find("[data-upstream-tuner-status]");
        Assert.Contains(upstream.Error, initial.TextContent, StringComparison.Ordinal);
        Assert.Contains(observed ? "2026-07-01 12:00:00Z" : "N/A", initial.TextContent, StringComparison.Ordinal);
        Assert.Equal(observed ? 1 : 0, initial.QuerySelectorAll("tbody tr").Length);
        if (observed)
        {
            Assert.Contains("last successful observation", initial.TextContent, StringComparison.Ordinal);
        }
        var recovered = upstream with
        {
            ObservedAtUtc = new DateTimeOffset(2026, 7, 1, 14, 1, 0, TimeSpan.FromHours(2)),
            Error = null,
            Receivers = [new PhysicalReceiverStatus { Index = 3, IsActive = true, HasLock = true, SignalStrength = 61 }]
        };

        // Act
        component.Render(parameters => parameters.Add(panel => panel.Devices,
            [local with { Status = Assert.IsType<PhysicalDeviceStatusSnapshot>(local.Status) with { UpstreamDevices = [recovered] } }]));

        // Assert
        var remote = component.Find("[data-upstream-tuner-status]");
        Assert.Empty(remote.QuerySelectorAll(".alert"));
        Assert.Contains("2026-07-01 12:01:00Z", remote.TextContent, StringComparison.Ordinal);
        var row = Assert.Single(remote.QuerySelectorAll("tbody tr"));
        Assert.Equal("Tuned", row.Children[1].TextContent);
        Assert.Equal("61%", row.Children[3].TextContent);
        Assert.Equal(4, component.FindAll("#deviceSection > .card > .card-body > .table-responsive tbody tr").Count);
    }

    /// <summary>Verifies each physical-device chevron hides the complete body independently while retaining accessible headers and all existing content.</summary>
    [Fact]
    public async Task PhysicalDevices_ToggleCard_PersistsIndependentStateAndKeepsHeaderVisible()
    {
        // Arrange
        await using var context = CreateContext();
        var first = LinkStatus([new UpstreamPhysicalDeviceStatus { DeviceId = Guid.NewGuid(), Name = "Upstream", Kind = "hdhomerun" }]) with
        {
            Error = "Device unavailable",
            SupportsRestart = true
        };
        var second = LinkStatus(null);
        var component = context.Render<DashboardPhysicalDevicesPanel>(parameters => parameters
            .Add(panel => panel.Expanded, true)
            .Add(panel => panel.Summary, "Devices")
            .Add(panel => panel.Devices, [first, second]));
        var selector = $"[data-physical-device='{first.Device.Id}']";
        var button = component.Find($"{selector} > .card-header button");
        Assert.All(component.FindAll("#deviceSection > .card > .card-body"), body => Assert.False(body.HasAttribute("hidden")));

        // Act
        await button.ClickAsync(new());

        // Assert
        var header = component.Find($"{selector} > .card-header");
        Assert.Contains(first.Device.Name, header.TextContent, StringComparison.Ordinal);
        Assert.Contains(first.Device.Kind, header.TextContent, StringComparison.Ordinal);
        Assert.Null(header.Closest("[hidden]"));
        button = component.Find($"{selector} > .card-header button");
        Assert.Equal("false", button.GetAttribute("aria-expanded"));
        Assert.NotNull(button.QuerySelector(".bi-chevron-down[aria-hidden='true']"));
        Assert.Contains("Show / hide device details", button.TextContent, StringComparison.Ordinal);
        var body = component.Find($"{selector} > .card-body");
        Assert.Equal(body.Id, button.GetAttribute("aria-controls"));
        Assert.True(body.HasAttribute("hidden"));
        Assert.Contains(first.Error!, body.TextContent, StringComparison.Ordinal);
        Assert.NotNull(body.QuerySelector("[data-upstream-tuner-status]"));
        Assert.NotEmpty(body.QuerySelectorAll("button"));
        Assert.False(component.Find($"[data-physical-device='{second.Device.Id}'] > .card-body").HasAttribute("hidden"));
        Assert.Equal(2, component.FindAll("#deviceSection > .card > .card-header button").Count);
        Assert.Empty(component.FindAll("[data-upstream-tuner-status] button"));
        var write = Assert.Single(context.JSInterop.Invocations, invocation => invocation.Identifier == "localStorage.setItem");
        Assert.Equal(DeviceStorageKey, write.Arguments[0]);
        Assert.False(JsonSerializer.Deserialize<Dictionary<string, bool>>(Assert.IsType<string>(write.Arguments[1]))![first.Device.Id.ToString()]);

        // Act
        await button.ClickAsync(new());

        // Assert
        Assert.False(component.Find($"{selector} > .card-body").HasAttribute("hidden"));
        Assert.Equal("true", component.Find($"{selector} > .card-header button").GetAttribute("aria-expanded"));
        Assert.NotNull(component.Find($"{selector} > .card-header .bi-chevron-up"));
    }

    /// <summary>Verifies restored preferences follow device identity through renaming, reordering, status refreshes, and new cards.</summary>
    [Fact]
    public async Task PhysicalDevices_RestoredState_FollowsIdentityNotNameOrPosition()
    {
        // Arrange
        var first = LinkStatus(null);
        var second = LinkStatus(null);
        var third = LinkStatus(null);
        var saved = JsonSerializer.Serialize(new Dictionary<string, bool> { [first.Device.Id.ToString()] = false, [second.Device.Id.ToString()] = true });
        await using var context = CreateContext(savedDeviceState: saved);
        var renamed = first with { Device = first.Device with { Name = "Renamed device" } };

        // Act
        var component = context.Render<DashboardPhysicalDevicesPanel>(parameters => parameters
            .Add(panel => panel.Expanded, false)
            .Add(panel => panel.Summary, "Devices")
            .Add(panel => panel.Devices, [second, renamed, third]));
        component.Render(parameters => parameters.Add(panel => panel.Expanded, true).Add(panel => panel.Devices, [third, renamed with { Error = "Refresh failed" }, second]));

        // Assert
        Assert.True(component.Find($"[data-physical-device='{first.Device.Id}'] > .card-body").HasAttribute("hidden"));
        Assert.Contains("Renamed device", component.Find($"[data-physical-device='{first.Device.Id}'] > .card-header").TextContent, StringComparison.Ordinal);
        Assert.Contains("Refresh failed", component.Find($"[data-physical-device='{first.Device.Id}'] > .card-body").TextContent, StringComparison.Ordinal);
        Assert.All(new[] { second, third }, device =>
            Assert.False(component.Find($"[data-physical-device='{device.Device.Id}'] > .card-body").HasAttribute("hidden")));
        Assert.DoesNotContain(context.JSInterop.Invocations, invocation => invocation.Identifier == "localStorage.setItem");
    }

    /// <summary>Verifies navigation drains queued writes, merges untouched saved choices, and restores the final choices in a new panel instance.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PhysicalDevices_NavigationDuringPendingStorage_PersistsFinalPreferences(bool restoring)
    {
        // Arrange
        await using var context = CreateContext(configureDeviceStorage: false);
        var devices = new[] { LinkStatus(null), LinkStatus(null), LinkStatus(null) };
        var keys = devices.Select(device => device.Device.Id.ToString()).ToArray();
        var saved = JsonSerializer.Serialize(new Dictionary<string, bool> { [keys[0]] = true, [keys[1]] = true, [keys[2]] = false });
        var restore = context.JSInterop.Setup<string?>("localStorage.getItem", DeviceStorageKey);
        if (!restoring) restore.SetResult(saved);
        var write = context.JSInterop.SetupVoid("localStorage.setItem", _ => true);
        var component = context.Render<DashboardPhysicalDevicesPanel>(parameters => parameters
            .Add(panel => panel.Expanded, true)
            .Add(panel => panel.Summary, "Devices")
            .Add(panel => panel.Devices, devices));
        Task first = Task.CompletedTask;
        Task second = Task.CompletedTask;
        Task disposal = Task.CompletedTask;
        string? persisted = null;
        try
        {
            // Act
            first = component.Find($"[data-physical-device='{keys[0]}'] > .card-header button").ClickAsync(new());
            component.WaitForAssertion(() => Assert.True(component.Find($"[data-physical-device='{keys[0]}'] > .card-body").HasAttribute("hidden")),
                timeout: LineupWebBunitContext.WaitTimeout);
            if (!restoring) Assert.Single(context.JSInterop.Invocations, invocation => invocation.Identifier == "localStorage.setItem");
            second = component.Find($"[data-physical-device='{keys[1]}'] > .card-header button").ClickAsync(new());
            component.WaitForAssertion(() => Assert.True(component.Find($"[data-physical-device='{keys[1]}'] > .card-body").HasAttribute("hidden")),
                timeout: LineupWebBunitContext.WaitTimeout);
            disposal = context.DisposeComponentsAsync();

            // Assert
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            Assert.False(disposal.IsCompleted);

            // Act
            if (restoring) restore.SetResult(saved);
            write.SetVoidResult();
            await Task.WhenAll(first, second, disposal).WaitAsync(LineupWebBunitContext.WaitTimeout, Xunit.TestContext.Current.CancellationToken);
            var writes = context.JSInterop.Invocations.Where(invocation => invocation.Identifier == "localStorage.setItem").ToArray();

            // Assert
            Assert.Equal(2, writes.Length);
            persisted = Assert.IsType<string>(writes.Last().Arguments[1]);
            var values = JsonSerializer.Deserialize<Dictionary<string, bool>>(persisted)!;
            Assert.All(keys, key => Assert.False(values[key]));
        }
        finally
        {
            if (restoring) restore.SetResult(saved);
            write.SetVoidResult();
            await Task.WhenAll(first, second, disposal).WaitAsync(LineupWebBunitContext.WaitTimeout, CancellationToken.None);
        }

        // Act
        await using var restoredContext = CreateContext(savedDeviceState: persisted);
        var restored = restoredContext.Render<DashboardPhysicalDevicesPanel>(parameters => parameters
            .Add(panel => panel.Expanded, true)
            .Add(panel => panel.Summary, "Devices")
            .Add(panel => panel.Devices, devices.Reverse().ToArray()));

        // Assert
        Assert.All(restored.FindAll("#deviceSection > .card > .card-body"), body => Assert.True(body.HasAttribute("hidden")));
    }

    /// <summary>Verifies malformed saved preferences are logged and do not disable device-card toggles.</summary>
    [Theory]
    [InlineData("{invalid")]
    [InlineData("""{"device":"invalid"}""")]
    public async Task PhysicalDevices_InvalidSavedState_LogsAndKeepsControlsUsable(string saved)
    {
        // Arrange
        await using var context = CreateContext(savedDeviceState: saved);
        var logger = Substitute.For<ILogger<DashboardPhysicalDevicesPanel>>();
        context.Services.AddSingleton(logger);
        var device = LinkStatus(null);
        var component = context.Render<DashboardPhysicalDevicesPanel>(parameters => parameters
            .Add(panel => panel.Expanded, true)
            .Add(panel => panel.Summary, "Devices")
            .Add(panel => panel.Devices, [device]));
        Assert.False(component.Find("#deviceSection > .card > .card-body").HasAttribute("hidden"));

        // Act
        await component.Find("#deviceSection > .card > .card-header button").ClickAsync(new());

        // Assert
        Assert.True(component.Find("#deviceSection > .card > .card-body").HasAttribute("hidden"));
        Assert.Single(logger.ReceivedCalls(), call => call.GetMethodInfo().Name == "Log" && call.GetArguments()[0] is LogLevel.Warning);
        var write = Assert.Single(context.JSInterop.Invocations, invocation => invocation.Identifier == "localStorage.setItem");
        Assert.False(JsonSerializer.Deserialize<Dictionary<string, bool>>(Assert.IsType<string>(write.Arguments[1]))![device.Device.Id.ToString()]);
    }

    /// <summary>Verifies failed browser reads and writes are logged and repeated toggles remain functional.</summary>
    [Fact]
    public async Task PhysicalDevices_BrowserStorageUnavailable_LogsAndKeepsControlsUsable()
    {
        // Arrange
        await using var context = CreateContext();
        var store = Substitute.For<IBrowserDataStore>();
        store.ReadAsync<Dictionary<string, bool>>(DeviceStorageKey, Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromException<Dictionary<string, bool>?>(new JSException("Storage denied.")));
        store.WriteAsync(DeviceStorageKey, Arg.Any<Dictionary<string, bool>>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromException(new JSException("Storage denied.")));
        context.Services.AddSingleton(store);
        var logger = Substitute.For<ILogger<DashboardPhysicalDevicesPanel>>();
        context.Services.AddSingleton(logger);
        var component = context.Render<DashboardPhysicalDevicesPanel>(parameters => parameters
            .Add(panel => panel.Expanded, true)
            .Add(panel => panel.Summary, "Devices")
            .Add(panel => panel.Devices, [LinkStatus(null)]));

        // Act
        await component.Find("#deviceSection > .card > .card-header button").ClickAsync(new());

        // Assert
        Assert.True(component.Find("#deviceSection > .card > .card-body").HasAttribute("hidden"));

        // Act
        await component.Find("#deviceSection > .card > .card-header button").ClickAsync(new());

        // Assert
        Assert.False(component.Find("#deviceSection > .card > .card-body").HasAttribute("hidden"));
        Assert.Equal(3, logger.ReceivedCalls().Count(call => call.GetMethodInfo().Name == "Log" && call.GetArguments()[0] is LogLevel.Warning));
    }

    private static PhysicalDeviceRuntimeStatus LinkStatus(IReadOnlyList<UpstreamPhysicalDeviceStatus>? upstream)
    {
        var device = new PhysicalDeviceSnapshot { Id = PhysicalDeviceId.New(), Name = "Remote Lineup Link", Kind = "lineup-link", Capacity = new PhysicalTunerCapacity(4) };
        return new PhysicalDeviceRuntimeStatus
        {
            Device = device,
            SupportsReceiverControl = true,
            Status = new PhysicalDeviceStatusSnapshot
            {
                Device = device,
                ReceiversAreStreamSlots = true,
                UpstreamDevices = upstream,
                Receivers = Enumerable.Range(0, 4).Select(index => new PhysicalReceiverStatus
                {
                    Index = index,
                    IsActive = index < 3,
                    IsStreaming = index < 3,
                    HasLock = index < 3,
                    SignalStrength = index switch { 0 => 99, 1 => 0, _ => null },
                    VirtualChannel = index < 3 ? $"2.{index + 1}" : null
                }).ToArray()
            }
        };
    }

    private static BunitContext CreateContext(
        IActiveStreamRegistry? activeStreams = null,
        ITopologyGuideService? guides = null,
        StreamingFlowSnapshot? streamingFlow = null,
        IPhysicalDeviceStatusService? deviceStatuses = null,
        string? savedDeviceState = null,
        bool configureDeviceStorage = true
    )
    {
        var context = new BunitContext();
        if (configureDeviceStorage)
        {
            context.JSInterop.Setup<string?>("localStorage.getItem", DeviceStorageKey).SetResult(savedDeviceState);
            context.JSInterop.SetupVoid("localStorage.setItem", _ => true).SetVoidResult();
        }
        var repository = Substitute.For<ILineupGuideReader>();
        var guideStart = DateTimeOffset.UtcNow;
        repository.GetStatisticsAsync(Arg.Any<CancellationToken>()).Returns(new GuideStatistics(
            12,
            345,
            guideStart,
            guideStart.AddDays(2).AddHours(3),
            guideStart.AddDays(2).AddHours(3)));

        var settings = Substitute.For<ILineupSettingsService>();
        settings.Settings.Returns(new LineupSettings
        {
            RefreshChannelsBeforeGuideFetch = false
        });

        var deviceState = Substitute.For<IPhysicalDeviceStatusService>();
        deviceState.Devices.Returns([]);
        var autoFetchState = Substitute.For<IAutoFetchStateService>();
        autoFetchState.IsEnabled.Returns(false);
        var timeZone = Substitute.For<ITimeZoneService>();
        timeZone.ConvertFromUtc(Arg.Any<DateTime>()).Returns(call => call.Arg<DateTime>());
        if (activeStreams is null)
        {
            activeStreams = Substitute.For<IActiveStreamRegistry>();
            activeStreams.GetActiveStreams().Returns([]);
        }

        var options = new DbContextOptionsBuilder<EpgDbContext>()
            .UseSqlite($"Data Source={Path.Combine(Path.GetTempPath(), $"lineup-dashboard-{Guid.NewGuid():N}.db")}")
            .Options;
        var store = new StreamingTopologyStore(new PooledDbContextFactory<EpgDbContext>(options));
        var refreshService = Substitute.For<IPhysicalLineupService>();
        refreshService.RefreshAsync(Arg.Any<StreamingConfiguration>(), Arg.Any<CancellationToken>()).Returns([]);
        guides ??= Substitute.For<ITopologyGuideService>();

        context.Services.AddSingleton(repository);
        context.Services.AddSingleton(settings);
        var webSettings = Substitute.For<IWebSettingsService>();
        webSettings.Settings.Returns(new WebSettings { IsSetupComplete = true, ActiveStreamRefreshIntervalSeconds = 0 });
        context.Services.AddSingleton(webSettings);
        context.Services.AddSingleton(deviceStatuses ?? deviceState);
        context.Services.AddSingleton(autoFetchState);
        context.Services.AddSingleton(timeZone);
        context.Services.AddSingleton(activeStreams);
        var flowSnapshots = Substitute.For<IStreamingFlowSnapshotService>();
        flowSnapshots.GetSnapshot().Returns(streamingFlow ?? new StreamingFlowSnapshot([], [], 0, 0, 0));
        context.Services.AddSingleton(flowSnapshots);
        context.Services.AddSingleton(store);
        context.Services.AddSingleton(refreshService);
        context.Services.AddSingleton(guides);
        context.Services.AddSingleton<IStatusNotificationService>(new StatusNotificationService());
        context.Services.AddScoped<IBrowserDataStore, BrowserDataStore>();
        context.Services.AddSingleton<IXmltvPublicationStore, XmltvPublicationStore>();
        return context;
    }

    private sealed class DisposedServiceProvider : IServiceProvider
    {
        /// <inheritdoc />
        public object? GetService(Type serviceType) => throw new ObjectDisposedException(nameof(IServiceProvider));
    }
}
