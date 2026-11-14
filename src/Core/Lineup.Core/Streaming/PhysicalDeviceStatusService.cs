using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Lineup.Core.Settings;

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

    /// <summary>Gets a safe activation or status-refresh failure message.</summary>
    public string? Error { get; init; }

    /// <summary>Gets whether receiver control is supported.</summary>
    public bool SupportsReceiverControl { get; init; }

    /// <summary>Gets whether device restart is supported.</summary>
    public bool SupportsRestart { get; init; }

    /// <summary>Gets the last completed refresh attempt, including failures.</summary>
    public DateTimeOffset? LastAttemptAtUtc { get; init; }

    /// <summary>Gets the last successful receiver-status observation.</summary>
    public DateTimeOffset? StatusObservedAtUtc { get; init; }

    /// <summary>Gets the last successful descriptive-information observation.</summary>
    public DateTimeOffset? InformationObservedAtUtc { get; init; }
}

/// <summary>
/// Default catalog-wide device status service.
/// </summary>
public sealed class PhysicalDeviceStatusService : IPhysicalDeviceStatusService
{
    private readonly IPhysicalDeviceCatalog _catalog;
    private readonly ILogger<PhysicalDeviceStatusService> _logger;
    private readonly PhysicalDeviceRefreshScheduler _refresh;
    private readonly CoalescedRefresh<int> _statuses;
    private readonly CoalescedRefresh<int> _information;
    private IReadOnlyList<PhysicalDeviceRuntimeStatus> _devices = [];

    /// <summary>Initializes a physical-device status service.</summary>
    public PhysicalDeviceStatusService(IPhysicalDeviceCatalog catalog, ILogger<PhysicalDeviceStatusService>? logger = null, PhysicalDeviceRefreshScheduler? refresh = null)
    {
        _catalog = catalog;
        _logger = logger ?? NullLogger<PhysicalDeviceStatusService>.Instance;
        _refresh = refresh ?? new PhysicalDeviceRefreshScheduler();
        _statuses = new CoalescedRefresh<int>(_logger);
        _information = new CoalescedRefresh<int>(_logger);
    }

    /// <summary>Gets the devices.</summary>
    public IReadOnlyList<PhysicalDeviceRuntimeStatus> Devices => Volatile.Read(ref _devices);

    /// <summary>Occurs when state changed.</summary>
    public event Action? StateChanged;

    /// <summary>Refreshes the physical device status service asynchronously.</summary>
    /// <param name="configuration">Configuration to apply.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task that completes when the operation has finished.</returns>
    public Task RefreshAsync(StreamingConfiguration configuration, CancellationToken cancellationToken = default) =>
        _statuses.RunAsync(ConfigurationRevision.Create(configuration.PhysicalDevices), token => RefreshCoreAsync(configuration, false, token), cancellationToken);

    /// <summary>Refreshes device information asynchronously.</summary>
    /// <param name="configuration">Configuration to apply.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task that completes when the operation has finished.</returns>
    public Task RefreshDeviceInformationAsync(StreamingConfiguration configuration, CancellationToken cancellationToken = default) =>
        _information.RunAsync(ConfigurationRevision.Create(configuration.PhysicalDevices), token => RefreshCoreAsync(configuration, true, token), cancellationToken);

