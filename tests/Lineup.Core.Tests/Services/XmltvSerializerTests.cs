using Lineup.Core.Models.Xmltv;
using Lineup.Core.Services;
using Xunit;

namespace Lineup.Core.Tests.Services;

public class XmltvSerializerTests
{
    [Fact]
    public void SerializeToStream_CreatesValidXml_WhenGivenDocument()
    {
        // Arrange
        var document = new XmltvDocument
        {
            SourceInfoName = "Lineup",
            GeneratorInfoName = "Lineup.Core",
            Channels =
            [
                new XmltvChannel
                {
                    Id = "5.1",
                    DisplayName = "PBS NC",
                    Icon = new XmltvIcon { Source = "http://example.com/pbs.png" }
                }
            ],
            Programmes =
            [
                new XmltvProgramme
                {
                    Start = "20240101120000 +0000",
                    Stop = "20240101130000 +0000",
                    Channel = "5.1",
                    Title = new XmltvText { Language = "en", Value = "Test Show" }
                }
            ]
        };

        using var stream = new MemoryStream();

        // Act
        XmltvSerializer.SerializeToStream(document, stream);

        // Assert
        Assert.True(stream.Length > 0);
        stream.Position = 0;
        using var reader = new StreamReader(stream);
        var xml = reader.ReadToEnd();
        Assert.Contains("<tv", xml);
        Assert.Contains("source-info-name=\"Lineup\"", xml);
        Assert.Contains("<channel id=\"5.1\"", xml);
        Assert.Contains("<programme", xml);
    }

    [Fact]
    public void DeserializeFromStream_ReturnsDocument_WhenGivenValidXml()
    {
        // Arrange
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <tv source-info-name="Test Source" generator-info-name="Test Generator">
              <channel id="2.1">
                <display-name>WFMY-HD</display-name>
              </channel>
              <programme start="20240101120000 +0000" stop="20240101130000 +0000" channel="2.1">
                <title lang="en">Test Program</title>
              </programme>
            </tv>
            """;

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));

        // Act
        var result = XmltvSerializer.DeserializeFromStream(stream);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Test Source", result.SourceInfoName);
        Assert.Equal("Test Generator", result.GeneratorInfoName);
        Assert.Single(result.Channels);
        Assert.Equal("2.1", result.Channels[0].Id);
        Assert.Equal("WFMY-HD", result.Channels[0].DisplayName);
        Assert.Single(result.Programmes);
        Assert.Equal("Test Program", result.Programmes[0].Title?.Value);
    }

    [Fact]
    public void RoundTrip_PreservesData_WhenSerializingAndDeserializing()
    {
        // Arrange
        var original = new XmltvDocument
        {
            SourceInfoName = "Lineup",
            GeneratorInfoName = "Lineup.Core",
            Channels =
            [
                new XmltvChannel
                {
                    Id = "7.1",
                    DisplayName = "ABC",
                    Icon = new XmltvIcon { Source = "http://example.com/abc.png" }
                },
                new XmltvChannel
                {
                    Id = "11.1",
                    DisplayName = "NBC"
                }
            ],
            Programmes =
            [
                new XmltvProgramme
                {
                    Start = "20240615200000 -0400",
                    Stop = "20240615210000 -0400",
                    Channel = "7.1",
                    Title = new XmltvText { Language = "en", Value = "Evening News" },
                    Description = new XmltvText { Language = "en", Value = "Daily news broadcast" }
                }
            ]
        };

        using var stream = new MemoryStream();

        // Act
        XmltvSerializer.SerializeToStream(original, stream);
        stream.Position = 0;
        var result = XmltvSerializer.DeserializeFromStream(stream);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(original.SourceInfoName, result.SourceInfoName);
        Assert.Equal(original.GeneratorInfoName, result.GeneratorInfoName);
        Assert.Equal(original.Channels.Count, result.Channels.Count);
        Assert.Equal(original.Programmes.Count, result.Programmes.Count);
        Assert.Equal(original.Channels[0].Id, result.Channels[0].Id);
        Assert.Equal(original.Programmes[0].Title?.Value, result.Programmes[0].Title?.Value);
    }

    [Fact]
    public void SerializeToStream_HandlesEmptyDocument()
    {
        // Arrange
        var document = new XmltvDocument();
        using var stream = new MemoryStream();

        // Act
        XmltvSerializer.SerializeToStream(document, stream);

        // Assert
        Assert.True(stream.Length > 0);
        stream.Position = 0;
        var result = XmltvSerializer.DeserializeFromStream(stream);
        Assert.NotNull(result);
        Assert.Empty(result.Channels);
        Assert.Empty(result.Programmes);
    }
}
