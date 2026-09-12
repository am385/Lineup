using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Lineup.HDHomeRun.Device.Protocol;

namespace Lineup.Web.Services;

/// <summary>
/// Answers SiliconDust UDP discovery requests for the current virtual HDHomeRun device.
/// </summary>
public class HdHomeRunDiscoveryService : BackgroundService
{
    private const int RequestsPerSourcePerSecond = 10;
    private const int MaximumTrackedSources = 1024;
    private static readonly TimeSpan DefaultRestartDelay = TimeSpan.FromSeconds(1);
    private readonly IHdHomeRunProxyProfileProvider _profiles;
    private readonly IAppSettingsService _settings;
    private readonly IDeviceStateService _deviceState;
    private readonly ILogger<HdHomeRunDiscoveryService> _logger;
    private readonly SemaphoreSlim _snapshotLock = new(1, 1);
    private readonly object _rateLimitGate = new();
    private readonly Dictionary<IPAddress, ResponseWindow> _responseWindows = [];
    private readonly TaskCompletionSource _executionStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IReadOnlyList<HdHomeRunAdvertisedDevice>? _advertisedDevices;
    private int _snapshotVersion;

    /// <summary>
    /// Initializes the SiliconDust discovery listener.
    /// </summary>
    /// <param name="profiles">Provider for enabled virtual device profiles.</param>
    /// <param name="settings">Application discovery settings.</param>
    /// <param name="deviceState">Physical device state used to invalidate cached advertisements.</param>
    /// <param name="logger">Logger for listener diagnostics.</param>
    public HdHomeRunDiscoveryService(IHdHomeRunProxyProfileProvider profiles, IAppSettingsService settings, IDeviceStateService deviceState, ILogger<HdHomeRunDiscoveryService> logger)
    {
        _profiles = profiles;
        _settings = settings;
        _deviceState = deviceState;
        _logger = logger;
    }

    /// <summary>
    /// Gets the delay before restarting an unexpectedly stopped listener.
    /// </summary>
    protected virtual TimeSpan ListenerRestartDelay => DefaultRestartDelay;

    /// <summary>
    /// Gets the current time used for discovery response rate limiting.
    /// </summary>
    protected virtual DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    /// <inheritdoc />
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await base.StartAsync(cancellationToken);
        await _executionStarted.Task.WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var changes = Channel.CreateUnbounded<bool>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        void SettingsChanged()
        {
            InvalidateAdvertisedDevices();
            changes.Writer.TryWrite(true);
        }
        void DeviceStateChanged() => InvalidateAdvertisedDevices();
        _settings.OnSettingsChanged += SettingsChanged;
        _deviceState.OnStateChanged += DeviceStateChanged;
        _executionStarted.TrySetResult();

