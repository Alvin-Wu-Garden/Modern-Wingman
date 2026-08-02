using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neo4j.Driver;

namespace AgentService.Modules.GraphRAG;

/// <summary>
/// GraphRAG V3 的 Neo4j 連線與批次設定。
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

/// <summary>BM25 搜尋命中的 V3 node。</summary>
/// <param name="Node">完整 domain node。</param>
/// <param name="Score">Neo4j full-text score。</param>
public sealed record GraphSearchHitV3(GraphNode Node, double Score);

/// <summary>一個中心節點的一階關係，供 relation-aware BFS 使用。</summary>
/// <param name="Node">鄰接 node。</param>
/// <param name="Edge">連接中心與鄰接 node 的 domain edge。</param>
/// <param name="Direction">outgoing 或 incoming。</param>
public sealed record GraphNeighborV3(
    GraphNode Node,
    GraphEdge Edge,
    string Direction);

/// <summary>GraphRAG primary 或 secondary community 的可檢索摘要。</summary>
/// <param name="CommunityId">穩定社群 ID。</param>
/// <param name="Kind">primary 或 secondary。</param>
/// <param name="Title">業務標題。</param>
/// <param name="Summary">有證據的繁體中文摘要。</param>
/// <param name="MemberIds">有界成員 node ID。</param>
/// <param name="CacheKey">member evidence 與 summary prompt version 的穩定快取鍵。</param>
/// <param name="AiEnriched">Summary 是否已由 LLM 在 canonical graph 外部改寫。</param>
public sealed record GraphCommunityReport(
    string CommunityId,
    string Kind,
    string Title,
    string Summary,
    IReadOnlyList<string> MemberIds,
    string? CacheKey = null,
    bool AiEnriched = false);

/// <summary>知識圖譜瀏覽器使用的 V3 node。</summary>
public sealed record GraphVisualNodeV3(
    string Id,
    [property: JsonIgnore] string Kind,
    [property: JsonIgnore] string Role,
    [property: JsonIgnore] string Name,
    [property: JsonIgnore] string? FilePath,
    [property: JsonIgnore] int? StartLine,
    [property: JsonIgnore] int? EndLine,
    [property: JsonIgnore] string Language,
    [property: JsonIgnore] int Degree,
    IReadOnlyDictionary<string, object?> Properties)
{
    // V3 欄位只供 adapter 內部投影；對外只序列化穩定的 Viewer Contract。
    public IReadOnlyList<string> Labels => [Kind];
    public string Caption => Name;
    public string Category => Kind;
    public IReadOnlyDictionary<string, int> Metrics =>
        new Dictionary<string, int> { ["degree"] = Degree };
}

/// <summary>知識圖譜瀏覽器使用的 V3 edge。</summary>
public sealed record GraphVisualEdgeV3(
    string Id,
    string Source,
    string Target,
    string Type,
    IReadOnlyDictionary<string, object?> Properties);

/// <summary>瀏覽器可視化子圖與截斷統計。</summary>
public sealed record GraphVisualDataV3(
    IReadOnlyList<GraphVisualNodeV3> Nodes,
    IReadOnlyList<GraphVisualEdgeV3> Edges,
    int TotalNodes,
    int LoadedNodes,
    int LoadedEdges,
    [property: JsonIgnore] bool HasMore)
{
    public string ContractVersion => "1.0";
    public bool Truncated => HasMore;
}

/// <summary>schema facet 名稱與數量。</summary>
public sealed record GraphFacetV3(string Name, int Count);

/// <summary>Viewer descriptor 中不帶實體 schema 語意的 facet value。</summary>
public sealed record GraphViewerFacetValue(string Token, string Label, int Count);

/// <summary>View 依此動態產生 filter，不直接認識 V3 kind／role／relationship。</summary>
public sealed record GraphViewerFacetDescriptor(
    string Id,
    string Label,
    string Description,
    string Target,
    string Selection,
    string Match,
    IReadOnlyList<GraphViewerFacetValue> Values);

public sealed record GraphViewerCapabilities(
    bool Search,
    bool Neighbors,
    bool Table,
    bool RawQuery);

public sealed record GraphViewerCaptionOption(string Id, string Label);

public sealed record GraphViewerQueryTemplate(
    string Id,
    string Label,
    string Text,
    string Target = "manual");

/// <summary>V3 可視化 schema；NodeKind 固定四種、relationship 固定九種。</summary>
public sealed record GraphVisualSchemaV3(
    int TotalNodes,
    int TotalEdges,
    [property: JsonIgnore] IReadOnlyList<GraphFacetV3> NodeKinds,
    [property: JsonIgnore] IReadOnlyList<GraphFacetV3> NodeRoles,
    [property: JsonIgnore] IReadOnlyList<GraphFacetV3> RelationshipTypes)
{
    public string ContractVersion => "1.0";
    public string? GraphRevision { get; init; }
    public GraphViewerCapabilities Capabilities => new(true, true, true, true);
    public IReadOnlyList<GraphViewerCaptionOption> CaptionOptions =>
    [
        new("caption", "節點名稱"),
        new("property:role", "功能角色"),
        new("category", "節點類型"),
    ];
    public IReadOnlyList<GraphViewerFacetDescriptor> Facets =>
    [
        new("node-category", "節點類型", "節點代表哪一類知識實體；選取後只顯示該類節點。", "node", "multiple", "any",
            NodeKinds.Select(value => new GraphViewerFacetValue(
                value.Name, value.Name, value.Count)).ToList()),
        new("node-role", "功能角色", "節點在系統中的用途或責任；同一節點類型可包含不同角色。", "node", "multiple", "any",
            NodeRoles.Select(value => new GraphViewerFacetValue(
                value.Name, value.Name, value.Count)).ToList()),
        new("edge-type", "關係類型", "節點之間的有向連線語意；選取後只顯示參與該關係的節點與連線。", "edge", "multiple", "any",
            RelationshipTypes.Select(value => new GraphViewerFacetValue(
                value.Name, value.Name, value.Count)).ToList()),
    ];
    public IReadOnlyList<GraphViewerQueryTemplate> QueryTemplates =>
    [
        new(
            "overview",
            "目前圖譜概覽",
            "MATCH (n:GraphEntity {projectId: $projectId, graphVersion: $graphVersion})\n" +
            "OPTIONAL MATCH (n:GraphEntity {projectId: $projectId, graphVersion: $graphVersion})-[r]->" +
            "(m:GraphEntity {projectId: $projectId, graphVersion: $graphVersion})\n" +
            "RETURN n, r, m"),
        new(
            "selected-node",
            "查詢此節點",
            "MATCH (n:GraphEntity {projectId: $projectId, graphVersion: $graphVersion})\n" +
            "WHERE n.id = '{{nodeId}}'\n" +
            "OPTIONAL MATCH (n:GraphEntity {projectId: $projectId, graphVersion: $graphVersion})-[r]-" +
            "(m:GraphEntity {projectId: $projectId, graphVersion: $graphVersion})\n" +
            "RETURN n, r, m",
            "node"),
        new(
            "selected-edge",
            "查詢此關係",
            "MATCH (source:GraphEntity {projectId: $projectId, graphVersion: $graphVersion})-[r]->" +
            "(target:GraphEntity {projectId: $projectId, graphVersion: $graphVersion})\n" +
            "WHERE source.id = '{{sourceId}}' AND target.id = '{{targetId}}' " +
            "AND type(r) = '{{edgeType}}'\n" +
            "RETURN source, r, target",
            "edge"),
    ];
    public string QueryHelp =>
        "Enter 執行；Shift+Enter 換行。查詢範本已包含目前專案與圖譜版本的限制條件；系統會自動套用結果筆數上限。";
}

public sealed record GraphViewerSearchFilter(
    string FacetId,
    IReadOnlyList<string> Tokens);

public sealed record GraphViewerSearchHit(GraphVisualNodeV3 Node, double Score);

public sealed record GraphViewerSearchResult(
    IReadOnlyList<GraphViewerSearchHit> Items,
    int Take,
    bool HasMore);

/// <summary>受限 read-only Cypher 的表格與可視化結果。</summary>
public sealed record GraphVisualQueryResultV3(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    GraphVisualDataV3 Graph);

/// <summary>
/// GraphRAG V3 儲存契約。Store 只接受已通過 <see cref="GraphAssembler.ValidateSnapshot"/> 的 snapshot，
/// 並以 immutable graphVersion 加 active anchor 達成失敗不污染上一版的原子發布。
/// </summary>
public interface IGraphStore
{
    /// <summary>確認 driver 與目標 database 可連線。</summary>
    Task<bool> PingAsync(CancellationToken cancellationToken = default);

    /// <summary>建立 V3 constraints 與 full-text index。</summary>
    Task EnsureSchemaAsync(CancellationToken cancellationToken = default);

    /// <summary>將完整 snapshot staging、驗證並原子切換為 active。</summary>
    Task PublishAsync(GraphSnapshot snapshot, CancellationToken cancellationToken = default);

    /// <summary>取得專案目前 active manifest；圖譜不一致或尚未發布時回傳 null。</summary>
    Task<string?> GetActiveManifestAsync(
        string projectId,
        CancellationToken cancellationToken = default);

    /// <summary>刪除指定專案的 domain graph、community report 與 anchor。</summary>
    Task DeleteProjectAsync(
        string projectId,
        CancellationToken cancellationToken = default);

