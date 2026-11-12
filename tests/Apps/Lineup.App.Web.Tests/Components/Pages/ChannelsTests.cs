using Bunit;
using Lineup.App.Web.Components.Pages;
using Lineup.App.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using System.Text.Json;
using Xunit;

namespace Lineup.App.Web.Tests.Components.Pages;

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

    /// <summary>Verifies explicit same-content confirmation, group additions, and removal preserve guide choices and global identity.</summary>
    [Fact]
    public async Task Grouping_CreateAddRemove_RequiresConfirmationAndPreservesBindings()
    {
        // Arrange
        using var fixture = await ChannelFixture.CreateAsync();
        var (topology, news, movies) = await PrepareGrouping(fixture);
        await fixture.GuideStore.SetBindingAsync(news, fixture.FirstGuideId, Xunit.TestContext.Current.CancellationToken);
        var component = fixture.Context.Render<Channels>();
        component.WaitForAssertion(() => Assert.Equal(3, ChannelRows(component).Count));

        // Act
        await component.Find("tr[data-provider-key='news'] .manage-channel-group").ClickAsync(new());
        component.Find(".group-candidate[data-provider-key='movies'] input").Change(true);

        // Assert
        Assert.Empty(component.FindAll(".channel-equivalence-group"));
        Assert.True(component.Find(".save-channel-group").HasAttribute("disabled"));
        Assert.All(await topology.ReadActiveAsync(Xunit.TestContext.Current.CancellationToken), channel => Assert.Null(channel.EquivalenceGroupId));
        var preview = component.Find("[data-output-name='All devices']");
        Assert.Single(preview.QuerySelectorAll(".group-projection")[0].QuerySelectorAll(".group-source-priority li"));
        Assert.Equal(2, preview.QuerySelectorAll(".group-projection")[1].QuerySelectorAll(".group-source-priority li").Length);
        Assert.Contains("First guide", preview.TextContent, StringComparison.Ordinal);

        // Act
        component.Find(".confirm-channel-content").Change(true);
        await component.Find(".save-channel-group").ClickAsync(new());
        var created = await topology.ReadActiveAsync(Xunit.TestContext.Current.CancellationToken);
        var group = created.Single(channel => channel.Source.Id == news).EquivalenceGroupId;
        var backup = new PhysicalSourceChannel
        {
            Id = new SourceChannelId(fixture.BetaDeviceId, "backup"),
            ProviderNumber = "2.1",
            ProviderName = "News backup"
        };
        await topology.ReconcileAsync(fixture.BetaDeviceId, [created.Single(channel => channel.Source.Id == movies).Source, backup], Xunit.TestContext.Current.CancellationToken);
        var sports = created.Single(channel => channel.Source.Id.ProviderKey == "sports").Source.Id;
        await topology.SetIdentityAsync(sports, "2.1", null, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);
        await component.Find("tr[data-provider-key='news'] .manage-channel-group").ClickAsync(new());
        Assert.True(component.Find(".group-candidate[data-provider-key='sports'] input").HasAttribute("disabled"));
        component.Find(".group-candidate[data-provider-key='backup'] input").Change(true);
        component.Find(".confirm-channel-content").Change(true);
        await component.Find(".save-channel-group").ClickAsync(new());
        var added = await topology.ReadActiveAsync(Xunit.TestContext.Current.CancellationToken);
        await component.Find("tr[data-provider-key='movies'] .manage-channel-group").ClickAsync(new());
        component.Find(".change-group-mode").Click();
        Assert.True(component.Find(".save-channel-group").HasAttribute("disabled"));
        component.Find(".confirm-channel-content").Change(true);
        await component.Find(".save-channel-group").ClickAsync(new());

        // Assert
        Assert.NotNull(group);
        Assert.Equal(group, created.Single(channel => channel.Source.Id == movies).EquivalenceGroupId);
        Assert.Equal(group, added.Single(channel => channel.Source.Id == backup.Id).EquivalenceGroupId);
        var removed = await topology.ReadActiveAsync(Xunit.TestContext.Current.CancellationToken);
        Assert.Null(removed.Single(channel => channel.Source.Id == movies).EquivalenceGroupId);
        Assert.Equal(group, removed.Single(channel => channel.Source.Id == news).EquivalenceGroupId);
        Assert.Equal("2.1", removed.Single(channel => channel.Source.Id == movies).NumberOverride);
        var binding = (await fixture.GuideStore.ReadBindingStatesAsync(Xunit.TestContext.Current.CancellationToken)).Single(binding => binding.SourceChannelId == news);
        Assert.Equal(fixture.FirstGuideId, binding.SelectedGuideChannelId);
        Assert.Empty(component.FindAll(".channel-equivalence-editor"));
    }

    /// <summary>Verifies changed saved topology, guide choices, and configuration invalidate a confirmed preview.</summary>
    [Theory]
    [InlineData("topology")]
    [InlineData("guide")]
    [InlineData("configuration")]
    [InlineData("subscription")]
    public async Task Grouping_StalePreview_RejectsSave(string changed)
    {
        // Arrange
        using var fixture = await ChannelFixture.CreateAsync();
        var (topology, news, movies) = await PrepareGrouping(fixture);
        var component = fixture.Context.Render<Channels>();
        component.WaitForAssertion(() => Assert.Equal(3, ChannelRows(component).Count));
        await component.Find("tr[data-provider-key='news'] .manage-channel-group").ClickAsync(new());
        component.Find(".group-candidate[data-provider-key='movies'] input").Change(true);
        component.Find(".confirm-channel-content").Change(true);
        if (changed == "topology")
        {
            await topology.SetEnabledAsync(movies, false, Xunit.TestContext.Current.CancellationToken);
        }
        else if (changed == "guide")
        {
            await fixture.GuideStore.SetBindingAsync(news, fixture.FirstGuideId, Xunit.TestContext.Current.CancellationToken);
        }
        else if (changed == "configuration")
        {
            var settings = fixture.Context.Services.GetRequiredService<ILineupSettingsService>();
            settings.Settings.Streaming = settings.Settings.Streaming with { KeepWarmPeriodSeconds = 10 };
        }
        else
        {
            var settings = fixture.Context.Services.GetRequiredService<ILineupSettingsService>();
            var selected = settings.Settings.Streaming.VirtualDevices[1].Subscription.SelectedDeviceIds;
            selected.Clear();
            selected.Add(fixture.BetaDeviceId);
        }

        // Act
        await component.Find(".save-channel-group").ClickAsync(new());

        // Assert
        Assert.All(await topology.ReadActiveAsync(Xunit.TestContext.Current.CancellationToken), channel => Assert.Null(channel.EquivalenceGroupId));
        Assert.Single(component.FindAll(".channel-equivalence-editor"));
        Assert.Contains(fixture.Context.Services.GetRequiredService<IStatusNotificationService>().Notifications,
            notification => notification.IsError && notification.Message.Contains("Reopen", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Verifies a failed publication does not masquerade as rollback or lose the committed group.</summary>
    [Fact]
    public async Task Grouping_PublicationFails_RetainsCommittedGroupAndReportsFailure()
    {
        // Arrange
        using var fixture = await ChannelFixture.CreateAsync();
        var (topology, news, _) = await PrepareGrouping(fixture);
        var publications = fixture.Context.Services.GetRequiredService<ITopologyGuideService>();
        publications.ApplyChannelChangeAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            await call.Arg<Func<CancellationToken, Task>>()(call.Arg<CancellationToken>());
            IReadOnlyList<GuideOutputPublicationResult> results =
            [
                new() { OutputId = GuideOutputId.New(), Name = "XMLTV", Succeeded = false, Error = "Output is read-only." }
            ];
            return results;
        });
        var component = fixture.Context.Render<Channels>();
        component.WaitForAssertion(() => Assert.Equal(3, ChannelRows(component).Count));
        await component.Find("tr[data-provider-key='news'] .manage-channel-group").ClickAsync(new());
        component.Find(".group-candidate[data-provider-key='movies'] input").Change(true);
        component.Find(".confirm-channel-content").Change(true);

        // Act
        await component.Find(".save-channel-group").ClickAsync(new());

        // Assert
        Assert.NotNull((await topology.ReadActiveAsync(Xunit.TestContext.Current.CancellationToken)).Single(channel => channel.Source.Id == news).EquivalenceGroupId);
        Assert.Empty(component.FindAll(".channel-equivalence-editor"));
        Assert.Contains(fixture.Context.Services.GetRequiredService<IStatusNotificationService>().Notifications,
            notification => notification.IsError && notification.Message.Contains("Output is read-only.", StringComparison.Ordinal));
    }

    /// <summary>Verifies subscription, adapter compatibility, and source enablement constrain the actual output preview.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Grouping_OutputPreview_UsesEligibleOrderedSources(bool incompatible, bool disabled)
    {
        // Arrange
        using var fixture = await ChannelFixture.CreateAsync();
        await PrepareGrouping(fixture, !disabled);
        if (incompatible)
        {
            var settings = fixture.Context.Services.GetRequiredService<ILineupSettingsService>();
            var streaming = settings.Settings.Streaming;
            settings.Settings.Streaming = streaming with
            {
                PhysicalDevices = streaming.PhysicalDevices.Select(device => device.Id == fixture.BetaDeviceId
                    ? device with { Adapter = new AdapterConfiguration("unsupported", JsonSerializer.SerializeToElement(new { })) } : device).ToArray()
            };
        }
        var component = fixture.Context.Render<Channels>();
        component.WaitForAssertion(() => Assert.Equal(3, ChannelRows(component).Count));

        // Act
        await component.Find("tr[data-provider-key='news'] .manage-channel-group").ClickAsync(new());
        component.Find(".group-candidate[data-provider-key='movies'] input").Change(true);

        // Assert
        var all = component.Find("[data-output-name='All devices'] .group-projection:last-child .group-source-priority");
        Assert.Equal(incompatible || disabled ? 1 : 2, all.QuerySelectorAll("li").Length);
        Assert.Contains("Alpha", all.QuerySelector("li")!.TextContent, StringComparison.Ordinal);
        Assert.Single(component.FindAll("[data-output-name='Alpha only'] .group-projection:last-child .group-source-priority li"));
    }

    /// <summary>Verifies publication remains single-flight after the atomic membership commit.</summary>
    [Fact]
    public async Task Grouping_PendingPublication_DisablesDuplicateSaveAndClose()
    {
        // Arrange
        using var fixture = await ChannelFixture.CreateAsync();
        await PrepareGrouping(fixture);
        var completion = new TaskCompletionSource<IReadOnlyList<GuideOutputPublicationResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var publications = fixture.Context.Services.GetRequiredService<ITopologyGuideService>();
        publications.ApplyChannelChangeAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            await call.Arg<Func<CancellationToken, Task>>()(call.Arg<CancellationToken>());
            return await completion.Task;
        });
        var component = fixture.Context.Render<Channels>();
        component.WaitForAssertion(() => Assert.Equal(3, ChannelRows(component).Count));
        await component.Find("tr[data-provider-key='news'] .manage-channel-group").ClickAsync(new());
        component.Find(".group-candidate[data-provider-key='movies'] input").Change(true);
        component.Find(".confirm-channel-content").Change(true);

        // Act
        var saving = component.Find(".save-channel-group").ClickAsync(new());
        component.WaitForAssertion(() =>
        {
            Assert.True(component.Find(".save-channel-group").HasAttribute("disabled"));
            Assert.True(component.Find(".channel-equivalence-editor .btn-close").HasAttribute("disabled"));
        });
        completion.SetResult([]);
        await saving;

        // Assert
        await publications.Received(1).ApplyChannelChangeAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>());
        Assert.Empty(component.FindAll(".channel-equivalence-editor"));
    }

    private static async Task<(StreamingTopologyStore Store, SourceChannelId News, SourceChannelId Movies)> PrepareGrouping(ChannelFixture fixture, bool enabled = true)
    {
        var topology = fixture.Context.Services.GetRequiredService<StreamingTopologyStore>();
        var channels = await topology.ReadActiveAsync(Xunit.TestContext.Current.CancellationToken);
        var news = channels.Single(channel => channel.Source.Id.ProviderKey == "news").Source.Id;
        var movies = channels.Single(channel => channel.Source.Id.ProviderKey == "movies").Source.Id;
        await topology.SetIdentityAsync(movies, "2.1", null, null, Xunit.TestContext.Current.CancellationToken);
        await topology.SetEnabledAsync(movies, enabled, Xunit.TestContext.Current.CancellationToken);
        return (topology, news, movies);
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
                    VirtualDevices =
                    [
                        new VirtualDeviceDefinition
                        {
                            Id = VirtualDeviceId.New(), Name = "All devices",
                            Adapter = new AdapterConfiguration("test-output", JsonSerializer.SerializeToElement(new { }))
                        },
                        new VirtualDeviceDefinition
                        {
                            Id = VirtualDeviceId.New(), Name = "Alpha only",
                            Adapter = new AdapterConfiguration("test-output", JsonSerializer.SerializeToElement(new { })),
                            Subscription = new PhysicalDeviceSubscription { Mode = PhysicalDeviceSubscriptionMode.Selected, SelectedDeviceIds = [alpha] }
                        }
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
            var outputDriver = Substitute.For<IVirtualTunerDriver>();
            outputDriver.Kind.Returns("test-output");
            outputDriver.SupportsPhysicalAdapter("test").Returns(true);
            var adapters = new StreamingAdapterRegistry([], [outputDriver], [guideDriver]);
            var timeZone = Substitute.For<ITimeZoneService>();
            timeZone.ConvertFromUtc(Arg.Any<DateTime>()).Returns(call => call.Arg<DateTime>());
            var refresh = Substitute.For<IPhysicalLineupService>();
            refresh.RefreshAsync(Arg.Any<StreamingConfiguration>(), Arg.Any<CancellationToken>()).Returns([]);
            var context = new BunitContext();
            context.Services.AddSingleton(topology);
            context.Services.AddSingleton(guides);
            context.Services.AddSingleton<IStreamingAdapterRegistry>(adapters);
            context.Services.AddSingleton(refresh);
            var topologyGuides = Substitute.For<ITopologyGuideService>();
            topologyGuides.ApplyChannelChangeAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>()).Returns(async call =>
            {
                await call.Arg<Func<CancellationToken, Task>>()(call.Arg<CancellationToken>());
                IReadOnlyList<GuideOutputPublicationResult> results = [];
                return results;
            });
            context.Services.AddSingleton(topologyGuides);
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
                Order = name == "Alpha" ? 0 : 1,
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
