using Microsoft.Extensions.Logging;

namespace Lineup.HDHomeRun.Device.Protocol;

/// <summary>
/// High-level interface for interacting with an HDHomeRun device.
/// Supports both HTTP API and native control protocol.
/// 
/// Native protocol commands from: https://info.hdhomerun.com/info/hdhomerun_config
/// </summary>
public class HDHomeRunDevice : IDisposable
{
private readonly ILogger<HDHomeRunDevice> _logger;
private readonly ILoggerFactory _loggerFactory;
private HDHomeRunControl? _control;
private HDHomeRunHttpControl? _httpControl;
private bool _nativeProtocolFailed;

/// <summary>
/// The discovered device information
/// </summary>
public HDHomeRunDiscoveredDevice DeviceInfo { get; }

/// <summary>
    /// Gets whether the device is connected via native protocol
    /// </summary>
    public bool IsConnected => _control?.IsConnected ?? false;

    /// <summary>
    /// Gets whether HTTP API is available
    /// </summary>
    public bool HasHttpApi => !string.IsNullOrEmpty(DeviceInfo.BaseUrl);

    /// <summary>
    /// Creates a new HDHomeRun device instance
    /// </summary>
    public HDHomeRunDevice(HDHomeRunDiscoveredDevice deviceInfo, ILoggerFactory loggerFactory)
    {
        DeviceInfo = deviceInfo;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<HDHomeRunDevice>();
    }

    #region Connection Management

