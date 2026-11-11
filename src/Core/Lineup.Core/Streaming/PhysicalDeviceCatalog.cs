using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Lineup.Core.Settings;

namespace Lineup.Core.Streaming;

/// <summary>
/// Activates and resolves configured physical tuner runtime instances.
/// </summary>
public interface IPhysicalDeviceCatalog : IPhysicalDeviceResolver
{
    /// <summary>Validates topology and returns whether the last published catalog, including partial activation, belongs to these enabled physical definitions.</summary>
    bool IsConfigurationCurrent(StreamingConfiguration configuration);

    /// <summary>Atomically creates and activates enabled physical devices.</summary>
    Task ApplyAsync(StreamingConfiguration configuration, CancellationToken cancellationToken = default);

    /// <summary>Publishes a refresh only while the matching topology generation is still current.</summary>
    Task<bool> TryPublishAsync(StreamingConfiguration configuration, Func<CancellationToken, Task> publish, CancellationToken cancellationToken = default);
}

internal sealed class PhysicalDeviceRuntimeState : IPhysicalDeviceResolver
{
    private CatalogSnapshot _snapshot = new(null, [], new HashSet<IPhysicalTunerDevice>(ReferenceEqualityComparer.Instance));

    /// <summary>Occurs after old admissions close and before replacement runtimes are published.</summary>
    internal event Func<IReadOnlyList<IPhysicalTunerDevice>, Task>? Retiring;

    public IReadOnlyList<IPhysicalTunerDevice> Devices => Volatile.Read(ref _snapshot).Devices;

    /// <summary>Checks the definition key atomically published with the active runtimes.</summary>
    public bool IsConfigurationCurrent(string activationKey) =>
        string.Equals(Volatile.Read(ref _snapshot).ActivationKey, activationKey, StringComparison.Ordinal);

    public IPhysicalTunerDevice GetRequired(PhysicalDeviceId deviceId) =>
        Devices.FirstOrDefault(device => device.Snapshot.Id == deviceId)
        ?? throw new InvalidOperationException($"Physical device {deviceId} is not active.");

    /// <summary>Checks that a resolved runtime is still published and accepting consumers.</summary>
    internal bool IsCurrent(IPhysicalTunerDevice runtime)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        return !snapshot.Retiring.Contains(runtime) && snapshot.Devices.Any(device => ReferenceEquals(device, runtime));
    }

    /// <summary>Closes admission before waiting for graph-owned resources to finish cleanup.</summary>
    internal async Task RetireAsync(IReadOnlyList<IPhysicalTunerDevice> devices)
    {
        if (devices.Count == 0)
        {
            return;
        }
        var snapshot = Volatile.Read(ref _snapshot);
        Volatile.Write(ref _snapshot, snapshot with
        {
            ActivationKey = null,
            Retiring = new HashSet<IPhysicalTunerDevice>(devices, ReferenceEqualityComparer.Instance)
        });
        if (Retiring is { } retiring)
        {
            foreach (Func<IReadOnlyList<IPhysicalTunerDevice>, Task> handler in retiring.GetInvocationList())
            {
                await handler(devices);
            }
        }
    }

    /// <summary>Publishes one definition identity together with its active runtimes.</summary>
    public void Replace(string activationKey, IReadOnlyList<IPhysicalTunerDevice> devices)
    {
        Volatile.Write(ref _snapshot, new CatalogSnapshot(activationKey, devices, new HashSet<IPhysicalTunerDevice>(ReferenceEqualityComparer.Instance)));
    }

    private sealed record CatalogSnapshot(string? ActivationKey, IReadOnlyList<IPhysicalTunerDevice> Devices, IReadOnlySet<IPhysicalTunerDevice> Retiring);
}

/// <summary>
/// Host-neutral physical device catalog.
/// </summary>
public sealed class PhysicalDeviceCatalog : IPhysicalDeviceCatalog
{
    private readonly IStreamingAdapterRegistry _adapters;
    private readonly PhysicalDeviceRuntimeState _state;
    private readonly ILogger<PhysicalDeviceCatalog> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _publicationGate = new(1, 1);
    private readonly PhysicalDeviceRefreshScheduler _refresh;
    private readonly CoalescedRefresh<int> _applications;
    private string? _requestedKey;
    private string? _activationKey;
    private Dictionary<PhysicalDeviceId, string> _deviceActivationKeys = [];

