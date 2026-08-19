using Neo4j.Driver;
using AgentService.Application.Models;

namespace AgentService.Modules.GraphRAG;

/// <summary>
/// 集中保存專案圖譜的 Neo4j envelope 與 Community schema 宣告。
/// 與查詢、發布流程分檔，避免核心持久化類別超過維護行數上限。
/// </summary>
public sealed partial class Neo4jGraphStore
{
    /// <inheritdoc />
    public async Task<GraphStorageAcceptanceDiagnostics>
        GetStorageAcceptanceDiagnosticsAsync(
            string projectId,
            CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        if (_driver is null)
            throw new InvalidOperationException("Neo4j V4 已停用。");

        var versions = await _manifests.ListSuccessfulAsync(projectId, cancellationToken);
        var activeVersion = (await _manifests.GetCurrentAsync(projectId, cancellationToken))?.Version;
        var previousVersion = versions.FirstOrDefault(item => item.Version != activeVersion)?.Version;
        if (activeVersion is null)
            return new GraphStorageAcceptanceDiagnostics(
                projectId, null, null, 0, 0, 0, 0, 0, 0);

        await using var session = OpenReadSession();
        cancellationToken.ThrowIfCancellationRequested();
        return await session.ExecuteReadAsync(async transaction =>
        {
            var nodeCursor = await transaction.RunAsync(
                """
                MATCH (n:GraphEntity {wingmanProjectId: $projectId})
                RETURN count(DISTINCT n.graphVersion) AS versionCount,
                       sum(CASE WHEN n.graphVersion = $activeVersion
                           THEN 1 ELSE 0 END) AS activeNodes,
                       sum(CASE WHEN n.graphVersion <> $activeVersion
                           THEN 1 ELSE 0 END) AS inactiveNodes
                """,
                new { projectId, activeVersion });
            var nodes = await nodeCursor.SingleAsync();

            var edgeCursor = await transaction.RunAsync(
                """
                MATCH (source:GraphEntity {wingmanProjectId: $projectId})-[r]->
                      (target:GraphEntity {wingmanProjectId: $projectId})
                RETURN sum(CASE
                           WHEN source.graphVersion = $activeVersion
                            AND target.graphVersion = $activeVersion
                           THEN 1 ELSE 0 END) AS activeEdges,
                       sum(CASE
                           WHEN source.graphVersion <> $activeVersion
                             OR target.graphVersion <> $activeVersion
                           THEN 1 ELSE 0 END) AS inactiveEdges
                """,
                new { projectId, activeVersion });
            var edges = await edgeCursor.SingleAsync();

            var communityCursor = await transaction.RunAsync(
                """
                MATCH (c:GraphCommunity {wingmanProjectId: $projectId})
                RETURN sum(CASE WHEN c.graphVersion <> $activeVersion
                           THEN 1 ELSE 0 END) AS inactiveCommunities
                """,
                new { projectId, activeVersion });
            var communities = await communityCursor.SingleAsync();

            return new GraphStorageAcceptanceDiagnostics(
                projectId,
                activeVersion,
                previousVersion,
                nodes["versionCount"].As<int>(),
                nodes["activeNodes"].As<int>(),
                edges["activeEdges"].As<int>(),
                nodes["inactiveNodes"].As<int>(),
                edges["inactiveEdges"].As<int>(),
                communities["inactiveCommunities"].As<int>());
        });
    }

    /// <summary>
    /// V4 啟動時必須存在的約束與索引。
    /// 所有名稱皆為固定契約，查詢端不得依賴 Neo4j 自動產生的名稱。
    /// </summary>
    private static readonly string[] SchemaStatements =
    [
        """
        CREATE CONSTRAINT graph_entity_identity_v5 IF NOT EXISTS
        FOR (n:GraphEntity)
        REQUIRE (n.wingmanProjectId, n.graphVersion, n.id) IS UNIQUE
        """,
        """
        CREATE CONSTRAINT graph_community_identity_v4 IF NOT EXISTS
        FOR (c:GraphCommunity)
        REQUIRE (c.wingmanProjectId, c.graphVersion, c.communityId) IS UNIQUE
        """,
        """
        CREATE INDEX graph_community_version_id_v4 IF NOT EXISTS
        FOR (c:GraphCommunity)
        ON (c.graphVersion, c.communityId)
        """,
        """
        CREATE INDEX graph_community_parent_v4 IF NOT EXISTS
        FOR (c:GraphCommunity)
        ON (c.graphVersion, c.tier, c.parentCommunityId)
        """,
        """
        CREATE FULLTEXT INDEX graph_entity_search_v4 IF NOT EXISTS
        FOR (n:GraphEntity)
        ON EACH [n.name, n.fullName, n.path, n.relativePath, n.route, n.signature,
                 n.qualifiedName, n.objectName, n.displayName, n.description]
        """,
        """
        CREATE FULLTEXT INDEX graph_community_v4_search IF NOT EXISTS
        FOR (c:GraphCommunity)
        ON EACH [c.title, c.summary]
        """,
    ];

    /// <summary>
    /// V4 是破壞式乾淨升級；移除會阻止同 schema V4 named index 建立的舊名稱。
    /// GraphEntity 的 scope 欄位遷移只搬移 Modern envelope，不刪除圖資料。
    /// </summary>
    private static readonly string[] LegacySchemaCleanupStatements =
    [
        "DROP CONSTRAINT graph_entity_identity_v4 IF EXISTS",
        "DROP CONSTRAINT entity_key IF EXISTS",
        "DROP CONSTRAINT project_graph_identity_v4 IF EXISTS",
        "DROP INDEX graphEntitySearchV3 IF EXISTS",
        "DROP CONSTRAINT graph_entity_identity_v3 IF EXISTS",
        "DROP CONSTRAINT project_graph_identity_v3 IF EXISTS",
        "DROP CONSTRAINT graph_community_identity_v3 IF EXISTS",
    ];
}
