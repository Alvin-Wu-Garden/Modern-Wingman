using System.Text.Json;
using Neo4j.Driver;

namespace AgentService.Modules.GraphRAG;

/// <summary>
/// Neo4j Graph Store 的唯讀視覺化與受限 Cypher 查詢功能。
/// 此 partial 共用核心 Store 的連線與 V4 active version 邊界。
/// </summary>
public sealed partial class Neo4jGraphStore
{

    /// <summary>
    /// 以通用 Viewer Contract 取得 active V4 graph 初始子圖。
    /// 篩選值先轉回既有 V4 kind／relationship 白名單，避免 Viewer 引入第二套 schema。
    /// </summary>
    public Task<GraphVisualData> GetViewerGraphAsync(
        string projectId,
        int limit,
        IReadOnlyList<GraphViewerSearchFilter>? filters,
        CancellationToken cancellationToken = default)
    {
        var (kinds, relationships) = ResolveViewerFilters(filters);
        return GetVisualGraphAsync(
            projectId,
            Math.Clamp(limit, 1, 5_000),
            kinds,
            relationships,
            cancellationToken);
    }

    /// <summary>
    /// 以 V4 full-text index 執行 bounded 全域搜尋，供圖譜 Viewer 在既有初始子圖之外
    /// 尋找遠端節點。搜尋結果仍使用 authority node 投影，不建立任何新的 graph entity。
    /// </summary>
    public async Task<GraphViewerSearchResult> SearchVisualGraphAsync(
        string projectId,
        string query,
        int take,
        IReadOnlyList<GraphViewerSearchFilter>? filters,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        take = Math.Clamp(take, 1, 100);
        var (kinds, relationships) = ResolveViewerFilters(filters);
        var allowedKinds = kinds.Count == 0
            ? null
            : kinds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var luceneQuery = GraphRetrievalService.BuildViewerLuceneQuery(query);
        // SearchAsync 內部已按 active project/version 分頁；多取一點再套 facet，
        // 避免高分但不符合 kind 的節點把真正候選擠出結果。
        var candidates = await SearchAsync(
            projectId,
            luceneQuery,
            Math.Min(100, Math.Max(take * 3, take)),
            cancellationToken);
        var relationshipNodeIds = relationships.Count == 0 || candidates.Count == 0
            ? null
            : await FindNodesWithRelationshipTypesAsync(
                projectId,
                candidates.Select(hit => hit.Node.Key).ToArray(),
                relationships,
                cancellationToken);
        var hits = candidates
            .Where(hit => allowedKinds is null ||
                          allowedKinds.Contains(hit.Node.Kind.ToString()))
            .Where(hit => relationshipNodeIds is null ||
                          relationshipNodeIds.Contains(hit.Node.Key))
            .Take(take)
            .Select(hit => new GraphViewerSearchHit(
                MapVisualNode(hit.Node, 0),
                hit.Score))
            .ToArray();
        return new GraphViewerSearchResult(
            hits,
            hits.Length,
            candidates.Count > hits.Length);
    }

