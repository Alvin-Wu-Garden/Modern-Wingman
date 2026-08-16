using System.Collections.Concurrent;
using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using AgentService.Application.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neo4j.Driver;
using AuthorityGraphNode = AgentService.Modules.GraphRAG.FblAuthority.GraphNode;
using AuthorityGraphRelationship = AgentService.Modules.GraphRAG.FblAuthority.GraphRelationship;
using AuthorityGraphNodeKind = AgentService.Modules.GraphRAG.FblAuthority.GraphNodeKind;
using AuthorityGraphRelationshipKind = AgentService.Modules.GraphRAG.FblAuthority.GraphRelationshipKind;
using AuthorityGraphSchema = AgentService.Modules.GraphRAG.FblAuthority.GraphSchema;
using ProjectGraphVersions = AgentService.Modules.GraphRAG.FblAuthority.ProjectGraphVersions;

namespace AgentService.Modules.GraphRAG;

/// <summary>
/// FBL authority graph 的 Neo4j 核心持久化實作，負責 immutable version、
/// active/previous pointer、節點關係批次寫入與版本清理。
/// </summary>
public sealed partial class Neo4jGraphStore : IGraphStore, IAsyncDisposable
{
    // 高連結節點 DETACH DELETE 會為一個節點生成大量 command；
    // 500 節點一批可避免百萬級圖在清理時形成過大 transaction log。
    // 500 筆會讓百萬級舊版本清理產生數千次 transaction；5,000 筆已在 2 GiB
    // Neo4j heap 的完整 FBL 圖實測通過，並可把切版後清理縮短到合理時間。
    private const int CleanupBatchSize = 5_000;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private static readonly Regex UnsafeCypher = new(
        @"\b(CREATE|MERGE|SET|DELETE|DETACH|REMOVE|DROP|ALTER|GRANT|DENY|REVOKE|LOAD\s+CSV|IMPORT|EXPORT|FOREACH|UNION|USE|SHOW|TERMINATE)\b|CALL\s+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex MatchClause = new(
        @"\b(?:OPTIONAL\s+)?MATCH\b(?<pattern>.*?)(?=\bWHERE\b|\bWITH\b|\bRETURN\b|\bOPTIONAL\s+MATCH\b|\bMATCH\b|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant |
        RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex NodePattern = new(
        @"\((?<node>[^()]*)\)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex BoundedLimit = new(
        @"\bLIMIT\s+\$limit\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant |
        RegexOptions.Compiled);

    private readonly GraphRagNeo4jOptions _options;
    private readonly ILogger<Neo4jGraphStore> _logger;
    private readonly IProjectIndexManifestStore _manifests;
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
        IProjectIndexManifestStore manifests,
        ILogger<Neo4jGraphStore> logger)
    {
        _options = options.Value;
        _manifests = manifests;
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
            // 探測逾時必須真正傳入 driver；只在前後檢查 token 仍可能讓
            // VerifyConnectivity 在網路斷線時長時間卡住整個準備階段。
            // Driver 5.x 沒有 cancellationToken 參數，使用 WaitAsync 讓目前
            // request 的逾時可以停止等待；底層連線工作會由 driver 自行收尾。
            await _driver.VerifyConnectivityAsync().WaitAsync(cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Ping 是 readiness 與健康狀態的布林探測。受管 Neo4j 冷啟動期間，
            // Bolt 尚未監聽而回傳 false 是預期流程；若在這個底層方法逐次記錄，
            // 每兩秒重試就會製造大量看似故障的 ServiceUnavailableException。
            // 是否需要記錄成功、等待或最終失敗，交由持有重試語意的 Runtime 決定。
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
            // Neo4j 的 IF NOT EXISTS 會把「相同 schema、不同名稱」視為已存在，
            // 因而略過 V4 named full-text index；查詢端再以 V4 名稱呼叫時便會 500。
            // V4 為破壞式乾淨升級，先移除已知 V3 schema 名稱，再建立並驗證固定 V4 名稱。
            foreach (var statement in LegacySchemaCleanupStatements)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await session.ExecuteWriteAsync(async transaction =>
                {
                    var cursor = await transaction.RunAsync(statement);
                    await cursor.ConsumeAsync();
                });
            }
            foreach (var statement in SchemaStatements)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await session.ExecuteWriteAsync(async transaction =>
                {
                    var cursor = await transaction.RunAsync(statement);
                    await cursor.ConsumeAsync();
                });
            }
            // Full-text index 建立可能先進入 POPULATING；等待完成再把服務標記為 ready，
            // 避免第一個問答請求剛好撞到尚未可查詢的索引。
            await session.ExecuteReadAsync(async transaction =>
            {
                var cursor = await transaction.RunAsync(
                    "CALL db.awaitIndexes($timeoutSeconds)",
                    new { timeoutSeconds = 30 });
                await cursor.ConsumeAsync();
                return true;
            });
            await session.ExecuteReadAsync(async transaction =>
            {
                var cursor = await transaction.RunAsync(
                    """
                    SHOW INDEXES
                    YIELD name, state
                    WHERE name IN [
                        'graph_entity_search_v4',
                        'graph_community_v4_search'
                    ]
                    RETURN name, state
                    """);
                var indexes = await cursor.ToListAsync(record => (
                    Name: record["name"].As<string>(),
                    State: record["state"].As<string>()));
                var online = indexes
                    .Where(index => index.State.Equals(
                        "ONLINE", StringComparison.OrdinalIgnoreCase))
                    .Select(index => index.Name)
                    .ToHashSet(StringComparer.Ordinal);
                if (!online.Contains("graph_entity_search_v4") ||
                    !online.Contains("graph_community_v4_search"))
                    throw new InvalidOperationException(
                        "Neo4j V4 full-text indexes 尚未 ONLINE，禁止開放問答。");
                return true;
            });
            _schemaReady = true;
        }
        finally
        {
            _schemaGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task PublishAsync(
        FblGraphSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await StageVersionAsync(snapshot, cancellationToken);
        await PromoteVersionAsync(snapshot, cancellationToken);
    }

    /// <inheritdoc />
    public Task FinalizePublishedVersionAsync(
        string projectId,
        CancellationToken cancellationToken = default) =>
        FinalizeActiveVersionAsync(projectId, cancellationToken);

    /// <summary>
    /// 發布後本機 manifest 或專案狀態寫入失敗時的補償交易。
    /// 先把 anchor 指回舊版本，再刪除本次候選節點；沒有舊版本時刪除整個空專案圖。
    /// </summary>
    public async Task RollbackPublishedVersionAsync(
        string projectId,
        string publishedVersion,
        string? previousVersion,
        CancellationToken cancellationToken = default)
    {
        if (_driver is null)
            return;

        await DeleteVersionAsync(projectId, publishedVersion, cancellationToken);
    }

    /// <inheritdoc />
    public async Task StageVersionAsync(
        FblGraphSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateAuthoritySnapshot(snapshot);
        await EnsureSchemaAsync(cancellationToken);
        var gate = _projectGates.GetOrAdd(
            snapshot.ProjectId,
            _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            await DeleteVersionAsync(
                snapshot.ProjectId,
                snapshot.GraphVersion,
                cancellationToken);
            try
            {
                await WriteNodesAsync(snapshot, cancellationToken);
                await WriteEdgesAsync(snapshot, cancellationToken);
                await SaveCommunityTemplatesAsync(
                    snapshot.ProjectId,
                    snapshot.GraphVersion,
                    snapshot.Communities,
                    cancellationToken);
                await ValidateStagingAsync(snapshot, cancellationToken);
            }
            catch
            {
                await DeleteVersionAsync(
                    snapshot.ProjectId,
                    snapshot.GraphVersion,
                    CancellationToken.None);
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task PromoteVersionAsync(
        FblGraphSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var validation = await ValidateVersionAsync(
            snapshot.ProjectId,
            snapshot.GraphVersion,
            snapshot.Document.Nodes.Count,
            snapshot.Document.Relationships.Count,
            cancellationToken);
        if (!validation.IsValid)
            throw new InvalidOperationException(
                $"Neo4j staging 不完整，禁止 Promote：{string.Join("；", validation.Errors)}");
        // active 指標由 Modern Wingman SQLite manifest 保存；Neo4j 不建立 ProjectGraph。
    }

    /// <inheritdoc />
    public async Task<GraphVersionPointers> GetVersionPointersAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var versions = await _manifests.ListSuccessfulAsync(projectId, cancellationToken);
        var active = await _manifests.GetCurrentAsync(projectId, cancellationToken);
        return new GraphVersionPointers(
            projectId,
            active?.Version,
            versions.FirstOrDefault(item => item.Version != active?.Version)?.Version);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GraphVersionPointers>> ListVersionPointersAsync(
        CancellationToken cancellationToken = default)
    {
        return [];
    }

    /// <inheritdoc />
    public async Task<GraphVersionValidation> ValidateVersionAsync(
        string projectId,
        string graphVersion,
        int? expectedNodes = null,
        int? expectedEdges = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(graphVersion);
        if (_driver is null)
            return new GraphVersionValidation(false, 0, 0, ["Neo4j V4 已停用。"]);
        await using var session = OpenReadSession();
        cancellationToken.ThrowIfCancellationRequested();
        return await session.ExecuteReadAsync(async transaction =>
        {
            var cursor = await transaction.RunAsync(
                """
                MATCH (n:GraphEntity {
                    wingmanProjectId: $projectId,
                    graphVersion: $graphVersion
                })
                WITH count(n) AS nodes
                OPTIONAL MATCH (:GraphEntity {
                    wingmanProjectId: $projectId,
                    graphVersion: $graphVersion
                })-[r]->(:GraphEntity {
                    wingmanProjectId: $projectId,
                    graphVersion: $graphVersion
                })
                RETURN nodes, count(r) AS edges
                """,
                new { projectId, graphVersion });
            var record = await cursor.SingleAsync();
            var nodes = record["nodes"].As<int>();
            var edges = record["edges"].As<int>();
            var errors = new List<string>();
            if (expectedNodes is not null && nodes != expectedNodes)
                errors.Add($"node count {nodes}/{expectedNodes}");
            if (expectedEdges is not null && edges != expectedEdges)
                errors.Add($"edge count {edges}/{expectedEdges}");
            return new GraphVersionValidation(errors.Count == 0, nodes, edges, errors);
        });
    }

    /// <inheritdoc />
    public async Task RestorePreviousVersionAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var versions = await _manifests.ListSuccessfulAsync(projectId, cancellationToken);
        var active = await _manifests.GetCurrentAsync(projectId, cancellationToken);
        var previous = versions.FirstOrDefault(item => item.Version != active?.Version)
            ?? throw new InvalidOperationException($"專案沒有可回復的 previous graphVersion：{projectId}。");
        await _manifests.ActivateAsync(projectId, previous.Version, cancellationToken);
    }

    /// <inheritdoc />
    public async Task FinalizeActiveVersionAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        if (_driver is null) return;
        await CleanupRetiredVersionsAsync(projectId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<string?> GetActiveManifestAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        return (await _manifests.GetCurrentAsync(projectId, cancellationToken))?.Version;
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
                    MATCH (n:GraphEntity {wingmanProjectId: $projectId})
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
                  AND (n.projectId = $projectId OR n.wingmanProjectId = $projectId)
                DETACH DELETE n
                """,
                new { projectId });
            await cursor.ConsumeAsync();
        });
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
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GraphSearchHit>> SearchAsync(
        string projectId,
        string query,
        int limit,
        CancellationToken cancellationToken = default) =>
        await SearchAsync(
            projectId,
            query,
            limit,
            graphVersion: null,
            cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<GraphSearchHit>> SearchAsync(
        string projectId,
        string query,
        int limit,
        string? graphVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        limit = Math.Clamp(limit, 1, 100);
        if (_driver is null)
        {
            // 未建立 driver 是基礎設施不可用，不是「沒有命中」；若回傳空集合，
            // Agent 會誤判查詢成功並浪費額外的 rewrite／工具呼叫。
            throw new GraphStoreException(
                GraphStoreFailureKind.Unavailable,
                "Neo4j 尚未建立連線，無法執行 Graph 檢索。");
        }
        if (graphVersion is not null && string.IsNullOrWhiteSpace(graphVersion))
            throw new GraphStoreException(
                GraphStoreFailureKind.SnapshotNotFound,
                "Graph 檢索要求的 graphVersion 不可為空。 ");
        graphVersion ??= await GetActiveManifestAsync(projectId, cancellationToken);
        if (graphVersion is null)
            return [];
        await using var session = OpenReadSession();
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await session.ExecuteReadAsync(async transaction =>
            {
                var snapshotVersion = graphVersion;

                // Neo4j full-text index 會同時包含其他專案及 retained versions。
                // 固定 global LIMIT 會讓目標 snapshot 永遠沒有機會進入候選，
                // 因此按 score 分頁；graphVersion 由呼叫端固定傳入，不再每頁重讀 active。
                var candidates = new List<GraphSearchHit>();
                var requiredCandidates = Math.Min(
                    MaximumScopedSearchCandidates,
                    Math.Max(limit * 20, 100));
                var skip = 0;
                while (candidates.Count < requiredCandidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var cursor = await transaction.RunAsync(
                        """
                        CALL db.index.fulltext.queryNodes(
                            'graph_entity_search_v4',
                            $query,
                            {skip: $skip, limit: $pageSize})
                        YIELD node, score
                        RETURN node, score
                        """,
                        new
                        {
                            query,
                            skip,
                            pageSize = FullTextPageSize,
                        });
                    var pageCount = 0;
                    while (await cursor.FetchAsync())
                    {
                        pageCount++;
                        var neo4jNode = cursor.Current["node"].As<INode>();
                        if (!IsActiveSearchScope(
                                neo4jNode,
                                projectId,
                                snapshotVersion))
                            continue;
                        candidates.Add(new GraphSearchHit(
                            MapNode(neo4jNode),
                            cursor.Current["score"].As<double>()));
                    }
                    skip += pageCount;
                    if (pageCount < FullTextPageSize)
                        break;
                }
                return DiversifySearchHits(candidates, limit);
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
                "Neo4j 無法提供 Graph 檢索，請確認服務是否已啟動。",
                exception);
        }
        catch (ClientException exception) when (IsFullTextSchemaFailure(exception))
        {
            throw new GraphStoreException(
                GraphStoreFailureKind.SchemaNotReady,
                "Neo4j Graph full-text index 尚未建立或未 ONLINE，請先執行 EnsureSchemaAsync。",
                exception);
        }
        catch (ClientException exception)
        {
            throw new GraphStoreException(
                GraphStoreFailureKind.QueryFailed,
                "Neo4j Graph 檢索查詢失敗。",
                exception);
        }
        catch (Exception exception)
        {
            throw new GraphStoreException(
                GraphStoreFailureKind.QueryFailed,
                "Neo4j Graph 檢索查詢失敗。",
                exception);
        }
    }

    /// <summary>辨識 full-text index 不存在／尚未完成時的 Neo4j 錯誤。</summary>
    private static bool IsFullTextSchemaFailure(ClientException exception)
    {
        var text = exception.Message;
        return text.Contains("graph_entity_search_v4", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("fulltext", StringComparison.OrdinalIgnoreCase) &&
               (text.Contains("index", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("does not exist", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Full-text 高分候選常被大量 Menu 或 Code 型別壟斷。以 NodeKind 做 deterministic
    /// round-robin，讓 Feature／EntryPoint／Code／Data 都有機會成為 seed；
    /// 最終數量仍嚴格受 limit 限制，不會放大檢索圖。
    /// </summary>
    internal static IReadOnlyList<GraphSearchHit> DiversifySearchHits(
        IReadOnlyList<GraphSearchHit> candidates,
        int limit)
    {
        limit = Math.Clamp(limit, 1, 100);
        var groups = candidates
            .GroupBy(hit => hit.Node.Kind)
            .Select(group => group
                .OrderByDescending(hit => hit.Score)
                .ThenBy(hit => hit.Node.Key, StringComparer.Ordinal)
                .ToList())
            .OrderByDescending(group => group[0].Score)
            .ThenBy(group => group[0].Node.Kind)
            .ToList();
        var selected = new List<GraphSearchHit>(Math.Min(limit, candidates.Count));
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
    public async Task<IReadOnlyList<GraphSearchHit>> GetCentralNodesAsync(
        string projectId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        limit = Math.Clamp(limit, 1, 500);
        if (_driver is null) return [];
        var graphVersion = await GetActiveManifestAsync(projectId, cancellationToken);
        if (graphVersion is null) return [];
        await using var session = OpenReadSession();
        cancellationToken.ThrowIfCancellationRequested();
        return await session.ExecuteReadAsync(async transaction =>
        {
            var cursor = await transaction.RunAsync(
                """
                MATCH (node:GraphEntity {
                    wingmanProjectId: $projectId,
                    graphVersion: $graphVersion
                })
                OPTIONAL MATCH (node)-[relationship]-(:GraphEntity {
                    wingmanProjectId: $projectId,
                    graphVersion: $graphVersion
                })
                RETURN node, count(relationship) AS degree
                ORDER BY
                    CASE
                        WHEN node:MenuItem THEN 0
                        WHEN node:ApiEndpoint THEN 1
                        WHEN node:Method THEN 2
                        WHEN node:Type THEN 3
                        WHEN 'DatabaseObject' THEN 4
                        ELSE 5
                    END,
                    degree DESC,
                    node.id
                LIMIT $limit
                """,
                new { projectId, graphVersion, limit });
            var result = new List<GraphSearchHit>();
            while (await cursor.FetchAsync())
                result.Add(new GraphSearchHit(
                    MapNode(cursor.Current["node"].As<INode>()),
                    cursor.Current["degree"].As<double>()));
            return (IReadOnlyList<GraphSearchHit>)result;
        });
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GraphSearchHit>> ListNodesByKindAsync(
        string projectId,
        string kind,
        string? nameFilter,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var graphVersion = await GetActiveManifestAsync(projectId, cancellationToken);
        if (string.IsNullOrWhiteSpace(graphVersion))
            return [];
        return await ListNodesByKindAsync(
            projectId,
            kind,
            nameFilter,
            limit,
            graphVersion,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GraphSearchHit>> ListNodesByKindAsync(
        string projectId,
        string kind,
        string? nameFilter,
        int limit,
        string? graphVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        limit = Math.Clamp(limit, 1, 200);
        if (_driver is null || string.IsNullOrWhiteSpace(graphVersion)) return [];
        await using var session = OpenReadSession();
        cancellationToken.ThrowIfCancellationRequested();
        return await session.ExecuteReadAsync(async transaction =>
        {
            var cursor = await transaction.RunAsync(
                """
                MATCH (node:GraphEntity {
                    wingmanProjectId: $projectId,
                    graphVersion: $graphVersion
                })
                WHERE $kind IN labels(node)
                  AND ($nameFilter IS NULL
                    OR toLower(coalesce(node.name, node.description, node.id))
                        CONTAINS toLower($nameFilter))
                RETURN node
                ORDER BY coalesce(node.name, node.description, node.id)
                LIMIT $limit
                """,
                new { projectId, graphVersion, kind, nameFilter, limit });
            var result = new List<GraphSearchHit>();
            while (await cursor.FetchAsync())
                result.Add(new GraphSearchHit(
                    MapNode(cursor.Current["node"].As<INode>()),
                    1.0));
            return (IReadOnlyList<GraphSearchHit>)result;
        });
    }

    /// <inheritdoc />
    public async Task<(int Nodes, int Edges)> GetStatsAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        if (_driver is null) return (0, 0);
        var graphVersion = await GetActiveManifestAsync(projectId, cancellationToken);
        if (graphVersion is null) return (0, 0);
        await using var session = OpenReadSession();
        cancellationToken.ThrowIfCancellationRequested();
        return await session.ExecuteReadAsync(async transaction =>
        {
            var cursor = await transaction.RunAsync(
                """
                OPTIONAL MATCH (n:GraphEntity {
                    wingmanProjectId: $projectId,
                    graphVersion: $graphVersion
                })
                WITH count(n) AS nodes
                OPTIONAL MATCH (:GraphEntity {
                    wingmanProjectId: $projectId,
                    graphVersion: $graphVersion
                })-[r]->(:GraphEntity {
                    wingmanProjectId: $projectId,
                    graphVersion: $graphVersion
                })
                RETURN nodes, count(r) AS edges
                """,
                new { projectId, graphVersion });
            // 尚未索引或已刪除的專案沒有任何 GraphEntity；
            // 統計 API 必須回傳零，而不是讓空 cursor 的 SingleAsync 變成 500。
            if (!await cursor.FetchAsync()) return (0, 0);
            var record = cursor.Current;
            return (record["nodes"].As<int>(), record["edges"].As<int>());
        });
    }

    /// <summary>
    /// 將 authority node 直接投影成 :GraphEntity 加精確 authority label。
    /// envelope 欄位供版本隔離與 UI 使用；原始 Properties 同時保留為 Neo4j property 與 JSON，避免舊模型轉譯失真。
    /// </summary>
    private async Task WriteNodesAsync(
        FblGraphSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await using var session = OpenWriteSession();
        foreach (var kindGroup in snapshot.Document.Nodes.GroupBy(node => node.Kind))
        {
            var authorityLabel = AuthorityGraphSchema.GetNodeLabel(kindGroup.Key);
            foreach (var batch in kindGroup.Chunk(_options.WriteBatchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rows = batch.Select(node => new Dictionary<string, object?>
                {
                    ["id"] = node.Key,
                    ["domainProperties"] = ToNeo4jProperties(node.Properties),
                }).ToList();
                await session.ExecuteWriteAsync(async transaction =>
                {
                    var cursor = await transaction.RunAsync(
                    $$"""
                    UNWIND $rows AS row
                    CREATE (n:GraphEntity:{{authorityLabel}} {
                        wingmanProjectId: $projectId,
                        graphVersion: $graphVersion,
                        id: row.id
                    })
                    SET n += row.domainProperties
                    """,
                    new
                    {
                        projectId = snapshot.ProjectId,
                        graphVersion = snapshot.GraphVersion,
                        rows,
                    });
                    await cursor.ConsumeAsync();
                });
            }
        }
    }

    /// <summary>依 authority relationship enum 分組，使用其唯一外部名稱建立 Neo4j relationship。</summary>
    private async Task WriteEdgesAsync(
        FblGraphSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await using var session = OpenWriteSession();
        foreach (var kindGroup in snapshot.Document.Relationships.GroupBy(edge => edge.Kind))
        {
            var relationshipType = RelationshipType(kindGroup.Key);
            foreach (var batch in kindGroup.Chunk(_options.WriteBatchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rows = batch.Select(edge => new
                {
                    SourceId = edge.SourceKey,
                    TargetId = edge.TargetKey,
                    DomainProperties = ToNeo4jProperties(edge.Properties),
                }).ToList();
                await session.ExecuteWriteAsync(async transaction =>
                {
                    var cursor = await transaction.RunAsync(
                        $$"""
                        UNWIND $rows AS row
                        MATCH (source:GraphEntity {
                            wingmanProjectId: $projectId,
                            graphVersion: $graphVersion,
                            id: row.SourceId
                        })
                        MATCH (target:GraphEntity {
                            wingmanProjectId: $projectId,
                            graphVersion: $graphVersion,
                            id: row.TargetId
                        })
                        CREATE (source)-[relationship:{{relationshipType}}]->(target)
                        SET relationship += row.DomainProperties
                        """,
                        new
                        {
                            projectId = snapshot.ProjectId,
                            graphVersion = snapshot.GraphVersion,
                            rows,
                        });
                    await cursor.ConsumeAsync();
                });
            }
        }
    }

    private async Task ValidateStagingAsync(
        FblGraphSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await using var session = OpenReadSession();
        cancellationToken.ThrowIfCancellationRequested();
        var validation = await session.ExecuteReadAsync(async transaction =>
        {
            var cursor = await transaction.RunAsync(
                """
                MATCH (n:GraphEntity {
                    wingmanProjectId: $projectId,
                    graphVersion: $graphVersion
                })
                WITH count(n) AS nodes
                OPTIONAL MATCH (:GraphEntity {
                    wingmanProjectId: $projectId,
                    graphVersion: $graphVersion
                })-[r]->(:GraphEntity {
                    wingmanProjectId: $projectId,
                    graphVersion: $graphVersion
                })
                RETURN nodes,
                       count(r) AS edges,
                       collect(DISTINCT type(r)) AS relationshipTypes
                """,
                new
                {
                    projectId = snapshot.ProjectId,
                    graphVersion = snapshot.GraphVersion,
                });
            var record = await cursor.SingleAsync();
            return new
            {
                Nodes = record["nodes"].As<int>(),
                Edges = record["edges"].As<int>(),
                RelationshipTypes = record["relationshipTypes"]
                    .As<List<string>>()
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToList(),
            };
        });
        var expectedRelationshipTypes = snapshot.Document.Relationships
            .Select(edge => RelationshipType(edge.Kind))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
        if (validation.Nodes != snapshot.Document.Nodes.Count ||
            validation.Edges != snapshot.Document.Relationships.Count ||
            !validation.RelationshipTypes.SequenceEqual(
                expectedRelationshipTypes, StringComparer.Ordinal))
            throw new InvalidOperationException(
                $"Neo4j staging 驗證不一致：nodes {validation.Nodes}/{snapshot.Document.Nodes.Count}, " +
                $"edges {validation.Edges}/{snapshot.Document.Relationships.Count}。");
    }

    /// <inheritdoc />
    public async Task DeleteVersionAsync(
        string projectId,
        string graphVersion,
        CancellationToken cancellationToken)
    {
        if (_driver is null) throw new InvalidOperationException("Neo4j V4 已停用。");
        var pointers = await GetVersionPointersAsync(projectId, cancellationToken);
        if (string.Equals(
                pointers.ActiveVersion,
                graphVersion,
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"禁止刪除目前 active Graph 版本：{projectId}/{graphVersion}。");
        await using var session = OpenWriteSession();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var deleted = await session.ExecuteWriteAsync(async transaction =>
            {
                var cursor = await transaction.RunAsync(
                    $$"""
                    MATCH (n:GraphEntity {
                        wingmanProjectId: $projectId,
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
        await session.ExecuteWriteAsync(async transaction =>
        {
            var cursor = await transaction.RunAsync(
                """
                MATCH (c:GraphCommunity {
                    wingmanProjectId: $projectId,
                    graphVersion: $graphVersion
                })
                DELETE c
                """,
                new { projectId, graphVersion });
            await cursor.ConsumeAsync();
        });
    }

    private async Task CleanupRetiredVersionsAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        var retainedVersions = (await _manifests.ListSuccessfulAsync(projectId, cancellationToken))
            .Select(item => item.Version)
            .ToArray();
        await using var session = OpenWriteSession();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var deleted = await session.ExecuteWriteAsync(async transaction =>
            {
                var cursor = await transaction.RunAsync(
                    $$"""
                    MATCH (n:GraphEntity {wingmanProjectId: $projectId})
                    WHERE NOT n.graphVersion IN $retainedVersions
                    WITH n LIMIT {{CleanupBatchSize}}
                    DETACH DELETE n
                    RETURN count(*) AS deleted
                    """,
                    new { projectId, retainedVersions });
                return (await cursor.SingleAsync())["deleted"].As<int>();
            });
            if (deleted == 0) break;
        }
        await session.ExecuteWriteAsync(async transaction =>
        {
            var cursor = await transaction.RunAsync(
                """
                MATCH (c:GraphCommunity {wingmanProjectId: $projectId})
                WHERE NOT c.graphVersion IN $retainedVersions
                DELETE c
                """,
                new { projectId, retainedVersions });
            await cursor.ConsumeAsync();
        });
    }

    /// <summary>
    /// 在資料庫端先套用 incoming／outgoing 方向，再執行 LIMIT。
    /// 此方法只接受已正規化的 in 或 out，查詢字串不接收任何外部可注入片段。
    /// </summary>
    private async Task<IReadOnlyList<GraphNeighbor>> GetDirectionalNeighborsAsync(
        string projectId,
        string nodeId,
        int limit,
        string direction,
        CancellationToken cancellationToken)
    {
        if (_driver is null) return [];
        if (direction is not ("in" or "out"))
            throw new ArgumentException("方向查詢只允許 in 或 out。", nameof(direction));
        var graphVersion = await GetActiveManifestAsync(projectId, cancellationToken);
        if (graphVersion is null) return [];

        var cypher = direction == "in"
            ? """
              MATCH (neighbor:GraphEntity {
                  wingmanProjectId: $projectId,
                  graphVersion: $graphVersion
              })-[relationship]->(center:GraphEntity {
                  wingmanProjectId: $projectId,
                  graphVersion: $graphVersion,
                  id: $nodeId
              })
              RETURN neighbor, relationship,
                     startNode(relationship).id AS sourceId,
                     endNode(relationship).id AS targetId
              ORDER BY type(relationship), neighbor.id
              LIMIT $limit
              """
            : """
              MATCH (center:GraphEntity {
                  wingmanProjectId: $projectId,
                  graphVersion: $graphVersion,
                  id: $nodeId
              })-[relationship]->(neighbor:GraphEntity {
                  wingmanProjectId: $projectId,
                  graphVersion: $graphVersion
              })
              RETURN neighbor, relationship,
                     startNode(relationship).id AS sourceId,
                     endNode(relationship).id AS targetId
              ORDER BY type(relationship), neighbor.id
              LIMIT $limit
              """;

        await using var session = OpenReadSession();
        cancellationToken.ThrowIfCancellationRequested();
        return await session.ExecuteReadAsync(async transaction =>
        {
            var cursor = await transaction.RunAsync(
                cypher,
                new { projectId, graphVersion, nodeId, limit });
            var result = new List<GraphNeighbor>();
            while (await cursor.FetchAsync())
            {
                result.Add(new GraphNeighbor(
                    MapNode(cursor.Current["neighbor"].As<INode>()),
                    MapEdge(
                        cursor.Current["relationship"].As<IRelationship>(),
                        cursor.Current["sourceId"].As<string>(),
                        cursor.Current["targetId"].As<string>()),
                    direction == "in" ? "incoming" : "outgoing"));
            }
            return (IReadOnlyList<GraphNeighbor>)result;
        });
    }

    /// <summary>
    /// 取得中心節點所在檔案的可視化子圖。
    /// 查詢同時限制 projectId 與 active graphVersion，並只回傳所選節點之間的有效關係。
    /// </summary>
    /// <param name="projectId">專案識別碼。</param>
    /// <param name="nodeIds">作為檔案來源的中心節點 IDs。</param>
    /// <param name="limit">最多載入的節點數量。</param>
    /// <param name="cancellationToken">取消權杖。</param>
    /// <returns>同檔案節點、其內部關係與截斷統計。</returns>
    private async Task<GraphVisualData> GetSameFileVisualGraphAsync(
        string projectId,
        IReadOnlyList<string> nodeIds,
        int limit,
        CancellationToken cancellationToken)
    {
        if (_driver is null) return new([], [], 0, 0, 0, false);
        var graphVersion = await GetActiveManifestAsync(projectId, cancellationToken);
        if (graphVersion is null) return new([], [], 0, 0, 0, false);

        var centerIds = nodeIds
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (centerIds.Count == 0) return new([], [], 0, 0, 0, false);

        await using var session = OpenReadSession();
        cancellationToken.ThrowIfCancellationRequested();
        return await session.ExecuteReadAsync(async transaction =>
        {
            // 先從 active graph 的中心節點解析檔案路徑，禁止信任前端直接提供路徑。
            var fileCursor = await transaction.RunAsync(
                """
                MATCH (center:GraphEntity {
                    wingmanProjectId: $projectId,
                    graphVersion: $graphVersion
                })
                WHERE center.id IN $centerIds
                  AND center.filePath IS NOT NULL
                  AND trim(center.filePath) <> ''
                RETURN DISTINCT center.filePath AS filePath
                ORDER BY filePath
                """,
                new { projectId, graphVersion, centerIds });
            var filePaths = new List<string>();
            while (await fileCursor.FetchAsync())
                filePaths.Add(fileCursor.Current["filePath"].As<string>());

            // 即使中心節點沒有 filePath，也要保留中心本身，讓使用者看得到展開起點；
            // 只有具備 filePath 的中心才會帶出同檔案的其他節點。
            var nodeCursor = await transaction.RunAsync(
                """
                MATCH (node:GraphEntity {
                    wingmanProjectId: $projectId,
                    graphVersion: $graphVersion
                })
                WHERE node.id IN $centerIds OR
                      (size($filePaths) > 0 AND node.filePath IN $filePaths)
                OPTIONAL MATCH (node)-[relationship]-(:GraphEntity {
                    wingmanProjectId: $projectId,
                    graphVersion: $graphVersion
                })
                WITH node, count(relationship) AS degree,
                    CASE WHEN node.id IN $centerIds THEN 0 ELSE 1 END AS centerPriority
                ORDER BY centerPriority, degree DESC, node.id
                LIMIT $limit
                RETURN node, degree
                """,
                new { projectId, graphVersion, centerIds, filePaths, limit });
            var visualNodes = new List<GraphVisualNode>();
            while (await nodeCursor.FetchAsync())
            {
                visualNodes.Add(MapVisualNode(
                    nodeCursor.Current["node"].As<INode>(),
                    nodeCursor.Current["degree"].As<int>()));
            }

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
                    WHERE source.id IN $ids AND target.id IN $ids
                    RETURN relationship, source.id AS sourceId, target.id AS targetId
                    ORDER BY relationship.id
                    LIMIT $edgeLimit
                    """,
                    new
                    {
                        projectId,
                        graphVersion,
                        ids,
                        edgeLimit = Math.Min(limit * 4, 20_000),
                    });
                while (await edgeCursor.FetchAsync())
                {
                    visualEdges.Add(MapVisualEdge(
                        edgeCursor.Current["relationship"].As<IRelationship>(),
                        edgeCursor.Current["sourceId"].As<string>(),
                        edgeCursor.Current["targetId"].As<string>()));
                }
            }

            var countCursor = await transaction.RunAsync(
                """
                MATCH (node:GraphEntity {
                    wingmanProjectId: $projectId,
                    graphVersion: $graphVersion
                })
                WITH node,
                    CASE WHEN node.id IN $centerIds OR
                              (size($filePaths) > 0 AND node.filePath IN $filePaths)
                         THEN 1 ELSE 0 END AS eligible
                RETURN count(node) AS total, sum(eligible) AS eligibleTotal
                """,
                new { projectId, graphVersion, centerIds, filePaths });
            var counts = await countCursor.SingleAsync();
            var total = counts["total"].As<int>();
            var eligibleTotal = counts["eligibleTotal"].As<int>();
            return new GraphVisualData(
                visualNodes,
                visualEdges,
                total,
                visualNodes.Count,
                visualEdges.Count,
                eligibleTotal > visualNodes.Count);
        });
    }

    private async Task<GraphVisualNode?> ReadNodeByIdAsync(
        string projectId,
        string nodeId,
        CancellationToken cancellationToken)
    {
        if (_driver is null) return null;
        var graphVersion = await GetActiveManifestAsync(projectId, cancellationToken);
        if (graphVersion is null) return null;
        await using var session = OpenReadSession();
        cancellationToken.ThrowIfCancellationRequested();
        return await session.ExecuteReadAsync(async transaction =>
        {
            var cursor = await transaction.RunAsync(
                """
                MATCH (node:GraphEntity {
                    wingmanProjectId: $projectId,
                    graphVersion: $graphVersion,
                    id: $nodeId
                })
                OPTIONAL MATCH (node)-[relationship]-(:GraphEntity {
                    wingmanProjectId: $projectId,
                    graphVersion: $graphVersion
                })
                RETURN node, count(relationship) AS degree
                """,
                new { projectId, graphVersion, nodeId });
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
        var allowed = Enum.GetNames<AuthorityGraphNodeKind>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = kinds.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var invalid = result.FirstOrDefault(value => !allowed.Contains(value));
        if (invalid is not null)
            throw new ArgumentException($"不允許的 V4 NodeKind filter：{invalid}。");
        return result.Select(value =>
                Enum.Parse<AuthorityGraphNodeKind>(value, ignoreCase: true).ToString())
            .ToList();
    }

    private static IReadOnlyList<string> NormalizeRelationships(
        IReadOnlyList<string>? relationships)
    {
        if (relationships is null || relationships.Count == 0) return [];
        var allowed = Enum.GetValues<AuthorityGraphRelationshipKind>()
            .ToDictionary(RelationshipType, kind => kind, StringComparer.OrdinalIgnoreCase);
        var result = relationships.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var invalid = result.FirstOrDefault(value => !allowed.ContainsKey(value));
        if (invalid is not null)
            throw new ArgumentException($"不允許的 V4 relationship filter：{invalid}。");
        return result.Select(value => RelationshipType(allowed[value])).ToList();
    }

    /// <summary>
    /// 驗證 UI 提供的 Cypher 僅能讀取同一個 active V4 graph。
    /// 每一個 MATCH node pattern 都必須明確使用 GraphEntity、wingmanProjectId 與 graphVersion，
    /// 並強制使用服務端提供的 $limit，避免只在 client 停止讀取但資料庫仍執行無界查詢。
    /// </summary>
    /// <param name="cypher">使用者輸入的單一 read-only statement。</param>
    /// <returns>通過隔離與 bounded validation 的原始 statement。</returns>
    internal static string EnsureReadOnlyCypher(string cypher)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cypher);
        if (UnsafeCypher.IsMatch(cypher))
            throw new InvalidOperationException("只允許 read-only V4 Cypher，禁止寫入與 procedure call。");
        if (cypher.Contains(';') ||
            cypher.Contains("//", StringComparison.Ordinal) ||
            cypher.Contains("/*", StringComparison.Ordinal))
            throw new InvalidOperationException("V4 Cypher 一次只允許一個 statement。");
        if (!BoundedLimit.IsMatch(cypher))
            throw new InvalidOperationException(
                "V4 Cypher 必須使用 LIMIT $limit，才能由服務端套用 bounded budget。");

        var matches = MatchClause.Matches(cypher);
        if (matches.Count == 0)
            throw new InvalidOperationException(
                "V4 Cypher 至少需要一個受 project/version 限制的 MATCH。");
        foreach (Match match in matches)
        {
            var nodePatterns = NodePattern.Matches(match.Groups["pattern"].Value);
            if (nodePatterns.Count == 0)
                throw new InvalidOperationException("MATCH 必須包含明確的 GraphEntity pattern。");
            foreach (Match nodePattern in nodePatterns)
            {
                var node = nodePattern.Groups["node"].Value;
                if (!node.Contains(":GraphEntity", StringComparison.Ordinal) ||
                    !node.Contains("wingmanProjectId", StringComparison.Ordinal) ||
                    !node.Contains("$projectId", StringComparison.Ordinal) ||
                    !node.Contains("$graphVersion", StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "每個 MATCH node 都必須使用 :GraphEntity，並以 wingmanProjectId=$projectId、graphVersion=$graphVersion 限制 active graph。");
            }
        }
        return cypher;
    }

    private static void CollectGraphValues(
        object? value,
        IDictionary<string, GraphVisualNode> nodes,
        IDictionary<string, GraphVisualEdge> edges,
        IDictionary<string, string> elementNodeIds)
    {
        switch (value)
        {
            case INode node when node.Labels.Contains("GraphEntity"):
                {
                    var mapped = MapVisualNode(node, 0);
                    nodes[mapped.Id] = mapped;
                    elementNodeIds[node.ElementId] = mapped.Id;
                    break;
                }
            case IRelationship relationship:
                {
                    if (elementNodeIds.TryGetValue(
                            relationship.StartNodeElementId,
                            out var sourceId) &&
                        elementNodeIds.TryGetValue(
                            relationship.EndNodeElementId,
                            out var targetId))
                    {
                        var mapped = MapVisualEdge(relationship, sourceId, targetId);
                        edges[mapped.Id] = mapped;
                    }
                    break;
                }
            case IPath path:
                foreach (var node in path.Nodes)
                    CollectGraphValues(node, nodes, edges, elementNodeIds);
                foreach (var relationship in path.Relationships)
                    CollectGraphValues(relationship, nodes, edges, elementNodeIds);
                break;
            case IEnumerable<object> values:
                {
                    var buffered = values.ToList();
                    foreach (var node in buffered.OfType<INode>())
                        CollectGraphValues(node, nodes, edges, elementNodeIds);
                    foreach (var item in buffered.Where(item => item is not INode))
                        CollectGraphValues(item, nodes, edges, elementNodeIds);
                    break;
                }
        }
    }

    private static object? ToSafeTableValue(object? value) => value switch
    {
        null => null,
        INode node => new Dictionary<string, object?>
        {
            ["id"] = StringProperty(node.Properties, "id"),
            ["kind"] = node.Labels.FirstOrDefault(label =>
                !label.Equals("GraphEntity", StringComparison.Ordinal)),
            ["role"] = StringProperty(node.Properties, "role"),
            ["rawKind"] = StringProperty(node.Properties, "kind"),
            ["rawRole"] = StringProperty(node.Properties, "role"),
            ["name"] = StringProperty(node.Properties, "name"),
            ["filePath"] = StringProperty(node.Properties, "filePath"),
        },
        IRelationship relationship => new Dictionary<string, object?>
        {
            ["id"] = StringProperty(relationship.Properties, "id"),
            ["type"] = relationship.Type,
            // V4 關係不再重複保存 sourceId／targetId；端點由 Neo4j 關係本身決定。
            ["sourceElementId"] = relationship.StartNodeElementId,
            ["targetElementId"] = relationship.EndNodeElementId,
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

    private static GraphVisualNode MapVisualNode(INode node, int degree) =>
        MapVisualNode(MapNode(node), degree);

    private static GraphVisualNode MapVisualNode(AuthorityGraphNode node, int degree) =>
        new(
            node.Key,
            node.Kind.ToString(),
            node.Kind.ToString(),
            DisplayName(node),
            AuthorityStringProperty(node.Properties, "filePath")
                ?? AuthorityStringProperty(node.Properties, "source_file")
                ?? AuthorityStringProperty(node.Properties, "file_path"),
            AuthorityIntProperty(node.Properties, "startLine")
                ?? AuthorityIntProperty(node.Properties, "source_line")
                ?? AuthorityIntProperty(node.Properties, "start_line"),
            AuthorityIntProperty(node.Properties, "endLine")
                ?? AuthorityIntProperty(node.Properties, "end_line"),
            AuthorityStringProperty(node.Properties, "language") ?? string.Empty,
            degree,
            node.Properties);

    private static GraphVisualEdge MapVisualEdge(
        IRelationship relationship,
        string sourceId,
        string targetId)
    {
        var edge = MapEdge(relationship, sourceId, targetId);
        return new GraphVisualEdge(
            edge.Id,
            edge.SourceKey,
            edge.TargetKey,
            RelationshipType(edge.Kind),
            edge.Properties);
    }

    /// <summary>由 :GraphEntity envelope 還原未經簡化的 authority node。</summary>
    private static AuthorityGraphNode MapNode(INode node)
    {
        var properties = node.Properties;
        var kindLabel = node.Labels.FirstOrDefault(label =>
            !label.Equals("GraphEntity", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("GraphEntity 缺少 ParallelExtractor 原始 label。");
        return new AuthorityGraphNode(
            RequiredString(properties, "id"),
            ParseNodeKind(kindLabel),
            ExtractDomainProperties(properties, NodeEnvelopeProperties));
    }

    private static AuthorityGraphNodeKind ParseNodeKind(string value) =>
        AuthorityGraphSchema.TryParseNodeLabel(value, out var kind)
            ? kind
            : throw new InvalidOperationException($"Neo4j 出現未允許的 ParallelExtractor label：{value}。");

    /// <summary>由 Neo4j relationship 還原 authority relationship 與完整 evidence。</summary>
    private static AuthorityGraphRelationship MapEdge(
        IRelationship relationship,
        string sourceId,
        string targetId)
    {
        var properties = relationship.Properties;
        var kind = ParseRelationshipType(relationship.Type);
        return new AuthorityGraphRelationship(
            AuthorityGraphRelationship.Create(kind, sourceId, targetId).Id,
            sourceId,
            targetId,
            kind,
            ExtractDomainProperties(properties, RelationshipEnvelopeProperties));
    }

    private static string RelationshipType(AuthorityGraphRelationshipKind kind) =>
        AuthorityGraphSchema.GetRelationshipType(kind);

    private static AuthorityGraphRelationshipKind ParseRelationshipType(string type)
    {
        foreach (var kind in Enum.GetValues<AuthorityGraphRelationshipKind>())
        {
            if (string.Equals(
                    AuthorityGraphSchema.GetRelationshipType(kind),
                    type,
                    StringComparison.Ordinal))
                return kind;
        }

        throw new InvalidOperationException($"Neo4j 出現未允許的 authority relationship type：{type}。");
    }

    /// <summary>在任何寫入前驗證 authority snapshot 的識別、端點與 schema 完整性。</summary>
    private static void ValidateAuthoritySnapshot(FblGraphSnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.ProjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.GraphVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.ContentDigest);
        ArgumentNullException.ThrowIfNull(snapshot.Document);
        AuthorityGraphSchema.EnsureCompleteMappings();

        var nodeKeys = snapshot.Document.Nodes
            .Select(node => node.Key)
            .ToHashSet(StringComparer.Ordinal);
        if (nodeKeys.Count != snapshot.Document.Nodes.Count)
            throw new InvalidOperationException("FBL authority snapshot 包含重複 node key。");
        if (snapshot.Document.Relationships.Any(edge =>
                !nodeKeys.Contains(edge.SourceKey) || !nodeKeys.Contains(edge.TargetKey)))
            throw new InvalidOperationException("FBL authority snapshot 存在端點缺失的 relationship。");
    }

    /// <summary>取得節點供 UI 與全文索引顯示的穩定短名稱。</summary>
    private static string DisplayName(AuthorityGraphNode node)
    {
        foreach (var key in new[]
                 {
                     "name", "display_name", "full_name", "signature", "qualified_name",
                     "file_path", "project_file", "solution_file", "path", "object_name", "menu_id",
                 })
        {
            if (AuthorityStringProperty(node.Properties, key) is { Length: > 0 } value)
                return value;
        }

        return node.Key;
    }

    /// <summary>
    /// 只把 Neo4j 原生支援的 scalar／scalar array 直接投影，並原名保留
    /// ParallelExtractor 的屬性；Modern Wingman 不額外建立相容欄位。
    /// </summary>
    private static IReadOnlyDictionary<string, object?> ToNeo4jProperties(
        IReadOnlyDictionary<string, object?> properties)
    {
        var reserved = new HashSet<string>(
        [
            "wingmanProjectId", "graphVersion", "id",
        ], StringComparer.Ordinal);
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in properties)
        {
            if (reserved.Contains(key) || string.IsNullOrWhiteSpace(key))
                continue;
            var converted = ToNeo4jValue(value);
            if (converted is not null)
                result[key] = converted;
        }

        return result;
    }

    /// <summary>將 enum、Guid 與 scalar collections 正規化成 Neo4j driver 可接受的值。</summary>
    private static object? ToNeo4jValue(object? value)
    {
        if (value is null) return null;
        if (value is string or bool or byte or short or int or long or float or double or decimal)
            return value;
        if (value is Enum or Guid or DateTime or DateTimeOffset)
            return value.ToString();
        if (value is IEnumerable values and not IDictionary)
        {
            var result = new List<object>();
            foreach (var item in values)
            {
                var converted = ToNeo4jValue(item);
                if (converted is null || converted is IEnumerable and not string)
                    return null;
                result.Add(converted);
            }
            return result;
        }
        return null;
    }

    private static readonly HashSet<string> NodeEnvelopeProperties =
        new(["wingmanProjectId", "graphVersion", "id"], StringComparer.Ordinal);

    private static readonly HashSet<string> RelationshipEnvelopeProperties =
        new(StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, object?> ExtractDomainProperties(
        IReadOnlyDictionary<string, object> properties,
        IReadOnlySet<string> envelope) =>
        properties
            .Where(pair => !envelope.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => (object?)pair.Value, StringComparer.Ordinal);

    /// <summary>由 JSON 還原 authority properties；JSON element 保留精確的 primitive 值。</summary>
    private static IReadOnlyDictionary<string, object?> DeserializeAuthorityProperties(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : JsonSerializer.Deserialize<Dictionary<string, object?>>(json, JsonOptions)
                ?? new Dictionary<string, object?>(StringComparer.Ordinal);

    private static string? AuthorityStringProperty(
        IReadOnlyDictionary<string, object?> properties,
        string key) =>
        properties.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static int? AuthorityIntProperty(
        IReadOnlyDictionary<string, object?> properties,
        string key) =>
        properties.TryGetValue(key, out var value) && value is not null &&
        int.TryParse(value.ToString(), out var number)
            ? number
            : null;

    /// <summary>建立不依賴舊 GraphModel 的穩定 SHA-256 十六進位字串。</summary>
    private static string StableSha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string RequiredString(
        IReadOnlyDictionary<string, object> properties,
        string key) =>
        StringProperty(properties, key) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Neo4j V4 property {key} 不可為空。");

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

    private static bool BoolProperty(
        IReadOnlyDictionary<string, object> properties,
        string key) =>
        properties.TryGetValue(key, out var value) &&
        value is not null &&
        Convert.ToBoolean(value);

    private static double DoubleProperty(
        IReadOnlyDictionary<string, object> properties,
        string key) =>
        properties.TryGetValue(key, out var value) && value is not null
            ? Convert.ToDouble(value)
            : 0;

    private static IReadOnlyList<string> DeserializeList(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? []
            : JsonSerializer.Deserialize<IReadOnlyList<string>>(json, JsonOptions) ?? [];

    private static IReadOnlyDictionary<string, string> DeserializeDictionary(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? new Dictionary<string, string>()
            : JsonSerializer.Deserialize<IReadOnlyDictionary<string, string>>(json, JsonOptions) ??
              new Dictionary<string, string>();

    private IAsyncSession OpenWriteSession() =>
        (_driver ?? throw new InvalidOperationException("Neo4j V4 已停用。"))
        .AsyncSession(configuration => configuration
            .WithDatabase(_options.Database)
            .WithDefaultAccessMode(AccessMode.Write));

    private IAsyncSession OpenReadSession() =>
        (_driver ?? throw new InvalidOperationException("Neo4j V4 已停用。"))
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

    private static readonly string[] VisualPropertyKeys =
    [
        "id",
        "kind",
        "role",
        "name",
        "language",
        "filePath",
        "startLine",
        "endLine",
        "degree",
    ];
}
