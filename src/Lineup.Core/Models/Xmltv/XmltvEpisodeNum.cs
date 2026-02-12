using System.Xml.Serialization;

namespace Lineup.Core.Models.Xmltv;

/// <summary>
/// Represents an episode number in XMLTV format
/// </summary>
public class XmltvEpisodeNum
{
    [XmlAttribute("system")]
    public string System { get; set; } = string.Empty;

    [XmlText]
    public string Value { get; set; } = string.Empty;
}
