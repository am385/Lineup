using System.Xml.Serialization;

namespace Lineup.Core.Models.Xmltv;

/// <summary>
/// Represents a TV programme in the XMLTV format
/// </summary>
public class XmltvProgramme
{
    [XmlAttribute("start")]
    public string Start { get; set; } = string.Empty;

    [XmlAttribute("stop")]
    public string Stop { get; set; } = string.Empty;

    [XmlAttribute("channel")]
    public string Channel { get; set; } = string.Empty;

    [XmlElement("title")]
    public XmltvText? Title { get; set; }

    [XmlElement("sub-title")]
    public XmltvText? SubTitle { get; set; }

    [XmlElement("desc")]
    public XmltvText? Description { get; set; }

    [XmlElement("category")]
    public List<XmltvText> Categories { get; set; } = [];

    [XmlElement("icon")]
    public XmltvIcon? Icon { get; set; }

    [XmlElement("episode-num")]
    public List<XmltvEpisodeNum> EpisodeNumbers { get; set; } = [];

    [XmlElement("previously-shown")]
    public XmltvPreviouslyShown? PreviouslyShown { get; set; }

    [XmlElement("new")]
    public XmltvNew? New { get; set; }
}
