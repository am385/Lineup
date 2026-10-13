using System.Text;
using System.Xml.Linq;
using Lineup.Common.Guides;
using Lineup.Common.Streaming;
using Xunit;

namespace Lineup.GuideOutputs.Xmltv.Tests;

/// <summary>Verifies the standalone XMLTV serializer.</summary>
public sealed class XmltvGuideSerializationTests
{
    /// <summary>Verifies a normalized virtual guide can be serialized without plugin implementation dependencies.</summary>
    [Fact]
    public void Serialize_VirtualGuideSnapshot_WritesXmltv()
    {
        // Arrange
        var virtualDeviceId = VirtualDeviceId.New();
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
                            ChannelId = new GuideChannelId(GuideSourceId.New(), "provider-channel"),
                            StartUtc = new DateTimeOffset(2026, 10, 9, 18, 0, 0, TimeSpan.Zero),
                            EndUtc = new DateTimeOffset(2026, 10, 9, 19, 0, 0, TimeSpan.Zero),
                            Title = "Evening News"
                        }
                    ]
                }
            ]
        };

        // Act
        var content = XmltvGuideSerialization.Serialize(snapshot);
        var document = XDocument.Parse(Encoding.UTF8.GetString(content));

        // Assert
        Assert.Equal("Lineup", (string?)document.Root?.Attribute("generator-info-name"));
        Assert.Equal("Example", document.Root?.Element("channel")?.Element("display-name")?.Value);
        Assert.Equal("Evening News", document.Root?.Element("programme")?.Element("title")?.Value);
    }
}
