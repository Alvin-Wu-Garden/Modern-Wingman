using System.Collections.Concurrent;
using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neo4j.Driver;
using AuthorityGraphNode = AgentService.Modules.GraphRAG.FblAuthority.GraphNode;
using AuthorityGraphRelationship = AgentService.Modules.GraphRAG.FblAuthority.GraphRelationship;
using AuthorityGraphEvidence = AgentService.Modules.GraphRAG.FblAuthority.GraphEvidence;
using AuthorityGraphNodeKind = AgentService.Modules.GraphRAG.FblAuthority.GraphNodeKind;
using AuthorityGraphRelationshipKind = AgentService.Modules.GraphRAG.FblAuthority.GraphRelationshipKind;
using AuthorityGraphSchema = AgentService.Modules.GraphRAG.FblAuthority.GraphSchema;

namespace AgentService.Modules.GraphRAG;

/// <summary>
/// FBL authority graph 的 Neo4j 核心持久化實作，負責 immutable version、
/// active/previous pointer、節點關係批次寫入與版本清理。
/// </summary>
public sealed partial class Neo4jGraphStore : IGraphStore, IAsyncDisposable
{
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
                "Neo4j V4 connectivity check 失敗；已遮蔽連線資訊。ExceptionType={ExceptionType}",
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
        await FinalizeActiveVersionAsync(snapshot.ProjectId, cancellationToken);
    }

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

        if (string.IsNullOrWhiteSpace(previousVersion))
        {
            await DeleteProjectAsync(projectId, cancellationToken);
            return;
        }

        await using var session = OpenWriteSession();
        var restored = await session.ExecuteWriteAsync(async transaction =>
        {
            var cursor = await transaction.RunAsync(
                """
                MATCH (p:ProjectGraph {projectId: $projectId})
                MATCH (old:GraphEntity {projectId: $projectId, graphVersion: $previousVersion})
                WITH p, count(old) AS nodeCount
                OPTIONAL MATCH (oldEdge:GraphEntity {projectId: $projectId, graphVersion: $previousVersion})
                    -[oldRel {graphVersion: $previousVersion}]->
                    (:GraphEntity {projectId: $projectId, graphVersion: $previousVersion})
                WITH p, nodeCount, count(oldRel) AS edgeCount
                SET p.activeManifestVersion = $previousVersion,
                    p.schemaVersion = 'fbl-authority-1',
                    p.nodeCount = nodeCount,
                    p.edgeCount = edgeCount,
                    p.previousManifestVersion = NULL,
                    p.promotedAt = datetime()
                RETURN count(p) AS restored
                """,
                new { projectId, previousVersion });
            return (await cursor.SingleAsync())["restored"].As<int>();
        });
        if (restored != 1)
            throw new InvalidOperationException($"Neo4j 找不到可恢復的上一版圖譜：{projectId}/{previousVersion}");

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
        await PromoteAsync(snapshot, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<GraphVersionPointers> GetVersionPointersAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        if (_driver is null)
            return new GraphVersionPointers(projectId, null, null);
        await using var session = OpenReadSession();
        cancellationToken.ThrowIfCancellationRequested();
        return await session.ExecuteReadAsync(async transaction =>
        {
            var cursor = await transaction.RunAsync(
                """
                MATCH (p:ProjectGraph {projectId: $projectId})
                RETURN p.activeManifestVersion AS active,
                       p.previousManifestVersion AS previous
                LIMIT 1
                """,
                new { projectId });
            if (!await cursor.FetchAsync())
                return new GraphVersionPointers(projectId, null, null);
            return new GraphVersionPointers(
                projectId,
                cursor.Current["active"].As<string?>(),
                cursor.Current["previous"].As<string?>());
        });
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GraphVersionPointers>> ListVersionPointersAsync(
        CancellationToken cancellationToken = default)
    {
        if (_driver is null) return [];
        await using var session = OpenReadSession();
        cancellationToken.ThrowIfCancellationRequested();
        return await session.ExecuteReadAsync(async transaction =>
        {
            var cursor = await transaction.RunAsync(
                """
                MATCH (p:ProjectGraph)
                RETURN p.projectId AS projectId,
                       p.activeManifestVersion AS active,
                       p.previousManifestVersion AS previous
                ORDER BY p.projectId
                """);
            var pointers = new List<GraphVersionPointers>();
            while (await cursor.FetchAsync())
            {
                pointers.Add(new GraphVersionPointers(
                    cursor.Current["projectId"].As<string>(),
                    cursor.Current["active"].As<string?>(),
                    cursor.Current["previous"].As<string?>()));
            }

            return (IReadOnlyList<GraphVersionPointers>)pointers;
        });
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
                    projectId: $projectId,
                    graphVersion: $graphVersion
                })
                WITH count(n) AS nodes,
                     count(CASE
                         WHEN n.kind IS NOT NULL
                          AND n.propertiesJson IS NOT NULL
                          AND n.degree IS NOT NULL
                         THEN 1 END) AS validNodes
                OPTIONAL MATCH (:GraphEntity {
                    projectId: $projectId,
                    graphVersion: $graphVersion
                })-[r {
                    graphVersion: $graphVersion
                }]->(:GraphEntity {
                    projectId: $projectId,
                    graphVersion: $graphVersion
                })
                RETURN nodes, validNodes, count(r) AS edges,
                     count(CASE
                         WHEN r.id IS NOT NULL
                          AND r.evidenceJson IS NOT NULL
                          AND NOT 'sourceId' IN keys(r)
                          AND NOT 'targetId' IN keys(r)
                         THEN 1 END) AS validEdges
                """,
                new { projectId, graphVersion });
            var record = await cursor.SingleAsync();
            var nodes = record["nodes"].As<int>();
            var validNodes = record["validNodes"].As<int>();
            var edges = record["edges"].As<int>();
            var validEdges = record["validEdges"].As<int>();
            var errors = new List<string>();
            if (validNodes != nodes)
                errors.Add($"authority node properties {validNodes}/{nodes}");
            if (validEdges != edges)
                errors.Add($"authority relationship properties {validEdges}/{edges}");
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
        if (_driver is null)
            throw new InvalidOperationException("Neo4j V4 已停用。");
        await using var session = OpenWriteSession();
        cancellationToken.ThrowIfCancellationRequested();
        var restored = await session.ExecuteWriteAsync(async transaction =>
        {
            var cursor = await transaction.RunAsync(
                """
                MATCH (p:ProjectGraph {projectId: $projectId})
                WHERE p.previousManifestVersion IS NOT NULL
                SET p.activeManifestVersion = p.previousManifestVersion,
                    p.schemaVersion = p.previousSchemaVersion,
                    p.canonicalDigest = p.previousCanonicalDigest,
                    p.nodeCount = p.previousNodeCount,
                    p.edgeCount = p.previousEdgeCount,
                    p.previousManifestVersion = NULL,
                    p.previousSchemaVersion = NULL,
                    p.previousCanonicalDigest = NULL,
                    p.previousNodeCount = NULL,
                    p.previousEdgeCount = NULL,
                    p.promotedAt = datetime()
                RETURN count(p) AS restored
                """,
                new { projectId });
            return (await cursor.SingleAsync())["restored"].As<int>();
        });
        if (restored != 1)
            throw new InvalidOperationException(
                $"Neo4j ProjectGraph 沒有可回復的 previous：{projectId}。");
    }

    /// <inheritdoc />
    public async Task FinalizeActiveVersionAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        if (_driver is null) return;
        await using (var session = OpenWriteSession())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await session.ExecuteWriteAsync(async transaction =>
            {
                var cursor = await transaction.RunAsync(
                    """
                    MATCH (p:ProjectGraph {projectId: $projectId})
                    SET p.previousManifestVersion = NULL,
                        p.previousSchemaVersion = NULL,
                        p.previousCanonicalDigest = NULL,
                        p.previousNodeCount = NULL,
                        p.previousEdgeCount = NULL
                    """,
                    new { projectId });
                await cursor.ConsumeAsync();
            });
        }

        await CleanupRetiredVersionsAsync(projectId, cancellationToken);
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
    public async Task<IReadOnlyList<GraphSearchHit>> SearchAsync(
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
            var manifestCursor = await transaction.RunAsync(
                """
                MATCH (p:ProjectGraph {projectId: $projectId})
                RETURN p.activeManifestVersion AS graphVersion
                """,
                new { projectId });
            if (!await manifestCursor.FetchAsync())
                return [];
            var graphVersion = manifestCursor.Current["graphVersion"].As<string?>();
            if (string.IsNullOrWhiteSpace(graphVersion))
                return [];

            // Neo4j full-text index 會同時包含其他專案及 retained versions。
            // 固定 global LIMIT 會讓目標 active scope 永遠沒有機會進入候選。
            // 這裡按 Lucene score 順序逐頁讀取，直到取得足夠 active 候選或
            // global hit 已耗盡；頁面大小固定，記憶體只保存目標 scope。
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
                            graphVersion))
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
                        WHEN 'Menu' THEN 0
                        WHEN 'Endpoint' THEN 1
                        WHEN 'WebAction' THEN 2
                        WHEN 'CodeClass' THEN 3
                        WHEN 'DatabaseObject' THEN 4
                        ELSE 5
                    END,
                    degree DESC,
                    node.id
                LIMIT $limit
                """,
                new { projectId, limit });
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
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        limit = Math.Clamp(limit, 1, 200);
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
                    graphVersion: p.activeManifestVersion,
                    kind: $kind
                })
                WHERE $nameFilter IS NULL
                    OR toLower(node.name) CONTAINS toLower($nameFilter)
                RETURN node
                ORDER BY node.name
                LIMIT $limit
                """,
                new { projectId, kind, nameFilter, limit });
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
            $"wingman_{StableSha256(projectId)[..12]}_{Guid.NewGuid():N}";
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

            // Cypher projection 只讀目前 active manifest，並依 FBL 業務鏈關係配置 discovery 權重。
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
                                            WHEN 'OPENS' THEN 1.00
                                            WHEN 'ROUTES_TO' THEN 1.00
                                            WHEN 'IMPLEMENTED_BY' THEN 0.98
                                            WHEN 'OPENS_CUSTOM_REPORT' THEN 0.98
                                            WHEN 'LOADS_PLUGIN_REPORT' THEN 0.98
                                            WHEN 'CONTAINS_DATA_SOURCE' THEN 0.95
                                            WHEN 'READS_VIA' THEN 0.90
                                            WHEN 'WRITES_VIA' THEN 0.95
                                            WHEN 'USES_DEFINITION' THEN 0.90
                                            WHEN 'MAPS_TO' THEN 0.90
                                            WHEN 'QUERIES' THEN 0.90
                                            WHEN 'CALLS' THEN 0.75
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
                "Neo4j GDS Leiden 不可用，將使用確定性的次級 Community 降級流程。" +
                " ProjectId={ProjectId}, ExceptionType={ExceptionType}",
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
                        "清理暫存 GDS projection 失敗；不影響目前的 domain graph。" +
                        " ProjectId={ProjectId}, ExceptionType={ExceptionType}",
                        projectId,
                        exception.GetType().Name);
                }
            }
        }
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
        var degrees = snapshot.Document.Relationships
            .SelectMany(edge => new[] { edge.SourceKey, edge.TargetKey })
            .GroupBy(key => key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        foreach (var kindGroup in snapshot.Document.Nodes.GroupBy(node => node.Kind))
        {
            var authorityLabel = AuthorityGraphSchema.GetNodeLabel(kindGroup.Key);
            foreach (var batch in kindGroup.Chunk(_options.WriteBatchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rows = batch.Select(node => new Dictionary<string, object?>
                {
                    ["id"] = node.Key,
                    ["kind"] = node.Kind.ToString(),
                    ["role"] = node.Kind.ToString(),
                    ["name"] = DisplayName(node),
                    ["searchableText"] = SearchableText(node),
                    ["language"] = AuthorityLanguage(node.Kind),
                    ["filePath"] = AuthorityStringProperty(node.Properties, "source_file")
                        ?? AuthorityStringProperty(node.Properties, "file_path"),
                    ["startLine"] = AuthorityIntProperty(node.Properties, "source_line")
                        ?? AuthorityIntProperty(node.Properties, "start_line"),
                    ["endLine"] = AuthorityIntProperty(node.Properties, "end_line"),
                    ["degree"] = degrees.GetValueOrDefault(node.Key),
                    ["propertiesJson"] = JsonSerializer.Serialize(node.Properties, JsonOptions),
                    ["domainProperties"] = ToNeo4jProperties(node.Properties),
                }).ToList();
                await session.ExecuteWriteAsync(async transaction =>
                {
                    var cursor = await transaction.RunAsync(
                    $$"""
                    UNWIND $rows AS row
                    CREATE (n:GraphEntity:{{authorityLabel}} {
                        projectId: $projectId,
                        graphVersion: $graphVersion,
                        id: row.id,
                        kind: row.kind,
                        role: row.role,
                        name: row.name,
                        searchableText: row.searchableText,
                        language: row.language,
                        filePath: row.filePath,
                        startLine: row.startLine,
                        endLine: row.endLine,
                        degree: row.degree,
                        propertiesJson: row.propertiesJson
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
                    edge.Id,
                    SourceId = edge.SourceKey,
                    TargetId = edge.TargetKey,
                    EvidenceJson = JsonSerializer.Serialize(edge.Evidence, JsonOptions),
                    EvidenceSourceKind = edge.Evidence.SourceKind.ToString(),
                    edge.Evidence.SourceFile,
                    LineNumber = edge.Evidence.SourceLine,
                    edge.Evidence.DatabaseObject,
                    edge.Evidence.DatabaseColumn,
                    edge.Evidence.RowKey,
                    edge.Evidence.RawValue,
                    PropertiesJson = JsonSerializer.Serialize(edge.Properties, JsonOptions),
                    DomainProperties = ToNeo4jProperties(edge.Properties),
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
                            evidenceJson: row.EvidenceJson,
                            evidenceSourceKind: row.EvidenceSourceKind,
                            sourceFile: row.SourceFile,
                            lineNumber: row.LineNumber,
                            databaseObject: row.DatabaseObject,
                            databaseColumn: row.DatabaseColumn,
                            rowKey: row.RowKey,
                            rawValue: row.RawValue,
                            propertiesJson: row.PropertiesJson
                        }]->(target)
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
                    projectId: $projectId,
                    graphVersion: $graphVersion
                })
                WITH count(n) AS nodes,
                     count(CASE WHEN n.kind IS NOT NULL
                                      AND n.propertiesJson IS NOT NULL
                                THEN 1 END) AS validNodes
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
                       validNodes,
                       count(r) AS edges,
                       count(CASE
                           WHEN r.id IS NOT NULL
                                AND r.evidenceJson IS NOT NULL
                           THEN 1
                       END) AS validEdges,
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
                ValidNodes = record["validNodes"].As<int>(),
                Edges = record["edges"].As<int>(),
                ValidEdges = record["validEdges"].As<int>(),
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
            validation.ValidNodes != snapshot.Document.Nodes.Count ||
            validation.ValidEdges != snapshot.Document.Relationships.Count ||
            !validation.RelationshipTypes.SequenceEqual(
                expectedRelationshipTypes, StringComparer.Ordinal))
            throw new InvalidOperationException(
                $"Neo4j staging 驗證不一致：nodes {validation.Nodes}/{snapshot.Document.Nodes.Count}, " +
                $"edges {validation.Edges}/{snapshot.Document.Relationships.Count}, " +
                $"valid nodes {validation.ValidNodes}/{snapshot.Document.Nodes.Count}, " +
                $"valid relationships {validation.ValidEdges}/{snapshot.Document.Relationships.Count}。");
    }

    private async Task PromoteAsync(
        FblGraphSnapshot snapshot,
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
                    p.previousSchemaVersion = CASE
                        WHEN p.activeManifestVersion = $graphVersion
                        THEN p.previousSchemaVersion
                        ELSE p.schemaVersion
                    END,
                    p.previousCanonicalDigest = CASE
                        WHEN p.activeManifestVersion = $graphVersion
                        THEN p.previousCanonicalDigest
                        ELSE p.canonicalDigest
                    END,
                    p.previousNodeCount = CASE
                        WHEN p.activeManifestVersion = $graphVersion
                        THEN p.previousNodeCount
                        ELSE p.nodeCount
                    END,
                    p.previousEdgeCount = CASE
                        WHEN p.activeManifestVersion = $graphVersion
                        THEN p.previousEdgeCount
                        ELSE p.edgeCount
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
                    graphVersion = snapshot.GraphVersion,
                    schemaVersion = "fbl-authority-1",
                    canonicalDigest = snapshot.ContentDigest,
                    nodeCount = snapshot.Document.Nodes.Count,
                    edgeCount = snapshot.Document.Relationships.Count,
                });
            await cursor.ConsumeAsync();
        });
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
        await session.ExecuteWriteAsync(async transaction =>
        {
            var cursor = await transaction.RunAsync(
                """
                MATCH (c:GraphCommunity {
                    projectId: $projectId,
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
        await session.ExecuteWriteAsync(async transaction =>
        {
            var cursor = await transaction.RunAsync(
                """
                MATCH (p:ProjectGraph {projectId: $projectId})
                MATCH (c:GraphCommunity {projectId: $projectId})
                WHERE c.graphVersion <> p.activeManifestVersion
                  AND (p.previousManifestVersion IS NULL
                       OR c.graphVersion <> p.previousManifestVersion)
                DELETE c
                """,
                new { projectId });
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
              RETURN neighbor, relationship,
                     startNode(relationship).id AS sourceId,
                     endNode(relationship).id AS targetId
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
                new { projectId, nodeId, limit });
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
                MATCH (p:ProjectGraph {projectId: $projectId})
                MATCH (center:GraphEntity {
                    projectId: $projectId,
                    graphVersion: p.activeManifestVersion
                })
                WHERE center.id IN $centerIds
                  AND center.filePath IS NOT NULL
                  AND trim(center.filePath) <> ''
                RETURN DISTINCT center.filePath AS filePath
                ORDER BY filePath
                """,
                new { projectId, centerIds });
            var filePaths = new List<string>();
            while (await fileCursor.FetchAsync())
                filePaths.Add(fileCursor.Current["filePath"].As<string>());

            // 即使中心節點沒有 filePath，也要保留中心本身，讓使用者看得到展開起點；
            // 只有具備 filePath 的中心才會帶出同檔案的其他節點。
            var nodeCursor = await transaction.RunAsync(
                """
                MATCH (p:ProjectGraph {projectId: $projectId})
                MATCH (node:GraphEntity {
                    projectId: $projectId,
                    graphVersion: p.activeManifestVersion
                })
                WHERE node.id IN $centerIds OR
                      (size($filePaths) > 0 AND node.filePath IN $filePaths)
                OPTIONAL MATCH (node)-[relationship]-(:GraphEntity {
                    projectId: $projectId,
                    graphVersion: p.activeManifestVersion
                })
                WITH node, count(relationship) AS degree,
                    CASE WHEN node.id IN $centerIds THEN 0 ELSE 1 END AS centerPriority
                ORDER BY centerPriority, degree DESC, node.id
                LIMIT $limit
                RETURN node, degree
                """,
                new { projectId, centerIds, filePaths, limit });
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
                    MATCH (p:ProjectGraph {projectId: $projectId})
                    MATCH (source:GraphEntity {
                        projectId: $projectId,
                        graphVersion: p.activeManifestVersion
                    })-[relationship]->(target:GraphEntity {
                        projectId: $projectId,
                        graphVersion: p.activeManifestVersion
                    })
                    WHERE source.id IN $ids AND target.id IN $ids
                    RETURN relationship, source.id AS sourceId, target.id AS targetId
                    ORDER BY relationship.id
                    LIMIT $edgeLimit
                    """,
                    new
                    {
                        projectId,
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
                MATCH (p:ProjectGraph {projectId: $projectId})
                MATCH (node:GraphEntity {
                    projectId: $projectId,
                    graphVersion: p.activeManifestVersion
                })
                WITH node,
                    CASE WHEN node.id IN $centerIds OR
                              (size($filePaths) > 0 AND node.filePath IN $filePaths)
                         THEN 1 ELSE 0 END AS eligible
                RETURN count(node) AS total, sum(eligible) AS eligibleTotal
                """,
                new { projectId, centerIds, filePaths });
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
    /// 每一個 MATCH node pattern 都必須明確使用 GraphEntity、projectId 與 graphVersion，
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
                    !node.Contains("$projectId", StringComparison.Ordinal) ||
                    !node.Contains("$graphVersion", StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "每個 MATCH node 都必須使用 :GraphEntity，並以 $projectId、$graphVersion 限制 active graph。");
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
            ["kind"] = StringProperty(node.Properties, "kind"),
            ["role"] = StringProperty(node.Properties, "role"),
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
            AuthorityStringProperty(node.Properties, "source_file")
                ?? AuthorityStringProperty(node.Properties, "file_path"),
            AuthorityIntProperty(node.Properties, "source_line")
                ?? AuthorityIntProperty(node.Properties, "start_line"),
            AuthorityIntProperty(node.Properties, "end_line"),
            AuthorityLanguage(node.Kind),
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
            new Dictionary<string, object?>
            {
                ["evidence"] = edge.Evidence,
                ["properties"] = edge.Properties,
            });
    }

    /// <summary>由 :GraphEntity envelope 還原未經簡化的 authority node。</summary>
    private static AuthorityGraphNode MapNode(INode node)
    {
        var properties = node.Properties;
        return new AuthorityGraphNode(
            RequiredString(properties, "id"),
            Enum.Parse<AuthorityGraphNodeKind>(RequiredString(properties, "kind"), ignoreCase: false),
            DeserializeAuthorityProperties(StringProperty(properties, "propertiesJson")));
    }

    /// <summary>由 Neo4j relationship 還原 authority relationship 與完整 evidence。</summary>
    private static AuthorityGraphRelationship MapEdge(
        IRelationship relationship,
        string sourceId,
        string targetId)
    {
        var properties = relationship.Properties;
        var kind = ParseRelationshipType(relationship.Type);
        return new AuthorityGraphRelationship(
            RequiredString(properties, "id"),
            sourceId,
            targetId,
            kind,
            JsonSerializer.Deserialize<AuthorityGraphEvidence>(
                RequiredString(properties, "evidenceJson"), JsonOptions)
                ?? throw new InvalidOperationException("Neo4j authority relationship evidenceJson 無效。"),
            DeserializeAuthorityProperties(StringProperty(properties, "propertiesJson")));
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
        foreach (var key in new[] { "name", "display_name", "qualified_name", "path", "object_name", "menu_id" })
        {
            if (AuthorityStringProperty(node.Properties, key) is { Length: > 0 } value)
                return value;
        }

        return node.Key;
    }

    /// <summary>把穩定 key、kind 與可讀 scalar properties 組成 BM25 文字，不加入大型 XML／原始碼。</summary>
    private static string SearchableText(AuthorityGraphNode node)
    {
        var values = new List<string> { node.Key, node.Kind.ToString(), DisplayName(node) };
        foreach (var (key, value) in node.Properties)
        {
            if (key.Contains("xml", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("source_text", StringComparison.OrdinalIgnoreCase))
                continue;
            if (value is string text && text.Length <= 1_000)
                values.Add(text);
            else if (value is Enum or Guid or int or long or bool)
                values.Add(value.ToString()!);
        }

        return string.Join(' ', values.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal));
    }

    /// <summary>以 authority node kind 提供 UI 使用的語言提示，不改變 graph schema。</summary>
    private static string AuthorityLanguage(AuthorityGraphNodeKind kind) => kind switch
    {
        AuthorityGraphNodeKind.CodeClass or AuthorityGraphNodeKind.WebAction => "csharp",
        AuthorityGraphNodeKind.ClientScript or AuthorityGraphNodeKind.ReactEntry => "javascript",
        AuthorityGraphNodeKind.ViewPage => "view",
        AuthorityGraphNodeKind.DatabaseObject or AuthorityGraphNodeKind.CustomReportDataSource
            or AuthorityGraphNodeKind.CustomParameterDataSource => "sql",
        _ => "business",
    };

    /// <summary>只把 Neo4j 原生支援的 scalar／scalar array 直接投影，完整資料仍保留於 propertiesJson。</summary>
    private static IReadOnlyDictionary<string, object?> ToNeo4jProperties(
        IReadOnlyDictionary<string, object?> properties)
    {
        var reserved = new HashSet<string>(
        [
            "projectId", "graphVersion", "id", "kind", "role", "name", "searchableText",
            "language", "filePath", "startLine", "endLine", "degree", "propertiesJson",
            "evidenceJson",
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
        "searchableText",
        "language",
        "filePath",
        "startLine",
        "endLine",
        "degree",
        "propertiesJson",
        "evidenceJson",
    ];
}
