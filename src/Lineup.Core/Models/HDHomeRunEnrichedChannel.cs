namespace Lineup.Core.Models;

/// <summary>
/// Represents enriched channel data combining local device info with remote EPG data.
/// Used internally for EPG processing after merging data from both sources.
/// </summary>
public record HDHomeRunEnrichedChannel
{
    /// <summary>
    /// Channel number (e.g., "2.1", "5.1")
    /// </summary>
    public required string GuideNumber { get; init; }

    /// <summary>
    /// Display name of the channel (e.g., "WFMY-HD", "PBS NC")
    /// </summary>
    public string? GuideName { get; init; }

    /// <summary>
    /// Network affiliate information (from remote API)
    /// </summary>
    public string? Affiliate { get; init; }

    /// <summary>
    /// URL to channel icon/logo image (from remote API)
    /// </summary>
    public string? ImageURL { get; init; }
}
