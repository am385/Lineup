using Lineup.Core.Storage;
using Lineup.Core.Common.Streaming;
using Lineup.Core.Extensibility.Streaming;
using Lineup.Core.Settings;

namespace Lineup.Core.Guides;

/// <summary>
/// Builds host-neutral guide projections from the configured physical-device union.
/// </summary>
public sealed class GuideQueryService : IGuideQueryService
{
    private static readonly VirtualDeviceId UnionVirtualDeviceId = new(Guid.Parse("8F58C8DC-2B33-4C17-B3B3-18BA69936C73"));
    private readonly DeviceGuideStore _guides;
    private readonly IStreamingAdapterRegistry _adapters;

    /// <summary>Initializes a guide query service.</summary>
    public GuideQueryService(StreamingTopologyStore topology, DeviceGuideStore guides, IStreamingAdapterRegistry adapters)
    {
        ArgumentNullException.ThrowIfNull(topology);
        _guides = guides;
        _adapters = adapters;
    }

    /// <inheritdoc />
    public IReadOnlyList<GuideSourceDiagnostic> GetSourceDiagnostics(StreamingConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return GuideSourceSubscriptionResolver.ResolveEligibleSources(configuration.GuideSources, configuration.PhysicalDevices, _adapters).Diagnostics;
    }

    /// <inheritdoc />
    public Task<VirtualGuideSnapshot> BuildUnionSnapshotAsync(StreamingConfiguration configuration, DateTimeOffset? startUtc = null, DateTimeOffset? endUtc = null, CancellationToken cancellationToken = default) =>
        BuildUnionAsync(configuration, startUtc, endUtc, true, cancellationToken);

    /// <inheritdoc />
    public Task<VirtualGuideSnapshot> BuildUnionDirectoryAsync(StreamingConfiguration configuration, CancellationToken cancellationToken = default) =>
        BuildUnionAsync(configuration, null, null, false, cancellationToken);

    private async Task<VirtualGuideSnapshot> BuildUnionAsync(StreamingConfiguration configuration, DateTimeOffset? startUtc, DateTimeOffset? endUtc, bool includePrograms, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var unionDevice = CreateUnionDevice();
        var eligibility = GuideSourceSubscriptionResolver.ResolveEligibleSources(configuration.GuideSources, configuration.PhysicalDevices, _adapters, cancellationToken);
        var snapshot = await _guides.BuildResolvedSnapshotAsync(
            UnionVirtualDeviceId,
            identities => ChannelTopologyResolver.Resolve(unionDevice, configuration.PhysicalDevices, identities).Channels,
            eligibility.Sources.Select(source => source.Scope).ToArray(),
            startUtc,
            endUtc,
            configuration.PhysicalDevices.Where(device => device.Enabled).Select(device => device.Id).ToArray(),
            includePrograms,
            cancellationToken);
        return snapshot with
        {
            SourceDiagnostics = eligibility.Diagnostics,
            Generation = snapshot.Generation! with { ConfigurationRevision = ConfigurationRevision.Create(configuration) }
        };
    }

