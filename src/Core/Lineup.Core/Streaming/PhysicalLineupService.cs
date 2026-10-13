using Lineup.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lineup.Core.Streaming;

/// <summary>
/// Refreshes configured physical-device lineups independently and reconciles successful results.
/// </summary>
public interface IPhysicalLineupService
{
    /// <summary>Applies the configuration and refreshes every enabled physical device.</summary>
    Task<IReadOnlyList<PhysicalLineupRefreshResult>> RefreshAsync(
        StreamingConfiguration configuration,
        CancellationToken cancellationToken = default);
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
}

/// <summary>
/// Default catalog-based physical lineup refresh coordinator.
/// </summary>
public sealed class PhysicalLineupService : IPhysicalLineupService
{
    private readonly IPhysicalDeviceCatalog _devices;
    private readonly StreamingTopologyStore _topology;
    private readonly ILogger<PhysicalLineupService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Initializes a physical lineup refresh coordinator.</summary>
    public PhysicalLineupService(
        IPhysicalDeviceCatalog devices,
        StreamingTopologyStore topology,
        ILogger<PhysicalLineupService>? logger = null)
    {
        _devices = devices;
        _topology = topology;
        _logger = logger ?? NullLogger<PhysicalLineupService>.Instance;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PhysicalLineupRefreshResult>> RefreshAsync(
        StreamingConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await _devices.ApplyAsync(configuration, cancellationToken);
            await _topology.PruneDevicesAsync(configuration.PhysicalDevices.Select(device => device.Id), cancellationToken);
            var results = new List<PhysicalLineupRefreshResult>(_devices.Devices.Count);
            foreach (var device in _devices.Devices)
            {
                try
                {
                    var lineup = await device.RefreshLineupAsync(cancellationToken);
                    var stored = await _topology.ReconcileAsync(device.Snapshot.Id, lineup, cancellationToken);
                    results.Add(new PhysicalLineupRefreshResult
                    {
                        DeviceId = device.Snapshot.Id,
                        Succeeded = true,
                        ChannelCount = stored.Count
                    });
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or HttpRequestException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(exception, "Unable to refresh physical lineup for device {PhysicalDeviceId}; prior state was preserved", device.Snapshot.Id);
                    results.Add(new PhysicalLineupRefreshResult
                    {
                        DeviceId = device.Snapshot.Id,
                        Succeeded = false,
                        Error = $"Physical lineup refresh failed ({exception.GetType().Name}); prior state was preserved."
                    });
                }
            }
            return results;
        }
        finally
        {
            _gate.Release();
        }
    }
}
