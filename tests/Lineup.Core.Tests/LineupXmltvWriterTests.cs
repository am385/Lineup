using Lineup.HDHomeRun.Api.Models;
using Lineup.HDHomeRun.Device.Models;
using System.Text;
using System.Xml.Linq;
using Xunit;

namespace Lineup.Core.Tests;

/// <summary>
/// Verifies Lineup XMLTV output independently from provider parsing.
/// </summary>
public sealed class LineupXmltvWriterTests
{
    /// <summary>
    /// Verifies unmodeled official and provider metadata survives normalization and publication.
    /// </summary>
    [Fact]
    public void Write_ParsedSupplementalMetadata_PreservesCompleteMetadata()
    {
        // Arrange
        const string xml = """
            <tv date="20260915" source-info-name="SiliconDust" source-info-url="https://example.test/source" generator-info-name="Provider" provider-version="2">
              <provider-root value="root" />
              <channel id="station" provider-channel="yes">
                <display-name lang="en">WTEST</display-name>
                <display-name lang="es">WTEST Español</display-name>
                <lcn>7.1</lcn>
                <icon src="https://example.test/channel.png" width="320" height="180" />
                <url system="homepage">https://example.test/channel</url>
                <provider-channel-child value="kept" />
              </channel>
              <programme start="20260914220000 +0000" stop="20260914223000 +0000" channel="station" pdc-start="20260914215900 +0000" provider-programme="yes">
                <title lang="en">Primary Show</title>
                <title lang="es">Programa Principal</title>
                <sub-title lang="en">Pilot</sub-title>
                <desc lang="en">Long description</desc>
                <credits><director>Director Name</director><actor role="Lead">Actor Name</actor></credits>
                <date>20250901</date>
                <category lang="en">Drama</category>
                <keyword lang="en">mystery</keyword>
                <language>English</language>
                <orig-language>French</orig-language>
                <length units="minutes">30</length>
                <icon src="https://example.test/program.png" width="640" height="360" />
                <url system="imdb">https://example.test/program</url>
                <country>US</country>
                <episode-num system="onscreen">S01E02</episode-num>
                <episode-num system="xmltv_ns">0.1.0/1</episode-num>
                <video><quality>HDTV</quality></video>
                <audio><stereo>Dolby Digital</stereo></audio>
                <previously-shown start="20250914220000 +0000" />
                <subtitles type="onscreen"><language>Spanish</language></subtitles>
                <rating system="MPAA"><value>TV-14</value></rating>
                <star-rating system="IMDB"><value>8/10</value></star-rating>
                <review type="text" source="Example">Review text</review>
                <image type="poster">https://example.test/poster.jpg</image>
                <series-id>series-1</series-id>
                <provider-programme-child value="kept" />
              </programme>
            </tv>
            """;
        var parser = new SiliconDustXmltvParser();
        var snapshot = parser.Parse(Encoding.UTF8.GetBytes(xml));
        var segment = Assert.Single(snapshot.Segments);
        var programme = Assert.Single(segment.Guide);
        var writer = new LineupXmltvWriter();
        var channel = new HDHomeRunChannel
        {
            GuideNumber = "7.1",
            GuideName = "Tuner Name",
            URL = "http://device/auto/v7.1"
        };

        // Act
        var content = writer.Write(snapshot, [channel], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1));
        var document = XDocument.Parse(Encoding.UTF8.GetString(content));

