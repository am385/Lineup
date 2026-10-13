using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lineup.Core.Streaming;

/// <summary>
/// Activates and resolves configured physical tuner runtime instances.
/// </summary>
public interface IPhysicalDeviceCatalog : IPhysicalDeviceResolver
{
    /// <summary>Atomically creates and activates enabled physical devices.</summary>
    Task ApplyAsync(StreamingConfiguration configuration, CancellationToken cancellationToken = default);
}

internal sealed class PhysicalDeviceRuntimeState : IPhysicalDeviceResolver
{
    private IReadOnlyList<IPhysicalTunerDevice> _devices = [];

    public IReadOnlyList<IPhysicalTunerDevice> Devices => Volatile.Read(ref _devices);

    public IPhysicalTunerDevice GetRequired(PhysicalDeviceId deviceId) =>
        Devices.FirstOrDefault(device => device.Snapshot.Id == deviceId)
        ?? throw new InvalidOperationException($"Physical device {deviceId} is not active.");

    public void Replace(IReadOnlyList<IPhysicalTunerDevice> devices)
    {
        Volatile.Write(ref _devices, devices);
    }
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
    public async Task ApplyAsync(StreamingConfiguration configuration, CancellationToken cancellationToken = default)
    {
        var errors = StreamingConfigurationValidator.Validate(configuration);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        }

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
            var activationSucceeded = true;
            try
            {
                foreach (var definition in definitions)
                {
                    try
                    {
                        var driver = _adapters.GetPhysical(definition.Adapter.Kind);
                        var adapterErrors = driver.Validate(definition.Adapter);
                        if (adapterErrors.Count > 0)
                        {
                            throw new InvalidOperationException($"Physical device {definition.Name} is invalid: {string.Join(" ", adapterErrors)}");
                        }
                        created.Add(await driver.CreateAsync(definition.Id, definition.Name, definition.Adapter, cancellationToken));
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or HttpRequestException or UnauthorizedAccessException)
                    {
                        activationSucceeded = false;
                        _logger.LogWarning(exception, "Unable to activate physical device {PhysicalDeviceId} ({PhysicalDeviceName}); other devices will continue", definition.Id, definition.Name);
                    }
                }
            }
            catch
            {
                await DisposeDevicesAsync(created);
                throw;
            }
            var previous = _state.Devices;
            _state.Replace(created);
            _activationKey = activationSucceeded ? activationKey : null;
            await DisposeDevicesAsync(previous);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string CreateActivationKey(IEnumerable<PhysicalDeviceDefinition> definitions) =>
        string.Join(
            '\n',
            definitions.Select(definition =>
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
