using System.Xml.Serialization;

namespace Lineup.Core.Models.Xmltv;

/// <summary>
/// Represents an icon/image in XMLTV format
/// </summary>
public class XmltvIcon
{
    [XmlAttribute("src")]
    public string Source { get; set; } = string.Empty;
}
