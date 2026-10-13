using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;

namespace Lineup.Core.Guides;

/// <summary>
/// Describes the outcome of refreshing one configured guide source.
/// </summary>
public sealed record GuideRefreshResult
{
    /// <summary>Gets the configured source identity.</summary>
    public required GuideSourceId SourceId { get; init; }

    /// <summary>Gets whether a complete import committed successfully.</summary>
    public required bool Succeeded { get; init; }

    /// <summary>Gets the committed channel count.</summary>
    public int ChannelCount { get; init; }

    /// <summary>Gets the committed program count.</summary>
    public int ProgramCount { get; init; }

    /// <summary>Gets an actionable failure message without provider authorization.</summary>
    public string? Error { get; init; }

    /// <summary>Gets binding diagnostics after a successful import.</summary>
    public IReadOnlyList<GuideBindingDiagnostic> Bindings { get; init; } = [];
}

/// <summary>
/// Describes the binding state for one physical source channel.
/// </summary>
public enum GuideBindingState
{
    /// <summary>A unique eligible guide channel was selected automatically.</summary>
    Automatic,

    /// <summary>A persisted manual guide-channel selection is active.</summary>
    Manual,

    /// <summary>Multiple eligible candidates require an explicit selection.</summary>
    NeedsGuideSelection,

    /// <summary>No eligible candidate matches the source channel.</summary>
    Unmatched,

    /// <summary>The persisted manual selection is no longer available.</summary>
    ManualUnavailable
}

/// <summary>
/// Reports authoritative binding state and eligible candidates for one physical source channel.
/// </summary>
public sealed record GuideBindingDiagnostic
{
    /// <summary>Gets the physical source channel.</summary>
    public required SourceChannelId SourceChannelId { get; init; }

    /// <summary>Gets the current binding state.</summary>
    public required GuideBindingState State { get; init; }

    /// <summary>Gets the selected authoritative guide channel, when present.</summary>
    public GuideChannelId? BoundChannelId { get; init; }

    /// <summary>Gets candidates from sources subscribed to the physical device.</summary>
    public IReadOnlyList<GuideChannel> Candidates { get; init; } = [];
}

/// <summary>
/// Coordinates configured guide sources and virtual guide projections.
/// </summary>
public interface IGuideOrchestrator
{
    /// <summary>Creates all enabled optional guide sources from the adapter registry.</summary>
    Task ApplyAsync(StreamingConfiguration configuration, CancellationToken cancellationToken = default);

    /// <summary>Refreshes every active source independently and returns one result per source.</summary>
    Task<IReadOnlyList<GuideRefreshResult>> RefreshAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Builds one virtual guide filtered through its physical-device subscription.</summary>
    Task<VirtualGuideSnapshot> BuildSnapshotAsync(VirtualDeviceId virtualDeviceId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Queries provider-neutral guide projections independently of a presentation host.
/// </summary>
public interface IGuideQueryService
{
    /// <summary>Builds the union guide for every enabled physical device.</summary>
    Task<VirtualGuideSnapshot> BuildUnionSnapshotAsync(
        StreamingConfiguration configuration,
        DateTimeOffset? startUtc = null,
        DateTimeOffset? endUtc = null,
        CancellationToken cancellationToken = default);

    /// <summary>Builds the scoped projection for one configured guide output.</summary>
    Task<VirtualGuideSnapshot> BuildOutputSnapshotAsync(
        StreamingConfiguration configuration,
        GuideOutputDefinition output,
        DateTimeOffset? startUtc = null,
        DateTimeOffset? endUtc = null,
        CancellationToken cancellationToken = default);

    /// <summary>Gets statistics for the active union guide projection.</summary>
    Task<GuideStatistics> GetStatisticsAsync(StreamingConfiguration configuration, CancellationToken cancellationToken = default);
}

/// <summary>
/// Statistics for one active provider-neutral guide projection.
/// </summary>
public sealed record GuideStatistics(
    int ChannelCount,
    int ProgramCount,
    DateTimeOffset? EarliestProgramStartUtc,
    DateTimeOffset? LatestProgramEndUtc,
    DateTimeOffset? SafeCoverageEndUtc)
{
    /// <summary>Gets the total program time span.</summary>
    public TimeSpan? TotalTimeSpan => EarliestProgramStartUtc.HasValue && LatestProgramEndUtc.HasValue
        ? LatestProgramEndUtc.Value - EarliestProgramStartUtc.Value
        : null;
}
