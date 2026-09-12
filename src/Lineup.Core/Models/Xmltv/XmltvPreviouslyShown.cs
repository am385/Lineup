using System.Xml.Serialization;

namespace Lineup.Core.Models.Xmltv;

/// <summary>
/// Represents a previously-shown indicator in XMLTV format
/// </summary>
public class XmltvPreviouslyShown
{
    /// <summary>
    /// Gets or sets start.
    /// </summary>
    [XmlAttribute("start")]
    public string? Start { get; set; }
}
