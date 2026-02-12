using Lineup.HDHomeRun.Api.Models;

namespace Lineup.Core.Models;

/// <summary>
/// Container for aggregated EPG data from HDHomeRun.
/// Combines channel information enriched from both local device and remote API.
/// </summary>
public record HDHomeRunEpgData
{
    /// <summary>
    /// Enriched channel data combining local device info with remote EPG metadata
    /// </summary>
    public List<HDHomeRunEnrichedChannel> Channels { get; init; } = [];

    /// <summary>
    /// Program/show data from the remote EPG API
    /// </summary>
    public List<HDHomeRunProgram> Programs { get; init; } = [];
}
