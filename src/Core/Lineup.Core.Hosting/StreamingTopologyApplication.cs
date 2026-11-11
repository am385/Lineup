namespace Lineup.Core.Hosting;

/// <summary>Identifies the current desired topology's application phase.</summary>
public enum TopologyApplicationPhase
{
    /// <summary>The desired configuration has not finished applying.</summary>
    Pending,

    /// <summary>The runtime is applying the desired configuration.</summary>
    Applying,

    /// <summary>All requested physical and guide operations succeeded.</summary>
    Applied,

    /// <summary>At least one requested operation failed or produced partial output.</summary>
    Degraded
}

/// <summary>Retains revision-correlated topology outcomes for the current process lifetime.</summary>
public sealed record StreamingTopologyApplication
{
    /// <summary>Gets the revision currently requested by saved settings.</summary>
    public required string DesiredRevision { get; init; }

    /// <summary>Gets the revision associated with the most recent application attempt.</summary>
    public string? AttemptedRevision { get; init; }

    /// <summary>Gets the most recent completely successful revision, if any.</summary>
    public string? AppliedRevision { get; init; }

    /// <summary>Gets the current desired revision's application phase.</summary>
    public TopologyApplicationPhase Phase { get; init; }

    /// <summary>Gets physical-device outcomes for the attempted revision.</summary>
    public IReadOnlyList<PhysicalLineupRefreshResult> Devices { get; init; } = [];

    /// <summary>Gets guide-output outcomes for the attempted revision.</summary>
    public IReadOnlyList<GuideOutputPublicationResult> Outputs { get; init; } = [];
}

/// <summary>Exposes topology application independently of configuration persistence.</summary>
public interface IStreamingTopologyRuntime
{
    /// <summary>Gets the latest immutable application snapshot.</summary>
    StreamingTopologyApplication Application { get; }

    /// <summary>Occurs when desired configuration or its application outcome changes.</summary>
    event Action? ApplicationChanged;

    /// <summary>Requests another attempt of the current desired configuration without rewriting settings.</summary>
    void Retry();

    /// <summary>Waits for an application outcome, rejecting a superseded desired revision.</summary>
    Task<StreamingTopologyApplication> WaitForApplicationAsync(string revision, CancellationToken cancellationToken = default);
}
