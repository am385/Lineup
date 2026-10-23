using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;
using Lineup.LineupLink.Protocol;

namespace Lineup.LineupLink.Physical;

/// <summary>Retrieves guide data through one active Lineup Link physical device.</summary>
public interface ILineupLinkGuideCapability
{
    /// <summary>Gets the physical-device identity.</summary>
    PhysicalDeviceId PhysicalDeviceId { get; }

    /// <summary>Gets the remote manifest.</summary>
    LineupLinkManifest Manifest { get; }

    /// <summary>Gets the remote virtual guide.</summary>
    Task<LineupLinkGuide> GetGuideAsync(CancellationToken cancellationToken = default);
}

/// <summary>Runtime physical input backed by one remote Lineup Link virtual device.</summary>
public sealed class LineupLinkPhysicalTunerDevice :
    IPhysicalTunerDevice,
    ILineupLinkGuideCapability,
    IPhysicalDeviceStatusCapability,
    IPhysicalDeviceInformationCapability,
    IPhysicalStreamDiagnosticCapability,
    IDisposable
{
    private readonly LineupLinkClient _client;
    private readonly PhysicalDeviceId _deviceId;
    private readonly string _name;
    private readonly object _receiverLock = new();
    private IReadOnlyDictionary<string, LineupLinkChannelReference> _channels = new Dictionary<string, LineupLinkChannelReference>(StringComparer.Ordinal);
    private IReadOnlyDictionary<int, ActiveReceiver> _activeReceivers = new Dictionary<int, ActiveReceiver>();
    private LineupLinkManifest _manifest;

    /// <summary>Initializes a remote physical input.</summary>
    public LineupLinkPhysicalTunerDevice(PhysicalDeviceId deviceId, string name, LineupLinkManifest manifest, LineupLinkClient client)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _deviceId = deviceId;
        _name = name;
        _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    /// <inheritdoc />
    public PhysicalDeviceSnapshot Snapshot
    {
        get
        {
            var manifest = Manifest;
            return new PhysicalDeviceSnapshot
            {
                Id = _deviceId,
                Kind = LineupLinkProtocol.AdapterKind,
                Name = _name,
                Capacity = new PhysicalTunerCapacity(manifest.TunerCount)
            };
        }
    }

    /// <inheritdoc />
    public PhysicalDeviceId PhysicalDeviceId => _deviceId;

    /// <inheritdoc />
    public LineupLinkManifest Manifest => Volatile.Read(ref _manifest);

    /// <inheritdoc />
    public async Task<PhysicalDeviceStatusSnapshot> RefreshStatusAsync(CancellationToken cancellationToken = default)
    {
        var manifest = await _client.GetManifestAsync(cancellationToken);
        Volatile.Write(ref _manifest, manifest);
        return CreateStatusSnapshot(manifest);
    }

    /// <inheritdoc />
    public async Task<PhysicalDeviceStatusSnapshot> RefreshDeviceInformationAsync(CancellationToken cancellationToken = default)
    {
        var manifest = await _client.GetManifestAsync(cancellationToken);
        Volatile.Write(ref _manifest, manifest);
        return CreateStatusSnapshot(manifest);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PhysicalSourceChannel>> RefreshLineupAsync(CancellationToken cancellationToken = default)
    {
        var lineup = await _client.GetLineupAsync(cancellationToken);
        var mapped = lineup.Channels.Select(channel =>
        {
            var providerKey = LineupLinkIdentity.CreateProviderKey(Manifest.InstanceId, Manifest.VirtualDeviceId, channel.Key);
            return (
                ProviderKey: providerKey,
                Reference: new LineupLinkChannelReference(channel.Key, channel.Number, channel.Name),
                Channel: new PhysicalSourceChannel
                {
                    Id = new SourceChannelId(_deviceId, providerKey),
                    ProviderNumber = channel.Number,
                    ProviderName = channel.Name,
                    Metadata = new Dictionary<string, string>(channel.Metadata, StringComparer.Ordinal)
                    {
                        ["lineupLinkInstanceId"] = Manifest.InstanceId.ToString("D"),
                        ["lineupLinkVirtualDeviceId"] = Manifest.VirtualDeviceId.ToString("D")
                    }
                });
        }).ToArray();
        Volatile.Write(ref _channels, mapped.ToDictionary(item => item.ProviderKey, item => item.Reference, StringComparer.Ordinal));
        return mapped.Select(item => item.Channel).ToArray();
    }

    /// <inheritdoc />
    public async ValueTask<IPhysicalStreamLease> OpenStreamAsync(PhysicalStreamRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ChannelId.PhysicalDeviceId != _deviceId)
        {
            throw new ArgumentException("The requested channel belongs to a different physical device.", nameof(request));
        }
        if (!Volatile.Read(ref _channels).TryGetValue(request.ChannelId.ProviderKey, out var channel))
        {
            throw new KeyNotFoundException($"Lineup Link channel '{request.ChannelId.ProviderKey}' is not active.");
        }

        HttpResponseMessage response;
        try
        {
            response = await _client.OpenStreamAsync(channel.RemoteKey, cancellationToken);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new PhysicalStreamAcquisitionException(
                PhysicalStreamFailureCategory.Unavailable,
                "source_unauthorized",
                "The remote Lineup Link device rejected the configured access token.",
                false,
                _deviceId,
                request.ChannelId,
                exception);
        }
        catch (LineupLinkRemoteException exception)
        {
            throw new PhysicalStreamAcquisitionException(
                MapFailureCategory(exception.Code),
                exception.Code,
                exception.Message,
                exception.Retryable,
                _deviceId,
                request.ChannelId,
                exception);
        }
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var receiverIndex = ReserveReceiver(channel);
            return new LineupLinkPhysicalStreamLease(response, stream, () => ReleaseReceiver(receiverIndex));
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public Task<LineupLinkGuide> GetGuideAsync(CancellationToken cancellationToken = default) => _client.GetGuideAsync(cancellationToken);

    /// <inheritdoc />
    public Uri CreateDiagnosticUri(string providerKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
        return Volatile.Read(ref _channels).TryGetValue(providerKey, out var channel)
            ? _client.CreateStreamUri(channel.RemoteKey)
            : throw new KeyNotFoundException($"Lineup Link channel '{providerKey}' is not active.");
    }

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();

    private static PhysicalStreamFailureCategory MapFailureCategory(string code) =>
        code switch
        {
            "source_capacity_exceeded" or "stream_limit" => PhysicalStreamFailureCategory.Capacity,
            "source_no_signal" => PhysicalStreamFailureCategory.NoSignal,
            "source_no_video_data" => PhysicalStreamFailureCategory.NoData,
            "source_not_found" or "channel_not_found" => PhysicalStreamFailureCategory.NotFound,
            "source_invalid" or "invalid_request" => PhysicalStreamFailureCategory.InvalidInput,
            "protected_content" => PhysicalStreamFailureCategory.ProtectedContent,
            _ => PhysicalStreamFailureCategory.Unavailable
        };

    private PhysicalDeviceStatusSnapshot CreateStatusSnapshot(LineupLinkManifest manifest)
    {
        IReadOnlyDictionary<int, ActiveReceiver> activeReceivers;
        lock (_receiverLock)
        {
            activeReceivers = _activeReceivers;
        }

        var receiverCount = manifest.TunerCount ?? (activeReceivers.Count == 0 ? 0 : activeReceivers.Keys.Max() + 1);
        var receivers = Enumerable.Range(0, receiverCount)
            .Select(index => activeReceivers.TryGetValue(index, out var active)
                ? new PhysicalReceiverStatus
                {
                    Index = index,
                    IsActive = true,
                    IsStreaming = true,
                    Channel = active.Name,
                    VirtualChannel = active.Number,
                    Target = active.Target
                }
                : new PhysicalReceiverStatus { Index = index })
            .ToArray();
        return new PhysicalDeviceStatusSnapshot
        {
            Device = Snapshot,
            Details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Remote name"] = manifest.Name,
                ["Remote base URL"] = _client.BaseUri.ToString(),
                ["Lineup instance ID"] = manifest.InstanceId.ToString("D"),
                ["Virtual device ID"] = manifest.VirtualDeviceId.ToString("D"),
                ["Protocol version"] = manifest.ProtocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Tuners"] = manifest.TunerCount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "Unbounded",
                ["Guide"] = manifest.GuideAvailable ? "Available" : "Unavailable"
            },
            Receivers = receivers
        };
    }

    private int ReserveReceiver(LineupLinkChannelReference channel)
    {
        lock (_receiverLock)
        {
            var receiverIndex = 0;
            while (_activeReceivers.ContainsKey(receiverIndex))
            {
                receiverIndex++;
            }
            var capacity = Manifest.TunerCount;
            if (capacity.HasValue && receiverIndex >= capacity.Value)
            {
                throw new InvalidOperationException($"Lineup Link device {_deviceId} has no available receivers.");
            }

            _activeReceivers = new Dictionary<int, ActiveReceiver>(_activeReceivers)
            {
                [receiverIndex] = new ActiveReceiver(channel.Number, channel.Name, _client.CreateStreamUri(channel.RemoteKey).ToString())
            };
            return receiverIndex;
        }
    }

    private void ReleaseReceiver(int receiverIndex)
    {
        lock (_receiverLock)
        {
            if (!_activeReceivers.ContainsKey(receiverIndex))
            {
                return;
            }
            var activeReceivers = new Dictionary<int, ActiveReceiver>(_activeReceivers);
            activeReceivers.Remove(receiverIndex);
            _activeReceivers = activeReceivers;
        }
    }

    private sealed record LineupLinkChannelReference(string RemoteKey, string Number, string Name);

    private sealed record ActiveReceiver(string Number, string Name, string Target);

    private sealed class LineupLinkPhysicalStreamLease : IPhysicalStreamLease
    {
        private readonly string? _contentType;
        private Action? _releaseReceiver;
        private HttpResponseMessage? _response;
        private Stream? _stream;

        public LineupLinkPhysicalStreamLease(HttpResponseMessage response, Stream stream, Action releaseReceiver)
        {
            _response = response;
            _stream = stream;
            _releaseReceiver = releaseReceiver;
            _contentType = response.Content.Headers.ContentType?.MediaType;
        }

        public Stream Stream => _stream ?? throw new ObjectDisposedException(nameof(LineupLinkPhysicalStreamLease));

        public string? ContentType => _contentType;

        public async ValueTask DisposeAsync()
        {
            var ownedStream = Interlocked.Exchange(ref _stream, null);
            var ownedResponse = Interlocked.Exchange(ref _response, null);
            var releaseReceiver = Interlocked.Exchange(ref _releaseReceiver, null);
            try
            {
                if (ownedStream is not null)
                {
                    await ownedStream.DisposeAsync();
                }
                ownedResponse?.Dispose();
            }
            finally
            {
                releaseReceiver?.Invoke();
            }
        }
    }
}
