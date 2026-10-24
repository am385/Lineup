using Lineup.Core.Settings;

namespace Lineup.Core.Streaming;

/// <summary>Identifies one lane in the dashboard streaming flow.</summary>
public enum StreamingFlowLane
{
    /// <summary>Configured physical or synthetic sources.</summary>
    PhysicalSource,
    /// <summary>Optional source-stage processing.</summary>
    PhysicalProcessing,
    /// <summary>Shared acquisition and fan-out.</summary>
    Multiplexer,
    /// <summary>Configured virtual outputs.</summary>
    VirtualOutput,
    /// <summary>Optional output-stage processing.</summary>
    VirtualProcessing,
    /// <summary>Logical downstream consumers.</summary>
    Consumer
}

/// <summary>Identifies the live state of one streaming flow node.</summary>
public enum StreamingFlowState
{
    /// <summary>The configured node is disabled.</summary>
    Disabled,
    /// <summary>The configured node is available but inactive.</summary>
    Idle,
    /// <summary>The node is serving one or more active paths.</summary>
    Active,
    /// <summary>The node is actively transforming media.</summary>
    Transcoding,
    /// <summary>The node is serving multiple logical receivers.</summary>
    Shared,
    /// <summary>The producer is idle but retained for reuse.</summary>
    Warm,
    /// <summary>The configured node is unavailable.</summary>
    Unavailable
}

/// <summary>Identifies one stage in an active streaming path.</summary>
public enum StreamingFlowStageKind
{
    /// <summary>The physical or generated source.</summary>
    PhysicalSource,
    /// <summary>Physical-device processing.</summary>
    SourceProcessing,
    /// <summary>Shared acquisition and fan-out.</summary>
    Multiplexer,
    /// <summary>The selected virtual output.</summary>
    VirtualOutput,
    /// <summary>Virtual-device processing.</summary>
    OutputProcessing,
    /// <summary>The downstream consumer.</summary>
    Consumer
}

/// <summary>One label/value row displayed for a selected flow node.</summary>
public sealed record StreamingFlowDetail(string Label, string Value);

/// <summary>One selectable node in the configured and live streaming topology.</summary>
public sealed record StreamingFlowNode(
    string Id,
    StreamingFlowLane Lane,
    string Label,
    string Subtitle,
    StreamingFlowState State,
    IReadOnlyList<StreamingFlowDetail> Details)
{
    /// <summary>Gets active virtual channel numbers associated with the node.</summary>
    public IReadOnlyList<string> ActiveChannels { get; init; } = [];

    /// <summary>Gets the number of active logical consumer sessions associated with the node.</summary>
    public int ActiveSessionCount { get; init; }
}

/// <summary>Describes one selectable stage in an active streaming path.</summary>
public sealed record StreamingFlowPathStage(
    StreamingFlowStageKind Kind,
    string Label,
    string Subtitle,
    IReadOnlyList<StreamingFlowDetail> Details)
{
    /// <summary>Gets the media entering this stage.</summary>
    public ActiveStreamMediaSnapshot? InputMedia { get; init; }

    /// <summary>Gets the media leaving this stage.</summary>
    public ActiveStreamMediaSnapshot? OutputMedia { get; init; }
}

/// <summary>One active consumer session through the device-level topology.</summary>
public sealed record StreamingFlowPath(
    string Id,
    string SessionId,
    string Endpoint,
    HostedStreamFormat Format,
    string Channel,
    DateTime StartedAtUtc,
    IReadOnlyList<StreamingFlowPathStage> Stages,
    bool IsSynthetic,
    bool IsShared,
    int SharedReceiverCount);

/// <summary>Immutable dashboard projection of configured topology and live streaming state.</summary>
public sealed record StreamingFlowSnapshot(
    IReadOnlyList<StreamingFlowNode> Nodes,
    IReadOnlyList<StreamingFlowPath> ActivePaths,
    int ActiveConsumerCount,
    int ActiveProducerCount,
    int WarmProducerCount)
{
    /// <summary>Gets all nodes in one display lane.</summary>
    public IReadOnlyList<StreamingFlowNode> GetLane(StreamingFlowLane lane) => Nodes.Where(node => node.Lane == lane).ToArray();
}

