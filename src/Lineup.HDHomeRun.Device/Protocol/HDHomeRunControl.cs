using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Lineup.HDHomeRun.Device.Protocol;

/// <summary>
/// Controls an HDHomeRun device using the control protocol.
/// 
/// The control protocol uses TCP on the same port as discovery (65001).
/// It allows getting and setting device variables like tuner settings.
/// </summary>
public class HDHomeRunControl : IDisposable
{
    private readonly ILogger<HDHomeRunControl> _logger;
    private readonly IPEndPoint _endpoint;
    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private uint _lockKey;

    /// <summary>
    /// HDHomeRun control port (same as discovery)
    /// </summary>
    public const int ControlPort = 65001;

    /// <summary>
    /// Default timeout for control operations
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets whether the connection is established
    /// </summary>
    public bool IsConnected => _tcpClient?.Connected ?? false;

    /// <summary>
    /// Gets the device IP address
    /// </summary>
    public IPAddress DeviceAddress => _endpoint.Address;

    /// <summary>
    /// Creates a new control client for the specified device
    /// </summary>
    /// <param name="deviceAddress">IP address of the HDHomeRun device</param>
    /// <param name="logger">Logger instance</param>
    public HDHomeRunControl(IPAddress deviceAddress, ILogger<HDHomeRunControl> logger)
    {
        _logger = logger;
        _endpoint = new IPEndPoint(deviceAddress, ControlPort);
    }

    /// <summary>
    /// Connects to the device
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (IsConnected)
            return;

        _tcpClient = new TcpClient();
        _tcpClient.ReceiveTimeout = (int)DefaultTimeout.TotalMilliseconds;
        _tcpClient.SendTimeout = (int)DefaultTimeout.TotalMilliseconds;

        _logger.LogDebug("Connecting to HDHomeRun at {Endpoint}", _endpoint);
        await _tcpClient.ConnectAsync(_endpoint, cancellationToken);
        _stream = _tcpClient.GetStream();

