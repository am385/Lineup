using System.Xml.Serialization;

namespace Lineup.Core.Models.Xmltv;

/// <summary>
/// Represents an episode number in XMLTV format
/// </summary>
public class XmltvEpisodeNum
{
    /// <summary>
    /// Gets or sets system.
    /// </summary>
    [XmlAttribute("system")]
    public string System { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets value.
    /// </summary>
    [XmlText]
    public string Value { get; set; } = string.Empty;
}
