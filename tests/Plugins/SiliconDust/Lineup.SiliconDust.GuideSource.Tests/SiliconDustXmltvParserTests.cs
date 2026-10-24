using System.Text;
using Xunit;

namespace Lineup.SiliconDust.GuideSource.Tests;

/// <summary>
/// Verifies normalization of SiliconDust XMLTV input.
/// </summary>
public class SiliconDustXmltvParserTests
{
    /// <summary>
    /// Verifies that channel numbers and program metadata are mapped into provider-neutral guide models.
    /// </summary>
    [Fact]
    public void Parse_MapsChannelAndProgramMetadata()
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
        var channel = Assert.Single(parser.Parse(Encoding.UTF8.GetBytes(xml)).Segments);

        // Assert
        var program = Assert.Single(channel.Guide);
        Assert.Equal("7.1", channel.GuideNumber);
        Assert.Equal("WTEST", channel.GuideName);
        Assert.Equal("https://example.test/channel.png", channel.ImageURL);
        Assert.Equal("Test Show", program.Title);
        Assert.Equal("Pilot", program.EpisodeTitle);
        Assert.Equal("Description", program.Synopsis);
        Assert.Equal("S01E02", program.EpisodeNumber);
        Assert.Equal("12345", program.SeriesID);
        Assert.Equal(1, program.First);
        Assert.Equal(["Series", "Drama"], program.Filter);
        Assert.Equal(new DateTimeOffset(2026, 9, 14, 22, 0, 0, TimeSpan.FromHours(-4)).ToUnixTimeSeconds(), program.StartTime);
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
    public void Parse_FansSharedStationProgramsOutToEveryLogicalChannel()
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
        var channels = parser.Parse(Encoding.UTF8.GetBytes(xml)).Segments;

        // Assert
        Assert.Equal(["7.1", "107.1"], channels.Select(channel => channel.GuideNumber));
        Assert.All(channels, channel => Assert.Equal("Shared Show", Assert.Single(channel.Guide).Title));
    }

    /// <summary>
    /// Verifies distinct station identities sharing a logical channel number are not silently merged.
    /// </summary>
    [Fact]
    public void Parse_DifferentStationsWithSameNumber_RetainsBothCandidates()
    {
        // Arrange
        const string xml = """
            <tv>
              <channel id="station-a"><display-name>Station A</display-name><lcn>7.1</lcn></channel>
              <channel id="station-b"><display-name>Station B</display-name><lcn>7.1</lcn></channel>
              <programme start="20260914220000 +0000" stop="20260914223000 +0000" channel="station-a">
                <title>Show A</title>
              </programme>
              <programme start="20260914223000 +0000" stop="20260914230000 +0000" channel="station-b">
                <title>Show B</title>
              </programme>
            </tv>
            """;
        var parser = new SiliconDustXmltvParser();

        // Act
        var channels = parser.Parse(Encoding.UTF8.GetBytes(xml)).Segments;

        // Assert
        Assert.Equal(2, channels.Count);
        Assert.All(channels, channel => Assert.Equal("7.1", channel.GuideNumber));
        Assert.Equal(2, channels.Select(channel => channel.ProviderKey).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(channels, channel => channel.GuideName == "Station A" && Assert.Single(channel.Guide).Title == "Show A");
        Assert.Contains(channels, channel => channel.GuideName == "Station B" && Assert.Single(channel.Guide).Title == "Show B");
    }

    /// <summary>
    /// Verifies minimal source data does not allocate empty supplemental envelopes.
    /// </summary>
    [Fact]
    public void Parse_MinimalTypedData_LeavesSupplementalMetadataNull()
    {
        // Arrange
        const string xml = """
            <tv>
              <channel id="station"><display-name>Channel</display-name><lcn>7.1</lcn></channel>
              <programme start="20260914220000 +0000" stop="20260914223000 +0000" channel="station"><title>Show</title></programme>
            </tv>
            """;
        var parser = new SiliconDustXmltvParser();

        // Act
        var result = parser.Parse(Encoding.UTF8.GetBytes(xml));

        // Assert
        Assert.Null(result.SupplementalXml);
        var channel = Assert.Single(result.Segments);
        Assert.Null(channel.SupplementalXml);
        Assert.Null(Assert.Single(channel.Guide).SupplementalXml);
    }
}
