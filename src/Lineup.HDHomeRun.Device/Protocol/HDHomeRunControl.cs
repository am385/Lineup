using System.Buffers.Binary;
using System.Collections.Concurrent;
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
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private readonly TimeSpan _exchangeTimeout;
    private readonly Func<CancellationToken, Task<Stream>>? _connectAsync;
    private readonly Func<uint> _createLockKey;
    private TcpClient? _tcpClient;
    private Stream? _stream;
    private readonly ConcurrentDictionary<int, uint> _tunerLockKeys = new();
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _tunerLockOperations = new();
    private int _disposed;

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
    public bool IsConnected => Volatile.Read(ref _disposed) == 0 && _stream != null;

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
        _exchangeTimeout = DefaultTimeout;
        _createLockKey = CreateRandomLockKey;
    }

    /// <summary>
    /// Creates a control client over a supplied stream for protocol testing.
    /// </summary>
    /// <param name="stream">The duplex protocol stream.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="exchangeTimeout">The timeout applied to each serialized exchange.</param>
    /// <param name="createLockKey">Optional lock-key generator.</param>
    internal HDHomeRunControl(Stream stream, ILogger<HDHomeRunControl> logger, TimeSpan? exchangeTimeout = null, Func<uint>? createLockKey = null)
    {
        _logger = logger;
        _endpoint = new IPEndPoint(IPAddress.None, ControlPort);
        _stream = stream;
        _exchangeTimeout = exchangeTimeout ?? DefaultTimeout;
        _createLockKey = createLockKey ?? CreateRandomLockKey;
    }

    /// <summary>
    /// Creates a control client with a supplied connection operation for protocol testing.
    /// </summary>
    /// <param name="connectAsync">The operation that establishes a duplex stream.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="exchangeTimeout">The timeout applied to connection and exchange operations.</param>
    internal HDHomeRunControl(Func<CancellationToken, Task<Stream>> connectAsync, ILogger<HDHomeRunControl> logger, TimeSpan? exchangeTimeout = null)
    {
        _logger = logger;
        _endpoint = new IPEndPoint(IPAddress.None, ControlPort);
        _connectAsync = connectAsync;
        _exchangeTimeout = exchangeTimeout ?? DefaultTimeout;
        _createLockKey = CreateRandomLockKey;
    }

    /// <summary>
    /// Connects to the device
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        using var connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectionCancellation.CancelAfter(_exchangeTimeout);
        var connectionToken = connectionCancellation.Token;
        var lockTaken = false;

        try
        {
            await _connectLock.WaitAsync(connectionToken);
            lockTaken = true;
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (IsConnected)
            {
                return;
            }

            if (_connectAsync != null)
            {
                var stream = await _connectAsync(connectionToken);
                if (Volatile.Read(ref _disposed) != 0)
                {
                    stream.Dispose();
                    ObjectDisposedException.ThrowIf(true, this);
                }

                _stream = stream;
                return;
            }

            var tcpClient = new TcpClient();

            try
            {
                _logger.LogDebug("Connecting to HDHomeRun at {Endpoint}", _endpoint);
                await tcpClient.ConnectAsync(_endpoint, connectionToken);
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                _tcpClient = tcpClient;
                _stream = tcpClient.GetStream();
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                _logger.LogInformation("Connected to HDHomeRun at {Endpoint}", _endpoint);
            }
            catch
            {
                tcpClient.Dispose();
                throw;
            }
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested && connectionCancellation.IsCancellationRequested)
        {
            InvalidateConnection();
            throw new TimeoutException($"HDHomeRun control connection timed out after {_exchangeTimeout}");
        }
        catch
        {
            InvalidateConnection();
            throw;
        }
        finally
        {
            if (lockTaken)
            {
                _connectLock.Release();
            }
        }
    }

    /// <summary>
    /// Gets a device variable value
    /// </summary>
    /// <param name="name">Variable name (e.g., "/sys/model", "/tuner0/channel")</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Variable value or null if not found</returns>
    public async Task<string?> GetAsync(string name, CancellationToken cancellationToken = default)
    {
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
        => await SetAsync(name, value, useStoredLockKey: true, lockKey: null, cancellationToken);

    private async Task<string?> SetAsync(string name, string value, bool useStoredLockKey, uint? lockKey, CancellationToken cancellationToken)
    {
        var builder = new HDHomeRunPacketBuilder()
            .AddTag(HDHomeRunTagType.GetSetName, name + "\0")
            .AddTag(HDHomeRunTagType.GetSetValue, value + "\0");

        if (lockKey.HasValue)
        {
            builder.AddTag(HDHomeRunTagType.GetSetLockkey, lockKey.Value);
        }
        else if (useStoredLockKey &&
            TryGetTunerIndex(name, out var tunerIndex) &&
            _tunerLockKeys.TryGetValue(tunerIndex, out var storedLockKey))
        {
            builder.AddTag(HDHomeRunTagType.GetSetLockkey, storedLockKey);
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
    /// <returns>True when the lock was acquired; otherwise, false.</returns>
    public async Task<bool> LockTunerAsync(int tunerIndex, bool force = false, CancellationToken cancellationToken = default)
        => await AcquireTunerLockAsync(tunerIndex, force, cancellationToken) != null;

    /// <summary>
    /// Acquires a tuner lock and returns its generated key.
    /// </summary>
    /// <param name="tunerIndex">Tuner index.</param>
    /// <param name="force">Whether to force acquisition.</param>
    /// <param name="cancellationToken">A token used to cancel acquisition.</param>
    /// <returns>The acquired lock key, or null when the device rejects the acquisition.</returns>
    internal async Task<uint?> AcquireTunerLockAsync(int tunerIndex, bool force = false, CancellationToken cancellationToken = default)
    {
        var operationLock = _tunerLockOperations.GetOrAdd(tunerIndex, static _ => new SemaphoreSlim(1, 1));
        await operationLock.WaitAsync(cancellationToken);

        try
        {
            _tunerLockKeys.TryGetValue(tunerIndex, out var previousLockKey);
            if (force)
            {
                await SetAsync($"/tuner{tunerIndex}/lockkey", "force", useStoredLockKey: false, previousLockKey == 0 ? null : previousLockKey, cancellationToken);
                _tunerLockKeys.TryRemove(tunerIndex, out _);
                previousLockKey = 0;
            }

            var newLockKey = _createLockKey();
            await SetAsync($"/tuner{tunerIndex}/lockkey", newLockKey.ToString(), useStoredLockKey: false, previousLockKey == 0 ? null : previousLockKey, cancellationToken);
            _tunerLockKeys[tunerIndex] = newLockKey;
            _logger.LogInformation("Acquired lock on tuner {TunerIndex}", tunerIndex);
            return newLockKey;
        }
        catch (HDHomeRunException ex)
        {
            _logger.LogWarning("Failed to acquire lock on tuner {TunerIndex}: {Error}", tunerIndex, ex.Message);
            return null;
        }
        finally
        {
            operationLock.Release();
        }
    }

    /// <summary>
    /// Releases a lock on a tuner
    /// </summary>
    public async Task ReleaseTunerLockAsync(int tunerIndex, CancellationToken cancellationToken = default)
    {
        var operationLock = _tunerLockOperations.GetOrAdd(tunerIndex, static _ => new SemaphoreSlim(1, 1));
        await operationLock.WaitAsync(cancellationToken);
        try
        {
            if (!_tunerLockKeys.ContainsKey(tunerIndex))
            {
                return;
            }

            try
            {
                await SetAsync($"/tuner{tunerIndex}/lockkey", "none", cancellationToken);
                _tunerLockKeys.TryRemove(tunerIndex, out _);
                _logger.LogInformation("Released lock on tuner {TunerIndex}", tunerIndex);
            }
            catch (HDHomeRunException ex)
            {
                _logger.LogWarning("Failed to release lock on tuner {TunerIndex}: {Error}", tunerIndex, ex.Message);
            }
        }
        finally
        {
            operationLock.Release();
        }
    }

    private static bool TryGetTunerIndex(string name, out int tunerIndex)
    {
        tunerIndex = 0;
        const string prefix = "/tuner";
        if (!name.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var separatorIndex = name.IndexOf('/', prefix.Length);
        return separatorIndex > prefix.Length &&
            int.TryParse(name.AsSpan(prefix.Length, separatorIndex - prefix.Length), out tunerIndex);
    }

    private static uint CreateRandomLockKey()
        => (uint)Random.Shared.Next(1, int.MaxValue);

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

    /// <summary>
    /// Sends one request and reads its exactly framed response.
    /// </summary>
    /// <param name="packet">The complete request packet.</param>
    /// <param name="cancellationToken">A token used to cancel the exchange.</param>
    /// <returns>The complete response packet.</returns>
    internal async Task<byte[]> SendAndReceiveAsync(byte[] packet, CancellationToken cancellationToken)
    {
        await _requestLock.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            using var exchangeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            exchangeCancellation.CancelAfter(_exchangeTimeout);
            var exchangeToken = exchangeCancellation.Token;

            try
            {
                if (_stream == null)
                {
                    await EnsureConnectedAsync(exchangeToken);
                }

                var stream = _stream ?? throw new InvalidOperationException("Not connected");
                await stream.WriteAsync(packet, exchangeToken);
                await stream.FlushAsync(exchangeToken);

                var header = new byte[HDHomeRunPacketBuilder.HeaderSize];
                await ReadExactlyAsync(stream, header, exchangeToken);

                var payloadLength = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2));
                var packetLength = HDHomeRunPacketBuilder.HeaderSize + payloadLength + HDHomeRunPacketBuilder.CrcSize;
                if (packetLength > HDHomeRunPacketBuilder.MaxPacketSize)
                {
                    throw new HDHomeRunException($"Response packet length {packetLength} exceeds the maximum packet size");
                }

                var response = new byte[packetLength];
                header.CopyTo(response, 0);
                await ReadExactlyAsync(stream, response.AsMemory(HDHomeRunPacketBuilder.HeaderSize), exchangeToken);
                ValidateResponsePacket(response);
                return response;
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested && exchangeCancellation.IsCancellationRequested)
            {
                InvalidateConnection();
                throw new TimeoutException($"HDHomeRun control exchange timed out after {_exchangeTimeout}");
            }
            catch
            {
                InvalidateConnection();
                throw;
            }
        }
        finally
        {
            _requestLock.Release();
        }
    }

    private static void ValidateResponsePacket(byte[] response)
    {
        var reader = new HDHomeRunPacketReader(response);
        if (!reader.IsValid)
        {
            throw new HDHomeRunException("Invalid response packet");
        }

        if (reader.PacketType != HDHomeRunPacketType.GetSetReply)
        {
            throw new HDHomeRunException($"Unexpected response type: {reader.PacketType}");
        }

        while (reader.TryReadTag(out _, out _))
        {
        }

        if (reader.HasError)
        {
            throw new HDHomeRunException("Malformed response packet payload");
        }
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var bytesRead = 0;
        while (bytesRead < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = await stream.ReadAsync(buffer[bytesRead..], cancellationToken);
            if (count == 0)
            {
                throw new HDHomeRunException("Connection closed by device before the complete response packet was received");
            }

            bytesRead += count;
        }
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

    private void InvalidateConnection()
    {
        var stream = Interlocked.Exchange(ref _stream, null);
        var tcpClient = Interlocked.Exchange(ref _tcpClient, null);
        stream?.Dispose();
        tcpClient?.Dispose();
    }

    /// <summary>
    /// Releases resources used by this instance.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        InvalidateConnection();
    }
}

/// <summary>
/// Exception thrown when an HDHomeRun operation fails
/// </summary>
public class HDHomeRunException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="HDHomeRunException"/> class.
    /// </summary>
    public HDHomeRunException(string message) : base(message) { }
    /// <summary>
    /// Initializes a new instance of the <see cref="HDHomeRunException"/> class.
    /// </summary>
    public HDHomeRunException(string message, Exception innerException) : base(message, innerException) { }
}
