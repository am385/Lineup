using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
}

internal sealed class PhysicalDeviceRuntimeState : IPhysicalDeviceResolver
{
    private CatalogSnapshot _snapshot = new(null, []);

    public IReadOnlyList<IPhysicalTunerDevice> Devices => Volatile.Read(ref _snapshot).Devices;

    /// <summary>Checks the definition key atomically published with the active runtimes.</summary>
    public bool IsConfigurationCurrent(string activationKey) =>
        string.Equals(Volatile.Read(ref _snapshot).ActivationKey, activationKey, StringComparison.Ordinal);

    public IPhysicalTunerDevice GetRequired(PhysicalDeviceId deviceId) =>
        Devices.FirstOrDefault(device => device.Snapshot.Id == deviceId)
        ?? throw new InvalidOperationException($"Physical device {deviceId} is not active.");

    /// <summary>Publishes one definition identity together with its active runtimes.</summary>
    public void Replace(string activationKey, IReadOnlyList<IPhysicalTunerDevice> devices)
    {
        Volatile.Write(ref _snapshot, new CatalogSnapshot(activationKey, devices));
    }

    private sealed record CatalogSnapshot(string? ActivationKey, IReadOnlyList<IPhysicalTunerDevice> Devices);
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
    private string? _activationKey;
    private Dictionary<PhysicalDeviceId, string> _deviceActivationKeys = [];

    /// <summary>Initializes a physical device catalog.</summary>
    public PhysicalDeviceCatalog(IStreamingAdapterRegistry adapters, ILogger<PhysicalDeviceCatalog>? logger = null)
        : this(adapters, new PhysicalDeviceRuntimeState(), logger)
    {
    }

    internal PhysicalDeviceCatalog(IStreamingAdapterRegistry adapters, PhysicalDeviceRuntimeState state, ILogger<PhysicalDeviceCatalog>? logger = null)
    {
        _adapters = adapters;
        _state = state;
        _logger = logger ?? NullLogger<PhysicalDeviceCatalog>.Instance;
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
    public async Task ApplyAsync(StreamingConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ValidateConfiguration(configuration);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var definitions = configuration.PhysicalDevices.Where(device => device.Enabled).OrderBy(device => device.Order).ThenBy(device => device.Id.Value).ToArray();
            var activationKey = CreateActivationKey(definitions);
            if (string.Equals(_activationKey, activationKey, StringComparison.Ordinal))
            {
                return;
            }

            var created = new List<IPhysicalTunerDevice>();
            var newlyCreated = new List<IPhysicalTunerDevice>();
            var previous = _state.Devices;
            var previousById = previous.ToDictionary(device => device.Snapshot.Id);
            var deviceActivationKeys = new Dictionary<PhysicalDeviceId, string>();
            var activationSucceeded = true;
            try
            {
                foreach (var definition in definitions)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var deviceKey = CreateActivationKey([definition]);
                    if (previousById.TryGetValue(definition.Id, out var existing) &&
                        _deviceActivationKeys.TryGetValue(definition.Id, out var previousKey) &&
                        string.Equals(previousKey, deviceKey, StringComparison.Ordinal))
                    {
                        created.Add(existing);
                        deviceActivationKeys.Add(definition.Id, deviceKey);
                        continue;
                    }
                    try
                    {
                        var driver = _adapters.GetPhysical(definition.Adapter.Kind);
                        var adapterErrors = driver.Validate(definition.Adapter);
                        if (adapterErrors.Count > 0)
                        {
                            throw new InvalidOperationException($"Physical device {definition.Name} is invalid: {string.Join(" ", adapterErrors)}");
                        }
                        var device = await driver.CreateAsync(definition.Id, definition.Name, definition.Adapter, cancellationToken);
                        newlyCreated.Add(device);
                        created.Add(device);
                        deviceActivationKeys.Add(definition.Id, deviceKey);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception) when (
                        exception is ArgumentException or InvalidOperationException or InvalidDataException or IOException or HttpRequestException or UnauthorizedAccessException)
                    {
                        activationSucceeded = false;
                        _logger.LogWarning(exception, "Unable to activate physical device {PhysicalDeviceId} ({PhysicalDeviceName}); other devices will continue", definition.Id, definition.Name);
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch
            {
                await DisposeDevicesAsync(newlyCreated);
                throw;
            }
            _deviceActivationKeys = deviceActivationKeys;
            _state.Replace(activationKey, created);
            _activationKey = activationSucceeded ? activationKey : null;
            var retained = created.ToHashSet<IPhysicalTunerDevice>(ReferenceEqualityComparer.Instance);
            await DisposeDevicesAsync(previous.Where(device => !retained.Contains(device)));
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
