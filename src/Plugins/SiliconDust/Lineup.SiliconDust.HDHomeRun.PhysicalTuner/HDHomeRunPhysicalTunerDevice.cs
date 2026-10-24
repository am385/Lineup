using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;
using Lineup.SiliconDust.HDHomeRun.Device;
using Lineup.SiliconDust.HDHomeRun.Device.Models;
using Lineup.SiliconDust.HDHomeRun.Device.Protocol;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text;

namespace Lineup.SiliconDust.HDHomeRun.PhysicalTuner;

/// <summary>
/// Represents one discovered physical HDHomeRun device.
/// </summary>
public sealed class HDHomeRunPhysicalTunerDevice :
    IPhysicalTunerDevice,
    IPhysicalDeviceAuthorizationCapability,
    IPhysicalDeviceStatusCapability,
    IPhysicalDeviceInformationCapability,
    IPhysicalReceiverControlCapability,
    IPhysicalDeviceRestartCapability,
    IPhysicalStreamDiagnosticCapability
{
    private const int MaximumDiagnosticBytes = 4096;
    private const int MaximumDiagnosticCharacters = 256;
    private readonly HDHomeRunDeviceClient _deviceClient;
    private readonly HttpClient _httpClient;
    private readonly ILogger<HDHomeRunPhysicalTunerDevice> _logger;
    private readonly string _address;
    private readonly HDHomeRunService _deviceService;
    private readonly SemaphoreSlim _capacity;
    private readonly SemaphoreSlim _lineupRefresh = new(1, 1);
    private readonly SemaphoreSlim _protocolDeviceInitialization = new(1, 1);
    private IReadOnlyDictionary<string, HDHomeRunChannel> _channels = new Dictionary<string, HDHomeRunChannel>(StringComparer.Ordinal);
    private HDHomeRunDeviceInfo _deviceInfo;
    private HDHomeRunDevice? _protocolDevice;

    internal HDHomeRunPhysicalTunerDevice(
        PhysicalDeviceId deviceId,
        string name,
        HDHomeRunDeviceInfo deviceInfo,
        HDHomeRunDeviceClient deviceClient,
        HttpClient httpClient,
        string address,
        HDHomeRunService deviceService,
        ILogger<HDHomeRunPhysicalTunerDevice> logger)
    {
        _deviceInfo = deviceInfo;
        _deviceClient = deviceClient;
        _httpClient = httpClient;
        _address = address;
        _deviceService = deviceService;
        _logger = logger;
        _capacity = new SemaphoreSlim(deviceInfo.TunerCount, deviceInfo.TunerCount);
        Snapshot = new PhysicalDeviceSnapshot
        {
            Id = deviceId,
            Kind = HDHomeRunPhysicalTunerDriver.DriverKind,
            Name = name,
            Capacity = new PhysicalTunerCapacity(deviceInfo.TunerCount)
        };
    }

    /// <summary>Gets the metadata returned by the device discovery endpoint.</summary>
    public HDHomeRunDeviceInfo DeviceInfo => Volatile.Read(ref _deviceInfo);

    /// <inheritdoc />
    public PhysicalDeviceSnapshot Snapshot { get; }

    /// <inheritdoc />
    public Uri CreateDiagnosticUri(string providerKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
        return new Uri(_httpClient.BaseAddress!, $"auto/v{Uri.EscapeDataString(providerKey)}");
    }

    /// <inheritdoc />
    public async Task<PhysicalDeviceStatusSnapshot> RefreshStatusAsync(CancellationToken cancellationToken = default)
    {
        var deviceInfo = DeviceInfo;
        var protocolDevice = await GetProtocolDeviceAsync(cancellationToken);
        var receivers = new List<PhysicalReceiverStatus>(deviceInfo.TunerCount);
        for (var index = 0; index < deviceInfo.TunerCount; index++)
        {
            var status = await protocolDevice.GetTunerStatusAsync(index, cancellationToken);
            receivers.Add(MapStatus(status));
        }

        return CreateStatusSnapshot(deviceInfo, receivers);
    }

    /// <inheritdoc />
    public async Task<PhysicalDeviceStatusSnapshot> RefreshDeviceInformationAsync(CancellationToken cancellationToken = default)
    {
        var previous = DeviceInfo;
        var refreshed = await _deviceClient.DiscoverDeviceAsync(cancellationToken);
        if (refreshed.TunerCount <= 0)
        {
            throw new InvalidOperationException($"HDHomeRun device '{refreshed.DeviceID}' reported invalid tuner count {refreshed.TunerCount}.");
        }

        Volatile.Write(ref _deviceInfo, refreshed);
        if (!string.Equals(previous.DeviceID, refreshed.DeviceID, StringComparison.Ordinal)
            || !string.Equals(previous.BaseURL, refreshed.BaseURL, StringComparison.OrdinalIgnoreCase))
        {
            Volatile.Write(ref _protocolDevice, null);
        }

        return CreateStatusSnapshot(refreshed, []);
    }

    private PhysicalDeviceStatusSnapshot CreateStatusSnapshot(HDHomeRunDeviceInfo deviceInfo, IReadOnlyList<PhysicalReceiverStatus> receivers) =>
        new()
        {
            Device = Snapshot with { Capacity = new PhysicalTunerCapacity(deviceInfo.TunerCount) },
            Details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["friendlyName"] = deviceInfo.FriendlyName,
                ["modelNumber"] = deviceInfo.ModelNumber,
                ["firmwareName"] = deviceInfo.FirmwareName,
                ["firmwareVersion"] = deviceInfo.FirmwareVersion,
                ["deviceId"] = deviceInfo.DeviceID,
                ["baseUrl"] = deviceInfo.BaseURL,
                ["lineupUrl"] = deviceInfo.LineupURL
            },
            Receivers = receivers
        };

    /// <inheritdoc />
    public async Task StopReceiverAsync(int receiverIndex, CancellationToken cancellationToken = default)
    {
        var device = await GetProtocolDeviceAsync(cancellationToken);
        await device.StopTunerAsync(receiverIndex, cancellationToken);
    }

    /// <inheritdoc />
    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        var device = await GetProtocolDeviceAsync(cancellationToken);
        await device.RestartAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask<string> GetAuthorizationAsync(CancellationToken cancellationToken = default)
    {
        var deviceInfo = await _deviceClient.DiscoverDeviceAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(deviceInfo.DeviceAuth))
        {
            throw new InvalidOperationException($"HDHomeRun device {Snapshot.Id} did not provide authorization.");
        }

        return deviceInfo.DeviceAuth;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PhysicalSourceChannel>> RefreshLineupAsync(CancellationToken cancellationToken = default)
    {
        await _lineupRefresh.WaitAsync(cancellationToken);
        try
        {
            var channels = await _deviceClient.FetchChannelLineupAsync(cancellationToken);
            var indexedChannels = new Dictionary<string, HDHomeRunChannel>(StringComparer.Ordinal);
            var translatedChannels = new List<PhysicalSourceChannel>(channels.Count);

            foreach (var channel in channels)
            {
                if (string.IsNullOrWhiteSpace(channel.GuideNumber))
                {
                    throw new InvalidOperationException("HDHomeRun lineup contained a channel without a guide number.");
                }

                if (!indexedChannels.TryAdd(channel.GuideNumber, channel))
                {
                    throw new InvalidOperationException($"HDHomeRun lineup contained duplicate guide number '{channel.GuideNumber}'.");
                }

                translatedChannels.Add(new PhysicalSourceChannel
                {
                    Id = new SourceChannelId(Snapshot.Id, channel.GuideNumber),
                    ProviderNumber = channel.GuideNumber,
                    ProviderName = channel.GuideName,
                    Metadata = CreateMetadata(channel)
                });
            }

            Volatile.Write(ref _channels, indexedChannels);
            return translatedChannels;
        }
        finally
        {
            _lineupRefresh.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<IPhysicalStreamLease> OpenStreamAsync(PhysicalStreamRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ChannelId.PhysicalDeviceId != Snapshot.Id)
        {
            throw new ArgumentException("The requested channel belongs to a different physical device.", nameof(request));
        }

        var channels = Volatile.Read(ref _channels);
        if (channels.Count == 0)
        {
            await RefreshLineupAsync(cancellationToken);
            channels = Volatile.Read(ref _channels);
        }

        if (!channels.TryGetValue(request.ChannelId.ProviderKey, out var channel))
        {
            throw new KeyNotFoundException($"Channel '{request.ChannelId.ProviderKey}' was not found in the HDHomeRun lineup.");
        }

        await _capacity.WaitAsync(cancellationToken);
        HttpResponseMessage? response = null;
        Stream? stream = null;
        try
        {
            if (!Uri.TryCreate(channel.URL, UriKind.Absolute, out var streamUri)
                || (streamUri.Scheme != Uri.UriSchemeHttp && streamUri.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException($"Channel '{channel.GuideNumber}' has an invalid stream URL.");
            }

            using var message = new HttpRequestMessage(HttpMethod.Get, streamUri);
            response = await _httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw await CreateAcquisitionExceptionAsync(response, request.ChannelId, cancellationToken);
            }
            stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            _logger.LogDebug("Acquired HDHomeRun stream for channel {GuideNumber} on device {DeviceId}", channel.GuideNumber, DeviceInfo.DeviceID);
            return new HDHomeRunPhysicalStreamLease(stream, response, _capacity);
        }
        catch (Exception exception)
        {
            if (stream is not null)
            {
                await stream.DisposeAsync();
            }

            response?.Dispose();
            _capacity.Release();
            if (exception is HttpRequestException httpException)
            {
                throw new PhysicalStreamAcquisitionException(
                    PhysicalStreamFailureCategory.Unavailable,
                    "source_unavailable",
                    "The HDHomeRun source could not be reached.",
                    retryable: true,
                    Snapshot.Id,
                    request.ChannelId,
                    httpException);
            }
            throw;
        }
    }

    private async Task<PhysicalStreamAcquisitionException> CreateAcquisitionExceptionAsync(
        HttpResponseMessage response,
        SourceChannelId channelId,
        CancellationToken cancellationToken)
    {
        var diagnostic = await ReadDiagnosticAsync(response, cancellationToken);
        var failure = ClassifyFailure(response.StatusCode, diagnostic);
        var safeDiagnostic = FormatDiagnosticForLog(diagnostic);
        var diagnosticMessage = string.IsNullOrWhiteSpace(safeDiagnostic)
            ? $"HDHomeRun stream request returned HTTP {(int)response.StatusCode}."
            : $"HDHomeRun stream request returned HTTP {(int)response.StatusCode}: {safeDiagnostic}";
        return new PhysicalStreamAcquisitionException(
            failure.Category,
            failure.Code,
            failure.Message,
            failure.Retryable,
            Snapshot.Id,
            channelId,
            new HttpRequestException(diagnosticMessage, inner: null, response.StatusCode));
    }

    private static async Task<string?> ReadDiagnosticAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Headers.TryGetValues("X-HDHomeRun-Error", out var values))
        {
            var header = SanitizeDiagnostic(values.FirstOrDefault());
            if (!string.IsNullOrWhiteSpace(header))
            {
                return header;
            }
        }

        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[MaximumDiagnosticBytes];
        var length = await content.ReadAsync(buffer.AsMemory(), cancellationToken);
        return SanitizeDiagnostic(Encoding.UTF8.GetString(buffer, 0, length));
    }

    private static string? SanitizeDiagnostic(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var sanitized = new string(value
            .Where(character => !char.IsControl(character) || character == ' ')
            .Take(MaximumDiagnosticCharacters)
            .ToArray())
            .Trim();
        return sanitized.Length == 0 ? null : sanitized;
    }

    private static HDHomeRunStreamFailure ClassifyFailure(HttpStatusCode statusCode, string? diagnostic)
    {
        var code = TryGetDiagnosticCode(diagnostic);
        if (code == 811 || ContainsAny(diagnostic, "content protection", "protected content", "copy protected"))
        {
            return new HDHomeRunStreamFailure(
                PhysicalStreamFailureCategory.ProtectedContent,
                "protected_content",
                "The HDHomeRun source is protected and cannot be streamed.",
                Retryable: false);
        }
        if (code == 807 || ContainsAny(diagnostic, "no video data", "no media data"))
        {
            return new HDHomeRunStreamFailure(
                PhysicalStreamFailureCategory.NoData,
                "source_no_video_data",
                "The HDHomeRun source returned no video data.",
                Retryable: true);
        }
        if (ContainsAny(diagnostic, "no signal", "weak signal", "unable to lock", "no lock"))
        {
            return new HDHomeRunStreamFailure(
                PhysicalStreamFailureCategory.NoSignal,
                "source_no_signal",
                "The HDHomeRun source could not lock a usable signal.",
                Retryable: true);
        }
        if (ContainsAny(diagnostic, "all tuners in use", "tuner busy", "tuner in use", "no tuner available", "no tuners available", "resource locked"))
        {
            return new HDHomeRunStreamFailure(
                PhysicalStreamFailureCategory.Capacity,
                "source_capacity_exceeded",
                "The HDHomeRun device has no available tuner capacity.",
                Retryable: true);
        }
        if (statusCode == HttpStatusCode.NotFound)
        {
            return new HDHomeRunStreamFailure(
                PhysicalStreamFailureCategory.NotFound,
                "source_not_found",
                "The HDHomeRun source channel is no longer available.",
                Retryable: false);
        }
        if (statusCode == HttpStatusCode.BadRequest)
        {
            return new HDHomeRunStreamFailure(
                PhysicalStreamFailureCategory.InvalidInput,
                "source_invalid",
                "The HDHomeRun device rejected the stream request.",
                Retryable: false);
        }
        return new HDHomeRunStreamFailure(
            PhysicalStreamFailureCategory.Unavailable,
            "source_unavailable",
            "The HDHomeRun source is temporarily unavailable.",
            Retryable: true);
    }

    private static int? TryGetDiagnosticCode(string? diagnostic)
    {
        if (string.IsNullOrWhiteSpace(diagnostic))
        {
            return null;
        }

        var separator = diagnostic.IndexOf(' ');
        var code = separator < 0 ? diagnostic : diagnostic[..separator];
        return int.TryParse(code, out var parsed) ? parsed : null;
    }

    private static bool ContainsAny(string? diagnostic, params string[] values) =>
        diagnostic is not null && values.Any(value => diagnostic.Contains(value, StringComparison.OrdinalIgnoreCase));

    private static string? FormatDiagnosticForLog(string? diagnostic)
    {
        if (string.IsNullOrWhiteSpace(diagnostic))
        {
            return null;
        }
        if (ContainsAny(diagnostic, "http://", "https://", "authorization", "password", "token=", "auth=", "api_key", "apikey"))
        {
            var code = TryGetDiagnosticCode(diagnostic);
            return code.HasValue ? $"{code.Value} HDHomeRun diagnostic redacted." : "HDHomeRun diagnostic redacted.";
        }
        return diagnostic;
    }

    private sealed record HDHomeRunStreamFailure(
        PhysicalStreamFailureCategory Category,
        string Code,
        string Message,
        bool Retryable);

    private static IReadOnlyDictionary<string, string> CreateMetadata(HDHomeRunChannel channel)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["hd"] = channel.HD.ToString(),
            ["drm"] = channel.DRM.ToString(),
            ["favorite"] = channel.Favorite.ToString()
        };

        AddIfPresent(metadata, "videoCodec", channel.VideoCodec);
        AddIfPresent(metadata, "audioCodec", channel.AudioCodec);
        AddIfPresent(metadata, "tags", channel.Tags);
        if (channel.SignalStrength.HasValue)
        {
            metadata["signalStrength"] = channel.SignalStrength.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (channel.SignalQuality.HasValue)
        {
            metadata["signalQuality"] = channel.SignalQuality.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return metadata;
    }

    private async Task<HDHomeRunDevice> GetProtocolDeviceAsync(CancellationToken cancellationToken)
    {
        var existing = Volatile.Read(ref _protocolDevice);
        if (existing is not null)
        {
            return existing;
        }

        await _protocolDeviceInitialization.WaitAsync(cancellationToken);
        try
        {
            existing = Volatile.Read(ref _protocolDevice);
            if (existing is not null)
            {
                return existing;
            }

            var created = await _deviceService.GetDeviceByIpAsync(_address, cancellationToken)
                ?? throw new InvalidOperationException($"HDHomeRun device {Snapshot.Id} is unavailable through the native control protocol.");
            Volatile.Write(ref _protocolDevice, created);
            return created;
        }
        finally
        {
            _protocolDeviceInitialization.Release();
        }
    }

    private static PhysicalReceiverStatus MapStatus(TunerStatus status) =>
        new()
        {
            Index = status.TunerIndex,
            IsActive = status.IsActive,
            IsStreaming = status.IsStreaming,
            HasLock = status.HasLock,
            Channel = status.Channel,
            VirtualChannel = status.VirtualChannel,
            Target = status.Target,
            LockType = status.LockType,
            SignalStrength = status.SignalStrength,
            SignalQuality = status.SignalToNoiseQuality,
            SymbolQuality = status.SymbolErrorQuality,
            BitsPerSecond = status.BitsPerSecond,
            PacketsPerSecond = status.PacketsPerSecond
        };

    private static void AddIfPresent(IDictionary<string, string> metadata, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            metadata[key] = value;
        }
    }
}
