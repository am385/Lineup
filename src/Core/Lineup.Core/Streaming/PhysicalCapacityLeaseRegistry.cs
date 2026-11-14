namespace Lineup.Core.Streaming;

/// <summary>
/// Identifies one distinct physical tuner acquisition.
/// </summary>
/// <param name="ChannelId">Source channel.</param>
/// <param name="TuneKey">Normalized adapter tune parameters.</param>
public sealed record PhysicalAcquisitionKey(SourceChannelId ChannelId, string TuneKey)
{
    /// <summary>Gets the host runtime generation, preventing different adapter instances from sharing a capacity lease.</summary>
    internal Guid RuntimeId { get; init; }

    /// <summary>Creates a key with deterministic, ordinal tune-parameter ordering.</summary>
    public static PhysicalAcquisitionKey Create(PhysicalStreamRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tuneKey = string.Join(
            "&",
            request.TuneParameters
                .OrderBy(parameter => parameter.Key, StringComparer.Ordinal)
                .ThenBy(parameter => parameter.Value, StringComparer.Ordinal)
                .Select(parameter => $"{Uri.EscapeDataString(parameter.Key)}={Uri.EscapeDataString(parameter.Value)}"));
        return new PhysicalAcquisitionKey(request.ChannelId, tuneKey);
    }
}

/// <summary>
/// Owns one receiver's participation in a physical acquisition.
/// </summary>
public interface IPhysicalCapacityLease : IDisposable, IAsyncDisposable
{
    /// <summary>Gets the physical device.</summary>
    PhysicalDeviceId DeviceId { get; }

    /// <summary>Gets the shared acquisition.</summary>
    PhysicalAcquisitionKey AcquisitionKey { get; }
}

/// <summary>
/// Atomically enforces physical capacity while sharing identical acquisitions.
/// </summary>
public interface IPhysicalCapacityLeaseRegistry
{
    /// <summary>
    /// Acquires a receiver lease or returns <see langword="null"/> when finite capacity is exhausted.
    /// </summary>
    ValueTask<IPhysicalCapacityLease?> TryAcquireAsync(PhysicalDeviceId deviceId, PhysicalAcquisitionKey acquisitionKey, PhysicalTunerCapacity capacity, CancellationToken cancellationToken = default);

    /// <summary>Gets immutable capacity diagnostics.</summary>
    IReadOnlyList<PhysicalCapacityDiagnostics> GetDiagnostics();
}

/// <summary>
/// Immutable capacity diagnostics for one physical device.
/// </summary>
public sealed record PhysicalCapacityDiagnostics(PhysicalDeviceId DeviceId, int? Capacity, int ActiveAcquisitions, int Receivers);

/// <summary>
/// Thread-safe in-memory physical capacity registry.
/// </summary>
public sealed class PhysicalCapacityLeaseRegistry : IPhysicalCapacityLeaseRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<PhysicalDeviceId, DeviceState> _devices = [];

    /// <summary>Attempts to acquire asynchronously.</summary>
    /// <param name="deviceId">Identity of the target device.</param>
    /// <param name="acquisitionKey">The acquisition key value.</param>
    /// <param name="capacity">The capacity value.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The operation's result.</returns>
    public ValueTask<IPhysicalCapacityLease?> TryAcquireAsync(PhysicalDeviceId deviceId, PhysicalAcquisitionKey acquisitionKey, PhysicalTunerCapacity capacity, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (acquisitionKey.ChannelId.PhysicalDeviceId != deviceId)
        {
            throw new ArgumentException("The acquisition channel must belong to the requested physical device.", nameof(acquisitionKey));
        }

        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_devices.TryGetValue(deviceId, out var state))
            {
                state = new DeviceState();
                _devices.Add(deviceId, state);
            }
            state.Capacity = capacity.TunerCount;
            if (state.Acquisitions.TryGetValue(acquisitionKey, out var receivers))
            {
                state.Acquisitions[acquisitionKey] = receivers + 1;
                return ValueTask.FromResult<IPhysicalCapacityLease?>(new Lease(this, deviceId, acquisitionKey));
            }
            if (state.Capacity is int finite && state.Acquisitions.Count >= Math.Max(0, finite))
            {
                if (state.Acquisitions.Count == 0)
                {
                    _devices.Remove(deviceId);
                }
                return ValueTask.FromResult<IPhysicalCapacityLease?>(null);
            }

            state.Acquisitions.Add(acquisitionKey, 1);
            return ValueTask.FromResult<IPhysicalCapacityLease?>(new Lease(this, deviceId, acquisitionKey));
        }
    }

    /// <summary>Gets diagnostics.</summary>
    /// <returns>The operation's result.</returns>
    public IReadOnlyList<PhysicalCapacityDiagnostics> GetDiagnostics()
    {
        lock (_gate)
        {
            return _devices
                .OrderBy(entry => entry.Key.Value)
                .Select(entry => new PhysicalCapacityDiagnostics(entry.Key, entry.Value.Capacity, entry.Value.Acquisitions.Count, entry.Value.Acquisitions.Values.Sum()))
                .ToArray();
        }
    }

    private void Release(PhysicalDeviceId deviceId, PhysicalAcquisitionKey acquisitionKey)
    {
        lock (_gate)
        {
            if (!_devices.TryGetValue(deviceId, out var state) || !state.Acquisitions.TryGetValue(acquisitionKey, out var receivers))
            {
                return;
            }
            if (receivers > 1)
            {
                state.Acquisitions[acquisitionKey] = receivers - 1;
                return;
            }
            state.Acquisitions.Remove(acquisitionKey);
            if (state.Acquisitions.Count == 0)
            {
                _devices.Remove(deviceId);
            }
        }
    }

    private sealed class DeviceState
    {
        /// <summary>Gets or sets the capacity.</summary>
        public int? Capacity { get; set; }

        /// <summary>Gets the acquisitions.</summary>
        public Dictionary<PhysicalAcquisitionKey, int> Acquisitions { get; } = [];
    }

    private sealed class Lease : IPhysicalCapacityLease
    {
        private PhysicalCapacityLeaseRegistry? _owner;

        /// <summary>Initializes the lease.</summary>
        /// <param name="owner">The owner value.</param>
        /// <param name="deviceId">Identity of the target device.</param>
        /// <param name="acquisitionKey">The acquisition key value.</param>
        public Lease(PhysicalCapacityLeaseRegistry owner, PhysicalDeviceId deviceId, PhysicalAcquisitionKey acquisitionKey)
        {
            _owner = owner;
            DeviceId = deviceId;
            AcquisitionKey = acquisitionKey;
        }

        /// <summary>Gets the device ID.</summary>
        public PhysicalDeviceId DeviceId { get; }

        /// <summary>Gets the acquisition key.</summary>
        public PhysicalAcquisitionKey AcquisitionKey { get; }

        /// <inheritdoc />
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(DeviceId, AcquisitionKey);

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
