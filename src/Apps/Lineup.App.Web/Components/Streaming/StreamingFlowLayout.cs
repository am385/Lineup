using Lineup.Core.Streaming;

namespace Lineup.App.Web.Components.Streaming;

/// <summary>Orders cards within their fixed lanes to reduce connection crossings without changing stream topology.</summary>
internal static class StreamingFlowLayout
{
    /// <summary>Groups same-lane processing chains and reduces crossings with stable, bidirectional barycenter sweeps.</summary>
    internal static IReadOnlyList<StreamingFlowNode> Order(IReadOnlyList<StreamingFlowNode> nodes, IReadOnlyList<StreamingFlowEdge> edges)
    {
        var byId = nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var connections = edges.Where(edge => byId.ContainsKey(edge.Source) && byId.ContainsKey(edge.Target)).ToArray();
        var neighbors = connections.SelectMany(edge => new[] { (Id: edge.Source, Other: edge.Target), (Id: edge.Target, Other: edge.Source) })
            .ToLookup(pair => pair.Id, pair => pair.Other, StringComparer.Ordinal);
        var groups = new Dictionary<string, string>(StringComparer.Ordinal);
        var chains = new Dictionary<string, IReadOnlyList<StreamingFlowNode>>(StringComparer.Ordinal);
        foreach (var node in nodes.OrderBy(node => node.Id, StringComparer.Ordinal))
        {
            if (groups.ContainsKey(node.Id)) continue;
            var pending = new Queue<string>();
            var members = new HashSet<string>(StringComparer.Ordinal);
            pending.Enqueue(node.Id);
            while (pending.TryDequeue(out var id))
            {
                if (!members.Add(id)) continue;
                groups.Add(id, node.Id);
                foreach (var neighbor in neighbors[id].Where(other => byId[other].Lane == node.Lane)) pending.Enqueue(neighbor);
            }
            chains.Add(node.Id, OrderChain(members, connections, byId));
        }

        var lanes = chains.Keys.GroupBy(id => byId[id].Lane).OrderBy(group => group.Key).Select(group => group.ToArray()).ToArray();
        var links = connections.Where(edge => byId[edge.Source].Lane < byId[edge.Target].Lane).DistinctBy(edge => (edge.Source, edge.Target)).ToArray();
        var incoming = links.ToLookup(edge => groups[edge.Target], edge => edge.Source, StringComparer.Ordinal);
        var outgoing = links.ToLookup(edge => groups[edge.Source], edge => edge.Target, StringComparer.Ordinal);
        var best = lanes.Select(lane => lane.ToArray()).ToArray();
        var bestScore = CountCrossings();
        for (var sweep = 0; sweep < 4 && bestScore > 0; sweep++)
        {
            var forward = sweep % 2 == 0;
            var adjacent = forward ? incoming : outgoing;
            var indices = forward ? Enumerable.Range(0, lanes.Length) : Enumerable.Range(0, lanes.Length).Reverse();
            foreach (var index in indices)
            {
                var ranks = GetRanks();
                lanes[index] = lanes[index].OrderBy(id => adjacent[id].Distinct(StringComparer.Ordinal).Select(other => (double)ranks[other])
                        .DefaultIfEmpty(chains[id].Average(node => ranks[node.Id])).Average())
                    .ThenBy(id => ranks[chains[id][0].Id]).ToArray();
            }
            var score = CountCrossings();
            if (score >= bestScore) continue;
            bestScore = score;
            best = lanes.Select(lane => lane.ToArray()).ToArray();
        }
        return best.SelectMany(lane => lane.SelectMany(id => chains[id])).ToArray();

        Dictionary<string, int> GetRanks() => lanes.SelectMany(lane => lane.SelectMany(id => chains[id]).Select((node, rank) => (id: node.Id, rank)))
            .ToDictionary(item => item.id, item => item.rank, StringComparer.Ordinal);

        long CountCrossings()
        {
            var ranks = GetRanks();
            long crossings = 0;
            foreach (var pair in links.GroupBy(edge => (byId[edge.Source].Lane, byId[edge.Target].Lane)))
            {
                var related = pair.ToArray();
                for (var first = 0; first < related.Length; first++)
                {
                    for (var second = first + 1; second < related.Length; second++)
                    {
                        var a = related[first];
                        var b = related[second];
                        if ((long)(ranks[a.Source] - ranks[b.Source]) * (ranks[a.Target] - ranks[b.Target]) < 0) crossings++;
                    }
                }
            }
            return crossings;
        }
    }

    private static IReadOnlyList<StreamingFlowNode> OrderChain(HashSet<string> members, IReadOnlyList<StreamingFlowEdge> edges, IReadOnlyDictionary<string, StreamingFlowNode> nodes)
    {
        var links = edges.Where(edge => members.Contains(edge.Source) && members.Contains(edge.Target)).ToArray();
        var incoming = links.GroupBy(edge => edge.Target).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var outgoing = links.ToLookup(edge => edge.Source, edge => edge.Target, StringComparer.Ordinal);
        var ready = new SortedSet<string>(members.Where(id => !incoming.ContainsKey(id)), StringComparer.Ordinal);
        var ordered = new List<StreamingFlowNode>();
        while (ready.Count > 0)
        {
            var id = ready.Min!;
            ready.Remove(id);
            ordered.Add(nodes[id]);
            foreach (var target in outgoing[id])
            {
                if (--incoming[target] == 0) ready.Add(target);
            }
        }
        if (ordered.Count != members.Count) throw new InvalidOperationException("The streaming flow contains a cycle within a processing lane.");
        return ordered;
    }
}