        CancellationTokenSource? listenerCancellation = null;
        Task? listenerTask = null;
        string? activeConfiguration = null;
        try
        {
            SettingsChanged();
            while (!stoppingToken.IsCancellationRequested)
            {
                while (changes.Reader.TryRead(out _))
                {
                }

                var configuration = GetDiscoveryConfiguration();
                if (configuration != activeConfiguration)
                {
                    await StopListenerAsync(listenerCancellation, listenerTask);
                    listenerCancellation?.Dispose();
                    listenerCancellation = null;
                    listenerTask = null;
                    activeConfiguration = null;
                }

                if (configuration == null)
                {
                    await changes.Reader.ReadAsync(stoppingToken);
                    continue;
                }

                if (listenerTask == null)
                {
                    listenerCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    activeConfiguration = configuration;
                    listenerTask = RunListenerSessionWithLoggingAsync(listenerCancellation.Token);
                }

                using var changeCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var changeTask = changes.Reader.WaitToReadAsync(changeCancellation.Token).AsTask();
                var completedTask = await Task.WhenAny(listenerTask, changeTask);
                if (completedTask == changeTask)
                {
                    await changeTask;
                    continue;
                }

                await listenerTask;
                listenerCancellation?.Dispose();
                listenerCancellation = null;
                listenerTask = null;
                activeConfiguration = null;
                changeCancellation.Cancel();
                await IgnoreExpectedCancellationAsync(changeTask);
                stoppingToken.ThrowIfCancellationRequested();
                _logger.LogWarning("SiliconDust discovery listener stopped unexpectedly; restarting in {RestartDelay}", ListenerRestartDelay);
                await WaitForRestartOrSettingsChangeAsync(changes.Reader, ListenerRestartDelay, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _settings.OnSettingsChanged -= SettingsChanged;
            _deviceState.OnStateChanged -= DeviceStateChanged;
            await StopListenerAsync(listenerCancellation, listenerTask);
            listenerCancellation?.Dispose();
        }
    }

    /// <summary>
    /// Runs one listener lifetime for the current valid discovery configuration.
    /// </summary>
    /// <param name="cancellationToken">Stops and disposes the listener.</param>
    protected virtual async Task RunListenerSessionAsync(CancellationToken cancellationToken)
    {
        using var client = CreateListener();
        _logger.LogInformation("Listening for SiliconDust discovery requests on UDP port {Port}", HDHomeRunDiscovery.DiscoveryPort);
        while (!cancellationToken.IsCancellationRequested)
        {
            var request = await client.ReceiveAsync(cancellationToken);
            foreach (var reply in await CreateRepliesAsync(request.Buffer, request.RemoteEndPoint.Address, cancellationToken))
            {
                await client.SendAsync(reply, request.RemoteEndPoint, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Validates a SiliconDust datagram and creates replies without resolving profiles for rejected traffic.
    /// </summary>
    /// <param name="request">Raw discovery datagram.</param>
    /// <param name="sourceAddress">Request source address.</param>
    /// <param name="cancellationToken">Stops profile resolution.</param>
    /// <returns>Replies for matching advertised devices.</returns>
    protected async Task<IReadOnlyList<byte[]>> CreateRepliesAsync(ReadOnlyMemory<byte> request, IPAddress sourceAddress, CancellationToken cancellationToken)
    {
        if (!HdHomeRunDiscoveryProtocol.IsDiscoveryRequest(request.Span) ||
            !TryAcquireResponsePermit(sourceAddress))
        {
            return [];
        }

        var replies = new List<byte[]>();
        foreach (var device in await GetAdvertisedDevicesAsync(cancellationToken))
        {
            if (HdHomeRunDiscoveryProtocol.TryCreateReply(request.Span, device, out var reply))
            {
                replies.Add(reply!);
            }
        }

        return replies;
    }

    /// <summary>
    /// Gets the cached immutable advertised-device snapshot, rebuilding it after relevant state changes.
    /// </summary>
    /// <param name="cancellationToken">Stops profile resolution.</param>
    /// <returns>The current advertised devices.</returns>
    protected async Task<IReadOnlyList<HdHomeRunAdvertisedDevice>> GetAdvertisedDevicesAsync(CancellationToken cancellationToken)
    {
        if (_advertisedDevices is { } cached)
        {
            return cached;
        }

        await _snapshotLock.WaitAsync(cancellationToken);
        try
        {
            while (_advertisedDevices == null)
            {
                var version = Volatile.Read(ref _snapshotVersion);
                var devices = HdHomeRunAdvertisedDeviceFactory.Create(await _profiles.GetProfilesAsync(cancellationToken));
                if (version == Volatile.Read(ref _snapshotVersion))
                {
                    _advertisedDevices = devices;
                }
            }

            return _advertisedDevices;
        }
        finally
        {
            _snapshotLock.Release();
        }
    }

    /// <summary>
    /// Attempts to reserve one response slot for a discovery request source.
    /// </summary>
    /// <param name="sourceAddress">Request source address.</param>
    /// <returns><see langword="true"/> while the source remains within the response limit.</returns>
    protected bool TryAcquireResponsePermit(IPAddress sourceAddress)
    {
        var now = UtcNow;
        lock (_rateLimitGate)
        {
            if (!_responseWindows.TryGetValue(sourceAddress, out var window) || now - window.Start >= TimeSpan.FromSeconds(1))
            {
                if (!_responseWindows.ContainsKey(sourceAddress) && _responseWindows.Count >= MaximumTrackedSources)
                {
                    var oldestSource = _responseWindows.MinBy(entry => entry.Value.Start).Key;
                    _responseWindows.Remove(oldestSource);
                }

                _responseWindows[sourceAddress] = new ResponseWindow(now, 1);
                return true;
            }

            if (window.Count >= RequestsPerSourcePerSecond)
            {
                return false;
            }

            _responseWindows[sourceAddress] = window with { Count = window.Count + 1 };
            return true;
        }
    }

    private void InvalidateAdvertisedDevices()
    {
        Interlocked.Increment(ref _snapshotVersion);
        _advertisedDevices = null;
    }

    private async Task RunListenerSessionWithLoggingAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RunListenerSessionAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SiliconDust discovery listener stopped after a socket or configuration failure");
        }
    }

    private string? GetDiscoveryConfiguration()
    {
        _settings.Settings.EnsureHdHomeRunProxyProfiles();
        if (!_settings.Settings.EnableHdHomeRunProxy || !_settings.Settings.EnableNetworkDiscovery)
        {
            _logger.LogInformation("HDHomeRun network discovery is disabled");
            return null;
        }

        var advertisedUrls = _settings.Settings.HdHomeRunProxyProfiles
            .Where(profile => profile.Enabled && HdHomeRunProxyProfileResolver.TryGetHttpRoot(profile.AdvertisedBaseUrl, out _))
            .Select(profile => profile.AdvertisedBaseUrl!.Trim())
            .OrderBy(url => url, StringComparer.Ordinal)
            .ToArray();
        if (advertisedUrls.Length > 0)
        {
            return string.Join('\n', advertisedUrls);
        }

        _logger.LogWarning("HDHomeRun network discovery is enabled, but AdvertisedBaseUrl is missing or invalid; no SiliconDust advertisements will be sent");
        return null;
    }

    private static async Task StopListenerAsync(CancellationTokenSource? cancellation, Task? listenerTask)
    {
        cancellation?.Cancel();
        if (listenerTask != null)
        {
            await listenerTask;
        }
    }

    private static async Task WaitForRestartOrSettingsChangeAsync(ChannelReader<bool> changes, TimeSpan restartDelay, CancellationToken cancellationToken)
    {
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var changeTask = changes.WaitToReadAsync(waitCancellation.Token).AsTask();
        var delayTask = Task.Delay(restartDelay, waitCancellation.Token);
        await Task.WhenAny(changeTask, delayTask);
        waitCancellation.Cancel();
        await IgnoreExpectedCancellationAsync(changeTask);
        await IgnoreExpectedCancellationAsync(delayTask);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task IgnoreExpectedCancellationAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static UdpClient CreateListener()
    {
        var client = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            client.ExclusiveAddressUse = false;
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
            client.Client.Bind(new IPEndPoint(IPAddress.Any, HDHomeRunDiscovery.DiscoveryPort));
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }

    }

    private sealed record ResponseWindow(DateTimeOffset Start, int Count);
}
