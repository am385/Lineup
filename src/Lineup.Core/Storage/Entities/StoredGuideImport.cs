using System.ComponentModel.DataAnnotations;

namespace Lineup.Core.Storage.Entities;

/// <summary>
/// Records one committed authoritative XMLTV import.
/// </summary>
public sealed class StoredGuideImport
{
    /// <summary>Gets or sets the import identifier.</summary>
    [Key]
    [MaxLength(32)]
    public required string Id { get; set; }

    /// <summary>Gets or sets when the import started.</summary>
    public DateTime StartedUtc { get; set; }

    /// <summary>Gets or sets when the import committed.</summary>
    public DateTime? CompletedUtc { get; set; }

    /// <summary>Gets or sets the earliest programme start in the snapshot.</summary>
    public long CoverageStart { get; set; }

    /// <summary>Gets or sets the latest programme end in the snapshot.</summary>
    public long CoverageEnd { get; set; }

    /// <summary>Gets or sets the imported channel count.</summary>
    public int ChannelCount { get; set; }

    /// <summary>Gets or sets the imported programme count.</summary>
    public int ProgramCount { get; set; }

    /// <summary>Gets or sets supplemental XMLTV root metadata.</summary>
    public string? SupplementalXml { get; set; }
}
