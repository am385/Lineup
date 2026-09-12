using System.Xml.Serialization;

namespace Lineup.Core.Models.Xmltv;

/// <summary>
/// Represents a TV programme in the XMLTV format
/// </summary>
public class XmltvProgramme
{
    /// <summary>
    /// Gets or sets start.
    /// </summary>
    [XmlAttribute("start")]
    public string Start { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets stop.
    /// </summary>
    [XmlAttribute("stop")]
    public string Stop { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets channel.
    /// </summary>
    [XmlAttribute("channel")]
    public string Channel { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets title.
    /// </summary>
    [XmlElement("title")]
    public XmltvText? Title { get; set; }

    /// <summary>
    /// Gets or sets sub title.
    /// </summary>
    [XmlElement("sub-title")]
    public XmltvText? SubTitle { get; set; }

    /// <summary>
    /// Gets or sets description.
    /// </summary>
    [XmlElement("desc")]
    public XmltvText? Description { get; set; }

    /// <summary>
    /// Gets or sets categories.
    /// </summary>
    [XmlElement("category")]
    public List<XmltvText> Categories { get; set; } = [];

    /// <summary>
    /// Gets or sets icon.
    /// </summary>
    [XmlElement("icon")]
    public XmltvIcon? Icon { get; set; }

    /// <summary>
    /// Gets or sets episode numbers.
    /// </summary>
    [XmlElement("episode-num")]
    public List<XmltvEpisodeNum> EpisodeNumbers { get; set; } = [];

    /// <summary>
    /// Gets or sets previously shown.
    /// </summary>
    [XmlElement("previously-shown")]
    public XmltvPreviouslyShown? PreviouslyShown { get; set; }

    /// <summary>
    /// Gets or sets new.
    /// </summary>
    [XmlElement("new")]
    public XmltvNew? New { get; set; }
}
