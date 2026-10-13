using Bunit;
using Lineup.Web.Components.Pages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using System.Text.Json;
using Xunit;

namespace Lineup.Web.Tests.Components.Pages;

/// <summary>Verifies channel searching, filtering, and sorting.</summary>
public sealed class ChannelsTests
{
    /// <summary>Verifies search and channel filters narrow the displayed physical sources.</summary>
    [Fact]
    public async Task Filters_SearchDeviceStateAndFlags_NarrowRows()
    {
        // Arrange
        using var fixture = await ChannelFixture.CreateAsync();
        var component = fixture.Context.Render<Channels>();
        component.WaitForAssertion(() => Assert.Equal(3, ChannelRows(component).Count));

        // Act
        component.Find("#channelSearch").Input("sports");

        // Assert
        component.WaitForAssertion(() => Assert.Equal(["sports"], ProviderKeys(component)));

        // Act
        component.Find("#clearChannelFilters").Click();
        component.Find("#channelDeviceFilter").Change(fixture.BetaDeviceId.ToString());

        // Assert
        component.WaitForAssertion(() => Assert.Equal(["movies"], ProviderKeys(component)));

        // Act
        component.Find("#clearChannelFilters").Click();
        component.Find("#channelStateFilter").Change("Disabled");

        // Assert
        component.WaitForAssertion(() => Assert.Equal(["movies"], ProviderKeys(component)));

        // Act
        component.Find("#clearChannelFilters").Click();
        component.Find("#channelFlagFilter").Change("Favorite");

        // Assert
        component.WaitForAssertion(() => Assert.Equal(["sports"], ProviderKeys(component)));
    }

    /// <summary>Verifies sortable headers toggle ascending and descending column order.</summary>
    [Fact]
    public async Task Sort_GlobalNumberHeader_TogglesNumericDirection()
    {
        // Arrange
        using var fixture = await ChannelFixture.CreateAsync();
        var component = fixture.Context.Render<Channels>();
        component.WaitForAssertion(() => Assert.Equal(3, ChannelRows(component).Count));
        var globalNumber = component.FindAll("thead button").Single(button => button.TextContent.Contains("Global number", StringComparison.Ordinal));

        // Act
        globalNumber.Click();

        // Assert
        component.WaitForAssertion(() => Assert.Equal(["news", "movies", "sports"], ProviderKeys(component)));
        Assert.Equal("ascending", FindGlobalNumberHeader(component).ParentElement!.GetAttribute("aria-sort"));

        // Act
        FindGlobalNumberHeader(component).Click();

        // Assert
        component.WaitForAssertion(() => Assert.Equal(["sports", "movies", "news"], ProviderKeys(component)));
        Assert.Equal("descending", FindGlobalNumberHeader(component).ParentElement!.GetAttribute("aria-sort"));
    }

    /// <summary>Verifies the sortable header inherits the active light or dark theme.</summary>
    [Fact]
    public async Task Render_SortableHeader_DoesNotForceLightTheme()
    {
        // Arrange
        using var fixture = await ChannelFixture.CreateAsync();

        // Act
        var component = fixture.Context.Render<Channels>();

        // Assert
        component.WaitForAssertion(() => Assert.DoesNotContain("table-light", component.Find("thead").ClassList));
        Assert.All(component.FindAll("thead button"), button => Assert.Contains("text-body", button.ClassList));
    }

    /// <summary>Verifies guide selections use the complete guide-source identity when provider keys collide.</summary>
    [Fact]
    public async Task GuideBinding_DuplicateProviderKeys_SelectsRequestedGuideSource()
    {
        // Arrange
        using var fixture = await ChannelFixture.CreateAsync();
        var component = fixture.Context.Render<Channels>();
        component.WaitForAssertion(() => Assert.Contains("Multiple eligible guide channels match", component.Markup, StringComparison.Ordinal));
        var select = component.Find("tr[data-provider-key='news'] select.guide-binding");
        var firstValue = $"{fixture.FirstGuideId.GuideSourceId.Value:D}|{fixture.FirstGuideId.ProviderKey}";
        var secondValue = $"{fixture.SecondGuideId.GuideSourceId.Value:D}|{fixture.SecondGuideId.ProviderKey}";

        // Act
        select.Change(secondValue);

        // Assert
        component.WaitForAssertion(() => Assert.Contains("Manual override.", component.Markup, StringComparison.Ordinal));
        var binding = (await fixture.GuideStore.ReadBindingStatesAsync(Xunit.TestContext.Current.CancellationToken))
            .Single(state => state.SourceChannelId.ProviderKey == "news");
        Assert.NotEqual(firstValue, secondValue);
        Assert.Equal(GuideBindingState.Manual, binding.State);
        Assert.Equal(fixture.SecondGuideId, binding.SelectedGuideChannelId);

        // Act
        component.Find("tr[data-provider-key='news'] select.guide-binding").Change(string.Empty);

        // Assert
        component.WaitForAssertion(() => Assert.Contains("Multiple eligible guide channels match", component.Markup, StringComparison.Ordinal));
        var restored = (await fixture.GuideStore.ReadBindingStatesAsync(Xunit.TestContext.Current.CancellationToken))
            .Single(state => state.SourceChannelId.ProviderKey == "news");
        Assert.Equal(GuideBindingState.NeedsGuideSelection, restored.State);
        Assert.Null(restored.SelectedGuideChannelId);
    }

