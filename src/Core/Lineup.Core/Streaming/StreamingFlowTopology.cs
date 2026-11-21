namespace Lineup.Core.Streaming;

/// <summary>Describes configured devices and reusable source/output processing paths without duplicate inventory vertices.</summary>
public sealed record StreamingFlowTopology(
    IReadOnlyList<StreamingFlowNode> Nodes,
    IReadOnlyList<StreamingFlowEdge> Edges,
    IReadOnlyList<StreamingFlowGraphPath> Paths,
    IReadOnlyList<StreamingFlowNode> ReusableOutputs);

/// <summary>Groups reusable producers by processing responsibility without changing runtime ownership.</summary>
internal static class StreamingFlowTopologyBuilder
{
    /// <summary>Overlays reported consumer routes and retained producers onto always-visible configured devices.</summary>
    internal static StreamingFlowTopology Build(
        IReadOnlyList<StreamingFlowNode> configured,
        IReadOnlyList<ActiveStreamSnapshot> streams,
        StreamingFlowGraph runtime,
        IReadOnlyDictionary<string, PhysicalDeviceId?> runtimePhysicalDevices,
        IReadOnlyList<StreamingProducerOutputDiagnostics> outputs,
        IReadOnlyList<StreamingFlowPath> activePaths
    )
    {
        var nodes = configured.Where(node => node.Lane != StreamingFlowLane.Multiplexer && !node.Id.StartsWith("synthetic:", StringComparison.Ordinal))
            .ToDictionary(node => node.Id, StringComparer.Ordinal);
        var runtimeNodes = runtime.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var parents = runtime.Edges.GroupBy(edge => edge.Target).ToDictionary(group => group.Key, group => group.First().Source, StringComparer.Ordinal);
        var upstreamIds = runtime.Edges.Select(edge => edge.Source).ToHashSet(StringComparer.Ordinal);
        var runtimePaths = runtime.Paths.ToDictionary(path => path.Id, StringComparer.Ordinal);
        var activePathsById = activePaths.ToDictionary(path => path.Id, StringComparer.Ordinal);
        var edges = new Dictionary<string, StreamingFlowEdge>(StringComparer.Ordinal);
        var paths = new List<StreamingFlowGraphPath>();
        var represented = new HashSet<string>(StringComparer.Ordinal);
        var replacedSources = new HashSet<string>(StringComparer.Ordinal);
        var replacedVirtuals = new HashSet<string>(StringComparer.Ordinal);
        var processorContexts = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var stream in streams)
        {
            var pathId = $"path:{stream.SessionId}";
            var reported = runtimePaths[pathId].NodeIds;
            var sourceId = AddSource(stream.Flow?.PhysicalDeviceId, reported, $"session-source:{stream.SessionId}");
            var stages = activePathsById[pathId].Stages;
            var sourceStage = stages.Single(stage => stage.Kind == StreamingFlowStageKind.SourceProcessing);
            var outputStage = stages.Single(stage => stage.Kind == StreamingFlowStageKind.OutputProcessing);
            var nodeIds = new List<string> { sourceId };
            nodeIds.AddRange(AddSourceProcessors(reported, sourceId, sourceStage, $"session:{stream.SessionId}", StreamingFlowState.Active));
            var virtualId = stream.Flow?.VirtualDeviceId is { } id ? $"virtual:{id}" : $"virtual:unassigned:{stream.SessionId}";
            if (!nodes.ContainsKey(virtualId))
            {
                nodes.Add(virtualId, new(virtualId, StreamingFlowLane.VirtualOutput, "Unavailable virtual output",
                    "Output attribution not configured", StreamingFlowState.Unavailable, []));
            }
            nodeIds.Add(virtualId);
            var producers = reported.Where(id => runtimeNodes[id].Lane == StreamingFlowLane.VirtualProcessing).ToArray();
            if (producers.Length > 0)
            {
                nodeIds.AddRange(producers.Select(producer => AddProcessor(producer, StreamingFlowLane.VirtualProcessing, virtualId,
                    producer == producers[^1] ? outputStage : null)));
            }
            else
            {
                var terminal = reported.LastOrDefault(id => runtimeNodes[id].Lane is StreamingFlowLane.PhysicalSource or StreamingFlowLane.PhysicalProcessing);
                var sharedCopy = outputStage.VideoEncoding?.Encoder == "copy" && outputStage.ResourceIds.Count == 0 && terminal is not null &&
                    stream.Format == HostedStreamFormat.MpegTs && !stream.Tracks.Any(track => track.IsSelected && track.Type != MediaTrackType.Video &&
                        !string.Equals(track.OutputCodec, "copy", StringComparison.OrdinalIgnoreCase));
                var processorId = sharedCopy ? $"output-processing:copy:{virtualId.Length}:{virtualId}{terminal}" : $"output-processing:session:{stream.SessionId}";
                AddUnreportedProcessor(processorId, StreamingFlowLane.VirtualProcessing, virtualId, outputStage, stream.Channel,
                    sharedCopy ? terminal! : "Not reported; session-scoped processing", StreamingFlowState.Active);
                nodeIds.Add(processorId);
            }
            nodeIds.Add($"consumer:{stream.SessionId}");
            var edgeIds = ConnectChain(nodeIds, StreamingFlowState.Active);
            paths.Add(new(pathId, nodeIds, edgeIds));
        }

