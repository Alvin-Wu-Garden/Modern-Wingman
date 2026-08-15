using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AgentService.Modules.GraphRAG;

namespace AgentService.Modules.GraphRAG.FblAuthority;

/// <summary>
/// 對 ParallelExtractor 原始圖執行確定性的單層加權 Leiden 分群。
/// 所有有邊節點與所有原始關係都參與；關係方向不影響社群，occurrenceCount 是邊權重。
/// </summary>
public static class FblAuthorityCommunityBuilder
{
    private const double Epsilon = 1e-12;
    private const int MaxLocalMovePasses = 100;

    /// <summary>
    /// 建立單層社群；孤立節點不建立 Community。
    /// 使用壓縮稀疏列（CSR）保存鄰接圖，避免大型專案為每個節點建立 Dictionary。
    /// </summary>
    public static IReadOnlyList<GraphCommunityReportV4> Build(
        GraphDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();

        var nodes = document.Nodes.ToArray();
        var nodesById = nodes.ToDictionary(node => node.Key, StringComparer.Ordinal);
        var graph = BuildAdjacency(document.Relationships, nodes, cancellationToken);
        if (graph.OrderedConnectedNodes.Length == 0)
            return [];

        var assignment = LocalMove(graph, cancellationToken);
        var groups = SplitDisconnectedCommunities(graph, assignment, cancellationToken);
        var reports = new GraphCommunityReportV4[groups.Count];
        for (var index = 0; index < groups.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var memberIds = groups[index]
                .Select(nodeIndex => graph.NodeIds[nodeIndex])
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            reports[index] = CreateReport(memberIds, nodesById);
        }

        Array.Sort(reports, static (left, right) =>
            string.CompareOrdinal(left.MemberIds[0], right.MemberIds[0]));
        return reports;
    }