    /// <summary>Initializes a physical device catalog.</summary>
    public PhysicalDeviceCatalog(IStreamingAdapterRegistry adapters, ILogger<PhysicalDeviceCatalog>? logger = null, PhysicalDeviceRefreshScheduler? refresh = null)
        : this(adapters, new PhysicalDeviceRuntimeState(), logger, refresh)
    {
    }

    /// <summary>Initializes a catalog with shared graph runtime state and host-wide refresh admission.</summary>
    internal PhysicalDeviceCatalog(IStreamingAdapterRegistry adapters, PhysicalDeviceRuntimeState state, ILogger<PhysicalDeviceCatalog>? logger = null, PhysicalDeviceRefreshScheduler? refresh = null)
    {
        _adapters = adapters;
        _state = state;
        _logger = logger ?? NullLogger<PhysicalDeviceCatalog>.Instance;
        _refresh = refresh ?? new PhysicalDeviceRefreshScheduler();
        _applications = new CoalescedRefresh<int>(_logger);
    }

    /// <inheritdoc />
    public IReadOnlyList<IPhysicalTunerDevice> Devices => _state.Devices;

    /// <inheritdoc />
    public IPhysicalTunerDevice GetRequired(PhysicalDeviceId deviceId) => _state.GetRequired(deviceId);

    /// <inheritdoc />
    public bool IsConfigurationCurrent(StreamingConfiguration configuration)
    {
        ValidateConfiguration(configuration);
        return _state.IsConfigurationCurrent(CreateActivationKey(configuration.PhysicalDevices));
    }

    /// <inheritdoc />
    public Task ApplyAsync(StreamingConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ValidateConfiguration(configuration);
        cancellationToken.ThrowIfCancellationRequested();
        var key = ConfigurationRevision.Create(configuration.PhysicalDevices);
        Volatile.Write(ref _requestedKey, key);
        return _applications.RunAsync(key, token => ApplyCoreAsync(configuration, token), cancellationToken, waitForCancellationCleanup: true);
    }

    /// <inheritdoc />
    public async Task<bool> TryPublishAsync(StreamingConfiguration configuration, Func<CancellationToken, Task> publish, CancellationToken cancellationToken = default)
    {
        await _publicationGate.WaitAsync(cancellationToken);
        try
        {
            var key = CreateActivationKey(configuration.PhysicalDevices);
            if (!string.Equals(Volatile.Read(ref _requestedKey), ConfigurationRevision.Create(configuration.PhysicalDevices), StringComparison.Ordinal) || !_state.IsConfigurationCurrent(key))
            {
                return false;
            }
            await publish(cancellationToken);
            return true;
        }
        finally
        {
            _publicationGate.Release();
        }
    }

    private async Task<int> ApplyCoreAsync(StreamingConfiguration configuration, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!string.Equals(Volatile.Read(ref _requestedKey), ConfigurationRevision.Create(configuration.PhysicalDevices), StringComparison.Ordinal))
            {
                return 0;
            }
            var definitions = configuration.PhysicalDevices.Where(device => device.Enabled).OrderBy(device => device.Order).ThenBy(device => device.Id.Value).ToArray();
            var activationKey = CreateActivationKey(definitions);
            if (string.Equals(_activationKey, activationKey, StringComparison.Ordinal))
            {
                return 0;
            }

