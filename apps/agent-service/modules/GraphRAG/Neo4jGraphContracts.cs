using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neo4j.Driver;
using AuthorityGraphDocument = AgentService.Modules.GraphRAG.FblAuthority.GraphDocument;
using AuthorityGraphNode = AgentService.Modules.GraphRAG.FblAuthority.GraphNode;
using AuthorityGraphRelationship = AgentService.Modules.GraphRAG.FblAuthority.GraphRelationship;

namespace AgentService.Modules.GraphRAG;

/// <summary>
/// GraphRAG V4 的 Neo4j 連線與批次設定。
/// Password 僅供 driver 建立連線，禁止序列化或寫入任何 log／snapshot。
/// </summary>
public sealed class GraphRagNeo4jOptions
{
    /// <summary>沿用既有 appsettings 的設定 section。</summary>
    public const string SectionName = "Neo4j";

    /// <summary>Neo4j 的 Bolt 連線位址。</summary>
    public string Uri { get; set; } = "bolt://127.0.0.1:17688";

    /// <summary>Neo4j 使用者名稱。</summary>
    public string Username { get; set; } = "neo4j";

    /// <summary>Neo4j 密碼；不得序列化。</summary>
    [JsonIgnore]
    public string Password { get; set; } = string.Empty;

    /// <summary>Neo4j database 名稱。</summary>
    public string Database { get; set; } = "neo4j";

    /// <summary>連線逾時秒數。</summary>
    public int ConnectionTimeoutSeconds { get; set; } = 3;

    /// <summary>transaction retry 上限秒數。</summary>
    public int TransactionRetrySeconds { get; set; } = 60;

    /// <summary>單次 UNWIND 寫入筆數。</summary>
    public int WriteBatchSize { get; set; } = 2_000;

    /// <summary>Lifecycle 設為 disabled 時不建立 driver。</summary>
    public bool Disabled { get; set; }
}

/// <summary>BM25 搜尋命中的 V4 node。</summary>
/// <param name="Node">完整 domain node。</param>
/// <param name="Score">Neo4j full-text score。</param>
public sealed record GraphSearchHit(AuthorityGraphNode Node, double Score);

/// <summary>Graph 檢索失敗的可辨識類型，避免把基礎設施錯誤誤報成查無結果。</summary>
public enum GraphStoreFailureKind
{
    /// <summary>Neo4j 無法連線或服務未啟動。</summary>
    Unavailable,

    /// <summary>必要的 constraint／full-text index 尚未建立或不可用。</summary>
    SchemaNotReady,

    /// <summary>要求的 immutable graph snapshot 不存在。</summary>
    SnapshotNotFound,

    /// <summary>Neo4j 查詢執行失敗。</summary>
    QueryFailed,
}

/// <summary>攜帶穩定失敗類型的 Graph Store 例外。</summary>
public sealed class GraphStoreException : Exception
{
    /// <summary>建立 Graph Store 例外。</summary>
    public GraphStoreException(
        GraphStoreFailureKind failureKind,
        string message,
        Exception? innerException = null)
        : base(message, innerException) =>
        FailureKind = failureKind;

    /// <summary>可供 API／診斷使用的穩定錯誤類型。</summary>
    public GraphStoreFailureKind FailureKind { get; }
}

/// <summary>一個中心節點的一階關係，供 relation-aware BFS 使用。</summary>
/// <param name="Node">鄰接 node。</param>
/// <param name="Relationship">連接中心與鄰接 node 的 authority relationship。</param>
/// <param name="Direction">outgoing 或 incoming。</param>
public sealed record GraphNeighbor(
    AuthorityGraphNode Node,
    AuthorityGraphRelationship Relationship,
    string Direction);

/// <summary>
/// Neo4j 唯一允許發布的 FBL 權威圖快照。
/// ProjectId 與 GraphVersion 負責隔離 immutable staging；ContentDigest 驗證發布內容未被替換；
/// Document 則直接使用 FBL authority schema，不再經過舊 GraphModel 的四種節點與九種關係轉譯。
/// </summary>
public sealed record FblGraphSnapshot(
    string ProjectId,
    string GraphVersion,
    string ContentDigest,
    AuthorityGraphDocument Document,
    IReadOnlyList<GraphCommunityReportV4> Communities);

