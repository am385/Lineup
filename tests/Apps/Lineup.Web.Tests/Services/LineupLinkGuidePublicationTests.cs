using System.Xml.Linq;
using System.Text.Json;
using Lineup.GuideOutputs.Json;
using Lineup.GuideOutputs.Json.Plugin;
using Lineup.GuideOutputs.Xmltv.Plugin;
using Lineup.LineupLink.Guides;
using Lineup.LineupLink.Physical;
using Lineup.LineupLink.Protocol;
using Lineup.LineupLink.Virtual;
using Lineup.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>Verifies linked guide metadata survives import, persistence, projection, and publication.</summary>
public sealed class LineupLinkGuidePublicationTests
{
    /// <summary>Verifies exported independent imports can be reimported with one valid root envelope and all schedules intact.</summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task GetGuideAsync_IndependentSources_RoundTripsThroughLinkedImporter(int metadataCount, bool dedicatedMapping)
    {
        // Arrange
        using var fixture = new Fixture();
        var scopes = new List<GuideSourceScope>();
        var channels = new List<ResolvedVirtualChannel>();
        var now = DateTimeOffset.UtcNow;
        for (var index = 0; index < 2; index++)
        {
            var number = index == 0 ? "7.1" : "9.1";
            var physical = PhysicalDeviceId.New();
            var source = GuideSourceId.New();
            var channel = new SourceChannelId(physical, "station");
            var guideChannel = new GuideChannelId(source, "station");
            await fixture.Topology.ReconcileAsync(physical,
                [new PhysicalSourceChannel { Id = channel, ProviderNumber = number, ProviderName = number }], TestContext.Current.CancellationToken);
            await fixture.Store.ImportAsync(new GuideImport
            {
                SourceId = source,
                Channels =
                [
                    new GuideChannel
                    {
                        Id = guideChannel,
                        Number = number,
                        Name = number,
                        IsProtected = true,
                        SupplementalXml = XmltvSupplementalMetadata.Create(elements: [new XElement("display-name", new XAttribute("lang", "es"), number)])
                    }
                ],
                Programs =
                [
                    new GuideProgram
                    {
                        ChannelId = guideChannel,
                        StartUtc = now,
                        EndUtc = now.AddHours(1),
                        Title = number,
                        SupplementalXml = XmltvSupplementalMetadata.Create(elements: [new XElement("desc", new XAttribute("lang", "es"), number)])
                    }
                ],
                CoverageStartUtc = now,
                CoverageEndUtc = now.AddHours(1),
                SupplementalXml = index < metadataCount ? RootMetadata(index == 0 ? "First" : "Second") : null
            }, TestContext.Current.CancellationToken);
            channels.Add(new ResolvedVirtualChannel(number, number, [channel]));
            scopes.Add(new GuideSourceScope(source, [physical]));
        }
        var id = VirtualDeviceId.New();
        var snapshot = await fixture.Store.BuildSnapshotAsync(id, channels, scopes, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(metadataCount, snapshot.SupplementalXml.Count);
        var grant = LineupLinkAccessGrantManager.Create(new LineupLinkVirtualConfiguration
        {
            RequireEncryption = false,
            PublicBaseUrl = "http://localhost:8080/"
        }, "round trip", null, DateTime.UtcNow);
        var settings = Substitute.For<ILineupSettingsService>();
        settings.Settings.Returns(new LineupSettings
        {
            ProtectedContentMode = dedicatedMapping ? ProtectedContentMode.ReturnError : ProtectedContentMode.StreamSlate,
            Streaming = new StreamingConfiguration
            {
                VirtualDevices =
                [
                    new VirtualDeviceDefinition
                    {
                        Id = id,
                        Name = "Export",
                        Adapter = new AdapterConfiguration(LineupLinkProtocol.AdapterKind, JsonSerializer.SerializeToElement(grant.Configuration))
                    }
                ]
            }
        });
        var guides = Substitute.For<IGuideOrchestrator>();
        guides.BuildSnapshotAsync(id, Arg.Any<CancellationToken>()).Returns(snapshot);
        var controller = new LineupLinkController(settings, new StreamingAdapterRegistry([], [new LineupLinkVirtualTunerDriver()], []),
            Substitute.For<IPhysicalDeviceCatalog>(), Substitute.For<IStreamingRouteResolver>(), guides, Substitute.For<IVirtualMpegTsOutputService>(),
            new ActiveStreamRegistry(), NullLogger<LineupLinkController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.Request.Headers.Authorization = "Bearer " + grant.GeneratedToken;

        // Act
        var exported = dedicatedMapping
            ? LineupLinkGuideExporter.Create(snapshot)
            : Assert.IsType<LineupLinkGuide>((await controller.GetGuideAsync(id.Value, TestContext.Current.CancellationToken)).Value);
        var resolver = Substitute.For<IPhysicalDeviceResolver>();
        var linked = Substitute.For<IPhysicalTunerDevice, ILineupLinkGuideCapability>();
        var capability = (ILineupLinkGuideCapability)linked;
        var linkedId = PhysicalDeviceId.New();
        capability.PhysicalDeviceId.Returns(linkedId);
        capability.Manifest.Returns(new LineupLinkManifest { InstanceId = Guid.NewGuid(), VirtualDeviceId = id.Value, Name = "Export" });
        capability.GetGuideAsync(Arg.Any<CancellationToken>()).Returns(exported);
        resolver.GetRequired(linkedId).Returns(linked);
        var driver = new LineupLinkGuideSourceDriver(resolver);
        var importId = GuideSourceId.New();
        var importer = await driver.CreateAsync(importId, driver.CreateDefaultConfiguration(importId), [linkedId], TestContext.Current.CancellationToken);
        var imported = await importer.RefreshAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(["7.1", "9.1"], imported.Channels.Select(channel => channel.Number));
        Assert.Equal(["7.1", "9.1"], imported.Programs.Select(program => program.Title).Order());
        Assert.All(imported.Channels, channel => Assert.Single(imported.Programs, program => program.ChannelId == channel.Id));
        Assert.All(imported.Channels, channel => Assert.Equal(dedicatedMapping, channel.IsProtected));
        Assert.All(imported.Channels, channel => Assert.Equal(
            channel.Number, Assert.Single(Assert.IsType<XmltvSupplementalMetadata>(XmltvSupplementalMetadata.Parse(channel.SupplementalXml)).Elements).Value));
        Assert.All(imported.Programs, program => Assert.Equal(
            program.Title, Assert.Single(Assert.IsType<XmltvSupplementalMetadata>(XmltvSupplementalMetadata.Parse(program.SupplementalXml)).Elements).Value));
        if (metadataCount == 0)
        {
            Assert.Null(exported.SupplementalXml);
            Assert.Null(imported.SupplementalXml);
        }
        else
        {
            var metadata = Assert.IsType<XmltvSupplementalMetadata>(XmltvSupplementalMetadata.Parse(imported.SupplementalXml));
            Assert.Equal("First", Assert.Single(metadata.Attributes, attribute => attribute.Name == "source-info-name").Value);
            Assert.Equal(new[] { "First", "Second" }.Take(metadataCount), metadata.Elements.Select(element => element.Value));
            Assert.Equal(metadataCount, metadata.Attributes.Count(attribute => attribute.Name.LocalName == "provider"));
        }
    }

    /// <summary>Verifies two remote metadata envelopes publish successfully without losing root, channel, or program metadata.</summary>
    [Theory]
    [InlineData("xmltv")]
    [InlineData("json")]
    public async Task PublishAsync_MultipleLinkedGuides_PreservesMergedMetadata(string format)
    {
        // Arrange
        using var fixture = new Fixture();
        await fixture.AddDeviceAsync("7.1", RootMetadata("First"));
        await fixture.AddDeviceAsync("9.1", RootMetadata("Second"));
        var import = await fixture.RefreshAsync();
        await fixture.Store.ImportAsync(import, TestContext.Current.CancellationToken);
        var snapshot = await fixture.SnapshotAsync();
        IGuidePublisher publisher = format == "xmltv" ? new XmltvGuidePublisher() : new JsonGuidePublisher();
        await using var destination = new MemoryStream();

        // Act
        await publisher.PublishAsync(snapshot, destination, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, import.Channels.Count);
        Assert.Equal(2, import.Programs.Count);
        Assert.Equal(2, snapshot.Channels.Count);
        Assert.All(snapshot.Channels, channel => Assert.Single(channel.Programs));
        var merged = Assert.IsType<XmltvSupplementalMetadata>(XmltvSupplementalMetadata.Parse(import.SupplementalXml));
        Assert.Equal("First", Assert.Single(merged.Attributes, attribute => attribute.Name == "source-info-name").Value);
        Assert.Equal(["First", "Second"], merged.Elements.Select(element => element.Value));
        Assert.Equal(2, merged.Attributes.Count(attribute => attribute.Name.LocalName == "provider"));
        Assert.All(snapshot.Channels, channel => Assert.NotNull(channel.SupplementalXml));
        Assert.All(snapshot.Channels.SelectMany(channel => channel.Programs), program => Assert.NotNull(program.SupplementalXml));
        destination.Position = 0;
        if (format == "xmltv")
        {
            var document = XDocument.Load(destination);
            var root = Assert.IsType<XElement>(document.Root);
            Assert.Equal("First", root.Attribute("source-info-name")?.Value);
            Assert.Equal(["First", "Second"], root.Elements(XName.Get("provider", "urn:lineup:test:providers")).Select(element => element.Value));
            Assert.Equal("First", root.Attribute(XName.Get("provider", "urn:lineup:test:First"))?.Value);
            Assert.Equal("Second", root.Attribute(XName.Get("provider", "urn:lineup:test:Second"))?.Value);
            Assert.Equal(2, root.Elements("channel").Count());
            Assert.Equal(2, root.Elements("programme").Count());
            Assert.Equal(2, root.Descendants("display-name").Count(element => (string?)element.Attribute("lang") == "es"));
            Assert.Equal(2, root.Descendants("desc").Count(element => (string?)element.Attribute("lang") == "es"));
        }
        else
        {
            var document = await JsonGuideSerialization.DeserializeAsync(destination, TestContext.Current.CancellationToken);
            Assert.NotNull(document);
            Assert.Equal("First", document.Supplemental["source-info-name"]?.GetValue<string>());
            Assert.Contains("First", document.Supplemental.ToJsonString(), StringComparison.Ordinal);
            Assert.Contains("Second", document.Supplemental.ToJsonString(), StringComparison.Ordinal);
            Assert.Equal(2, document.Channels.Count);
            Assert.All(document.Channels, channel => Assert.Single(channel.Programs));
            Assert.All(document.Channels, channel => Assert.NotNull(channel.Supplemental));
            Assert.All(document.Channels.SelectMany(channel => channel.Programs), program => Assert.NotNull(program.Supplemental));
        }
    }

    /// <summary>Verifies missing and single envelopes remain supported and multiple envelopes merge in subscription order.</summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData(" ", "")]
    [InlineData("First", null)]
    [InlineData(null, "Second")]
    [InlineData("First", "Second")]
    public async Task RefreshAsync_RootMetadata_ProducesOneOptionalEnvelope(string? first, string? second)
    {
        // Arrange
        using var fixture = new Fixture();
        await fixture.AddDeviceAsync("7.1", string.IsNullOrWhiteSpace(first) ? first : RootMetadata(first));
        await fixture.AddDeviceAsync("9.1", string.IsNullOrWhiteSpace(second) ? second : RootMetadata(second));

        // Act
        var import = await fixture.RefreshAsync();

        // Assert
        var expected = new[] { first, second }.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        if (expected.Length == 0)
        {
            Assert.Null(import.SupplementalXml);
        }
        else
        {
            var merged = Assert.IsType<XmltvSupplementalMetadata>(XmltvSupplementalMetadata.Parse(import.SupplementalXml));
            Assert.Equal(expected, merged.Elements.Select(element => element.Value));
            Assert.Equal(expected[0], Assert.Single(merged.Attributes, attribute => attribute.Name == "source-info-name").Value);
            Assert.Equal(expected.Length, merged.Attributes.Count(attribute => attribute.Name.LocalName == "provider"));
        }
        Assert.Equal(2, import.Channels.Count);
        Assert.Equal(2, import.Programs.Count);
    }

    /// <summary>Verifies invalid remote envelopes fail refresh safely without replacing a previously committed guide.</summary>
    [Theory]
    [InlineData("<broken>")]
    [InlineData("<unsupported/>")]
    [InlineData("<!DOCTYPE supplemental [<!ENTITY blocked SYSTEM 'file:///lineup-must-not-read'>]><supplemental xmlns='urn:lineup:xmltv-supplemental:v1'>&blocked;</supplemental>")]
    public async Task RefreshAllAsync_InvalidLinkedMetadata_PreservesCommittedImport(string invalid)
    {
        // Arrange
        using var fixture = new Fixture();
        await fixture.AddDeviceAsync("7.1", RootMetadata("First"));
        await fixture.Store.ImportAsync(await fixture.RefreshAsync(), TestContext.Current.CancellationToken);
        var prior = Assert.Single(await fixture.Store.ReadImportStatesAsync(TestContext.Current.CancellationToken));
        await fixture.AddDeviceAsync("9.1", invalid);
        var orchestrator = new GuideOrchestrator(new StreamingAdapterRegistry([], [], [fixture.Driver]), fixture.Store, fixture.Topology);
        await orchestrator.ApplyAsync(new StreamingConfiguration
        {
            PhysicalDevices = fixture.Definitions,
            GuideSources = [new GuideSourceDefinition { Id = fixture.SourceId, Name = "Linked guides", Adapter = fixture.Driver.CreateDefaultConfiguration(fixture.SourceId) }]
        }, TestContext.Current.CancellationToken);

        // Act
        var result = Assert.Single(await orchestrator.RefreshAllAsync(TestContext.Current.CancellationToken));

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains("InvalidDataException", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(invalid, result.Error!, StringComparison.Ordinal);
        Assert.Equal(prior, Assert.Single(await fixture.Store.ReadImportStatesAsync(TestContext.Current.CancellationToken)));
        var snapshot = await fixture.SnapshotAsync();
        Assert.Single(snapshot.Channels.SelectMany(channel => channel.Programs));
        Assert.Contains("First", Assert.Single(snapshot.SupplementalXml), StringComparison.Ordinal);
    }

    private static string? RootMetadata(string provider) => XmltvSupplementalMetadata.Create(
        [new XAttribute("source-info-name", provider), new XAttribute(XName.Get("provider", $"urn:lineup:test:{provider}"), provider)],
        [new XElement(XName.Get("provider", "urn:lineup:test:providers"), provider)]);

    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("lineup-linked-guide-");
        private readonly IPhysicalDeviceResolver _devices = Substitute.For<IPhysicalDeviceResolver>();
        private readonly List<ResolvedVirtualChannel> _channels = [];
        private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

        /// <summary>Creates isolated SQLite storage and linked-device resolution.</summary>
        internal Fixture()
        {
            var factory = new ContextFactory(Path.Combine(_directory.FullName, "guides.db"));
            Store = new DeviceGuideStore(factory);
            Topology = new StreamingTopologyStore(factory);
            Driver = new LineupLinkGuideSourceDriver(_devices);
        }

        /// <summary>Gets the shared local guide-source identity.</summary>
        internal GuideSourceId SourceId { get; } = GuideSourceId.New();

        /// <summary>Gets the configured physical links in subscription order.</summary>
        internal List<PhysicalDeviceDefinition> Definitions { get; } = [];

        /// <summary>Gets persisted guide storage.</summary>
        internal DeviceGuideStore Store { get; }

        /// <summary>Gets persisted physical-channel identities.</summary>
        internal StreamingTopologyStore Topology { get; }

        /// <summary>Gets the real linked guide-source driver.</summary>
        internal LineupLinkGuideSourceDriver Driver { get; }

        /// <summary>Adds one remote link with a distinct qualified channel and deterministic guide data.</summary>
        internal async Task AddDeviceAsync(string number, string? supplemental)
        {
            var id = PhysicalDeviceId.New();
            var manifest = new LineupLinkManifest { InstanceId = Guid.NewGuid(), VirtualDeviceId = Guid.NewGuid(), Name = $"Remote {number}" };
            var device = Substitute.For<IPhysicalTunerDevice, ILineupLinkGuideCapability>();
            var capability = (ILineupLinkGuideCapability)device;
            capability.PhysicalDeviceId.Returns(id);
            capability.Manifest.Returns(manifest);
            capability.GetGuideAsync(Arg.Any<CancellationToken>()).Returns(call =>
            {
                call.Arg<CancellationToken>().ThrowIfCancellationRequested();
                return Task.FromResult(new LineupLinkGuide
                {
                    VirtualDeviceId = manifest.VirtualDeviceId,
                    Channels =
                    [
                        new LineupLinkGuideChannel
                        {
                            Key = "station",
                            Number = number,
                            Name = $"News {number}",
                            SupplementalXml = XmltvSupplementalMetadata.Create(elements: [new XElement("display-name", new XAttribute("lang", "es"), $"Canal {number}")])
                        }
                    ],
                    Programs =
                    [
                        new LineupLinkGuideProgram
                        {
                            ChannelKey = "station",
                            StartUtc = _now,
                            EndUtc = _now.AddHours(1),
                            Title = $"Program {number}",
                            SupplementalXml = XmltvSupplementalMetadata.Create(elements: [new XElement("desc", new XAttribute("lang", "es"), $"Detalle {number}")])
                        }
                    ],
                    SupplementalXml = supplemental
                });
            });
            _devices.GetRequired(id).Returns(device);
            Definitions.Add(new PhysicalDeviceDefinition
            {
                Id = id,
                Name = manifest.Name,
                Adapter = new AdapterConfiguration(Driver.Kind, System.Text.Json.JsonSerializer.SerializeToElement(new { }))
            });
            var key = LineupLinkIdentity.CreateProviderKey(manifest.InstanceId, manifest.VirtualDeviceId, "station");
            var channelId = new SourceChannelId(id, key);
            await Topology.ReconcileAsync(id, [new PhysicalSourceChannel { Id = channelId, ProviderNumber = number, ProviderName = manifest.Name }], TestContext.Current.CancellationToken);
            _channels.Add(new ResolvedVirtualChannel(number, manifest.Name, [channelId]));
        }

        /// <summary>Refreshes the configured links through the actual source adapter.</summary>
        internal async Task<GuideImport> RefreshAsync()
        {
            var source = await Driver.CreateAsync(SourceId, Driver.CreateDefaultConfiguration(SourceId), Definitions.Select(device => device.Id).ToArray(), TestContext.Current.CancellationToken);
            return await source.RefreshAsync(TestContext.Current.CancellationToken);
        }

        /// <summary>Projects committed data onto the configured linked channels.</summary>
        internal Task<VirtualGuideSnapshot> SnapshotAsync() => Store.BuildSnapshotAsync(
            VirtualDeviceId.New(), _channels, [new GuideSourceScope(SourceId, Definitions.Select(device => device.Id).ToArray())], cancellationToken: TestContext.Current.CancellationToken);

        /// <inheritdoc />
        public void Dispose() => _directory.Delete(recursive: true);
    }

    private sealed class ContextFactory(string path) : IDbContextFactory<EpgDbContext>
    {
        /// <inheritdoc />
        public EpgDbContext CreateDbContext() => new(new DbContextOptionsBuilder<EpgDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);
    }
}
