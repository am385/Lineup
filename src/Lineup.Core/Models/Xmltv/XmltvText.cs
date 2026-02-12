using System.Xml.Serialization;

namespace Lineup.Core.Models.Xmltv;

/// <summary>
/// Represents text content with language attribute in XMLTV format
/// </summary>
public class XmltvText
{
    [XmlAttribute("lang")]
    public string? Language { get; set; }

    [XmlText]
    public string Value { get; set; } = string.Empty;
}