/// <summary>知識圖譜瀏覽器使用的 V4 node。</summary>
public sealed record GraphVisualNode(
    string Id,
    string Kind,
    string Role,
    string Name,
    string? FilePath,
    int? StartLine,
    int? EndLine,
    string Language,
    int Degree,
    IReadOnlyDictionary<string, object?> Properties)
{
    /// <summary>
    /// Viewer Contract 使用的 labels 投影。V4 canonical schema 只有一個 kind，
    /// 因此不額外製造與權威圖不同的節點分類。
    /// </summary>
    public IReadOnlyList<string> Labels => [Kind];

    /// <summary>Viewer 預設顯示名稱；保留 Name 作為既有 API 的相容欄位。</summary>
    public string Caption => Name;

    /// <summary>Viewer 的通用分類欄位，直接對應 V4 node kind。</summary>
    public string Category => Kind;

    /// <summary>Viewer 使用的聚合指標；目前只提供 bounded degree。</summary>
    public IReadOnlyDictionary<string, int> Metrics =>
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["degree"] = Degree,
        };
}

/// <summary>知識圖譜瀏覽器使用的 V4 edge。</summary>
public sealed record GraphVisualEdge(
    string Id,
    string Source,
    string Target,
    string Type,
    IReadOnlyDictionary<string, object?> Properties);

/// <summary>瀏覽器可視化子圖與截斷統計。</summary>
public sealed record GraphVisualData(
    IReadOnlyList<GraphVisualNode> Nodes,
    IReadOnlyList<GraphVisualEdge> Edges,
    int TotalNodes,
    int LoadedNodes,
    int LoadedEdges,
    bool HasMore)
{
    /// <summary>Viewer Contract 版本；不影響既有 graph API 欄位。</summary>
    public string ContractVersion => "1.0";

    /// <summary>Viewer Contract 使用的截斷欄位，與既有 HasMore 同義。</summary>
    public bool Truncated => HasMore;
}

/// <summary>schema facet 名稱與數量。</summary>
public sealed record GraphFacet(string Name, int Count);

/// <summary>Viewer 動態 facet 中的一個可選值。</summary>
public sealed record GraphViewerFacetValue(string Token, string Label, int Count);

/// <summary>Viewer 動態 facet 描述；由 active V4 graph 的實際統計產生。</summary>
public sealed record GraphViewerFacetDescriptor(
    string Id,
    string Label,
    string Kind,
    IReadOnlyList<GraphViewerFacetValue> Values,
    bool MultiSelect = true,
    string? Description = null,
    string? Target = null,
    string Selection = "multiple",
    string Match = "any");

/// <summary>Viewer 目前可使用的 bounded 功能。</summary>
public sealed record GraphViewerCapabilities(
    bool Search = true,
    bool Neighbors = true,
    bool Table = true,
    bool RawQuery = true);

/// <summary>Viewer 可選的節點標題欄位。</summary>
public sealed record GraphViewerCaptionOption(string Id, string Label);

/// <summary>Viewer 內建的唯讀查詢範本。</summary>
public sealed record GraphViewerQueryTemplate(
    string Id,
    string Label,
    string Text,
    string Target = "table");

/// <summary>Viewer 搜尋或初始圖譜載入的 facet 篩選。</summary>
public sealed record GraphViewerSearchFilter(
    string FacetId,
    IReadOnlyList<string> Tokens);

/// <summary>Viewer 全域搜尋命中；只回傳 active V4 graph 的節點。</summary>
public sealed record GraphViewerSearchHit(
    GraphVisualNode Node,
    double Score);

/// <summary>Viewer 全域搜尋結果與 bounded 截斷資訊。</summary>
public sealed record GraphViewerSearchResult(
    IReadOnlyList<GraphViewerSearchHit> Hits,
    int Total,
    bool HasMore,
    string ContractVersion = "1.0");