        // Assert
        Assert.DoesNotContain("Primary Show", programme.SupplementalXml);
        Assert.DoesNotContain("Long description", programme.SupplementalXml);
        Assert.Equal("SiliconDust", (string?)document.Root!.Attribute("source-info-name"));
        Assert.Equal("https://example.test/source", (string?)document.Root.Attribute("source-info-url"));
        Assert.Equal("Lineup", (string?)document.Root.Attribute("generator-info-name"));
        Assert.Equal("2", (string?)document.Root.Attribute("provider-version"));
        Assert.NotNull(document.Root.Element("provider-root"));
        var channelElement = Assert.Single(document.Root.Elements("channel"));
        Assert.Equal("yes", (string?)channelElement.Attribute("provider-channel"));
        Assert.Equal(["Tuner Name", "WTEST Español"], channelElement.Elements("display-name").Select(element => element.Value));
        Assert.Equal("180", (string?)channelElement.Elements("icon").First().Attribute("height"));
        Assert.NotNull(channelElement.Element("url"));
        Assert.NotNull(channelElement.Element("provider-channel-child"));
        var programmeElement = Assert.Single(document.Root.Elements("programme"));
        Assert.Equal("yes", (string?)programmeElement.Attribute("provider-programme"));
        Assert.Equal("20260914215900 +0000", (string?)programmeElement.Attribute("pdc-start"));
        Assert.Equal(["Primary Show", "Programa Principal"], programmeElement.Elements("title").Select(element => element.Value));
        Assert.Equal("en", (string?)programmeElement.Elements("title").First().Attribute("lang"));
        Assert.Equal("Director Name", programmeElement.Element("credits")?.Element("director")?.Value);
        Assert.Equal("0.1.0/1", programmeElement.Elements("episode-num").Single(element => (string?)element.Attribute("system") == "xmltv_ns").Value);
        Assert.Equal("TV-14", programmeElement.Element("rating")?.Element("value")?.Value);
        Assert.NotNull(programmeElement.Element("provider-programme-child"));
        var names = programmeElement.Elements().Select(element => element.Name.LocalName).ToArray();
        Assert.True(Array.IndexOf(names, "credits") < Array.IndexOf(names, "rating"));
    }

    /// <summary>
    /// Verifies corrupted persisted supplemental metadata fails publication explicitly.
    /// </summary>
    [Fact]
    public void Write_InvalidSupplementalXml_ThrowsInvalidDataException()
    {
        // Arrange
        var writer = new LineupXmltvWriter();
        var snapshot = new XmltvGuideSnapshot
        {
            Segments = [],
            SupplementalXml = "<invalid"
        };

        // Act
        var action = () => writer.Write(snapshot, [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1));

        // Assert
        Assert.Throws<InvalidDataException>(action);
    }

    /// <summary>
    /// Verifies normalized guide data round-trips through Lineup XMLTV without losing supported metadata.
    /// </summary>
    [Fact]
    public void Write_NormalizedData_RoundTripsSupportedMetadata()
    {
        // Arrange
        var writer = new LineupXmltvWriter();
        var parser = new SiliconDustXmltvParser();
        var start = new DateTimeOffset(2026, 9, 14, 22, 0, 0, TimeSpan.Zero);
        var segment = new HDHomeRunChannelEpgSegment
        {
            GuideNumber = "7.1",
            GuideName = "Guide Name",
            ImageURL = "https://example.test/channel.png",
            Guide =
            [
                new HDHomeRunProgram
                {
                    Title = "Test Show",
                    EpisodeTitle = "Pilot",
                    Synopsis = "Description",
                    StartTime = start.ToUnixTimeSeconds(),
                    EndTime = start.AddMinutes(30).ToUnixTimeSeconds(),
                    ImageURL = "https://example.test/program.png",
                    EpisodeNumber = "S01E01",
                    OriginalAirdate = start.AddYears(-1).ToUnixTimeSeconds(),
                    First = 1,
                    SeriesID = "series-1",
                    Filter = ["Drama", "Series"]
                }
            ]
        };
        var channel = new HDHomeRunChannel
        {
            GuideNumber = "7.1",
            GuideName = "Tuner Name",
            URL = "http://device/auto/v7.1"
        };

        // Act
        var content = writer.Write([segment], [channel], start, start.AddDays(1));
        var result = Assert.Single(parser.Parse(content).Segments);

        // Assert
        var programme = Assert.Single(result.Guide);
        Assert.Equal("Tuner Name", result.GuideName);
        Assert.Equal("https://example.test/channel.png", result.ImageURL);
        Assert.Equal("Test Show", programme.Title);
        Assert.Equal("Pilot", programme.EpisodeTitle);
        Assert.Equal("Description", programme.Synopsis);
        Assert.Equal("S01E01", programme.EpisodeNumber);
        Assert.Equal("series-1", programme.SeriesID);
        Assert.Equal(["Drama", "Series"], programme.Filter);
        Assert.Equal(1, programme.First);
    }
}
