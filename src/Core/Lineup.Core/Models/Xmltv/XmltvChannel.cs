using System.Xml.Serialization;

namespace Lineup.Core.Models.Xmltv;

/// <summary>
/// Represents a channel in the XMLTV format
/// </summary>
public class XmltvChannel
{
    /// <summary>
    /// Gets or sets id.
    /// </summary>
    [XmlAttribute("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets display name.
    /// </summary>
    [XmlElement("display-name")]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets icon.
    /// </summary>
    [XmlElement("icon")]
    public XmltvIcon? Icon { get; set; }
}
