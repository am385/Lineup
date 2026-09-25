using System.Text.Json.Serialization;

namespace Lineup.HDHomeRun.Api.Models;

/// <summary>
/// Represents a program/show from the Lineup API
/// </summary>
public record HDHomeRunProgram
{
    /// <summary>
    /// Gets or sets title.
    /// </summary>
    public string? Title { get; init; }
    /// <summary>
    /// Gets or sets episode title.
    /// </summary>
    public string? EpisodeTitle { get; init; }
    /// <summary>
    /// Gets or sets synopsis.
    /// </summary>
    public string? Synopsis { get; init; }
    /// <summary>
    /// Gets or sets start time.
    /// </summary>
    public long StartTime { get; init; }
    /// <summary>
    /// Gets or sets end time.
    /// </summary>
    public long EndTime { get; init; }
    /// <summary>
    /// Gets or sets image url.
    /// </summary>
    public string? ImageURL { get; init; }
    /// <summary>
    /// Gets or sets poster url.
    /// </summary>
    public string? PosterURL { get; init; }
    /// <summary>
    /// Gets or sets episode number.
    /// </summary>
    public string? EpisodeNumber { get; init; }
    /// <summary>
    /// Gets or sets original airdate.
    /// </summary>
    public long? OriginalAirdate { get; init; }
    /// <summary>
    /// Gets or sets first.
    /// </summary>
    public int? First { get; init; }
    /// <summary>
    /// Gets or sets series id.
    /// </summary>
    public string? SeriesID { get; init; }
    /// <summary>
    /// Gets or sets filter.
    /// </summary>
    public List<string>? Filter { get; init; }
    /// <summary>
    /// Gets or sets guide number.
    /// </summary>
    public string? GuideNumber { get; set; }

    /// <summary>
    /// Gets supplemental XMLTV metadata not represented by typed properties.
    /// </summary>
    [JsonIgnore]
    public string? SupplementalXml { get; init; }

    /// <summary>
    /// Gets structured official XMLTV metadata derived from <see cref="SupplementalXml"/>.
    /// </summary>
    [JsonIgnore]
    public XmltvProgrammeMetadata? Metadata { get; init; }
}