/// <summary>V4 可視化 schema；由 active graph 的實際 node 與 relationship 統計產生。</summary>
public sealed record GraphVisualSchema(
    int TotalNodes,
    int TotalEdges,
    IReadOnlyList<GraphFacet> NodeKinds,
    IReadOnlyList<GraphFacet> RelationshipTypes,
    IReadOnlyList<string> PropertyKeys)
{
    /// <summary>Viewer Contract 版本。</summary>
    public string ContractVersion => "1.0";

    /// <summary>目前 active graph 的 immutable version；舊 store 可為 null。</summary>
    public string? GraphRevision { get; init; }

    /// <summary>Viewer 可直接使用的功能旗標。</summary>
    public GraphViewerCapabilities Capabilities { get; init; } = new();

    /// <summary>可切換的標題欄位；role 不列入，避免混淆 V4 kind。</summary>
    public IReadOnlyList<GraphViewerCaptionOption> CaptionOptions { get; init; } =
    [
        new("caption", "節點名稱"),
        new("category", "節點類型"),
    ];

    /// <summary>
    /// 將既有 nodeKinds／relationshipTypes 投影為通用 facet，
    /// 讓新版 Viewer 不必綁定 Neo4j 或 FBL 內部欄位名稱。
    /// </summary>
    public IReadOnlyList<GraphViewerFacetDescriptor> Facets =>
    [
        new(
            "node-category",
            "節點類型",
            "node",
            NodeKinds.Select(item =>
                    new GraphViewerFacetValue(item.Name, item.Name, item.Count))
                .ToArray(),
            true,
            "依 V4 權威節點 kind 篩選目前 active graph。",
            "node",
            "multiple",
            "any"),
        new(
            "edge-type",
            "關係類型",
            "edge",
            RelationshipTypes.Select(item =>
                    new GraphViewerFacetValue(item.Name, item.Name, item.Count))
                .ToArray(),
            true,
            "依 V4 權威 relationship type 篩選圖譜與搜尋結果。",
            "edge",
            "multiple",
            "any"),
    ];

    /// <summary>Viewer 內建查詢範本；實際版本由 API 注入 graphVersion 參數。</summary>
    public IReadOnlyList<GraphViewerQueryTemplate> QueryTemplates =>
    [
        new(
            "entrypoints",
            "入口與功能鏈",
            "MATCH path=(n:GraphEntity {projectId: $projectId, graphVersion: $graphVersion})-[r*1..4]->(m:GraphEntity {projectId: $projectId, graphVersion: $graphVersion})\n" +
            "RETURN path LIMIT $limit",
            "manual"),
        new(
            "high-degree",
            "高連結節點",
            "MATCH (n:GraphEntity {projectId: $projectId, graphVersion: $graphVersion})\n" +
            "OPTIONAL MATCH (n)-[r]-()\n" +
            "RETURN n, count(r) AS degree ORDER BY degree DESC LIMIT $limit",
            "manual"),
        new(
            "selected-node",
            "選取節點的一階關係",
            "MATCH (n:GraphEntity {projectId: $projectId, graphVersion: $graphVersion, id: '{{nodeId}}'})\n" +
            "OPTIONAL MATCH (n)-[r]-(m:GraphEntity {projectId: $projectId, graphVersion: $graphVersion})\n" +
            "RETURN n, r, m LIMIT $limit",
            "node"),
        new(
            "selected-edge",
            "選取關係的兩端",
            "MATCH (source:GraphEntity {projectId: $projectId, graphVersion: $graphVersion, id: '{{sourceId}}'})\n" +
            "-[relationship]->(target:GraphEntity {projectId: $projectId, graphVersion: $graphVersion, id: '{{targetId}}'})\n" +
            "WHERE type(relationship) = '{{edgeType}}'\n" +
            "RETURN source, relationship, target LIMIT $limit",
            "edge"),
    ];

    /// <summary>提供前端顯示的查詢限制與語意說明。</summary>
    public string QueryHelp =>
        "只允許目前 active V4 graph 的唯讀查詢；查詢結果會套用節點與資料列上限。";
}

