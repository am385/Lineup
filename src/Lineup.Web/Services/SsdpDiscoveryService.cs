using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace Lineup.Web.Services;

/// <summary>
/// Advertises the current virtual HDHomeRun device as a UPnP MediaServer over SSDP.
/// </summary>
public class SsdpDiscoveryService : BackgroundService
{
    private const int SsdpPort = 1900;
    private const int RequestsPerSourcePerSecond = 10;
    private const int MaximumTrackedSources = 1024;
    private static readonly TimeSpan DefaultRestartDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DefaultAliveNotificationInterval = TimeSpan.FromMinutes(15);
    private static readonly IPAddress MulticastAddress = IPAddress.Parse("239.255.255.250");
    private static readonly IPEndPoint MulticastEndpoint = new(MulticastAddress, SsdpPort);
    private readonly IHdHomeRunProxyProfileProvider _profiles;
    private readonly IAppSettingsService _settings;
    private readonly IDeviceStateService _deviceState;
    private readonly ILogger<SsdpDiscoveryService> _logger;
    private readonly SemaphoreSlim _snapshotLock = new(1, 1);
    private readonly object _rateLimitGate = new();
    private readonly Dictionary<IPAddress, ResponseWindow> _responseWindows = [];
    private readonly TaskCompletionSource _executionStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IReadOnlyList<HdHomeRunAdvertisedDevice>? _advertisedDevices;
    private int _snapshotVersion;

    /// <summary>
    /// Initializes the SSDP listener.
    /// </summary>
    /// <param name="profiles">Provider for enabled virtual device profiles.</param>
    /// <param name="settings">Application discovery settings.</param>
    /// <param name="deviceState">Physical device state used to invalidate cached advertisements.</param>
    /// <param name="logger">Logger for listener diagnostics.</param>
    public SsdpDiscoveryService(IHdHomeRunProxyProfileProvider profiles, IAppSettingsService settings, IDeviceStateService deviceState, ILogger<SsdpDiscoveryService> logger)
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
    /// Gets the interval used to renew SSDP advertisements before their 30-minute cache lifetime expires.
    /// </summary>
    protected virtual TimeSpan AliveNotificationInterval => DefaultAliveNotificationInterval;

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
        Task<bool>? listenerTask = null;
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
                    await StopListenersAsync(listenerCancellation, listenerTask);
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

                var shouldRestart = await listenerTask;
                listenerCancellation?.Dispose();
                listenerCancellation = null;
                listenerTask = null;
                activeConfiguration = null;
                changeCancellation.Cancel();
                await IgnoreExpectedCancellationAsync(changeTask);
                stoppingToken.ThrowIfCancellationRequested();
                if (!shouldRestart)
                {
                    await changes.Reader.ReadAsync(stoppingToken);
                    continue;
                }