/// <summary>Builds immutable configured and runtime streaming-flow snapshots.</summary>
public interface IStreamingFlowSnapshotService
{
    /// <summary>Gets the current configured topology and runtime overlay.</summary>
    StreamingFlowSnapshot GetSnapshot();
}

/// <summary>Combines settings, shared producer diagnostics, device state, and logical consumers for the Dashboard.</summary>
public sealed class StreamingFlowSnapshotService : IStreamingFlowSnapshotService
{
    private readonly ILineupSettingsService _settings;
    private readonly IStreamingSessionGraph _graph;
    private readonly IReadOnlyList<IStreamingProducerDiagnosticsContributor> _producerDiagnostics;
    private readonly IActiveStreamRegistry _activeStreams;
    private readonly IPhysicalDeviceStatusService _deviceStatuses;

    /// <summary>Initializes the streaming-flow projection service.</summary>
    public StreamingFlowSnapshotService(
        ILineupSettingsService settings,
        IStreamingSessionGraph graph,
        IEnumerable<IStreamingProducerDiagnosticsContributor> producerDiagnostics,
        IActiveStreamRegistry activeStreams,
        IPhysicalDeviceStatusService deviceStatuses)
    {
        _settings = settings;
        _graph = graph;
        _producerDiagnostics = producerDiagnostics.ToArray();
        _activeStreams = activeStreams;
        _deviceStatuses = deviceStatuses;
    }

