using Lineup.Core.Common.Streaming;

namespace Lineup.Core.VirtualOutputs.Runtime;

/// <summary>Publishes safe desired-versus-applied virtual-output runtime status.</summary>
public interface IVirtualOutputRuntimeStatus
{
    /// <summary>Returns immutable snapshots ordered by virtual-device identifier.</summary>
    IReadOnlyList<VirtualOutputRuntimeSnapshot> GetSnapshots();

    /// <summary>Replaces one virtual-device runtime snapshot.</summary>
    void Set(VirtualOutputRuntimeSnapshot snapshot);

    /// <summary>Atomically updates one virtual-device runtime snapshot.</summary>
    void Update(VirtualDeviceId virtualDeviceId, Func<VirtualOutputRuntimeSnapshot?, VirtualOutputRuntimeSnapshot> update);

    /// <summary>Removes runtime status for one virtual device.</summary>
    bool Remove(VirtualDeviceId virtualDeviceId);
}

/// <summary>Thread-safe in-memory virtual-output runtime status registry.</summary>
public sealed class VirtualOutputRuntimeStatus : IVirtualOutputRuntimeStatus
{
    private readonly Lock _gate = new();
    private readonly Dictionary<VirtualDeviceId, VirtualOutputRuntimeSnapshot> _snapshots = [];

    /// <summary>Gets snapshots.</summary>
    /// <returns>The operation's result.</returns>
    public IReadOnlyList<VirtualOutputRuntimeSnapshot> GetSnapshots()
    {
        lock (_gate)
        {
            return _snapshots.Values.OrderBy(snapshot => snapshot.VirtualDeviceId.Value).ToArray();
        }
    }

    /// <summary>Sets the virtual output runtime status.</summary>
    /// <param name="snapshot">The snapshot value.</param>
    public void Set(VirtualOutputRuntimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            _snapshots.TryGetValue(snapshot.VirtualDeviceId, out var previous);
            if (previous?.LastRecoveryAttemptUtc is not null && snapshot.LastRecoveryAttemptUtc is null)
            {
                snapshot = snapshot with { PreviousFailure = previous.PreviousFailure, LastRecoveryAttemptUtc = previous.LastRecoveryAttemptUtc };
            }
            _snapshots[snapshot.VirtualDeviceId] = snapshot;
        }
    }

    /// <summary>Updates the virtual output runtime status.</summary>
    /// <param name="virtualDeviceId">The virtual device ID value.</param>
    /// <param name="update">The update value.</param>
    public void Update(VirtualDeviceId virtualDeviceId, Func<VirtualOutputRuntimeSnapshot?, VirtualOutputRuntimeSnapshot> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_gate)
        {
            _snapshots.TryGetValue(virtualDeviceId, out var current);
            _snapshots[virtualDeviceId] = update(current);
        }
    }

    /// <summary>Removes the virtual output runtime status.</summary>
    /// <param name="virtualDeviceId">The virtual device ID value.</param>
    /// <returns>The operation's result.</returns>
    public bool Remove(VirtualDeviceId virtualDeviceId)
    {
        lock (_gate)
        {
            return _snapshots.Remove(virtualDeviceId);
        }
    }
}