/// <summary>受限 read-only Cypher 的表格與可視化結果。</summary>
public sealed record GraphVisualQueryResult(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    GraphVisualData Graph);

/// <summary>
/// Community B1～B7 驗收所需的去敏感聚合資料。
/// 此模型只保存數量、範圍與 deterministic digest，不包含標題、摘要、成員 ID
/// 或原始碼，可安全提供給本機驗收腳本。
/// </summary>
public sealed record GraphCommunityAcceptanceDiagnostics(
    string ProjectId,
    string GraphVersion,
    int C0Count,
    int EligibleAnchorCount,
    int C1Count,
    int C1ResolvedCount,
    int C1UnresolvedCount,
    int? C1ResolvedMinimumMembers,
    int? C1ResolvedMaximumMembers,
    int C1InvalidUnresolvedCount,
    int C1InvalidParentCount,
    int C2Count,
    int? C2MinimumMembers,
    int C2InvalidMemberCount,
    int ConnectedEligibleCount,
    int ConnectedAssignedCount,
    int ConnectedUnresolvedCount,
    string MembershipDigest);

/// <summary>
/// GraphRAG FBL authority 儲存契約。Store 只接受已完成 Preflight 的 authority snapshot，
/// 並以 immutable graphVersion 加 active anchor 達成失敗不污染上一版的原子發布。
/// </summary>
public interface IGraphStore
{
    /// <summary>確認 driver 與目標 database 可連線。</summary>
    Task<bool> PingAsync(CancellationToken cancellationToken = default);

    /// <summary>建立 V4 constraints 與 full-text index。</summary>
    Task EnsureSchemaAsync(CancellationToken cancellationToken = default);

    /// <summary>將完整 snapshot staging、驗證並原子切換為 active。</summary>
    Task PublishAsync(FblGraphSnapshot snapshot, CancellationToken cancellationToken = default);

