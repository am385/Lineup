using Lineup.Core.Settings;
using Lineup.Core.Storage;

namespace Lineup.Core.Streaming;

/// <summary>
/// Resolves channel numbers through the provider-neutral configured topology.
/// </summary>
public interface IStreamingRouteResolver
{
    /// <summary>Resolves a channel for one configured virtual device.</summary>
    Task<ResolvedStreamingRoute?> ResolveVirtualAsync(VirtualDeviceId virtualDeviceId, string channelNumber, CancellationToken cancellationToken = default);

    /// <summary>Gets the published lineup for one configured virtual device.</summary>
    Task<IReadOnlyList<ResolvedVirtualChannel>> GetVirtualLineupAsync(VirtualDeviceId virtualDeviceId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Canonical topology route from one virtual channel to its preferred physical source.
/// </summary>
public sealed record ResolvedStreamingRoute
{
    /// <summary>Gets the selected virtual device.</summary>
    public required VirtualDeviceDefinition VirtualDevice { get; init; }

    /// <summary>Gets the selected physical device.</summary>
    public required IPhysicalTunerDevice PhysicalDevice { get; init; }

    /// <summary>Gets the selected physical source channel.</summary>
    public required SourceChannelId SourceChannelId { get; init; }

    /// <summary>Gets ordered equivalent source candidates used for future failover.</summary>
    public required IReadOnlyList<SourceChannelId> SourceCandidates { get; init; }

    /// <summary>Gets a diagnostic URI for the selected physical source.</summary>
    public required Uri DiagnosticUri { get; init; }

    /// <summary>Gets the Core acquisition descriptor.</summary>
    public required StreamAcquisitionDescriptor Acquisition { get; init; }

    /// <summary>Gets ordered runtime candidates used for physical failover.</summary>
    public required IReadOnlyList<ResolvedStreamingCandidate> Candidates { get; init; }

    /// <summary>Gets the effective physical-stage profile for the preferred source.</summary>
    public required PhysicalTranscodeProfile PhysicalProfile { get; init; }

    /// <summary>Gets the effective virtual-stage profile for this output.</summary>
    public required VirtualTranscodeProfile VirtualProfile { get; init; }

    /// <summary>Creates the preferred physical acquisition for compatibility callers.</summary>
    public ValueTask<IStreamSourceLease> OpenAsync(CancellationToken cancellationToken) => Candidates[0].OpenAsync(cancellationToken);
}

/// <summary>One ordered physical source candidate for a resolved virtual channel.</summary>
public sealed record ResolvedStreamingCandidate
{
    /// <summary>Gets the physical adapter.</summary>
    public required IPhysicalTunerDevice PhysicalDevice { get; init; }

    /// <summary>Gets the provider channel on the physical adapter.</summary>
    public required SourceChannelId SourceChannelId { get; init; }

    /// <summary>Gets the exact graph acquisition identity and capacity.</summary>
    public required StreamAcquisitionDescriptor Acquisition { get; init; }

    /// <summary>Gets the physical normalization profile belonging to this adapter.</summary>
    public required PhysicalTranscodeProfile PhysicalProfile { get; init; }

    /// <summary>Gets the physical source URI used for diagnostics.</summary>
    public required Uri DiagnosticUri { get; init; }

    /// <summary>Gets normalized provider metadata for the selected physical source channel.</summary>
    public IReadOnlyDictionary<string, string> SourceMetadata { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Opens this exact physical source; Core owns its capacity lease.</summary>
    public async ValueTask<IStreamSourceLease> OpenAsync(CancellationToken cancellationToken)
    {
        var lease = await PhysicalDevice.OpenStreamAsync(new PhysicalStreamRequest { ChannelId = SourceChannelId }, cancellationToken);
        return new PhysicalSourceLease(lease);
    }

    private sealed class PhysicalSourceLease(IPhysicalStreamLease lease) : IStreamSourceLease
    {
        public Stream Stream => lease.Stream;

        public string? ContentType => lease.ContentType;

        public ValueTask DisposeAsync() => lease.DisposeAsync();
    }
}

/// <summary>
/// Lazily activates configured physical adapters, refreshes topology, and resolves virtual routes.
/// </summary>
public sealed class StreamingRouteResolver : IStreamingRouteResolver
{
    private readonly ILineupSettingsService _settings;
    private readonly IPhysicalDeviceCatalog _catalog;
    private readonly IPhysicalLineupService _lineups;
    private readonly StreamingTopologyStore _topologyStore;
    private readonly IStreamingAdapterRegistry? _adapters;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private StreamingConfiguration? _activeConfiguration;

    /// <summary>Initializes the topology route resolver.</summary>
    public StreamingRouteResolver(
        ILineupSettingsService settings,
        IPhysicalDeviceCatalog catalog,
        IPhysicalLineupService lineups,
        StreamingTopologyStore topologyStore,
        IStreamingAdapterRegistry? adapters = null)
    {
        _settings = settings;
        _catalog = catalog;
        _lineups = lineups;
        _topologyStore = topologyStore;
        _adapters = adapters;
    }

    /// <inheritdoc />
    public Task<ResolvedStreamingRoute?> ResolveVirtualAsync(
        VirtualDeviceId virtualDeviceId,
        string channelNumber,
        CancellationToken cancellationToken = default) =>
        ResolveAsync(virtualDeviceId, channelNumber, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ResolvedVirtualChannel>> GetVirtualLineupAsync(
        VirtualDeviceId virtualDeviceId,
        CancellationToken cancellationToken = default)
    {
        var configuration = _settings.Settings.Streaming;
        var virtualDevice = configuration.VirtualDevices.SingleOrDefault(device => device.Enabled && device.Id == virtualDeviceId)
            ?? throw new KeyNotFoundException("The requested configured virtual device was not found.");
        var physicalDefinitions = _adapters is null
            ? configuration.PhysicalDevices
            : VirtualDeviceSubscriptionResolver.Resolve(
                virtualDevice,
                configuration.PhysicalDevices,
                _adapters.GetVirtual(virtualDevice.Adapter.Kind));
        var identities = await _topologyStore.ReadActiveAsync(cancellationToken);
        return ChannelTopologyResolver.Resolve(virtualDevice, physicalDefinitions, identities).Channels;
    }

    private async Task<ResolvedStreamingRoute?> ResolveAsync(
        VirtualDeviceId virtualDeviceId,
        string channelNumber,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelNumber);
        var configuration = _settings.Settings.Streaming;
        if (!configuration.PhysicalDevices.Any(device => device.Enabled))
        {
            throw new InvalidOperationException("No enabled physical streaming devices are configured.");
        }

        await EnsureCatalogAsync(configuration, cancellationToken);
        var virtualDevice = configuration.VirtualDevices.SingleOrDefault(device => device.Enabled && device.Id == virtualDeviceId)
            ?? throw new KeyNotFoundException("The requested configured virtual device was not found.");
        var physicalDefinitions = _adapters is null
            ? configuration.PhysicalDevices
            : VirtualDeviceSubscriptionResolver.Resolve(
                virtualDevice,
                configuration.PhysicalDevices,
                _adapters.GetVirtual(virtualDevice.Adapter.Kind));
        var identities = await _topologyStore.ReadActiveAsync(cancellationToken);
        var resolution = ChannelTopologyResolver.Resolve(virtualDevice, physicalDefinitions, identities);
        var channel = resolution.Channels.SingleOrDefault(candidate =>
            string.Equals(candidate.Number, channelNumber.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Channel '{channelNumber}' is not available to virtual device {virtualDevice.Id}.");
        var liveDevices = _catalog.Devices.ToDictionary(device => device.Snapshot.Id);
        var availableSources = channel.Sources.Where(source => liveDevices.ContainsKey(source.PhysicalDeviceId)).ToArray();
        if (availableSources.Length == 0)
        {
            throw new InvalidOperationException($"Channel '{channelNumber}' has no active physical source.");
        }
        var sourceId = availableSources[0];
        var physicalDevice = liveDevices[sourceId.PhysicalDeviceId];
        var physicalDefinition = configuration.PhysicalDevices.Single(device => device.Id == sourceId.PhysicalDeviceId);
        var diagnosticUri = CreateDiagnosticUri(physicalDevice, sourceId.ProviderKey);
        var definitions = configuration.PhysicalDevices.ToDictionary(device => device.Id);
        var sourceMetadata = identities.ToDictionary(identity => identity.Source.Id, identity => identity.Source.Metadata);
        var candidates = availableSources
            .Select(candidate =>
            {
                var device = liveDevices[candidate.PhysicalDeviceId];
                var definition = definitions[candidate.PhysicalDeviceId];
                var candidateRequest = new PhysicalStreamRequest { ChannelId = candidate };
                return new ResolvedStreamingCandidate
                {
                    PhysicalDevice = device,
                    SourceChannelId = candidate,
                    DiagnosticUri = CreateDiagnosticUri(device, candidate.ProviderKey),
                    SourceMetadata = sourceMetadata[candidate],
                    Acquisition = new StreamAcquisitionDescriptor(
                        candidate.PhysicalDeviceId,
                        PhysicalAcquisitionKey.Create(candidateRequest),
                        device.Snapshot.Capacity),
                    PhysicalProfile = definition.TranscodeOverrides.Apply(configuration.PhysicalDefaults)
                };
            })
            .ToArray();
        return new ResolvedStreamingRoute
        {
            VirtualDevice = virtualDevice,
            PhysicalDevice = physicalDevice,
            SourceChannelId = sourceId,
            SourceCandidates = availableSources,
            DiagnosticUri = diagnosticUri,
            Candidates = candidates,
            Acquisition = candidates[0].Acquisition,
            PhysicalProfile = physicalDefinition.TranscodeOverrides.Apply(configuration.PhysicalDefaults),
            VirtualProfile = virtualDevice.TranscodeOverrides.Apply(configuration.VirtualDefaults)
        };
    }

    private async Task EnsureCatalogAsync(StreamingConfiguration configuration, CancellationToken cancellationToken)
    {
        if (ReferenceEquals(Volatile.Read(ref _activeConfiguration), configuration))
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (ReferenceEquals(_activeConfiguration, configuration))
            {
                return;
            }
            await _lineups.RefreshAsync(configuration, cancellationToken);
            Volatile.Write(ref _activeConfiguration, configuration);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static Uri CreateDiagnosticUri(IPhysicalTunerDevice device, string providerKey)
    {
        return device is IPhysicalStreamDiagnosticCapability capability
            ? capability.CreateDiagnosticUri(providerKey)
            : new Uri($"lineup://physical/{device.Snapshot.Id.Value:D}/{Uri.EscapeDataString(providerKey)}");
    }
}