    /// <summary>
    /// Connects to the device using native protocol.
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_nativeProtocolFailed)
        {
            throw new HDHomeRunException("Native protocol is not supported by this device. Use HTTP API methods.");
        }

        if (_control == null)
        {
            _control = new HDHomeRunControl(DeviceInfo.IpAddress, _loggerFactory.CreateLogger<HDHomeRunControl>());
        }

        try
        {
            await _control.ConnectAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _nativeProtocolFailed = true;
            _logger.LogWarning(ex, "Native protocol connection failed");
            throw;
        }
    }

    private async Task<bool> TryConnectNativeAsync(CancellationToken cancellationToken)
    {
        if (_nativeProtocolFailed)
        {
            return false;
        }

        try
        {
            await ConnectAsync(cancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private HDHomeRunHttpControl GetHttpControl()
    {
        _httpControl ??= new HDHomeRunHttpControl(
            DeviceInfo.BaseUrl ?? $"http://{DeviceInfo.IpAddress}",
            _loggerFactory.CreateLogger<HDHomeRunHttpControl>());
        return _httpControl;
    }

    #endregion

    #region Generic Variable Access (Native Protocol)

    /// <summary>
    /// Gets a device variable using native protocol.
    /// </summary>
    /// <param name="name">Variable path (e.g., "/sys/model")</param>
    public async Task<string?> GetVariableAsync(string name, CancellationToken cancellationToken = default)
    {
        if (!await TryConnectNativeAsync(cancellationToken))
        {
            throw new HDHomeRunException("Native protocol not available - use HTTP API methods");
        }
        return await _control!.GetAsync(name, cancellationToken);
    }

    /// <summary>
    /// Sets a device variable using native protocol.
    /// </summary>
    /// <param name="name">Variable path (e.g., "/tuner0/channel")</param>
    /// <param name="value">Value to set</param>
    public async Task<string?> SetVariableAsync(string name, string value, CancellationToken cancellationToken = default)
    {
        if (!await TryConnectNativeAsync(cancellationToken))
        {
            throw new HDHomeRunException("Native protocol not available");
        }
        return await _control!.SetAsync(name, value, cancellationToken);
    }

    #endregion

    #region System Commands (/sys/*) - HTTP API with Native Fallback

    /// <summary>
    /// Gets the device model name (via HTTP API)
    /// </summary>
    public async Task<string?> GetModelAsync(CancellationToken cancellationToken = default)
    {
        // Try HTTP API first
        var discover = await GetHttpControl().DiscoverAsync(cancellationToken);
        if (discover?.ModelNumber != null)
        {
            return discover.ModelNumber;
        }

        // Fallback to native protocol
        if (await TryConnectNativeAsync(cancellationToken))
        {
            return await _control!.GetAsync("/sys/model", cancellationToken);
        }

        return null;
    }

    /// <summary>
    /// Gets the firmware version (via HTTP API)
    /// </summary>
    public async Task<string?> GetFirmwareVersionAsync(CancellationToken cancellationToken = default)
    {
        // Try HTTP API first
        var discover = await GetHttpControl().DiscoverAsync(cancellationToken);
        if (discover?.FirmwareVersion != null)
        {
            return discover.FirmwareVersion;
        }

        // Fallback to native protocol
        if (await TryConnectNativeAsync(cancellationToken))
        {
            return await _control!.GetAsync("/sys/version", cancellationToken);
        }

        return null;
    }

    /// <summary>
    /// Gets the hardware model/revision
    /// </summary>
    public async Task<string?> GetHardwareModelAsync(CancellationToken cancellationToken = default)
    {
        return await GetVariableAsync("/sys/hwmodel", cancellationToken);
    }

    /// <summary>
    /// Gets the supported features
    /// </summary>
    public async Task<string?> GetFeaturesAsync(CancellationToken cancellationToken = default)
    {
        return await GetVariableAsync("/sys/features", cancellationToken);
    }


    /// <summary>
    /// Gets the firmware copyright (native protocol only)
    /// </summary>
    public async Task<string?> GetCopyrightAsync(CancellationToken cancellationToken = default)
    {
        if (!await TryConnectNativeAsync(cancellationToken))
        {
            return null;
        }
        return await _control!.GetAsync("/sys/copyright", cancellationToken);
    }

    /// <summary>
    /// Gets system debug information (native protocol only)
    /// </summary>
    public async Task<string?> GetSystemDebugAsync(CancellationToken cancellationToken = default)
    {
        if (!await TryConnectNativeAsync(cancellationToken))
        {
            return null;
        }
        return await _control!.GetAsync("/sys/debug", cancellationToken);
    }

    /// <summary>
    /// Restarts the HDHomeRun device.
    /// Tries native protocol first, then HTTP API.
    /// </summary>
    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Restarting device {DeviceId}", DeviceInfo.DeviceIdHex);

        // Try native protocol first
        if (await TryConnectNativeAsync(cancellationToken))
        {
            try
            {
                await _control!.SetAsync("/sys/restart", "self", cancellationToken);
                _control.Dispose();
                _control = null;
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Native restart failed, trying HTTP API");
            }
        }

        // Try HTTP API (may not work on all devices)
        var success = await GetHttpControl().RestartAsync(cancellationToken);
        if (!success)
        {
            throw new HDHomeRunException("Failed to restart device - neither native protocol nor HTTP API worked");
        }

        _control?.Dispose();
        _control = null;
    }

    #endregion

    #region Lineup Commands (/lineup/*)

    /// <summary>
    /// Gets the lineup location (country:postcode)
    /// </summary>
    public async Task<string?> GetLineupLocationAsync(CancellationToken cancellationToken = default)
    {
        return await GetVariableAsync("/lineup/location", cancellationToken);
    }

    /// <summary>
    /// Sets the lineup location
    /// </summary>
    /// <param name="countryCode">Country code (e.g., "US")</param>
    /// <param name="postCode">Postal code</param>
    public async Task SetLineupLocationAsync(string countryCode, string postCode, CancellationToken cancellationToken = default)
    {
        await SetVariableAsync("/lineup/location", $"{countryCode}:{postCode}", cancellationToken);
    }

    /// <summary>
    /// Disables lineup server connection
    /// </summary>
    public async Task DisableLineupAsync(CancellationToken cancellationToken = default)
    {
        await SetVariableAsync("/lineup/location", "disabled", cancellationToken);
    }

    #endregion

    #region IR Commands (/ir/*)

    /// <summary>
    /// Gets the IR target (IP:port for IR blaster events)
    /// </summary>
    public async Task<string?> GetIrTargetAsync(CancellationToken cancellationToken = default)
    {
        return await GetVariableAsync("/ir/target", cancellationToken);
    }

    /// <summary>
    /// Sets the IR target
    /// </summary>
    /// <param name="ipAddress">Target IP address</param>
    /// <param name="port">Target port</param>
    public async Task SetIrTargetAsync(string ipAddress, int port, CancellationToken cancellationToken = default)
    {
        await SetVariableAsync("/ir/target", $"{ipAddress}:{port}", cancellationToken);
    }

    #endregion

    #region Tuner Commands (/tuner{n}/*) - HTTP API with Native Fallback

    /// <summary>
    /// Gets comprehensive tuner status.
    /// Uses HTTP API status endpoint or native protocol.
    /// </summary>
    public async Task<TunerStatus> GetTunerStatusAsync(int tunerIndex, CancellationToken cancellationToken = default)
    {
        // Try HTTP API first - get status from /status.json (modern devices)
        try
        {
            var httpControl = GetHttpControl();
            var httpStatus = await httpControl.GetTunerStatusAsync(tunerIndex, cancellationToken);
            if (httpStatus != null)
            {
                return new TunerStatus
                {
                    TunerIndex = tunerIndex,
                    Channel = httpStatus.Channel,
                    VirtualChannel = httpStatus.VirtualChannel,
                    Target = httpStatus.TargetIP,
                    LockType = httpStatus.IsActive ? "locked" : null,
                    SignalStrength = httpStatus.SignalStrengthPercent ?? (httpStatus.IsActive ? 100 : 0),
                    SignalToNoiseQuality = httpStatus.SignalQualityPercent ?? (httpStatus.IsActive ? 100 : 0),
                    SymbolErrorQuality = httpStatus.SymbolQualityPercent ?? (httpStatus.IsActive ? 100 : 0),
                    BitsPerSecond = (httpStatus.NetworkRate ?? 0) * 8 // NetworkRate is in bytes/sec
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "HTTP tuner status failed, trying native protocol");
        }

        // Fallback to native protocol
        if (await TryConnectNativeAsync(cancellationToken))
        {
            var statusStr = await _control!.GetAsync($"/tuner{tunerIndex}/status", cancellationToken);
            var channelStr = await _control.GetAsync($"/tuner{tunerIndex}/channel", cancellationToken);
            var vchannelStr = await _control.GetAsync($"/tuner{tunerIndex}/vchannel", cancellationToken);
            var targetStr = await _control.GetAsync($"/tuner{tunerIndex}/target", cancellationToken);

            return TunerStatus.Parse(tunerIndex, statusStr, channelStr, vchannelStr, targetStr);
        }

        // Return empty status if nothing works
        return new TunerStatus { TunerIndex = tunerIndex };
    }

    /// <summary>
    /// Gets detailed tuner debug information (native protocol only)
    /// </summary>
    public async Task<TunerDebugInfo?> GetTunerDebugAsync(int tunerIndex, CancellationToken cancellationToken = default)
    {
        if (!await TryConnectNativeAsync(cancellationToken))
        {
            return null; // Not available via HTTP API
        }
        var debugStr = await _control!.GetAsync($"/tuner{tunerIndex}/debug", cancellationToken);
        return debugStr != null ? TunerDebugInfo.Parse(debugStr) : null;
    }

    /// <summary>
    /// Gets stream information (detected programs/sub-channels)
    /// </summary>
    public async Task<List<StreamProgram>> GetStreamInfoAsync(int tunerIndex, CancellationToken cancellationToken = default)
    {
        if (!await TryConnectNativeAsync(cancellationToken))
        {
            return []; // Not available via HTTP API
        }
        var streamInfo = await _control!.GetAsync($"/tuner{tunerIndex}/streaminfo", cancellationToken);
        return StreamProgram.ParseStreamInfo(streamInfo);
    }

    /// <summary>
    /// Gets the channel map for a tuner
    /// </summary>
    public async Task<string?> GetChannelMapAsync(int tunerIndex, CancellationToken cancellationToken = default)
    {
        if (!await TryConnectNativeAsync(cancellationToken))
        {
            return null;
        }
        return await _control!.GetAsync($"/tuner{tunerIndex}/channelmap", cancellationToken);
    }

    /// <summary>
    /// Sets the channel map for a tuner
    /// </summary>
    /// <param name="tunerIndex">Tuner index</param>
    /// <param name="channelMap">Channel map (e.g., "us-bcast", "us-cable", "eu-bcast")</param>
    public async Task SetChannelMapAsync(int tunerIndex, string channelMap, CancellationToken cancellationToken = default)
    {
        await SetVariableAsync($"/tuner{tunerIndex}/channelmap", channelMap, cancellationToken);
    }

    /// <summary>
    /// Tunes to a physical channel
    /// </summary>
    /// <param name="tunerIndex">Tuner index</param>
    /// <param name="modulation">Modulation type (e.g., "auto", "8vsb", "qam256")</param>
    /// <param name="frequency">Frequency in Hz or channel number</param>
    public async Task SetPhysicalChannelAsync(int tunerIndex, string modulation, string frequency, CancellationToken cancellationToken = default)
    {
        await SetVariableAsync($"/tuner{tunerIndex}/channel", $"{modulation}:{frequency}", cancellationToken);
    }

    /// <summary>
    /// Stops the tuner (sets channel to none)
    /// </summary>
    public async Task StopTunerAsync(int tunerIndex, CancellationToken cancellationToken = default)
    {
        await SetVariableAsync($"/tuner{tunerIndex}/channel", "none", cancellationToken);
    }

    /// <summary>
    /// Tunes to a virtual channel (e.g., "2.1")
    /// </summary>
    public async Task TuneChannelAsync(int tunerIndex, string virtualChannel, CancellationToken cancellationToken = default)
    {
        await SetVariableAsync($"/tuner{tunerIndex}/vchannel", virtualChannel, cancellationToken);
    }

    /// <summary>
    /// Gets the current virtual channel
    /// </summary>
    public async Task<string?> GetVirtualChannelAsync(int tunerIndex, CancellationToken cancellationToken = default)
    {
        return await GetVariableAsync($"/tuner{tunerIndex}/vchannel", cancellationToken);
    }

    /// <summary>
    /// Sets the program filter (filters by sub-channel/program number)
    /// </summary>
    public async Task SetProgramFilterAsync(int tunerIndex, int programNumber, CancellationToken cancellationToken = default)
    {
        await SetVariableAsync($"/tuner{tunerIndex}/program", programNumber.ToString(), cancellationToken);
    }

    /// <summary>
    /// Sets the PID filter
    /// </summary>
    /// <param name="tunerIndex">Tuner index</param>
    /// <param name="filter">PID filter (e.g., "0x0000-0x1FFF" for all, "0x0000 0x0030-0x0033" for specific)</param>
    public async Task SetPidFilterAsync(int tunerIndex, string filter, CancellationToken cancellationToken = default)
    {
        await SetVariableAsync($"/tuner{tunerIndex}/filter", filter, cancellationToken);
    }

    /// <summary>
    /// Gets the streaming target
    /// </summary>
    public async Task<string?> GetTargetAsync(int tunerIndex, CancellationToken cancellationToken = default)
    {
        return await GetVariableAsync($"/tuner{tunerIndex}/target", cancellationToken);
    }

    /// <summary>
    /// Sets the streaming target (UDP or RTP)
    /// </summary>
    /// <param name="tunerIndex">Tuner index</param>
    /// <param name="protocol">Protocol ("udp" or "rtp")</param>
    /// <param name="ipAddress">Target IP address</param>
    /// <param name="port">Target port</param>
    public async Task SetTargetAsync(int tunerIndex, string protocol, string ipAddress, int port, CancellationToken cancellationToken = default)
    {
        await SetVariableAsync($"/tuner{tunerIndex}/target", $"{protocol}://{ipAddress}:{port}", cancellationToken);
    }

    /// <summary>
    /// Stops streaming (sets target to none)
    /// </summary>
    public async Task ClearTargetAsync(int tunerIndex, CancellationToken cancellationToken = default)
    {
        await SetVariableAsync($"/tuner{tunerIndex}/target", "none", cancellationToken);
    }

    /// <summary>
    /// Acquires a lock on a tuner (native protocol only)
    /// </summary>
    /// <param name="tunerIndex">Tuner index</param>
    /// <param name="force">Force lock even if already locked by another client</param>
    /// <returns>Lock key if successful, null if failed</returns>
    public async Task<uint?> AcquireLockAsync(int tunerIndex, bool force = false, CancellationToken cancellationToken = default)
    {
        if (!await TryConnectNativeAsync(cancellationToken))
        {
            return null; // Not available via HTTP API
        }
        var success = await _control!.LockTunerAsync(tunerIndex, force, cancellationToken);
        return success ? 1u : null;
    }

    /// <summary>
    /// Releases a tuner lock (native protocol only)
    /// </summary>
    public async Task ReleaseLockAsync(int tunerIndex, CancellationToken cancellationToken = default)
    {
        if (!await TryConnectNativeAsync(cancellationToken))
        {
            return; // Not available via HTTP API
        }
        await _control!.ReleaseTunerLockAsync(tunerIndex, cancellationToken);
    }

    #endregion

    #region Convenience Methods

    /// <summary>
    /// Gets the HTTP streaming URL for a virtual channel
    /// </summary>
    /// <param name="virtualChannel">Virtual channel (e.g., "2.1")</param>
    /// <param name="transcodeProfile">Optional transcode profile (e.g., "heavy", "mobile")</param>
    public string GetHttpStreamUrl(string virtualChannel, string? transcodeProfile = null)
    {
        var baseUrl = DeviceInfo.BaseUrl ?? $"http://{DeviceInfo.IpAddress}:5004";
        var url = $"{baseUrl}/auto/v{virtualChannel}";

        if (!string.IsNullOrEmpty(transcodeProfile))
        {
            url += $"?transcode={transcodeProfile}";
        }

        return url;
    }

    /// <summary>
    /// Gets comprehensive device information
    /// </summary>
    public async Task<NativeDeviceInfo> GetDeviceInfoAsync(CancellationToken cancellationToken = default)
    {
        var model = await GetModelAsync(cancellationToken);
        var firmware = await GetFirmwareVersionAsync(cancellationToken);
        var hwModel = await GetHardwareModelAsync(cancellationToken);
        var features = await GetFeaturesAsync(cancellationToken);

        return new NativeDeviceInfo
        {
            DeviceId = DeviceInfo.DeviceIdHex,
            IpAddress = DeviceInfo.IpAddress.ToString(),
            Model = model,
            FirmwareVersion = firmware,
            HardwareModel = hwModel,
            Features = features,
            TunerCount = DeviceInfo.TunerCount,
            BaseUrl = DeviceInfo.BaseUrl
        };
    }

    /// <summary>
    /// Starts streaming a channel to a UDP target
    /// </summary>
    public async Task StartUdpStreamAsync(int tunerIndex, string virtualChannel, string targetIp, int targetPort, CancellationToken cancellationToken = default)
    {
        await TuneChannelAsync(tunerIndex, virtualChannel, cancellationToken);
        await SetTargetAsync(tunerIndex, "udp", targetIp, targetPort, cancellationToken);
    }

    /// <summary>
    /// Starts streaming a channel to an RTP target
    /// </summary>
    public async Task StartRtpStreamAsync(int tunerIndex, string virtualChannel, string targetIp, int targetPort, CancellationToken cancellationToken = default)
    {
        await TuneChannelAsync(tunerIndex, virtualChannel, cancellationToken);
        await SetTargetAsync(tunerIndex, "rtp", targetIp, targetPort, cancellationToken);
    }

    /// <summary>
    /// Stops all streaming on a tuner.
    /// </summary>
    public async Task StopStreamingAsync(int tunerIndex, CancellationToken cancellationToken = default)
    {
        // Try native protocol
        if (await TryConnectNativeAsync(cancellationToken))
        {
            try
            {
                await _control!.SetAsync($"/tuner{tunerIndex}/target", "none", cancellationToken);
                await _control!.SetAsync($"/tuner{tunerIndex}/channel", "none", cancellationToken);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Native stop streaming failed");
            }
        }

        // HTTP API doesn't have a direct stop mechanism - streams stop when client disconnects
        _logger.LogInformation("Tuner {TunerIndex} stop requested - HTTP streams stop when client disconnects", tunerIndex);
    }

    #endregion

    public void Dispose()
    {
        _control?.Dispose();
        _httpControl?.Dispose();
    }
}

/// <summary>
/// Comprehensive device information
/// </summary>
public record NativeDeviceInfo
{
    public required string DeviceId { get; init; }
    public required string IpAddress { get; init; }
    public string? Model { get; init; }
    public string? FirmwareVersion { get; init; }
    public string? HardwareModel { get; init; }
    public string? Features { get; init; }
    public int TunerCount { get; init; }
    public string? BaseUrl { get; init; }
}

/// <summary>
/// Represents the status of an HDHomeRun tuner
/// </summary>
public record TunerStatus
{
    /// <summary>
    /// Tuner index
    /// </summary>
    public int TunerIndex { get; init; }

    /// <summary>
    /// Physical channel (e.g., "8vsb:177000000")
    /// </summary>
    public string? Channel { get; init; }

    /// <summary>
    /// Virtual channel (e.g., "2.1")
    /// </summary>
    public string? VirtualChannel { get; init; }

    /// <summary>
    /// Streaming target (e.g., "udp://192.168.1.100:5000")
    /// </summary>
    public string? Target { get; init; }

    /// <summary>
    /// Signal strength (0-100). 80% is approximately -12dBmV.
    /// </summary>
    public int SignalStrength { get; init; }

    /// <summary>
    /// Signal-to-noise quality (0-100)
    /// </summary>
    public int SignalToNoiseQuality { get; init; }

    /// <summary>
    /// Symbol error quality (0-100). Number of uncorrectable digital errors.
    /// </summary>
    public int SymbolErrorQuality { get; init; }

    /// <summary>
    /// Lock type (e.g., "8vsb", "qam256", "none")
    /// </summary>
    public string? LockType { get; init; }

    /// <summary>
    /// Bits per second
    /// </summary>
    public long BitsPerSecond { get; init; }

    /// <summary>
    /// Packets per second
    /// </summary>
    public int PacketsPerSecond { get; init; }

    /// <summary>
    /// Whether the tuner is currently in use
    /// </summary>
    public bool IsActive => !string.IsNullOrEmpty(Channel) && Channel != "none";

    /// <summary>
    /// Whether the tuner is currently streaming
    /// </summary>
    public bool IsStreaming => !string.IsNullOrEmpty(Target) && Target != "none";

    /// <summary>
    /// Whether the tuner has signal lock
    /// </summary>
    public bool HasLock => !string.IsNullOrEmpty(LockType) && LockType != "none";

    /// <summary>
    /// Parses tuner status from device response strings
    /// </summary>
    public static TunerStatus Parse(int tunerIndex, string? status, string? channel, string? vchannel, string? target)
    {
        int ss = 0, snq = 0, seq = 0, pps = 0;
        long bps = 0;
        string? lockType = null;

        // Parse status string like "ch=8vsb:177000000 lock=8vsb ss=85 snq=100 seq=100 bps=19392672 pps=0"
        if (!string.IsNullOrEmpty(status))
        {
            var parts = status.Split(' ');
            foreach (var part in parts)
            {
                var kv = part.Split('=');
                if (kv.Length == 2)
                {
                    switch (kv[0])
                    {
                        case "lock":
                            lockType = kv[1];
                            break;
                        case "ss":
                            int.TryParse(kv[1], out ss);
                            break;
                        case "snq":
                            int.TryParse(kv[1], out snq);
                            break;
                        case "seq":
                            int.TryParse(kv[1], out seq);
                            break;
                        case "bps":
                            long.TryParse(kv[1], out bps);
                            break;
                        case "pps":
                            int.TryParse(kv[1], out pps);
                            break;
                    }
                }
            }
        }

        return new TunerStatus
        {
            TunerIndex = tunerIndex,
            Channel = channel,
            VirtualChannel = vchannel,
            Target = target,
            LockType = lockType,
            SignalStrength = ss,
            SignalToNoiseQuality = snq,
            SymbolErrorQuality = seq,
            BitsPerSecond = bps,
            PacketsPerSecond = pps
        };
    }
}

/// <summary>
/// Detailed tuner debug information
/// </summary>
public record TunerDebugInfo
{
    /// <summary>
    /// Transport stream resync count
    /// </summary>
    public int ResyncCount { get; init; }

    /// <summary>
    /// Buffer overflow count
    /// </summary>
    public int OverflowCount { get; init; }

    /// <summary>
    /// Transport stream utilization percentage
    /// </summary>
    public int Utilization { get; init; }

    /// <summary>
    /// Transport error count
    /// </summary>
    public int TransportErrors { get; init; }

    /// <summary>
    /// Missed packet count
    /// </summary>
    public int MissedPackets { get; init; }

    /// <summary>
    /// CRC error count
    /// </summary>
    public int CrcErrors { get; init; }

    /// <summary>
    /// Network error count
    /// </summary>
    public int NetworkErrors { get; init; }

    /// <summary>
    /// Stop reason
    /// </summary>
    public int StopReason { get; init; }

    /// <summary>
    /// Parses debug info from device response
    /// </summary>
    public static TunerDebugInfo Parse(string debugStr)
    {
        int resync = 0, overflow = 0, ut = 0, te = 0, miss = 0, crc = 0, netErr = 0, stop = 0;

        // Format:
        // tun: ch=... lock=... ss=... snq=... seq=... dbg=...
        // dev: resync=0 overflow=0
        // ts:  bps=... ut=94 te=0 miss=0 crc=0
        // flt: bps=...
        // net: pps=0 err=0 stop=0

        var lines = debugStr.Split('\n');
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("dev:"))
            {
                ParseKeyValues(trimmed[4..], v =>
                {
                    if (v.TryGetValue("resync", out var r)) int.TryParse(r, out resync);
                    if (v.TryGetValue("overflow", out var o)) int.TryParse(o, out overflow);
                });
            }
            else if (trimmed.StartsWith("ts:"))
            {
                ParseKeyValues(trimmed[3..], v =>
                {
                    if (v.TryGetValue("ut", out var u)) int.TryParse(u, out ut);
                    if (v.TryGetValue("te", out var t)) int.TryParse(t, out te);
                    if (v.TryGetValue("miss", out var m)) int.TryParse(m, out miss);
                    if (v.TryGetValue("crc", out var c)) int.TryParse(c, out crc);
                });
            }
            else if (trimmed.StartsWith("net:"))
            {
                ParseKeyValues(trimmed[4..], v =>
                {
                    if (v.TryGetValue("err", out var e)) int.TryParse(e, out netErr);
                    if (v.TryGetValue("stop", out var s)) int.TryParse(s, out stop);
                });
            }
        }

        return new TunerDebugInfo
        {
            ResyncCount = resync,
            OverflowCount = overflow,
            Utilization = ut,
            TransportErrors = te,
            MissedPackets = miss,
            CrcErrors = crc,
            NetworkErrors = netErr,
            StopReason = stop
        };
    }

    private static void ParseKeyValues(string str, Action<Dictionary<string, string>> action)
    {
        var dict = new Dictionary<string, string>();
        var parts = str.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var kv = part.Split('=');
            if (kv.Length == 2)
            {
                dict[kv[0]] = kv[1];
            }
        }
        action(dict);
    }
}

