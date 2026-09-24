using System.Text;
using Xunit;

namespace Lineup.Core.Tests;

/// <summary>
/// Verifies normalization of SiliconDust XMLTV input.
/// </summary>
public class SiliconDustXmltvParserTests
{
    /// <summary>
    /// Verifies that channel numbers and programme metadata are mapped into provider-neutral guide models.
    /// </summary>
    [Fact]
    public void Parse_MapsChannelAndProgrammeMetadata()
    {
        // Arrange
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <tv source-info-name="HDHomeRun">
              <channel id="US123.hdhomerun.com">
                <display-name>WTEST</display-name>
                <display-name>7.1 WTEST</display-name>
                <lcn>7.1</lcn>
                <icon src="https://example.test/channel.png" />
              </channel>
              <programme start="20260914220000 -0400" stop="20260914223000 -0400" channel="US123.hdhomerun.com">
                <title lang="en">Test Show</title>
                <sub-title lang="en">Pilot</sub-title>
                <desc lang="en">Description</desc>
                <date>20250901</date>
                <category>Series</category>
                <category>Drama</category>
                <icon src="https://example.test/program.png" />
                <episode-num system="dd_progid">EP0001.0001</episode-num>
                <episode-num system="onscreen">S01E02</episode-num>
                <series-id>12345</series-id>
                <new />
              </programme>
            </tv>
            """;
        var parser = new SiliconDustXmltvParser();

        // Act
        var channel = Assert.Single(parser.Parse(Encoding.UTF8.GetBytes(xml)));

        // Assert
        var programme = Assert.Single(channel.Guide);
        Assert.Equal("7.1", channel.GuideNumber);
        Assert.Equal("WTEST", channel.GuideName);
        Assert.Equal("https://example.test/channel.png", channel.ImageURL);
        Assert.Equal("Test Show", programme.Title);
        Assert.Equal("Pilot", programme.EpisodeTitle);
        Assert.Equal("Description", programme.Synopsis);
        Assert.Equal("S01E02", programme.EpisodeNumber);
        Assert.Equal("12345", programme.SeriesID);
        Assert.Equal(1, programme.First);
        Assert.Equal(["Series", "Drama"], programme.Filter);
        Assert.Equal(new DateTimeOffset(2026, 9, 14, 22, 0, 0, TimeSpan.FromHours(-4)).ToUnixTimeSeconds(), programme.StartTime);
    }

    /// <summary>
    /// Verifies that unsafe document types are rejected.
    /// </summary>
    [Fact]
    public void Parse_RejectsDocumentTypeDefinitions()
    {
        // Arrange
        const string xml = """<!DOCTYPE tv [<!ENTITY value "unsafe">]><tv>&value;</tv>""";
        var parser = new SiliconDustXmltvParser();

        // Act
        var action = () => parser.Parse(Encoding.UTF8.GetBytes(xml));

        // Assert
        Assert.Throws<System.Xml.XmlException>(action);
    }

    /// <summary>
    /// Verifies that station IDs shared by simulcast channels retain every logical channel number.
    /// </summary>
    [Fact]
    public void Parse_FansSharedStationProgrammesOutToEveryLogicalChannel()
    {
        // Arrange
        const string xml = """
            <tv>
              <channel id="shared"><display-name>Primary</display-name><lcn>7.1</lcn></channel>
              <channel id="shared"><display-name>Simulcast</display-name><lcn>107.1</lcn></channel>
              <programme start="20260914220000 +0000" stop="20260914223000 +0000" channel="shared">
                <title>Shared Show</title>
              </programme>
            </tv>
            """;
        var parser = new SiliconDustXmltvParser();

        // Act
        var channels = parser.Parse(Encoding.UTF8.GetBytes(xml));

        // Assert
        Assert.Equal(["7.1", "107.1"], channels.Select(channel => channel.GuideNumber));
        Assert.All(channels, channel => Assert.Equal("Shared Show", Assert.Single(channel.Guide).Title));
    }
}
