namespace Lineup.Common.Guides;

/// <summary>
/// Represents a normalized XMLTV guide and metadata attached to its root element.
/// </summary>
public sealed record XmltvGuideSnapshot
{
    /// <summary>
    /// Gets the normalized channel segments.
    /// </summary>
    public required IReadOnlyList<GuideChannelSegment> Segments { get; init; }

    /// <summary>
    /// Gets supplemental root XMLTV metadata not represented by typed properties.
    /// </summary>
    public string? SupplementalXml { get; init; }
}
