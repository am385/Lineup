using System.Text.Json;
using NSubstitute;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>
/// Verifies topology-aware guide refresh and publication.
/// </summary>
public sealed class TopologyGuideServiceTests
{
    /// <summary>
    /// Verifies refresh applies the current topology so disabled guide sources cannot be bypassed by Web entry points.
    /// </summary>
    [Fact]
    public async Task RefreshAndPublishAsync_AppliesCurrentTopologyBeforeRefreshing()
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
                    Adapter = Adapter("xmltv")
                }
            ]
        };
        var settings = Substitute.For<ILineupSettingsService>();
        settings.Settings.Returns(new LineupSettings { Streaming = configuration, XmltvOutputPath = "epg.xml" });
        var guides = Substitute.For<IGuideOrchestrator>();
        guides.RefreshAllAsync(Arg.Any<CancellationToken>()).Returns([]);
        var guideQueries = Substitute.For<IGuideQueryService>();
        guideQueries.BuildOutputSnapshotAsync(
                configuration,
                configuration.GuideOutputs[0],
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>())
            .Returns(new VirtualGuideSnapshot { VirtualDeviceId = virtualDeviceId, Channels = [] });
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
    [Fact]
    public async Task PublishAsync_MultipleOutputs_ContinuesAfterFailure()
    {
        // Arrange
        var firstId = GuideOutputId.New();
        var secondId = GuideOutputId.New();
        var configuration = new StreamingConfiguration
        {
            GuideOutputs =
            [
                new GuideOutputDefinition { Id = firstId, Name = "Broken", Adapter = Adapter("xmltv") },
                new GuideOutputDefinition { Id = secondId, Name = "Working", Adapter = Adapter("xmltv") },
                new GuideOutputDefinition { Id = GuideOutputId.New(), Name = "Disabled", Enabled = false, Adapter = Adapter("xmltv") }
            ]
        };
        var settings = Substitute.For<ILineupSettingsService>();
        settings.Settings.Returns(new LineupSettings { Streaming = configuration });
        var guides = Substitute.For<IGuideOrchestrator>();
        var guideQueries = Substitute.For<IGuideQueryService>();
        guideQueries.BuildOutputSnapshotAsync(
                configuration,
                Arg.Any<GuideOutputDefinition>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>())
            .Returns(call => new VirtualGuideSnapshot
            {
                VirtualDeviceId = new VirtualDeviceId(call.ArgAt<GuideOutputDefinition>(1).Id.Value),
                Channels = []
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
        var results = await service.PublishAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Collection(
            results,
            result =>
            {
                Assert.Equal(firstId, result.OutputId);
                Assert.False(result.Succeeded);
                Assert.Contains("Disk unavailable", result.Error, StringComparison.Ordinal);
            },
            result =>
            {
                Assert.Equal(secondId, result.OutputId);
                Assert.True(result.Succeeded);
            });
        await publications.Received(1).PublishAsync(
            "working.xml",
            Arg.Any<Func<Stream, CancellationToken, Task>>(),
            Arg.Any<CancellationToken>());
    }

    private static AdapterConfiguration Adapter(string kind)
    {
        using var document = JsonDocument.Parse("{}");
        return new AdapterConfiguration(kind, document.RootElement.Clone());
    }
}
