using Lineup.Core.Settings;

namespace Lineup.Core.Streaming;

/// <summary>Owns the single deployment-wide budget for admitted plugin TCP connections.</summary>
public interface IPluginConnectionBudget
{
    /// <summary>Acquires one connection slot, or returns null when the current limit is full.</summary>
    IAsyncDisposable? TryAcquire(VirtualDeviceId? workerDeviceId = null);

    /// <summary>Gets authoritative usage, including connections retained while a lower limit drains.</summary>
    PluginConnectionBudgetSnapshot GetSnapshot();
}

/// <summary>Describes authoritative deployment connection usage.</summary>
/// <param name="MaximumConnections">Configured limit, or zero for unlimited.</param>
/// <param name="ActiveConnections">Connections whose permits have not been released.</param>
/// <param name="ManagementHostedConnections">Admitted connections on the shared management listener.</param>
/// <param name="WorkerConnections">Admitted connections grouped by worker device.</param>
public sealed record PluginConnectionBudgetSnapshot(int MaximumConnections, int ActiveConnections, int ManagementHostedConnections, IReadOnlyDictionary<VirtualDeviceId, int> WorkerConnections)
{
    /// <summary>Gets whether existing connections exceed a newly reduced finite limit.</summary>
    public bool IsDraining => MaximumConnections > 0 && ActiveConnections > MaximumConnections;
}

/// <summary>Atomically admits connections while preserving permit ownership through configuration changes.</summary>
public sealed class PluginConnectionBudget(ILineupSettingsService settings) : IPluginConnectionBudget
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, VirtualDeviceId?> _connections = [];

    /// <summary>Attempts to acquire.</summary>
    /// <param name="workerDeviceId">The worker device ID value.</param>
    /// <returns>The operation's result.</returns>
    public IAsyncDisposable? TryAcquire(VirtualDeviceId? workerDeviceId = null)
    {
        lock (_gate)
        {
            var maximum = ReadMaximum();
            if (maximum > 0 && _connections.Count >= maximum)
            {
                return null;
            }
            var id = Guid.NewGuid();
            _connections.Add(id, workerDeviceId);
            return new ConnectionLease(this, id);
        }
    }

    /// <summary>Gets snapshot.</summary>
    /// <returns>The operation's result.</returns>
    public PluginConnectionBudgetSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            var workers = _connections.Values.Where(device => device.HasValue)
                .GroupBy(device => device!.Value).ToDictionary(group => group.Key, group => group.Count());
            return new PluginConnectionBudgetSnapshot(
                ReadMaximum(), _connections.Count, _connections.Values.Count(device => !device.HasValue), workers);
        }
    }

    private int ReadMaximum()
    {
        var maximum = settings.Settings.Streaming.MaximumPluginConnections;
        if (maximum < 0)
        {
            throw new InvalidOperationException("The deployment-wide plugin connection limit cannot be negative.");
        }
        return maximum;
    }

    private void Release(Guid id)
    {
        lock (_gate)
        {
            _connections.Remove(id);
        }
    }

    private sealed class ConnectionLease(PluginConnectionBudget owner, Guid id) : IAsyncDisposable
    {
        private int _disposed;

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Release(id);
            }
            return ValueTask.CompletedTask;
        }
    }
}
