using System.Xml.Serialization;

namespace Lineup.Core.Models.Xmltv;

/// <summary>
/// Represents text content with language attribute in XMLTV format
/// </summary>
public class XmltvText
{
    /// <summary>
    /// Gets or sets language.
    /// </summary>
    [XmlAttribute("lang")]
    public string? Language { get; set; }

    /// <summary>
    /// Gets or sets value.
    /// </summary>
    [XmlText]
    public string Value { get; set; } = string.Empty;
}
