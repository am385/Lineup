using System.ComponentModel.DataAnnotations;

namespace Lineup.Core.Storage.Entities;

/// <summary>
/// Database entity for one logical channel discovered from the configured physical tuners.
/// </summary>
public sealed class StoredLineupChannel
{
    /// <summary>Gets or sets the database identifier.</summary>
    [Key]
    public int Id { get; set; }

    /// <summary>Gets or sets the normalized logical channel number.</summary>
    [Required]
    [MaxLength(20)]
    public required string GuideNumber { get; set; }

    /// <summary>Gets or sets the tuner-provided display name.</summary>
    [Required]
    [MaxLength(200)]
    public required string GuideName { get; set; }

    /// <summary>Gets or sets the tuner streaming URL.</summary>
    [Required]
    [MaxLength(2000)]
    public required string URL { get; set; }

    /// <summary>Gets or sets the reported video codec.</summary>
    [MaxLength(50)]
    public string? VideoCodec { get; set; }

    /// <summary>Gets or sets the reported audio codec.</summary>
    [MaxLength(50)]
    public string? AudioCodec { get; set; }

    /// <summary>Gets or sets whether the channel is high definition.</summary>
    public bool HD { get; set; }

    /// <summary>Gets or sets whether the channel is DRM protected.</summary>
    public bool DRM { get; set; }

    /// <summary>Gets or sets whether the tuner marks the channel as a favorite.</summary>
    public bool Favorite { get; set; }

    /// <summary>Gets or sets tuner-provided tags.</summary>
    [MaxLength(500)]
    public string? Tags { get; set; }

    /// <summary>Gets or sets the last reported signal strength.</summary>
    public int? SignalStrength { get; set; }

    /// <summary>Gets or sets the last reported signal quality.</summary>
    public int? SignalQuality { get; set; }

    /// <summary>Gets or sets unmodeled tuner properties serialized as JSON.</summary>
    public string? AdditionalPropertiesJson { get; set; }

    /// <summary>Gets or sets whether Lineup exposes this channel.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Gets or sets whether the channel appeared in the latest successful refresh.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Gets or sets when the channel was first discovered.</summary>
    public DateTime FirstSeenUtc { get; set; }

    /// <summary>Gets or sets when the channel was most recently discovered.</summary>
    public DateTime LastSeenUtc { get; set; }
}