/// <summary>
/// Represents a program (sub-channel) detected in a stream
/// </summary>
public record StreamProgram
{
    /// <summary>
    /// Program number
    /// </summary>
    public int ProgramNumber { get; init; }

    /// <summary>
    /// Virtual channel (e.g., "2.1")
    /// </summary>
    public string? VirtualChannel { get; init; }

    /// <summary>
    /// Program name (e.g., "WCBS-HD")
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Whether the program is encrypted
    /// </summary>
    public bool IsEncrypted { get; init; }

    /// <summary>
    /// Whether this is a control stream
    /// </summary>
    public bool IsControl { get; init; }

    /// <summary>
    /// Parses stream info from device response
    /// </summary>
    public static List<StreamProgram> ParseStreamInfo(string? streamInfo)
    {
        var programs = new List<StreamProgram>();

        if (string.IsNullOrEmpty(streamInfo))
            return programs;

        // Format: "3: 20.1 KBWB-HD" or "2: 0 (encrypted)"
        var lines = streamInfo.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            // Skip metadata lines
            if (trimmed.StartsWith("tsid=") || trimmed.StartsWith("pcr="))
                continue;

            var colonIndex = trimmed.IndexOf(':');
            if (colonIndex <= 0) continue;

            if (!int.TryParse(trimmed[..colonIndex], out var programNumber))
                continue;

            var rest = trimmed[(colonIndex + 1)..].Trim();
            var isEncrypted = rest.Contains("(encrypted)");
            var isControl = rest.Contains("(control)");

            // Remove flags from the string
            rest = rest.Replace("(encrypted)", "").Replace("(control)", "").Trim();

            string? vchannel = null;
            string? name = null;

            var spaceIndex = rest.IndexOf(' ');
            if (spaceIndex > 0)
            {
                vchannel = rest[..spaceIndex];
                name = rest[(spaceIndex + 1)..].Trim();
            }
            else
            {
                vchannel = rest;
            }

            // "0" means no virtual channel
            if (vchannel == "0")
                vchannel = null;

            programs.Add(new StreamProgram
            {
                ProgramNumber = programNumber,
                VirtualChannel = vchannel,
                Name = string.IsNullOrEmpty(name) ? null : name,
                IsEncrypted = isEncrypted,
                IsControl = isControl
            });
        }

        return programs;
    }
}