    /// <summary>Verifies channels without an automatic match can still select any eligible guide channel manually.</summary>
    [Fact]
    public async Task GuideBinding_NoAutomaticMatch_AllowsManualSelection()
    {
        // Arrange
        using var fixture = await ChannelFixture.CreateAsync();
        var component = fixture.Context.Render<Channels>();
        component.WaitForAssertion(() =>
            Assert.Contains("No provider identity or number matched automatically", component.Markup, StringComparison.Ordinal));
        var select = component.Find("tr[data-provider-key='sports'] select.guide-binding");
        var selectedValue = $"{fixture.FirstGuideId.GuideSourceId.Value:D}|{fixture.FirstGuideId.ProviderKey}";

        // Act
        select.Change(selectedValue);

        // Assert
        component.WaitForAssertion(() =>
            Assert.Contains("Manual override.", component.Find("tr[data-provider-key='sports']").TextContent, StringComparison.Ordinal));
        var binding = (await fixture.GuideStore.ReadBindingStatesAsync(Xunit.TestContext.Current.CancellationToken))
            .Single(state => state.SourceChannelId.ProviderKey == "sports");
        Assert.Equal(GuideBindingState.Manual, binding.State);
        Assert.Equal(fixture.FirstGuideId, binding.SelectedGuideChannelId);
    }

    private static IReadOnlyList<AngleSharp.Dom.IElement> ChannelRows(IRenderedComponent<Channels> component) =>
        component.FindAll("tr.topology-channel");

    private static string[] ProviderKeys(IRenderedComponent<Channels> component) =>
        ChannelRows(component).Select(row => row.GetAttribute("data-provider-key")!).ToArray();

    private static AngleSharp.Dom.IElement FindGlobalNumberHeader(IRenderedComponent<Channels> component) =>
        component.FindAll("thead button").Single(button => button.TextContent.Contains("Global number", StringComparison.Ordinal));

    private sealed class ChannelFixture : IDisposable
    {
        private readonly string _databasePath;

        private ChannelFixture(
            BunitContext context,
            string databasePath,
            PhysicalDeviceId betaDeviceId,
            DeviceGuideStore guideStore,
            GuideChannelId firstGuideId,
            GuideChannelId secondGuideId)
        {
            Context = context;
            _databasePath = databasePath;
            BetaDeviceId = betaDeviceId;
            GuideStore = guideStore;
            FirstGuideId = firstGuideId;
            SecondGuideId = secondGuideId;
        }

        public BunitContext Context { get; }

        public PhysicalDeviceId BetaDeviceId { get; }

        public DeviceGuideStore GuideStore { get; }

        public GuideChannelId FirstGuideId { get; }

        public GuideChannelId SecondGuideId { get; }

        public static async Task<ChannelFixture> CreateAsync()
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"lineup-channels-{Guid.NewGuid():N}.db");
            var options = new DbContextOptionsBuilder<EpgDbContext>().UseSqlite($"Data Source={databasePath};Pooling=False").Options;
            var contexts = new TestDbContextFactory(options);
            var topology = new StreamingTopologyStore(contexts);
            var guides = new DeviceGuideStore(contexts);
            var alpha = PhysicalDeviceId.New();
            var beta = PhysicalDeviceId.New();
            await topology.ReconcileAsync(
                alpha,
                [
                    Channel(alpha, "sports", "10.1", "Sports", favorite: true, videoCodec: "h264", signalStrength: 80),
                    Channel(alpha, "news", "2.1", "News", hd: true, videoCodec: "mpeg2video", signalStrength: 90)
                ],
                Xunit.TestContext.Current.CancellationToken);
            await topology.ReconcileAsync(
                beta,
                [Channel(beta, "movies", "7.1", "Movies", drm: true, videoCodec: "hevc", signalStrength: 70)],
                Xunit.TestContext.Current.CancellationToken);
            await topology.SetEnabledAsync(new SourceChannelId(beta, "movies"), false, Xunit.TestContext.Current.CancellationToken);
            var firstGuideId = new GuideChannelId(GuideSourceId.New(), "shared");
            var secondGuideId = new GuideChannelId(GuideSourceId.New(), "shared");
            await guides.ImportAsync(GuideImport(firstGuideId, "First guide"), Xunit.TestContext.Current.CancellationToken);
            await guides.ImportAsync(GuideImport(secondGuideId, "Second guide"), Xunit.TestContext.Current.CancellationToken);

