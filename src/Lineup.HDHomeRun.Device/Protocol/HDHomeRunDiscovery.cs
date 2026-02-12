using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Lineup.HDHomeRun.Device.Protocol;

/// <summary>
/// Discovers HDHomeRun devices on the local network using UDP broadcast.
/// 
/// The discovery protocol uses UDP port 65001 with broadcast messages.
/// Devices respond with their ID, type, and capabilities.
/// </summary>
public class HDHomeRunDiscovery : IDisposable
{
    private readonly ILogger<HDHomeRunDiscovery> _logger;
    private readonly UdpClient _udpClient;

    /// <summary>
    /// HDHomeRun discovery port
    /// </summary>
    public const int DiscoveryPort = 65001;

    /// <summary>
    /// Default timeout for discovery operations
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Creates a new HDHomeRun discovery client
    /// </summary>
    public HDHomeRunDiscovery(ILogger<HDHomeRunDiscovery> logger)
    {
        _logger = logger;
        _udpClient = new UdpClient();
        _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
        _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
    }

    /// <summary>
    /// Discovers all HDHomeRun devices on the network
    /// </summary>
    /// <param name="timeout">How long to wait for responses</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of discovered devices</returns>
    public async Task<List<HDHomeRunDiscoveredDevice>> DiscoverAllAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        return await DiscoverAsync(
            HDHomeRunDeviceType.Wildcard,
            HDHomeRunDeviceId.Wildcard,
            timeout ?? DefaultTimeout,
            cancellationToken);
    }

    /// <summary>
    /// Discovers HDHomeRun devices matching the specified criteria
    /// </summary>
    /// <param name="deviceType">Device type to find (use Wildcard for all)</param>
    /// <param name="deviceId">Device ID to find (use Wildcard for all)</param>
    /// <param name="timeout">How long to wait for responses</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of discovered devices</returns>
    public async Task<List<HDHomeRunDiscoveredDevice>> DiscoverAsync(
        HDHomeRunDeviceType deviceType,
        uint deviceId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var devices = new List<HDHomeRunDiscoveredDevice>();
        var seenDevices = new HashSet<uint>();

        // Build discovery packet
        var packet = new HDHomeRunPacketBuilder()
            .AddTag(HDHomeRunTagType.DeviceType, (uint)deviceType)
            .AddTag(HDHomeRunTagType.DeviceId, deviceId)
            .Build(HDHomeRunPacketType.DiscoverRequest);

        _logger.LogDebug("Sending discovery packet for DeviceType={DeviceType}, DeviceId={DeviceId:X8}",
            deviceType, deviceId);

        // Send broadcast
        var broadcastEndpoint = new IPEndPoint(IPAddress.Broadcast, DiscoveryPort);
        await _udpClient.SendAsync(packet, packet.Length, broadcastEndpoint);

        // Also send to multicast address used by some devices
        try
        {
            var multicastEndpoint = new IPEndPoint(IPAddress.Parse("224.0.0.1"), DiscoveryPort);
            await _udpClient.SendAsync(packet, packet.Length, multicastEndpoint);
        }
        catch
        {
            // Multicast may not be available on all systems
        }

        // Receive responses until timeout
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        while (!cts.Token.IsCancellationRequested)
        {
            try
            {
                var receiveTask = _udpClient.ReceiveAsync(cts.Token);
                var result = await receiveTask;

                var device = ParseDiscoveryResponse(result.Buffer, result.RemoteEndPoint);
                if (device != null && !seenDevices.Contains(device.DeviceId))
                {
                    seenDevices.Add(device.DeviceId);
                    devices.Add(device);
                    _logger.LogInformation("Discovered device: {Device}", device);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException ex)
            {
                _logger.LogDebug(ex, "Socket exception during discovery");
                break;
            }
        }

        _logger.LogInformation("Discovery complete. Found {Count} device(s)", devices.Count);
        return devices;
    }

    /// <summary>
    /// Discovers a specific device by ID
    /// </summary>
    public async Task<HDHomeRunDiscoveredDevice?> DiscoverByIdAsync(
        uint deviceId,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var devices = await DiscoverAsync(
            HDHomeRunDeviceType.Wildcard,
            deviceId,
            timeout ?? DefaultTimeout,
            cancellationToken);

        return devices.FirstOrDefault(d => d.DeviceId == deviceId);
    }

    /// <summary>
    /// Discovers a specific device by IP address
    /// </summary>
    public async Task<HDHomeRunDiscoveredDevice?> DiscoverByIpAsync(
        IPAddress ipAddress,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveTimeout = timeout ?? DefaultTimeout;

        _logger.LogDebug("Discovering device at {IpAddress} with timeout {Timeout}ms",
            ipAddress, effectiveTimeout.TotalMilliseconds);

        var packet = new HDHomeRunPacketBuilder()
            .AddTag(HDHomeRunTagType.DeviceType, (uint)HDHomeRunDeviceType.Wildcard)
            .AddTag(HDHomeRunTagType.DeviceId, HDHomeRunDeviceId.Wildcard)
            .Build(HDHomeRunPacketType.DiscoverRequest);

        var endpoint = new IPEndPoint(ipAddress, DiscoveryPort);

        // Use a dedicated UDP client for unicast to avoid issues with the shared broadcast client
        using var unicastClient = new UdpClient();
        unicastClient.Client.ReceiveTimeout = (int)effectiveTimeout.TotalMilliseconds;

        // Try up to 3 times with increasing delays
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                _logger.LogDebug("Discovery attempt {Attempt} to {IpAddress}", attempt, ipAddress);

                await unicastClient.SendAsync(packet, packet.Length, endpoint);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(effectiveTimeout);

                var result = await unicastClient.ReceiveAsync(cts.Token);

                _logger.LogDebug("Received {Bytes} bytes from {Endpoint}",
                    result.Buffer.Length, result.RemoteEndPoint);

                var device = ParseDiscoveryResponse(result.Buffer, result.RemoteEndPoint);
                if (device != null)
                {
                    _logger.LogInformation("Discovered device at {IpAddress}: {Device}", ipAddress, device);
                    return device;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("Discovery attempt {Attempt} timed out", attempt);
                // Timeout, try again
            }
            catch (SocketException ex)
            {
                _logger.LogDebug(ex, "Socket error during discovery attempt {Attempt}", attempt);
                // Socket error, try again
            }

            if (attempt < 3 && !cancellationToken.IsCancellationRequested)
            {
                // Small delay between retries
                await Task.Delay(100 * attempt, cancellationToken);
            }
        }

        _logger.LogWarning("Failed to discover device at {IpAddress} after 3 attempts", ipAddress);
        return null;
    }

    private HDHomeRunDiscoveredDevice? ParseDiscoveryResponse(byte[] data, IPEndPoint remoteEndPoint)
    {
        var reader = new HDHomeRunPacketReader(data);

        if (!reader.IsValid || reader.PacketType != HDHomeRunPacketType.DiscoverReply)
        {
            _logger.LogDebug("Invalid discovery response from {Endpoint}", remoteEndPoint);
            return null;
        }

        uint deviceId = 0;
        HDHomeRunDeviceType deviceType = HDHomeRunDeviceType.Tuner;
        int tunerCount = 0;
        string? baseUrl = null;
        string? lineupUrl = null;
        string? deviceAuth = null;
        string? storageId = null;
        string? storageUrl = null;

        while (reader.TryReadTag(out var tag, out var value))
        {
            switch (tag)
            {
                case HDHomeRunTagType.DeviceId:
                    deviceId = HDHomeRunPacketReader.ReadUInt32(value);
                    break;
                case HDHomeRunTagType.DeviceType:
                    deviceType = (HDHomeRunDeviceType)HDHomeRunPacketReader.ReadUInt32(value);
                    break;
                case HDHomeRunTagType.TunerCount:
                    if (value.Length >= 1)
                        tunerCount = value[0];
                    break;
                case HDHomeRunTagType.BaseUrl:
                    baseUrl = HDHomeRunPacketReader.ReadString(value);
                    break;
                case HDHomeRunTagType.LineupUrl:
                    lineupUrl = HDHomeRunPacketReader.ReadString(value);
                    break;
                case HDHomeRunTagType.DeviceAuthStr:
                    deviceAuth = HDHomeRunPacketReader.ReadString(value);
                    break;
                case HDHomeRunTagType.StorageId:
                    storageId = HDHomeRunPacketReader.ReadString(value);
                    break;
                case HDHomeRunTagType.StorageUrl:
                    storageUrl = HDHomeRunPacketReader.ReadString(value);
                    break;
            }
        }

        if (deviceId == 0)
        {
            _logger.LogDebug("Discovery response missing device ID from {Endpoint}", remoteEndPoint);
            return null;
        }

        return new HDHomeRunDiscoveredDevice
        {
            IpAddress = remoteEndPoint.Address,
            DeviceId = deviceId,
            DeviceType = deviceType,
            TunerCount = tunerCount,
            BaseUrl = baseUrl,
            LineupUrl = lineupUrl,
            DeviceAuth = deviceAuth,
            StorageId = storageId,
            StorageUrl = storageUrl
        };
    }

    public void Dispose()
    {
        _udpClient.Dispose();
    }
}