    /// <summary>
    /// 將 edge-type facet 套用到全域搜尋候選。
    /// 搜尋本身由 full-text index 完成，關係 facet 則以單次 bounded Cypher
    /// 只保留同 active project/version 且具有指定 relationship type 的候選 node，
    /// 避免 UI 顯示已套用篩選、實際結果卻完全忽略 edge-type 的落差。
    /// </summary>
    private async Task<IReadOnlySet<string>> FindNodesWithRelationshipTypesAsync(
        string projectId,
        IReadOnlyList<string> candidateIds,
        IReadOnlyList<string> relationshipTypes,
        CancellationToken cancellationToken)
    {
        if (_driver is null || candidateIds.Count == 0 || relationshipTypes.Count == 0)
            return new HashSet<string>(StringComparer.Ordinal);
        var graphVersion = await GetActiveManifestAsync(projectId, cancellationToken);
        if (graphVersion is null)
            return new HashSet<string>(StringComparer.Ordinal);

        await using var session = OpenReadSession();
        cancellationToken.ThrowIfCancellationRequested();
        return await session.ExecuteReadAsync(async transaction =>
        {
            var cursor = await transaction.RunAsync(
                """
                MATCH (source:GraphEntity {
                    wingmanProjectId: $projectId,
                    graphVersion: $graphVersion
                })-[relationship]->(target:GraphEntity {
                    wingmanProjectId: $projectId,
                    graphVersion: $graphVersion
                })
                WHERE type(relationship) IN $relationshipTypes
                  AND (source.id IN $candidateIds OR target.id IN $candidateIds)
                WITH collect(DISTINCT source.id) + collect(DISTINCT target.id) AS ids
                UNWIND ids AS id
                RETURN DISTINCT id
                """,
                new
                {
                    projectId,
                    graphVersion,
                    candidateIds,
                    relationshipTypes,
                });
            var matched = new HashSet<string>(StringComparer.Ordinal);
            while (await cursor.FetchAsync())
            {
                var id = cursor.Current["id"].As<string>();
                if (!string.IsNullOrWhiteSpace(id)) matched.Add(id);
            }
            return matched;
        });
    }

    /// <summary>將 Viewer facet 轉成現有 V4 API 的嚴格白名單。</summary>
    private static (IReadOnlyList<string> Kinds, IReadOnlyList<string> Relationships)
        ResolveViewerFilters(IReadOnlyList<GraphViewerSearchFilter>? filters)
    {
        if (filters is null || filters.Count == 0)
            return ([], []);

        var kinds = new List<string>();
        var relationships = new List<string>();
        foreach (var filter in filters)
        {
            if (filter is null ||
                string.IsNullOrWhiteSpace(filter.FacetId) ||
                filter.Tokens is null)
                continue;
            switch (filter.FacetId.Trim().ToLowerInvariant())
            {
                case "node-category":
                    kinds.AddRange(filter.Tokens);
                    break;
                case "edge-type":
                    relationships.AddRange(filter.Tokens);
                    break;
                default:
                    throw new ArgumentException(
                        $"不允許的 Viewer facet：{filter.FacetId}。",
                        nameof(filters));
            }
        }
        return (NormalizeKinds(kinds), NormalizeRelationships(relationships));
    }

