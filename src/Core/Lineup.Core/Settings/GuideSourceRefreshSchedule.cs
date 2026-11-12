namespace Lineup.Core.Settings;

/// <summary>Persists one guide source's automatic refresh eligibility independently of output publication.</summary>
public sealed record GuideSourceRefreshSchedule
{
    /// <summary>Gets the next permitted automatic attempt in UTC.</summary>
    public required DateTime NextRefreshUtc { get; init; }

    /// <summary>Gets the source-definition revision to which this schedule applies.</summary>
    public required string ConfigurationRevision { get; init; }
}