    /// <summary>
    /// 發布後續步驟失敗時，將 active anchor 恢復到發布前版本並清理候選版本。
    /// 非 Neo4j 測試 store 可使用預設空實作。
    /// </summary>
    Task RollbackPublishedVersionAsync(
        string projectId,
        string publishedVersion,
        string? previousVersion,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>取得專案目前 active manifest；圖譜不一致或尚未發布時回傳 null。</summary>
    Task<string?> GetActiveManifestAsync(
        string projectId,
        CancellationToken cancellationToken = default);

    /// <summary>刪除指定專案的 domain graph、community report 與 anchor。</summary>
    Task DeleteProjectAsync(
        string projectId,
        CancellationToken cancellationToken = default);

    /// <summary>使用 Neo4j full-text BM25 搜尋 active graph。</summary>
    Task<IReadOnlyList<GraphSearchHit>> SearchAsync(
        string projectId,
        string query,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 在指定 immutable graphVersion 搜尋。正式問答必須使用此多載，
    /// 避免同一輪的每個 query variant 重讀 active manifest。
    /// 舊實作以預設轉呼叫保留相容性。
    /// </summary>
    Task<IReadOnlyList<GraphSearchHit>> SearchAsync(
        string projectId,
        string query,
        int limit,
        string? graphVersion,
        CancellationToken cancellationToken = default) =>
        SearchAsync(projectId, query, limit, cancellationToken);

    /// <summary>取得 active graph 的一階鄰接關係；多 hop budget 由 retrieval service 控制。</summary>
    Task<IReadOnlyList<GraphNeighbor>> GetNeighborsAsync(
        string projectId,
        string nodeId,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 在指定 immutable graphVersion 取得鄰接關係；未指定時沿用 active graph。
    /// </summary>
    Task<IReadOnlyList<GraphNeighbor>> GetNeighborsAsync(
        string projectId,
        string nodeId,
        int limit,
        string? graphVersion,
        CancellationToken cancellationToken = default) =>
        GetNeighborsAsync(projectId, nodeId, limit, cancellationToken);

    /// <summary>
    /// 批次取得多個 active graph 節點的一階鄰接關係。
    /// 預設實作保留測試 store 與非 Neo4j store 的相容性；正式 Neo4j store 必須覆寫此方法，
    /// 以單一查詢處理同一層 frontier，避免 Local Search 形成逐節點 N+1 round trip。
    /// </summary>
    /// <param name="projectId">Modern Wingman 專案識別碼。</param>
    /// <param name="nodeIds">同一遍歷層待展開的去重節點識別碼。</param>
    /// <param name="limitPerNode">每個中心節點最多保留的鄰居數。</param>
    /// <param name="cancellationToken">取消整批讀取的 token。</param>
    /// <returns>以中心節點 ID 分組的鄰接關係；沒有鄰居的節點仍會回傳空集合。</returns>
    async Task<IReadOnlyDictionary<string, IReadOnlyList<GraphNeighbor>>>
        GetNeighborsBatchAsync(
            string projectId,
            IReadOnlyList<string> nodeIds,
            int limitPerNode,
            CancellationToken cancellationToken = default)
    {
        var distinctIds = nodeIds
            .Where(nodeId => !string.IsNullOrWhiteSpace(nodeId))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var values = await Task.WhenAll(distinctIds.Select(async nodeId =>
            new
            {
                NodeId = nodeId,
                Neighbors = await GetNeighborsAsync(
                    projectId,
                    nodeId,
                    limitPerNode,
                    cancellationToken),
            }));
        return values.ToDictionary(
            item => item.NodeId,
            item => item.Neighbors,
            StringComparer.Ordinal);
    }

    /// <summary>
    /// 在指定 immutable graphVersion 批次取得鄰接關係；正式問答使用此多載
    /// 以固定整輪 BFS 的 snapshot。舊 store 會退回既有 active 查詢。
    /// </summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<GraphNeighbor>>>
        GetNeighborsBatchAsync(
            string projectId,
            IReadOnlyList<string> nodeIds,
            int limitPerNode,
            string? graphVersion,
            CancellationToken cancellationToken = default) =>
        GetNeighborsBatchAsync(projectId, nodeIds, limitPerNode, cancellationToken);

    /// <summary>依 active graph degree 取得入口與核心節點，供 Repo Map 使用。</summary>
    Task<IReadOnlyList<GraphSearchHit>> GetCentralNodesAsync(
        string projectId,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>取得 active graph 的 node／edge 數量。</summary>
    Task<(int Nodes, int Edges)> GetStatsAsync(
        string projectId,
        CancellationToken cancellationToken = default);

    /// <summary>寫入指定 immutable version 的 deterministic C0/C1/C2 templates。</summary>
    Task SaveCommunityTemplatesAsync(
        string projectId,
        string graphVersion,
        IReadOnlyList<GraphCommunityReportV4> templates,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>列出 active graph 的 Community templates。</summary>
    Task<IReadOnlyList<GraphCommunityReportV4>> ListCommunityTemplatesAsync(
        string projectId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<GraphCommunityReportV4>>([]);

    /// <summary>
    /// 取得 active graph 的 Community 驗收聚合。測試或非 Neo4j Store 可回傳 null，
    /// 但正式 Neo4j Store 必須覆寫，禁止由呼叫端下載全部成員後自行猜測。
    /// </summary>
    Task<GraphCommunityAcceptanceDiagnostics?> GetCommunityAcceptanceDiagnosticsAsync(
        string projectId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<GraphCommunityAcceptanceDiagnostics?>(null);

    /// <summary>以單一 server query 批次取得 Local Search 已選定的 Community IDs。</summary>
    Task<IReadOnlyList<GraphCommunityReportV4>> GetCommunityTemplatesByIdsAsync(
        string projectId,
        IReadOnlyList<string> communityIds,
        int limit = 20,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<GraphCommunityReportV4>>([]);

    /// <summary>在 Neo4j server 端搜尋 C0，最多回傳兩筆。</summary>
    Task<IReadOnlyList<GraphCommunityReportV4>> SearchC0CommunityTemplatesAsync(
        string projectId,
        string query,
        int limit = 2,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<GraphCommunityReportV4>>([]);

    /// <summary>在指定 C0 parent 下由 Neo4j server 端搜尋 C1，最多回傳十二筆。</summary>
    Task<IReadOnlyList<GraphCommunityReportV4>> SearchC1CommunityTemplatesAsync(
        string projectId,
        string parentCommunityId,
        string query,
        int limit = 12,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<GraphCommunityReportV4>>([]);

    /// <summary>在 Neo4j server 端搜尋 C2，最多回傳五筆。</summary>
    Task<IReadOnlyList<GraphCommunityReportV4>> SearchC2CommunityTemplatesAsync(
        string projectId,
        string query,
        int limit = 5,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<GraphCommunityReportV4>>([]);

    /// <summary>
    /// 以 graphVersion + cacheKey compare-and-set 更新 AI 摘要，
    /// 防止舊 Queue job 覆寫已發布的新版本 template。
    /// </summary>
    Task<bool> TryUpdateCommunitySummaryAsync(
        string projectId,
        string graphVersion,
        string communityId,
        string expectedCacheKey,
        string summary,
        string summaryState,
        int retryCount,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    /// <summary>
    /// GDS 可用時以加權 Leiden 偵測 secondary community member groups；
    /// 未安裝 GDS 時回傳 null，呼叫端必須使用 deterministic fallback。
    /// 預設實作讓測試／非 Neo4j store 明確表示不提供 GDS，而不必模擬外掛。
    /// </summary>
    Task<IReadOnlyList<IReadOnlyList<string>>?> TryDetectLeidenCommunitiesAsync(
        string projectId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<IReadOnlyList<string>>?>(null);

    /// <summary>取得 active graph 的可視化初始子圖。</summary>
    Task<GraphVisualData> GetVisualGraphAsync(
        string projectId,
        int limit,
        IReadOnlyList<string>? kinds,
        IReadOnlyList<string>? relationshipTypes,
        CancellationToken cancellationToken = default);

    /// <summary>取得 active V4 schema facet。</summary>
    Task<GraphVisualSchema> GetVisualSchemaAsync(
        string projectId,
        CancellationToken cancellationToken = default);

    /// <summary>從指定 node IDs 展開 active graph 鄰域。</summary>
    Task<GraphVisualData> GetVisualNeighborsAsync(
        string projectId,
        IReadOnlyList<string> nodeIds,
        int depth,
        int limit,
        string mode,
        CancellationToken cancellationToken = default);

    /// <summary>執行強制 project/version scoped 的 read-only V4 Cypher。</summary>
    Task<GraphVisualQueryResult> QueryVisualGraphAsync(
        string projectId,
        string cypher,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Viewer Contract 的 bounded 初始圖。預設投影到既有 V4 graph API，
    /// 讓測試 store 與舊呼叫端不必同步改寫。
    /// </summary>
    Task<GraphVisualData> GetViewerGraphAsync(
        string projectId,
        int limit,
        IReadOnlyList<GraphViewerSearchFilter>? filters,
        CancellationToken cancellationToken = default) =>
        GetVisualGraphAsync(
            projectId,
            limit,
            filters?.Where(filter =>
                    filter.FacetId.Equals("node-category", StringComparison.OrdinalIgnoreCase))
                .SelectMany(filter => filter.Tokens)
                .ToArray(),
            filters?.Where(filter =>
                    filter.FacetId.Equals("edge-type", StringComparison.OrdinalIgnoreCase))
                .SelectMany(filter => filter.Tokens)
                .ToArray(),
            cancellationToken);

    /// <summary>Viewer Contract 的全域節點搜尋；舊 store 預設回傳空結果。</summary>
    Task<GraphViewerSearchResult> SearchVisualGraphAsync(
        string projectId,
        string query,
        int take,
        IReadOnlyList<GraphViewerSearchFilter>? filters,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new GraphViewerSearchResult(
            Array.Empty<GraphViewerSearchHit>(),
            0,
            false));
}