    /// <inheritdoc />
    public async Task<GraphVisualData> GetVisualGraphAsync(
        string projectId,
        int limit,
        IReadOnlyList<string>? kinds,
        IReadOnlyList<string>? relationshipTypes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        limit = Math.Clamp(limit, 1, 5_000);
        var normalizedKinds = NormalizeKinds(kinds);
        var normalizedRelationships = NormalizeRelationships(relationshipTypes);
        if (_driver is null) return new([], [], 0, 0, 0, false);
        var graphVersion = await GetActiveManifestAsync(projectId, cancellationToken);
        if (graphVersion is null) return new([], [], 0, 0, 0, false);
        await using var session = OpenReadSession();
        cancellationToken.ThrowIfCancellationRequested();
        return await session.ExecuteReadAsync(async transaction =>
        {
            // 可視化圖必須先從「真的存在關係」的節點開始取樣。若先依 NodeKind 取滿 limit，
            // 大型專案很容易被數量龐大的 EntryPoint 填滿，之後再查兩端都在清單內的 edge
            // 就可能得到零條線。這裡先以 relationship 建立核心節點集合，再用一般重要節點
            // 補滿剩餘額度，既維持 bounded query，也確保預設畫面能呈現實際程式碼關聯。
            var relationshipSeedCursor = await transaction.RunAsync(
                """
                MATCH (source:GraphEntity {
                    wingmanProjectId: $projectId,
                    graphVersion: $graphVersion
                })-[relationship]->(target:GraphEntity {
                    wingmanProjectId: $projectId,
                    graphVersion: $graphVersion
                })
                WHERE (size($kinds) = 0 OR
                       (any(label IN labels(source) WHERE label IN $kinds) AND
                        any(label IN labels(target) WHERE label IN $kinds)))
                  AND (size($relationshipTypes) = 0 OR
                       type(relationship) IN $relationshipTypes)
                WITH source, target, relationship,
                    CASE
                        WHEN source:Solution THEN 0
                        WHEN source:Project THEN 1
                        WHEN source:File THEN 2
                        WHEN source:Type THEN 3
                        ELSE 4
                    END AS sourcePriority,
                    CASE
                        WHEN target:Solution THEN 0
                        WHEN target:Project THEN 1
                        WHEN target:File THEN 2
                        WHEN target:Type THEN 3
                        ELSE 4
                    END AS targetPriority
                ORDER BY
                    sourcePriority + targetPriority,
                    sourcePriority,
                    source.id,
                    target.id,
                    relationship.id
                LIMIT $edgeSeedLimit
                RETURN source.id AS sourceId, target.id AS targetId
                """,
                new
                {
                    projectId,
                    graphVersion,
                    kinds = normalizedKinds,
                    relationshipTypes = normalizedRelationships,
                    edgeSeedLimit = Math.Min(limit * 4, 20_000),
                });
            var relationshipSeeds = new List<(string SourceId, string TargetId)>();
            while (await relationshipSeedCursor.FetchAsync())
            {
                relationshipSeeds.Add((
                    relationshipSeedCursor.Current["sourceId"].As<string>(),
                    relationshipSeedCursor.Current["targetId"].As<string>()));
            }

            var coreNodeIds = SelectRelationshipCoreNodeIds(relationshipSeeds, limit);
            var nodeCursor = await transaction.RunAsync(
                """
                MATCH (node:GraphEntity {
                    wingmanProjectId: $projectId,
                    graphVersion: $graphVersion
                })
                WHERE size($kinds) = 0 OR any(label IN labels(node) WHERE label IN $kinds)
                OPTIONAL MATCH (node)-[relationship]-(:GraphEntity {
                    wingmanProjectId: $projectId,
                    graphVersion: $graphVersion
                })
                WITH node, count(relationship) AS degree,
                    CASE WHEN node.id IN $coreNodeIds THEN 0 ELSE 1 END AS corePriority
                ORDER BY
                    corePriority,
                    CASE
                        WHEN node:Solution THEN 0
                        WHEN node:Project THEN 1
                        WHEN node:File THEN 2
                        WHEN node:Type THEN 3
                        ELSE 4
                    END,
                    degree DESC,
                    node.id
                LIMIT $limit
                RETURN node, degree
                """,
                new
                {
                    projectId,
                    graphVersion,
                    kinds = normalizedKinds,
                    coreNodeIds,
                    limit,
                });
            var visualNodes = new List<GraphVisualNode>();
            while (await nodeCursor.FetchAsync())
                visualNodes.Add(MapVisualNode(
                    nodeCursor.Current["node"].As<INode>(),
                    nodeCursor.Current["degree"].As<int>()));

            var ids = visualNodes.Select(node => node.Id).ToList();
            var visualEdges = new List<GraphVisualEdge>();
            if (ids.Count > 0)
            {
                var edgeCursor = await transaction.RunAsync(
                    """
                    MATCH (source:GraphEntity {
                        wingmanProjectId: $projectId,
                        graphVersion: $graphVersion
                    })-[relationship]->(target:GraphEntity {
                        wingmanProjectId: $projectId,
                        graphVersion: $graphVersion
                    })
                    WHERE source.id IN $ids
                      AND target.id IN $ids
                      AND (size($relationshipTypes) = 0 OR type(relationship) IN $relationshipTypes)
                    RETURN relationship, source.id AS sourceId, target.id AS targetId
                    ORDER BY relationship.id
                    LIMIT $edgeLimit
                    """,
                    new
                    {
                        projectId,
                        graphVersion,
                        ids,
                        relationshipTypes = normalizedRelationships,
                        edgeLimit = Math.Min(limit * 4, 20_000),
                    });
                while (await edgeCursor.FetchAsync())
                    visualEdges.Add(MapVisualEdge(
                        edgeCursor.Current["relationship"].As<IRelationship>(),
                        edgeCursor.Current["sourceId"].As<string>(),
                        edgeCursor.Current["targetId"].As<string>()));
            }

            var countCursor = await transaction.RunAsync(
                """
                MATCH (node:GraphEntity {
                    wingmanProjectId: $projectId,
                    graphVersion: $graphVersion
                })
                WHERE size($kinds) = 0 OR any(label IN labels(node) WHERE label IN $kinds)
                RETURN count(node) AS total
                """,
                new { projectId, graphVersion, kinds = normalizedKinds });
            var total = (await countCursor.SingleAsync())["total"].As<int>();
            return new GraphVisualData(
                visualNodes,
                visualEdges,
                total,
                visualNodes.Count,
                visualEdges.Count,
                total > visualNodes.Count);
        });
    }