        _logger.LogInformation("Connected to HDHomeRun at {Endpoint}", _endpoint);
    }

    /// <summary>
    /// Gets a device variable value
    /// </summary>
    /// <param name="name">Variable name (e.g., "/sys/model", "/tuner0/channel")</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Variable value or null if not found</returns>
    public async Task<string?> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);

        var packet = new HDHomeRunPacketBuilder()
            .AddTag(HDHomeRunTagType.GetSetName, name + "\0")
            .Build(HDHomeRunPacketType.GetSetRequest);

        _logger.LogDebug("Getting variable: {Name}", name);

        var response = await SendAndReceiveAsync(packet, cancellationToken);
        return ParseGetSetResponse(response, out var error) ?? (error != null ? throw new HDHomeRunException(error) : null);
    }

    /// <summary>
    /// Sets a device variable value
    /// </summary>
    /// <param name="name">Variable name (e.g., "/tuner0/channel")</param>
    /// <param name="value">Value to set</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The value after setting (may differ from input)</returns>
    public async Task<string?> SetAsync(string name, string value, CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);

        var builder = new HDHomeRunPacketBuilder()
            .AddTag(HDHomeRunTagType.GetSetName, name + "\0")
            .AddTag(HDHomeRunTagType.GetSetValue, value + "\0");

        if (_lockKey != 0)
        {
            builder.AddTag(HDHomeRunTagType.GetSetLockkey, _lockKey);
        }

        var packet = builder.Build(HDHomeRunPacketType.GetSetRequest);

        _logger.LogDebug("Setting variable: {Name} = {Value}", name, value);

        var response = await SendAndReceiveAsync(packet, cancellationToken);
        return ParseGetSetResponse(response, out var error) ?? (error != null ? throw new HDHomeRunException(error) : null);
    }

    /// <summary>
    /// Acquires a lock on a tuner
    /// </summary>
    /// <param name="tunerIndex">Tuner index (0, 1, etc.)</param>
    /// <param name="force">Force lock even if already locked by another client</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task<bool> LockTunerAsync(int tunerIndex, bool force = false, CancellationToken cancellationToken = default)
    {
        // Generate a random lock key
        _lockKey = (uint)Random.Shared.Next(1, int.MaxValue);

        var lockValue = force ? $"force:{_lockKey}" : $"{_lockKey}";

        try
        {
            await SetAsync($"/tuner{tunerIndex}/lockkey", lockValue, cancellationToken);
            _logger.LogInformation("Acquired lock on tuner {TunerIndex}", tunerIndex);
            return true;
        }
        catch (HDHomeRunException ex)
        {
            _logger.LogWarning("Failed to acquire lock on tuner {TunerIndex}: {Error}", tunerIndex, ex.Message);
            _lockKey = 0;
            return false;
        }
    }

    /// <summary>
    /// Releases a lock on a tuner
    /// </summary>
    public async Task ReleaseTunerLockAsync(int tunerIndex, CancellationToken cancellationToken = default)
    {
        if (_lockKey == 0)
            return;

        try
        {
            await SetAsync($"/tuner{tunerIndex}/lockkey", "none", cancellationToken);
            _logger.LogInformation("Released lock on tuner {TunerIndex}", tunerIndex);
        }
        catch (HDHomeRunException ex)
        {
            _logger.LogWarning("Failed to release lock on tuner {TunerIndex}: {Error}", tunerIndex, ex.Message);
        }
        finally
        {
            _lockKey = 0;
        }
    }

    /// <summary>
    /// Gets the device model
    /// </summary>
    public Task<string?> GetModelAsync(CancellationToken cancellationToken = default)
        => GetAsync("/sys/model", cancellationToken);

    /// <summary>
    /// Gets the device firmware version
    /// </summary>
    public Task<string?> GetFirmwareVersionAsync(CancellationToken cancellationToken = default)
        => GetAsync("/sys/version", cancellationToken);

    /// <summary>
    /// Gets the device hardware revision
    /// </summary>
    public Task<string?> GetHardwareRevisionAsync(CancellationToken cancellationToken = default)
        => GetAsync("/sys/hwmodel", cancellationToken);

    /// <summary>
    /// Gets the tuner channel
    /// </summary>
    public Task<string?> GetTunerChannelAsync(int tunerIndex, CancellationToken cancellationToken = default)
        => GetAsync($"/tuner{tunerIndex}/channel", cancellationToken);

    /// <summary>
    /// Sets the tuner channel
    /// </summary>
    public Task<string?> SetTunerChannelAsync(int tunerIndex, string channel, CancellationToken cancellationToken = default)
        => SetAsync($"/tuner{tunerIndex}/channel", channel, cancellationToken);

    /// <summary>
    /// Gets the tuner status
    /// </summary>
    public Task<string?> GetTunerStatusAsync(int tunerIndex, CancellationToken cancellationToken = default)
        => GetAsync($"/tuner{tunerIndex}/status", cancellationToken);

    /// <summary>
    /// Gets the tuner stream info
    /// </summary>
    public Task<string?> GetTunerStreamInfoAsync(int tunerIndex, CancellationToken cancellationToken = default)
        => GetAsync($"/tuner{tunerIndex}/streaminfo", cancellationToken);

    /// <summary>
    /// Gets the tuner virtual channel
    /// </summary>
    public Task<string?> GetTunerVirtualChannelAsync(int tunerIndex, CancellationToken cancellationToken = default)
        => GetAsync($"/tuner{tunerIndex}/vchannel", cancellationToken);

    /// <summary>
    /// Sets the tuner virtual channel
    /// </summary>
    public Task<string?> SetTunerVirtualChannelAsync(int tunerIndex, string channel, CancellationToken cancellationToken = default)
        => SetAsync($"/tuner{tunerIndex}/vchannel", channel, cancellationToken);

    /// <summary>
    /// Gets the tuner target (streaming destination)
    /// </summary>
    public Task<string?> GetTunerTargetAsync(int tunerIndex, CancellationToken cancellationToken = default)
        => GetAsync($"/tuner{tunerIndex}/target", cancellationToken);

    /// <summary>
    /// Sets the tuner target (streaming destination)
    /// </summary>
    /// <param name="tunerIndex">Tuner index</param>
    /// <param name="target">Target URI (e.g., "udp://192.168.1.100:5000" or "none")</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public Task<string?> SetTunerTargetAsync(int tunerIndex, string target, CancellationToken cancellationToken = default)
        => SetAsync($"/tuner{tunerIndex}/target", target, cancellationToken);

    /// <summary>
    /// Gets the lineup ID
    /// </summary>
    public Task<string?> GetLineupAsync(CancellationToken cancellationToken = default)
        => GetAsync("/lineup/location", cancellationToken);

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (!IsConnected)
        {
            await ConnectAsync(cancellationToken);
        }
    }

    private async Task<byte[]> SendAndReceiveAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (_stream == null)
            throw new InvalidOperationException("Not connected");

        await _stream.WriteAsync(packet, cancellationToken);
        await _stream.FlushAsync(cancellationToken);

        var buffer = new byte[HDHomeRunPacketBuilder.MaxPacketSize];
        var bytesRead = await _stream.ReadAsync(buffer, cancellationToken);

        if (bytesRead == 0)
            throw new HDHomeRunException("Connection closed by device");

        return buffer[..bytesRead];
    }

    private string? ParseGetSetResponse(byte[] data, out string? error)
    {
        error = null;
        var reader = new HDHomeRunPacketReader(data);

        if (!reader.IsValid)
        {
            error = "Invalid response packet";
            return null;
        }

        if (reader.PacketType != HDHomeRunPacketType.GetSetReply)
        {
            error = $"Unexpected response type: {reader.PacketType}";
            return null;
        }

        string? value = null;

        while (reader.TryReadTag(out var tag, out var tagValue))
        {
            switch (tag)
            {
                case HDHomeRunTagType.GetSetValue:
                    value = HDHomeRunPacketReader.ReadString(tagValue);
                    break;
                case HDHomeRunTagType.ErrorMessage:
                    error = HDHomeRunPacketReader.ReadString(tagValue);
                    break;
            }
        }

        return value;
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _tcpClient?.Dispose();
    }
}

/// <summary>
/// Exception thrown when an HDHomeRun operation fails
/// </summary>
public class HDHomeRunException : Exception
{
    public HDHomeRunException(string message) : base(message) { }
    public HDHomeRunException(string message, Exception innerException) : base(message, innerException) { }
}
