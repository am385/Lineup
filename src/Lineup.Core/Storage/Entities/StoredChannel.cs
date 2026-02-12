using System.ComponentModel.DataAnnotations;

namespace Lineup.Core.Storage.Entities;

/// <summary>
/// Database entity for storing raw channel data from the API
/// </summary>
public class StoredChannel
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Channel number (e.g., "2.1", "5.1")
    /// </summary>
    [Required]
    [MaxLength(20)]
    public required string GuideNumber { get; set; }

    /// <summary>
    /// Display name of the channel from the API
    /// </summary>
    [MaxLength(200)]
    public string? GuideName { get; set; }

    /// <summary>
    /// Network affiliate information from the API
    /// </summary>
    [MaxLength(100)]
    public string? Affiliate { get; set; }

    /// <summary>
    /// URL to channel icon/logo image from the API
    /// </summary>
    [MaxLength(500)]
    public string? ImageURL { get; set; }

    /// <summary>
    /// When this channel data was last updated
    /// </summary>
    public DateTime LastUpdatedUtc { get; set; }
}
