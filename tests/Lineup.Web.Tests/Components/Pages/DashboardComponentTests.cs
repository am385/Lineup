using System.Text.Json;
using Bunit;
using Lineup.Core;
using Lineup.Core.Storage;
using Lineup.HDHomeRun.Device;
using Lineup.HDHomeRun.Device.Models;
using Lineup.Web.Components.Pages;
using Lineup.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
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
            Assert.Contains("Discovering · tuner.local", component.Markup);
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
    public void ToggleSection_PersistsUpdatedState(string toggleId, string collapsedProperty)
    {
        // Arrange
        using var context = CreateContext();
        context.JSInterop.Setup<string?>("localStorage.getItem", StorageKey).SetResult(null);
        context.JSInterop.SetupVoid("localStorage.setItem", _ => true).SetVoidResult();
        var component = context.Render<Dashboard>();

        // Act
        component.Find($"#{toggleId}").Click();

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
    /// Verifies Dashboard refreshes and displays the physical channel lineup without navigating away.
    /// </summary>
    [Fact]
    public async Task RefreshChannels_UpdatesDashboardLineup()
    {
        // Arrange
        var provider = Substitute.For<IChannelLineupProvider>();
        provider.FetchChannelLineupAsync(Arg.Any<CancellationToken>())
            .Returns(
            [
                new HDHomeRunChannel { GuideNumber = "7.1", GuideName = "One", URL = "http://tuner.local/auto/v7.1" },
                new HDHomeRunChannel { GuideNumber = "9.1", GuideName = "Two", URL = "http://tuner.local/auto/v9.1" }
            ]);
        using var context = CreateContext(provider);
        context.JSInterop.Setup<string?>("localStorage.getItem", StorageKey).SetResult(null);
        var component = context.Render<Dashboard>();

        // Act
        component.Find("#refreshChannels").Click();

        // Assert
        await provider.Received(1).FetchChannelLineupAsync(Arg.Any<CancellationToken>());
        component.WaitForAssertion(() =>
        {
            Assert.Contains("Refreshed 2 tuner channels.", component.Markup);
            Assert.Equal("2", component.Find("#channelsSection .text-success").TextContent.Trim());
            Assert.DoesNotContain(component.FindAll("a"), link => link.GetAttribute("href") == "/channels");
        });
    }

    private static BunitContext CreateContext(IChannelLineupProvider? channelProvider = null)
    {
        var context = new BunitContext();
        var repository = Substitute.For<IEpgRepository>();
        repository.GetCacheStatisticsAsync().Returns(new CacheStatistics(12, 345, null, null, new TimeSpan(2, 3, 0, 0)));
        repository.GetSafeFetchStartTimeAsync().Returns((DateTime?)null);

        var settings = Substitute.For<IAppSettingsService>();
        settings.Settings.Returns(new AppSettings
        {
            IsSetupComplete = true,
            DeviceAddress = "tuner.local",
            ActiveStreamRefreshIntervalSeconds = 0
        });

        var deviceState = Substitute.For<IDeviceStateService>();
        deviceState.TunerStatuses.Returns([]);
        deviceState.LastError.Returns((string?)null);
        var autoFetchState = Substitute.For<IAutoFetchStateService>();
        autoFetchState.IsEnabled.Returns(false);
        var timeZone = Substitute.For<ITimeZoneService>();
        timeZone.ConvertFromUtc(Arg.Any<DateTime>()).Returns(call => call.Arg<DateTime>());
        var activeStreams = Substitute.For<IActiveStreamRegistry>();
        activeStreams.GetActiveStreams().Returns([]);

        var store = new ChannelLineupStore(Path.Combine(Path.GetTempPath(), $"lineup-dashboard-{Guid.NewGuid():N}.db"));
        var refreshService = new ChannelLineupRefreshService(
            NullLogger<ChannelLineupRefreshService>.Instance,
            channelProvider ?? Substitute.For<IChannelLineupProvider>(),
            store);
        var orchestrator = new EpgOrchestrator(
            NullLogger<EpgOrchestrator>.Instance,
            store,
            null!,
            repository,
            new LineupXmltvWriter(),
            Substitute.For<IXmltvPublicationStore>());

        context.Services.AddSingleton(repository);
        context.Services.AddSingleton(settings);
        context.Services.AddSingleton(deviceState);
        context.Services.AddSingleton(autoFetchState);
        context.Services.AddSingleton(timeZone);
        context.Services.AddSingleton(activeStreams);
        context.Services.AddSingleton(store);
        context.Services.AddSingleton(refreshService);
        context.Services.AddSingleton(orchestrator);
        context.Services.AddScoped<IBrowserDataStore, BrowserDataStore>();
        context.Services.AddSingleton<IXmltvPublicationStore, XmltvPublicationStore>();
        return context;
    }
}
