using System.ComponentModel.DataAnnotations;

namespace Lineup.Core.Storage.Entities;

/// <summary>
/// Database entity for storing program/show data
/// </summary>
public class StoredProgram
{
    /// <summary>
    /// Gets or sets id.
    /// </summary>
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Channel number this program belongs to
    /// </summary>
    [Required]
    [MaxLength(20)]
    public required string GuideNumber { get; set; }

    /// <summary>
    /// Gets or sets title.
    /// </summary>
    [MaxLength(500)]
    public string? Title { get; set; }

    /// <summary>
    /// Gets or sets episode title.
    /// </summary>
    [MaxLength(500)]
    public string? EpisodeTitle { get; set; }

    /// <summary>
    /// Gets or sets synopsis.
    /// </summary>
    [MaxLength(4000)]
    public string? Synopsis { get; set; }

    /// <summary>
    /// Unix timestamp for program start
    /// </summary>
    public long StartTime { get; set; }

    /// <summary>
    /// Unix timestamp for program end
    /// </summary>
    public long EndTime { get; set; }

    /// <summary>
    /// Gets or sets image url.
    /// </summary>
    [MaxLength(500)]
    public string? ImageURL { get; set; }

    /// <summary>
    /// Gets or sets poster url.
    /// </summary>
    [MaxLength(500)]
    public string? PosterURL { get; set; }

    /// <summary>
    /// Gets or sets episode number.
    /// </summary>
    [MaxLength(50)]
    public string? EpisodeNumber { get; set; }

    /// <summary>
    /// Gets or sets original airdate.
    /// </summary>
    public long? OriginalAirdate { get; set; }

    /// <summary>
    /// Gets or sets first.
    /// </summary>
    public int? First { get; set; }

    /// <summary>
    /// Gets or sets series id.
    /// </summary>
    [MaxLength(100)]
    public string? SeriesID { get; set; }

    /// <summary>
    /// Comma-separated filter values
    /// </summary>
    [MaxLength(500)]
    public string? Filter { get; set; }

    /// <summary>
    /// When this program data was fetched from the API
    /// </summary>
    public DateTime FetchedAtUtc { get; set; }

    /// <summary>
    /// Identifier of the most recent authoritative import that contained this programme.
    /// </summary>
    [MaxLength(32)]
    public string? LastSeenImportId { get; set; }
}
