using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Lineup.SiliconDust.HDHomeRun.Device.Protocol;
using Lineup.SiliconDust.HDHomeRun.VirtualTuner;
using Lineup.VirtualOutputs.Hosting;

const int ssdpPort = 1900;
const int maximumTrackedSources = 1024;
const int requestsPerSourcePerSecond = 10;
var bootstrap = await VirtualOutputWorkerBootstrapReader.ReadAsync(cancellationToken: CancellationToken.None);
if (!string.Equals(bootstrap.VirtualDevice.Adapter.Kind, "hdhomerun-discovery", StringComparison.Ordinal))
{
    throw new InvalidOperationException($"HDHomeRun discovery worker cannot host adapter kind '{bootstrap.VirtualDevice.Adapter.Kind}'.");
}

var advertisements = bootstrap.Advertisements
    .Where(advertisement => string.Equals(advertisement.AdapterKind, HdHomeRunVirtualTunerDriver.AdapterKind, StringComparison.OrdinalIgnoreCase))
    .Select(advertisement =>
    {
        var metadata = advertisement.Metadata.Deserialize<HdHomeRunDiscoveryAdvertisementMetadata>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException($"Advertisement for {advertisement.VirtualDeviceId} has invalid HDHomeRun metadata.");
        return new HdHomeRunAdvertisedDevice
        {
            DeviceId = uint.Parse(metadata.DeviceId, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            DeviceAuth = metadata.DeviceAuth,
            TunerCount = checked((byte)Math.Clamp(advertisement.TunerCount ?? 0, 0, byte.MaxValue)),
            BaseUri = advertisement.PublicBaseUri
        };
    })
    .ToArray();
if (advertisements.Length == 0)
{
    throw new InvalidDataException("The HDHomeRun discovery worker did not receive any advertisement snapshots.");
}

using var shutdown = new CancellationTokenSource();
var session = new VirtualOutputWorkerSession(bootstrap);
await session.RegisterAsync(shutdown.Token);
var responseWindows = new ConcurrentDictionary<IPAddress, ResponseWindow>();
var leaseTask = session.RunLeaseLoopAsync(
    () => (Ready: true, ActiveConnections: 0),
    shutdown.Cancel,
    shutdown.Token);
var discoveryTask = RunHdHomeRunDiscoveryAsync(advertisements, responseWindows, shutdown.Token);
var ssdpTask = RunSsdpAsync(advertisements, responseWindows, shutdown.Token);
await Task.WhenAny(leaseTask, discoveryTask, ssdpTask);
shutdown.Cancel();
try
{
    await Task.WhenAll(leaseTask, discoveryTask, ssdpTask);
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
}

static async Task RunHdHomeRunDiscoveryAsync(
    IReadOnlyList<HdHomeRunAdvertisedDevice> advertisements,
    ConcurrentDictionary<IPAddress, ResponseWindow> responseWindows,
    CancellationToken cancellationToken)
{
    using var client = new UdpClient(AddressFamily.InterNetwork);
    client.ExclusiveAddressUse = false;
    client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
    client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
    client.Client.Bind(new IPEndPoint(IPAddress.Any, HDHomeRunDiscovery.DiscoveryPort));
    while (!cancellationToken.IsCancellationRequested)
    {
        var request = await client.ReceiveAsync(cancellationToken);
        if (!HdHomeRunDiscoveryProtocol.IsDiscoveryRequest(request.Buffer) ||
            !TryAcquireResponsePermit(request.RemoteEndPoint.Address, responseWindows))
        {
            continue;
        }
        var respondedDeviceIds = new List<string>();
        foreach (var advertisement in advertisements)
        {
            if (HdHomeRunDiscoveryProtocol.TryCreateReply(request.Buffer, advertisement, out var reply))
            {
                await client.SendAsync(reply!, request.RemoteEndPoint, cancellationToken);
                respondedDeviceIds.Add(advertisement.DeviceId.ToString("X8", CultureInfo.InvariantCulture));
            }
        }
        Console.WriteLine(
            $"HDHomeRun discovery request received from {request.RemoteEndPoint}; sent {respondedDeviceIds.Count} response(s) for device(s): {FormatDeviceIds(respondedDeviceIds)}.");
    }
}

static async Task RunSsdpAsync(
    IReadOnlyList<HdHomeRunAdvertisedDevice> advertisements,
    ConcurrentDictionary<IPAddress, ResponseWindow> responseWindows,
    CancellationToken cancellationToken)
{
    using var client = new UdpClient(AddressFamily.InterNetwork);
    client.ExclusiveAddressUse = false;
    client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
    client.Client.Bind(new IPEndPoint(IPAddress.Any, ssdpPort));
    client.JoinMulticastGroup(IPAddress.Parse("239.255.255.250"));
    client.MulticastLoopback = false;
    await SendSsdpNotificationsAsync(client, advertisements, alive: true, cancellationToken);
    var receiveTask = ReceiveSsdpAsync(client, advertisements, responseWindows, cancellationToken);
    var aliveTask = SendAliveLoopAsync(client, advertisements, cancellationToken);
    try
    {
        await Task.WhenAll(receiveTask, aliveTask);
    }
    finally
    {
        try
        {
            await SendSsdpNotificationsAsync(client, advertisements, alive: false, CancellationToken.None);
        }
        catch (SocketException)
        {
        }
    }
}

static async Task ReceiveSsdpAsync(
    UdpClient client,
    IReadOnlyList<HdHomeRunAdvertisedDevice> advertisements,
    ConcurrentDictionary<IPAddress, ResponseWindow> responseWindows,
    CancellationToken cancellationToken)
{
    while (!cancellationToken.IsCancellationRequested)
    {
        var request = await client.ReceiveAsync(cancellationToken);
        if (!TryAcquireResponsePermit(request.RemoteEndPoint.Address, responseWindows))
        {
            continue;
        }
        var text = Encoding.ASCII.GetString(request.Buffer);
        if (!SsdpDiscoveryProtocol.TryParseSearch(text, out var requestedTarget))
        {
            continue;
        }
        var responseCount = 0;
        var respondedDeviceIds = new List<string>();
        foreach (var advertisement in advertisements)
        {
            var responses = SsdpDiscoveryProtocol.CreateSearchResponses(text, advertisement);
            foreach (var response in responses)
            {
                await client.SendAsync(Encoding.ASCII.GetBytes(response), request.RemoteEndPoint, cancellationToken);
                responseCount++;
            }
            if (responses.Count > 0)
            {
                respondedDeviceIds.Add(advertisement.DeviceId.ToString("X8", CultureInfo.InvariantCulture));
            }
        }
        Console.WriteLine(
            $"SSDP discovery request received from {request.RemoteEndPoint} for '{requestedTarget}'; sent {responseCount} response(s) for device(s): {FormatDeviceIds(respondedDeviceIds)}.");
    }
}

static async Task SendAliveLoopAsync(
    UdpClient client,
    IReadOnlyList<HdHomeRunAdvertisedDevice> advertisements,
    CancellationToken cancellationToken)
{
    while (!cancellationToken.IsCancellationRequested)
    {
        await Task.Delay(TimeSpan.FromMinutes(15), cancellationToken);
        await SendSsdpNotificationsAsync(client, advertisements, alive: true, cancellationToken);
    }
}

static async Task SendSsdpNotificationsAsync(
    UdpClient client,
    IReadOnlyList<HdHomeRunAdvertisedDevice> advertisements,
    bool alive,
    CancellationToken cancellationToken)
{
    var endpoint = new IPEndPoint(IPAddress.Parse("239.255.255.250"), ssdpPort);
    foreach (var advertisement in advertisements)
    {
        foreach (var target in new[] { SsdpDiscoveryProtocol.RootDeviceTarget, SsdpDiscoveryProtocol.MediaServerTarget })
        {
            var payload = Encoding.ASCII.GetBytes(SsdpDiscoveryProtocol.FormatNotification(target, advertisement, alive));
            await client.SendAsync(payload, endpoint, cancellationToken);
        }
    }
}

static bool TryAcquireResponsePermit(
    IPAddress source,
    ConcurrentDictionary<IPAddress, ResponseWindow> responseWindows)
{
    var now = DateTimeOffset.UtcNow;
    if (responseWindows.Count >= maximumTrackedSources && !responseWindows.ContainsKey(source))
    {
        var oldest = responseWindows.MinBy(pair => pair.Value.Start).Key;
        responseWindows.TryRemove(oldest, out _);
    }
    var updated = responseWindows.AddOrUpdate(
        source,
        _ => new ResponseWindow(now, 1),
        (_, current) => now - current.Start >= TimeSpan.FromSeconds(1)
            ? new ResponseWindow(now, 1)
            : current with { Count = current.Count + 1 });
    return updated.Count <= requestsPerSourcePerSecond;
}

static string FormatDeviceIds(IReadOnlyCollection<string> deviceIds) =>
    deviceIds.Count == 0 ? "none" : string.Join(", ", deviceIds);

internal sealed record ResponseWindow(DateTimeOffset Start, int Count);
