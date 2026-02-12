namespace Lineup.HDHomeRun.Api.Models;

/// <summary>
/// Represents a program/show from the Lineup API
/// </summary>
public record HDHomeRunProgram
{
    public string? Title { get; init; }
    public string? EpisodeTitle { get; init; }
    public string? Synopsis { get; init; }
    public long StartTime { get; init; }
    public long EndTime { get; init; }
    public string? ImageURL { get; init; }
    public string? PosterURL { get; init; }
    public string? EpisodeNumber { get; init; }
    public long? OriginalAirdate { get; init; }
    public int? First { get; init; }
    public string? SeriesID { get; init; }
    public List<string>? Filter { get; init; }
    public string? GuideNumber { get; set; }
}
