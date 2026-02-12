using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Lineup.HDHomeRun.Device.Protocol;
using Microsoft.Extensions.Logging;

namespace Lineup.HDHomeRun.Device;

/// <summary>
/// High-level service for managing HDHomeRun devices.
/// Provides discovery, control, and streaming capabilities.
/// </summary>
public class HDHomeRunService : IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<HDHomeRunService> _logger;
    private HDHomeRunDiscovery? _discovery;
    private readonly Dictionary<uint, HDHomeRunDevice> _devices = new();

    /// <summary>
    /// Creates a new HDHomeRun service
    /// </summary>
    public HDHomeRunService(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<HDHomeRunService>();
    }

    /// <summary>
    /// Runs connectivity diagnostics and returns detailed results
    /// </summary>
    public async Task<DeviceDiagnostics> DiagnoseConnectivityAsync(
        string addressOrHostname,
        CancellationToken cancellationToken = default)
    {
        var diagnostics = new DeviceDiagnostics
        {
            InputAddress = addressOrHostname,
            Timestamp = DateTime.UtcNow
        };

        // Step 1: Resolve address
        IPAddress? ip = null;
        if (IPAddress.TryParse(addressOrHostname, out ip))
        {
            diagnostics.ResolvedIpAddress = ip.ToString();
            diagnostics.AddResult("DNS Resolution", true, "Input is already an IP address");
        }
        else
        {
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(addressOrHostname, cancellationToken);
                ip = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);

                if (ip != null)
                {
                    diagnostics.ResolvedIpAddress = ip.ToString();
                    diagnostics.AddResult("DNS Resolution", true, $"Resolved to {ip}");
                }
                else
                {
                    diagnostics.AddResult("DNS Resolution", false, "No IPv4 address found");
                    return diagnostics;
                }
            }
            catch (Exception ex)
            {
                diagnostics.AddResult("DNS Resolution", false, $"Failed: {ex.Message}");
                return diagnostics;
            }
        }

        // Step 2: Ping test
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(ip, 2000);
            diagnostics.AddResult("Ping", reply.Status == IPStatus.Success,
                reply.Status == IPStatus.Success
                    ? $"Success ({reply.RoundtripTime}ms)"
                    : $"Failed: {reply.Status}");
        }
        catch (Exception ex)
        {
            diagnostics.AddResult("Ping", false, $"Error: {ex.Message}");
        }

        // Step 3: HTTP API test (port 80)
        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var response = await httpClient.GetAsync($"http://{ip}/discover.json", cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                diagnostics.AddResult("HTTP API (port 80)", true, $"Success - {json.Length} bytes");
                diagnostics.HttpApiAvailable = true;
            }
            else
            {
                diagnostics.AddResult("HTTP API (port 80)", false, $"HTTP {(int)response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            diagnostics.AddResult("HTTP API (port 80)", false, $"Error: {ex.Message}");
        }

        // Step 4: TCP connection test (port 65001)
        try
        {
            using var tcpClient = new TcpClient();
            var connectTask = tcpClient.ConnectAsync(ip, HDHomeRunDiscovery.DiscoveryPort);
            if (await Task.WhenAny(connectTask, Task.Delay(3000, cancellationToken)) == connectTask)
            {
                diagnostics.AddResult("TCP Port 65001", true, "Connection successful");
                diagnostics.TcpPortOpen = true;
            }
            else
            {
                diagnostics.AddResult("TCP Port 65001", false, "Connection timeout");
            }
        }
        catch (SocketException ex)
        {
            diagnostics.AddResult("TCP Port 65001", false, $"Socket error: {ex.SocketErrorCode}");
        }
        catch (Exception ex)
        {
            diagnostics.AddResult("TCP Port 65001", false, $"Error: {ex.Message}");
        }

        // Step 5: UDP discovery test
        try
        {
            using var udpClient = new UdpClient();
            var packet = new HDHomeRunPacketBuilder()
                .AddTag(HDHomeRunTagType.DeviceType, (uint)HDHomeRunDeviceType.Wildcard)
                .AddTag(HDHomeRunTagType.DeviceId, HDHomeRunDeviceId.Wildcard)
                .Build(HDHomeRunPacketType.DiscoverRequest);

            diagnostics.UdpPacketSent = packet;
            diagnostics.AddResult("UDP Packet Built", true, $"{packet.Length} bytes: {FormatHexDump(packet)}");

            var endpoint = new IPEndPoint(ip, HDHomeRunDiscovery.DiscoveryPort);
            await udpClient.SendAsync(packet, packet.Length, endpoint);

            udpClient.Client.ReceiveTimeout = 3000;

            using var cts = new CancellationTokenSource(3000);
            try
            {
                var result = await udpClient.ReceiveAsync(cts.Token);
                diagnostics.UdpPacketReceived = result.Buffer;
                diagnostics.AddResult("UDP Discovery", true, $"Received {result.Buffer.Length} bytes: {FormatHexDump(result.Buffer, 32)}");
                diagnostics.UdpDiscoveryWorks = true;
            }
            catch (OperationCanceledException)
            {
                diagnostics.AddResult("UDP Discovery", false, "No response (timeout)");
            }
        }
        catch (SocketException ex)
        {
            diagnostics.AddResult("UDP Discovery", false, $"Socket error: {ex.SocketErrorCode}");
        }
        catch (Exception ex)
        {
            diagnostics.AddResult("UDP Discovery", false, $"Error: {ex.Message}");
        }

        // Step 6: TCP Control protocol test with detailed packet logging
        if (diagnostics.TcpPortOpen)
        {
            try
            {
                // Build a simple get request for /sys/model
                var getPacket = new HDHomeRunPacketBuilder()
                    .AddTag(HDHomeRunTagType.GetSetName, "/sys/model\0")
                    .Build(HDHomeRunPacketType.GetSetRequest);

                diagnostics.TcpPacketSent = getPacket;
                diagnostics.AddResult("TCP Packet Built", true, $"{getPacket.Length} bytes: {FormatHexDump(getPacket)}");

                using var tcpClient = new TcpClient();
                await tcpClient.ConnectAsync(ip, HDHomeRunDiscovery.DiscoveryPort);

                var stream = tcpClient.GetStream();
                stream.ReadTimeout = 5000;
                stream.WriteTimeout = 5000;

                // Send the packet
                await stream.WriteAsync(getPacket);
                await stream.FlushAsync();
                diagnostics.AddResult("TCP Packet Sent", true, "Packet sent successfully");

                // Try to receive response
                var buffer = new byte[1500];
                try
                {
                    using var readCts = new CancellationTokenSource(5000);
                    var bytesRead = await stream.ReadAsync(buffer.AsMemory(), readCts.Token);

                    if (bytesRead > 0)
                    {
                        var response = buffer[..bytesRead];
                        diagnostics.TcpPacketReceived = response;
                        diagnostics.AddResult("TCP Response", true, $"Received {bytesRead} bytes: {FormatHexDump(response)}");

                        // Try to parse the response
                        var reader = new HDHomeRunPacketReader(response);
                        if (reader.IsValid)
                        {
                            diagnostics.AddResult("TCP Packet Parse", true, $"Valid packet, type={reader.PacketType}, payload={reader.PayloadLength} bytes");

                            while (reader.TryReadTag(out var tag, out var value))
                            {
                                var valueStr = tag == HDHomeRunTagType.GetSetValue || tag == HDHomeRunTagType.ErrorMessage
                                    ? HDHomeRunPacketReader.ReadString(value)
                                    : FormatHexDump(value.ToArray(), 16);
                                diagnostics.AddResult($"  Tag {tag}", true, valueStr);

                                if (tag == HDHomeRunTagType.GetSetValue)
                                {
                                    diagnostics.DeviceModel = HDHomeRunPacketReader.ReadString(value);
                                }
                            }
                            diagnostics.ControlProtocolWorks = true;
                        }
                        else
                        {
                            diagnostics.AddResult("TCP Packet Parse", false, "Invalid packet (bad CRC or format)");
                        }
                    }
                    else
                    {
                        diagnostics.AddResult("TCP Response", false, "Connection closed (0 bytes)");
                    }
                }
                catch (OperationCanceledException)
                {
                    diagnostics.AddResult("TCP Response", false, "Read timeout");
                }
                catch (IOException ex)
                {
                    diagnostics.AddResult("TCP Response", false, $"IO error: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                diagnostics.AddResult("TCP Control Protocol", false, $"Error: {ex.Message}");
            }
        }

        return diagnostics;
    }

    private static string FormatHexDump(byte[] data, int maxBytes = 64)
    {
        if (data == null || data.Length == 0) return "(empty)";

        var take = Math.Min(data.Length, maxBytes);
        var hex = BitConverter.ToString(data, 0, take).Replace("-", " ");

        if (data.Length > maxBytes)
        {
            hex += $" ... (+{data.Length - maxBytes} more)";
        }

        return hex;
    }

    /// <summary>
    /// Discovers all HDHomeRun devices on the network
    /// </summary>
    public async Task<List<HDHomeRunDiscoveredDevice>> DiscoverDevicesAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        _discovery ??= new HDHomeRunDiscovery(_loggerFactory.CreateLogger<HDHomeRunDiscovery>());
        return await _discovery.DiscoverAllAsync(timeout, cancellationToken);
    }

    /// <summary>
    /// Gets or creates a device instance for the specified discovered device
    /// </summary>
    public HDHomeRunDevice GetDevice(HDHomeRunDiscoveredDevice deviceInfo)
    {
        if (!_devices.TryGetValue(deviceInfo.DeviceId, out var device))
        {
            device = new HDHomeRunDevice(deviceInfo, _loggerFactory);
            _devices[deviceInfo.DeviceId] = device;
        }
        return device;
    }

    /// <summary>
    /// Gets device information by IP address or hostname.
    /// Uses HTTP API as the primary method (most reliable for modern devices),
    /// with fallbacks to native protocols for older devices.
    /// </summary>
    public async Task<HDHomeRunDevice?> GetDeviceByIpAsync(
        string addressOrHostname,
        CancellationToken cancellationToken = default)
    {
        // Try to parse as IP address first
        IPAddress? ip = null;
        if (!IPAddress.TryParse(addressOrHostname, out ip))
        {
            // Try to resolve hostname
            try
            {
                _logger.LogDebug("Resolving hostname: {Hostname}", addressOrHostname);
                var addresses = await Dns.GetHostAddressesAsync(addressOrHostname, cancellationToken);
                ip = addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);

                if (ip == null)
                {
                    _logger.LogWarning("Could not resolve hostname to IPv4 address: {Hostname}", addressOrHostname);
                    return null;
                }

                _logger.LogDebug("Resolved {Hostname} to {IpAddress}", addressOrHostname, ip);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resolve hostname: {Hostname}", addressOrHostname);
                return null;
            }
        }

        // PRIMARY: Try HTTP API first (works reliably on all modern devices like FLEX, CONNECT, etc.)
        _logger.LogDebug("Trying HTTP API connection to {IpAddress}", ip);
        var device = await ConnectViaHttpAsync(ip, cancellationToken);
        if (device != null)
        {
            return device;
        }

        // FALLBACK 1: Try UDP discovery (for older devices)
        _logger.LogDebug("HTTP API failed, trying UDP discovery for {IpAddress}", ip);
        _discovery ??= new HDHomeRunDiscovery(_loggerFactory.CreateLogger<HDHomeRunDiscovery>());
        var deviceInfo = await _discovery.DiscoverByIpAsync(ip, TimeSpan.FromSeconds(2), cancellationToken);

        if (deviceInfo != null)
        {
            return GetDevice(deviceInfo);
        }

        // FALLBACK 2: Try direct TCP connection (for very old devices)
        _logger.LogDebug("UDP discovery failed, trying direct TCP connection to {IpAddress}", ip);
        return await ConnectDirectAsync(ip, cancellationToken);
    }

    /// <summary>
    /// Connects to a device using only the HTTP API.
    /// This is the most reliable method for modern HDHomeRun devices.
    /// </summary>
    public async Task<HDHomeRunDevice?> ConnectViaHttpAsync(
        IPAddress ipAddress,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var httpControl = new HDHomeRunHttpControl(ipAddress, _loggerFactory.CreateLogger<HDHomeRunHttpControl>());
            var discover = await httpControl.DiscoverAsync(cancellationToken);

            if (discover == null)
            {
                _logger.LogWarning("HTTP API discover failed for {IpAddress}", ipAddress);
                httpControl.Dispose();
                return null;
            }

            // Parse device ID
            uint deviceId = 0;
            if (!string.IsNullOrEmpty(discover.DeviceID))
            {
                uint.TryParse(discover.DeviceID, System.Globalization.NumberStyles.HexNumber, null, out deviceId);
            }
            if (deviceId == 0)
            {
                var ipBytes = ipAddress.GetAddressBytes();
                deviceId = BitConverter.ToUInt32(ipBytes, 0);
            }

            var deviceInfo = new HDHomeRunDiscoveredDevice
            {
                IpAddress = ipAddress,
                DeviceId = deviceId,
                DeviceType = HDHomeRunDeviceType.Tuner,
                TunerCount = discover.TunerCount,
                BaseUrl = discover.BaseURL ?? $"http://{ipAddress}",
                LineupUrl = discover.LineupURL
            };

            httpControl.Dispose();

            _logger.LogInformation("Connected via HTTP API to {IpAddress}: {Model}",
                ipAddress, discover.ModelNumber);
            return GetDevice(deviceInfo);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HTTP API connection to {IpAddress} failed", ipAddress);
            return null;
        }
    }

    /// <summary>
    /// Connects directly to a device via TCP without UDP discovery.
    /// Useful when UDP is blocked by firewalls.
    /// </summary>
    public async Task<HDHomeRunDevice?> ConnectDirectAsync(
        IPAddress ipAddress,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Create a minimal device info for direct connection
            var control = new HDHomeRunControl(ipAddress, _loggerFactory.CreateLogger<HDHomeRunControl>());
            await control.ConnectAsync(cancellationToken);

            // Get device info via control protocol
            var model = await control.GetModelAsync(cancellationToken);
            var deviceIdStr = await control.GetAsync("/sys/dvb/dvbc/id", cancellationToken);

            // Try to get tuner count
            int tunerCount = 0;
            for (int i = 0; i < 8; i++)
            {
                try
                {
                    var status = await control.GetAsync($"/tuner{i}/status", cancellationToken);
                    if (status == null) break;
                    tunerCount++;
                }
                catch
                {
                    break;
                }
            }

            if (tunerCount == 0) tunerCount = 2; // Default assumption

            // Parse device ID or generate one from IP
            uint deviceId = 0;
            if (!string.IsNullOrEmpty(deviceIdStr))
            {
                uint.TryParse(deviceIdStr.Replace("-", ""), System.Globalization.NumberStyles.HexNumber, null, out deviceId);
            }
            if (deviceId == 0)
            {
                // Generate a pseudo-ID from IP address
                var ipBytes = ipAddress.GetAddressBytes();
                deviceId = BitConverter.ToUInt32(ipBytes, 0);
            }

            var deviceInfo = new HDHomeRunDiscoveredDevice
            {
                IpAddress = ipAddress,
                DeviceId = deviceId,
                DeviceType = HDHomeRunDeviceType.Tuner,
                TunerCount = tunerCount,
                BaseUrl = $"http://{ipAddress}"
            };

            control.Dispose();

            _logger.LogInformation("Connected directly to device at {IpAddress}: {Model}", ipAddress, model);
            return GetDevice(deviceInfo);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Direct TCP connection to {IpAddress} failed", ipAddress);
            return null;
        }
    }

    /// <summary>
    /// Gets comprehensive status for all tuners on a device
    /// </summary>
    public async Task<List<TunerStatus>> GetAllTunerStatusAsync(
        HDHomeRunDevice device,
        CancellationToken cancellationToken = default)
    {
        var statuses = new List<TunerStatus>();

        for (int i = 0; i < device.DeviceInfo.TunerCount; i++)
        {
            try
            {
                var status = await device.GetTunerStatusAsync(i, cancellationToken);
                statuses.Add(status);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get status for tuner {TunerIndex}", i);
            }
        }

        return statuses;
    }

    public void Dispose()
    {
        foreach (var device in _devices.Values)
        {
            device.Dispose();
        }
        _devices.Clear();
        _discovery?.Dispose();
    }
}