    private async Task<int> RefreshCoreAsync(StreamingConfiguration configuration, bool informationOnly, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        await _catalog.ApplyAsync(configuration, cancellationToken);
        var activeDevices = _catalog.Devices.ToDictionary(device => device.Snapshot.Id);
        var definitions = configuration.PhysicalDevices.Where(device => device.Enabled).OrderBy(device => device.Order).ThenBy(device => device.Id.Value).ToArray();
        var initialized = await _catalog.TryPublishAsync(configuration, _ =>
        {
            Volatile.Write(ref _devices, definitions.Select(definition => activeDevices.TryGetValue(definition.Id, out var device)
                ? Devices.SingleOrDefault(prior => prior.Device.Id == definition.Id && prior.Device.Kind == definition.Adapter.Kind)
                    ?? CreateRuntimeStatus(device, null, null, definition.Name)
                : CreateActivationFailureStatus(definition)).ToArray());
            return Task.CompletedTask;
        }, cancellationToken);
        if (!initialized)
        {
            return 0;
        }
        await Parallel.ForEachAsync(definitions,
            new ParallelOptions { MaxDegreeOfParallelism = _refresh.MaximumConcurrentOperations, CancellationToken = cancellationToken }, async (definition, token) =>
        {
            if (!activeDevices.TryGetValue(definition.Id, out var device))
            {
                return;
            }
            PhysicalDeviceStatusSnapshot? refreshed = null;
            string? error = null;
            var startedAt = DateTimeOffset.UtcNow;
            try
            {
                refreshed = await _refresh.RunAsync(ct => informationOnly
                    ? device is IPhysicalDeviceInformationCapability information
                        ? information.RefreshDeviceInformationAsync(ct)
                        : Task.FromResult(Devices.SingleOrDefault(item => item.Device.Id == definition.Id)?.Status ?? new PhysicalDeviceStatusSnapshot { Device = device.Snapshot })
                    : device is IPhysicalDeviceStatusCapability status ? status.RefreshStatusAsync(ct) : Task.FromResult(new PhysicalDeviceStatusSnapshot { Device = device.Snapshot }), token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is ArgumentException or InvalidOperationException or InvalidDataException or IOException or HttpRequestException or UnauthorizedAccessException or TimeoutException)
            {
                _logger.LogWarning(exception, "Unable to refresh {RefreshKind} for physical device {PhysicalDeviceId}", informationOnly ? "information" : "status", device.Snapshot.Id);
                error = $"{(informationOnly ? "Device information" : "Status")} refresh failed ({exception.GetType().Name}).";
            }
            var published = await _catalog.TryPublishAsync(configuration, _ =>
            {
                if (!_catalog.Devices.Any(current => ReferenceEquals(current, device)))
                {
                    return Task.CompletedTask;
                }
                var prior = Devices.SingleOrDefault(item => item.Device.Id == definition.Id);
                var status = refreshed ?? prior?.Status;
                if (refreshed is not null && informationOnly)
                {
                    status = refreshed with { Receivers = prior?.Status?.Receivers ?? refreshed.Receivers };
                }
                else if (refreshed is not null && prior?.InformationObservedAtUtc > startedAt)
                {
                    status = refreshed with { Device = prior.Device };
                }
                var now = DateTimeOffset.UtcNow;
                var updated = CreateRuntimeStatus(device, status, error, definition.Name) with
                {
                    LastAttemptAtUtc = now,
                    StatusObservedAtUtc = !informationOnly && error is null ? now : prior?.StatusObservedAtUtc,
                    InformationObservedAtUtc = informationOnly && error is null ? now : prior?.InformationObservedAtUtc
                };
                Volatile.Write(ref _devices, Devices.Select(item => item.Device.Id == definition.Id ? updated : item).ToArray());
                return Task.CompletedTask;
            }, token);
            if (published)
            {
                StateChanged?.Invoke();
            }
        });
        if (activeDevices.Count == 0)
        {
            StateChanged?.Invoke();
        }
        return 0;
    }

    /// <summary>Stops receiver asynchronously.</summary>
    /// <param name="deviceId">Identity of the target device.</param>
    /// <param name="receiverIndex">The receiver index value.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task that completes when the operation has finished.</returns>
    public async Task StopReceiverAsync(PhysicalDeviceId deviceId, int receiverIndex, CancellationToken cancellationToken = default)
    {
        var device = GetDevice(deviceId);
        if (device is not IPhysicalReceiverControlCapability control)
        {
            throw new NotSupportedException($"Physical device {deviceId} does not support receiver control.");
        }
        await control.StopReceiverAsync(receiverIndex, cancellationToken);
    }

    /// <summary>Executes restart asynchronously.</summary>
    /// <param name="deviceId">Identity of the target device.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task that completes when the operation has finished.</returns>
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

    private PhysicalDeviceRuntimeStatus CreateActivationFailureStatus(PhysicalDeviceDefinition definition)
    {
        _logger.LogWarning("Unable to report status for inactive physical device {PhysicalDeviceId}; activation failed", definition.Id);
        var prior = Devices.SingleOrDefault(device => device.Device.Id == definition.Id &&
            string.Equals(device.Device.Kind, definition.Adapter.Kind, StringComparison.OrdinalIgnoreCase));
        return new PhysicalDeviceRuntimeStatus
        {
            Device = new PhysicalDeviceSnapshot
            {
                Id = definition.Id,
                Kind = definition.Adapter.Kind,
                Name = definition.Name,
                Capacity = prior?.Device.Capacity ?? new PhysicalTunerCapacity(0)
            },
            Status = prior?.Status,
            LastAttemptAtUtc = DateTimeOffset.UtcNow,
            StatusObservedAtUtc = prior?.StatusObservedAtUtc,
            InformationObservedAtUtc = prior?.InformationObservedAtUtc,
            Error = "Physical device activation failed; check the device configuration and connection."
        };
    }

    private static PhysicalDeviceRuntimeStatus CreateRuntimeStatus(IPhysicalTunerDevice device, PhysicalDeviceStatusSnapshot? status, string? error, string name)
    {
        var snapshot = status?.Device ?? device.Snapshot;
        if (!string.Equals(snapshot.Name, name, StringComparison.Ordinal))
        {
            snapshot = snapshot with { Name = name };
            if (status is not null)
            {
                status = status with { Device = snapshot };
            }
        }
        return new()
        {
            Device = snapshot,
            Status = status,
            Error = error,
            SupportsReceiverControl = device is IPhysicalReceiverControlCapability,
            SupportsRestart = device is IPhysicalDeviceRestartCapability
        };
    }
}
