using System.Text.Json.Serialization;

namespace Lineup.HDHomeRun.Api.Models;

/// <summary>
/// Represents a segment of EPG data for a channel returned by the HDHomeRun API
/// Contains channel metadata and program listings for a specific time range
/// </summary>
public record HDHomeRunChannelEpgSegment
{
    /// <summary>
    /// Gets the virtual channel number.
    /// </summary>
    public string? GuideNumber { get; init; }

    /// <summary>
    /// Gets the channel display name.
    /// </summary>
    public string? GuideName { get; init; }

    /// <summary>
    /// Gets the channel affiliate.
    /// </summary>
    public string? Affiliate { get; init; }

    /// <summary>
    /// Gets the channel image URL.
    /// </summary>
    public string? ImageURL { get; init; }

    /// <summary>
    /// Gets whether the channel is marked as DRM-protected by the device lineup.
    /// </summary>
    public bool DRM { get; init; }

    /// <summary>
    /// Gets whether the channel is marked as a favorite by the device lineup.
    /// </summary>
    public bool Favorite { get; init; }

    /// <summary>
    /// Gets the channel's program listings.
    /// </summary>
    public List<HDHomeRunProgram> Guide { get; init; } = [];

    /// <summary>
    /// Gets supplemental XMLTV metadata not represented by typed properties.
    /// </summary>
    [JsonIgnore]
    public string? SupplementalXml { get; init; }
}
