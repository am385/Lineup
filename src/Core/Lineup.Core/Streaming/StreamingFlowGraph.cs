namespace Lineup.Core.Streaming;

/// <summary>One directed connection reported by the runtime topology.</summary>
public sealed record StreamingFlowEdge(string Id, string Source, string Target)
{
    /// <summary>Gets whether this connection is configured, serving consumers, or retaining a warm producer.</summary>
    public StreamingFlowState State { get; init; } = StreamingFlowState.Idle;
}

/// <summary>Identifies the exact ordered graph elements traversed by one consumer.</summary>
public sealed record StreamingFlowGraphPath(string Id, IReadOnlyList<string> NodeIds, IReadOnlyList<string> EdgeIds);

/// <summary>Contains runtime graph elements and separately displayed inactive configuration.</summary>
public sealed record StreamingFlowGraph(
    IReadOnlyList<StreamingFlowNode> Nodes,
    IReadOnlyList<StreamingFlowEdge> Edges,
    IReadOnlyList<StreamingFlowGraphPath> Paths,
    IReadOnlyList<StreamingFlowNode> ConfiguredNodes);

/// <summary>Projects passive runtime identities into selectable streaming paths.</summary>
internal static class StreamingFlowGraphBuilder
{
    /// <summary>Builds a graph without opening sources or extending producer lifetimes.</summary>
    internal static StreamingFlowGraph Build(
        StreamingConfiguration configuration,
        StreamingSessionGraphDiagnostics diagnostics,
        IReadOnlyList<ActiveStreamSnapshot> streams,
        IReadOnlyList<StreamingProducerOutputDiagnostics> outputs,
        IReadOnlyList<StreamingFlowNode> configured
    )
    {
        var runtime = diagnostics.Nodes.Where(node => !string.IsNullOrEmpty(node.NodeId)).ToDictionary(node => node.NodeId, StringComparer.Ordinal);
        var configuredById = configured.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var physicalById = configuration.PhysicalDevices.ToDictionary(device => device.Id);
        var streamsByOutput = streams.Where(stream => stream.Flow?.ReusableOutputId is not null)
            .GroupBy(stream => stream.Flow!.ReusableOutputId!.Value).ToDictionary(group => group.Key, group => group.ToArray());
        var usedConfiguration = new HashSet<string>(StringComparer.Ordinal);
        var nodes = new Dictionary<string, StreamingFlowNode>(StringComparer.Ordinal);
        var edges = new Dictionary<string, StreamingFlowEdge>(StringComparer.Ordinal);
        var routes = new List<StreamingFlowGraphPath>();
        var ancestry = streams.ToDictionary(stream => stream.SessionId, stream => GetAncestry(stream.Flow?.ProducerNodeId, runtime), StringComparer.Ordinal);
        var sessionsByNode = new Dictionary<string, List<ActiveStreamSnapshot>>(StringComparer.Ordinal);
        var warmNodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in diagnostics.Nodes.Where(node => node.IsWarm))
        {
            warmNodes.UnionWith(GetAncestry(node.NodeId, runtime));
        }
        foreach (var output in outputs.Where(output => output.IsWarm))
        {
            warmNodes.UnionWith(GetAncestry(output.Context?.Flow?.ProducerNodeId, runtime));
        }
        foreach (var stream in streams)
        {
            foreach (var id in ancestry[stream.SessionId])
            {
                if (!sessionsByNode.TryGetValue(id, out var related))
                {
                    related = [];
                    sessionsByNode.Add(id, related);
                }
                related.Add(stream);
            }
        }
        foreach (var node in runtime.Values)
        {
            var related = sessionsByNode.GetValueOrDefault(node.NodeId) ?? [];
            var physical = GetAncestry(node.NodeId, runtime).Select(id => runtime.GetValueOrDefault(id)?.PhysicalDeviceId).FirstOrDefault(id => id.HasValue);
            var device = physical.HasValue ? physicalById.GetValueOrDefault(physical.Value) : null;
            if (physical.HasValue) usedConfiguration.Add($"physical:{physical}");
            var source = node.ParentNodeId is null;
            var lane = source ? StreamingFlowLane.PhysicalSource
                : node.Stage == "virtual-mpegts" ? StreamingFlowLane.VirtualProcessing : StreamingFlowLane.PhysicalProcessing;
            var templateId = source ? $"physical:{physical}"
                : lane == StreamingFlowLane.VirtualProcessing ? $"virtual-processing:{related.FirstOrDefault()?.Flow?.VirtualDeviceId}" : $"physical-processing:{physical}";
            var template = configuredById.GetValueOrDefault(templateId);
            var details = new List<StreamingFlowDetail>(template?.Details.Where(detail => detail.Label is not "Active consumers" and not "Reusable outputs") ?? [])
            {
                new("Runtime node", node.NodeId),
                new("Stage", node.Stage ?? node.Kind),
                new("Direct receivers", node.ReceiverCount.ToString()),
                new("Source channel", node.SourceChannelId?.ProviderKey ?? "Not reported")
            };
            nodes.Add(RuntimeId(node.NodeId), new StreamingFlowNode(
                RuntimeId(node.NodeId), lane, source ? device?.Name ?? node.Kind : node.Stage ?? node.Kind,
                device?.Name ?? node.SourceChannelId?.ProviderKey ?? "Runtime producer",
                related.Count == 0 && warmNodes.Contains(node.NodeId) ? StreamingFlowState.Warm
                    : !node.IsRunning ? StreamingFlowState.Unavailable
                    : related.Count > 1 ? StreamingFlowState.Shared
                    : node.VideoEncoding is { Encoder: not "copy" } ? StreamingFlowState.Transcoding : StreamingFlowState.Active,
                details)
            {
                ResourceIds = node.ResourceIds,
                VideoEncodings = node.VideoEncoding is { } encoding ? [encoding] : [],
                ActiveChannels = related.Select(stream => stream.Channel).Distinct(StringComparer.Ordinal).ToArray(),
                ActiveSessionCount = related.Count
            });
        }
        foreach (var node in runtime.Values.Where(node => node.ParentNodeId is not null))
        {
            EnsureUnknown(RuntimeId(node.ParentNodeId!), node.ParentNodeId!);
            Connect(RuntimeId(node.ParentNodeId!), RuntimeId(node.NodeId));
        }
        foreach (var output in outputs)
        {
            var related = streamsByOutput.GetValueOrDefault(output.OutputId) ?? [];
            AddOutput(output.OutputId, related, output.Context, output.IsWarm, output.ReceiverCount);
        }
        foreach (var stream in streams)
        {
            var ids = ancestry[stream.SessionId].Select(RuntimeId).ToList();
            foreach (var id in ancestry[stream.SessionId])
            {
                EnsureUnknown(RuntimeId(id), id);
            }
            if (stream.Flow?.ReusableOutputId is { } outputId)
            {
                AddOutput(outputId, streamsByOutput[outputId], null, false, streamsByOutput[outputId].Length);
                if (ids.Count == 0 && stream.Flow.SyntheticSource is not null)
                {
                    ids.Add(AddSynthetic(OutputId(outputId), stream.Flow.SyntheticSource, stream.Channel, 1));
                }
                ids.Add(OutputId(outputId));
            }
            if (ids.Count == 0)
            {
                var id = $"session-source:{stream.SessionId}";
                if (stream.Flow?.SyntheticSource is { } synthetic)
                {
                    id = AddSynthetic(id, synthetic, stream.Channel, 1);
                }
                else
                {
                    EnsureUnknown(id, "Source producer not reported");
                }
                ids.Add(id);
            }
            var terminal = ids[^1];
            var virtualId = stream.Flow?.VirtualDeviceId;
            if (virtualId.HasValue) usedConfiguration.Add($"virtual:{virtualId}");
            var routeId = $"route:{virtualId}:{terminal}";
            var template = configuredById.GetValueOrDefault($"virtual:{virtualId}");
            if (!nodes.ContainsKey(routeId))
            {
                nodes.Add(routeId, new StreamingFlowNode(routeId, StreamingFlowLane.VirtualOutput,
                    template?.Label ?? "Unassigned virtual output", template?.Subtitle ?? "Virtual output",
                    StreamingFlowState.Active, template?.Details.Where(detail => detail.Label != "Active consumers").ToArray() ?? []));
            }
            var route = nodes[routeId];
            nodes[routeId] = route with
            {
                State = route.ActiveSessionCount > 0 ? StreamingFlowState.Shared : StreamingFlowState.Active,
                ActiveSessionCount = route.ActiveSessionCount + 1,
                ActiveChannels = route.ActiveChannels.Append(stream.Channel).Distinct(StringComparer.Ordinal).ToArray()
            };
            ids.Add(routeId);
            var consumer = configuredById[$"consumer:{stream.SessionId}"];
            nodes.Add(consumer.Id, consumer);
            ids.Add(consumer.Id);
            var pathEdges = new List<string>();
            for (var index = 1; index < ids.Count; index++)
            {
                pathEdges.Add(Connect(ids[index - 1], ids[index]));
            }
            routes.Add(new($"path:{stream.SessionId}", ids, pathEdges));
        }
        return new(nodes.Values.ToArray(), edges.Values.ToArray(), routes,
            configured.Where(node => !usedConfiguration.Contains(node.Id) && node.ActiveSessionCount == 0 && node.State != StreamingFlowState.Warm &&
                node.Lane is StreamingFlowLane.PhysicalSource or StreamingFlowLane.VirtualOutput).ToArray());

