using Neo4j.Driver;

namespace AgentService.Modules.GraphRAG;

/// <summary>
/// Neo4j active graph 的 bounded 鄰接檢索。
/// 關係優先序先保留跨層業務骨架，再保留一般 CALLS，避免高 degree Controller
/// 在固定鄰居預算內把 Route、Data 與排程關係擠掉。
/// </summary>
public sealed partial class Neo4jGraphStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<GraphNeighbor>> GetNeighborsAsync(
        string projectId,
        string nodeId,
        int limit,
        CancellationToken cancellationToken = default) =>
        await GetNeighborsAsync(
            projectId,
            nodeId,
            limit,
            graphVersion: null,
            cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<GraphNeighbor>> GetNeighborsAsync(
        string projectId,
        string nodeId,
        int limit,
        string? graphVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        limit = Math.Clamp(limit, 1, 500);
        if (_driver is null)
        {
            throw new GraphStoreException(
                GraphStoreFailureKind.Unavailable,
                "Neo4j 尚未建立連線，無法讀取 Graph 鄰接關係。");
        }
        await using var session = OpenReadSession();
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await session.ExecuteReadAsync(async transaction =>
            {
            var cursor = await transaction.RunAsync(
                """
                MATCH (p:ProjectGraph {projectId: $projectId})
                MATCH (center:GraphEntity {
                    projectId: $projectId,
                    id: $nodeId
                })-[relationship]-(neighbor:GraphEntity {
                    projectId: $projectId
                })
                WHERE center.graphVersion = coalesce($graphVersion, p.activeManifestVersion)
                  AND neighbor.graphVersion = coalesce($graphVersion, p.activeManifestVersion)
                RETURN neighbor, relationship,
                       startNode(relationship).id AS sourceId,
                       endNode(relationship).id AS targetId,
                       CASE WHEN startNode(relationship) = center
                            THEN 'outgoing' ELSE 'incoming' END AS direction
                ORDER BY
                    CASE type(relationship)
                        WHEN 'OPENS' THEN 0
                        WHEN 'ROUTES_TO' THEN 1
                        WHEN 'IMPLEMENTED_BY' THEN 2
                        WHEN 'RENDERS' THEN 3
                        WHEN 'LOADS_PLUGIN_REPORT' THEN 4
                        WHEN 'OPENS_CUSTOM_REPORT' THEN 5
                        WHEN 'CONTAINS_DATA_SOURCE' THEN 6
                        WHEN 'READS_VIA' THEN 7
                        WHEN 'WRITES_VIA' THEN 8
                        WHEN 'USES_DEFINITION' THEN 9
                        WHEN 'MAPS_TO' THEN 10
                        WHEN 'QUERIES' THEN 11
                        WHEN 'CALLS' THEN 12
                        ELSE 13
                    END,
                    neighbor.id
                LIMIT $limit
                """,
                new { projectId, nodeId, limit, graphVersion });
            var result = new List<GraphNeighbor>();
            while (await cursor.FetchAsync())
            {
                var relationship = cursor.Current["relationship"].As<IRelationship>();
                result.Add(new GraphNeighbor(
                    MapNode(cursor.Current["neighbor"].As<INode>()),
                    MapEdge(
                        relationship,
                        cursor.Current["sourceId"].As<string>(),
                        cursor.Current["targetId"].As<string>()),
                    cursor.Current["direction"].As<string>()));
            }
                return (IReadOnlyList<GraphNeighbor>)result;
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ServiceUnavailableException exception)
        {
            throw new GraphStoreException(
                GraphStoreFailureKind.Unavailable,
                "Neo4j 無法提供 Graph 鄰接關係。",
                exception);
        }
        catch (ClientException exception)
        {
            throw new GraphStoreException(
                GraphStoreFailureKind.QueryFailed,
                "Neo4j Graph 鄰接查詢失敗。",
                exception);
        }
        catch (Exception exception)
        {
            throw new GraphStoreException(
                GraphStoreFailureKind.QueryFailed,
                "Neo4j Graph 鄰接查詢失敗。",
                exception);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<GraphNeighbor>>>
        GetNeighborsBatchAsync(
            string projectId,
            IReadOnlyList<string> nodeIds,
            int limitPerNode,
            CancellationToken cancellationToken = default) =>
        await GetNeighborsBatchAsync(
            projectId,
            nodeIds,
            limitPerNode,
            graphVersion: null,
            cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<GraphNeighbor>>>
        GetNeighborsBatchAsync(
            string projectId,
            IReadOnlyList<string> nodeIds,
            int limitPerNode,
            string? graphVersion,
            CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(nodeIds);
        limitPerNode = Math.Clamp(limitPerNode, 1, 500);
        var distinctIds = nodeIds
            .Where(nodeId => !string.IsNullOrWhiteSpace(nodeId))
            .Distinct(StringComparer.Ordinal)
            .Take(500)
            .ToList();
        var empty = distinctIds.ToDictionary(
            nodeId => nodeId,
            _ => (IReadOnlyList<GraphNeighbor>)Array.Empty<GraphNeighbor>(),
            StringComparer.Ordinal);
        if (_driver is null)
        {
            throw new GraphStoreException(
                GraphStoreFailureKind.Unavailable,
                "Neo4j 尚未建立連線，無法讀取 Graph 鄰接關係。");
        }
        if (distinctIds.Count == 0) return empty;

        await using var session = OpenReadSession();
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await session.ExecuteReadAsync(async transaction =>
            {
                var cursor = await transaction.RunAsync(
                """
                MATCH (p:ProjectGraph {projectId: $projectId})
                UNWIND $nodeIds AS nodeId
                MATCH (center:GraphEntity {
                    projectId: $projectId,
                    id: nodeId
                })-[relationship]-(neighbor:GraphEntity {
                    projectId: $projectId
                })
                WHERE center.graphVersion = coalesce($graphVersion, p.activeManifestVersion)
                  AND neighbor.graphVersion = coalesce($graphVersion, p.activeManifestVersion)
                WITH center, neighbor, relationship,
                     CASE WHEN startNode(relationship) = center
                          THEN 'outgoing' ELSE 'incoming' END AS direction
                ORDER BY
                    center.id,
                    CASE type(relationship)
                        WHEN 'OPENS' THEN 0
                        WHEN 'ROUTES_TO' THEN 1
                        WHEN 'IMPLEMENTED_BY' THEN 2
                        WHEN 'RENDERS' THEN 3
                        WHEN 'LOADS_PLUGIN_REPORT' THEN 4
                        WHEN 'OPENS_CUSTOM_REPORT' THEN 5
                        WHEN 'CONTAINS_DATA_SOURCE' THEN 6
                        WHEN 'READS_VIA' THEN 7
                        WHEN 'WRITES_VIA' THEN 8
                        WHEN 'USES_DEFINITION' THEN 9
                        WHEN 'MAPS_TO' THEN 10
                        WHEN 'QUERIES' THEN 11
                        WHEN 'CALLS' THEN 12
                        ELSE 13
                    END,
                    neighbor.id
                WITH center.id AS centerId,
                     collect({
                         neighbor: neighbor,
                         relationship: relationship,
                         direction: direction
                     })[0..$limitPerNode] AS bounded
                UNWIND bounded AS item
                RETURN centerId,
                       item.neighbor AS neighbor,
                       item.relationship AS relationship,
                       startNode(item.relationship).id AS sourceId,
                       endNode(item.relationship).id AS targetId,
                       item.direction AS direction
                ORDER BY
                    centerId,
                    CASE type(item.relationship)
                        WHEN 'OPENS' THEN 0
                        WHEN 'ROUTES_TO' THEN 1
                        WHEN 'IMPLEMENTED_BY' THEN 2
                        WHEN 'RENDERS' THEN 3
                        WHEN 'LOADS_PLUGIN_REPORT' THEN 4
                        WHEN 'OPENS_CUSTOM_REPORT' THEN 5
                        WHEN 'CONTAINS_DATA_SOURCE' THEN 6
                        WHEN 'READS_VIA' THEN 7
                        WHEN 'WRITES_VIA' THEN 8
                        WHEN 'USES_DEFINITION' THEN 9
                        WHEN 'MAPS_TO' THEN 10
                        WHEN 'QUERIES' THEN 11
                        WHEN 'CALLS' THEN 12
                        ELSE 13
                    END,
                    item.neighbor.id
                """,
                new { projectId, nodeIds = distinctIds, limitPerNode, graphVersion });
            var mutable = distinctIds.ToDictionary(
                nodeId => nodeId,
                _ => new List<GraphNeighbor>(),
                StringComparer.Ordinal);
            while (await cursor.FetchAsync())
            {
                var centerId = cursor.Current["centerId"].As<string>();
                var relationship = cursor.Current["relationship"].As<IRelationship>();
                mutable[centerId].Add(new GraphNeighbor(
                    MapNode(cursor.Current["neighbor"].As<INode>()),
                    MapEdge(
                        relationship,
                        cursor.Current["sourceId"].As<string>(),
                        cursor.Current["targetId"].As<string>()),
                    cursor.Current["direction"].As<string>()));
            }
                return mutable.ToDictionary(
                    pair => pair.Key,
                    pair => (IReadOnlyList<GraphNeighbor>)pair.Value,
                    StringComparer.Ordinal);
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ServiceUnavailableException exception)
        {
            throw new GraphStoreException(
                GraphStoreFailureKind.Unavailable,
                "Neo4j 無法提供 Graph 鄰接關係。",
                exception);
        }
        catch (ClientException exception)
        {
            throw new GraphStoreException(
                GraphStoreFailureKind.QueryFailed,
                "Neo4j Graph 鄰接查詢失敗。",
                exception);
        }
        catch (Exception exception)
        {
            throw new GraphStoreException(
                GraphStoreFailureKind.QueryFailed,
                "Neo4j Graph 鄰接查詢失敗。",
                exception);
        }
    }
}
