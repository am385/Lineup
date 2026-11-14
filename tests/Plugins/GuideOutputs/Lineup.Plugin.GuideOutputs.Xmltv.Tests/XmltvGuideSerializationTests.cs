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
    /// <summary>Verifies optional placeholders retain empty channels without hiding real programs or matching titles.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Serialize_UnavailableChannels_RespectsPlaceholderPolicy(bool includeUnavailablePrograms, bool hasRealProgram)
    {
        // Arrange
        var deviceId = VirtualDeviceId.New();
        var start = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
        var snapshot = new VirtualGuideSnapshot
        {
            VirtualDeviceId = deviceId,
            Channels =
            [
                new VirtualGuideChannel
                {
                    ChannelId = VirtualChannelId.New(deviceId),
                    Number = "7.1",
                    Name = "No guide",
                    Programs = []
                },
                new VirtualGuideChannel
                {
                    ChannelId = VirtualChannelId.New(deviceId),
                    Number = "8.1",
                    Name = "With guide",
                    Programs = hasRealProgram
                        ? [new GuideProgram
                        {
                            ChannelId = new GuideChannelId(GuideSourceId.New(), "station"),
                            StartUtc = start,
                            EndUtc = start.AddHours(1),
                            Title = "Not Available",
                            Synopsis = "An actual provider program with the same title."
                        }]
                        : []
                }
            ]
        };

        // Act
        var content = includeUnavailablePrograms ? XmltvGuideSerialization.Serialize(snapshot, true) : XmltvGuideSerialization.Serialize(snapshot);
        var document = XDocument.Parse(Encoding.UTF8.GetString(content));

        // Assert
        Assert.Equal(["lineup.channel.7.1", "lineup.channel.8.1"], document.Root!.Elements("channel").Select(channel => (string?)channel.Attribute("id")));
        var programs = document.Root.Elements("programme").ToArray();
        Assert.Equal((hasRealProgram ? 1 : 0) + (includeUnavailablePrograms ? hasRealProgram ? 1 : 2 : 0), programs.Length);
        Assert.Equal(includeUnavailablePrograms ? 1 : 0, programs.Count(program => (string?)program.Attribute("channel") == "lineup.channel.7.1"));
        if (hasRealProgram)
        {
            var program = Assert.Single(programs, program => (string?)program.Attribute("channel") == "lineup.channel.8.1");
            Assert.Equal("Not Available", program.Element("title")?.Value);
            Assert.Equal("An actual provider program with the same title.", program.Element("desc")?.Value);
            Assert.Equal("20261114120000 +0000", (string?)program.Attribute("start"));
            Assert.Equal("20261114130000 +0000", (string?)program.Attribute("stop"));
        }
    }

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

    /// <summary>Verifies Jellyfin retains program artwork immediately following preserved language metadata.</summary>
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