        foreach (var output in outputs.Where(output => output.Context?.Flow?.VirtualDeviceId is not null))
        {
            var producerId = $"output:{output.OutputId}";
            var virtualId = $"virtual:{output.Context!.Flow!.VirtualDeviceId}";
            if (represented.Contains(producerId) || !runtimeNodes.ContainsKey(producerId) || !nodes.ContainsKey(virtualId)) continue;
            var chain = GetAncestry(producerId);
            var state = output.IsWarm ? StreamingFlowState.Warm : StreamingFlowState.Active;
            var sourceId = AddSource(output.Context.Flow.PhysicalDeviceId, chain, $"producer-source:{producerId}");
            MarkWarm(sourceId, state);
            MarkWarm(virtualId, state);
            var route = new List<string> { sourceId };
            route.AddRange(AddSourceProcessors(chain, sourceId, null, producerId, state));
            route.Add(virtualId);
            route.AddRange(chain.Where(id => runtimeNodes[id].Lane == StreamingFlowLane.VirtualProcessing)
                .Select(id => AddProcessor(id, StreamingFlowLane.VirtualProcessing, virtualId)));
            ConnectChain(route, state);
        }

        foreach (var producer in runtime.Nodes.Where(node => node.Lane is StreamingFlowLane.PhysicalProcessing or StreamingFlowLane.VirtualProcessing ||
            node.Lane == StreamingFlowLane.PhysicalSource && node.State == StreamingFlowState.Warm && !upstreamIds.Contains(node.Id)))
        {
            if (represented.Contains(producer.Id)) continue;
            var chain = GetAncestry(producer.Id);
            var state = producer.State == StreamingFlowState.Warm ? StreamingFlowState.Warm
                : producer.State == StreamingFlowState.Unavailable ? StreamingFlowState.Idle : StreamingFlowState.Active;
            var sourceId = AddSource(null, chain, $"producer-source:{producer.Id}");
            MarkWarm(sourceId, state);
            var route = new List<string> { sourceId };
            route.AddRange(AddSourceProcessors(chain, sourceId, null, producer.Id, state));
            route.AddRange(chain.Where(id => runtimeNodes[id].Lane == StreamingFlowLane.VirtualProcessing)
                .Select(id => AddProcessor(id, StreamingFlowLane.VirtualProcessing, null)));
            ConnectChain(route, state);
        }

