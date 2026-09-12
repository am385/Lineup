using System.Xml.Serialization;

namespace Lineup.Core.Models.Xmltv;

/// <summary>
/// Root element of an XMLTV document
/// </summary>
[XmlRoot("tv")]
public class XmltvDocument
{
    /// <summary>
    /// Gets or sets source info name.
    /// </summary>
    [XmlAttribute("source-info-name")]
    public string SourceInfoName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets generator info name.
    /// </summary>
    [XmlAttribute("generator-info-name")]
    public string GeneratorInfoName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets channels.
    /// </summary>
    [XmlElement("channel")]
    public List<XmltvChannel> Channels { get; set; } = [];

    /// <summary>
    /// Gets or sets programmes.
    /// </summary>
    [XmlElement("programme")]
    public List<XmltvProgramme> Programmes { get; set; } = [];
}
