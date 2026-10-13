using Lineup.Core.Storage;
using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;

namespace Lineup.Core.Guides;

/// <summary>
/// Builds host-neutral guide projections from the configured physical-device union.
/// </summary>
public sealed class GuideQueryService : IGuideQueryService
{
    private static readonly VirtualDeviceId UnionVirtualDeviceId = new(Guid.Parse("8F58C8DC-2B33-4C17-B3B3-18BA69936C73"));
    private readonly StreamingTopologyStore _topology;
    private readonly DeviceGuideStore _guides;
    private readonly IStreamingAdapterRegistry _adapters;

    /// <summary>Initializes a guide query service.</summary>
    public GuideQueryService(StreamingTopologyStore topology, DeviceGuideStore guides, IStreamingAdapterRegistry adapters)
    {
        _topology = topology;
        _guides = guides;
        _adapters = adapters;
    }

    /// <inheritdoc />
    public async Task<VirtualGuideSnapshot> BuildUnionSnapshotAsync(
        StreamingConfiguration configuration,
        DateTimeOffset? startUtc = null,
        DateTimeOffset? endUtc = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var unionDevice = new VirtualDeviceDefinition
        {
            Id = UnionVirtualDeviceId,
            Name = "Lineup",
            Enabled = true,
            Adapter = new AdapterConfiguration("lineup-union", default),
            Subscription = new PhysicalDeviceSubscription { Mode = PhysicalDeviceSubscriptionMode.All }
        };
        var identities = await _topology.ReadActiveAsync(cancellationToken);
        var resolution = ChannelTopologyResolver.Resolve(unionDevice, configuration.PhysicalDevices, identities);
        var activeGuideScopes = configuration.GuideSources
            .Where(source => source.Enabled)
            .Select(source => GuideSourceSubscriptionResolver.Resolve(source, configuration.PhysicalDevices, _adapters.GetGuide(source.Adapter.Kind)))
            .Where(scope => scope.PhysicalDeviceIds.Count > 0)
            .ToArray();
        return await _guides.BuildSnapshotAsync(
            UnionVirtualDeviceId,
            resolution.Channels,
            activeGuideScopes,
            startUtc,
            endUtc,
            cancellationToken);
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
        var identities = await _topology.ReadActiveAsync(cancellationToken);
        var resolution = ChannelTopologyResolver.Resolve(projectionDevice, configuration.PhysicalDevices, identities);
        var activeGuideScopes = ResolveSelectedGuideSources(configuration, output)
            .Select(source => GuideSourceSubscriptionResolver.Resolve(
                source,
                configuration.PhysicalDevices,
                _adapters.GetGuide(source.Adapter.Kind)))
            .Select(scope => new GuideSourceScope(
                scope.SourceId,
                scope.PhysicalDeviceIds.Where(selectedPhysicalDeviceIds.Contains).ToArray()))
            .Where(scope => scope.PhysicalDeviceIds.Count > 0)
            .ToArray();
        return await _guides.BuildSnapshotAsync(
            projectionDevice.Id,
            resolution.Channels,
            activeGuideScopes,
            startUtc,
            endUtc,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<GuideStatistics> GetStatisticsAsync(StreamingConfiguration configuration, CancellationToken cancellationToken = default)
    {
        var snapshot = await BuildUnionSnapshotAsync(configuration, cancellationToken: cancellationToken);
        var programs = snapshot.Channels.SelectMany(channel => channel.Programs).ToArray();
        var latestByChannel = snapshot.Channels
            .Select(channel => channel.Programs.Count == 0 ? (DateTimeOffset?)null : channel.Programs.Max(program => program.EndUtc))
            .OfType<DateTimeOffset>()
            .ToArray();
        return new GuideStatistics(
            snapshot.Channels.Count,
            programs.Length,
            programs.Length == 0 ? null : programs.Min(program => program.StartUtc),
            programs.Length == 0 ? null : programs.Max(program => program.EndUtc),
            latestByChannel.Length == 0 ? null : latestByChannel.Min());
    }

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