        foreach (var sourceId in replacedSources) nodes.Remove($"physical-processing:{sourceId["physical:".Length..]}");
        foreach (var virtualId in replacedVirtuals) nodes.Remove($"virtual-processing:{virtualId["virtual:".Length..]}");
        foreach (var source in nodes.Values.Where(node => node.Lane == StreamingFlowLane.PhysicalSource).ToArray())
        {
            if (!source.Id.StartsWith("physical:", StringComparison.Ordinal)) continue;
            var processorId = $"physical-processing:{source.Id["physical:".Length..]}";
            if (nodes.ContainsKey(processorId)) Connect(source.Id, processorId);
        }
        foreach (var output in nodes.Values.Where(node => node.Lane == StreamingFlowLane.VirtualOutput).ToArray())
        {
            var processorId = $"virtual-processing:{output.Id["virtual:".Length..]}";
            if (nodes.ContainsKey(processorId)) Connect(output.Id, processorId);
        }
        var topologyNodes = nodes.Values.OrderBy(node => node.Lane is StreamingFlowLane.PhysicalProcessing or StreamingFlowLane.VirtualProcessing
            ? node.Id : string.Empty, StringComparer.Ordinal).ToArray();
        return new(topologyNodes, edges.Values.ToArray(), paths, []);

        IReadOnlyList<string> GetAncestry(string producerId)
        {
            var chain = new List<string>();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            string? current = producerId;
            while (current is not null && visited.Add(current))
            {
                chain.Add(current);
                current = parents.GetValueOrDefault(current);
            }
            chain.Reverse();
            return chain;
        }

        string AddSource(PhysicalDeviceId? physicalId, IReadOnlyList<string> chain, string fallbackId)
        {
            physicalId ??= chain.Where(id => id.StartsWith("runtime:", StringComparison.Ordinal))
                .Select(id => runtimePhysicalDevices.GetValueOrDefault(id["runtime:".Length..])).FirstOrDefault(id => id.HasValue);
            var producer = chain.Select(id => runtimeNodes[id]).FirstOrDefault(node => node.Lane == StreamingFlowLane.PhysicalSource);
            var id = physicalId is not null ? $"physical:{physicalId}" : producer?.Id ?? fallbackId;
            if (!nodes.ContainsKey(id))
            {
                nodes.Add(id, producer is null
                    ? new(id, StreamingFlowLane.PhysicalSource, "Unavailable source", "Source attribution not reported", StreamingFlowState.Unavailable, [])
                    : producer with { Id = id });
            }
            return id;
        }

        IReadOnlyList<string> AddSourceProcessors(
            IReadOnlyList<string> chain,
            string sourceId,
            StreamingFlowPathStage? stage,
            string fallbackIdentity,
            StreamingFlowState state
        )
        {
            if (sourceId.StartsWith("physical:", StringComparison.Ordinal)) replacedSources.Add(sourceId);
            var producers = chain.Where(id => runtimeNodes[id].Lane == StreamingFlowLane.PhysicalProcessing).ToArray();
            if (producers.Length == 0)
            {
                producers = chain.Where(id => runtimeNodes[id].Lane == StreamingFlowLane.PhysicalSource).Take(1).ToArray();
            }
            if (producers.Length > 0)
            {
                return producers.Select(producer => AddProcessor(producer, StreamingFlowLane.PhysicalProcessing, sourceId,
                    producer == producers[^1] ? stage : null)).ToArray();
            }
            var id = $"source-processing:{fallbackIdentity}";
            AddUnreportedProcessor(id, StreamingFlowLane.PhysicalProcessing, sourceId, stage, null, "Source producer not reported", state);
            return [id];
        }