    /// <summary>使用 Neo4j full-text BM25 搜尋 active graph。</summary>
    Task<IReadOnlyList<GraphSearchHitV3>> SearchAsync(
        string projectId,
        string query,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>取得 active graph 的一階鄰接關係；多 hop budget 由 retrieval service 控制。</summary>
    Task<IReadOnlyList<GraphNeighborV3>> GetNeighborsAsync(
        string projectId,
        string nodeId,
        int limit,
        CancellationToken cancellationToken = default);

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
    async Task<IReadOnlyDictionary<string, IReadOnlyList<GraphNeighborV3>>>
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

    /// <summary>依 active graph degree 取得入口與核心節點，供 Repo Map 使用。</summary>
    Task<IReadOnlyList<GraphSearchHitV3>> GetCentralNodesAsync(
        string projectId,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>取得 active graph 的 node／edge 數量。</summary>
    Task<(int Nodes, int Edges)> GetStatsAsync(
        string projectId,
        CancellationToken cancellationToken = default);

    /// <summary>覆寫特定 active manifest 的 community reports。</summary>
    Task SaveCommunityReportsAsync(
        string projectId,
        string manifestVersion,
        IReadOnlyList<GraphCommunityReport> reports,
        CancellationToken cancellationToken = default);

    /// <summary>列出 active manifest 的 community reports。</summary>
    Task<IReadOnlyList<GraphCommunityReport>> ListCommunityReportsAsync(
        string projectId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// GDS 可用時以加權 Leiden 偵測 secondary community member groups；
    /// 未安裝 GDS 時回傳 null，呼叫端必須使用 deterministic fallback。
    /// 預設實作讓測試／非 Neo4j store 明確表示不提供 GDS，而不必模擬外掛。
    /// </summary>
    Task<IReadOnlyList<IReadOnlyList<string>>?> TryDetectLeidenCommunitiesAsync(
        string projectId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<IReadOnlyList<string>>?>(null);

    /// <summary>以 descriptor facet tokens 讀取可視化子圖。</summary>
    Task<GraphVisualDataV3> GetViewerGraphAsync(
        string projectId,
        int limit,
        IReadOnlyList<GraphViewerSearchFilter>? filters,
        CancellationToken cancellationToken = default);

    /// <summary>取得 active V3 schema facet。</summary>
    Task<GraphVisualSchemaV3> GetVisualSchemaAsync(
        string projectId,
        CancellationToken cancellationToken = default);

    /// <summary>跨越目前畫布取樣範圍搜尋 active graph。</summary>
    Task<GraphViewerSearchResult> SearchVisualGraphAsync(
        string projectId,
        string query,
        IReadOnlyList<GraphViewerSearchFilter>? filters,
        int take,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new GraphViewerSearchResult([], Math.Clamp(take, 1, 100), false));

    /// <summary>從指定 node IDs 展開 active graph 鄰域。</summary>
    Task<GraphVisualDataV3> GetVisualNeighborsAsync(
        string projectId,
        IReadOnlyList<string> nodeIds,
        int depth,
        int limit,
        string mode,
        CancellationToken cancellationToken = default);

    /// <summary>執行強制 project/version scoped 的 read-only V3 Cypher。</summary>
    Task<GraphVisualQueryResultV3> QueryVisualGraphAsync(
        string projectId,
        string cypher,
        int limit,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Neo4j V3 實作。Domain node 只使用 GraphEntity label，kind 與 role 都存為 property，
/// relationship type 只可能由九種 <see cref="GraphEdgeKind"/> 白名單產生。
/// </summary>
public sealed class Neo4jGraphStore : IGraphStore, IAsyncDisposable
{
    private const int CleanupBatchSize = 5_000;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private static readonly Regex UnsafeCypher = new(
        @"\b(CREATE|INSERT|MERGE|SET|DELETE|DETACH|REMOVE|DROP|ALTER|GRANT|DENY|REVOKE|LOAD\s+CSV|IMPORT|EXPORT|FOREACH|UNION|USE|SHOW|TERMINATE)\b|CALL\s+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex MatchClause = new(
        @"\b(?:OPTIONAL\s+)?MATCH\b(?<pattern>.*?)(?=\bWHERE\b|\bWITH\b|\bRETURN\b|\bOPTIONAL\s+MATCH\b|\bMATCH\b|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant |
        RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex NodePattern = new(
        @"\((?<node>[^()]*)\)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex BoundedLimit = new(
        @"\bLIMIT\s+\$limit\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant |
        RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex TerminalLimit = new(
        @"\bLIMIT\s+(?:\d+|\$[A-Za-z_][A-Za-z0-9_]*)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant |
        RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex ScopedProjectProperty = new(
        @"\bprojectId\s*:\s*\$projectId\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ScopedVersionProperty = new(
        @"\bgraphVersion\s*:\s*\$graphVersion\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex UnboundedAggregate = new(
        @"\bcollect\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly GraphRagNeo4jOptions _options;
    private readonly ILogger<Neo4jGraphStore> _logger;
    private readonly IDriver? _driver;
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _projectGates =
        new(StringComparer.Ordinal);
    private bool _schemaReady;

    /// <summary>
    /// 建立 Neo4j driver；Disabled 時保持 null，使 app 在未啟用 GraphRAG 時仍可啟動。
    /// </summary>
    /// <param name="options">Neo4j 連線設定。</param>
    /// <param name="runtimeOptions">用來判斷 lifecycle 是否明確停用。</param>
    /// <param name="logger">結構化 logger；任何訊息都不得包含 Password。</param>
    public Neo4jGraphStore(
        IOptions<GraphRagNeo4jOptions> options,
        IOptions<GraphRagNeo4jRuntimeOptions> runtimeOptions,
        ILogger<Neo4jGraphStore> logger)
    {
        _options = options.Value;
        _logger = logger;
        ValidateOptions(_options);
        if (_options.Disabled ||
            runtimeOptions.Value.Mode.Equals("disabled", StringComparison.OrdinalIgnoreCase))
            return;
        _driver = GraphDatabase.Driver(
            _options.Uri,
            AuthTokens.Basic(_options.Username, _options.Password),
            builder => builder
                .WithConnectionTimeout(TimeSpan.FromSeconds(_options.ConnectionTimeoutSeconds))
                .WithMaxTransactionRetryTime(TimeSpan.FromSeconds(_options.TransactionRetrySeconds)));
    }

    /// <inheritdoc />
    public async Task<bool> PingAsync(CancellationToken cancellationToken = default)
    {
        if (_driver is null) return false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _driver.VerifyConnectivityAsync();
            cancellationToken.ThrowIfCancellationRequested();
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogDebug(
                "Neo4j V3 connectivity check 失敗；已遮蔽連線資訊。ExceptionType={ExceptionType}",
                exception.GetType().Name);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        if (_schemaReady) return;
        await _schemaGate.WaitAsync(cancellationToken);
        try
        {
            if (_schemaReady) return;
            await using var session = OpenWriteSession();
            foreach (var statement in SchemaStatements)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await session.ExecuteWriteAsync(async transaction =>
                {
                    var cursor = await transaction.RunAsync(statement);
                    await cursor.ConsumeAsync();
                });
            }
            _schemaReady = true;
        }
        finally
        {
            _schemaGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task PublishAsync(
        GraphSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        GraphAssembler.ValidateSnapshot(snapshot);
        await EnsureSchemaAsync(cancellationToken);
        var gate = _projectGates.GetOrAdd(snapshot.ProjectId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            await DeleteVersionAsync(
                snapshot.ProjectId, snapshot.ManifestVersion, cancellationToken);
            try
            {
                await WriteNodesAsync(snapshot, cancellationToken);
                await WriteEdgesAsync(snapshot, cancellationToken);
                await ValidateStagingAsync(snapshot, cancellationToken);
                await PromoteAsync(snapshot, cancellationToken);
                await CleanupRetiredVersionsAsync(snapshot.ProjectId, cancellationToken);
            }
            catch
            {
                await DeleteVersionAsync(
                    snapshot.ProjectId, snapshot.ManifestVersion, CancellationToken.None);
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetActiveManifestAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        if (_driver is null) return null;
        await using var session = OpenReadSession();
        cancellationToken.ThrowIfCancellationRequested();
        return await session.ExecuteReadAsync(async transaction =>
        {
            var cursor = await transaction.RunAsync(
                """
                MATCH (p:ProjectGraph {projectId: $projectId})
                OPTIONAL MATCH (n:GraphEntity {
                    projectId: $projectId,
                    graphVersion: p.activeManifestVersion
                })
                WITH p, count(n) AS nodeCount
                OPTIONAL MATCH (:GraphEntity {
                    projectId: $projectId,
                    graphVersion: p.activeManifestVersion
                })-[r {
                    graphVersion: p.activeManifestVersion
                }]->(:GraphEntity {
                    projectId: $projectId,
                    graphVersion: p.activeManifestVersion
                })
                WITH p, nodeCount, count(r) AS edgeCount
                RETURN CASE
                    WHEN p.activeManifestVersion IS NULL THEN null
                    WHEN nodeCount <> p.nodeCount THEN null
                    WHEN edgeCount <> p.edgeCount THEN null
                    ELSE p.activeManifestVersion
                END AS manifest
                """,
                new { projectId });
            // 尚未建立 ProjectGraph 是首次索引的正常狀態；不能用 SingleAsync，
            // 否則空結果會被誤判成資料庫故障，讓任何新專案都無法發布第一版。
            if (!await cursor.FetchAsync()) return null;
            return cursor.Current["manifest"].As<string?>();
        });
    }

    /// <inheritdoc />
    public async Task DeleteProjectAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        if (_driver is null) return;
        await using var session = OpenWriteSession();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var deleted = await session.ExecuteWriteAsync(async transaction =>
            {
                var cursor = await transaction.RunAsync(
                    $$"""
                    MATCH (n:GraphEntity {projectId: $projectId})
                    WITH n LIMIT {{CleanupBatchSize}}
                    DETACH DELETE n
                    RETURN count(*) AS deleted
                    """,
                    new { projectId });
                return (await cursor.SingleAsync())["deleted"].As<int>();
            });
            if (deleted == 0) break;
        }
        await session.ExecuteWriteAsync(async transaction =>
        {
            var cursor = await transaction.RunAsync(
                """
                MATCH (n)
                WHERE (n:ProjectGraph OR n:CommunityReport OR n:GraphCommunity)
                  AND n.projectId = $projectId
                DETACH DELETE n
                """,
                new { projectId });
            await cursor.ConsumeAsync();
        });
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GraphSearchHitV3>> SearchAsync(
        string projectId,
        string query,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        limit = Math.Clamp(limit, 1, 100);
        if (_driver is null) return [];
        await using var session = OpenReadSession();
        cancellationToken.ThrowIfCancellationRequested();
        return await session.ExecuteReadAsync(async transaction =>
        {
            var cursor = await transaction.RunAsync(
                """
                MATCH (p:ProjectGraph {projectId: $projectId})
                CALL db.index.fulltext.queryNodes(
                    'graphEntitySearchV3',
                    $query,
                    {limit: $candidateLimit})
                YIELD node, score
                WHERE node.projectId = $projectId
                  AND node.graphVersion = p.activeManifestVersion
                RETURN node, score
                ORDER BY score DESC, node.id
                LIMIT $candidateLimit
                """,
                new
                {
                    projectId,
                    query,
                    candidateLimit = Math.Min(limit * 20, 2_000),
                });
            var candidates = new List<GraphSearchHitV3>();
            while (await cursor.FetchAsync())
                candidates.Add(new GraphSearchHitV3(
                    MapNode(cursor.Current["node"].As<INode>()),
                    cursor.Current["score"].As<double>()));
            return DiversifySearchHits(candidates, limit);
        });
    }

    /// <summary>
    /// Full-text 高分候選常被大量 Menu 或 Code 型別壟斷。以 NodeKind 做 deterministic
    /// round-robin，讓 Feature／EntryPoint／Code／Data 都有機會成為 seed；
    /// 最終數量仍嚴格受 limit 限制，不會放大檢索圖。
    /// </summary>
    internal static IReadOnlyList<GraphSearchHitV3> DiversifySearchHits(
        IReadOnlyList<GraphSearchHitV3> candidates,
        int limit)
    {
        limit = Math.Clamp(limit, 1, 100);
        var groups = candidates
            .GroupBy(hit => hit.Node.Kind)
            .Select(group => group
                .OrderByDescending(hit => hit.Score)
                .ThenBy(hit => hit.Node.Id, StringComparer.Ordinal)
                .ToList())
            .OrderByDescending(group => group[0].Score)
            .ThenBy(group => group[0].Node.Kind)
            .ToList();
        var selected = new List<GraphSearchHitV3>(Math.Min(limit, candidates.Count));
        for (var rank = 0;
             selected.Count < limit && groups.Any(group => rank < group.Count);
             rank++)
        {
            foreach (var group in groups)
            {
                if (rank < group.Count) selected.Add(group[rank]);
                if (selected.Count >= limit) break;
            }
        }
        return selected;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GraphNeighborV3>> GetNeighborsAsync(
        string projectId,
        string nodeId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        limit = Math.Clamp(limit, 1, 500);
        if (_driver is null) return [];
        await using var session = OpenReadSession();
        cancellationToken.ThrowIfCancellationRequested();
        return await session.ExecuteReadAsync(async transaction =>
        {
            var cursor = await transaction.RunAsync(
                """
                MATCH (p:ProjectGraph {projectId: $projectId})
                MATCH (center:GraphEntity {
                    projectId: $projectId,
                    graphVersion: p.activeManifestVersion,
                    id: $nodeId
                })-[relationship]-(neighbor:GraphEntity {
                    projectId: $projectId,
                    graphVersion: p.activeManifestVersion
                })
                RETURN neighbor, relationship,
                       CASE WHEN startNode(relationship) = center
                            THEN 'outgoing' ELSE 'incoming' END AS direction
                ORDER BY type(relationship), neighbor.id
                LIMIT $limit
                """,
                new { projectId, nodeId, limit });
            var result = new List<GraphNeighborV3>();
            while (await cursor.FetchAsync())
            {
                var relationship = cursor.Current["relationship"].As<IRelationship>();
                result.Add(new GraphNeighborV3(
                    MapNode(cursor.Current["neighbor"].As<INode>()),
                    MapEdge(relationship),
                    cursor.Current["direction"].As<string>()));
            }
            return (IReadOnlyList<GraphNeighborV3>)result;
        });
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<GraphNeighborV3>>>
        GetNeighborsBatchAsync(
            string projectId,
            IReadOnlyList<string> nodeIds,
            int limitPerNode,
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
            _ => (IReadOnlyList<GraphNeighborV3>)Array.Empty<GraphNeighborV3>(),
            StringComparer.Ordinal);
        if (_driver is null || distinctIds.Count == 0) return empty;

        await using var session = OpenReadSession();
        cancellationToken.ThrowIfCancellationRequested();
        return await session.ExecuteReadAsync(async transaction =>
        {
            var cursor = await transaction.RunAsync(
                """
                MATCH (p:ProjectGraph {projectId: $projectId})
                UNWIND $nodeIds AS nodeId
                MATCH (center:GraphEntity {
                    projectId: $projectId,
                    graphVersion: p.activeManifestVersion,
                    id: nodeId
                })-[relationship]-(neighbor:GraphEntity {
                    projectId: $projectId,
                    graphVersion: p.activeManifestVersion
                })
                WITH center, neighbor, relationship,
                     CASE WHEN startNode(relationship) = center
                          THEN 'outgoing' ELSE 'incoming' END AS direction
                ORDER BY center.id, type(relationship), neighbor.id
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
                       item.direction AS direction
                ORDER BY centerId, type(item.relationship), item.neighbor.id
                """,
                new { projectId, nodeIds = distinctIds, limitPerNode });
            var mutable = distinctIds.ToDictionary(
                nodeId => nodeId,
                _ => new List<GraphNeighborV3>(),
                StringComparer.Ordinal);
            while (await cursor.FetchAsync())
            {
                var centerId = cursor.Current["centerId"].As<string>();
                var relationship = cursor.Current["relationship"].As<IRelationship>();
                mutable[centerId].Add(new GraphNeighborV3(
                    MapNode(cursor.Current["neighbor"].As<INode>()),
                    MapEdge(relationship),
                    cursor.Current["direction"].As<string>()));
            }
            return mutable.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<GraphNeighborV3>)pair.Value,
                StringComparer.Ordinal);
        });
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GraphSearchHitV3>> GetCentralNodesAsync(
        string projectId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        limit = Math.Clamp(limit, 1, 500);
        if (_driver is null) return [];
        await using var session = OpenReadSession();
        cancellationToken.ThrowIfCancellationRequested();
        return await session.ExecuteReadAsync(async transaction =>
        {
            var cursor = await transaction.RunAsync(
                """
                MATCH (p:ProjectGraph {projectId: $projectId})
                MATCH (node:GraphEntity {
                    projectId: $projectId,
                    graphVersion: p.activeManifestVersion
                })
                OPTIONAL MATCH (node)-[relationship]-(:GraphEntity {
                    projectId: $projectId,
                    graphVersion: p.activeManifestVersion
                })
                RETURN node, count(relationship) AS degree
                ORDER BY
                    CASE node.kind
                        WHEN 'Feature' THEN 0
                        WHEN 'EntryPoint' THEN 1
                        WHEN 'Code' THEN 2
                        ELSE 3
                    END,
                    degree DESC,
                    node.id
                LIMIT $limit
                """,
                new { projectId, limit });
            var result = new List<GraphSearchHitV3>();
            while (await cursor.FetchAsync())
                result.Add(new GraphSearchHitV3(
                    MapNode(cursor.Current["node"].As<INode>()),
                    cursor.Current["degree"].As<double>()));
            return (IReadOnlyList<GraphSearchHitV3>)result;
        });
    }

    /// <inheritdoc />
    public async Task<(int Nodes, int Edges)> GetStatsAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        if (_driver is null) return (0, 0);
        await using var session = OpenReadSession();
        cancellationToken.ThrowIfCancellationRequested();
        return await session.ExecuteReadAsync(async transaction =>
        {
            var cursor = await transaction.RunAsync(
                """
                MATCH (p:ProjectGraph {projectId: $projectId})
                OPTIONAL MATCH (n:GraphEntity {
                    projectId: $projectId,
                    graphVersion: p.activeManifestVersion
                })
                WITH p, count(n) AS nodes
                OPTIONAL MATCH (:GraphEntity {
                    projectId: $projectId,
                    graphVersion: p.activeManifestVersion
                })-[r]->(:GraphEntity {
                    projectId: $projectId,
                    graphVersion: p.activeManifestVersion
                })
                WHERE r IS NULL OR r.graphVersion = p.activeManifestVersion
                RETURN nodes, count(r) AS edges
                """,
                new { projectId });
            // 尚未索引或已 DeleteProject 的專案沒有 ProjectGraph anchor；
            // 統計 API 必須回傳零，而不是讓空 cursor 的 SingleAsync 變成 500。
            if (!await cursor.FetchAsync()) return (0, 0);
            var record = cursor.Current;
            return (record["nodes"].As<int>(), record["edges"].As<int>());
        });
    }

    /// <inheritdoc />
    public async Task SaveCommunityReportsAsync(
        string projectId,
        string manifestVersion,
        IReadOnlyList<GraphCommunityReport> reports,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestVersion);
        ArgumentNullException.ThrowIfNull(reports);
        if (_driver is null) throw new InvalidOperationException("Neo4j V3 已停用。");
        var active = await GetActiveManifestAsync(projectId, cancellationToken);
        if (!string.Equals(active, manifestVersion, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Community report 只能寫入目前 active manifest，避免摘要與圖譜版本錯置。");

        await using var session = OpenWriteSession();
        await session.ExecuteWriteAsync(async transaction =>
        {
            var delete = await transaction.RunAsync(
                """
                MATCH (c:CommunityReport {
                    projectId: $projectId,
                    graphVersion: $manifestVersion
                })
                DELETE c
                """,
                new { projectId, manifestVersion });
            await delete.ConsumeAsync();
            if (reports.Count == 0) return;
            var rows = reports.Select(report => new
            {
                report.CommunityId,
                report.Kind,
                report.Title,
                report.Summary,
                report.CacheKey,
                report.AiEnriched,
                memberIdsJson = JsonSerializer.Serialize(report.MemberIds, JsonOptions),
            }).ToList();
            var insert = await transaction.RunAsync(
                """
                UNWIND $rows AS row
                CREATE (c:CommunityReport {
                    projectId: $projectId,
                    graphVersion: $manifestVersion,
                    communityId: row.CommunityId,
                    kind: row.Kind,
                    title: row.Title,
                    summary: row.Summary,
                    cacheKey: row.CacheKey,
                    aiEnriched: row.AiEnriched,
                    memberIdsJson: row.memberIdsJson
                })
                """,
                new { projectId, manifestVersion, rows });
            await insert.ConsumeAsync();
        });
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GraphCommunityReport>> ListCommunityReportsAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        if (_driver is null) return [];
        await using var session = OpenReadSession();
        cancellationToken.ThrowIfCancellationRequested();
        return await session.ExecuteReadAsync(async transaction =>
        {
            var cursor = await transaction.RunAsync(
                """
                MATCH (p:ProjectGraph {projectId: $projectId})
                MATCH (c:CommunityReport {
                    projectId: $projectId,
                    graphVersion: p.activeManifestVersion
                })
                RETURN c
                ORDER BY c.kind, c.communityId
                """,
                new { projectId });
            var result = new List<GraphCommunityReport>();
            while (await cursor.FetchAsync())
            {
                var properties = cursor.Current["c"].As<INode>().Properties;
                result.Add(new GraphCommunityReport(
                    RequiredString(properties, "communityId"),
                    RequiredString(properties, "kind"),
                    RequiredString(properties, "title"),
                    RequiredString(properties, "summary"),
                    DeserializeList(StringProperty(properties, "memberIdsJson")),
                    StringProperty(properties, "cacheKey"),
                    properties.TryGetValue("aiEnriched", out var aiEnriched) &&
                    aiEnriched.As<bool>()));
            }
            return (IReadOnlyList<GraphCommunityReport>)result;
        });
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<IReadOnlyList<string>>?> TryDetectLeidenCommunitiesAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        if (_driver is null) return null;
        var graphVersion = await GetActiveManifestAsync(projectId, cancellationToken);
        if (graphVersion is null) return null;

        // graph catalog name 只存在於 GDS 記憶體，使用隨機 suffix 避免兩個專案索引
        // 或清理重試互相碰撞；domain graph 與 primary community 都不會被 Leiden 改寫。
        var graphName =
            $"wingman_{GraphIdentity.Sha256(projectId)[..12]}_{Guid.NewGuid():N}";
        var projected = false;
        await using var session = OpenWriteSession();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var functionCursor = await session.RunAsync(
                """
                SHOW FUNCTIONS
                YIELD name
                WHERE name = 'gds.graph.project'
                RETURN count(*) AS count
                """);
            var hasProjection =
                (await functionCursor.SingleAsync())["count"].As<long>() > 0;
            var procedureCursor = await session.RunAsync(
                """
                SHOW PROCEDURES
                YIELD name
                WHERE name = 'gds.leiden.stream'
                RETURN count(*) AS count
                """);
            var hasLeiden =
                (await procedureCursor.SingleAsync())["count"].As<long>() > 0;
            if (!hasProjection || !hasLeiden) return null;

            // Cypher projection 只讀目前 active manifest，並把九種關係轉為 SPEC 權重。
            // undirected projection 只供 discovery；原始 domain edge 方向仍完整保留在 Neo4j。
            var projectionCursor = await session.RunAsync(
                """
                MATCH (source:GraphEntity {
                    projectId: $projectId,
                    graphVersion: $graphVersion
                })
                OPTIONAL MATCH (source)-[relationship]->(target:GraphEntity {
                    projectId: $projectId,
                    graphVersion: $graphVersion
                })
                RETURN gds.graph.project(
                    $graphName,
                    source,
                    target,
                    {
                        relationshipProperties:
                            CASE
                                WHEN relationship IS NULL THEN {}
                                ELSE {
                                    weight:
                                        CASE type(relationship)
                                            WHEN 'ROUTES_TO' THEN 1.00
                                            WHEN 'HANDLES' THEN 0.95
                                            WHEN 'TRIGGERS' THEN 0.95
                                            WHEN 'DISPATCHES_TO' THEN 0.90
                                            WHEN 'WRITES' THEN 0.90
                                            WHEN 'READS' THEN 0.85
                                            WHEN 'MAPS_TO' THEN 0.85
                                            WHEN 'CALLS' THEN 0.75
                                            WHEN 'DEPENDS_ON' THEN 0.70
                                            ELSE 0.50
                                        END
                                }
                            END
                    },
                    {
                        undirectedRelationshipTypes: ['*'],
                        readConcurrency: 1
                    }
                ) AS graph
                """,
                new { projectId, graphVersion, graphName });
            await projectionCursor.SingleAsync();
            projected = true;

            // randomSeed + concurrency=1 讓相同 topology 的 membership 可重現；
            // 回傳 member IDs 後由 GraphCommunityBuilder 重新計算穩定 community ID，
            // 不採用 GDS 執行期 communityId 作為持久 identity。
            var leidenCursor = await session.RunAsync(
                """
                CALL gds.leiden.stream(
                    $graphName,
                    {
                        relationshipWeightProperty: 'weight',
                        randomSeed: 23,
                        concurrency: 1,
                        logProgress: false
                    })
                YIELD nodeId, communityId
                RETURN gds.util.asNode(nodeId).id AS id, communityId
                ORDER BY communityId, id
                """,
                new { graphName });
            var groups = new SortedDictionary<long, List<string>>();
            while (await leidenCursor.FetchAsync())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = leidenCursor.Current["id"].As<string?>();
                if (string.IsNullOrWhiteSpace(id)) continue;
                var communityId = leidenCursor.Current["communityId"].As<long>();
                if (!groups.TryGetValue(communityId, out var members))
                {
                    members = [];
                    groups.Add(communityId, members);
                }
                members.Add(id);
            }
            return groups.Values
                .Where(group => group.Count >= 2)
                .Select(group => (IReadOnlyList<string>)group
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToList())
                .OrderBy(group => group[0], StringComparer.Ordinal)
                .ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // GDS 是 optional discovery 能力。外掛缺失、版本不相容或記憶體估算拒絕時，
            // 必須安全退回 deterministic label propagation，不能讓 canonical publish 失敗。
            _logger.LogInformation(
                "Neo4j GDS Leiden 不可用，將使用 deterministic secondary community fallback。" +
                " Project={ProjectId}, ExceptionType={ExceptionType}",
                projectId,
                exception.GetType().Name);
            return null;
        }
        finally
        {
            if (projected)
            {
                try
                {
                    var dropCursor = await session.RunAsync(
                        """
                        CALL gds.graph.drop($graphName, false)
                        YIELD graphName
                        RETURN graphName
                        """,
                        new { graphName });
                    await dropCursor.ConsumeAsync();
                }
                catch (Exception exception) when (
                    exception is not OperationCanceledException)
                {
                    _logger.LogWarning(
                        "清理暫存 GDS projection 失敗；不影響 active domain graph。" +
                        " Project={ProjectId}, ExceptionType={ExceptionType}",
                        projectId,
                        exception.GetType().Name);
                }
            }
        }
    }

    /// <inheritdoc />
    private async Task<GraphVisualDataV3> GetVisualGraphCoreAsync(
        string projectId,
        int limit,
        IReadOnlyList<string>? kinds,
        IReadOnlyList<string>? relationshipTypes,
        CancellationToken cancellationToken = default,
        IReadOnlyList<string>? roles = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        limit = Math.Clamp(limit, 1, 10_000);
        var normalizedKinds = NormalizeKinds(kinds);
        var normalizedRelationships = NormalizeRelationships(relationshipTypes);
        var normalizedRoles = NormalizeRoles(roles);
        if (_driver is null) return new([], [], 0, 0, 0, false);
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
                MATCH (p:ProjectGraph {projectId: $projectId})
                MATCH (source:GraphEntity {
                    projectId: $projectId,
                    graphVersion: p.activeManifestVersion
                })-[relationship]->(target:GraphEntity {
                    projectId: $projectId,
                    graphVersion: p.activeManifestVersion
                })
                WHERE (size($kinds) = 0 OR
                       (source.kind IN $kinds AND target.kind IN $kinds))
                  AND (size($roles) = 0 OR
                       (source.role IN $roles AND target.role IN $roles))
                  AND (size($relationshipTypes) = 0 OR
                       type(relationship) IN $relationshipTypes)
                WITH source, target, relationship,
                    CASE source.kind
                        WHEN 'Feature' THEN 0
                        WHEN 'EntryPoint' THEN 1
                        WHEN 'Code' THEN 2
                        ELSE 3
                    END AS sourcePriority,
                    CASE target.kind
                        WHEN 'Feature' THEN 0
                        WHEN 'EntryPoint' THEN 1
                        WHEN 'Code' THEN 2
                        ELSE 3
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
                    kinds = normalizedKinds,
                    roles = normalizedRoles,
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
                MATCH (p:ProjectGraph {projectId: $projectId})
                MATCH (node:GraphEntity {
                    projectId: $projectId,
                    graphVersion: p.activeManifestVersion
                })
                WHERE (size($kinds) = 0 OR node.kind IN $kinds)
                  AND (size($roles) = 0 OR node.role IN $roles)
                  AND (size($relationshipTypes) = 0 OR EXISTS {
                      MATCH (node)-[filteredRelationship]-(:GraphEntity {
                          projectId: $projectId,
                          graphVersion: p.activeManifestVersion
                      })
                      WHERE type(filteredRelationship) IN $relationshipTypes
                  })
                OPTIONAL MATCH (node)-[relationship]-(:GraphEntity {
                    projectId: $projectId,
                    graphVersion: p.activeManifestVersion
                })
                WITH node, count(relationship) AS degree,
                    CASE WHEN node.id IN $coreNodeIds THEN 0 ELSE 1 END AS corePriority
                ORDER BY
                    corePriority,
                    CASE node.kind
                        WHEN 'Feature' THEN 0
                        WHEN 'EntryPoint' THEN 1
                        WHEN 'Code' THEN 2
                        ELSE 3
                    END,
                    degree DESC,
                    node.id
                LIMIT $limit
                RETURN node, degree
                """,
                new
                {
                    projectId,
                    kinds = normalizedKinds,
                    roles = normalizedRoles,
                    relationshipTypes = normalizedRelationships,
                    coreNodeIds,
                    limit,
                });
            var visualNodes = new List<GraphVisualNodeV3>();
            while (await nodeCursor.FetchAsync())
                visualNodes.Add(MapVisualNode(
                    nodeCursor.Current["node"].As<INode>(),
                    nodeCursor.Current["degree"].As<int>()));

            var ids = visualNodes.Select(node => node.Id).ToList();
            var visualEdges = new List<GraphVisualEdgeV3>();
            if (ids.Count > 0)
            {
                var edgeCursor = await transaction.RunAsync(
                    """
                    MATCH (p:ProjectGraph {projectId: $projectId})
                    MATCH (source:GraphEntity {
                        projectId: $projectId,
                        graphVersion: p.activeManifestVersion
                    })-[relationship]->(target:GraphEntity {
                        projectId: $projectId,
                        graphVersion: p.activeManifestVersion
                    })
                    WHERE source.id IN $ids
                      AND target.id IN $ids
                      AND (size($relationshipTypes) = 0 OR type(relationship) IN $relationshipTypes)
                    RETURN relationship
                    ORDER BY relationship.id
                    LIMIT $edgeLimit
                    """,
                    new
                    {
                        projectId,
                        ids,
                        relationshipTypes = normalizedRelationships,
                        edgeLimit = Math.Min(limit * 4, 20_000),
                    });
                while (await edgeCursor.FetchAsync())
                    visualEdges.Add(MapVisualEdge(
                        edgeCursor.Current["relationship"].As<IRelationship>()));
            }

            var countCursor = await transaction.RunAsync(
                """
                MATCH (p:ProjectGraph {projectId: $projectId})
                MATCH (node:GraphEntity {
                    projectId: $projectId,
                    graphVersion: p.activeManifestVersion
                })
                WHERE (size($kinds) = 0 OR node.kind IN $kinds)
                  AND (size($roles) = 0 OR node.role IN $roles)
                  AND (size($relationshipTypes) = 0 OR EXISTS {
                      MATCH (node)-[filteredRelationship]-(:GraphEntity {
                          projectId: $projectId,
                          graphVersion: p.activeManifestVersion
                      })
                      WHERE type(filteredRelationship) IN $relationshipTypes
                  })
                RETURN count(node) AS total
                """,
                new
                {
                    projectId,
                    kinds = normalizedKinds,
                    roles = normalizedRoles,
                    relationshipTypes = normalizedRelationships,
                });
            var total = (await countCursor.SingleAsync())["total"].As<int>();
            return new GraphVisualDataV3(
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
        IEnumerable<GraphVisualEdgeV3> edges,
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
        IEnumerable<GraphVisualEdgeV3> edges,
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
    internal static IReadOnlyList<GraphVisualEdgeV3> KeepVisualEdgesWithEndpoints(
        IEnumerable<GraphVisualEdgeV3> edges,
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
    public async Task<GraphVisualSchemaV3> GetVisualSchemaAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        if (_driver is null)
            return new(0, 0, [], [], []);
        await using var session = OpenReadSession();
        cancellationToken.ThrowIfCancellationRequested();
        return await session.ExecuteReadAsync(async transaction =>
        {
            var revisionCursor = await transaction.RunAsync(
                """
                MATCH (p:ProjectGraph {projectId: $projectId})
                RETURN p.activeManifestVersion AS revision
                """,
                new { projectId });
            var graphRevision = (await revisionCursor.SingleAsync())["revision"].As<string?>();
            var nodeCursor = await transaction.RunAsync(
                """
                MATCH (p:ProjectGraph {projectId: $projectId})
                MATCH (node:GraphEntity {
                    projectId: $projectId,
                    graphVersion: p.activeManifestVersion
                })
                RETURN node.kind AS name, count(node) AS count
                ORDER BY name
                """,
                new { projectId });
            var nodeKinds = new List<GraphFacetV3>();
            while (await nodeCursor.FetchAsync())
                nodeKinds.Add(new GraphFacetV3(
                    nodeCursor.Current["name"].As<string>(),
                    nodeCursor.Current["count"].As<int>()));

            var roleCursor = await transaction.RunAsync(
                """
                MATCH (p:ProjectGraph {projectId: $projectId})
                MATCH (node:GraphEntity {
                    projectId: $projectId,
                    graphVersion: p.activeManifestVersion
                })
                WHERE node.role IS NOT NULL AND trim(node.role) <> ''
                RETURN node.role AS name, count(node) AS count
                ORDER BY name
                """,
                new { projectId });
            var nodeRoles = new List<GraphFacetV3>();
            while (await roleCursor.FetchAsync())
                nodeRoles.Add(new GraphFacetV3(
                    roleCursor.Current["name"].As<string>(),
                    roleCursor.Current["count"].As<int>()));

            var edgeCursor = await transaction.RunAsync(
                """
                MATCH (p:ProjectGraph {projectId: $projectId})
                MATCH (:GraphEntity {
                    projectId: $projectId,
                    graphVersion: p.activeManifestVersion
                })-[relationship]->(:GraphEntity {
                    projectId: $projectId,
                    graphVersion: p.activeManifestVersion
                })
                RETURN type(relationship) AS name, count(relationship) AS count
                ORDER BY name
                """,
                new { projectId });
            var relationships = new List<GraphFacetV3>();
            while (await edgeCursor.FetchAsync())
                relationships.Add(new GraphFacetV3(
                    edgeCursor.Current["name"].As<string>(),
                    edgeCursor.Current["count"].As<int>()));
            return new GraphVisualSchemaV3(
                nodeKinds.Sum(item => item.Count),
                relationships.Sum(item => item.Count),
                nodeKinds,
                nodeRoles,
                relationships)
            {
                GraphRevision = graphRevision,
            };
        });
    }

    /// <inheritdoc />
    public Task<GraphVisualDataV3> GetViewerGraphAsync(
        string projectId,
        int limit,
        IReadOnlyList<GraphViewerSearchFilter>? filters,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeViewerFilters(filters);
        return GetVisualGraphCoreAsync(
            projectId,
            limit,
            normalized.Kinds,
            normalized.RelationshipTypes,
            cancellationToken,
            normalized.Roles);
    }

    /// <inheritdoc />
    public async Task<GraphViewerSearchResult> SearchVisualGraphAsync(
        string projectId,
        string query,
        IReadOnlyList<GraphViewerSearchFilter>? filters,
        int take,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        take = Math.Clamp(take, 1, 100);
        var normalizedFilters = NormalizeViewerFilters(filters);
        if (_driver is null) return new([], take, false);

        var luceneQuery = GraphRetrievalService.BuildViewerLuceneQuery(query);
        if (string.IsNullOrWhiteSpace(luceneQuery)) return new([], take, false);
        await using var session = OpenReadSession();
        cancellationToken.ThrowIfCancellationRequested();
        return await session.ExecuteReadAsync(async transaction =>
        {
            var cursor = await transaction.RunAsync(
                """
                MATCH (p:ProjectGraph {projectId: $projectId})
                CALL db.index.fulltext.queryNodes(
                    'graphEntitySearchV3',
                    $query,
                    {limit: $candidateLimit})
                YIELD node, score
                WHERE node.projectId = $projectId
                  AND node.graphVersion = p.activeManifestVersion
                  AND (size($kinds) = 0 OR node.kind IN $kinds)
                  AND (size($roles) = 0 OR node.role IN $roles)
                  AND (size($relationshipTypes) = 0 OR EXISTS {
                      MATCH (node)-[incident]-(:GraphEntity {
                          projectId: $projectId,
                          graphVersion: p.activeManifestVersion
                      })
                      WHERE type(incident) IN $relationshipTypes
                  })
                OPTIONAL MATCH (node)-[relationship]-(:GraphEntity {
                    projectId: $projectId,
                    graphVersion: p.activeManifestVersion
                })
                WITH node, score, count(relationship) AS degree
                RETURN node, score, degree
                ORDER BY score DESC, node.id
                LIMIT $resultLimit
                """,
                new
                {
                    projectId,
                    query = luceneQuery,
                    kinds = normalizedFilters.Kinds,
                    roles = normalizedFilters.Roles,
                    relationshipTypes = normalizedFilters.RelationshipTypes,
                    candidateLimit = Math.Min((take + 1) * 20, 2_000),
                    resultLimit = take + 1,
                });
            var hits = new List<GraphViewerSearchHit>();
            while (await cursor.FetchAsync())
                hits.Add(new GraphViewerSearchHit(
                    MapVisualNode(
                        cursor.Current["node"].As<INode>(),
                        cursor.Current["degree"].As<int>()),
                    cursor.Current["score"].As<double>()));
            var hasMore = hits.Count > take;
            return new GraphViewerSearchResult(hits.Take(take).ToList(), take, hasMore);
        });
    }

    /// <inheritdoc />
    public async Task<GraphVisualDataV3> GetVisualNeighborsAsync(
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
        limit = Math.Clamp(limit, 1, 10_000);
        var normalizedMode = NormalizeVisualNeighborMode(mode);

        var selected = new Dictionary<string, GraphVisualNodeV3>(StringComparer.Ordinal);
        var edges = new Dictionary<string, GraphVisualEdgeV3>(StringComparer.Ordinal);
        var neighborQueryReachedCap = false;
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
                // 篩選，高 degree 節點可能剛好被另一方向填滿，造成傳入／傳出漏資料。
                var neighbors = normalizedMode == "all"
                    ? await GetNeighborsAsync(
                        projectId, id, Math.Min(limit, 500), cancellationToken)
                    : await GetDirectionalNeighborsAsync(
                        projectId,
                        id,
                        Math.Min(limit, 500),
                        normalizedMode,
                        cancellationToken);
                if (neighbors.Count >= Math.Min(limit, 500))
                    neighborQueryReachedCap = true;
                foreach (var neighbor in neighbors)
                {
                    if (selected.Count >= limit &&
                        !selected.ContainsKey(neighbor.Node.Id))
                        break;
                    if (!selected.ContainsKey(neighbor.Node.Id))
                    {
                        selected[neighbor.Node.Id] =
                            MapVisualNode(neighbor.Node, 0);
                        next.Add(neighbor.Node.Id);
                    }
                    edges[neighbor.Edge.Id] = new GraphVisualEdgeV3(
                        neighbor.Edge.Id,
                        neighbor.Edge.SourceId,
                        neighbor.Edge.TargetId,
                        RelationshipType(neighbor.Edge.Kind),
                        new Dictionary<string, object?>
                        {
                            ["evidence"] = neighbor.Edge.Evidence,
                        });
                }
            }
            frontier = next.Distinct(StringComparer.Ordinal).ToList();
        }
        var stats = await GetStatsAsync(projectId, cancellationToken);
        return new GraphVisualDataV3(
            selected.Values.OrderBy(node => node.Id, StringComparer.Ordinal).ToList(),
            edges.Values.OrderBy(edge => edge.Id, StringComparer.Ordinal).ToList(),
            stats.Nodes,
            selected.Count,
            edges.Count,
            neighborQueryReachedCap ||
            (selected.Count < stats.Nodes && selected.Count >= limit));
    }

    /// <summary>
    /// 將前端語意化的展開模式轉成後端實際使用的方向。
    /// </summary>
    /// <param name="mode">UI 或 API 傳入的展開模式。</param>
    /// <returns>all、in 或 out。</returns>
    internal static string NormalizeVisualNeighborMode(string mode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        return mode.Trim().ToLowerInvariant() switch
        {
            "all" => "all",
            "in" => "in",
            "out" => "out",
            _ => throw new ArgumentException(
                "Graph neighbor mode 只允許 all、in、out。",
                nameof(mode)),
        };
    }

    /// <inheritdoc />
    public async Task<GraphVisualQueryResultV3> QueryVisualGraphAsync(
        string projectId,
        string cypher,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(cypher);
        limit = Math.Clamp(limit, 1, 10_000);
        cypher = EnsureReadOnlyCypher(cypher);
        var manifest = await GetActiveManifestAsync(projectId, cancellationToken) ??
            throw new InvalidOperationException("專案尚無 active V3 graph。");
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
            var nodes = new Dictionary<string, GraphVisualNodeV3>(StringComparer.Ordinal);
            var edges = new Dictionary<string, GraphVisualEdgeV3>(StringComparer.Ordinal);
            while (rows.Count < limit && await cursor.FetchAsync())
            {
                if (columns.Count == 0)
                    columns.AddRange(cursor.Current.Keys);
                var row = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var key in columns)
                {
                    var value = cursor.Current[key];
                    CollectGraphValues(value, nodes, edges);
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
                    MATCH (p:ProjectGraph {projectId: $projectId})
                    MATCH (node:GraphEntity {
                        projectId: $projectId,
                        graphVersion: p.activeManifestVersion
                    })
                    WHERE node.id IN $nodeIds
                    OPTIONAL MATCH (node)-[relationship]-(:GraphEntity {
                        projectId: $projectId,
                        graphVersion: p.activeManifestVersion
                    })
                    RETURN node, count(relationship) AS degree
                    ORDER BY node.id
                    """,
                    new { projectId, nodeIds = missingEndpointIds });
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
            return new GraphVisualQueryResultV3(
                columns,
                rows,
                new GraphVisualDataV3(
                    nodes.Values.OrderBy(node => node.Id, StringComparer.Ordinal).ToList(),
                    completeEdges,
                    nodes.Count,
                    nodes.Count,
                    completeEdges.Count,
                    false));
        });
    }

    private async Task WriteNodesAsync(
        GraphSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await using var session = OpenWriteSession();
        foreach (var batch in snapshot.Nodes.Chunk(_options.WriteBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = batch.Select(node => new Dictionary<string, object?>
            {
                ["id"] = node.Id,
                ["kind"] = node.Kind.ToString(),
                ["role"] = node.Role,
                ["name"] = node.Name,
                ["searchableText"] = node.SearchableText,
                ["aliasesText"] = string.Join(' ', node.Aliases),
                ["aliasesJson"] = JsonSerializer.Serialize(node.Aliases, JsonOptions),
                ["language"] = node.Language,
                ["technology"] = node.Technology,
                ["state"] = node.State,
                ["filePath"] = node.FilePath,
                ["startLine"] = node.StartLine,
                ["endLine"] = node.EndLine,
                ["attributesJson"] = JsonSerializer.Serialize(node.Attributes, JsonOptions),
                ["evidenceJson"] = JsonSerializer.Serialize(node.Evidence, JsonOptions),
            }).ToList();
            await session.ExecuteWriteAsync(async transaction =>
            {
                var cursor = await transaction.RunAsync(
                    """
                    UNWIND $rows AS row
                    CREATE (n:GraphEntity {
                        projectId: $projectId,
                        graphVersion: $graphVersion,
                        id: row.id,
                        kind: row.kind,
                        role: row.role,
                        name: row.name,
                        searchableText: row.searchableText,
                        aliasesText: row.aliasesText,
                        aliasesJson: row.aliasesJson,
                        language: row.language,
                        technology: row.technology,
                        state: row.state,
                        filePath: row.filePath,
                        startLine: row.startLine,
                        endLine: row.endLine,
                        attributesJson: row.attributesJson,
                        evidenceJson: row.evidenceJson
                    })
                    """,
                    new
                    {
                        projectId = snapshot.ProjectId,
                        graphVersion = snapshot.ManifestVersion,
                        rows,
                    });
                await cursor.ConsumeAsync();
            });
        }
    }

    private async Task WriteEdgesAsync(
        GraphSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await using var session = OpenWriteSession();
        foreach (var kindGroup in snapshot.Edges.GroupBy(edge => edge.Kind))
        {
            var relationshipType = RelationshipType(kindGroup.Key);
            foreach (var batch in kindGroup.Chunk(_options.WriteBatchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rows = batch.Select(edge => new
                {
                    edge.Id,
                    edge.SourceId,
                    edge.TargetId,
                    evidenceJson = JsonSerializer.Serialize(edge.Evidence, JsonOptions),
                }).ToList();
                await session.ExecuteWriteAsync(async transaction =>
                {
                    var cursor = await transaction.RunAsync(
                        $$"""
                        UNWIND $rows AS row
                        MATCH (source:GraphEntity {
                            projectId: $projectId,
                            graphVersion: $graphVersion,
                            id: row.SourceId
                        })
                        MATCH (target:GraphEntity {
                            projectId: $projectId,
                            graphVersion: $graphVersion,
                            id: row.TargetId
                        })
                        CREATE (source)-[relationship:{{relationshipType}} {
                            id: row.Id,
                            graphVersion: $graphVersion,
                            sourceId: row.SourceId,
                            targetId: row.TargetId,
                            evidenceJson: row.evidenceJson
                        }]->(target)
                        """,
                        new
                        {
                            projectId = snapshot.ProjectId,
                            graphVersion = snapshot.ManifestVersion,
                            rows,
                        });
                    await cursor.ConsumeAsync();
                });
            }
        }
    }

    private async Task ValidateStagingAsync(
        GraphSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await using var session = OpenReadSession();
        cancellationToken.ThrowIfCancellationRequested();
        var validation = await session.ExecuteReadAsync(async transaction =>
        {
            var cursor = await transaction.RunAsync(
                """
                MATCH (n:GraphEntity {
                    projectId: $projectId,
                    graphVersion: $graphVersion
                })
                WITH count(n) AS nodes,
                     count(CASE
                         WHEN n.evidenceJson IS NOT NULL
                              AND size(n.evidenceJson) > 2
                         THEN 1
                     END) AS nodesWithEvidence
                OPTIONAL MATCH (:GraphEntity {
                    projectId: $projectId,
                    graphVersion: $graphVersion
                })-[r {
                    graphVersion: $graphVersion
                }]->(:GraphEntity {
                    projectId: $projectId,
                    graphVersion: $graphVersion
                })
                RETURN nodes,
                       nodesWithEvidence,
                       count(r) AS edges,
                       count(CASE
                           WHEN r.evidenceJson IS NOT NULL
                                AND size(r.evidenceJson) > 2
                           THEN 1
                       END) AS edgesWithEvidence,
                       collect(DISTINCT type(r)) AS relationshipTypes
                """,
                new
                {
                    projectId = snapshot.ProjectId,
                    graphVersion = snapshot.ManifestVersion,
                });
            var record = await cursor.SingleAsync();
            return new
            {
                Nodes = record["nodes"].As<int>(),
                NodesWithEvidence = record["nodesWithEvidence"].As<int>(),
                Edges = record["edges"].As<int>(),
                EdgesWithEvidence = record["edgesWithEvidence"].As<int>(),
                RelationshipTypes = record["relationshipTypes"]
                    .As<List<string>>()
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToList(),
            };
        });
        var expectedRelationshipTypes = snapshot.Edges
            .Select(edge => RelationshipType(edge.Kind))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
        if (validation.Nodes != snapshot.Nodes.Count ||
            validation.Edges != snapshot.Edges.Count ||
            validation.NodesWithEvidence != snapshot.Nodes.Count ||
            validation.EdgesWithEvidence != snapshot.Edges.Count ||
            !validation.RelationshipTypes.SequenceEqual(
                expectedRelationshipTypes, StringComparer.Ordinal))
            throw new InvalidOperationException(
                $"Neo4j staging 驗證不一致：nodes {validation.Nodes}/{snapshot.Nodes.Count}, " +
                $"edges {validation.Edges}/{snapshot.Edges.Count}, " +
                $"node evidence {validation.NodesWithEvidence}/{snapshot.Nodes.Count}, " +
                $"edge evidence {validation.EdgesWithEvidence}/{snapshot.Edges.Count}。");
    }

    private async Task PromoteAsync(
        GraphSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await using var session = OpenWriteSession();
        cancellationToken.ThrowIfCancellationRequested();
        await session.ExecuteWriteAsync(async transaction =>
        {
            var cursor = await transaction.RunAsync(
                """
                MERGE (p:ProjectGraph {projectId: $projectId})
                SET p.previousManifestVersion = CASE
                        WHEN p.activeManifestVersion = $graphVersion
                        THEN p.previousManifestVersion
                        ELSE p.activeManifestVersion
                    END,
                    p.activeManifestVersion = $graphVersion,
                    p.schemaVersion = $schemaVersion,
                    p.canonicalDigest = $canonicalDigest,
                    p.nodeCount = $nodeCount,
                    p.edgeCount = $edgeCount,
                    p.promotedAt = datetime()
                """,
                new
                {
                    projectId = snapshot.ProjectId,
                    graphVersion = snapshot.ManifestVersion,
                    schemaVersion = snapshot.SchemaVersion,
                    canonicalDigest = snapshot.CanonicalDigest,
                    nodeCount = snapshot.Nodes.Count,
                    edgeCount = snapshot.Edges.Count,
                });
            await cursor.ConsumeAsync();
        });
    }

    private async Task DeleteVersionAsync(
        string projectId,
        string graphVersion,
        CancellationToken cancellationToken)
    {
        if (_driver is null) throw new InvalidOperationException("Neo4j V3 已停用。");
        await using var session = OpenWriteSession();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var deleted = await session.ExecuteWriteAsync(async transaction =>
            {
                var cursor = await transaction.RunAsync(
                    $$"""
                    MATCH (n:GraphEntity {
                        projectId: $projectId,
                        graphVersion: $graphVersion
                    })
                    WITH n LIMIT {{CleanupBatchSize}}
                    DETACH DELETE n
                    RETURN count(*) AS deleted
                    """,
                    new { projectId, graphVersion });
                return (await cursor.SingleAsync())["deleted"].As<int>();
            });
            if (deleted == 0) break;
        }
    }

    private async Task CleanupRetiredVersionsAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        await using var session = OpenWriteSession();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var deleted = await session.ExecuteWriteAsync(async transaction =>
            {
                var cursor = await transaction.RunAsync(
                    $$"""
                    MATCH (p:ProjectGraph {projectId: $projectId})
                    MATCH (n:GraphEntity {projectId: $projectId})
                    WHERE n.graphVersion <> p.activeManifestVersion
                      AND (p.previousManifestVersion IS NULL
                           OR n.graphVersion <> p.previousManifestVersion)
                    WITH n LIMIT {{CleanupBatchSize}}
                    DETACH DELETE n
                    RETURN count(*) AS deleted
                    """,
                    new { projectId });
                return (await cursor.SingleAsync())["deleted"].As<int>();
            });
            if (deleted == 0) break;
        }
    }

    /// <summary>
    /// 在資料庫端先套用 incoming／outgoing 方向，再執行 LIMIT。
    /// 此方法只接受已正規化的 in 或 out，查詢字串不接收任何外部可注入片段。
    /// </summary>
    private async Task<IReadOnlyList<GraphNeighborV3>> GetDirectionalNeighborsAsync(
        string projectId,
        string nodeId,
        int limit,
        string direction,
        CancellationToken cancellationToken)
    {
        if (_driver is null) return [];
        if (direction is not ("in" or "out"))
            throw new ArgumentException("方向查詢只允許 in 或 out。", nameof(direction));

        var cypher = direction == "in"
            ? """
              MATCH (p:ProjectGraph {projectId: $projectId})
              MATCH (neighbor:GraphEntity {
                  projectId: $projectId,
                  graphVersion: p.activeManifestVersion
              })-[relationship]->(center:GraphEntity {
                  projectId: $projectId,
                  graphVersion: p.activeManifestVersion,
                  id: $nodeId
              })
              RETURN neighbor, relationship
              ORDER BY type(relationship), neighbor.id
              LIMIT $limit
              """
            : """
              MATCH (p:ProjectGraph {projectId: $projectId})
              MATCH (center:GraphEntity {
                  projectId: $projectId,
                  graphVersion: p.activeManifestVersion,
                  id: $nodeId
              })-[relationship]->(neighbor:GraphEntity {
                  projectId: $projectId,
                  graphVersion: p.activeManifestVersion
              })
              RETURN neighbor, relationship
              ORDER BY type(relationship), neighbor.id
              LIMIT $limit
              """;

        await using var session = OpenReadSession();
        cancellationToken.ThrowIfCancellationRequested();
        return await session.ExecuteReadAsync(async transaction =>
        {
            var cursor = await transaction.RunAsync(
                cypher,
                new { projectId, nodeId, limit });
            var result = new List<GraphNeighborV3>();
            while (await cursor.FetchAsync())
            {
                result.Add(new GraphNeighborV3(
                    MapNode(cursor.Current["neighbor"].As<INode>()),
                    MapEdge(cursor.Current["relationship"].As<IRelationship>()),
                    direction == "in" ? "incoming" : "outgoing"));
            }
            return (IReadOnlyList<GraphNeighborV3>)result;
        });
    }

    private async Task<GraphVisualNodeV3?> ReadNodeByIdAsync(
        string projectId,
        string nodeId,
        CancellationToken cancellationToken)
    {
        if (_driver is null) return null;
        await using var session = OpenReadSession();
        cancellationToken.ThrowIfCancellationRequested();
        return await session.ExecuteReadAsync(async transaction =>
        {
            var cursor = await transaction.RunAsync(
                """
                MATCH (p:ProjectGraph {projectId: $projectId})
                MATCH (node:GraphEntity {
                    projectId: $projectId,
                    graphVersion: p.activeManifestVersion,
                    id: $nodeId
                })
                OPTIONAL MATCH (node)-[relationship]-(:GraphEntity {
                    projectId: $projectId,
                    graphVersion: p.activeManifestVersion
                })
                RETURN node, count(relationship) AS degree
                """,
                new { projectId, nodeId });
            return await cursor.FetchAsync()
                ? MapVisualNode(
                    cursor.Current["node"].As<INode>(),
                    cursor.Current["degree"].As<int>())
                : null;
        });
    }

    private static IReadOnlyList<string> NormalizeKinds(
        IReadOnlyList<string>? kinds)
    {
        if (kinds is null || kinds.Count == 0) return [];
        var allowed = Enum.GetNames<GraphNodeKind>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = kinds.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var invalid = result.FirstOrDefault(value => !allowed.Contains(value));
        if (invalid is not null)
            throw new ArgumentException($"不允許的 V3 NodeKind filter：{invalid}。");
        return result.Select(value =>
                Enum.Parse<GraphNodeKind>(value, ignoreCase: true).ToString())
            .ToList();
    }

    private static (
        IReadOnlyList<string> Kinds,
        IReadOnlyList<string> Roles,
        IReadOnlyList<string> RelationshipTypes) NormalizeViewerFilters(
            IReadOnlyList<GraphViewerSearchFilter>? filters)
    {
        if (filters is null || filters.Count == 0) return ([], [], []);
        var duplicate = filters
            .GroupBy(filter => filter.FacetId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException($"重複的 graph facet：{duplicate.Key}。");

        IReadOnlyList<string> Tokens(string facetId) => filters
            .FirstOrDefault(filter => string.Equals(
                filter.FacetId, facetId, StringComparison.Ordinal))?.Tokens ?? [];

        var allowedFacets = new HashSet<string>(
            ["node-category", "node-role", "edge-type"],
            StringComparer.Ordinal);
        var unknown = filters.FirstOrDefault(filter =>
            !allowedFacets.Contains(filter.FacetId));
        if (unknown is not null)
            throw new ArgumentException($"不允許的 graph facet：{unknown.FacetId}。");

        return (
            NormalizeKinds(Tokens("node-category")),
            NormalizeRoles(Tokens("node-role")),
            NormalizeRelationships(Tokens("edge-type")));
    }

    private static IReadOnlyList<string> NormalizeRoles(IReadOnlyList<string>? roles) =>
        roles is null
            ? []
            : roles.Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.Ordinal)
                .Take(100)
                .ToList();

    private static IReadOnlyList<string> NormalizeRelationships(
        IReadOnlyList<string>? relationships)
    {
        if (relationships is null || relationships.Count == 0) return [];
        var allowed = Enum.GetValues<GraphEdgeKind>()
            .ToDictionary(RelationshipType, kind => kind, StringComparer.OrdinalIgnoreCase);
        var result = relationships.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var invalid = result.FirstOrDefault(value => !allowed.ContainsKey(value));
        if (invalid is not null)
            throw new ArgumentException($"不允許的 V3 relationship filter：{invalid}。");
        return result.Select(value => RelationshipType(allowed[value])).ToList();
    }

    /// <summary>
    /// 驗證 UI 提供的 Cypher 僅能讀取同一個 active V3 graph。
    /// 每一個 MATCH node pattern 都必須明確使用 GraphEntity、projectId 與 graphVersion，
    /// 並由服務端補上或正規化 $limit，避免只在 client 停止讀取但資料庫仍執行無界查詢。
    /// </summary>
    /// <param name="cypher">使用者輸入的單一 read-only statement。</param>
    /// <returns>通過隔離驗證且已套用 bounded budget 的 statement。</returns>
    internal static string EnsureReadOnlyCypher(string cypher)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cypher);
        cypher = cypher.Trim();
        var structuralText = MaskCypherStringLiterals(cypher);
        if (UnsafeCypher.IsMatch(structuralText))
            throw new InvalidOperationException("只允許 read-only V3 Cypher，禁止寫入與 procedure call。");
        if (UnboundedAggregate.IsMatch(structuralText))
            throw new InvalidOperationException("read-only V3 Cypher 不允許 collect()，避免 LIMIT 前建立無界集合。");
        if (structuralText.Contains(';') ||
            structuralText.Contains("//", StringComparison.Ordinal) ||
            structuralText.Contains("/*", StringComparison.Ordinal))
            throw new InvalidOperationException("V3 Cypher 一次只允許一個 statement。");
        if (!BoundedLimit.IsMatch(cypher))
            cypher = TerminalLimit.IsMatch(cypher)
                ? TerminalLimit.Replace(cypher, "LIMIT $limit")
                : $"{cypher}\nLIMIT $limit";

        var matches = MatchClause.Matches(cypher);
        if (matches.Count == 0)
            throw new InvalidOperationException(
                "V3 Cypher 至少需要一個受 project/version 限制的 MATCH。");
        foreach (Match match in matches)
        {
            var nodePatterns = NodePattern.Matches(match.Groups["pattern"].Value);
            if (nodePatterns.Count == 0)
                throw new InvalidOperationException("MATCH 必須包含明確的 GraphEntity pattern。");
            foreach (Match nodePattern in nodePatterns)
            {
                var node = nodePattern.Groups["node"].Value;
                if (!node.Contains(":GraphEntity", StringComparison.Ordinal) ||
                    !ScopedProjectProperty.IsMatch(node) ||
                    !ScopedVersionProperty.IsMatch(node))
                    throw new InvalidOperationException(
                        "每個 MATCH node 都必須使用 :GraphEntity，並以 $projectId、$graphVersion 限制 active graph。");
            }
        }
        return cypher;
    }

    private static string MaskCypherStringLiterals(string cypher)
    {
        var chars = cypher.ToCharArray();
        for (var index = 0; index < chars.Length; index++)
        {
            if (chars[index] is not ('\'' or '"')) continue;
            var quote = chars[index];
            for (index++; index < chars.Length; index++)
            {
                if (chars[index] == '\\' && index + 1 < chars.Length)
                {
                    chars[index] = ' ';
                    chars[++index] = ' ';
                    continue;
                }
                if (chars[index] == quote)
                {
                    if (index + 1 < chars.Length && chars[index + 1] == quote)
                    {
                        chars[index] = ' ';
                        chars[++index] = ' ';
                        continue;
                    }
                    break;
                }
                chars[index] = ' ';
            }
        }
        return new string(chars);
    }

    private static void CollectGraphValues(
        object? value,
        IDictionary<string, GraphVisualNodeV3> nodes,
        IDictionary<string, GraphVisualEdgeV3> edges)
    {
        switch (value)
        {
            case INode node when node.Labels.Contains("GraphEntity"):
            {
                var mapped = MapVisualNode(node, 0);
                nodes[mapped.Id] = mapped;
                break;
            }
            case IRelationship relationship:
            {
                var mapped = MapVisualEdge(relationship);
                edges[mapped.Id] = mapped;
                break;
            }
            case IPath path:
                foreach (var node in path.Nodes)
                    CollectGraphValues(node, nodes, edges);
                foreach (var relationship in path.Relationships)
                    CollectGraphValues(relationship, nodes, edges);
                break;
            case IEnumerable<object> values:
                foreach (var item in values)
                    CollectGraphValues(item, nodes, edges);
                break;
        }
    }

    private static object? ToSafeTableValue(object? value) => value switch
    {
        null => null,
        INode node => new Dictionary<string, object?>
        {
            ["id"] = StringProperty(node.Properties, "id"),
            ["kind"] = StringProperty(node.Properties, "kind"),
            ["role"] = StringProperty(node.Properties, "role"),
            ["name"] = StringProperty(node.Properties, "name"),
            ["filePath"] = StringProperty(node.Properties, "filePath"),
        },
        IRelationship relationship => new Dictionary<string, object?>
        {
            ["id"] = StringProperty(relationship.Properties, "id"),
            ["type"] = relationship.Type,
            ["source"] = StringProperty(relationship.Properties, "sourceId"),
            ["target"] = StringProperty(relationship.Properties, "targetId"),
        },
        IPath path => new
        {
            nodes = path.Nodes.Select(node => StringProperty(node.Properties, "id")).ToList(),
            relationships = path.Relationships
                .Select(relationship => StringProperty(relationship.Properties, "id"))
                .ToList(),
        },
        IDictionary<string, object> dictionary => dictionary.ToDictionary(
            pair => pair.Key,
            pair => ToSafeTableValue(pair.Value),
            StringComparer.Ordinal),
        IEnumerable<object> values => values.Select(ToSafeTableValue).ToList(),
        _ => value,
    };

    private static GraphVisualNodeV3 MapVisualNode(INode node, int degree) =>
        MapVisualNode(MapNode(node), degree);

    private static GraphVisualNodeV3 MapVisualNode(GraphNode node, int degree) =>
        new(
            node.Id,
            node.Kind.ToString(),
            node.Role,
            node.Name,
            node.FilePath,
            node.StartLine,
            node.EndLine,
            node.Language,
            degree,
            new Dictionary<string, object?>
            {
                ["role"] = node.Role,
                ["filePath"] = node.FilePath,
                ["startLine"] = node.StartLine,
                ["endLine"] = node.EndLine,
                ["language"] = node.Language,
                ["technology"] = node.Technology,
                ["state"] = node.State,
                ["aliases"] = node.Aliases,
                ["attributes"] = node.Attributes,
                ["evidence"] = node.Evidence,
            });

    private static GraphVisualEdgeV3 MapVisualEdge(IRelationship relationship)
    {
        var edge = MapEdge(relationship);
        return new GraphVisualEdgeV3(
            edge.Id,
            edge.SourceId,
            edge.TargetId,
            RelationshipType(edge.Kind),
            new Dictionary<string, object?>
            {
                ["evidence"] = edge.Evidence,
            });
    }

    private static GraphNode MapNode(INode node)
    {
        var properties = node.Properties;
        return new GraphNode(
            RequiredString(properties, "id"),
            Enum.Parse<GraphNodeKind>(RequiredString(properties, "kind"), ignoreCase: false),
            RequiredString(properties, "role"),
            RequiredString(properties, "name"),
            RequiredString(properties, "searchableText"),
            RequiredString(properties, "language"),
            StringProperty(properties, "technology"),
            RequiredString(properties, "state"),
            DeserializeList(StringProperty(properties, "aliasesJson")),
            StringProperty(properties, "filePath"),
            IntProperty(properties, "startLine"),
            IntProperty(properties, "endLine"),
            DeserializeDictionary(StringProperty(properties, "attributesJson")),
            DeserializeEvidence(StringProperty(properties, "evidenceJson")));
    }

    private static GraphEdge MapEdge(IRelationship relationship)
    {
        var properties = relationship.Properties;
        var kind = ParseRelationshipType(relationship.Type);
        var evidence = DeserializeEvidence(StringProperty(properties, "evidenceJson"));
        var source = relationship.StartNodeElementId;
        var target = relationship.EndNodeElementId;

        // Neo4j relationship API 的 element ID 不是 domain node ID，因此寫入時必須保存 sourceId/targetId。
        // 若遇到舊 staging relationship 缺少這兩個 property，直接拒絕，不以 element ID 冒充 stable ID。
        source = StringProperty(properties, "sourceId") ??
            throw new InvalidOperationException("Neo4j V3 relationship 缺少 sourceId。");
        target = StringProperty(properties, "targetId") ??
            throw new InvalidOperationException("Neo4j V3 relationship 缺少 targetId。");
        return new GraphEdge(
            RequiredString(properties, "id"),
            source,
            kind,
            target,
            evidence);
    }

    private static string RelationshipType(GraphEdgeKind kind) => kind switch
    {
        GraphEdgeKind.RoutesTo => "ROUTES_TO",
        GraphEdgeKind.Handles => "HANDLES",
        GraphEdgeKind.Calls => "CALLS",
        GraphEdgeKind.DispatchesTo => "DISPATCHES_TO",
        GraphEdgeKind.Triggers => "TRIGGERS",
        GraphEdgeKind.Reads => "READS",
        GraphEdgeKind.Writes => "WRITES",
        GraphEdgeKind.MapsTo => "MAPS_TO",
        GraphEdgeKind.DependsOn => "DEPENDS_ON",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "不允許的 V3 EdgeKind。"),
    };

    private static GraphEdgeKind ParseRelationshipType(string type) => type switch
    {
        "ROUTES_TO" => GraphEdgeKind.RoutesTo,
        "HANDLES" => GraphEdgeKind.Handles,
        "CALLS" => GraphEdgeKind.Calls,
        "DISPATCHES_TO" => GraphEdgeKind.DispatchesTo,
        "TRIGGERS" => GraphEdgeKind.Triggers,
        "READS" => GraphEdgeKind.Reads,
        "WRITES" => GraphEdgeKind.Writes,
        "MAPS_TO" => GraphEdgeKind.MapsTo,
        "DEPENDS_ON" => GraphEdgeKind.DependsOn,
        _ => throw new InvalidOperationException($"Neo4j 出現未允許的 relationship type：{type}。"),
    };

    private static string RequiredString(
        IReadOnlyDictionary<string, object> properties,
        string key) =>
        StringProperty(properties, key) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Neo4j V3 property {key} 不可為空。");

    private static string? StringProperty(
        IReadOnlyDictionary<string, object> properties,
        string key) =>
        properties.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static int? IntProperty(
        IReadOnlyDictionary<string, object> properties,
        string key) =>
        properties.TryGetValue(key, out var value) && value is not null
            ? Convert.ToInt32(value)
            : null;

    private static IReadOnlyList<string> DeserializeList(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? []
            : JsonSerializer.Deserialize<IReadOnlyList<string>>(json, JsonOptions) ?? [];

    private static IReadOnlyDictionary<string, string> DeserializeDictionary(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? new Dictionary<string, string>()
            : JsonSerializer.Deserialize<IReadOnlyDictionary<string, string>>(json, JsonOptions) ??
              new Dictionary<string, string>();

    private static IReadOnlyList<GraphEvidence> DeserializeEvidence(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? []
            : JsonSerializer.Deserialize<IReadOnlyList<GraphEvidence>>(json, JsonOptions) ?? [];

    private IAsyncSession OpenWriteSession() =>
        (_driver ?? throw new InvalidOperationException("Neo4j V3 已停用。"))
        .AsyncSession(configuration => configuration
            .WithDatabase(_options.Database)
            .WithDefaultAccessMode(AccessMode.Write));

    private IAsyncSession OpenReadSession() =>
        (_driver ?? throw new InvalidOperationException("Neo4j V3 已停用。"))
        .AsyncSession(configuration => configuration
            .WithDatabase(_options.Database)
            .WithDefaultAccessMode(AccessMode.Read));

    private static void ValidateOptions(GraphRagNeo4jOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Disabled) return;
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Uri);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Username);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Database);
        if (options.ConnectionTimeoutSeconds is < 1 or > 30)
            throw new InvalidOperationException("Neo4j ConnectionTimeoutSeconds 必須介於 1 到 30。");
        if (options.TransactionRetrySeconds is < 1 or > 300)
            throw new InvalidOperationException("Neo4j TransactionRetrySeconds 必須介於 1 到 300。");
        if (options.WriteBatchSize is < 100 or > 20_000)
            throw new InvalidOperationException("Neo4j WriteBatchSize 必須介於 100 到 20000。");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _schemaGate.Dispose();
        foreach (var gate in _projectGates.Values) gate.Dispose();
        if (_driver is not null) await _driver.DisposeAsync();
    }

    private static readonly string[] SchemaStatements =
    [
        """
        CREATE CONSTRAINT graph_entity_identity_v3 IF NOT EXISTS
        FOR (n:GraphEntity)
        REQUIRE (n.projectId, n.graphVersion, n.id) IS UNIQUE
        """,
        """
        CREATE CONSTRAINT project_graph_identity_v3 IF NOT EXISTS
        FOR (p:ProjectGraph)
        REQUIRE p.projectId IS UNIQUE
        """,
        """
        CREATE CONSTRAINT graph_community_identity_v3 IF NOT EXISTS
        FOR (c:CommunityReport)
        REQUIRE (c.projectId, c.graphVersion, c.communityId, c.kind) IS UNIQUE
        """,
        """
        CREATE FULLTEXT INDEX graphEntitySearchV3 IF NOT EXISTS
        FOR (n:GraphEntity)
        ON EACH [n.name, n.searchableText, n.aliasesText]
        """,
    ];

}