/// <summary>
/// Results of device connectivity diagnostics
/// </summary>
public class DeviceDiagnostics
{
    public string InputAddress { get; set; } = "";
    public string? ResolvedIpAddress { get; set; }
    public DateTime Timestamp { get; set; }
    public bool HttpApiAvailable { get; set; }
    public bool TcpPortOpen { get; set; }
    public bool UdpDiscoveryWorks { get; set; }
    public bool ControlProtocolWorks { get; set; }
    public string? DeviceModel { get; set; }
    public List<DiagnosticResult> Results { get; } = [];

    // Packet dumps for debugging
    public byte[]? UdpPacketSent { get; set; }
    public byte[]? UdpPacketReceived { get; set; }
    public byte[]? TcpPacketSent { get; set; }
    public byte[]? TcpPacketReceived { get; set; }

    public void AddResult(string test, bool success, string message)
    {
        Results.Add(new DiagnosticResult { Test = test, Success = success, Message = message });
    }

    public bool IsFullyConnectable => HttpApiAvailable && TcpPortOpen && ControlProtocolWorks;

    public override string ToString()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Diagnostics for {InputAddress} ({ResolvedIpAddress ?? "unresolved"})");
        sb.AppendLine($"Timestamp: {Timestamp:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        foreach (var result in Results)
        {
            var status = result.Success ? "Ã¢Å“â€œ" : "Ã¢Å“â€”";
            sb.AppendLine($"  [{status}] {result.Test}: {result.Message}");
        }
        sb.AppendLine();
        sb.AppendLine($"Summary: HTTP={HttpApiAvailable}, TCP={TcpPortOpen}, UDP={UdpDiscoveryWorks}, Control={ControlProtocolWorks}");
        return sb.ToString();
    }

