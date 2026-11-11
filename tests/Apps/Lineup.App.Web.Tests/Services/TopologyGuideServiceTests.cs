using System.Text.Json;
using System.Xml.Linq;
using System.Net;
using Lineup.App.Web.Services;
using Lineup.Plugin.GuideOutputs.Json;
using Lineup.Plugin.GuideOutputs.Json.Plugin;
using Lineup.Plugin.GuideOutputs.Xmltv.Plugin;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.App.Web.Tests.Services;

/// <summary>
/// Verifies topology-aware guide refresh and publication.
/// </summary>
public sealed class TopologyGuideServiceTests
{
    /// <summary>
    /// Verifies refresh applies the current topology so disabled guide sources cannot be bypassed by Web entry points.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshAndPublishAsync_AppliesCurrentTopologyBeforeRefreshing(bool partial)
    {
        // Arrange
        var physicalDeviceId = PhysicalDeviceId.New();
        var virtualDeviceId = VirtualDeviceId.New();
        var outputId = GuideOutputId.New();
        var configuration = new StreamingConfiguration
        {
            PhysicalDevices =
            [
                new PhysicalDeviceDefinition
                {
                    Id = physicalDeviceId,
                    Name = "HDHomeRun",
                    Adapter = Adapter("hdhomerun")
                }
            ],
            VirtualDevices =
            [
                new VirtualDeviceDefinition
                {
                    Id = virtualDeviceId,
                    Name = "Web Player",
                    Adapter = Adapter("web-player")
                }
            ],
            GuideOutputs =
            [
                new GuideOutputDefinition
                {
                    Id = outputId,
                    Name = "Global XMLTV",
                    Key = "xmltv",
                    Adapter = Adapter("xmltv")
                }
            ]
        };
        var settings = Substitute.For<ILineupSettingsService>();
        settings.Settings.Returns(new LineupSettings { Streaming = configuration, XmltvOutputPath = "epg.xml" });
        var guides = Substitute.For<IGuideOrchestrator>();
        guides.RefreshAllAsync(Arg.Any<CancellationToken>()).Returns([]);
        var guideQueries = Substitute.For<IGuideQueryService>();
        IReadOnlyList<GuideSourceDiagnostic> diagnostics = partial
            ?
            [
                new GuideSourceDiagnostic
                {
                    SourceId = GuideSourceId.New(),
                    SourceName = "Missing source",
                    Code = GuideSourceDiagnosticCode.AdapterUnavailable,
                    Message = "Install the source plugin."
                }
            ]
            : [];
        guideQueries.BuildOutputSnapshotAsync(
                configuration,
                configuration.GuideOutputs[0],
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>())
            .Returns(new VirtualGuideSnapshot { VirtualDeviceId = virtualDeviceId, Channels = [], SourceDiagnostics = diagnostics });
        var publisher = Substitute.For<IGuidePublisher>();
        publisher.Kind.Returns("xmltv");
        var outputDriver = Substitute.For<IGuideOutputDriver>();
        outputDriver.Kind.Returns("xmltv");
        outputDriver.PublisherKind.Returns("xmltv");
        outputDriver.ResolveDestination(Arg.Any<AdapterConfiguration>()).Returns(new GuideOutputDestination
        {
            Path = "epg.xml",
            ContentType = "application/xml",
            FileName = "epg.xml"
        });
        var adapters = new StreamingAdapterRegistry([], [], [], [outputDriver]);
        var publications = Substitute.For<IXmltvPublicationStore>();
        publications.PublishAsync(
                Arg.Any<string>(),
                Arg.Any<Func<Stream, CancellationToken, Task>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<Stream, CancellationToken, Task>>(1)(new MemoryStream(), call.ArgAt<CancellationToken>(2)));
        var service = new TopologyGuideService(settings, guides, guideQueries, adapters, [publisher], publications);

        // Act
        var result = await service.RefreshAndPublishAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(result.Sources);
        var publication = Assert.Single(result.Outputs);
        Assert.True(publication.Succeeded);
        Assert.Equal(partial, publication.IsPartial);
        Assert.Equal(diagnostics, publication.SourceDiagnostics);
        await publisher.Received(1).PublishAsync(
            Arg.Is<VirtualGuideSnapshot>(snapshot => snapshot.IsPartial == partial && snapshot.SourceDiagnostics == diagnostics),
            Arg.Any<Stream>(), Arg.Any<CancellationToken>());
        await guides.Received(1).ApplyAsync(
            Arg.Is<StreamingConfiguration>(current => current.GuideSources.Count == 0),
            Arg.Any<CancellationToken>());
        await guides.Received(1).RefreshAllAsync(Arg.Any<CancellationToken>());
        await guideQueries.Received(1).BuildOutputSnapshotAsync(
            configuration,
            configuration.GuideOutputs[0],
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<CancellationToken>());
        await publications.Received(1).PublishAsync(
            "epg.xml",
            Arg.Any<Func<Stream, CancellationToken, Task>>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>Verifies every enabled output is attempted and one failure does not block later outputs.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishAsync_MultipleOutputs_ContinuesAfterFailure(bool capturedConfiguration)
    {
        // Arrange
        var firstId = GuideOutputId.New();
        var secondId = GuideOutputId.New();
        var configuration = new StreamingConfiguration
        {
            GuideOutputs =
            [
                new GuideOutputDefinition { Id = firstId, Name = "Broken", Key = "broken", Adapter = Adapter("xmltv") },
                new GuideOutputDefinition { Id = secondId, Name = "Working", Key = "working", Adapter = Adapter("xmltv") },
                new GuideOutputDefinition { Id = GuideOutputId.New(), Name = "Disabled", Key = "disabled", Enabled = false, Adapter = Adapter("xmltv") }
            ]
        };
        var settings = Substitute.For<ILineupSettingsService>();
        settings.Settings.Returns(new LineupSettings { Streaming = capturedConfiguration ? new StreamingConfiguration() : configuration });
        var guides = Substitute.For<IGuideOrchestrator>();
        var guideQueries = Substitute.For<IGuideQueryService>();
        var diagnostic = new GuideSourceDiagnostic
        {
            SourceId = GuideSourceId.New(),
            SourceName = "Missing source",
            Code = GuideSourceDiagnosticCode.AdapterUnavailable,
            Message = "Install the source plugin."
        };
        guideQueries.BuildOutputSnapshotAsync(
                configuration,
                Arg.Any<GuideOutputDefinition>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>())
            .Returns(call => new VirtualGuideSnapshot
            {
                VirtualDeviceId = new VirtualDeviceId(call.ArgAt<GuideOutputDefinition>(1).Id.Value),
                Channels = [],
                SourceDiagnostics = [diagnostic]
            });
        var publisher = Substitute.For<IGuidePublisher>();
        publisher.Kind.Returns("xmltv");
        var outputDriver = Substitute.For<IGuideOutputDriver>();
        outputDriver.Kind.Returns("xmltv");
        outputDriver.PublisherKind.Returns("xmltv");
        outputDriver.ResolveDestination(Arg.Any<AdapterConfiguration>())
            .Returns(
                new GuideOutputDestination { Path = "broken.xml", ContentType = "application/xml", FileName = "broken.xml" },
                new GuideOutputDestination { Path = "working.xml", ContentType = "application/xml", FileName = "working.xml" });
        var publications = Substitute.For<IXmltvPublicationStore>();
        publications.PublishAsync(
                "broken.xml",
                Arg.Any<Func<Stream, CancellationToken, Task>>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => throw new IOException("Disk unavailable."));
        publications.PublishAsync(
                "working.xml",
                Arg.Any<Func<Stream, CancellationToken, Task>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var service = new TopologyGuideService(
            settings,
            guides,
            guideQueries,
            new StreamingAdapterRegistry([], [], [], [outputDriver]),
            [publisher],
            publications);

        // Act
        var results = capturedConfiguration
            ? await service.PublishConfigurationAsync(configuration, TestContext.Current.CancellationToken)
            : await service.PublishAsync(TestContext.Current.CancellationToken);

        // Assert
        await guides.Received(1).ApplyAsync(configuration, Arg.Any<CancellationToken>());
        Assert.Collection(
            results,
            result =>
            {
                Assert.Equal(firstId, result.OutputId);
                Assert.False(result.Succeeded);
                Assert.True(result.IsPartial);
                Assert.Equal(diagnostic, Assert.Single(result.SourceDiagnostics));
                Assert.Contains("Disk unavailable", result.Error, StringComparison.Ordinal);
            },
            result =>
            {
                Assert.Equal(secondId, result.OutputId);
                Assert.True(result.Succeeded);
                Assert.True(result.IsPartial);
                Assert.Equal(diagnostic, Assert.Single(result.SourceDiagnostics));
            });
        await publications.Received(1).PublishAsync(
            "working.xml",
            Arg.Any<Func<Stream, CancellationToken, Task>>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>Verifies both publication entrypoints serialize committed data without constructing or refreshing providers.</summary>
    [Theory]
    [InlineData("xmltv", false)]
    [InlineData("xmltv", true)]
    [InlineData("json", false)]
    [InlineData("json", true)]
    public async Task PublishAsync_CommittedCache_DoesNotActivateSources(string format, bool single)
    {
        // Arrange
        using var fixture = new CachedPublicationFixture(format);
        await fixture.SeedAsync();

        // Act
        var result = await fixture.PublishAsync(single, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(result.Succeeded);
        Assert.False(result.IsPartial);
        Assert.Equal(1, result.ChannelCount);
        Assert.Equal(1, result.ProgramCount);
        Assert.Equal(["Committed broadcast"], await fixture.ReadTitlesAsync());
        await fixture.AssertNoActivationAsync();
    }

    /// <summary>Verifies publication updates management and dedicated virtual-guide projections without requiring a source refresh.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishAsync_ConfigurationChange_UpdatesVirtualExportWithoutActivation(bool single)
    {
        // Arrange
        using var fixture = new CachedPublicationFixture("xmltv");
        await fixture.SeedAsync();
        await fixture.PublishAsync(single, TestContext.Current.CancellationToken);
        var id = fixture.Configuration.VirtualDevices[0].Id;
        var original = await fixture.Guides.BuildSnapshotAsync(id, TestContext.Current.CancellationToken);
        fixture.Configuration = fixture.Configuration with { GuideSources = [fixture.Configuration.GuideSources[0] with { Enabled = false }] };

        // Act
        var result = await fixture.PublishAsync(single, TestContext.Current.CancellationToken);
        var exported = await fixture.Guides.BuildSnapshotAsync(id, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("Committed broadcast", Assert.Single(Assert.Single(original.Channels).Programs).Title);
        Assert.True(result.Succeeded);
        Assert.Empty(Assert.Single(exported.Channels).Programs);
        Assert.False(exported.IsPartial);
        await fixture.AssertNoActivationAsync();
    }

    /// <summary>Verifies current configuration restricts committed cache without provider activation or loss of exclusion diagnostics.</summary>
    [Theory]
    [InlineData("disabled", false)]
    [InlineData("disabled", true)]
    [InlineData("removed", false)]
    [InlineData("removed", true)]
    [InlineData("unsubscribed", false)]
    [InlineData("unsubscribed", true)]
    [InlineData("incompatible", false)]
    [InlineData("incompatible", true)]
    [InlineData("unknown-driver", false)]
    [InlineData("unknown-driver", true)]
    [InlineData("unknown-device", false)]
    [InlineData("unknown-device", true)]
    [InlineData("disabled-device", false)]
    [InlineData("disabled-device", true)]
    [InlineData("unselected", false)]
    [InlineData("unselected", true)]
    [InlineData("disabled-virtual", false)]
    [InlineData("disabled-virtual", true)]
    public async Task PublishAsync_IneligibleCache_RetainsCurrentSelectionAndDiagnostics(string restriction, bool single)
    {
        // Arrange
        using var fixture = new CachedPublicationFixture("xmltv");
        await fixture.SeedAsync();
        var configuration = fixture.Configuration;
        var source = configuration.GuideSources[0];
        var otherDevice = configuration.PhysicalDevices[0] with { Id = PhysicalDeviceId.New(), Name = "Other device" };
        var selectedSource = source with { Id = GuideSourceId.New(), Name = "Selected source" };
        GuideSourceDiagnosticCode? expected = restriction switch
        {
            "unknown-driver" => GuideSourceDiagnosticCode.AdapterUnavailable,
            "incompatible" or "unknown-device" => GuideSourceDiagnosticCode.InvalidSubscription,
            "disabled-device" => GuideSourceDiagnosticCode.NoCompatibleDevices,
            _ => null
        };
        fixture.Configuration = restriction switch
        {
            "disabled" => configuration with { GuideSources = [source with { Enabled = false }] },
            "removed" => configuration with { GuideSources = [] },
            "unsubscribed" => configuration with
            {
                PhysicalDevices = [.. configuration.PhysicalDevices, otherDevice],
                GuideSources = [source with { Subscription = new PhysicalDeviceSubscription { Mode = PhysicalDeviceSubscriptionMode.Selected, SelectedDeviceIds = [otherDevice.Id] } }]
            },
            "incompatible" => configuration with { PhysicalDevices = [configuration.PhysicalDevices[0] with { Adapter = Adapter("other") }] },
            "unknown-driver" => configuration with { GuideSources = [source with { Adapter = Adapter("missing") }] },
            "unknown-device" => configuration with
            {
                GuideSources = [source with { Subscription = new PhysicalDeviceSubscription { Mode = PhysicalDeviceSubscriptionMode.Selected, SelectedDeviceIds = [otherDevice.Id] } }]
            },
            "disabled-device" => configuration with { PhysicalDevices = [configuration.PhysicalDevices[0] with { Enabled = false }] },
            "disabled-virtual" => configuration with { VirtualDevices = [configuration.VirtualDevices[0] with { Enabled = false }] },
            "unselected" => configuration with
            {
                GuideSources = [source with { Adapter = Adapter("missing") }, selectedSource],
                GuideOutputs =
                [
                    fixture.Output with
                    {
                        GuideSourceSelectionMode = GuideOutputSelectionMode.Selected,
                        SelectedGuideSourceIds = [selectedSource.Id]
                    }
                ]
            },
            _ => throw new ArgumentOutOfRangeException(nameof(restriction))
        };

        // Act
        var result = await fixture.PublishAsync(single, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(result.Succeeded);
        Assert.Equal(0, result.ProgramCount);
        Assert.Equal(restriction is "disabled-device" or "disabled-virtual" ? 0 : 1, result.ChannelCount);
        string[] expectedTitles = result.ChannelCount == 0 ? [] : ["Not Available"];
        Assert.Equal(expectedTitles, await fixture.ReadTitlesAsync());
        Assert.Equal(expected.HasValue, result.IsPartial);
        if (expected is { } code)
        {
            var diagnostic = Assert.Single(result.SourceDiagnostics);
            Assert.Equal(source.Id, diagnostic.SourceId);
            Assert.Equal(code, diagnostic.Code);
        }
        Assert.Equal(1, Assert.Single(await fixture.Store.ReadImportStatesAsync(TestContext.Current.CancellationToken)).ProgramCount);
        await fixture.AssertNoActivationAsync();
    }

    /// <summary>Verifies refresh still activates sources once, reports failures and preserves or replaces committed data appropriately.</summary>
    [Theory]
    [InlineData("activation", false)]
    [InlineData("activation", true)]
    [InlineData("refresh", false)]
    [InlineData("refresh", true)]
    [InlineData("healthy", false)]
    [InlineData("healthy", true)]
    public async Task RefreshAsync_ExplicitRefresh_RetainsActivationAndRecovery(string failure, bool publish)
    {
        // Arrange
        using var fixture = new CachedPublicationFixture("json");
        await fixture.SeedAsync();
        if (failure != "activation")
        {
            fixture.Driver.CreateAsync(Arg.Any<GuideSourceId>(), Arg.Any<AdapterConfiguration>(), Arg.Any<IReadOnlyList<PhysicalDeviceId>>(), Arg.Any<CancellationToken>())
                .Returns(new ValueTask<IGuideSource>(fixture.Source));
        }
        if (failure == "refresh")
        {
            fixture.Source.RefreshAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException<GuideImport>(new IOException("Private provider detail")));
        }

        // Act
        var result = publish
            ? await fixture.Service.RefreshAndPublishAsync(TestContext.Current.CancellationToken)
            : await fixture.Service.RefreshAsync(TestContext.Current.CancellationToken);
        var publication = publish ? Assert.Single(result.Outputs) : await fixture.PublishAsync(false, TestContext.Current.CancellationToken);

        // Assert
        var refreshed = Assert.Single(result.Sources);
        Assert.Equal(failure == "healthy", refreshed.Succeeded);
        if (!refreshed.Succeeded)
        {
            Assert.Contains("preserved", refreshed.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("Private provider detail", refreshed.Error!, StringComparison.Ordinal);
        }
        Assert.True(publication.Succeeded);
        Assert.False(publication.IsPartial);
        Assert.Equal([failure == "healthy" ? "Refreshed broadcast" : "Committed broadcast"], await fixture.ReadTitlesAsync());
        await fixture.Driver.Received(1).CreateAsync(
            fixture.Source.Id, Arg.Any<AdapterConfiguration>(), Arg.Any<IReadOnlyList<PhysicalDeviceId>>(), Arg.Any<CancellationToken>());
        await fixture.Source.Received(failure == "activation" ? 0 : 1).RefreshAsync(Arg.Any<CancellationToken>());
        Assert.Equal(publish ? 1 : 0, result.Outputs.Count);
    }

    /// <summary>Verifies activation cancellation prevents publication and releases the service gate for a later successful refresh.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshAsync_ActivationCancelled_DoesNotPublishAndAllowsRetry(bool publish)
    {
        // Arrange
        using var fixture = new CachedPublicationFixture("xmltv");
        await fixture.SeedAsync();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        fixture.Driver.CreateAsync(Arg.Any<GuideSourceId>(), Arg.Any<AdapterConfiguration>(), Arg.Any<IReadOnlyList<PhysicalDeviceId>>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cancellation.Cancel();
                return new ValueTask<IGuideSource>(Task.FromCanceled<IGuideSource>(cancellation.Token));
            });

        // Act
        var exception = await Record.ExceptionAsync(() => publish
            ? fixture.Service.RefreshAndPublishAsync(cancellation.Token)
            : fixture.Service.RefreshAsync(cancellation.Token));

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.False(File.Exists(fixture.OutputPath));
        await fixture.Source.DidNotReceive().RefreshAsync(Arg.Any<CancellationToken>());

        // Arrange
        fixture.Driver.CreateAsync(Arg.Any<GuideSourceId>(), Arg.Any<AdapterConfiguration>(), Arg.Any<IReadOnlyList<PhysicalDeviceId>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IGuideSource>(fixture.Source));

        // Act
        var recovered = await fixture.Service.RefreshAndPublishAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.True(Assert.Single(recovered.Sources).Succeeded);
        Assert.True(Assert.Single(recovered.Outputs).Succeeded);
        Assert.Equal(["Refreshed broadcast"], await fixture.ReadTitlesAsync());
    }

    /// <summary>Verifies structural validation is preserved without constructing sources or replacing an existing publication.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishAsync_InvalidStructure_RejectsBeforeActivationOrWriting(bool single)
    {
        // Arrange
        using var fixture = new CachedPublicationFixture("xmltv");
        await fixture.SeedAsync();
        fixture.Configuration = fixture.Configuration with { MaximumPluginConnections = -1 };
        await File.WriteAllTextAsync(fixture.OutputPath, "previous publication", TestContext.Current.CancellationToken);

        // Act
        var exception = await Record.ExceptionAsync(() => fixture.PublishAsync(single, TestContext.Current.CancellationToken));

        // Assert
        Assert.Contains("connection limit cannot be negative", Assert.IsType<InvalidOperationException>(exception).Message, StringComparison.Ordinal);
        Assert.Equal("previous publication", await File.ReadAllTextAsync(fixture.OutputPath, TestContext.Current.CancellationToken));
        await fixture.AssertNoActivationAsync();
    }

    /// <summary>Verifies query, serializer and publication failures remain observable and cancellation never becomes partial success.</summary>
    [Theory]
    [InlineData("query", false, false)]
    [InlineData("query", false, true)]
    [InlineData("publisher", false, false)]
    [InlineData("publisher", false, true)]
    [InlineData("store", false, false)]
    [InlineData("store", false, true)]
    [InlineData("query", true, false)]
    [InlineData("query", true, true)]
    [InlineData("publisher", true, false)]
    [InlineData("publisher", true, true)]
    [InlineData("store", true, false)]
    [InlineData("store", true, true)]
    public async Task PublishAsync_FailureOrCancellation_PreservesPublication(string phase, bool cancelled, bool single)
    {
        // Arrange
        using var fixture = new CachedPublicationFixture("xmltv");
        await fixture.SeedAsync();
        await File.WriteAllTextAsync(fixture.OutputPath, "previous publication", TestContext.Current.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Exception Fail()
        {
            if (cancelled)
            {
                cancellation.Cancel();
                return new OperationCanceledException(cancellation.Token);
            }
            return new IOException("Publication unavailable");
        }
        if (phase == "query")
        {
            fixture.OnContextCreation = () => throw Fail();
        }
        else if (phase == "publisher")
        {
            fixture.Publisher.PublishAsync(Arg.Any<VirtualGuideSnapshot>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromException(Fail()));
        }
        else
        {
            fixture.Publications.PublishAsync(Arg.Any<string>(), Arg.Any<Func<Stream, CancellationToken, Task>>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromException(Fail()));
        }

        // Act
        GuideOutputPublicationResult? result = null;
        var exception = await Record.ExceptionAsync(async () => result = await fixture.PublishAsync(single, cancellation.Token));

        // Assert
        if (cancelled)
        {
            Assert.IsAssignableFrom<OperationCanceledException>(exception);
            Assert.Null(result);
        }
        else
        {
            Assert.Null(exception);
            Assert.NotNull(result);
            Assert.False(result.Succeeded);
            Assert.False(result.IsPartial);
            Assert.Contains("Publication unavailable", result.Error, StringComparison.Ordinal);
        }
        Assert.Equal("previous publication", await File.ReadAllTextAsync(fixture.OutputPath, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(fixture.OutputPath)!, "*.tmp"));
        await fixture.AssertNoActivationAsync();
    }

    /// <summary>Verifies missing or disabled outputs remain no-ops without activating configured sources.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishAsync_MissingOrDisabledOutput_DoesNotActivateSources(bool disabled)
    {
        // Arrange
        using var fixture = new CachedPublicationFixture("xmltv");
        await fixture.SeedAsync();
        if (disabled)
        {
            fixture.Configuration = fixture.Configuration with { GuideOutputs = [fixture.Output with { Enabled = false }] };
        }
        var id = disabled ? fixture.Output.Id : GuideOutputId.New();

        // Act
        var result = await fixture.Service.PublishAsync(id, TestContext.Current.CancellationToken);
        var outputs = disabled ? await fixture.Service.PublishAsync(TestContext.Current.CancellationToken) : [];

        // Assert
        Assert.Null(result);
        Assert.Empty(outputs);
        Assert.False(File.Exists(fixture.OutputPath));
        await fixture.AssertNoActivationAsync();
    }

    /// <summary>Verifies advertised production HTTP routes serve the exact cached publication, including on-demand creation without provider activation.</summary>
    [Theory]
    [InlineData("xmltv", true, false)]
    [InlineData("xmltv", true, true)]
    [InlineData("xmltv", false, false)]
    [InlineData("xmltv", false, true)]
    [InlineData("json", false, false)]
    [InlineData("json", false, true)]
    public async Task PublishAsync_AdvertisedDownload_ServesExactCommittedCache(string format, bool builtIn, bool onDemand)
    {
        // Arrange
        using var fixture = new CachedPublicationFixture(format);
        fixture.Configuration = fixture.Configuration with { GuideOutputs = [fixture.Output with { IsBuiltIn = builtIn }] };
        await fixture.SeedAsync();
        if (!onDemand)
        {
            await fixture.PublishAsync(false, TestContext.Current.CancellationToken);
        }
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = Path.GetDirectoryName(fixture.OutputPath) });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton(fixture.Settings);
        builder.Services.AddSingleton(fixture.Adapters);
        builder.Services.AddSingleton<ITopologyGuideService>(fixture.Service);
        builder.Services.AddSingleton(fixture.Publications);
        await using var app = builder.Build();
        app.MapGuideOutputEndpoints();
        await app.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(app.Urls)), Timeout = TimeSpan.FromSeconds(10) };
            var before = Assert.Single((await fixture.Status.GetStatusAsync(TestContext.Current.CancellationToken)).Guide.Outputs);

            // Act
            using var inline = await client.GetAsync($"{before.DownloadUrl}?download=false", TestContext.Current.CancellationToken);
            var bytes = await inline.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
            using var attachment = await client.GetAsync(before.DownloadUrl, TestContext.Current.CancellationToken);
            var after = await fixture.Status.GetStatusAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal(builtIn ? "/api/xmltv" : "/api/guide-outputs/output", before.DownloadUrl);
            Assert.Equal(!onDemand, before.Available);
            Assert.Equal(HttpStatusCode.OK, inline.StatusCode);
            Assert.Equal(HttpStatusCode.OK, attachment.StatusCode);
            Assert.Null(inline.Content.Headers.ContentDisposition);
            Assert.Equal("attachment", attachment.Content.Headers.ContentDisposition?.DispositionType);
            Assert.Equal(format == "xmltv" ? "application/xml" : "application/json", inline.Content.Headers.ContentType?.MediaType);
            Assert.Equal(await File.ReadAllBytesAsync(fixture.OutputPath, TestContext.Current.CancellationToken), bytes);
            Assert.Equal(bytes, await attachment.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
            Assert.Equal(["Committed broadcast"], await fixture.ReadTitlesAsync());
            Assert.True(Assert.Single(after.Guide.Outputs).Available);
            Assert.Equal(1, after.Guide.ProgramCount);
            await fixture.Publications.Received(1).PublishAsync(Arg.Any<string>(), Arg.Any<Func<Stream, CancellationToken, Task>>(), Arg.Any<CancellationToken>());
            await fixture.AssertNoActivationAsync();
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
        }
    }

    private sealed class CachedPublicationFixture : IDbContextFactory<EpgDbContext>, IDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("lineup-cached-publication-");
        private readonly DbContextOptions<EpgDbContext> _options;
        private readonly string _format;

        /// <summary>Initializes real cached storage and publishers with a source factory that must not run during publication.</summary>
        internal CachedPublicationFixture(string format)
        {
            _format = format;
            _options = new DbContextOptionsBuilder<EpgDbContext>().UseSqlite($"Data Source={Path.Combine(_root.FullName, "guides.db")};Pooling=False").Options;
            var device = PhysicalDeviceId.New();
            var source = GuideSourceId.New();
            Configuration = new StreamingConfiguration
            {
                PhysicalDevices = [new PhysicalDeviceDefinition { Id = device, Name = "Physical", Adapter = Adapter("physical") }],
                VirtualDevices = [new VirtualDeviceDefinition { Id = VirtualDeviceId.New(), Name = "Virtual", Adapter = Adapter("virtual") }],
                GuideSources =
                [
                    new GuideSourceDefinition
                    {
                        Id = source,
                        Name = "Guide",
                        Adapter = Adapter("guide"),
                        Subscription = new PhysicalDeviceSubscription { Mode = PhysicalDeviceSubscriptionMode.Selected, SelectedDeviceIds = [device] }
                    }
                ],
                GuideOutputs = [new GuideOutputDefinition { Id = GuideOutputId.New(), Name = "Output", Key = "output", Adapter = Adapter(format) }]
            };
            Source = Substitute.For<IGuideSource>();
            Source.Id.Returns(source);
            Source.RefreshAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(Import("Refreshed broadcast")));
            Driver = Substitute.For<IGuideSourceDriver>();
            Driver.Kind.Returns("guide");
            Driver.Validate(Arg.Any<AdapterConfiguration>()).Returns([]);
            Driver.SupportsPhysicalAdapter(Arg.Any<string>()).Returns(call => call.Arg<string>() == "physical");
            Driver.CreateAsync(Arg.Any<GuideSourceId>(), Arg.Any<AdapterConfiguration>(), Arg.Any<IReadOnlyList<PhysicalDeviceId>>(), Arg.Any<CancellationToken>())
                .Returns(_ => new ValueTask<IGuideSource>(Task.FromException<IGuideSource>(new IOException("Private provider detail"))));
            var virtualDriver = Substitute.For<IVirtualTunerDriver>();
            virtualDriver.Kind.Returns("virtual");
            virtualDriver.SupportsPhysicalAdapter(Arg.Any<string>()).Returns(true);
            var outputDriver = Substitute.For<IGuideOutputDriver>();
            outputDriver.Kind.Returns(format);
            outputDriver.PublisherKind.Returns(format);
            outputDriver.ResolveDestination(Arg.Any<AdapterConfiguration>()).Returns(new GuideOutputDestination
            {
                Path = OutputPath,
                ContentType = format == "xmltv" ? "application/xml" : "application/json",
                FileName = Path.GetFileName(OutputPath)
            });
            var adapters = new StreamingAdapterRegistry([], [virtualDriver], [Driver], [outputDriver]);
            IGuidePublisher realPublisher = format == "xmltv" ? new XmltvGuidePublisher() : new JsonGuidePublisher();
            Publisher = Substitute.For<IGuidePublisher>();
            Publisher.Kind.Returns(format);
            Publisher.PublishAsync(Arg.Any<VirtualGuideSnapshot>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
                .Returns(call => realPublisher.PublishAsync(call.Arg<VirtualGuideSnapshot>(), call.Arg<Stream>(), call.Arg<CancellationToken>()));
            Publications = Substitute.For<IXmltvPublicationStore>();
            var realPublications = new XmltvPublicationStore();
            Publications.PublishAsync(Arg.Any<string>(), Arg.Any<Func<Stream, CancellationToken, Task>>(), Arg.Any<CancellationToken>())
                .Returns(call => realPublications.PublishAsync(call.Arg<string>(), call.Arg<Func<Stream, CancellationToken, Task>>(), call.Arg<CancellationToken>()));
            Publications.Exists(Arg.Any<string>()).Returns(call => realPublications.Exists(call.Arg<string>()));
            Publications.OpenRead(Arg.Any<string>()).Returns(call => realPublications.OpenRead(call.Arg<string>()));
            Store = new DeviceGuideStore(this);
            var topology = new StreamingTopologyStore(this);
            Settings = Substitute.For<ILineupSettingsService>();
            Settings.Settings.Returns(_ => new LineupSettings { Streaming = Configuration, XmltvOutputPath = OutputPath });
            Guides = new GuideOrchestrator(adapters, Store, topology);
            Adapters = adapters;
            var queries = new GuideQueryService(topology, Store, adapters);
            Service = new TopologyGuideService(Settings, Guides, queries, adapters, [Publisher], Publications);
            var webSettings = Substitute.For<IWebSettingsService>();
            webSettings.Settings.Returns(new WebSettings { IsSetupComplete = true });
            Status = new StatusApiService(new LineupGuideReader(queries, Settings), Settings, webSettings,
                Substitute.For<IPhysicalDeviceStatusService>(), Substitute.For<IAutoFetchStateService>(), new ActiveStreamRegistry(),
                new StatusApiRuntime("cached-publication"), NullLogger<StatusApiService>.Instance, Publications, new LineupStartupState(LineupStartupStatus.Ready),
                adapters: adapters, topologyStore: topology, guideStore: Store);
        }

        /// <summary>Gets or sets the current saved topology.</summary>
        internal StreamingConfiguration Configuration { get; set; }
        /// <summary>Gets the configured output.</summary>
        internal GuideOutputDefinition Output => Configuration.GuideOutputs[0];
        /// <summary>Gets the owned publication destination.</summary>
        internal string OutputPath => Path.Combine(_root.FullName, $"guide.{_format}");
        /// <summary>Gets the persisted guide store.</summary>
        internal DeviceGuideStore Store { get; }
        /// <summary>Gets the production publication service.</summary>
        internal TopologyGuideService Service { get; }
        /// <summary>Gets the real status aggregation using the same cached data and publication destination.</summary>
        internal StatusApiService Status { get; }
        /// <summary>Gets the current shared settings boundary.</summary>
        internal ILineupSettingsService Settings { get; }
        /// <summary>Gets the adapters shared by publication and HTTP delivery.</summary>
        internal IStreamingAdapterRegistry Adapters { get; }
        /// <summary>Gets the virtual-guide projection used by management and dedicated exports.</summary>
        internal IGuideOrchestrator Guides { get; }
        /// <summary>Gets the monitored source factory.</summary>
        internal IGuideSourceDriver Driver { get; }
        /// <summary>Gets the monitored refresh source.</summary>
        internal IGuideSource Source { get; }
        /// <summary>Gets the real serializer's monitored boundary.</summary>
        internal IGuidePublisher Publisher { get; }
        /// <summary>Gets the real atomic publication store's monitored boundary.</summary>
        internal IXmltvPublicationStore Publications { get; }
        /// <summary>Gets or sets an owned query failure or cancellation callback.</summary>
        internal Action? OnContextCreation { get; set; }

        /// <inheritdoc />
        public EpgDbContext CreateDbContext()
        {
            OnContextCreation?.Invoke();
            return new EpgDbContext(_options);
        }

        /// <summary>Commits a physical channel and its original guide without activating providers.</summary>
        internal async Task SeedAsync()
        {
            var device = Configuration.PhysicalDevices[0].Id;
            await new StreamingTopologyStore(this).ReconcileAsync(device,
                [new PhysicalSourceChannel { Id = new SourceChannelId(device, "7"), ProviderNumber = "7", ProviderName = "Channel 7" }], TestContext.Current.CancellationToken);
            await Store.ImportAsync(Import("Committed broadcast"), TestContext.Current.CancellationToken);
        }

        /// <summary>Invokes either public cached-publication entrypoint.</summary>
        internal async Task<GuideOutputPublicationResult> PublishAsync(bool single, CancellationToken cancellationToken) =>
            single
                ? Assert.IsType<GuideOutputPublicationResult>(await Service.PublishAsync(Output.Id, cancellationToken))
                : Assert.Single(await Service.PublishAsync(cancellationToken));

        /// <summary>Reads actual serialized programs from XMLTV or JSON.</summary>
        internal async Task<string[]> ReadTitlesAsync()
        {
            await using var stream = File.OpenRead(OutputPath);
            if (_format == "xmltv")
            {
                return XDocument.Load(stream).Descendants("programme").Select(program => (string)program.Element("title")!).ToArray();
            }
            var document = await JsonGuideSerialization.DeserializeAsync(stream, TestContext.Current.CancellationToken);
            return Assert.IsType<JsonGuideDocument>(document).Channels.SelectMany(channel => channel.Programs).Select(program => program.Title).ToArray();
        }

        /// <summary>Verifies publication never constructs or refreshes a source runtime.</summary>
        internal async Task AssertNoActivationAsync()
        {
            await Driver.DidNotReceive().CreateAsync(Arg.Any<GuideSourceId>(), Arg.Any<AdapterConfiguration>(), Arg.Any<IReadOnlyList<PhysicalDeviceId>>(), Arg.Any<CancellationToken>());
            await Source.DidNotReceive().RefreshAsync(Arg.Any<CancellationToken>());
        }

        /// <inheritdoc />
        public void Dispose() => _root.Delete(recursive: true);

        private GuideImport Import(string title)
        {
            var start = DateTimeOffset.UtcNow;
            var channel = new GuideChannelId(Source.Id, "7");
            return new GuideImport
            {
                SourceId = Source.Id,
                Channels = [new GuideChannel { Id = channel, Number = "7", Name = "Guide 7" }],
                Programs = [new GuideProgram { ChannelId = channel, StartUtc = start, EndUtc = start.AddHours(1), Title = title }],
                CoverageStartUtc = start,
                CoverageEndUtc = start.AddHours(1)
            };
        }
    }

    private static AdapterConfiguration Adapter(string kind)
    {
        using var document = JsonDocument.Parse("{}");
        return new AdapterConfiguration(kind, document.RootElement.Clone());
    }
}
