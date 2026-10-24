using System.Xml.Linq;
using Lineup.GuideOutputs.Json;
using Lineup.GuideOutputs.Json.Plugin;
using Lineup.GuideOutputs.Xmltv.Plugin;
using Lineup.LineupLink.Guides;
using Lineup.LineupLink.Physical;
using Lineup.LineupLink.Protocol;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>Verifies linked guide metadata survives import, persistence, projection, and publication.</summary>
public sealed class LineupLinkGuidePublicationTests
{
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