    public string GetPacketDumps()
    {
        var sb = new System.Text.StringBuilder();

        if (UdpPacketSent != null)
        {
            sb.AppendLine("UDP Packet Sent:");
            sb.AppendLine(FormatHexDumpFull(UdpPacketSent));
            sb.AppendLine();
        }

        if (UdpPacketReceived != null)
        {
            sb.AppendLine("UDP Packet Received:");
            sb.AppendLine(FormatHexDumpFull(UdpPacketReceived));
            sb.AppendLine();
        }

        if (TcpPacketSent != null)
        {
            sb.AppendLine("TCP Packet Sent:");
            sb.AppendLine(FormatHexDumpFull(TcpPacketSent));
            sb.AppendLine();
        }

        if (TcpPacketReceived != null)
        {
            sb.AppendLine("TCP Packet Received:");
            sb.AppendLine(FormatHexDumpFull(TcpPacketReceived));
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string FormatHexDumpFull(byte[] data)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < data.Length; i += 16)
        {
            sb.Append($"  {i:X4}: ");

            // Hex bytes
            for (int j = 0; j < 16; j++)
            {
                if (i + j < data.Length)
                    sb.Append($"{data[i + j]:X2} ");
                else
                    sb.Append("   ");

                if (j == 7) sb.Append(' ');
            }

            sb.Append(" |");

            // ASCII
            for (int j = 0; j < 16 && i + j < data.Length; j++)
            {
                var c = (char)data[i + j];
                sb.Append(c is >= ' ' and <= '~' ? c : '.');
            }

            sb.AppendLine("|");
        }
        return sb.ToString();
    }
}

public record DiagnosticResult
{
    public required string Test { get; init; }
    public required bool Success { get; init; }
    public required string Message { get; init; }
}
