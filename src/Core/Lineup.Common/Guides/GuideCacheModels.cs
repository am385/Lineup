using System.Text.Json;

namespace Lineup.Common.Guides;

/// <summary>
/// Describes one channel in the compatibility lineup cache.
/// </summary>
public sealed record TunerChannel
{
    /// <summary>Gets the channel number.</summary>
    public required string GuideNumber { get; init; }

    /// <summary>Gets the channel display name.</summary>
    public required string GuideName { get; init; }

    /// <summary>Gets the source video codec when known.</summary>
    public string? VideoCodec { get; init; }

    /// <summary>Gets the source audio codec when known.</summary>
    public string? AudioCodec { get; init; }

    /// <summary>Gets whether the source marks the channel as high definition.</summary>
    public bool HD { get; init; }

    /// <summary>Gets whether the source marks the channel as protected.</summary>
    public bool DRM { get; init; }

    /// <summary>Gets whether the source marks the channel as a favorite.</summary>
    public bool Favorite { get; init; }

    /// <summary>Gets provider tags.</summary>
    public string? Tags { get; init; }

    /// <summary>Gets signal strength when available.</summary>
    public int? SignalStrength { get; init; }

    /// <summary>Gets signal quality when available.</summary>
    public int? SignalQuality { get; init; }

    /// <summary>Gets the source stream URI.</summary>
    public required string URL { get; init; }

    /// <summary>Gets additional provider metadata.</summary>
    public IReadOnlyDictionary<string, JsonElement>? AdditionalProperties { get; init; }
}

/// <summary>
/// Describes one normalized program stored in the compatibility guide cache.
/// </summary>
public sealed record GuideCacheProgram
{
    /// <summary>Gets the program title.</summary>
    public string? Title { get; init; }

    /// <summary>Gets the episode title.</summary>
    public string? EpisodeTitle { get; init; }

    /// <summary>Gets the synopsis.</summary>
    public string? Synopsis { get; init; }

    /// <summary>Gets the Unix start timestamp.</summary>
    public long StartTime { get; init; }

    /// <summary>Gets the Unix end timestamp.</summary>
    public long EndTime { get; init; }

    /// <summary>Gets the primary image URL.</summary>
    public string? ImageURL { get; init; }

    /// <summary>Gets the poster URL.</summary>
    public string? PosterURL { get; init; }

    /// <summary>Gets the provider episode number.</summary>
    public string? EpisodeNumber { get; init; }

    /// <summary>Gets the Unix original-airdate timestamp.</summary>
    public long? OriginalAirdate { get; init; }

    /// <summary>Gets the provider first-airing marker.</summary>
    public int? First { get; init; }

    /// <summary>Gets the provider series identifier.</summary>
    public string? SeriesID { get; init; }

    /// <summary>Gets provider categories or filters.</summary>
    public List<string>? Filter { get; init; }

    /// <summary>Gets the channel number.</summary>
    public string? GuideNumber { get; set; }

    /// <summary>Gets supplemental XMLTV metadata not represented by typed properties.</summary>
    public string? SupplementalXml { get; init; }

    /// <summary>Gets structured XMLTV metadata.</summary>
    public XmltvProgramMetadata? Metadata { get; init; }
}

/// <summary>
/// Describes one normalized guide channel and its programs.
/// </summary>
public sealed record GuideChannelSegment
{
    /// <summary>Gets the stable provider channel identity.</summary>
    public string? ProviderKey { get; init; }

    /// <summary>Gets the channel number.</summary>
    public string? GuideNumber { get; init; }

    /// <summary>Gets the channel display name.</summary>
    public string? GuideName { get; init; }

    /// <summary>Gets the network affiliate.</summary>
    public string? Affiliate { get; init; }

    /// <summary>Gets the channel image URL.</summary>
    public string? ImageURL { get; init; }

    /// <summary>Gets whether the channel is protected.</summary>
    public bool DRM { get; init; }

    /// <summary>Gets whether the channel is a favorite.</summary>
    public bool Favorite { get; init; }

    /// <summary>Gets the channel programs.</summary>
    public List<GuideCacheProgram> Guide { get; init; } = [];

    /// <summary>Gets supplemental XMLTV metadata not represented by typed properties.</summary>
    public string? SupplementalXml { get; init; }
}
