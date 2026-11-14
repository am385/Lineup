using Lineup.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Lineup.Core.Settings;

namespace Lineup.Core.Streaming;

/// <summary>
/// Refreshes configured physical-device lineups independently and reconciles successful results.
/// </summary>
public interface IPhysicalLineupService
{
    /// <summary>Applies configuration and returns one refresh outcome per enabled physical device, including activation failures.</summary>
    Task<IReadOnlyList<PhysicalLineupRefreshResult>> RefreshAsync(StreamingConfiguration configuration, CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of refreshing one configured physical device lineup.
/// </summary>
public sealed record PhysicalLineupRefreshResult
{
    /// <summary>Gets the configured physical device.</summary>
    public required PhysicalDeviceId DeviceId { get; init; }

    /// <summary>Gets whether the latest lineup committed successfully.</summary>
    public required bool Succeeded { get; init; }

    /// <summary>Gets the committed active channel count.</summary>
    public int ChannelCount { get; init; }

    /// <summary>Gets an actionable error without sensitive adapter details.</summary>
    public string? Error { get; init; }

    /// <summary>Gets when this refresh attempt completed.</summary>
    public DateTimeOffset ObservedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Default catalog-based physical lineup refresh coordinator.
/// </summary>
public sealed class PhysicalLineupService : IPhysicalLineupService
{
    private readonly IPhysicalDeviceCatalog _devices;
    private readonly StreamingTopologyStore _topology;
    private readonly ILogger<PhysicalLineupService> _logger;
    private readonly PhysicalDeviceRefreshScheduler _refresh;
    private readonly CoalescedRefresh<IReadOnlyList<PhysicalLineupRefreshResult>> _requests;

    /// <summary>Initializes a physical lineup refresh coordinator.</summary>
    public PhysicalLineupService(IPhysicalDeviceCatalog devices, StreamingTopologyStore topology, ILogger<PhysicalLineupService>? logger = null, PhysicalDeviceRefreshScheduler? refresh = null)
    {
        _devices = devices;
        _topology = topology;
        _logger = logger ?? NullLogger<PhysicalLineupService>.Instance;
        _refresh = refresh ?? new PhysicalDeviceRefreshScheduler();
        _requests = new CoalescedRefresh<IReadOnlyList<PhysicalLineupRefreshResult>>(_logger);
    }

    /// <summary>Refreshes the physical lineup service asynchronously.</summary>
    /// <param name="configuration">Configuration to apply.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The operation's result.</returns>
    public Task<IReadOnlyList<PhysicalLineupRefreshResult>> RefreshAsync(StreamingConfiguration configuration, CancellationToken cancellationToken = default) =>
        _requests.RunAsync(ConfigurationRevision.Create(configuration.PhysicalDevices), token => RefreshCoreAsync(configuration, token), cancellationToken);

    private async Task<IReadOnlyList<PhysicalLineupRefreshResult>> RefreshCoreAsync(StreamingConfiguration configuration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        await _devices.ApplyAsync(configuration, cancellationToken);
        var definitions = configuration.PhysicalDevices.Where(device => device.Enabled).OrderBy(device => device.Order).ThenBy(device => device.Id.Value).ToArray();
        var initialized = await _devices.TryPublishAsync(configuration, async ct =>
        {
            await _topology.PruneDevicesAsync(configuration.PhysicalDevices.Select(device => device.Id), ct);
        }, cancellationToken);
        if (!initialized)
        {
            return definitions.Select(definition => new PhysicalLineupRefreshResult
            {
                DeviceId = definition.Id,
                Succeeded = false,
                Error = "Physical configuration changed before lineup publication; the obsolete refresh was discarded."
            }).ToArray();
        }
        var activeDevices = _devices.Devices.ToDictionary(device => device.Snapshot.Id);
        var results = new PhysicalLineupRefreshResult[definitions.Length];
        await Parallel.ForEachAsync(Enumerable.Range(0, definitions.Length),
            new ParallelOptions { MaxDegreeOfParallelism = _refresh.MaximumConcurrentOperations, CancellationToken = cancellationToken }, async (index, token) =>
        {
            var definition = definitions[index];
            token.ThrowIfCancellationRequested();
            if (!activeDevices.TryGetValue(definition.Id, out var device))
            {
                _logger.LogWarning(
                    "Unable to refresh physical lineup for inactive device {PhysicalDeviceId}; activation failed and prior state was preserved",
                    definition.Id);
                results[index] = new PhysicalLineupRefreshResult
                {
                    DeviceId = definition.Id,
                    Succeeded = false,
                    Error = "Physical device activation failed; check the device configuration and connection. Prior state was preserved."
                };
                return;
            }
            try
            {
                var lineup = await _refresh.RunAsync(device.RefreshLineupAsync, token);
                var channelCount = 0;
                var committed = await _devices.TryPublishAsync(configuration, async ct =>
                {
                    if (!_devices.Devices.Any(current => ReferenceEquals(current, device)))
                    {
                        throw new InvalidOperationException("The physical-device runtime changed before lineup publication.");
                    }
                    channelCount = (await _topology.ReconcileAsync(device.Snapshot.Id, lineup, ct)).Count;
                }, token);
                results[index] = new PhysicalLineupRefreshResult
                {
                    DeviceId = device.Snapshot.Id,
                    Succeeded = committed,
                    ChannelCount = channelCount,
                    Error = committed ? null : "Physical configuration changed before lineup publication; the obsolete refresh was discarded."
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is ArgumentException or InvalidOperationException or InvalidDataException or IOException or HttpRequestException or UnauthorizedAccessException or TimeoutException)
            {
                _logger.LogWarning(exception, "Unable to refresh physical lineup for device {PhysicalDeviceId}; prior state was preserved", device.Snapshot.Id);
                results[index] = new PhysicalLineupRefreshResult
                {
                    DeviceId = device.Snapshot.Id,
                    Succeeded = false,
                    Error = $"Physical lineup refresh failed ({exception.GetType().Name}); prior state was preserved."
                };
            }
        });
        return results;
    }
}
