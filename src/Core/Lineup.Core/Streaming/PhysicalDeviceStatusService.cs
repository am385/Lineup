using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lineup.Core.Streaming;

/// <summary>
/// Maintains catalog-wide physical-device status and optional control operations.
/// </summary>
public interface IPhysicalDeviceStatusService
{
    /// <summary>Gets the latest state for every configured enabled physical device.</summary>
    IReadOnlyList<PhysicalDeviceRuntimeStatus> Devices { get; }

    /// <summary>Occurs when cached device status changes.</summary>
    event Action? StateChanged;

    /// <summary>Applies configuration and refreshes every enabled physical device.</summary>
    Task RefreshAsync(StreamingConfiguration configuration, CancellationToken cancellationToken = default);

    /// <summary>Applies configuration and refreshes descriptive information for every enabled physical device.</summary>
    Task RefreshDeviceInformationAsync(StreamingConfiguration configuration, CancellationToken cancellationToken = default);

    /// <summary>Stops one receiver when the owning adapter supports control.</summary>
    Task StopReceiverAsync(PhysicalDeviceId deviceId, int receiverIndex, CancellationToken cancellationToken = default);

    /// <summary>Restarts one device when the owning adapter supports restart.</summary>
    Task RestartAsync(PhysicalDeviceId deviceId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Cached runtime status and capability flags for one physical device.
/// </summary>
public sealed record PhysicalDeviceRuntimeStatus
{
    /// <summary>Gets configured device metadata.</summary>
    public required PhysicalDeviceSnapshot Device { get; init; }

    /// <summary>Gets the most recent successful status snapshot.</summary>
    public PhysicalDeviceStatusSnapshot? Status { get; init; }

    /// <summary>Gets a safe status-refresh failure message.</summary>
    public string? Error { get; init; }

    /// <summary>Gets whether receiver control is supported.</summary>
    public bool SupportsReceiverControl { get; init; }

    /// <summary>Gets whether device restart is supported.</summary>
    public bool SupportsRestart { get; init; }
}

/// <summary>
/// Default catalog-wide device status service.
/// </summary>
public sealed class PhysicalDeviceStatusService : IPhysicalDeviceStatusService
{
    private readonly IPhysicalDeviceCatalog _catalog;
    private readonly ILogger<PhysicalDeviceStatusService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<PhysicalDeviceRuntimeStatus> _devices = [];

    /// <summary>Initializes a physical-device status service.</summary>
    public PhysicalDeviceStatusService(IPhysicalDeviceCatalog catalog, ILogger<PhysicalDeviceStatusService>? logger = null)
    {
        _catalog = catalog;
        _logger = logger ?? NullLogger<PhysicalDeviceStatusService>.Instance;
    }

    /// <inheritdoc />
    public IReadOnlyList<PhysicalDeviceRuntimeStatus> Devices => Volatile.Read(ref _devices);

    /// <inheritdoc />
    public event Action? StateChanged;

    /// <inheritdoc />
    public async Task RefreshAsync(StreamingConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await _catalog.ApplyAsync(configuration, cancellationToken);
            var statuses = new List<PhysicalDeviceRuntimeStatus>(_catalog.Devices.Count);
            foreach (var device in _catalog.Devices)
            {
                try
                {
                    var status = device is IPhysicalDeviceStatusCapability capability
                        ? await capability.RefreshStatusAsync(cancellationToken)
                        : new PhysicalDeviceStatusSnapshot { Device = device.Snapshot };
                    statuses.Add(CreateRuntimeStatus(device, status, null));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or HttpRequestException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(exception, "Unable to refresh status for physical device {PhysicalDeviceId}", device.Snapshot.Id);
                    var prior = Devices.SingleOrDefault(item => item.Device.Id == device.Snapshot.Id)?.Status;
                    statuses.Add(CreateRuntimeStatus(device, prior, $"Status refresh failed ({exception.GetType().Name})."));
                }
            }
            Volatile.Write(ref _devices, statuses);
        }
        finally
        {
            _gate.Release();
        }
        StateChanged?.Invoke();
    }

    /// <inheritdoc />
    public async Task RefreshDeviceInformationAsync(StreamingConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await _catalog.ApplyAsync(configuration, cancellationToken);
            var statuses = new List<PhysicalDeviceRuntimeStatus>(_catalog.Devices.Count);
            foreach (var device in _catalog.Devices)
            {
                var prior = Devices.SingleOrDefault(item => item.Device.Id == device.Snapshot.Id);
                try
                {
                    var refreshed = device is IPhysicalDeviceInformationCapability capability
                        ? await capability.RefreshDeviceInformationAsync(cancellationToken)
                        : prior?.Status ?? new PhysicalDeviceStatusSnapshot { Device = device.Snapshot };
                    var status = refreshed with { Receivers = prior?.Status?.Receivers ?? refreshed.Receivers };
                    statuses.Add(CreateRuntimeStatus(device, status, null));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or HttpRequestException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(exception, "Unable to refresh information for physical device {PhysicalDeviceId}", device.Snapshot.Id);
                    statuses.Add(CreateRuntimeStatus(device, prior?.Status, $"Device information refresh failed ({exception.GetType().Name})."));
                }
            }
            Volatile.Write(ref _devices, statuses);
        }
        finally
        {
            _gate.Release();
        }
        StateChanged?.Invoke();
    }

    /// <inheritdoc />
    public async Task StopReceiverAsync(PhysicalDeviceId deviceId, int receiverIndex, CancellationToken cancellationToken = default)
    {
        var device = GetDevice(deviceId);
        if (device is not IPhysicalReceiverControlCapability control)
        {
            throw new NotSupportedException($"Physical device {deviceId} does not support receiver control.");
        }
        await control.StopReceiverAsync(receiverIndex, cancellationToken);
    }

    /// <inheritdoc />
    public async Task RestartAsync(PhysicalDeviceId deviceId, CancellationToken cancellationToken = default)
    {
        var device = GetDevice(deviceId);
        if (device is not IPhysicalDeviceRestartCapability restart)
        {
            throw new NotSupportedException($"Physical device {deviceId} does not support restart.");
        }
        await restart.RestartAsync(cancellationToken);
    }

    private IPhysicalTunerDevice GetDevice(PhysicalDeviceId deviceId) =>
        _catalog.Devices.SingleOrDefault(device => device.Snapshot.Id == deviceId)
        ?? throw new KeyNotFoundException($"Physical device {deviceId} is not active.");

    private static PhysicalDeviceRuntimeStatus CreateRuntimeStatus(
        IPhysicalTunerDevice device,
        PhysicalDeviceStatusSnapshot? status,
        string? error) =>
        new()
        {
            Device = status?.Device ?? device.Snapshot,
            Status = status,
            Error = error,
            SupportsReceiverControl = device is IPhysicalReceiverControlCapability,
            SupportsRestart = device is IPhysicalDeviceRestartCapability
        };
}