    /// <inheritdoc />
    public async Task<VirtualGuideSnapshot> BuildOutputSnapshotAsync(
        StreamingConfiguration configuration,
        GuideOutputDefinition output,
        DateTimeOffset? startUtc = null,
        DateTimeOffset? endUtc = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(output);
        var selectedVirtualDevices = ResolveSelectedVirtualDevices(configuration, output);
        var selectedPhysicalDeviceIds = selectedVirtualDevices
            .SelectMany(device => VirtualDeviceSubscriptionResolver.Resolve(
                device,
                configuration.PhysicalDevices,
                _adapters.GetVirtual(device.Adapter.Kind)))
            .Select(device => device.Id)
            .Distinct()
            .ToHashSet();
        var projectionDevice = new VirtualDeviceDefinition
        {
            Id = new VirtualDeviceId(output.Id.Value),
            Name = output.Name,
            Enabled = output.Enabled,
            Adapter = new AdapterConfiguration("guide-output", default),
            Subscription = new PhysicalDeviceSubscription
            {
                Mode = PhysicalDeviceSubscriptionMode.Selected,
                SelectedDeviceIds = selectedPhysicalDeviceIds
            }
        };
        var eligibility = GuideSourceSubscriptionResolver.ResolveEligibleSources(
            ResolveSelectedGuideSources(configuration, output), configuration.PhysicalDevices, _adapters, cancellationToken);
        var activeGuideScopes = eligibility.Sources
            .Select(source => source.Scope)
            .Select(scope => new GuideSourceScope(
                scope.SourceId,
                scope.PhysicalDeviceIds.Where(selectedPhysicalDeviceIds.Contains).ToArray()))
            .Where(scope => scope.PhysicalDeviceIds.Count > 0)
            .ToArray();
        var snapshot = await _guides.BuildResolvedSnapshotAsync(
            projectionDevice.Id,
            identities => ChannelTopologyResolver.Resolve(projectionDevice, configuration.PhysicalDevices, identities).Channels,
            activeGuideScopes,
            startUtc,
            endUtc,
            selectedPhysicalDeviceIds,
            cancellationToken: cancellationToken);
        return snapshot with
        {
            SourceDiagnostics = eligibility.Diagnostics,
            Generation = snapshot.Generation! with { ConfigurationRevision = ConfigurationRevision.Create(configuration) }
        };
    }

    /// <inheritdoc />
    public async Task<GuideStatistics> GetStatisticsAsync(StreamingConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var unionDevice = CreateUnionDevice();
        var eligibility = GuideSourceSubscriptionResolver.ResolveEligibleSources(configuration.GuideSources, configuration.PhysicalDevices, _adapters, cancellationToken);
        var statistics = await _guides.GetStatisticsAsync(
            identities => ChannelTopologyResolver.Resolve(unionDevice, configuration.PhysicalDevices, identities).Channels,
            eligibility.Sources.Select(source => source.Scope).ToArray(),
            configuration.PhysicalDevices.Where(device => device.Enabled).Select(device => device.Id).ToArray(), cancellationToken);
        return statistics with
        {
            SourceDiagnostics = eligibility.Diagnostics,
            Generation = statistics.Generation! with { ConfigurationRevision = ConfigurationRevision.Create(configuration) }
        };
    }

    private static VirtualDeviceDefinition CreateUnionDevice() => new()
    {
        Id = UnionVirtualDeviceId,
        Name = "Lineup",
        Enabled = true,
        Adapter = new AdapterConfiguration("lineup-union", default),
        Subscription = new PhysicalDeviceSubscription { Mode = PhysicalDeviceSubscriptionMode.All }
    };

    private static IReadOnlyList<VirtualDeviceDefinition> ResolveSelectedVirtualDevices(
        StreamingConfiguration configuration,
        GuideOutputDefinition output)
    {
        var enabled = configuration.VirtualDevices.Where(device => device.Enabled).ToArray();
        return output.VirtualDeviceSelectionMode == GuideOutputSelectionMode.All ||
            output.SelectedVirtualDeviceIds.Count == 0
                ? enabled
                : enabled.Where(device => output.SelectedVirtualDeviceIds.Contains(device.Id)).ToArray();
    }

    private static IReadOnlyList<GuideSourceDefinition> ResolveSelectedGuideSources(
        StreamingConfiguration configuration,
        GuideOutputDefinition output)
    {
        var enabled = configuration.GuideSources.Where(source => source.Enabled).ToArray();
        return output.GuideSourceSelectionMode == GuideOutputSelectionMode.All ||
            output.SelectedGuideSourceIds.Count == 0
                ? enabled
                : enabled.Where(source => output.SelectedGuideSourceIds.Contains(source.Id)).ToArray();
    }
}
