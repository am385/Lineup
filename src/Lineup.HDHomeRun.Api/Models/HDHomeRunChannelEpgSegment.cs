namespace Lineup.HDHomeRun.Api.Models;

/// <summary>
/// Represents a segment of EPG data for a channel returned by the HDHomeRun API
/// Contains channel metadata and program listings for a specific time range
/// </summary>
public record HDHomeRunChannelEpgSegment
{
    public string? GuideNumber { get; init; }
    public string? GuideName { get; init; }
    public string? Affiliate { get; init; }
    public string? ImageURL { get; init; }
    public List<HDHomeRunProgram> Guide { get; init; } = [];
}