        void EnsureUnknown(string id, string label)
        {
            nodes.TryAdd(id, new(id, StreamingFlowLane.Multiplexer, "Unavailable producer", label, StreamingFlowState.Unavailable,
                [new("Runtime node", label)]));
        }

        string Connect(string source, string target)
        {
            var id = $"edge:{source.Length}:{source}{target}";
            edges.TryAdd(id, new(id, source, target));
            return id;
        }

        string AddSynthetic(string owner, string label, string channel, int consumers, bool warm = false)
        {
            var id = $"synthetic:{owner}";
            nodes.TryAdd(id, new(id, StreamingFlowLane.PhysicalSource, label, channel, warm ? StreamingFlowState.Warm : StreamingFlowState.Active, [new("Source", label)])
            {
                ActiveChannels = [channel],
                ActiveSessionCount = consumers
            });
            return id;
        }

        void AddOutput(StreamSessionId id, ActiveStreamSnapshot[] related, StreamingProducerContext? context, bool warm, int receivers)
        {
            var key = OutputId(id);
            if (nodes.ContainsKey(key)) return;
            var flow = related.FirstOrDefault()?.Flow ?? context?.Flow;
            if (flow?.VirtualDeviceId is { } virtualDeviceId) usedConfiguration.Add($"virtual:{virtualDeviceId}");
            var encoding = related.FirstOrDefault(stream => stream.ActiveVideoEncoder is not null) is { } reported
                ? new VideoEncodingSnapshot(reported.ActiveVideoEncoder!, reported.RequestedVideoEncoder, reported.HardwareFallbackApplied) { Processing = reported.VideoProcessing }
                : context?.VideoEncoding;
            var details = new List<StreamingFlowDetail>(configuredById.GetValueOrDefault($"virtual-processing:{flow?.VirtualDeviceId}")?.Details
                .Where(detail => detail.Label is not "Active consumers" and not "Reusable outputs") ?? [])
            {
                new("Reusable output", id.ToString()),
                new("Direct viewers", receivers.ToString())
            };
            nodes.Add(key, new(key, StreamingFlowLane.VirtualProcessing, "Reusable output", id.ToString(),
                warm ? StreamingFlowState.Warm : related.Length > 1 ? StreamingFlowState.Shared
                    : encoding is { Encoder: not "copy" } ? StreamingFlowState.Transcoding : StreamingFlowState.Active,
                details)
            {
                ResourceIds = related.SelectMany(stream => stream.ResourceIds).Concat(context?.ResourceIds ?? []).Distinct(StringComparer.Ordinal).ToArray(),
                VideoEncodings = encoding is null ? [] : [encoding],
                ActiveChannels = related.Select(stream => stream.Channel).Concat(context is null ? [] : [context.Channel]).Distinct(StringComparer.Ordinal).ToArray(),
                ActiveSessionCount = related.Length
            });
            if (flow?.ProducerNodeId is { } parent)
            {
                EnsureUnknown(RuntimeId(parent), parent);
                Connect(RuntimeId(parent), key);
            }
            else if (flow?.SyntheticSource is { } synthetic)
            {
                Connect(AddSynthetic(key, synthetic, context?.Channel ?? related.FirstOrDefault()?.Channel ?? "", related.Length, warm), key);
            }
        }
    }

    private static string RuntimeId(string id) => $"runtime:{id}";

    private static string OutputId(StreamSessionId id) => $"output:{id}";

    private static IReadOnlyList<string> GetAncestry(string? nodeId, IReadOnlyDictionary<string, StreamNodeDiagnostics> nodes)
    {
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (nodeId is not null && seen.Add(nodeId))
        {
            ids.Add(nodeId);
            nodeId = nodes.TryGetValue(nodeId, out var node) ? node.ParentNodeId : null;
        }
        ids.Reverse();
        return ids;
    }
}
