using System.Text.Json;
using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;
using Xunit;

namespace Lineup.Core.Tests.Streaming;

/// <summary>
/// Verifies provider-neutral channel projection.
/// </summary>
public sealed class ChannelTopologyResolverTests
{
    /// <summary>
    /// Verifies duplicate numbers publish the first configured device and flag the later source.
    /// </summary>
    [Fact]
    public void Resolve_DuplicateNumbers_ReportsLaterPhysicalSource()
    {
        // Arrange
        var first = CreatePhysical("First", 0);
        var second = CreatePhysical("Second", 1);
        var output = CreateVirtual();
        PhysicalChannelIdentity[] channels =
        [
            CreateChannel(first.Id, "a", "7.1", "First News"),
            CreateChannel(second.Id, "b", "7.1", "Second News")
        ];

        // Act
        var result = ChannelTopologyResolver.Resolve(output, [first, second], channels);

        // Assert
        Assert.Equal(first.Id, Assert.Single(result.Channels).Sources[0].PhysicalDeviceId);
        Assert.Equal(second.Id, Assert.Single(result.Conflicts).ConflictingSource.PhysicalDeviceId);
    }

    /// <summary>
    /// Verifies one physical override resolves the number consistently before virtual projection.
    /// </summary>
    [Fact]
    public void Resolve_GlobalNumberOverride_PublishesBothSources()
    {
        // Arrange
        var first = CreatePhysical("First", 0);
        var second = CreatePhysical("Second", 1);
        var output = CreateVirtual();
        PhysicalChannelIdentity[] channels =
        [
            CreateChannel(first.Id, "a", "7.1", "First News"),
            CreateChannel(second.Id, "b", "7.1", "Second News") with { NumberOverride = "107.1" }
        ];

        // Act
        var result = ChannelTopologyResolver.Resolve(output, [first, second], channels);

        // Assert
        Assert.Empty(result.Conflicts);
        Assert.Equal(["7.1", "107.1"], result.Channels.Select(channel => channel.Number));
    }

    /// <summary>
    /// Verifies equivalent sources become ordered candidates for one channel.
    /// </summary>
    [Fact]
    public void Resolve_EquivalentSources_CreatesOneFailoverChannel()
    {
        // Arrange
        var first = CreatePhysical("First", 0);
        var second = CreatePhysical("Second", 1);
        var output = CreateVirtual();
        var group = Guid.NewGuid();
        PhysicalChannelIdentity[] channels =
        [
            CreateChannel(first.Id, "a", "7.1", "News") with { EquivalenceGroupId = group },
            CreateChannel(second.Id, "b", "7.1", "News") with { EquivalenceGroupId = group }
        ];

        // Act
        var result = ChannelTopologyResolver.Resolve(output, [first, second], channels);

        // Assert
        Assert.Empty(result.Conflicts);
        Assert.Equal([first.Id, second.Id], Assert.Single(result.Channels).Sources.Select(source => source.PhysicalDeviceId));
    }

    /// <summary>
    /// Verifies a virtual channel publishes metadata from its preferred physical source.
    /// </summary>
    [Fact]
    public void Resolve_ChannelMetadata_PublishesPreferredSourceValues()
    {
        // Arrange
        var first = CreatePhysical("First", 0);
        var second = CreatePhysical("Second", 1);
        var output = CreateVirtual();
        var group = Guid.NewGuid();
        PhysicalChannelIdentity[] channels =
        [
            CreateChannel(first.Id, "a", "7.1", "News", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["videoCodec"] = "mpeg2video",
                ["signalQuality"] = "92"
            }) with { EquivalenceGroupId = group },
            CreateChannel(second.Id, "b", "7.1", "News", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["videoCodec"] = "h264",
                ["signalQuality"] = "80"
            }) with { EquivalenceGroupId = group }
        ];

        // Act
        var result = ChannelTopologyResolver.Resolve(output, [first, second], channels);

        // Assert
        var channel = Assert.Single(result.Channels);
        Assert.Equal("mpeg2video", channel.Metadata["videoCodec"]);
        Assert.Equal("92", channel.Metadata["signalQuality"]);
    }

    private static PhysicalDeviceDefinition CreatePhysical(string name, int order)
    {
        var id = PhysicalDeviceId.New();
        return new PhysicalDeviceDefinition
        {
            Id = id,
            Name = name,
            Order = order,
            Adapter = EmptyAdapter("hdhomerun")
        };
    }

    private static VirtualDeviceDefinition CreateVirtual() => new()
    {
        Id = VirtualDeviceId.New(),
        Name = "Aggregate",
        Adapter = EmptyAdapter("hdhomerun")
    };

    private static PhysicalChannelIdentity CreateChannel(PhysicalDeviceId deviceId, string key, string number, string name, IReadOnlyDictionary<string, string>? metadata = null) => new()
    {
        Source = new PhysicalSourceChannel
        {
            Id = new SourceChannelId(deviceId, key),
            ProviderNumber = number,
            ProviderName = name,
            Metadata = metadata ?? new Dictionary<string, string>(StringComparer.Ordinal)
        }
    };

    private static AdapterConfiguration EmptyAdapter(string kind)
    {
        using var document = JsonDocument.Parse("{}");
        return new AdapterConfiguration(kind, document.RootElement.Clone());
    }
}
