using System.ComponentModel.DataAnnotations;

namespace Lineup.Core.Storage.Entities;

/// <summary>
/// Database entity for the last successful import from one guide source.
/// </summary>
public sealed class StoredDeviceGuideImport
{
    /// <summary>Gets or sets the database identifier.</summary>
    [Key]
    public int Id { get; set; }

    /// <summary>Gets or sets the guide source identifier.</summary>
    [MaxLength(36)]
    public required string GuideSourceId { get; set; }

    /// <summary>Gets or sets when the source data was fetched.</summary>
    public DateTime FetchedUtc { get; set; }

    /// <summary>Gets or sets when the import committed.</summary>
    public DateTime CommittedUtc { get; set; }

    /// <summary>Gets or sets the earliest program start as a Unix timestamp.</summary>
    public long CoverageStart { get; set; }

    /// <summary>Gets or sets the latest program end as a Unix timestamp.</summary>
    public long CoverageEnd { get; set; }

    /// <summary>Gets or sets the imported channel count.</summary>
    public int ChannelCount { get; set; }

    /// <summary>Gets or sets the imported program count.</summary>
    public int ProgramCount { get; set; }

    /// <summary>Gets or sets supplemental XMLTV root metadata.</summary>
    public string? SupplementalXml { get; set; }
}

/// <summary>
/// Database entity for one provider-neutral physical source channel.
/// </summary>
public sealed class StoredPhysicalSourceChannel
{
    /// <summary>Gets or sets the database identifier.</summary>
    [Key]
    public int Id { get; set; }

    /// <summary>Gets or sets the owning physical device identifier.</summary>
    [MaxLength(36)]
    public required string PhysicalDeviceId { get; set; }

    /// <summary>Gets or sets the stable adapter-defined channel key.</summary>
    [MaxLength(500)]
    public required string ProviderKey { get; set; }

    /// <summary>Gets or sets the immutable provider number.</summary>
    [MaxLength(100)]
    public required string ProviderNumber { get; set; }

    /// <summary>Gets or sets the immutable provider name.</summary>
    [MaxLength(300)]
    public required string ProviderName { get; set; }

    /// <summary>Gets or sets the global number override.</summary>
    [MaxLength(100)]
    public string? NumberOverride { get; set; }

    /// <summary>Gets or sets the global name override.</summary>
    [MaxLength(300)]
    public string? NameOverride { get; set; }

    /// <summary>Gets or sets an optional equivalence group.</summary>
    [MaxLength(36)]
    public string? EquivalenceGroupId { get; set; }

    /// <summary>Gets or sets whether the channel is enabled for routing and publication.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Gets or sets adapter-neutral provider metadata serialized as JSON.</summary>
    public string? MetadataJson { get; set; }

    /// <summary>Gets or sets whether the channel appeared in the latest successful device refresh.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Gets or sets when the channel was first observed.</summary>
    public DateTime FirstSeenUtc { get; set; }

    /// <summary>Gets or sets when the channel was most recently observed.</summary>
    public DateTime LastSeenUtc { get; set; }
}

/// <summary>
/// Database entity for one channel returned by a guide source.
/// </summary>
public sealed class StoredGuideSourceChannel
{
    /// <summary>Gets or sets the database identifier.</summary>
    [Key]
    public int Id { get; set; }

    /// <summary>Gets or sets the guide source identifier.</summary>
    [MaxLength(36)]
    public required string GuideSourceId { get; set; }

    /// <summary>Gets or sets the stable provider guide-channel key.</summary>
    [MaxLength(500)]
    public required string ProviderKey { get; set; }

    /// <summary>Gets or sets the provider guide number.</summary>
    [MaxLength(100)]
    public required string Number { get; set; }

    /// <summary>Gets or sets the provider guide name.</summary>
    [MaxLength(300)]
    public required string Name { get; set; }

    /// <summary>Gets or sets the network affiliate.</summary>
    [MaxLength(300)]
    public string? Affiliate { get; set; }

    /// <summary>Gets or sets the primary channel image URL.</summary>
    public string? ImageUrl { get; set; }

    /// <summary>Gets or sets whether the guide source marks the channel as protected.</summary>
    public bool IsProtected { get; set; }

    /// <summary>Gets or sets whether the guide source marks the channel as a favorite.</summary>
    public bool IsFavorite { get; set; }

    /// <summary>Gets or sets supplemental XMLTV channel metadata.</summary>
    public string? SupplementalXml { get; set; }

    /// <summary>Gets or sets whether the channel appeared in the latest successful guide refresh.</summary>
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Database entity selecting one authoritative guide channel for a physical source channel.
/// </summary>
public sealed class StoredSourceGuideBinding
{
    /// <summary>Gets or sets the database identifier.</summary>
    [Key]
    public int Id { get; set; }

    /// <summary>Gets or sets the physical source channel database identifier.</summary>
    public int PhysicalSourceChannelId { get; set; }

    /// <summary>Gets or sets the authoritative guide channel database identifier.</summary>
    public int GuideSourceChannelId { get; set; }
}

/// <summary>
/// Database entity for one normalized program from a guide source.
/// </summary>
public sealed class StoredGuideSourceProgram
{
    /// <summary>Gets or sets the database identifier.</summary>
    [Key]
    public long Id { get; set; }

    /// <summary>Gets or sets the owning guide channel database identifier.</summary>
    public int GuideSourceChannelId { get; set; }

    /// <summary>Gets or sets the program start as a Unix timestamp.</summary>
    public long StartTime { get; set; }

    /// <summary>Gets or sets the program end as a Unix timestamp.</summary>
    public long EndTime { get; set; }

    /// <summary>Gets or sets the program title.</summary>
    [MaxLength(500)]
    public required string Title { get; set; }

    /// <summary>Gets or sets the optional synopsis.</summary>
    public string? Synopsis { get; set; }

    /// <summary>Gets or sets the optional episode title.</summary>
    public string? EpisodeTitle { get; set; }

    /// <summary>Gets or sets the primary program image URL.</summary>
    public string? ImageUrl { get; set; }

    /// <summary>Gets or sets the optional poster image URL.</summary>
    public string? PosterUrl { get; set; }

    /// <summary>Gets or sets the preferred display episode number.</summary>
    public string? EpisodeNumber { get; set; }

    /// <summary>Gets or sets the original air date as a Unix timestamp.</summary>
    public long? OriginalAirdate { get; set; }

    /// <summary>Gets or sets the source first-airing marker.</summary>
    public bool? IsFirstAiring { get; set; }

    /// <summary>Gets or sets the provider series identifier.</summary>
    public string? SeriesId { get; set; }

    /// <summary>Gets or sets categories serialized as JSON.</summary>
    public string? CategoriesJson { get; set; }

    /// <summary>Gets or sets structured XMLTV metadata serialized as JSON.</summary>
    public string? MetadataJson { get; set; }

    /// <summary>Gets or sets supplemental XMLTV program metadata.</summary>
    public string? SupplementalXml { get; set; }
}
