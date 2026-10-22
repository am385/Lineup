using System.Reflection;
using System.Text.Json;
using Bunit;
using Lineup.Web.Components.Pages;
using Lineup.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using NSubstitute;
using Xunit;

namespace Lineup.Web.Tests.Components.Pages;

/// <summary>
/// Verifies dashboard section presentation and browser-persisted collapse state.
/// </summary>
public class DashboardComponentTests
{
    private const string StorageKey = "lineup-dashboard-sections-v1";

    /// <summary>
    /// Verifies dashboard disposal does not resolve services after the host service provider has shut down.
    /// </summary>
    [Fact]
    public void Dispose_ServiceProviderUnavailable_DoesNotResolveServices()
    {
        // Arrange
        using var context = CreateContext();
        context.JSInterop.Setup<string?>("localStorage.getItem", StorageKey).SetResult(null);
        var component = context.Render<Dashboard>();
        var servicesProperty = typeof(Dashboard).GetProperty("Services", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(servicesProperty);
        servicesProperty.SetValue(component.Instance, new DisposedServiceProvider());

        // Act
        var exception = Record.Exception(component.Instance.Dispose);

        // Assert
        Assert.Null(exception);
    }

    /// <summary>
    /// Verifies a browser without saved preferences sees every persistent section expanded.
    /// </summary>
    [Fact]
    public void Sections_NoSavedState_StartExpanded()
    {
        // Arrange
        using var context = CreateContext();
        context.JSInterop.Setup<string?>("localStorage.getItem", StorageKey).SetResult(null);

        // Act
        var component = context.Render<Dashboard>();

        // Assert
        Assert.All(
            new[] { "toggleChannelsSection", "toggleGuideSection", "toggleDeviceSection", "toggleActiveStreamsSection" },
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
    public void Sections_SavedCollapsedState_RestoresSummaries()
    {
        // Arrange
        using var context = CreateContext();
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
        });
    }

    /// <summary>
    /// Verifies each restored preference is mapped to its corresponding section.
    /// </summary>
    [Fact]
    public void Sections_MixedSavedState_RestoresIndependently()
    {
        // Arrange
        using var context = CreateContext();
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
        });
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
        using var context = CreateContext();
        context.JSInterop.Setup<string?>("localStorage.getItem", StorageKey).SetResult(null);
        context.JSInterop.SetupVoid("localStorage.setItem", _ => true).SetVoidResult();
        var component = context.Render<Dashboard>();

        // Act
        await component.Find($"#{toggleId}").ClickAsync(new());

        // Assert
        component.WaitForAssertion(() => Assert.Equal("false", component.Find($"#{toggleId}").GetAttribute("aria-expanded")));
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
    public void Sections_InvalidSavedState_FallBackToExpanded()
    {
        // Arrange
        using var context = CreateContext();
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
        using var context = CreateContext(guides: guides);
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
        });
    }

    /// <summary>
    /// Verifies shared CMAF presentations are not mislabeled as HLS-only sessions.
    /// </summary>
    [Fact]
    public void ActiveStreams_CmafPresentation_UsesCmafLabel()
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
                new StreamingFlowNode("multiplexer", StreamingFlowLane.Multiplexer, "Shared Multiplexer", "Reference-counted stream graph", StreamingFlowState.Active, [])
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
        using var context = CreateContext(activeStreams: activeStreams, streamingFlow: snapshot);
        context.JSInterop.Setup<string?>("localStorage.getItem", StorageKey).SetResult(null);

        // Act
        var component = context.Render<Dashboard>();

        // Assert
        Assert.Contains("CMAF", component.Find("#activeStreamsSection").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("HLS", component.Find("#activeStreamsSection").TextContent, StringComparison.Ordinal);
        Assert.Contains("session", component.Find(".streaming-flow-path").TextContent, StringComparison.Ordinal);
        var cards = component.FindAll("#activeStreamsSection > .card");
        Assert.Equal(2, cards.Count);
        Assert.Contains("Streaming Flow", cards[0].QuerySelector(".card-header")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("Active Paths", cards[1].QuerySelector(".card-header")!.TextContent, StringComparison.Ordinal);
        Assert.Empty(component.FindAll("#activeStreamsSection table"));
    }

    private static BunitContext CreateContext(
        IActiveStreamRegistry? activeStreams = null,
        ITopologyGuideService? guides = null,
        StreamingFlowSnapshot? streamingFlow = null)
    {
        var context = new BunitContext();
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
            IsSetupComplete = true,
            ActiveStreamRefreshIntervalSeconds = 0,
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
        context.Services.AddSingleton(deviceState);
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
        public object? GetService(Type serviceType) => throw new ObjectDisposedException(nameof(IServiceProvider));
    }
}
