using System.Text;
using System.Xml.Linq;
using Lineup.Common.Guides;
using Lineup.Extensibility.Guides;
using Lineup.Core.Storage;
using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;
using Lineup.GuideOutputs.Xmltv;
using Lineup.GuideOutputs.Xmltv.Plugin;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Lineup.Core.Tests.Guides;

/// <summary>
/// Verifies binding diagnostics and provider-neutral XMLTV publication.
/// </summary>
public sealed class GuideBindingAndPublicationTests
{
    /// <summary>
    /// Verifies unique matches resolve automatically while duplicate same-device matches require an explicit selection.
    /// </summary>
    [Fact]
    public async Task GetBindingDiagnosticsAsync_DuplicateNumber_ReportsAmbiguityAndSuggestion()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-guide-bindings-");
        try
        {
            var factory = new TestContextFactory(Path.Combine(root.FullName, "lineup.db"));
            var guideStore = new DeviceGuideStore(factory);
            var topologyStore = new StreamingTopologyStore(factory);
            var deviceId = PhysicalDeviceId.New();
            var sourceId = GuideSourceId.New();
            await topologyStore.ReconcileAsync(
                deviceId,
                [PhysicalChannel(deviceId, "7"), PhysicalChannel(deviceId, "8")],
                TestContext.Current.CancellationToken);
            var start = DateTimeOffset.Parse("2026-10-02T12:00:00Z");
            var channels = new[]
            {
                GuideChannel(sourceId, "a", "7"),
                GuideChannel(sourceId, "b", "7"),
                GuideChannel(sourceId, "c", "8")
            };
            await guideStore.ImportAsync(
                new GuideImport
                {
                    SourceId = sourceId,
                    Channels = channels,
                    Programs = channels.Select(channel => Program(channel.Id, start)).ToArray(),
                    CoverageStartUtc = start,
                    CoverageEndUtc = start.AddHours(1)
                },
                TestContext.Current.CancellationToken);

            // Act
            var diagnostics = await guideStore.GetBindingDiagnosticsAsync(sourceId, TestContext.Current.CancellationToken);

            // Assert
            Assert.Collection(
                diagnostics.OrderBy(diagnostic => diagnostic.SourceChannelId.ProviderKey),
                diagnostic =>
                {
                    Assert.Equal(GuideBindingState.NeedsGuideSelection, diagnostic.State);
                    Assert.Equal(2, diagnostic.Candidates.Count);
                    Assert.All(diagnostic.Candidates, candidate => Assert.Equal(sourceId, candidate.Id.GuideSourceId));
                },
                diagnostic =>
                {
                    Assert.Equal(GuideBindingState.Automatic, diagnostic.State);
                    Assert.Single(diagnostic.Candidates);
                    Assert.NotNull(diagnostic.BoundChannelId);
                });
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            root.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Verifies XMLTV publication consumes only the provider-neutral virtual snapshot.
    /// </summary>
    [Fact]
    public async Task PublishAsync_VirtualSnapshot_WritesExistingXmltvShape()
    {
        // Arrange
        var virtualDeviceId = VirtualDeviceId.New();
        var sourceId = GuideSourceId.New();
        var snapshot = new VirtualGuideSnapshot
        {
            VirtualDeviceId = virtualDeviceId,
            Channels =
            [
                new VirtualGuideChannel
                {
                    ChannelId = VirtualChannelId.New(virtualDeviceId),
                    Number = "7.1",
                    Name = "News",
                    Programs =
                    [
                        new GuideProgram
                        {
                            ChannelId = new GuideChannelId(sourceId, "station"),
                            StartUtc = DateTimeOffset.Parse("2026-10-02T12:00:00Z"),
                            EndUtc = DateTimeOffset.Parse("2026-10-02T13:00:00Z"),
                            Title = "Noon News",
                            Synopsis = "Local news."
                        }
                    ]
                }
            ]
        };
        var publisher = new XmltvGuidePublisher();
        await using var destination = new MemoryStream();

        // Act
        await publisher.PublishAsync(snapshot, destination, TestContext.Current.CancellationToken);

        // Assert
        var document = XDocument.Parse(Encoding.UTF8.GetString(destination.ToArray()));
        Assert.Equal("Lineup", document.Root?.Attribute("generator-info-name")?.Value);
        Assert.Equal("7.1", document.Descendants("lcn").Single().Value);
        Assert.Equal("News", document.Descendants("display-name").Single().Value);
        Assert.Equal("Noon News", document.Descendants("title").Single().Value);
        Assert.Equal("Local news.", document.Descendants("desc").Single().Value);
    }

    private static PhysicalSourceChannel PhysicalChannel(PhysicalDeviceId deviceId, string number) =>
        new()
        {
            Id = new SourceChannelId(deviceId, number),
            ProviderNumber = number,
            ProviderName = $"Channel {number}"
        };

    private static GuideChannel GuideChannel(GuideSourceId sourceId, string key, string number) =>
        new()
        {
            Id = new GuideChannelId(sourceId, key),
            Number = number,
            Name = $"Guide {key}"
        };

    private static GuideProgram Program(GuideChannelId channelId, DateTimeOffset start) =>
        new()
        {
            ChannelId = channelId,
            StartUtc = start,
            EndUtc = start.AddHours(1),
            Title = "Program"
        };

    private sealed class TestContextFactory(string path) : IDbContextFactory<EpgDbContext>
    {
        private readonly DbContextOptions<EpgDbContext> _options = new DbContextOptionsBuilder<EpgDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;

        public EpgDbContext CreateDbContext() => new(_options);
    }
}