        string AddProcessor(string producerId, StreamingFlowLane lane, string? contextId, StreamingFlowPathStage? stage = null)
        {
            represented.Add(producerId);
            if (lane == StreamingFlowLane.VirtualProcessing && contextId is not null) replacedVirtuals.Add(contextId);
            var id = $"{(lane == StreamingFlowLane.PhysicalProcessing ? "source" : "output")}-processing:{producerId}";
            var producer = runtimeNodes[producerId];
            var existing = nodes.GetValueOrDefault(id);
            var encodings = producer.VideoEncodings.Count > 0 ? producer.VideoEncodings
                : existing?.VideoEncodings.Count > 0 ? existing.VideoEncodings : stage?.VideoEncoding is { } encoding ? [encoding] : [];
            var passThrough = lane == StreamingFlowLane.PhysicalProcessing && producer.Lane == StreamingFlowLane.PhysicalSource &&
                !producerId.StartsWith("synthetic:", StringComparison.Ordinal);
            if (passThrough && encodings.Count == 0) encodings = [new("copy", null, false)];
            var resources = producer.ResourceIds.Count > 0 ? producer.ResourceIds
                : (existing?.ResourceIds ?? []).Concat(stage?.ResourceIds ?? []).Distinct(StringComparer.Ordinal).ToArray();
            nodes[id] = producer with
            {
                Id = id,
                Lane = lane,
                Label = GetProcessorLabel(lane, encodings, resources),
                State = producer.State == StreamingFlowState.Active && encodings.Any(encoding => encoding.Encoder != "copy") ? StreamingFlowState.Transcoding : producer.State,
                Subtitle = "Output attribution not reported",
                IsLogicalStage = producerId.StartsWith("synthetic:", StringComparison.Ordinal),
                VideoEncodings = encodings,
                ResourceIds = resources,
                Details = producer.Details.Concat(stage?.Details ?? []).DistinctBy(detail => detail.Label, StringComparer.Ordinal)
                    .Append(new("Producer identity", producerId)).ToArray()
            };
            if (contextId is not null)
            {
                if (!processorContexts.TryGetValue(id, out var contexts))
                {
                    contexts = new(StringComparer.Ordinal);
                    processorContexts.Add(id, contexts);
                }
                contexts.Add(contextId);
                nodes[id] = nodes[id] with
                {
                    Subtitle = string.Join(", ", contexts.Select(context => nodes[context].Label).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                };
            }
            return id;
        }

        void AddUnreportedProcessor(
            string id,
            StreamingFlowLane lane,
            string contextId,
            StreamingFlowPathStage? stage,
            string? channel,
            string identity,
            StreamingFlowState state
        )
        {
            if (lane == StreamingFlowLane.VirtualProcessing) replacedVirtuals.Add(contextId);
            var encodings = stage?.VideoEncoding is { } encoding ? new[] { encoding } : [];
            var resources = stage?.ResourceIds ?? [];
            var processor = nodes.GetValueOrDefault(id) ?? new StreamingFlowNode(id, lane, GetProcessorLabel(lane, encodings, resources),
                nodes[contextId].Label, encodings.Any(encoding => encoding.Encoder != "copy") ? StreamingFlowState.Transcoding : state,
                (stage?.Details ?? []).Append(new("Producer identity", identity)).ToArray())
            {
                VideoEncodings = encodings,
                ResourceIds = resources
            };
            nodes[id] = processor with
            {
                State = processor.ActiveSessionCount > 0 ? StreamingFlowState.Shared : processor.State,
                ActiveSessionCount = processor.ActiveSessionCount + (stage is null ? 0 : 1),
                ActiveChannels = channel is null ? processor.ActiveChannels : processor.ActiveChannels.Append(channel).Distinct(StringComparer.Ordinal).ToArray()
            };
        }

        static string GetProcessorLabel(StreamingFlowLane lane, IReadOnlyList<VideoEncodingSnapshot> encodings, IReadOnlyList<string> resources) =>
            encodings.Any(encoding => encoding.Encoder != "copy") ? lane == StreamingFlowLane.PhysicalProcessing ? "Source Transcoder" : "Output Transcoder"
                : encodings.Count > 0 ? resources.Count > 0 ? "Copy / processing" : "Pass-through"
                : lane == StreamingFlowLane.PhysicalProcessing ? "Source processing" : "Output processing";

        void MarkWarm(string id, StreamingFlowState state)
        {
            if (state == StreamingFlowState.Warm && nodes[id].State == StreamingFlowState.Idle) nodes[id] = nodes[id] with { State = state };
        }

        IReadOnlyList<string> ConnectChain(IReadOnlyList<string> route, StreamingFlowState state) =>
            route.Skip(1).Select((target, index) => Connect(route[index], target, state)).ToArray();

        string Connect(string source, string target, StreamingFlowState state = StreamingFlowState.Idle)
        {
            var id = $"edge:{source.Length}:{source}{target}";
            if (!edges.TryGetValue(id, out var edge) || edge.State == StreamingFlowState.Idle || state == StreamingFlowState.Active)
            {
                edges[id] = new(id, source, target) { State = state };
            }
            return id;
        }
    }
}
