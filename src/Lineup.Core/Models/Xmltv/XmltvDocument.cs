using System.Xml.Serialization;

namespace Lineup.Core.Models.Xmltv;

/// <summary>
/// Root element of an XMLTV document
/// </summary>
[XmlRoot("tv")]
public class XmltvDocument
{
    [XmlAttribute("source-info-name")]
    public string SourceInfoName { get; set; } = string.Empty;

    [XmlAttribute("generator-info-name")]
    public string GeneratorInfoName { get; set; } = string.Empty;

    [XmlElement("channel")]
    public List<XmltvChannel> Channels { get; set; } = [];

    [XmlElement("programme")]
    public List<XmltvProgramme> Programmes { get; set; } = [];
}