                _logger.LogWarning("SSDP discovery listener stopped unexpectedly; restarting in {RestartDelay}", ListenerRestartDelay);
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
            await StopListenersAsync(listenerCancellation, listenerTask);
            listenerCancellation?.Dispose();
        }
    }

    /// <summary>
    /// Runs one socket lifetime for the current valid SSDP configuration.
    /// </summary>
    /// <param name="cancellationToken">Stops and disposes all listener sockets.</param>
    /// <returns><see langword="true"/> when an established listener stops and should be restarted; otherwise, <see langword="false"/>.</returns>
    protected virtual async Task<bool> RunListenerSessionAsync(CancellationToken cancellationToken)
    {
        List<UdpClient>? listeners = null;
        try
        {
            listeners = CreateListeners();
            if (listeners.Count == 0)
            {
                return false;
            }

            await SendAliveNotificationsAsync(cancellationToken);

            _logger.LogInformation("Listening for SSDP discovery requests on UDP port {Port} with {ListenerCount} socket(s)", SsdpPort, listeners.Count);
            using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var listenerTasks = listeners
                .Select(listener => ReceiveLoopAsync(listener, sessionCancellation.Token))
                .Append(RunAliveNotificationLoopAsync(sessionCancellation.Token))
                .ToArray();
            var completedTask = await Task.WhenAny(listenerTasks);
            try
            {
                await completedTask;
            }
            finally
            {
                sessionCancellation.Cancel();
                await ObserveSessionShutdownAsync(listenerTasks);
            }

            return true;
        }
        finally
        {
            if (listeners != null)
            {
                foreach (var listener in listeners)
                {
                    listener.Dispose();
                }
            }
        }
    }

    private async Task<bool> RunListenerSessionWithLoggingAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await RunListenerSessionAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SSDP discovery listener stopped after a socket or configuration failure");
            return true;
        }
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_settings.Settings.EnableNetworkDiscovery)
        {
            try
            {
                using var client = new UdpClient(AddressFamily.InterNetwork);
                client.MulticastLoopback = false;
                foreach (var device in await GetAdvertisedDevicesAsync(cancellationToken))
                {
                    await SendNotificationsAsync(client, device, false, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to send SSDP byebye notifications");
            }
        }

        await base.StopAsync(cancellationToken);
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
    /// Sends one complete set of SSDP alive advertisements for the current profiles.
    /// </summary>
    /// <param name="cancellationToken">Stops notification transmission.</param>
    protected virtual async Task SendAliveNotificationsAsync(CancellationToken cancellationToken)
    {
        using var client = new UdpClient(AddressFamily.InterNetwork);
        client.MulticastLoopback = false;
        foreach (var device in await GetAdvertisedDevicesAsync(cancellationToken))
        {
            await SendNotificationsAsync(client, device, true, cancellationToken);
        }
    }

    /// <summary>
    /// Renews SSDP alive advertisements until the current listener session stops.
    /// </summary>
    /// <param name="cancellationToken">Stops the renewal loop.</param>
    protected async Task RunAliveNotificationLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await WaitForNextAliveNotificationAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await SendAliveNotificationsAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Waits until the next SSDP alive advertisement renewal is due.
    /// </summary>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <returns>A task that completes when advertisements should be renewed.</returns>
    protected virtual async ValueTask WaitForNextAliveNotificationAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(AliveNotificationInterval, cancellationToken);
    }

    private static async Task ObserveSessionShutdownAsync(IEnumerable<Task> tasks)
    {
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // The task selected by WhenAny is awaited separately so its failure remains authoritative.
        }
    }

    private string? GetDiscoveryConfiguration()
    {
        _settings.Settings.EnsureHdHomeRunProxyProfiles();
        if (!_settings.Settings.EnableHdHomeRunProxy || !_settings.Settings.EnableNetworkDiscovery)
        {
            _logger.LogInformation("SSDP discovery is disabled");
            return null;
        }

        var advertisedProfiles = _settings.Settings.HdHomeRunProxyProfiles
            .Where(profile => profile.Enabled && HdHomeRunProxyProfileResolver.TryGetHttpRoot(profile.AdvertisedBaseUrl, out _))
            .OrderBy(profile => profile.VirtualDeviceId, StringComparer.Ordinal)
            .Select(profile => string.Join('\u001f', profile.VirtualDeviceId, profile.PhysicalAddress, profile.AdvertisedBaseUrl!.Trim(), profile.FriendlyName, profile.TunerCountCap, profile.UsesLegacyDeviceAddress))
            .ToArray();
        if (advertisedProfiles.Length > 0)
        {
            return string.Join('\n', advertisedProfiles);
        }

        _logger.LogWarning("SSDP discovery is enabled, but AdvertisedBaseUrl is missing or invalid; no SSDP advertisements will be sent");
        return null;
    }

    private static async Task StopListenersAsync(CancellationTokenSource? cancellation, Task? listenerTask)
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

    private List<UdpClient> CreateListeners()
    {
        var listeners = new List<UdpClient>();
        try
        {
            UdpClient multicastListener;
            try
            {
                multicastListener = SsdpDiscoverySocket.CreateBoundListener(IPAddress.Any, SsdpPort, allowAddressSharing: !OperatingSystem.IsWindows());
            }
            catch (SocketException ex) when (OperatingSystem.IsWindows() &&
                ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
            {
                _logger.LogError(
                    ex,
                    "SSDP disabled: UDP port 1900 is already owned on Windows, commonly by SSDPSRV. " +
                    "Windows does not reliably duplicate SSDP datagrams across shared port bindings. " +
                    "Stop and disable the Windows SSDP Discovery service, then restart Lineup, or use the manual profile URLs");
                return listeners;
            }

            listeners.Add(multicastListener);
            LogSocketBinding(multicastListener);
            JoinMulticastInterfaces(multicastListener);
            return listeners;
        }
        catch
        {
            foreach (var listener in listeners)
            {
                listener.Dispose();
            }

            throw;
        }
    }

    private async Task ReceiveLoopAsync(UdpClient client, CancellationToken cancellationToken)
    {
        var localEndpoint = client.Client.LocalEndPoint;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var request = await client.ReceiveAsync(cancellationToken);
                var requestText = Encoding.UTF8.GetString(request.Buffer);
                foreach (var response in await CreateSearchResponsesAsync(requestText, request.RemoteEndPoint.Address, cancellationToken))
                {
                    await client.SendAsync(Encoding.UTF8.GetBytes(response), request.RemoteEndPoint, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SSDP socket {LocalEndpoint} stopped receiving", localEndpoint);
        }
    }

    /// <summary>
    /// Validates an SSDP datagram and creates responses without resolving profiles for rejected traffic.
    /// </summary>
    /// <param name="request">Raw HTTPU request text.</param>
    /// <param name="sourceAddress">Request source address.</param>
    /// <param name="cancellationToken">Stops profile resolution.</param>
    /// <returns>Responses for each current advertised device.</returns>
    protected async Task<IReadOnlyList<string>> CreateSearchResponsesAsync(string request, IPAddress sourceAddress, CancellationToken cancellationToken)
    {
        if (!SsdpDiscoveryProtocol.TryParseSearch(request, out _) ||
            !TryAcquireResponsePermit(sourceAddress))
        {
            return [];
        }

        var responses = new List<string>();
        foreach (var device in await GetAdvertisedDevicesAsync(cancellationToken))
        {
            responses.AddRange(SsdpDiscoveryProtocol.CreateSearchResponses(request, device));
        }

        return responses;
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

    private void JoinMulticastInterfaces(UdpClient client)
    {
        var joined = false;
        foreach (var address in GetLocalIpv4Addresses())
        {
            try
            {
                client.JoinMulticastGroup(MulticastAddress, address);
                joined = true;
                _logger.LogInformation("SSDP socket joined multicast group {Group} on interface {Address}", MulticastAddress, address);
            }
            catch (SocketException ex)
            {
                _logger.LogDebug(ex, "Unable to join SSDP multicast group on interface {Address}", address);
            }
        }

        if (!joined)
        {
            client.JoinMulticastGroup(MulticastAddress);
        }

        client.MulticastLoopback = false;
    }

    private void LogSocketBinding(UdpClient client)
    {
        var reuseAddress = Convert.ToInt32(client.Client.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress)) != 0;
        _logger.LogInformation("SSDP socket bound to {LocalEndpoint}; ExclusiveAddressUse={ExclusiveAddressUse}, ReuseAddress={ReuseAddress}", client.Client.LocalEndPoint, client.ExclusiveAddressUse, reuseAddress);
    }

    private static List<IPAddress> GetLocalIpv4Addresses()
    {
        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus == OperationalStatus.Up)
            .SelectMany(network => network.GetIPProperties().UnicastAddresses)
            .Select(unicast => unicast.Address)
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
            .Distinct()
            .ToList();
        if (!addresses.Contains(IPAddress.Loopback))
        {
            addresses.Add(IPAddress.Loopback);
        }

        return addresses;
    }

    private static async Task SendNotificationsAsync(UdpClient client, HdHomeRunAdvertisedDevice device, bool alive, CancellationToken cancellationToken)
    {
        foreach (var target in new[] { SsdpDiscoveryProtocol.RootDeviceTarget, SsdpDiscoveryProtocol.MediaServerTarget })
        {
            var message = SsdpDiscoveryProtocol.FormatNotification(target, device, alive);
            await client.SendAsync(Encoding.UTF8.GetBytes(message), MulticastEndpoint, cancellationToken);
        }
    }

    private sealed record ResponseWindow(DateTimeOffset Start, int Count);
}
