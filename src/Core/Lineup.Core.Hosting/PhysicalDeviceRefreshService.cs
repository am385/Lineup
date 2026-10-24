using System.Threading.Channels;
using Lineup.Core.Settings;
using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lineup.Core.Hosting;

/// <summary>
/// Periodically refreshes provider-neutral status for every enabled physical device.
/// </summary>
public sealed class PhysicalDeviceRefreshService : BackgroundService
{
    private readonly IPhysicalDeviceStatusService _devices;
    private readonly ILineupSettingsService _settings;
    private readonly ILogger<PhysicalDeviceRefreshService> _logger;
    private readonly Channel<bool> _settingsChanged = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });

    /// <summary>Initializes a catalog-wide physical-device refresh service.</summary>
    public PhysicalDeviceRefreshService(
        IPhysicalDeviceStatusService devices,
        ILineupSettingsService settings,
        ILogger<PhysicalDeviceRefreshService> logger)
    {
        _devices = devices;
        _settings = settings;
        _logger = logger;
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _settings.OnSettingsChanged += SettingsChanged;
        return base.StartAsync(cancellationToken);
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _settings.OnSettingsChanged -= SettingsChanged;
        await base.StopAsync(cancellationToken);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogDebug("Refreshing tuner status for configured physical devices");
                await _devices.RefreshAsync(_settings.Settings.Streaming, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Unable to refresh configured physical-device status");
            }

            var seconds = _settings.Settings.TunerRefreshIntervalSeconds;
            if (seconds <= 0)
            {
                await _settingsChanged.Reader.ReadAsync(stoppingToken);
                continue;
            }

            using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var delay = Task.Delay(TimeSpan.FromSeconds(seconds), waitCancellation.Token);
            var changed = _settingsChanged.Reader.ReadAsync(waitCancellation.Token).AsTask();
            var completed = await Task.WhenAny(delay, changed);
            await waitCancellation.CancelAsync();
            if (completed == changed)
            {
                await changed;
            }
        }
    }

    private void SettingsChanged() => _settingsChanged.Writer.TryWrite(true);
}
