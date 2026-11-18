namespace Lineup.Core.Common.Streaming;

/// <summary>One authenticated worker generation's cached observation, timed by the receiving management owner.</summary>
public sealed record WorkerResourceObservation(string OwnerId, DateTimeOffset ReceivedAtUtc, IReadOnlyList<ProcessResourceSample> Processes, bool IncompleteInterval = false);

/// <summary>Exchanges monitoring demand and bounded worker observations without coupling monitoring to worker health.</summary>
public static class WorkerResourceReports
{
    private static readonly object _gate = new();
    private static readonly Dictionary<string, WorkerResourceObservation> _observations = new(StringComparer.Ordinal);
    private static DateTimeOffset _demandUntil;

    /// <summary>Gets whether management currently requests optional process observations.</summary>
    public static bool IsRequested
    {
        get
        {
            lock (_gate)
            {
                return DateTimeOffset.UtcNow < _demandUntil;
            }
        }
    }

    /// <summary>Renews short-lived demand; owner failure cannot leave monitoring permanently enabled.</summary>
    public static void SetRequested(bool enabled)
    {
        lock (_gate)
        {
            _demandUntil = enabled ? DateTimeOffset.UtcNow.AddSeconds(6) : DateTimeOffset.MinValue;
            if (!enabled)
            {
                _observations.Clear();
            }
        }
    }

    /// <summary>Replaces one authenticated worker's current observation without retaining ended generations.</summary>
    public static void Update(string ownerId, IReadOnlyList<ProcessResourceSample> processes, bool incompleteInterval = false)
    {
        lock (_gate)
        {
            _observations[ownerId] = new(ownerId, DateTimeOffset.UtcNow, processes.ToArray(), incompleteInterval);
        }
    }

    /// <summary>Gets current observations after discarding reports outside the active owner inventory.</summary>
    public static IReadOnlyList<WorkerResourceObservation> Read(IReadOnlySet<string> owners)
    {
        lock (_gate)
        {
            foreach (var id in _observations.Keys.Where(id => !owners.Contains(id)).ToArray())
            {
                _observations.Remove(id);
            }
            return _observations.Values.ToArray();
        }
    }
}
