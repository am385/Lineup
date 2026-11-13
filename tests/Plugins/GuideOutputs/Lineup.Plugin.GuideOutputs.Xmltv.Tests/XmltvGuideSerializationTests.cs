using System.Text;
using System.Xml.Linq;
using Jellyfin.XmlTv;
using Lineup.Core.Common.Guides;
using Lineup.Core.Common.Streaming;
using Xunit;

namespace Lineup.Plugin.GuideOutputs.Xmltv.Tests;

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

    /// <summary>Verifies Jellyfin retains programme artwork immediately following preserved language metadata.</summary>
    [Theory]
    [InlineData("language")]
    [InlineData("orig-language")]
    public void Serialize_LanguageBeforeProgramIcon_JellyfinRetainsArtwork(string languageElement)
    {
        // Arrange
        var virtualDeviceId = VirtualDeviceId.New();
        var start = new DateTimeOffset(2026, 11, 13, 18, 0, 0, TimeSpan.Zero);
        const string imageUrl = "https://example.test/program.jpg";
        XNamespace supplementalNamespace = "urn:lineup:xmltv-supplemental:v1";
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
                            StartUtc = start,
                            EndUtc = start.AddHours(1),
                            Title = "Evening News",
                            ImageUrl = imageUrl,
                            SupplementalXml = new XElement(
                                supplementalNamespace + "supplemental",
                                new XElement(supplementalNamespace + "elements", new XElement(languageElement, "English"))).ToString()
                        }
                    ]
                }
            ]
        };
        var path = Path.Combine(Path.GetTempPath(), $"lineup-jellyfin-artwork-{Guid.NewGuid():N}.xml");
        try
        {
            // Act
            File.WriteAllBytes(path, XmltvGuideSerialization.Serialize(snapshot));
            var reader = new XmlTvReader(path, "en");
            var channel = Assert.Single(reader.GetChannels());
            var program = Assert.Single(reader.GetProgrammes(channel.Id, start, start.AddHours(1), CancellationToken.None));

            // Assert
            Assert.Equal("Evening News", program.Title);
            Assert.Equal("English", languageElement == "language" ? program.Language : program.OriginalLanguage);
            Assert.NotNull(program.Icons);
            Assert.Equal(imageUrl, Assert.Single(program.Icons).Source);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