    /// <summary>
    /// 依照已排序的 relationship 候選，建立不超過 node budget 的關係核心。
    /// 一條 relationship 的兩端必須一起納入；若剩餘額度不足，就跳過該關係，
    /// 避免留下只有單端、畫面仍無法繪線的半套取樣結果。
    /// </summary>
    /// <param name="relationships">已依重要性排序的來源與目標 ID。</param>
    /// <param name="nodeLimit">可視化圖允許載入的 node 數量上限。</param>
    /// <returns>依首次出現順序排列且不重複的核心 node IDs。</returns>
    internal static IReadOnlyList<string> SelectRelationshipCoreNodeIds(
        IEnumerable<(string SourceId, string TargetId)> relationships,
        int nodeLimit)
    {
        ArgumentNullException.ThrowIfNull(relationships);
        if (nodeLimit <= 0) return [];

        var selected = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<string>(Math.Min(nodeLimit, 5_000));
        foreach (var (sourceId, targetId) in relationships)
        {
            if (string.IsNullOrWhiteSpace(sourceId) ||
                string.IsNullOrWhiteSpace(targetId))
                continue;

            // Self-loop 只需要一個額度；一般關係則必須確認兩端能原子加入。
            var missing = new List<string>(2);
            if (!selected.Contains(sourceId))
                missing.Add(sourceId);
            if (!selected.Contains(targetId) &&
                !string.Equals(sourceId, targetId, StringComparison.Ordinal))
                missing.Add(targetId);
            if (selected.Count + missing.Count > nodeLimit)
                continue;

            foreach (var nodeId in missing)
            {
                selected.Add(nodeId);
                ordered.Add(nodeId);
            }
            if (selected.Count == nodeLimit)
                break;
        }
        return ordered;
    }

    /// <summary>
    /// 將 Cypher aggregate 帶回的 node 集合限制在服務端 budget 內。
    /// 優先保留現有集合中可形成完整 relationship 的端點，再依原始出現順序補入孤立節點。
    /// </summary>
    internal static IReadOnlyList<string> SelectBoundedVisualNodeIds(
        IEnumerable<string> nodeIds,
        IEnumerable<GraphVisualEdge> edges,
        int nodeLimit)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        ArgumentNullException.ThrowIfNull(edges);
        if (nodeLimit <= 0) return [];

        var available = nodeIds
            .Where(nodeId => !string.IsNullOrWhiteSpace(nodeId))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (available.Count <= nodeLimit) return available;