            var settings = Substitute.For<ILineupSettingsService>();
            settings.Settings.Returns(new LineupSettings
            {
                IsSetupComplete = true,
                Streaming = new StreamingConfiguration
                {
                    PhysicalDevices =
                    [
                        Physical(alpha, "Alpha"),
                        Physical(beta, "Beta")
                    ],
                    GuideSources =
                    [
                        GuideSource(firstGuideId.GuideSourceId, "First Source", alpha),
                        GuideSource(secondGuideId.GuideSourceId, "Second Source", alpha)
                    ]
                }
            });
            var guideDriver = Substitute.For<IGuideSourceDriver>();
            guideDriver.Kind.Returns("test-guide");
            guideDriver.SupportsPhysicalAdapter("test").Returns(true);
            var adapters = new StreamingAdapterRegistry([], [], [guideDriver]);
            var timeZone = Substitute.For<ITimeZoneService>();
            timeZone.ConvertFromUtc(Arg.Any<DateTime>()).Returns(call => call.Arg<DateTime>());
            var refresh = Substitute.For<IPhysicalLineupService>();
            refresh.RefreshAsync(Arg.Any<StreamingConfiguration>(), Arg.Any<CancellationToken>()).Returns([]);
            var context = new BunitContext();
            context.Services.AddSingleton(topology);
            context.Services.AddSingleton(guides);
            context.Services.AddSingleton<IStreamingAdapterRegistry>(adapters);
            context.Services.AddSingleton(refresh);
            context.Services.AddSingleton(Substitute.For<ITopologyGuideService>());
            context.Services.AddSingleton(settings);
            context.Services.AddSingleton<IXmltvPublicationStore, XmltvPublicationStore>();
            context.Services.AddSingleton(timeZone);
            return new ChannelFixture(context, databasePath, beta, guides, firstGuideId, secondGuideId);
        }

        public void Dispose()
        {
            Context.Dispose();
            File.Delete(_databasePath);
        }

        private static PhysicalSourceChannel Channel(
            PhysicalDeviceId deviceId,
            string providerKey,
            string number,
            string name,
            bool favorite = false,
            bool drm = false,
            bool hd = false,
            string? videoCodec = null,
            int? signalStrength = null)
        {
            var metadata = new Dictionary<string, string>();
            if (favorite)
            {
                metadata["favorite"] = bool.TrueString;
            }
            if (drm)
            {
                metadata["drm"] = bool.TrueString;
            }
            if (hd)
            {
                metadata["hd"] = bool.TrueString;
            }
            if (videoCodec is not null)
            {
                metadata["videoCodec"] = videoCodec;
            }
            if (signalStrength.HasValue)
            {
                metadata["signalStrength"] = signalStrength.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            return new PhysicalSourceChannel
            {
                Id = new SourceChannelId(deviceId, providerKey),
                ProviderNumber = number,
                ProviderName = name,
                Metadata = metadata
            };
        }

        private static PhysicalDeviceDefinition Physical(PhysicalDeviceId id, string name) =>
            new()
            {
                Id = id,
                Name = name,
                Adapter = new AdapterConfiguration("test", JsonSerializer.SerializeToElement(new { }))
            };

        private static GuideSourceDefinition GuideSource(GuideSourceId id, string name, PhysicalDeviceId physicalDeviceId) =>
            new()
            {
                Id = id,
                Name = name,
                Adapter = new AdapterConfiguration("test-guide", JsonSerializer.SerializeToElement(new { })),
                Subscription = new PhysicalDeviceSubscription
                {
                    Mode = PhysicalDeviceSubscriptionMode.Selected,
                    SelectedDeviceIds = [physicalDeviceId]
                }
            };

        private static GuideImport GuideImport(GuideChannelId channelId, string name)
        {
            var start = DateTimeOffset.Parse("2026-10-02T12:00:00Z");
            return new GuideImport
            {
                SourceId = channelId.GuideSourceId,
                Channels = [new GuideChannel { Id = channelId, Number = "2.1", Name = name }],
                Programs = [],
                CoverageStartUtc = start,
                CoverageEndUtc = start.AddDays(1)
            };
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<EpgDbContext> options) : IDbContextFactory<EpgDbContext>
    {
        public EpgDbContext CreateDbContext() => new(options);

        public Task<EpgDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
