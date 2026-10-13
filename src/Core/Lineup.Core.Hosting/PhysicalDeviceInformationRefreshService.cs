using System.Threading.Channels;
using Lineup.Core.Settings;
using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lineup.Core.Hosting;

/// <summary>
/// Periodically refreshes descriptive information for every enabled physical device.
/// </summary>
public sealed class PhysicalDeviceInformationRefreshService : BackgroundService
{
    private readonly IPhysicalDeviceStatusService _devices;
    private readonly ILineupSettingsService _settings;
    private readonly ILogger<PhysicalDeviceInformationRefreshService> _logger;
    private readonly Channel<bool> _settingsChanged = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });

    /// <summary>Initializes a physical-device information refresh service.</summary>
    public PhysicalDeviceInformationRefreshService(
        IPhysicalDeviceStatusService devices,
        ILineupSettingsService settings,
        ILogger<PhysicalDeviceInformationRefreshService> logger)
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
            var minutes = _settings.Settings.DeviceRefreshIntervalMinutes;
            if (minutes <= 0)
            {
                await _settingsChanged.Reader.ReadAsync(stoppingToken);
                continue;
            }

            using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var delay = Task.Delay(TimeSpan.FromMinutes(minutes), waitCancellation.Token);
            var changed = _settingsChanged.Reader.ReadAsync(waitCancellation.Token).AsTask();
            var completed = await Task.WhenAny(delay, changed);
            await waitCancellation.CancelAsync();
            if (completed == changed)
            {
                await changed;
                continue;
            }

            try
            {
                _logger.LogDebug("Refreshing information for configured physical devices");
                await _devices.RefreshDeviceInformationAsync(_settings.Settings.Streaming, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Unable to refresh configured physical-device information");
            }
        }
    }

    private void SettingsChanged() => _settingsChanged.Writer.TryWrite(true);
}