        var availableSet = available.ToHashSet(StringComparer.Ordinal);
        var core = SelectRelationshipCoreNodeIds(
            edges.Where(edge => availableSet.Contains(edge.Source) &&
                                availableSet.Contains(edge.Target))
                .Select(edge => (edge.Source, edge.Target)),
            nodeLimit);
        var selected = core.ToHashSet(StringComparer.Ordinal);
        var result = core.ToList();
        foreach (var nodeId in available)
        {
            if (result.Count >= nodeLimit) break;
            if (selected.Add(nodeId)) result.Add(nodeId);
        }
        return result;
    }

    /// <summary>
    /// 在剩餘 node budget 內，依 edge 順序挑選可完整補齊的端點 IDs。
    /// 同一條 edge 缺少的所有端點必須一起納入，避免花費額度後仍留下 orphan edge。
    /// </summary>
    internal static IReadOnlyList<string> SelectMissingVisualEndpointIds(
        IEnumerable<GraphVisualEdge> edges,
        IReadOnlySet<string> existingNodeIds,
        int nodeBudget)
    {
        ArgumentNullException.ThrowIfNull(edges);
        ArgumentNullException.ThrowIfNull(existingNodeIds);
        if (nodeBudget <= 0) return [];

        var selected = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<string>(nodeBudget);
        foreach (var edge in edges)
        {
            var missing = new List<string>(2);
            if (!existingNodeIds.Contains(edge.Source) &&
                !selected.Contains(edge.Source))
                missing.Add(edge.Source);
            if (!existingNodeIds.Contains(edge.Target) &&
                !selected.Contains(edge.Target) &&
                !string.Equals(edge.Source, edge.Target, StringComparison.Ordinal))
                missing.Add(edge.Target);
            if (selected.Count + missing.Count > nodeBudget)
                continue;

            foreach (var nodeId in missing)
            {
                selected.Add(nodeId);
                ordered.Add(nodeId);
            }
        }
        return ordered;
    }

    /// <summary>
    /// 僅保留來源與目標都存在於可視化 node 集合的 edges。
    /// </summary>
    internal static IReadOnlyList<GraphVisualEdge> KeepVisualEdgesWithEndpoints(
        IEnumerable<GraphVisualEdge> edges,
        IReadOnlySet<string> nodeIds)
    {
        ArgumentNullException.ThrowIfNull(edges);
        ArgumentNullException.ThrowIfNull(nodeIds);
        return edges
            .Where(edge => nodeIds.Contains(edge.Source) &&
                           nodeIds.Contains(edge.Target))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<GraphVisualSchema> GetVisualSchemaAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        if (_driver is null)
            return new(0, 0, [], [], VisualPropertyKeys);
        var graphVersion = await GetActiveManifestAsync(projectId, cancellationToken);
        if (graphVersion is null)
            return new(0, 0, [], [], VisualPropertyKeys);
        await using var session = OpenReadSession();
        cancellationToken.ThrowIfCancellationRequested();
        return await session.ExecuteReadAsync(async transaction =>
        {
            var nodeCursor = await transaction.RunAsync(
                """
                MATCH (node:GraphEntity {
                    wingmanProjectId: $projectId,
                    graphVersion: $graphVersion
                })
                UNWIND [label IN labels(node) WHERE label <> 'GraphEntity'] AS name
                RETURN name, count(node) AS count
                ORDER BY name
                """,
                new { projectId, graphVersion });
            var nodeKinds = new List<GraphFacet>();
            while (await nodeCursor.FetchAsync())
                nodeKinds.Add(new GraphFacet(
                    nodeCursor.Current["name"].As<string>(),
                    nodeCursor.Current["count"].As<int>()));

            var edgeCursor = await transaction.RunAsync(
                """
                MATCH (:GraphEntity {
                    wingmanProjectId: $projectId,
                    graphVersion: $graphVersion
                })-[relationship]->(:GraphEntity {
                    wingmanProjectId: $projectId,
                    graphVersion: $graphVersion
                })
                RETURN type(relationship) AS name, count(relationship) AS count
                ORDER BY name
                """,
                new { projectId, graphVersion });
            var relationships = new List<GraphFacet>();
            while (await edgeCursor.FetchAsync())
                relationships.Add(new GraphFacet(
                    edgeCursor.Current["name"].As<string>(),
                    edgeCursor.Current["count"].As<int>()));
            return new GraphVisualSchema(
                nodeKinds.Sum(item => item.Count),
                relationships.Sum(item => item.Count),
                nodeKinds,
                relationships,
                VisualPropertyKeys)
            {
                GraphRevision = graphVersion,
            };
        });
    }

    /// <inheritdoc />
    public async Task<GraphVisualData> GetVisualNeighborsAsync(
        string projectId,
        IReadOnlyList<string> nodeIds,
        int depth,
        int limit,
        string mode,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(nodeIds);
        depth = Math.Clamp(depth, 1, 4);
        limit = Math.Clamp(limit, 1, 5_000);
        var normalizedMode = NormalizeVisualNeighborMode(mode);

        // same-file 不是關係方向的別名，必須以中心節點的 filePath 查找同檔案節點；
        // 若錯誤映射成 all，UI 看似有結果，實際上卻會混入其他檔案的一般鄰居。
        if (normalizedMode == "same-file")
            return await GetSameFileVisualGraphAsync(
                projectId,
                nodeIds,
                limit,
                cancellationToken);

        var selected = new Dictionary<string, GraphVisualNode>(StringComparer.Ordinal);
        var edges = new Dictionary<string, GraphVisualEdge>(StringComparer.Ordinal);
        var frontier = nodeIds.Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        for (var level = 0; level <= depth && frontier.Count > 0 &&
                                  selected.Count < limit; level++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var next = new List<string>();
            foreach (var id in frontier)
            {
                if (level == 0)
                {
                    var center = await ReadNodeByIdAsync(
                        projectId, id, cancellationToken);
                    if (center is not null) selected[id] = center;
                }
                if (level == depth) continue;
                // 方向條件必須在 Neo4j LIMIT 之前套用。若先讀取混合方向 500 筆再於記憶體
                // 篩選，高 degree 節點可能剛好被另一方向填滿，造成 callers/callees 漏資料。
                var neighbors = normalizedMode == "all"
                    ? await GetNeighborsAsync(
                        projectId, id, Math.Min(limit, 500), cancellationToken)
                    : await GetDirectionalNeighborsAsync(
                        projectId,
                        id,
                        Math.Min(limit, 500),
                        normalizedMode,
                        cancellationToken);
                foreach (var neighbor in neighbors)
                {
                    if (selected.Count >= limit &&
                        !selected.ContainsKey(neighbor.Node.Key))
                        break;
                    if (!selected.ContainsKey(neighbor.Node.Key))
                    {
                        selected[neighbor.Node.Key] =
                            MapVisualNode(neighbor.Node, 0);
                        next.Add(neighbor.Node.Key);
                    }
                    edges[neighbor.Relationship.Id] = new GraphVisualEdge(
                        neighbor.Relationship.Id,
                        neighbor.Relationship.SourceKey,
                        neighbor.Relationship.TargetKey,
                        RelationshipType(neighbor.Relationship.Kind),
                        neighbor.Relationship.Properties);
                }
            }
            frontier = next.Distinct(StringComparer.Ordinal).ToList();
        }
        var stats = await GetStatsAsync(projectId, cancellationToken);
        return new GraphVisualData(
            selected.Values.OrderBy(node => node.Id, StringComparer.Ordinal).ToList(),
            edges.Values.OrderBy(edge => edge.Id, StringComparer.Ordinal).ToList(),
            stats.Nodes,
            selected.Count,
            edges.Count,
            selected.Count < stats.Nodes && selected.Count >= limit);
    }

    /// <summary>
    /// 將前端語意化的展開模式轉成後端實際使用的方向。
    /// callers 代表指向中心節點的 incoming edge；callees 則代表中心節點發出的 outgoing edge。
    /// </summary>
    /// <param name="mode">UI 或 API 傳入的展開模式。</param>
    /// <returns>all、in、out 或 same-file。</returns>
    internal static string NormalizeVisualNeighborMode(string mode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        return mode.Trim().ToLowerInvariant() switch
        {
            "all" => "all",
            "in" or "callers" => "in",
            "out" or "callees" => "out",
            "same-file" => "same-file",
            _ => throw new ArgumentException(
                "Graph neighbor mode 只允許 all、in、out、callers、callees、same-file。",
                nameof(mode)),
        };
    }

    /// <inheritdoc />
    public async Task<GraphVisualQueryResult> QueryVisualGraphAsync(
        string projectId,
        string cypher,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(cypher);
        limit = Math.Clamp(limit, 1, 5_000);
        cypher = EnsureReadOnlyCypher(cypher);
        var manifest = await GetActiveManifestAsync(projectId, cancellationToken) ??
            throw new InvalidOperationException("專案尚無 active V4 graph。");
        if (_driver is null)
            return new([], [], new([], [], 0, 0, 0, false));
        await using var session = OpenReadSession();
        cancellationToken.ThrowIfCancellationRequested();
        return await session.ExecuteReadAsync(async transaction =>
        {
            var cursor = await transaction.RunAsync(
                cypher,
                new { projectId, graphVersion = manifest, limit });
            var columns = new List<string>();
            var rows = new List<IReadOnlyDictionary<string, object?>>();
            var nodes = new Dictionary<string, GraphVisualNode>(StringComparer.Ordinal);
            var edges = new Dictionary<string, GraphVisualEdge>(StringComparer.Ordinal);
            var elementNodeIds = new Dictionary<string, string>(StringComparer.Ordinal);
            while (rows.Count < limit && await cursor.FetchAsync())
            {
                if (columns.Count == 0)
                    columns.AddRange(cursor.Current.Keys);
                var row = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var key in columns)
                {
                    var value = cursor.Current[key];
                    CollectGraphValues(value, nodes, edges, elementNodeIds);
                    row[key] = ToSafeTableValue(value);
                }
                rows.Add(row);
            }

            // Cypher 的 LIMIT 限制 row 數，不限制 `collect(node)` 內的元素數量。
            // 因此在補端點前先把已收集 node 壓回服務端 budget，並優先保留能形成 edge 的端點。
            if (nodes.Count > limit)
            {
                var retainedNodeIds = SelectBoundedVisualNodeIds(
                    nodes.Keys,
                    edges.Values.OrderBy(edge => edge.Id, StringComparer.Ordinal),
                    limit).ToHashSet(StringComparer.Ordinal);
                foreach (var nodeId in nodes.Keys
                             .Where(nodeId => !retainedNodeIds.Contains(nodeId))
                             .ToList())
                    nodes.Remove(nodeId);
            }

            // `RETURN relationship` 不會自動把端點 node 放進 Neo4j record。ForceGraph 若收到
            // orphan edge 會產生錯誤或不可見連線，因此在剩餘 node budget 內優先補齊完整端點，
            // 最後再移除仍缺任一端的 edge，保證回傳圖譜永遠符合結構不變量。
            var missingEndpointIds = SelectMissingVisualEndpointIds(
                edges.Values.OrderBy(edge => edge.Id, StringComparer.Ordinal),
                nodes.Keys.ToHashSet(StringComparer.Ordinal),
                Math.Max(0, limit - nodes.Count));
            if (missingEndpointIds.Count > 0)
            {
                var endpointCursor = await transaction.RunAsync(
                    """
                    MATCH (node:GraphEntity {
                        wingmanProjectId: $projectId,
                        graphVersion: $graphVersion
                    })
                    WHERE node.id IN $nodeIds
                    OPTIONAL MATCH (node)-[relationship]-(:GraphEntity {
                        wingmanProjectId: $projectId,
                        graphVersion: $graphVersion
                    })
                    RETURN node, count(relationship) AS degree
                    ORDER BY node.id
                    """,
                    new { projectId, graphVersion = manifest, nodeIds = missingEndpointIds });
                while (await endpointCursor.FetchAsync())
                {
                    var mapped = MapVisualNode(
                        endpointCursor.Current["node"].As<INode>(),
                        endpointCursor.Current["degree"].As<int>());
                    nodes[mapped.Id] = mapped;
                }
            }

            var completeEdges = KeepVisualEdgesWithEndpoints(
                edges.Values,
                nodes.Keys.ToHashSet(StringComparer.Ordinal))
                .OrderBy(edge => edge.Id, StringComparer.Ordinal)
                .Take(Math.Min(limit * 4, 20_000))
                .ToList();
            return new GraphVisualQueryResult(
                columns,
                rows,
                new GraphVisualData(
                    nodes.Values.OrderBy(node => node.Id, StringComparer.Ordinal).ToList(),
                    completeEdges,
                    nodes.Count,
                    nodes.Count,
                    completeEdges.Count,
                    false));
        });
    }
}
