using System.ComponentModel.DataAnnotations;

namespace Lineup.Core.Storage.Entities;

/// <summary>
/// Database entity for storing program/show data
/// </summary>
public class StoredProgram
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Channel number this program belongs to
    /// </summary>
    [Required]
    [MaxLength(20)]
    public required string GuideNumber { get; set; }

    [MaxLength(500)]
    public string? Title { get; set; }

    [MaxLength(500)]
    public string? EpisodeTitle { get; set; }

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

    [MaxLength(500)]
    public string? ImageURL { get; set; }

    [MaxLength(500)]
    public string? PosterURL { get; set; }

    [MaxLength(50)]
    public string? EpisodeNumber { get; set; }

    public long? OriginalAirdate { get; set; }

    public int? First { get; set; }

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
}
