using Lineup.Common.Streaming;

namespace Lineup.VirtualOutputs.Runtime;

/// <summary>Publishes safe desired-versus-applied virtual-output runtime status.</summary>
public interface IVirtualOutputRuntimeStatus
{
    /// <summary>Returns immutable snapshots ordered by virtual-device identifier.</summary>
    IReadOnlyList<VirtualOutputRuntimeSnapshot> GetSnapshots();

    /// <summary>Replaces one virtual-device runtime snapshot.</summary>
    void Set(VirtualOutputRuntimeSnapshot snapshot);

    /// <summary>Atomically updates one virtual-device runtime snapshot.</summary>
    void Update(
        VirtualDeviceId virtualDeviceId,
        Func<VirtualOutputRuntimeSnapshot?, VirtualOutputRuntimeSnapshot> update);

    /// <summary>Removes runtime status for one virtual device.</summary>
    bool Remove(VirtualDeviceId virtualDeviceId);
}

/// <summary>Thread-safe in-memory virtual-output runtime status registry.</summary>
public sealed class VirtualOutputRuntimeStatus : IVirtualOutputRuntimeStatus
{
    private readonly Lock _gate = new();
    private readonly Dictionary<VirtualDeviceId, VirtualOutputRuntimeSnapshot> _snapshots = [];

    /// <inheritdoc />
    public IReadOnlyList<VirtualOutputRuntimeSnapshot> GetSnapshots()
    {
        lock (_gate)
        {
            return _snapshots.Values.OrderBy(snapshot => snapshot.VirtualDeviceId.Value).ToArray();
        }
    }

    /// <inheritdoc />
    public void Set(VirtualOutputRuntimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            _snapshots[snapshot.VirtualDeviceId] = snapshot;
        }
    }

    /// <inheritdoc />
    public void Update(
        VirtualDeviceId virtualDeviceId,
        Func<VirtualOutputRuntimeSnapshot?, VirtualOutputRuntimeSnapshot> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_gate)
        {
            _snapshots.TryGetValue(virtualDeviceId, out var current);
            _snapshots[virtualDeviceId] = update(current);
        }
    }

    /// <inheritdoc />
    public bool Remove(VirtualDeviceId virtualDeviceId)
    {
        lock (_gate)
        {
            return _snapshots.Remove(virtualDeviceId);
        }
    }
}