    private static CsrGraph BuildAdjacency(
        IReadOnlyList<GraphRelationship> relationships,
        IReadOnlyList<GraphNode> nodes,
        CancellationToken cancellationToken)
    {
        var nodeIds = new string[nodes.Count];
        var nodeIndices = new Dictionary<string, int>(nodes.Count, StringComparer.Ordinal);
        for (var index = 0; index < nodes.Count; index++)
        {
            var nodeId = nodes[index].Key;
            nodeIds[index] = nodeId;
            nodeIndices.Add(nodeId, index);
        }

        var adjacencyCounts = new int[nodes.Count];
        var adjacencyEntryCount = 0;
        for (var index = 0; index < relationships.Count; index++)
        {
            if ((index & 0x3fff) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            var edge = relationships[index];
            if (!nodeIndices.TryGetValue(edge.SourceKey, out var source) ||
                !nodeIndices.TryGetValue(edge.TargetKey, out var target))
            {
                throw new InvalidOperationException("Community 分群遇到端點不存在的原始關係。");
            }

            adjacencyCounts[source] = checked(adjacencyCounts[source] + 1);
            adjacencyEntryCount = checked(adjacencyEntryCount + 1);
            if (source != target)
            {
                adjacencyCounts[target] = checked(adjacencyCounts[target] + 1);
                adjacencyEntryCount = checked(adjacencyEntryCount + 1);
            }
        }

        var offsets = new int[nodes.Count + 1];
        for (var index = 0; index < adjacencyCounts.Length; index++)
            offsets[index + 1] = checked(offsets[index] + adjacencyCounts[index]);

        var neighbors = new int[adjacencyEntryCount];
        var weights = new double[adjacencyEntryCount];
        var weightedDegree = new double[nodes.Count];
        var cursors = offsets.AsSpan(0, nodes.Count).ToArray();
        for (var index = 0; index < relationships.Count; index++)
        {
            if ((index & 0x3fff) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            var edge = relationships[index];
            var source = nodeIndices[edge.SourceKey];
            var target = nodeIndices[edge.TargetKey];
            var weight = ReadWeight(edge.Properties);
            AddAdjacencyEntry(source, target, weight, cursors, neighbors, weights, weightedDegree);
            if (source != target)
                AddAdjacencyEntry(target, source, weight, cursors, neighbors, weights, weightedDegree);
        }

        var orderedConnectedNodes = Enumerable.Range(0, nodes.Count)
            .Where(index => adjacencyCounts[index] > 0)
            .OrderBy(index => nodeIds[index], StringComparer.Ordinal)
            .ToArray();
        return new CsrGraph(nodeIds, offsets, neighbors, weights, weightedDegree, orderedConnectedNodes);
    }

    private static void AddAdjacencyEntry(
        int source,
        int target,
        double weight,
        int[] cursors,
        int[] neighbors,
        double[] weights,
        double[] weightedDegree)
    {
        var position = cursors[source]++;
        neighbors[position] = target;
        weights[position] = weight;
        weightedDegree[source] += weight;
    }

    private static double ReadWeight(IReadOnlyDictionary<string, object?> properties)
    {
        if (!properties.TryGetValue("occurrenceCount", out var value) || value is null)
            return 1d;
        return double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : 1d;
    }

    /// <summary>Leiden 第一階段：以固定節點順序做加權 modularity local moving。</summary>
    private static int[] LocalMove(CsrGraph graph, CancellationToken cancellationToken)
    {
        var nodeCount = graph.NodeIds.Length;
        var community = Enumerable.Range(0, nodeCount).ToArray();
        var totalWeight = graph.OrderedConnectedNodes.Sum(index => graph.WeightedDegree[index]);
        if (totalWeight <= Epsilon)
            return community;

        var totals = new double[nodeCount];
        var accumulatedWeights = new double[nodeCount];
        var accumulationStamp = new int[nodeCount];
        var maxNeighborCount = graph.OrderedConnectedNodes.Max(index => graph.Offsets[index + 1] - graph.Offsets[index]);
        var candidateCommunities = new int[maxNeighborCount];
        var stamp = 0;

        for (var pass = 0; pass < MaxLocalMovePasses; pass++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Array.Clear(totals);
            foreach (var node in graph.OrderedConnectedNodes)
                totals[community[node]] += graph.WeightedDegree[node];

            var changed = false;
            foreach (var node in graph.OrderedConnectedNodes)
            {
                if ((++stamp & 0x3fff) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                if (stamp == int.MaxValue)
                {
                    Array.Clear(accumulationStamp);
                    stamp = 1;
                }

                var oldCommunity = community[node];
                var degree = graph.WeightedDegree[node];
                totals[oldCommunity] -= degree;
                var candidateCount = 0;
                for (var position = graph.Offsets[node]; position < graph.Offsets[node + 1]; position++)
                {
                    var candidate = community[graph.Neighbors[position]];
                    if (accumulationStamp[candidate] != stamp)
                    {
                        accumulationStamp[candidate] = stamp;
                        accumulatedWeights[candidate] = 0d;
                        candidateCommunities[candidateCount++] = candidate;
                    }
                    accumulatedWeights[candidate] += graph.Weights[position];
                }

                var bestCommunity = oldCommunity;
                var bestGain = 0d;
                for (var candidateIndex = 0; candidateIndex < candidateCount; candidateIndex++)
                {
                    var candidate = candidateCommunities[candidateIndex];
                    var gain = accumulatedWeights[candidate] - degree * totals[candidate] / totalWeight;
                    if (gain > bestGain + Epsilon ||
                        Math.Abs(gain - bestGain) <= Epsilon &&
                        string.CompareOrdinal(graph.NodeIds[candidate], graph.NodeIds[bestCommunity]) < 0)
                    {
                        bestGain = gain;
                        bestCommunity = candidate;
                    }
                }

                community[node] = bestCommunity;
                totals[bestCommunity] += degree;
                changed |= bestCommunity != oldCommunity;
            }

            if (!changed)
                break;
        }

        return community;
    }

    /// <summary>Leiden refinement：同一社群若不是連通圖，確定性拆成不同群。</summary>
    private static List<int[]> SplitDisconnectedCommunities(
        CsrGraph graph,
        int[] assignment,
        CancellationToken cancellationToken)
    {
        var visited = new bool[graph.NodeIds.Length];
        var queue = new Queue<int>();
        var groups = new List<int[]>();

        foreach (var root in graph.OrderedConnectedNodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (visited[root])
                continue;

            var rootCommunity = assignment[root];
            var members = new List<int>();
            visited[root] = true;
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                members.Add(current);
                for (var position = graph.Offsets[current]; position < graph.Offsets[current + 1]; position++)
                {
                    var neighbor = graph.Neighbors[position];
                    if (!visited[neighbor] && assignment[neighbor] == rootCommunity)
                    {
                        visited[neighbor] = true;
                        queue.Enqueue(neighbor);
                    }
                }
            }
            groups.Add(members.ToArray());
        }

        return groups;
    }

    private static GraphCommunityReportV4 CreateReport(
        IReadOnlyList<string> memberIds,
        IReadOnlyDictionary<string, GraphNode> nodesById)
    {
        var identity = string.Join('\n', memberIds);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        var communityId = $"community:{hash[..24]}";
        var labels = memberIds.Select(id => nodesById[id].Kind.ToString())
            .GroupBy(value => value, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => $"{group.Key} {group.Count()}")
            .ToArray();
        var title = memberIds.Select(id => DisplayName(nodesById[id]))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? communityId;
        var summary = $"包含 {memberIds.Count} 個節點：{string.Join("、", labels)}。";
        var cacheKey = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{ProjectGraphVersions.Community}\n{identity}"))).ToLowerInvariant();
        return new GraphCommunityReportV4(
            communityId,
            "C0",
            null,
            true,
            title,
            summary,
            GraphCommunitySummaryStates.Template,
            0,
            memberIds,
            memberIds.Count,
            memberIds.Where(id => nodesById[id].Kind == GraphNodeKind.DatabaseObject)
                .Select(id => DisplayName(nodesById[id])).Take(20).ToArray(),
            memberIds.Where(id => nodesById[id].Kind is GraphNodeKind.MenuItem or GraphNodeKind.ApiEndpoint)
                .Select(id => DisplayName(nodesById[id])).Take(20).ToArray(),
            cacheKey,
            false,
            0,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["algorithm"] = "weighted-leiden-single-level" });
    }

    private static string DisplayName(GraphNode node)
    {
        foreach (var key in new[] { "name", "fullName", "path", "qualifiedName", "objectName" })
            if (node.Properties.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value?.ToString()))
                return value!.ToString()!;
        return node.Key;
    }

    /// <summary>以整數索引保存大型圖，避免每個節點各自配置雜湊表。</summary>
    private sealed record CsrGraph(
        string[] NodeIds,
        int[] Offsets,
        int[] Neighbors,
        double[] Weights,
        double[] WeightedDegree,
        int[] OrderedConnectedNodes);
}