            var created = new IPhysicalTunerDevice?[definitions.Length];
            var newlyCreated = new System.Collections.Concurrent.ConcurrentBag<IPhysicalTunerDevice>();
            var previous = _state.Devices;
            var previousById = previous.ToDictionary(device => device.Snapshot.Id);
            var deviceActivationKeys = new System.Collections.Concurrent.ConcurrentDictionary<PhysicalDeviceId, string>();
            var activationFailures = 0;
            try
            {
                await Parallel.ForEachAsync(Enumerable.Range(0, definitions.Length),
                    new ParallelOptions { MaxDegreeOfParallelism = _refresh.MaximumConcurrentOperations, CancellationToken = cancellationToken }, async (index, token) =>
                {
                    var definition = definitions[index];
                    token.ThrowIfCancellationRequested();
                    var deviceKey = $"{definition.Id.Value:D}|{definition.Adapter.Kind.Length}:{definition.Adapter.Kind}|{definition.Adapter.Configuration.GetRawText()}";
                    if (previousById.TryGetValue(definition.Id, out var existing) &&
                        _state.IsCurrent(existing) &&
                        _deviceActivationKeys.TryGetValue(definition.Id, out var previousKey) &&
                        string.Equals(previousKey, deviceKey, StringComparison.Ordinal))
                    {
                        created[index] = existing;
                        deviceActivationKeys[definition.Id] = deviceKey;
                        return;
                    }
                    try
                    {
                        var driver = _adapters.GetPhysical(definition.Adapter.Kind);
                        var adapterErrors = driver.Validate(definition.Adapter);
                        if (adapterErrors.Count > 0)
                        {
                            throw new InvalidOperationException($"Physical device {definition.Name} is invalid: {string.Join(" ", adapterErrors)}");
                        }
                        var device = await _refresh.RunAsync(ct => driver.CreateAsync(definition.Id, definition.Name, definition.Adapter, ct).AsTask(), token, device => DisposeDevicesAsync([device]));
                        newlyCreated.Add(device);
                        created[index] = device;
                        deviceActivationKeys[definition.Id] = deviceKey;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception) when (
                        exception is ArgumentException or InvalidOperationException or InvalidDataException or IOException or HttpRequestException or UnauthorizedAccessException or TimeoutException)
                    {
                        Interlocked.Increment(ref activationFailures);
                        _logger.LogWarning(exception, "Unable to activate physical device {PhysicalDeviceId} ({PhysicalDeviceName}); other devices will continue", definition.Id, definition.Name);
                    }
                });
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch
            {
                await DisposeDevicesAsync(newlyCreated);
                throw;
            }
            var activated = created.OfType<IPhysicalTunerDevice>().ToArray();
            var retained = activated.ToHashSet<IPhysicalTunerDevice>(ReferenceEqualityComparer.Instance);
            var retired = previous.Where(device => !retained.Contains(device)).ToArray();
            try
            {
                await _publicationGate.WaitAsync(cancellationToken);
            }
            catch
            {
                await DisposeDevicesAsync(newlyCreated);
                throw;
            }
            try
            {
                try
                {
                    if (!string.Equals(Volatile.Read(ref _requestedKey), ConfigurationRevision.Create(configuration.PhysicalDevices), StringComparison.Ordinal))
                    {
                        await DisposeDevicesAsync(newlyCreated);
                        return 0;
                    }
                    await _state.RetireAsync(retired);
                }
                catch
                {
                    _activationKey = null;
                    await DisposeDevicesAsync(newlyCreated);
                    throw;
                }
                _deviceActivationKeys = new Dictionary<PhysicalDeviceId, string>(deviceActivationKeys);
                _state.Replace(activationKey, activated);
                _activationKey = activationFailures == 0 ? activationKey : null;
            }
            finally
            {
                _publicationGate.Release();
            }
            await DisposeDevicesAsync(retired);
            return 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void ValidateConfiguration(StreamingConfiguration configuration)
    {
        var errors = StreamingConfigurationValidator.Validate(configuration);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        }
    }

    private static string CreateActivationKey(IEnumerable<PhysicalDeviceDefinition> definitions) =>
        string.Join(
            '\n',
            definitions.Where(definition => definition.Enabled).OrderBy(definition => definition.Order).ThenBy(definition => definition.Id.Value).Select(definition =>
                $"{definition.Id.Value:D}|{definition.Order}|{definition.Name.Length}:{definition.Name}|{definition.Adapter.Kind.Length}:{definition.Adapter.Kind}|{definition.Adapter.Configuration.GetRawText()}"));

    private static async ValueTask DisposeDevicesAsync(IEnumerable<IPhysicalTunerDevice> devices)
    {
        foreach (var device in devices)
        {
            if (device is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
            else if (device is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
