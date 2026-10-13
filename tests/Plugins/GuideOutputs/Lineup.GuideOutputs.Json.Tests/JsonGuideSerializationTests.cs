using Lineup.Common.Guides;
using Lineup.Common.Streaming;
using Xunit;

namespace Lineup.GuideOutputs.Json.Tests;

/// <summary>Verifies the standalone JSON guide document contract.</summary>
public sealed class JsonGuideSerializationTests
{
    /// <summary>Verifies a consumer can round-trip guide data without referencing plugin implementation assemblies.</summary>
    [Fact]
    public async Task SerializeAsync_VirtualGuideDocument_RoundTrips()
    {
        // Arrange
        var virtualDeviceId = VirtualDeviceId.New();
        var guideSourceId = GuideSourceId.New();
        var snapshot = new VirtualGuideSnapshot
        {
            VirtualDeviceId = virtualDeviceId,
            Channels =
            [
                new VirtualGuideChannel
                {
                    ChannelId = VirtualChannelId.New(virtualDeviceId),
                    Number = "7.1",
                    Name = "Example",
                    Programs =
                    [
                        new GuideProgram
                        {
                            ChannelId = new GuideChannelId(guideSourceId, "provider-channel"),
                            StartUtc = new DateTimeOffset(2026, 10, 9, 18, 0, 0, TimeSpan.Zero),
                            EndUtc = new DateTimeOffset(2026, 10, 9, 19, 0, 0, TimeSpan.Zero),
                            Title = "Evening News"
                        }
                    ]
                }
            ]
        };
        var expected = JsonGuideDocument.FromSnapshot(snapshot);
        await using var destination = new MemoryStream();

        // Act
        await JsonGuideSerialization.SerializeAsync(expected, destination, TestContext.Current.CancellationToken);
        destination.Position = 0;
        var actual = await JsonGuideSerialization.DeserializeAsync(destination, TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(actual);
        Assert.Equal(JsonGuideDocument.CurrentSchemaVersion, actual.SchemaVersion);
        Assert.Equal(virtualDeviceId.Value, actual.VirtualDeviceId);
        var program = Assert.Single(Assert.Single(actual.Channels).Programs);
        Assert.Equal("Evening News", program.Title);
        Assert.Equal(guideSourceId.Value, program.ChannelId.GuideSourceId);
    }
}
