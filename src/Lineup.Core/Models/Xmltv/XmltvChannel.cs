using System.Xml.Serialization;

namespace Lineup.Core.Models.Xmltv;

/// <summary>
/// Represents a channel in the XMLTV format
/// </summary>
public class XmltvChannel
{
    [XmlAttribute("id")]
    public string Id { get; set; } = string.Empty;

    [XmlElement("display-name")]
    public string DisplayName { get; set; } = string.Empty;

    [XmlElement("icon")]
    public XmltvIcon? Icon { get; set; }
}