    /// <inheritdoc />
    public StreamingFlowSnapshot GetSnapshot()
    {
        var configuration = _settings.Settings.Streaming;
        var graph = _graph.GetDiagnostics();
        var producerDiagnostics = _producerDiagnostics.Select(contributor => contributor.GetProducerDiagnostics()).ToArray();
        var streams = _activeStreams.GetActiveStreams();
        var nodesById = graph.Nodes.ToDictionary(node => node.NodeId, StringComparer.Ordinal);
        var graphPhysicalDevices = graph.Nodes.ToDictionary(node => node.NodeId, node => ResolvePhysicalDevice(node, nodesById), StringComparer.Ordinal);
        var statusByDevice = _deviceStatuses.Devices.ToDictionary(status => status.Device.Id);
        var nodes = new List<StreamingFlowNode>();

        foreach (var physical in configuration.PhysicalDevices.OrderBy(device => device.Order))
        {
            var relatedStreams = streams.Where(stream => stream.Flow?.PhysicalDeviceId == physical.Id).ToArray();
            var relatedGraph = graph.Nodes.Where(node => graphPhysicalDevices[node.NodeId] == physical.Id).ToArray();
            statusByDevice.TryGetValue(physical.Id, out var runtime);
            var state = GetPhysicalState(physical, runtime, relatedStreams, relatedGraph);
            var channels = relatedStreams.Select(stream => stream.Channel).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(channel => channel, StringComparer.OrdinalIgnoreCase).ToArray();
            var capacity = graph.Capacity.SingleOrDefault(item => item.DeviceId == physical.Id);
            var profile = physical.TranscodeOverrides.Apply(configuration.PhysicalDefaults);
            nodes.Add(new StreamingFlowNode(
                $"physical:{physical.Id}",
                StreamingFlowLane.PhysicalSource,
                physical.Name,
                physical.Adapter.Kind,
                state,
                [
                    new("Device ID", physical.Id.ToString()),
                    new("Adapter", physical.Adapter.Kind),
                    new("Configured", physical.Enabled ? "Enabled" : "Disabled"),
                    new("Capacity", capacity is null ? "Inactive" : $"{capacity.ActiveAcquisitions}/{capacity.Capacity?.ToString() ?? "unlimited"} acquisitions"),
                    new("Receivers", capacity?.Receivers.ToString() ?? "0"),
                    new("Runtime", runtime?.Error ?? (runtime?.Status is null ? "No status" : "Available"))
                ])
            {
                ActiveChannels = channels,
                ActiveSessionCount = relatedStreams.Length
            });

            var physicalTransforms = relatedGraph.Where(node => node.Stage == "source-normalization").ToArray();
            var processingState = GetProcessingState(physical.Enabled, relatedStreams, physicalTransforms);
            nodes.Add(new StreamingFlowNode(
                $"physical-processing:{physical.Id}",
                StreamingFlowLane.PhysicalProcessing,
                IsConfigured(profile) ? "Source Transcoder" : "Pass-through",
                physical.Name,
                processingState,
                [
                    new("Effective policy", Describe(profile)),
                    new("Runtime stages", physicalTransforms.Length.ToString()),
                    new("Shared receivers", physicalTransforms.Sum(node => node.ReceiverCount).ToString())
                ])
            {
                ActiveChannels = channels,
                ActiveSessionCount = relatedStreams.Length
            });
        }

        var graphRunning = graph.Nodes.Count(node => node.IsRunning);
        var graphWarm = graph.Nodes.Count(node => node.IsWarm);
        var externalWarm = producerDiagnostics.Sum(diagnostics => diagnostics.WarmProducerCount);
        var externalActive = producerDiagnostics.Sum(diagnostics => diagnostics.ActiveProducerCount);
        var reusableOutputs = producerDiagnostics.Sum(diagnostics => diagnostics.ReusableOutputCount);
        var producerCount = graph.Nodes.Count(node => node.IsRunning && !node.IsWarm && node.Stage is not null) + externalActive;
        var multiplexerState = streams.Count > 1
            ? StreamingFlowState.Shared
            : streams.Count == 1
                ? StreamingFlowState.Active
                : graphWarm + externalWarm > 0
                    ? StreamingFlowState.Warm
                    : StreamingFlowState.Idle;
        nodes.Add(new StreamingFlowNode(
            "multiplexer",
            StreamingFlowLane.Multiplexer,
            "Shared Multiplexer",
            "Reference-counted stream graph",
            multiplexerState,
            [
                new("Active graph nodes", graphRunning.ToString()),
                new("Reusable outputs", reusableOutputs.ToString()),
                new("Warm producers", (graphWarm + externalWarm).ToString()),
                new("Logical consumers", streams.Count.ToString())
            ])
        {
            ActiveChannels = streams.Select(stream => stream.Channel).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(channel => channel, StringComparer.OrdinalIgnoreCase).ToArray(),
            ActiveSessionCount = streams.Count
        });

        foreach (var virtualDevice in configuration.VirtualDevices)
        {
            var relatedStreams = streams.Where(stream => stream.Flow?.VirtualDeviceId == virtualDevice.Id).ToArray();
            var channels = relatedStreams.Select(stream => stream.Channel).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(channel => channel, StringComparer.OrdinalIgnoreCase).ToArray();
            var state = !virtualDevice.Enabled
                ? StreamingFlowState.Disabled
                : relatedStreams.Length > 1
                    ? StreamingFlowState.Shared
                    : relatedStreams.Length == 1
                        ? StreamingFlowState.Active
                        : StreamingFlowState.Idle;
            nodes.Add(new StreamingFlowNode(
                $"virtual:{virtualDevice.Id}",
                StreamingFlowLane.VirtualOutput,
                virtualDevice.Name,
                virtualDevice.Adapter.Kind,
                state,
                [
                    new("Device ID", virtualDevice.Id.ToString()),
                    new("Adapter", virtualDevice.Adapter.Kind),
                    new("Configured", virtualDevice.Enabled ? "Enabled" : "Disabled"),
                    new("Subscription", Describe(virtualDevice.Subscription)),
                    new("Active consumers", relatedStreams.Length.ToString())
                ])
            {
                ActiveChannels = channels,
                ActiveSessionCount = relatedStreams.Length
            });

            var profile = virtualDevice.TranscodeOverrides.Apply(configuration.VirtualDefaults);
            var transforms = relatedStreams.SelectMany(stream => stream.Tracks).Any(track => track.IsSelected && !string.Equals(track.OutputCodec, "copy", StringComparison.OrdinalIgnoreCase));
            var outputState = !virtualDevice.Enabled
                ? StreamingFlowState.Disabled
                : transforms
                    ? StreamingFlowState.Transcoding
                    : relatedStreams.Length > 0
                        ? StreamingFlowState.Active
                        : StreamingFlowState.Idle;
            nodes.Add(new StreamingFlowNode(
                $"virtual-processing:{virtualDevice.Id}",
                StreamingFlowLane.VirtualProcessing,
                IsConfigured(profile) ? "Output Transcoder" : "Pass-through",
                virtualDevice.Name,
                outputState,
                [
                    new("Effective policy", Describe(profile)),
                    new("Reusable outputs", relatedStreams.Select(stream => stream.Flow?.ReusableOutputId).Where(id => id.HasValue).Distinct().Count().ToString()),
                    new("Active consumers", relatedStreams.Length.ToString())
                ])
            {
                ActiveChannels = channels,
                ActiveSessionCount = relatedStreams.Length
            });
        }

        foreach (var stream in streams)
        {
            nodes.Add(new StreamingFlowNode(
                $"consumer:{stream.SessionId}",
                StreamingFlowLane.Consumer,
                stream.ClientAddress ?? stream.ClientId ?? "Unknown endpoint",
                $"{stream.Format} · channel {stream.Channel}",
                StreamingFlowState.Active,
                [
                    new("Session", stream.SessionId),
                    new("Endpoint", stream.ClientAddress ?? "Unknown"),
                    new("Client ID", stream.ClientId ?? "Not provided"),
                    new("Channel", stream.Channel),
                    new("Format", stream.Format.ToString()),
                    new("Active", FormatDuration(DateTime.UtcNow - stream.StartedAtUtc))
                ])
            {
                ActiveChannels = [stream.Channel],
                ActiveSessionCount = 1
            });
        }

        foreach (var source in streams.Select(stream => stream.Flow?.SyntheticSource).Where(source => !string.IsNullOrWhiteSpace(source)).Distinct(StringComparer.Ordinal))
        {
            var syntheticStreams = streams.Where(stream => string.Equals(stream.Flow?.SyntheticSource, source, StringComparison.Ordinal)).ToArray();
            nodes.Add(new StreamingFlowNode(
                $"synthetic:{source}",
                StreamingFlowLane.PhysicalSource,
                source!,
                "Generated by Lineup",
                StreamingFlowState.Active,
                [new("Source", source!), new("Active consumers", syntheticStreams.Length.ToString())])
            {
                ActiveChannels = syntheticStreams.Select(stream => stream.Channel).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                ActiveSessionCount = syntheticStreams.Length
            });
        }

        var sharedReceiverCounts = streams
            .GroupBy(GetShareKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var paths = streams
            .Select(stream => CreatePath(stream, configuration, graph, graphPhysicalDevices, sharedReceiverCounts[GetShareKey(stream)]))
            .OrderBy(path => path.StartedAtUtc)
            .ThenBy(path => path.SessionId, StringComparer.Ordinal)
            .ToArray();

        return new StreamingFlowSnapshot(nodes, paths, streams.Count, producerCount, graphWarm + externalWarm);
    }

    private static StreamingFlowPath CreatePath(
        ActiveStreamSnapshot stream,
        StreamingConfiguration configuration,
        StreamingSessionGraphDiagnostics graph,
        IReadOnlyDictionary<string, PhysicalDeviceId?> graphPhysicalDevices,
        int sharedReceiverCount)
    {
        var flow = stream.Flow;
        var physicalDefinition = flow?.PhysicalDeviceId is { } physicalId
            ? configuration.PhysicalDevices.SingleOrDefault(device => device.Id == physicalId)
            : null;
        var virtualDefinition = flow?.VirtualDeviceId is { } virtualId
            ? configuration.VirtualDevices.SingleOrDefault(device => device.Id == virtualId)
            : null;
        var physicalName = physicalDefinition?.Name ?? flow?.PhysicalDeviceId?.ToString() ?? flow?.SyntheticSource ?? "Generated source";
        var virtualName = virtualDefinition?.Name ?? flow?.VirtualDeviceId?.ToString() ?? "Unassigned virtual output";
        var physicalTranscoding = flow?.PhysicalDeviceId is { } deviceId &&
                                  graph.Nodes.Any(node => node.Stage == "source-normalization" && graphPhysicalDevices[node.NodeId] == deviceId);
        var outputTranscoding = stream.Tracks.Any(track => track.IsSelected && !string.Equals(track.OutputCodec, "copy", StringComparison.OrdinalIgnoreCase));
        var producerDescription = flow?.ReusableOutputId is not null
            ? $"Reusable output {FormatCompactId(flow.ReusableOutputId.Value)}"
            : flow?.ProducerNodeId is not null
                ? $"Producer {flow.ProducerNodeId}"
                : "Session-owned producer";
        StreamingFlowPathStage[] stages =
        [
            new(
                StreamingFlowStageKind.PhysicalSource,
                physicalName,
                flow?.SyntheticSource is not null ? "Generated by Lineup" : physicalDefinition?.Adapter.Kind ?? "Physical device",
                [
                    new("Channel", stream.Channel),
                    new("Device ID", flow?.PhysicalDeviceId?.ToString() ?? "Generated"),
                    new("Source channel", flow?.SourceChannelId?.ProviderKey ?? stream.Channel)
                ])
            {
                InputMedia = stream.PhysicalInput,
                OutputMedia = stream.PhysicalInput
            },
            new(
                StreamingFlowStageKind.SourceProcessing,
                flow?.SyntheticSource is not null ? "Generated" : physicalTranscoding ? "Source Transcoder" : "Pass-through",
                physicalName,
                [
                    new("Processing", flow?.SyntheticSource is not null ? "Synthetic media generation" : physicalTranscoding ? "Physical-device transcode profile" : "No physical-device transform"),
                    new("Effective policy", physicalDefinition is null ? "Not applicable" : Describe(physicalDefinition.TranscodeOverrides.Apply(configuration.PhysicalDefaults)))
                ])
            {
                InputMedia = stream.PhysicalInput,
                OutputMedia = stream.PhysicalOutput
            },
            new(
                StreamingFlowStageKind.Multiplexer,
                "Shared Multiplexer",
                producerDescription,
                [
                    new("Sharing", sharedReceiverCount > 1 ? $"{sharedReceiverCount} consumer sessions" : "Dedicated to this session"),
                    new("Producer node", flow?.ProducerNodeId ?? "Not reported"),
                    new("Reusable output", flow?.ReusableOutputId?.ToString() ?? "Not retained")
                ])
            {
                InputMedia = stream.PhysicalOutput,
                OutputMedia = stream.PhysicalOutput
            },
            new(
                StreamingFlowStageKind.VirtualOutput,
                virtualName,
                virtualDefinition?.Adapter.Kind ?? "Virtual device",
                [
                    new("Device ID", flow?.VirtualDeviceId?.ToString() ?? "Unassigned"),
                    new("Subscription", virtualDefinition is null ? "Not available" : Describe(virtualDefinition.Subscription)),
                    new("Shared consumers", sharedReceiverCount.ToString())
                ])
            {
                InputMedia = stream.PhysicalOutput,
                OutputMedia = stream.PhysicalOutput
            },
            new(
                StreamingFlowStageKind.OutputProcessing,
                outputTranscoding ? $"{FormatHostedStreamFormat(stream.Format)} transcode" : $"{FormatHostedStreamFormat(stream.Format)} pass-through",
                virtualName,
                [
                    new("Processing", outputTranscoding ? "Virtual-device output transcode" : "No output codec transform"),
                    new("Effective policy", virtualDefinition is null ? "Not available" : Describe(virtualDefinition.TranscodeOverrides.Apply(configuration.VirtualDefaults)))
                ])
            {
                InputMedia = stream.PhysicalOutput,
                OutputMedia = stream.VirtualOutput
            },
            new(
                StreamingFlowStageKind.Consumer,
                stream.ClientAddress ?? stream.ClientId ?? "Unknown endpoint",
                $"{FormatHostedStreamFormat(stream.Format)} · channel {stream.Channel}",
                [
                    new("Session", stream.SessionId),
                    new("Client address", stream.ClientAddress ?? "Unknown"),
                    new("Client ID", stream.ClientId ?? "Not provided"),
                    new("Format", FormatHostedStreamFormat(stream.Format)),
                    new("Active", FormatDuration(DateTime.UtcNow - stream.StartedAtUtc))
                ])
            {
                InputMedia = stream.VirtualOutput,
                OutputMedia = stream.VirtualOutput
            }
        ];
        return new StreamingFlowPath(
            $"path:{stream.SessionId}",
            stream.SessionId,
            stream.ClientAddress ?? stream.ClientId ?? "Unknown endpoint",
            stream.Format,
            stream.Channel,
            stream.StartedAtUtc,
            stages,
            flow?.SyntheticSource is not null,
            sharedReceiverCount > 1,
            sharedReceiverCount);
    }

    private static string GetShareKey(ActiveStreamSnapshot stream) =>
        stream.Flow?.ReusableOutputId is { } outputId
            ? $"output:{outputId}"
            : !string.IsNullOrWhiteSpace(stream.Flow?.ProducerNodeId)
                ? $"producer:{stream.Flow.ProducerNodeId}"
                : $"session:{stream.SessionId}";

    private static string FormatCompactId(StreamSessionId id)
    {
        const int visibleCharacters = 8;
        var value = id.ToString();
        return value.Length > visibleCharacters ? $"{value[..visibleCharacters]}..." : value;
    }

    private static string FormatHostedStreamFormat(HostedStreamFormat format) => format switch
    {
        HostedStreamFormat.MpegTs => "MPEG-TS",
        HostedStreamFormat.Cmaf => "CMAF",
        _ => format.ToString()
    };

    private static PhysicalDeviceId? ResolvePhysicalDevice(StreamNodeDiagnostics node, IReadOnlyDictionary<string, StreamNodeDiagnostics> nodes)
    {
        var current = node;
        while (true)
        {
            if (current.PhysicalDeviceId is { } physicalDeviceId)
            {
                return physicalDeviceId;
            }
            if (current.ParentNodeId is null || !nodes.TryGetValue(current.ParentNodeId, out current!))
            {
                return null;
            }
        }
    }

    private static StreamingFlowState GetPhysicalState(
        PhysicalDeviceDefinition device,
        PhysicalDeviceRuntimeStatus? runtime,
        IReadOnlyList<ActiveStreamSnapshot> streams,
        IReadOnlyList<StreamNodeDiagnostics> nodes)
    {
        if (!device.Enabled)
        {
            return StreamingFlowState.Disabled;
        }
        if (runtime?.Error is not null)
        {
            return StreamingFlowState.Unavailable;
        }
        if (streams.Count > 1)
        {
            return StreamingFlowState.Shared;
        }
        if (streams.Count == 1)
        {
            return StreamingFlowState.Active;
        }
        return nodes.Any(node => node.IsWarm) ? StreamingFlowState.Warm : StreamingFlowState.Idle;
    }

    private static StreamingFlowState GetProcessingState(
        bool enabled,
        IReadOnlyList<ActiveStreamSnapshot> streams,
        IReadOnlyList<StreamNodeDiagnostics> transforms)
    {
        if (!enabled)
        {
            return StreamingFlowState.Disabled;
        }
        if (transforms.Any(node => node.IsWarm))
        {
            return StreamingFlowState.Warm;
        }
        if (transforms.Any(node => node.IsRunning))
        {
            return StreamingFlowState.Transcoding;
        }
        return streams.Count > 0 ? StreamingFlowState.Active : StreamingFlowState.Idle;
    }

    private static bool IsConfigured(PhysicalTranscodeProfile profile) =>
        profile.ConvertHevcToH264 ||
        profile.Deinterlace != SourceDeinterlacePolicy.Preserve ||
        profile.Audio != OutputAudioPolicy.Copy ||
        profile.Ac4Audio != OutputAudioPolicy.Copy;

    private static bool IsConfigured(VirtualTranscodeProfile profile) =>
        profile.ConvertHevcToH264 ||
        profile.Deinterlace != SourceDeinterlacePolicy.Preserve ||
        profile.Audio != OutputAudioPolicy.Copy ||
        profile.Ac4Audio != OutputAudioPolicy.Copy;

    private static string Describe(PhysicalTranscodeProfile profile) =>
        $"HEVC {(profile.ConvertHevcToH264 ? "to H.264" : "preserved")}; deinterlace {profile.Deinterlace}; audio {profile.Audio}; AC-4 {profile.Ac4Audio}";

    private static string Describe(VirtualTranscodeProfile profile) =>
        $"HEVC {(profile.ConvertHevcToH264 ? "to H.264" : "preserved")}; deinterlace {profile.Deinterlace}; audio {profile.Audio}; AC-4 {profile.Ac4Audio}";

    private static string Describe(PhysicalDeviceSubscription subscription) =>
        subscription.Mode == PhysicalDeviceSubscriptionMode.All
            ? "All compatible enabled physical devices"
            : $"{subscription.SelectedDeviceIds.Count} selected physical device(s)";

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}h {duration.Minutes}m"
            : duration.TotalMinutes >= 1
                ? $"{(int)duration.TotalMinutes}m {duration.Seconds}s"
                : $"{Math.Max(0, (int)duration.TotalSeconds)}s";
}
