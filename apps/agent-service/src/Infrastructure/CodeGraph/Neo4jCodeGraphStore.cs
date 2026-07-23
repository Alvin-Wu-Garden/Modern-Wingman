using AgentService.Application.Contracts;
using AgentService.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neo4j.Driver;
using System.Text.RegularExpressions;

namespace AgentService.Infrastructure.CodeGraph;

/// <summary>Neo4j 連線設定。</summary>
public sealed class Neo4jOptions
{
    public const string SectionName = "Neo4j";

    public string Uri { get; set; } = "bolt://127.0.0.1:17688";
    public string Username { get; set; } = "neo4j";
    public string Password { get; set; } = "";
    public string Database { get; set; } = "neo4j";
    public int ConnectionTimeoutSeconds { get; set; } = 3;
    public int TransactionRetrySeconds { get; set; } = 60;
    /// <summary>
    /// Rows sent in one staging write. Input is validated and the run-owned stage is
    /// emptied before writing, so batches use CREATE without sacrificing retry safety.
    /// </summary>
    public int WriteBatchSize { get; set; } = 10_000;
}

/// <summary>
/// 程式碼知識圖譜的 Neo4j 實作（WS3.2 / WS3.5）。
///
/// 圖模型：
///   (:CodeNode {projectId, key, kind, name, signature, filePath, startLine, endLine, language, doc})
///   關係型別 = CodeEdgeKind 大寫（CONTAINS / CALLS / IMPLEMENTS / INHERITS / REFERENCES / DECLARED_IN）
///   (:Community {projectId, communityId, title, summary, memberKeys})
///
/// 檢索：full-text index `codeSearch`（name + signature + doc）— BM25，
/// 不依賴 embeddings（企業目前僅 Copilot SDK，無 embeddings API）。
/// </summary>
public sealed class Neo4jCodeGraphStore : ICodeGraphStore, IAsyncDisposable
{
    private const int CleanupBatchSize = 5_000;
    private static readonly Regex UnsafeReadOnlyCypher = new(
        @"\b(CREATE|MERGE|SET|DELETE|DETACH|REMOVE|DROP|ALTER|GRANT|DENY|REVOKE|LOAD\s+CSV|IMPORT|EXPORT|FOREACH)\b|CALL\s+(dbms|gds|apoc)\.",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex InternalGraphLabels = new(
        @"\b(CodeNodeStage|CodeNodeRetired|ProjectGraphPointer|ProjectGraph)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex UnlabeledNodePattern = new(
        @"(?<![\p{L}\p{N}_])\(\s*(?:[A-Za-z_][A-Za-z0-9_]*\s*)?(?:\{[^()]*\}\s*)?\)",
        RegexOptions.Compiled);

    private readonly IDriver? _driver;
    private readonly Neo4jOptions _options;
    private readonly ILogger<Neo4jCodeGraphStore> _logger;
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private bool _schemaReady;

    public Neo4jCodeGraphStore(
        IOptions<Neo4jOptions> options,
        IOptions<Neo4jLifecycleOptions> lifecycleOptions,
        ILogger<Neo4jCodeGraphStore> logger)
    {
        _options = options.Value;
        _logger = logger;
        if (string.Equals(lifecycleOptions.Value.Mode, "disabled", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Neo4j 已停用（Mode=disabled），跳過 driver 初始化。");
            return;
        }
        _driver = GraphDatabase.Driver(
            _options.Uri,
            AuthTokens.Basic(_options.Username, _options.Password),
            builder => builder
                .WithConnectionTimeout(TimeSpan.FromSeconds(
                    Math.Clamp(_options.ConnectionTimeoutSeconds, 1, 30)))
                .WithMaxTransactionRetryTime(TimeSpan.FromSeconds(
                    Math.Clamp(_options.TransactionRetrySeconds, 1, 300))));
    }

    private IAsyncSession OpenSession() =>
        (_driver ?? throw new InvalidOperationException("Neo4j driver 未初始化（Mode=disabled）")).AsyncSession(o => o.WithDatabase(_options.Database));

    private IAsyncSession OpenReadSession() =>
        (_driver ?? throw new InvalidOperationException("Neo4j driver 未初始化（Mode=disabled）")).AsyncSession(o => o
            .WithDatabase(_options.Database)
            .WithDefaultAccessMode(AccessMode.Read));

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        if (_driver is null) return false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(
            Math.Clamp(_options.ConnectionTimeoutSeconds, 1, 30)));
        try
        {
            ct.ThrowIfCancellationRequested();
            return await _driver.TryVerifyConnectivityAsync().WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug(
                "Neo4j ping 在 {TimeoutSeconds} 秒後逾時",
                Math.Clamp(_options.ConnectionTimeoutSeconds, 1, 30));
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Neo4j ping 失敗");
            return false;
        }
    }

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        if (_schemaReady) return;
        await _schemaGate.WaitAsync(ct);
        try
        {
            if (_schemaReady) return;
            await using var session = OpenSession();

            async Task RunSchemaAsync(string cypher)
            {
                ct.ThrowIfCancellationRequested();
                var cursor = await session.RunAsync(cypher);
                await cursor.ConsumeAsync();
            }

            await RunSchemaAsync(
                "CREATE CONSTRAINT code_node_key IF NOT EXISTS " +
                "FOR (n:CodeNode) REQUIRE (n.projectId, n.key) IS UNIQUE");

            // V1 keyed staging nodes by manifestVersion. V2 has an immutable graphId and
            // keeps manifestVersion solely on the graph anchor.
            await RunSchemaAsync("DROP CONSTRAINT code_node_stage_key IF EXISTS");
            await RunSchemaAsync(
                "CREATE CONSTRAINT code_node_stage_key IF NOT EXISTS " +
                "FOR (n:CodeNodeStage) REQUIRE (n.projectId, n.graphId, n.key) IS UNIQUE");

            await RunSchemaAsync(
                "CREATE CONSTRAINT project_graph_pointer IF NOT EXISTS " +
                "FOR (p:ProjectGraphPointer) REQUIRE p.projectId IS UNIQUE");

            await RunSchemaAsync(
                "CREATE CONSTRAINT project_graph_id IF NOT EXISTS " +
                "FOR (g:ProjectGraph) REQUIRE (g.projectId, g.graphId) IS UNIQUE");

            await RunSchemaAsync(
                "CREATE CONSTRAINT project_graph_manifest IF NOT EXISTS " +
                "FOR (g:ProjectGraph) REQUIRE (g.projectId, g.manifestVersion) IS UNIQUE");

            await RunSchemaAsync(
                "CREATE FULLTEXT INDEX codeSearch IF NOT EXISTS " +
                "FOR (n:CodeNode) ON EACH [n.name, n.signature, n.doc]");

            await RunSchemaAsync(
                "CREATE INDEX community_project IF NOT EXISTS " +
                "FOR (c:Community) ON (c.projectId)");

            _schemaReady = true;
            _logger.LogInformation("Neo4j schema 初始化完成");
        }
        finally
        {
            _schemaGate.Release();
        }
    }

    public async Task ReplaceProjectAsync(
        string projectId,
        GraphPublishDescriptor descriptor,
        CodeAnalysisResult result,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(result);
        ValidatePublishInput(descriptor, result);
        await EnsureSchemaAsync(ct);

        var manifestVersion = descriptor.ManifestVersion;
        var graphId = descriptor.GraphId;
        var writeBatchSize = Math.Clamp(_options.WriteBatchSize, 500, 25_000);
        await using var session = OpenSession();

        if (await IsPublishedGraphAsync(session, projectId, descriptor, ct))
            return;

        // A failed retry may leave this run's own stage behind. Never delete another
        // run's stage: the in-process gate is not a distributed lock.
        await DeleteStagedProjectAsync(session, projectId, graphId, ct);
        try
        {
            await CreateStagingAnchorAsync(session, projectId, descriptor, ct);

            foreach (var chunk in result.Nodes.Chunk(writeBatchSize))
            {
                ct.ThrowIfCancellationRequested();
                var nodes = chunk.Select(n => new Dictionary<string, object?>
                {
                    ["key"] = n.Key, ["kind"] = n.Kind.ToString(), ["name"] = n.Name,
                    ["signature"] = n.Signature, ["filePath"] = n.FilePath,
                    ["startLine"] = n.StartLine, ["endLine"] = n.EndLine, ["language"] = n.Language,
                    ["technology"] = n.Technology, ["sourceKind"] = n.SourceKind.ToString(),
                    ["confidence"] = n.Confidence.ToString(), ["extractorId"] = n.ExtractorId,
                    ["extractorVersion"] = n.ExtractorVersion, ["indexedAt"] = n.IndexedAt?.ToString("O"),
                    ["contentHash"] = n.ContentHash, ["reason"] = n.Reason, ["doc"] = n.DocComment,
                    ["locationsJson"] = n.LocationsJson, ["evidenceJson"] = n.EvidenceJson,
                }).ToList();
                await session.ExecuteWriteAsync(async transaction =>
                {
                    var cursor = await transaction.RunAsync(
                    """
                    UNWIND $nodes AS node
                    CREATE (n:CodeNodeStage {
                        projectId: $projectId,
                        graphId: $graphId,
                        key: node.key
                    })
                    SET n.kind = node.kind, n.name = node.name, n.signature = node.signature,
                        n.filePath = node.filePath, n.startLine = node.startLine, n.endLine = node.endLine,
                        n.language = node.language, n.technology = node.technology,
                        n.sourceKind = node.sourceKind, n.confidence = node.confidence,
                        n.extractorId = node.extractorId, n.extractorVersion = node.extractorVersion,
                        n.indexedAt = node.indexedAt, n.contentHash = node.contentHash,
                        n.reason = node.reason, n.doc = node.doc,
                        n.locationsJson = node.locationsJson, n.evidenceJson = node.evidenceJson
                    """,
                    new { projectId, graphId, nodes });
                    await cursor.ConsumeAsync();
                    return true;
                });
            }

            foreach (var group in result.Edges.GroupBy(edge => edge.Kind))
            {
                var relationshipType = ToRelType(group.Key);
                foreach (var chunk in group.Chunk(writeBatchSize))
                {
                    ct.ThrowIfCancellationRequested();
                    var edges = chunk.Select(edge => new Dictionary<string, object?>
                    {
                        ["source"] = edge.SourceKey, ["target"] = edge.TargetKey,
                        ["sourceKind"] = edge.SourceKind.ToString(), ["confidence"] = edge.Confidence.ToString(),
                        ["extractorId"] = edge.ExtractorId, ["extractorVersion"] = edge.ExtractorVersion,
                        ["reason"] = edge.Reason, ["indexedAt"] = edge.IndexedAt?.ToString("O"),
                        ["contentHash"] = edge.ContentHash,
                        ["evidenceJson"] = edge.EvidenceJson,
                    }).ToList();
                    await session.ExecuteWriteAsync(async transaction =>
                    {
                        var cursor = await transaction.RunAsync(
                            $$"""
                        UNWIND $edges AS edge
                        MATCH (s:CodeNodeStage {
                            projectId: $projectId,
                            graphId: $graphId,
                            key: edge.source
                        })
                        MATCH (t:CodeNodeStage {
                            projectId: $projectId,
                            graphId: $graphId,
                            key: edge.target
                        })
                        CREATE (s)-[r:{{relationshipType}}]->(t)
                        SET r.sourceKind = edge.sourceKind, r.confidence = edge.confidence,
                            r.extractorId = edge.extractorId, r.extractorVersion = edge.extractorVersion,
                            r.reason = edge.reason, r.indexedAt = edge.indexedAt,
                            r.contentHash = edge.contentHash, r.evidenceJson = edge.evidenceJson,
                            r.graphId = $graphId
                        """,
                        new { projectId, graphId, edges });
                        await cursor.ConsumeAsync();
                        return true;
                    });
                }
            }

            await ValidateStagedGraphAsync(session, projectId, descriptor, ct);

            var promoted = await session.ExecuteWriteAsync(async transaction =>
            {
                ct.ThrowIfCancellationRequested();
                var stagedCursor = await transaction.RunAsync(
                    """
                    MATCH (g:ProjectGraph {
                        projectId: $projectId,
                        graphId: $graphId,
                        manifestVersion: $manifestVersion,
                        status: 'Staging'
                    })
                    RETURN g
                    """,
                    new { projectId, graphId, manifestVersion });
                if (!await stagedCursor.FetchAsync())
                {
                    var currentCursor = await transaction.RunAsync(
                        """
                        MATCH (pointer:ProjectGraphPointer {projectId: $projectId})-[:ACTIVE]->
                              (target:ProjectGraph {projectId: $projectId, graphId: $graphId, manifestVersion: $manifestVersion})
                        OPTIONAL MATCH (pointer)-[:PREVIOUS]->(previous:ProjectGraph)
                        RETURN target.graphId AS graphId, previous.graphId AS previousGraphId
                        """,
                        new { projectId, graphId, manifestVersion });
                    var currentRecords = await currentCursor.ToListAsync();
                    return currentRecords.Count == 1
                        ? (Promoted: true, PreviousGraphId: currentRecords[0]["previousGraphId"].As<string?>())
                        : (Promoted: false, PreviousGraphId: (string?)null);
                }

                var pointerCursor = await transaction.RunAsync(
                    """
                    MERGE (pointer:ProjectGraphPointer {projectId: $projectId})
                    WITH pointer
                    OPTIONAL MATCH (pointer)-[activeRel:ACTIVE]->(old:ProjectGraph)
                    OPTIONAL MATCH (pointer)-[previousRel:PREVIOUS]->(previous:ProjectGraph)
                    RETURN pointer, activeRel, old, previousRel, previous
                    """,
                    new { projectId });
                var pointerRecord = await pointerCursor.SingleAsync();
                var oldGraph = pointerRecord["old"] as INode;
                var previousGraphId = oldGraph is not null &&
                                      oldGraph.Properties.TryGetValue("graphId", out var oldGraphId)
                    ? oldGraphId.As<string?>()
                    : null;

                var retireGraph = await transaction.RunAsync(
                    """
                    MATCH (n:CodeNode {projectId: $projectId})
                    REMOVE n:CodeNode
                    SET n:CodeNodeRetired
                    """,
                    new { projectId });
                await retireGraph.ConsumeAsync();
                var deleteCommunities = await transaction.RunAsync(
                    "MATCH (c:Community {projectId: $projectId}) DELETE c",
                    new { projectId });
                await deleteCommunities.ConsumeAsync();
                var promote = await transaction.RunAsync(
                    """
                    MATCH (n:CodeNodeStage {projectId: $projectId, graphId: $graphId})
                    REMOVE n:CodeNodeStage
                    SET n:CodeNode
                    """,
                    new { projectId, graphId });
                await promote.ConsumeAsync();

                var switchPointer = await transaction.RunAsync(
                    """
                    MATCH (pointer:ProjectGraphPointer {projectId: $projectId})
                    MATCH (target:ProjectGraph {projectId: $projectId, graphId: $graphId})
                    OPTIONAL MATCH (pointer)-[activeRel:ACTIVE]->(old:ProjectGraph)
                    OPTIONAL MATCH (pointer)-[previousRel:PREVIOUS]->(previous:ProjectGraph)
                    FOREACH (_ IN CASE WHEN activeRel IS NULL THEN [] ELSE [1] END |
                        DELETE activeRel
                    )
                    FOREACH (_ IN CASE WHEN previousRel IS NULL THEN [] ELSE [1] END |
                        DELETE previousRel
                    )
                    SET target.status = 'Active', target.publishedAt = datetime()
                    FOREACH (_ IN CASE WHEN old IS NULL THEN [] ELSE [1] END |
                        SET old.status = 'Previous'
                    )
                    FOREACH (_ IN CASE WHEN previous IS NULL OR previous = old THEN [] ELSE [1] END |
                        SET previous.status = 'Retired'
                    )
                    MERGE (pointer)-[:ACTIVE]->(target)
                    FOREACH (_ IN CASE WHEN old IS NULL THEN [] ELSE [1] END |
                        MERGE (pointer)-[:PREVIOUS]->(old)
                    )
                    """,
                    new { projectId, graphId });
                await switchPointer.ConsumeAsync();
                return (Promoted: true, PreviousGraphId: previousGraphId);
            });
            if (!promoted.Promoted)
                throw new InvalidOperationException(
                    $"Neo4j staging graph disappeared before promotion: {projectId}/{manifestVersion}");

            try
            {
                await DeleteObsoleteRetiredProjectAsync(
                    session, projectId, promoted.PreviousGraphId, CancellationToken.None);
            }
            catch (Exception cleanupError)
            {
                // The active graph has already switched successfully. Retired nodes
                // are invisible to all graph queries and the next replace will retry
                // bounded cleanup, so cleanup failure must not fail the index itself.
                _logger.LogWarning(cleanupError,
                    "Failed to clean retired Neo4j graph: project={ProjectId}", projectId);
            }
        }
        catch
        {
            try
            {
                await DeleteStagedProjectAsync(session, projectId, graphId, CancellationToken.None);
            }
            catch (Exception cleanupError)
            {
                _logger.LogWarning(cleanupError,
                    "Failed to clean Neo4j staging graph: project={ProjectId}, manifest={ManifestVersion}",
                    projectId, manifestVersion);
            }
            throw;
        }

        _logger.LogInformation(
            "Neo4j atomic replace completed: project={ProjectId}, manifest={ManifestVersion}, {Nodes} nodes, {Edges} edges",
            projectId, manifestVersion, result.Nodes.Count, result.Edges.Count);
    }

    public async Task ApplyProjectDeltaAsync(
        string projectId,
        GraphPublishDescriptor descriptor,
        GraphPublishDelta delta,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(delta);
        ValidateDeltaInput(descriptor, delta);
        await EnsureSchemaAsync(ct);

        var graphId = descriptor.GraphId;
        var writeBatchSize = Math.Clamp(_options.WriteBatchSize, 500, 25_000);
        await using var session = OpenSession();
        if (await IsPublishedGraphAsync(session, projectId, descriptor, ct))
            return;

        var baseGraphId = await GetRequiredBaseGraphIdAsync(
            session, projectId, delta.BaseManifestVersion, ct);
        await DeleteStagedProjectAsync(session, projectId, graphId, ct);
        try
        {
            await CreateStagingAnchorAsync(session, projectId, descriptor, ct);

            // Clone the immutable active snapshot. Every query is pinned to baseGraphId,
            // so a concurrent full publish can never make this stage a mixed graph.
            var cloneNodes = await session.RunAsync(
                """
                MATCH (source:CodeNode {projectId: $projectId, graphId: $baseGraphId})
                CREATE (copy:CodeNodeStage)
                SET copy = properties(source), copy.graphId = $graphId
                RETURN count(copy) AS cloned
                """,
                new { projectId, baseGraphId, graphId });
            await cloneNodes.ConsumeAsync();

            var relationshipTypesCursor = await session.RunAsync(
                """
                MATCH (:CodeNode {projectId: $projectId, graphId: $baseGraphId})
                      -[relationship]->
                      (:CodeNode {projectId: $projectId, graphId: $baseGraphId})
                RETURN DISTINCT type(relationship) AS relationshipType
                ORDER BY relationshipType
                """,
                new { projectId, baseGraphId });
            var relationshipTypes = (await relationshipTypesCursor.ToListAsync())
                .Select(record => record["relationshipType"].As<string>())
                .ToList();
            var allowedRelationshipTypes = Enum.GetValues<CodeEdgeKind>()
                .Select(ToRelType)
                .ToHashSet(StringComparer.Ordinal);
            var unknownRelationshipType = relationshipTypes.FirstOrDefault(
                relationshipType => !allowedRelationshipTypes.Contains(relationshipType));
            if (unknownRelationshipType is not null)
                throw new InvalidOperationException(
                    $"Active graph contains unsupported relationship type '{unknownRelationshipType}'.");

            foreach (var relationshipType in relationshipTypes)
            {
                ct.ThrowIfCancellationRequested();
                var cloneEdges = await session.RunAsync(
                    $$"""
                    MATCH (source:CodeNode {projectId: $projectId, graphId: $baseGraphId})
                          -[relationship:{{relationshipType}}]->
                          (target:CodeNode {projectId: $projectId, graphId: $baseGraphId})
                    MATCH (copySource:CodeNodeStage {
                        projectId: $projectId, graphId: $graphId, key: source.key
                    })
                    MATCH (copyTarget:CodeNodeStage {
                        projectId: $projectId, graphId: $graphId, key: target.key
                    })
                    CREATE (copySource)-[copyRelationship:{{relationshipType}}]->(copyTarget)
                    SET copyRelationship = properties(relationship),
                        copyRelationship.graphId = $graphId
                    RETURN count(copyRelationship) AS cloned
                    """,
                    new { projectId, baseGraphId, graphId });
                await cloneEdges.ConsumeAsync();
            }

            foreach (var group in delta.RemovedEdges.GroupBy(edge => edge.Kind))
            {
                ct.ThrowIfCancellationRequested();
                var relationshipType = ToRelType(group.Key);
                var edges = group.Select(edge => new
                {
                    source = edge.SourceKey,
                    target = edge.TargetKey,
                }).ToList();
                var cursor = await session.RunAsync(
                    $$"""
                    UNWIND $edges AS edge
                    MATCH (source:CodeNodeStage {
                        projectId: $projectId, graphId: $graphId, key: edge.source
                    })-[relationship:{{relationshipType}}]->(target:CodeNodeStage {
                        projectId: $projectId, graphId: $graphId, key: edge.target
                    })
                    DELETE relationship
                    RETURN count(relationship) AS removed
                    """,
                    new { projectId, graphId, edges });
                var removed = (await cursor.SingleAsync())["removed"].As<long>();
                if (removed != edges.Count)
                    throw new InvalidOperationException(
                        $"Neo4j delta expected to remove {edges.Count} {relationshipType} edges but removed {removed}.");
            }

            foreach (var chunk in delta.RemovedNodeKeys.Chunk(writeBatchSize))
            {
                ct.ThrowIfCancellationRequested();
                var cursor = await session.RunAsync(
                    """
                    UNWIND $keys AS key
                    MATCH (node:CodeNodeStage {
                        projectId: $projectId, graphId: $graphId, key: key
                    })
                    DETACH DELETE node
                    RETURN count(node) AS removed
                    """,
                    new { projectId, graphId, keys = chunk.ToList() });
                var removed = (await cursor.SingleAsync())["removed"].As<long>();
                if (removed != chunk.Length)
                    throw new InvalidOperationException(
                        $"Neo4j delta expected to remove {chunk.Length} nodes but removed {removed}.");
            }

            foreach (var chunk in delta.UpsertNodes.Chunk(writeBatchSize))
            {
                ct.ThrowIfCancellationRequested();
                var nodes = chunk.Select(ToNodeProperties).ToList();
                var cursor = await session.RunAsync(
                    """
                    UNWIND $nodes AS node
                    MERGE (target:CodeNodeStage {
                        projectId: $projectId, graphId: $graphId, key: node.key
                    })
                    SET target = node,
                        target.projectId = $projectId,
                        target.graphId = $graphId,
                        target.key = node.key
                    """,
                    new { projectId, graphId, nodes });
                await cursor.ConsumeAsync();
            }

            foreach (var group in delta.UpsertEdges.GroupBy(edge => edge.Kind))
            {
                var relationshipType = ToRelType(group.Key);
                foreach (var chunk in group.Chunk(writeBatchSize))
                {
                    ct.ThrowIfCancellationRequested();
                    var edges = chunk.Select(ToEdgeProperties).ToList();
                    var cursor = await session.RunAsync(
                        $$"""
                        UNWIND $edges AS edge
                        MATCH (source:CodeNodeStage {
                            projectId: $projectId, graphId: $graphId, key: edge.source
                        })
                        MATCH (target:CodeNodeStage {
                            projectId: $projectId, graphId: $graphId, key: edge.target
                        })
                        OPTIONAL MATCH (source)-[existing:{{relationshipType}}]->(target)
                        DELETE existing
                        CREATE (source)-[relationship:{{relationshipType}}]->(target)
                        SET relationship.sourceKind = edge.sourceKind,
                            relationship.confidence = edge.confidence,
                            relationship.extractorId = edge.extractorId,
                            relationship.extractorVersion = edge.extractorVersion,
                            relationship.reason = edge.reason,
                            relationship.indexedAt = edge.indexedAt,
                            relationship.contentHash = edge.contentHash,
                            relationship.evidenceJson = edge.evidenceJson,
                            relationship.graphId = $graphId
                        RETURN count(relationship) AS upserted
                        """,
                        new { projectId, graphId, edges });
                    var upserted = (await cursor.SingleAsync())["upserted"].As<long>();
                    if (upserted != edges.Count)
                        throw new InvalidOperationException(
                            $"Neo4j delta expected to upsert {edges.Count} {relationshipType} edges but upserted {upserted}.");
                }
            }

            await ValidateStagedGraphAsync(session, projectId, descriptor, ct);
            (bool Promoted, string? PreviousGraphId) promoted;
            try
            {
                promoted = await PromoteDeltaStageAsync(
                    session, projectId, descriptor, baseGraphId, ct);
            }
            catch
            {
                // A lost Bolt acknowledgement can make commit outcome uncertain.
                // Treat an exactly matching active anchor + graph as success; otherwise
                // rethrow and let the outer handler clean only this run's stage.
                if (!await IsPublishedGraphAsync(
                        session, projectId, descriptor, CancellationToken.None))
                    throw;
                promoted = (true, baseGraphId);
            }
            if (!promoted.Promoted)
                throw new InvalidOperationException(
                    $"Neo4j delta staging graph disappeared before promotion: {projectId}/{descriptor.ManifestVersion}");

            try
            {
                await DeleteObsoleteRetiredProjectAsync(
                    session, projectId, promoted.PreviousGraphId, CancellationToken.None);
            }
            catch (Exception cleanupError)
            {
                _logger.LogWarning(cleanupError,
                    "Failed to clean retired Neo4j graph after delta publish: project={ProjectId}", projectId);
            }
        }
        catch
        {
            try
            {
                await DeleteStagedProjectAsync(session, projectId, graphId, CancellationToken.None);
            }
            catch (Exception cleanupError)
            {
                _logger.LogWarning(cleanupError,
                    "Failed to clean Neo4j delta staging graph: project={ProjectId}, manifest={ManifestVersion}",
                    projectId, descriptor.ManifestVersion);
            }
            throw;
        }

        _logger.LogInformation(
            "Neo4j atomic delta completed: project={ProjectId}, manifest={ManifestVersion}, {Nodes} nodes, {Edges} edges",
            projectId, descriptor.ManifestVersion, descriptor.ExpectedNodeCount, descriptor.ExpectedEdgeCount);
    }

    private static Dictionary<string, object?> ToNodeProperties(CodeNode node) => new()
    {
        ["key"] = node.Key,
        ["kind"] = node.Kind.ToString(),
        ["name"] = node.Name,
        ["signature"] = node.Signature,
        ["filePath"] = node.FilePath,
        ["startLine"] = node.StartLine,
        ["endLine"] = node.EndLine,
        ["language"] = node.Language,
        ["technology"] = node.Technology,
        ["sourceKind"] = node.SourceKind.ToString(),
        ["confidence"] = node.Confidence.ToString(),
        ["extractorId"] = node.ExtractorId,
        ["extractorVersion"] = node.ExtractorVersion,
        ["indexedAt"] = node.IndexedAt?.ToString("O"),
        ["contentHash"] = node.ContentHash,
        ["reason"] = node.Reason,
        ["doc"] = node.DocComment,
        ["locationsJson"] = node.LocationsJson,
        ["evidenceJson"] = node.EvidenceJson,
    };

    private static Dictionary<string, object?> ToEdgeProperties(CodeEdge edge) => new()
    {
        ["source"] = edge.SourceKey,
        ["target"] = edge.TargetKey,
        ["sourceKind"] = edge.SourceKind.ToString(),
        ["confidence"] = edge.Confidence.ToString(),
        ["extractorId"] = edge.ExtractorId,
        ["extractorVersion"] = edge.ExtractorVersion,
        ["reason"] = edge.Reason,
        ["indexedAt"] = edge.IndexedAt?.ToString("O"),
        ["contentHash"] = edge.ContentHash,
        ["evidenceJson"] = edge.EvidenceJson,
    };

    private static void ValidateDeltaInput(
        GraphPublishDescriptor descriptor,
        GraphPublishDelta delta)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.ManifestVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.GraphId);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.GraphSchemaVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.AnalysisSnapshotHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(delta.BaseManifestVersion);
        ArgumentNullException.ThrowIfNull(delta.RemovedNodeKeys);
        ArgumentNullException.ThrowIfNull(delta.UpsertNodes);
        ArgumentNullException.ThrowIfNull(delta.RemovedEdges);
        ArgumentNullException.ThrowIfNull(delta.UpsertEdges);
        if (descriptor.ExpectedNodeCount < 0 || descriptor.ExpectedEdgeCount < 0)
            throw new InvalidOperationException("Graph publish descriptor counts cannot be negative.");

        var removedNodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in delta.RemovedNodeKeys)
        {
            if (string.IsNullOrWhiteSpace(key) || !removedNodes.Add(key))
                throw new InvalidOperationException($"Delta contains an empty or duplicate removed node key: '{key}'.");
        }

        var upsertNodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in delta.UpsertNodes)
        {
            if (string.IsNullOrWhiteSpace(node.Key) || !upsertNodes.Add(node.Key))
                throw new InvalidOperationException($"Delta contains an empty or duplicate upsert node key: '{node.Key}'.");
            if (removedNodes.Contains(node.Key))
                throw new InvalidOperationException($"Delta cannot remove and upsert node '{node.Key}' in the same publish.");
        }

        static string EdgeIdentity(string source, CodeEdgeKind kind, string target) =>
            $"{source}\0{kind}\0{target}";
        var removedEdges = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edge in delta.RemovedEdges)
        {
            if (string.IsNullOrWhiteSpace(edge.SourceKey) || string.IsNullOrWhiteSpace(edge.TargetKey) ||
                !removedEdges.Add(EdgeIdentity(edge.SourceKey, edge.Kind, edge.TargetKey)))
                throw new InvalidOperationException("Delta contains an empty or duplicate removed edge identity.");
        }

        var upsertEdges = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edge in delta.UpsertEdges)
        {
            var identity = EdgeIdentity(edge.SourceKey, edge.Kind, edge.TargetKey);
            if (string.IsNullOrWhiteSpace(edge.SourceKey) || string.IsNullOrWhiteSpace(edge.TargetKey) ||
                !upsertEdges.Add(identity))
                throw new InvalidOperationException("Delta contains an empty or duplicate upsert edge identity.");
            if (removedEdges.Contains(identity))
                throw new InvalidOperationException($"Delta cannot remove and upsert edge '{identity}' in the same publish.");
            if (removedNodes.Contains(edge.SourceKey) || removedNodes.Contains(edge.TargetKey))
                throw new InvalidOperationException($"Delta cannot upsert an edge attached to a removed node: '{identity}'.");
        }
    }

    private static async Task<string> GetRequiredBaseGraphIdAsync(
        IAsyncSession session,
        string projectId,
        string baseManifestVersion,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var cursor = await session.RunAsync(
            """
            MATCH (:ProjectGraphPointer {projectId: $projectId})-[:ACTIVE]->
                  (graph:ProjectGraph {
                      projectId: $projectId,
                      manifestVersion: $baseManifestVersion,
                      status: 'Active'
                  })
            RETURN graph.graphId AS graphId
            """,
            new { projectId, baseManifestVersion });
        var records = await cursor.ToListAsync();
        if (records.Count != 1 || string.IsNullOrWhiteSpace(records[0]["graphId"].As<string?>()))
            throw new InvalidOperationException(
                $"Neo4j delta base manifest is not active: {projectId}/{baseManifestVersion}.");
        return records[0]["graphId"].As<string>();
    }

    private static async Task<(bool Promoted, string? PreviousGraphId)> PromoteDeltaStageAsync(
        IAsyncSession session,
        string projectId,
        GraphPublishDescriptor descriptor,
        string baseGraphId,
        CancellationToken ct)
    {
        return await session.ExecuteWriteAsync(async transaction =>
        {
            ct.ThrowIfCancellationRequested();
            var stagedCursor = await transaction.RunAsync(
                """
                MATCH (target:ProjectGraph {
                    projectId: $projectId,
                    graphId: $graphId,
                    manifestVersion: $manifestVersion,
                    status: 'Staging'
                })
                MATCH (pointer:ProjectGraphPointer {projectId: $projectId})-[:ACTIVE]->
                      (base:ProjectGraph {projectId: $projectId, graphId: $baseGraphId, status: 'Active'})
                RETURN target, pointer, base
                """,
                new
                {
                    projectId,
                    graphId = descriptor.GraphId,
                    manifestVersion = descriptor.ManifestVersion,
                    baseGraphId,
                });
            var stagedRecords = await stagedCursor.ToListAsync();
            if (stagedRecords.Count != 1)
                return (false, (string?)null);

            var retireGraph = await transaction.RunAsync(
                """
                MATCH (node:CodeNode {projectId: $projectId, graphId: $baseGraphId})
                REMOVE node:CodeNode
                SET node:CodeNodeRetired
                """,
                new { projectId, baseGraphId });
            await retireGraph.ConsumeAsync();

            var deleteCommunities = await transaction.RunAsync(
                "MATCH (community:Community {projectId: $projectId}) DELETE community",
                new { projectId });
            await deleteCommunities.ConsumeAsync();

            var promote = await transaction.RunAsync(
                """
                MATCH (node:CodeNodeStage {projectId: $projectId, graphId: $graphId})
                REMOVE node:CodeNodeStage
                SET node:CodeNode
                """,
                new { projectId, graphId = descriptor.GraphId });
            await promote.ConsumeAsync();

            var switchPointer = await transaction.RunAsync(
                """
                MATCH (pointer:ProjectGraphPointer {projectId: $projectId})
                      -[activeRel:ACTIVE]->
                      (base:ProjectGraph {projectId: $projectId, graphId: $baseGraphId})
                MATCH (target:ProjectGraph {projectId: $projectId, graphId: $graphId})
                OPTIONAL MATCH (pointer)-[previousRel:PREVIOUS]->(previous:ProjectGraph)
                DELETE activeRel
                FOREACH (_ IN CASE WHEN previousRel IS NULL THEN [] ELSE [1] END |
                    DELETE previousRel
                )
                SET target.status = 'Active', target.publishedAt = datetime(),
                    base.status = 'Previous'
                FOREACH (_ IN CASE WHEN previous IS NULL OR previous = base THEN [] ELSE [1] END |
                    SET previous.status = 'Retired'
                )
                MERGE (pointer)-[:ACTIVE]->(target)
                MERGE (pointer)-[:PREVIOUS]->(base)
                RETURN base.graphId AS previousGraphId
                """,
                new { projectId, baseGraphId, graphId = descriptor.GraphId });
            var switched = await switchPointer.ToListAsync();
            return switched.Count == 1
                ? (true, switched[0]["previousGraphId"].As<string?>())
                : (false, (string?)null);
        });
    }

    private static void ValidatePublishInput(
        GraphPublishDescriptor descriptor,
        CodeAnalysisResult result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.ManifestVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.GraphId);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.GraphSchemaVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.AnalysisSnapshotHash);

        if (descriptor.ExpectedNodeCount != result.Nodes.Count ||
            descriptor.ExpectedEdgeCount != result.Edges.Count)
        {
            throw new InvalidOperationException(
                $"Graph publish descriptor count mismatch: expected " +
                $"{descriptor.ExpectedNodeCount}/{descriptor.ExpectedEdgeCount}, actual " +
                $"{result.Nodes.Count}/{result.Edges.Count}.");
        }

        var nodeKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in result.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Key) || !nodeKeys.Add(node.Key))
                throw new InvalidOperationException($"Graph contains an empty or duplicate node key: '{node.Key}'.");
        }

        var edgeKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edge in result.Edges)
        {
            if (!nodeKeys.Contains(edge.SourceKey) || !nodeKeys.Contains(edge.TargetKey))
            {
                throw new InvalidOperationException(
                    $"Graph contains a dangling edge: {edge.SourceKey} -[{edge.Kind}]-> {edge.TargetKey}.");
            }

            var identity = $"{edge.SourceKey}\0{edge.Kind}\0{edge.TargetKey}";
            if (!edgeKeys.Add(identity))
                throw new InvalidOperationException($"Graph contains a duplicate edge: {identity}.");
        }
    }

    private static async Task<bool> IsPublishedGraphAsync(
        IAsyncSession session,
        string projectId,
        GraphPublishDescriptor descriptor,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var cursor = await session.RunAsync(
            """
            MATCH (:ProjectGraphPointer {projectId: $projectId})-[:ACTIVE]->
                  (g:ProjectGraph {projectId: $projectId, graphId: $graphId})
            OPTIONAL MATCH (n:CodeNode {projectId: $projectId})
            OPTIONAL MATCH (n)-[r]->(:CodeNode {projectId: $projectId})
            RETURN g.manifestVersion AS manifestVersion,
                   g.graphSchemaVersion AS graphSchemaVersion,
                   g.analysisSnapshotHash AS analysisSnapshotHash,
                   g.nodeCount AS nodeCount, g.edgeCount AS edgeCount,
                   count(DISTINCT n) AS actualNodeCount,
                   count(DISTINCT CASE WHEN n.graphId = g.graphId THEN n END) AS matchingNodeCount,
                   count(r) AS actualEdgeCount,
                   count(CASE WHEN r.graphId = g.graphId THEN r END) AS matchingEdgeCount
            """,
            new { projectId, graphId = descriptor.GraphId });
        var records = await cursor.ToListAsync();
        if (records.Count == 0) return false;

        var record = records[0];
        var anchorMatches =
            string.Equals(record["manifestVersion"].As<string?>(), descriptor.ManifestVersion, StringComparison.Ordinal) &&
            string.Equals(record["graphSchemaVersion"].As<string?>(), descriptor.GraphSchemaVersion, StringComparison.Ordinal) &&
            string.Equals(record["analysisSnapshotHash"].As<string?>(), descriptor.AnalysisSnapshotHash, StringComparison.Ordinal) &&
            record["nodeCount"].As<long>() == descriptor.ExpectedNodeCount &&
            record["edgeCount"].As<long>() == descriptor.ExpectedEdgeCount;
        if (!anchorMatches)
            throw new InvalidOperationException(
                $"The active graph id '{descriptor.GraphId}' does not match the requested publish descriptor.");

        var graphMatches =
            record["actualNodeCount"].As<long>() == descriptor.ExpectedNodeCount &&
            record["matchingNodeCount"].As<long>() == descriptor.ExpectedNodeCount &&
            record["actualEdgeCount"].As<long>() == descriptor.ExpectedEdgeCount &&
            record["matchingEdgeCount"].As<long>() == descriptor.ExpectedEdgeCount;
        if (!graphMatches)
            throw new InvalidOperationException(
                $"The active graph '{descriptor.GraphId}' failed idempotent publish validation.");

        return true;
    }

    private static async Task CreateStagingAnchorAsync(
        IAsyncSession session,
        string projectId,
        GraphPublishDescriptor descriptor,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var cursor = await session.RunAsync(
            """
            CREATE (g:ProjectGraph {
                projectId: $projectId,
                graphId: $graphId,
                manifestVersion: $manifestVersion,
                graphSchemaVersion: $graphSchemaVersion,
                analysisSnapshotHash: $analysisSnapshotHash,
                canonicalDigest: $canonicalDigest,
                nodeCount: $nodeCount,
                edgeCount: $edgeCount,
                status: 'Staging',
                createdAt: datetime()
            })
            RETURN g.graphId AS graphId
            """,
            new
            {
                projectId,
                graphId = descriptor.GraphId,
                manifestVersion = descriptor.ManifestVersion,
                graphSchemaVersion = descriptor.GraphSchemaVersion,
                analysisSnapshotHash = descriptor.AnalysisSnapshotHash,
                canonicalDigest = descriptor.CanonicalDigest ?? descriptor.AnalysisSnapshotHash,
                nodeCount = descriptor.ExpectedNodeCount,
                edgeCount = descriptor.ExpectedEdgeCount,
            });
        await cursor.ConsumeAsync();
    }

    private static async Task ValidateStagedGraphAsync(
        IAsyncSession session,
        string projectId,
        GraphPublishDescriptor descriptor,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var nodeCursor = await session.RunAsync(
            """
            MATCH (g:ProjectGraph {projectId: $projectId, graphId: $graphId, status: 'Staging'})
            OPTIONAL MATCH (n:CodeNodeStage {projectId: $projectId, graphId: $graphId})
            RETURN count(n) AS nodeCount, count(n.graphId) AS graphVersionedNodeCount,
                   count(DISTINCT n.key) AS distinctKeyCount,
                   g.nodeCount AS expectedNodeCount, g.edgeCount AS expectedEdgeCount,
                   g.analysisSnapshotHash AS analysisSnapshotHash,
                   g.graphSchemaVersion AS graphSchemaVersion
            """,
            new { projectId, graphId = descriptor.GraphId });
        var nodeRecords = await nodeCursor.ToListAsync();
        if (nodeRecords.Count != 1)
            throw new InvalidOperationException("Neo4j staging graph anchor is missing or duplicated.");

        var nodeRecord = nodeRecords[0];
        var nodeCount = nodeRecord["nodeCount"].As<long>();
        var versionedNodeCount = nodeRecord["graphVersionedNodeCount"].As<long>();
        var distinctKeyCount = nodeRecord["distinctKeyCount"].As<long>();
        if (nodeCount != descriptor.ExpectedNodeCount ||
            versionedNodeCount != nodeCount ||
            distinctKeyCount != nodeCount ||
            nodeRecord["expectedNodeCount"].As<long>() != descriptor.ExpectedNodeCount ||
            nodeRecord["expectedEdgeCount"].As<long>() != descriptor.ExpectedEdgeCount ||
            !string.Equals(nodeRecord["analysisSnapshotHash"].As<string?>(), descriptor.AnalysisSnapshotHash, StringComparison.Ordinal) ||
            !string.Equals(nodeRecord["graphSchemaVersion"].As<string?>(), descriptor.GraphSchemaVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Neo4j staging node validation failed for {projectId}/{descriptor.GraphId}.");
        }

        var edgeCursor = await session.RunAsync(
            """
            OPTIONAL MATCH (s:CodeNodeStage {projectId: $projectId, graphId: $graphId})
            OPTIONAL MATCH (s)-[r]->(t:CodeNodeStage {projectId: $projectId, graphId: $graphId})
            RETURN count(r) AS edgeCount, count(r.graphId) AS graphVersionedEdgeCount,
                   count(CASE WHEN t IS NULL THEN null ELSE 1 END) AS endpointCount
            """,
            new { projectId, graphId = descriptor.GraphId });
        var edgeRecord = await edgeCursor.SingleAsync();
        var edgeCount = edgeRecord["edgeCount"].As<long>();
        if (edgeCount != descriptor.ExpectedEdgeCount ||
            edgeRecord["graphVersionedEdgeCount"].As<long>() != edgeCount ||
            edgeRecord["endpointCount"].As<long>() != edgeCount)
        {
            throw new InvalidOperationException(
                $"Neo4j staging edge validation failed for {projectId}/{descriptor.GraphId}.");
        }
    }

    private static async Task DeleteStagedProjectAsync(
        IAsyncSession session,
        string projectId,
        string graphId,
        CancellationToken ct)
    {
        const int batchSize = CleanupBatchSize;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var deleted = await session.ExecuteWriteAsync(async transaction =>
            {
                var cursor = await transaction.RunAsync(
                    """
                    MATCH (n:CodeNodeStage {projectId: $projectId, graphId: $graphId})
                    WITH n LIMIT $batchSize
                    DETACH DELETE n
                    RETURN count(*) AS deleted
                    """,
                    new { projectId, graphId, batchSize });
                return (await cursor.SingleAsync())["deleted"].As<long>();
            });
            if (deleted == 0)
                break;
        }

        await RunAndConsumeAsync(session,
            """
            MATCH (g:ProjectGraph {projectId: $projectId, graphId: $graphId})
            WHERE g.status = 'Staging'
              AND NOT (:ProjectGraphPointer {projectId: $projectId})-[:ACTIVE|PREVIOUS]->(g)
            DELETE g
            """,
            new { projectId, graphId });
    }

    private static async Task DeleteObsoleteRetiredProjectAsync(
        IAsyncSession session,
        string projectId,
        string? previousGraphId,
        CancellationToken ct)
    {
        const int batchSize = CleanupBatchSize;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var deleted = await session.ExecuteWriteAsync(async transaction =>
            {
                var cursor = await transaction.RunAsync(
                    """
                    MATCH (n:CodeNodeRetired {projectId: $projectId})
                    WHERE $previousGraphId IS NULL OR n.graphId IS NULL OR n.graphId <> $previousGraphId
                    WITH n LIMIT $batchSize
                    DETACH DELETE n
                    RETURN count(*) AS deleted
                    """,
                    new { projectId, previousGraphId, batchSize });
                return (await cursor.SingleAsync())["deleted"].As<long>();
            });
            if (deleted == 0)
                break;
        }


        await RunAndConsumeAsync(session,
            """
            MATCH (g:ProjectGraph {projectId: $projectId})
            WHERE NOT (:ProjectGraphPointer {projectId: $projectId})-[:ACTIVE|PREVIOUS]->(g)
            DETACH DELETE g
            """,
            new { projectId });
    }

    public async Task<string?> GetProjectManifestVersionAsync(
        string projectId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ct.ThrowIfCancellationRequested();
        await using var session = OpenReadSession();
        var cursor = await session.RunAsync(
            """
            MATCH (pointer:ProjectGraphPointer {projectId: $projectId})-[:ACTIVE]->
                  (graph:ProjectGraph {projectId: $projectId, status: 'Active'})
            OPTIONAL MATCH (node:CodeNode {projectId: $projectId})
            OPTIONAL MATCH (node)-[relationship]->(:CodeNode {projectId: $projectId})
            RETURN graph.manifestVersion AS version, graph.graphId AS graphId,
                   graph.nodeCount AS expectedNodeCount, graph.edgeCount AS expectedEdgeCount,
                   count(DISTINCT node) AS nodeCount,
                   count(DISTINCT CASE WHEN node.graphId = graph.graphId THEN node END) AS matchingNodeCount,
                   count(DISTINCT CASE WHEN node.graphId IS NOT NULL THEN node END) AS graphVersionedNodeCount,
                   count(relationship) AS edgeCount,
                   count(relationship.graphId) AS graphVersionedEdgeCount,
                   count(CASE WHEN relationship.graphId = graph.graphId THEN relationship END) AS matchingEdgeCount
            """,
            new { projectId });
        var records = await cursor.ToListAsync();
        if (records.Count == 0)
            return null;

        var record = records[0];
        var graphId = record["graphId"].As<string?>();
        var version = record["version"].As<string?>();
        var nodeCount = record["nodeCount"].As<long>();
        var edgeCount = record["edgeCount"].As<long>();
        var expectedNodeCount = record["expectedNodeCount"].As<long>();
        var expectedEdgeCount = record["expectedEdgeCount"].As<long>();
        if (string.IsNullOrWhiteSpace(graphId) || string.IsNullOrWhiteSpace(version) ||
            nodeCount != expectedNodeCount || edgeCount != expectedEdgeCount ||
            record["matchingNodeCount"].As<long>() != nodeCount ||
            record["graphVersionedNodeCount"].As<long>() != nodeCount ||
            record["graphVersionedEdgeCount"].As<long>() != edgeCount ||
            record["matchingEdgeCount"].As<long>() != edgeCount)
        {
            _logger.LogError(
                "Neo4j active graph is inconsistent: project={ProjectId}, manifest={ManifestVersion}, graph={GraphId}, nodes={NodeCount}/{ExpectedNodes}, edges={EdgeCount}/{ExpectedEdges}",
                projectId, version, graphId, nodeCount, expectedNodeCount, edgeCount, expectedEdgeCount);
            return null;
        }

        return version;
    }

    public async Task DeleteProjectAsync(string projectId, CancellationToken ct = default)
    {
        await using var session = OpenSession();
        foreach (var label in new[] { "CodeNodeStage", "CodeNode", "CodeNodeRetired" })
            await DeleteProjectNodesByLabelAsync(session, projectId, label, ct);
        await RunAndConsumeAsync(session,
            "MATCH (c:Community {projectId: $projectId}) DELETE c",
            new { projectId });
        await RunAndConsumeAsync(session,
            "MATCH (p:ProjectGraphPointer {projectId: $projectId}) DETACH DELETE p",
            new { projectId });
        await RunAndConsumeAsync(session,
            "MATCH (g:ProjectGraph {projectId: $projectId}) DETACH DELETE g",
            new { projectId });
    }

    private static async Task DeleteProjectNodesByLabelAsync(
        IAsyncSession session,
        string projectId,
        string label,
        CancellationToken ct)
    {
        const int batchSize = CleanupBatchSize;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var cursor = await session.RunAsync(
                $"MATCH (n:{label} {{projectId: $projectId}}) " +
                "WITH n LIMIT $batchSize DETACH DELETE n RETURN count(*) AS deleted",
                new { projectId, batchSize });
            if ((await cursor.SingleAsync())["deleted"].As<long>() == 0)
                return;
        }
    }

    public async Task<IReadOnlyList<GraphSearchHit>> SearchAsync(
        string projectId, string query, int limit = 20, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0)
            return [];

        await using var session = OpenReadSession();
        // Callers may supply natural-language questions, file paths, error logs, or a
        // structured Evidence Pack.  Passing that text straight to Lucene's query
        // parser is unsafe: punctuation such as ':' and '(' has query syntax meaning.
        // Search individual literal terms instead, so a rich prompt can never turn into
        // an invalid Lucene expression (or silently change the intended query semantics).
        var safe = BuildLuceneQuery(query);
        if (safe.Length == 0)
            return [];
        var cursor = await session.RunAsync(
            """
            CALL db.index.fulltext.queryNodes('codeSearch', $query)
            YIELD node, score
            MATCH (:ProjectGraphPointer {projectId: $projectId})-[:ACTIVE]->
                  (graph:ProjectGraph {projectId: $projectId, status: 'Active'})
            WHERE node.projectId = $projectId AND node.graphId = graph.graphId
            RETURN node.key AS key, node.kind AS kind, node.name AS name,
                   node.signature AS signature, node.filePath AS filePath,
                   node.startLine AS startLine, node.endLine AS endLine, score,
                   node.sourceKind AS sourceKind, node.confidence AS confidence,
                   node.extractorId AS extractorId, node.extractorVersion AS extractorVersion,
                   node.reason AS reason, graph.manifestVersion AS manifestVersion,
                   node.contentHash AS contentHash
            LIMIT $limit
            """,
            new { projectId, query = safe, limit });

        var hits = new List<GraphSearchHit>();
        await foreach (var record in cursor)
        {
            hits.Add(ToHit(record));
        }
        return hits;
    }

    public async Task<IReadOnlyList<GraphSearchHit>> GetCentralNodesAsync(
        string projectId,
        int limit = 200,
        CancellationToken ct = default)
    {
        if (limit <= 0) return [];
        await using var session = OpenReadSession();
        var cursor = await session.RunAsync(
            """
            MATCH (:ProjectGraphPointer {projectId: $projectId})-[:ACTIVE]->
                  (graph:ProjectGraph {projectId: $projectId, status: 'Active'})
            MATCH (node:CodeNode {projectId: $projectId, graphId: graph.graphId})
            OPTIONAL MATCH (node)-[relationship]-(neighbor:CodeNode {projectId: $projectId, graphId: graph.graphId})
            WITH graph, node, count(relationship) AS degree
            ORDER BY degree DESC, node.key ASC
            RETURN node.key AS key, node.kind AS kind, node.name AS name,
                   node.signature AS signature, node.filePath AS filePath,
                   node.startLine AS startLine, node.endLine AS endLine, toFloat(degree) AS score,
                   node.sourceKind AS sourceKind, node.confidence AS confidence,
                   node.extractorId AS extractorId, node.extractorVersion AS extractorVersion,
                   node.reason AS reason, graph.manifestVersion AS manifestVersion,
                   node.contentHash AS contentHash
            LIMIT $limit
            """,
            new { projectId, limit = Math.Min(limit, 1000) });
        var hits = new List<GraphSearchHit>();
        await foreach (var record in cursor) hits.Add(ToHit(record));
        return hits;
    }

    public async Task<GraphNeighborhood> GetNeighborhoodAsync(
        string projectId, string nodeKey, int depth = 1, CancellationToken ct = default)
    {
        const int neighborLimit = 100;
        await using var session = OpenReadSession();
        var boundedDepth = Math.Clamp(depth, 1, 6);
        return await session.ExecuteReadAsync(async transaction =>
        {
        ct.ThrowIfCancellationRequested();
        var centerCursor = await transaction.RunAsync(
            """
            MATCH (:ProjectGraphPointer {projectId: $projectId})-[:ACTIVE]->
                  (graph:ProjectGraph {projectId: $projectId, status: 'Active'})
            MATCH (n:CodeNode {projectId: $projectId, graphId: graph.graphId, key: $nodeKey})
            RETURN n.key AS key, n.kind AS kind, n.name AS name,
                   n.signature AS signature, n.filePath AS filePath,
                   n.startLine AS startLine, n.endLine AS endLine, 1.0 AS score,
                   n.sourceKind AS sourceKind, n.confidence AS confidence,
                   n.extractorId AS extractorId, n.extractorVersion AS extractorVersion,
                   n.reason AS reason, graph.manifestVersion AS manifestVersion,
                   n.contentHash AS contentHash
            """,
            new { projectId, nodeKey });
        var centerRecords = await centerCursor.ToListAsync();
        var center = centerRecords.Count > 0 ? ToHit(centerRecords[0]) : null;

        ct.ThrowIfCancellationRequested();
        var neighborCursor = await transaction.RunAsync(
            $$"""
            MATCH (:ProjectGraphPointer {projectId: $projectId})-[:ACTIVE]->
                  (graph:ProjectGraph {projectId: $projectId, status: 'Active'})
            MATCH path = (n:CodeNode {projectId: $projectId, graphId: graph.graphId, key: $nodeKey})-[rels*1..{{boundedDepth}}]-(m:CodeNode {projectId: $projectId, graphId: graph.graphId})
            WHERE all(node IN nodes(path) WHERE node.graphId = graph.graphId)
            WITH graph, n, m, rels
            RETURN DISTINCT m.key AS key, m.kind AS kind, m.name AS name, m.filePath AS filePath,
                   m.startLine AS startLine, m.endLine AS endLine,
                   type(rels[0]) AS relKind,
                   CASE WHEN startNode(rels[0]) = n THEN 'out' ELSE 'in' END AS direction,
                   [relationship IN rels | relationship.sourceKind] AS sourceKinds,
                   [relationship IN rels | relationship.confidence] AS confidences,
                   [relationship IN rels | relationship.extractorId] AS extractorIds,
                   [relationship IN rels | relationship.extractorVersion] AS extractorVersions,
                   [relationship IN rels | relationship.reason] AS reasons,
                   graph.manifestVersion AS manifestVersion, m.contentHash AS contentHash
            LIMIT $fetchLimit
            """,
            new { projectId, nodeKey, fetchLimit = neighborLimit + 1 });

        var neighbors = new List<GraphNeighborNode>();
        var truncated = false;
        await foreach (var record in neighborCursor)
        {
            if (neighbors.Count == neighborLimit)
            {
                truncated = true;
                break;
            }
            var sourceKinds = RecordStrings(record, "sourceKinds");
            var confidences = RecordStrings(record, "confidences");
            neighbors.Add(new GraphNeighborNode(
                record["key"].As<string>(),
                record["kind"].As<string>(),
                record["name"].As<string>(),
                record["filePath"].As<string?>(),
                record["relKind"].As<string>(),
                record["direction"].As<string>(),
                record["startLine"].As<int?>(),
                record["endLine"].As<int?>(),
                SingleOrUnknown<GraphSourceKind>(sourceKinds),
                WeakestConfidence(confidences),
                JoinDistinct(RecordStrings(record, "extractorIds")),
                JoinDistinct(RecordStrings(record, "extractorVersions")),
                JoinDistinct(RecordStrings(record, "reasons")),
                record["manifestVersion"].As<string?>(),
                record["contentHash"].As<string?>()));
        }

        return new GraphNeighborhood(
            center,
            neighbors,
            Truncated: truncated,
            Depth: boundedDepth);
        });
    }

    public async Task<IReadOnlyList<ImpactPath>> GetReverseCallChainAsync(
        string projectId, string nodeKey, int maxDepth = 3, CancellationToken ct = default)
    {
        const int pathLimit = 50;
        var boundedDepth = Math.Clamp(maxDepth, 1, 8);
        await using var session = OpenReadSession();
        var cursor = await session.RunAsync(
            $$"""
            MATCH (:ProjectGraphPointer {projectId: $projectId})-[:ACTIVE]->
                  (graph:ProjectGraph {projectId: $projectId, status: 'Active'})
            MATCH path = (caller:CodeNode)-[:CALLS|DISPATCHES_TO|IMPLEMENTS|OVERRIDES|READS|WRITES|MAPS_TO|SERIALIZES_TO|MIGRATES|CONTAINS*1..{{boundedDepth}}]-(target:CodeNode {projectId: $projectId, key: $nodeKey})
            WHERE caller.projectId = $projectId
              AND caller.kind IN ['Method', 'Type', 'Endpoint', 'Query', 'Migration', 'Test', 'File']
              AND all(node IN nodes(path) WHERE node.projectId = $projectId AND node.graphId = graph.graphId)
              AND all(node IN nodes(path) WHERE single(other IN nodes(path) WHERE other = node))
              AND all(index IN range(0, size(relationships(path)) - 1) WHERE
                  type(relationships(path)[index]) = 'OVERRIDES' OR
                  startNode(relationships(path)[index]) = nodes(path)[index])
            RETURN [n IN nodes(path) | {
                key: n.key, kind: n.kind, name: n.name, signature: n.signature,
                filePath: n.filePath, startLine: n.startLine, endLine: n.endLine,
                sourceKind: n.sourceKind, confidence: n.confidence,
                extractorId: n.extractorId, extractorVersion: n.extractorVersion,
                reason: n.reason, manifestVersion: graph.manifestVersion,
                contentHash: n.contentHash
            }] AS chain,
            [relationship IN relationships(path) | {
                sourceKind: relationship.sourceKind, confidence: relationship.confidence,
                extractorId: relationship.extractorId,
                extractorVersion: relationship.extractorVersion,
                reason: relationship.reason, manifestVersion: graph.manifestVersion,
                contentHash: relationship.contentHash
            }] AS provenance,
            length(path) = {{boundedDepth}} AND EXISTS {
                MATCH (predecessor:CodeNode {projectId: $projectId})-[previous:CALLS|DISPATCHES_TO|IMPLEMENTS|OVERRIDES|READS|WRITES|MAPS_TO|SERIALIZES_TO|MIGRATES|CONTAINS]-(caller)
                WHERE NOT predecessor IN nodes(path)
                  AND predecessor.graphId = graph.graphId
                  AND (type(previous) = 'OVERRIDES' OR startNode(previous) = predecessor)
            } AS depthTruncated
            LIMIT $fetchLimit
            """,
            new { projectId, nodeKey, fetchLimit = pathLimit + 1 });

        var paths = new List<ImpactPath>(pathLimit + 1);
        await foreach (var record in cursor)
        {
            var chain = record["chain"].As<List<object>>()
                .Select(o =>
                {
                    var map = (IReadOnlyDictionary<string, object>)o;
                    return ToHit(map);
                })
                .ToList();
            var provenance = record["provenance"].As<List<object>>()
                .Cast<IReadOnlyDictionary<string, object>>()
                .ToList();
            var sourceKinds = MapStrings(provenance, "sourceKind");
            var confidences = MapStrings(provenance, "confidence");
            paths.Add(new ImpactPath(
                chain,
                record["depthTruncated"].As<bool>(),
                Math.Max(0, chain.Count - 1),
                SingleOrUnknown<GraphSourceKind>(sourceKinds),
                WeakestConfidence(confidences),
                JoinDistinct(MapStrings(provenance, "extractorId")),
                JoinDistinct(MapStrings(provenance, "extractorVersion")),
                JoinDistinct(MapStrings(provenance, "reason")),
                SingleOrNull(MapStrings(provenance, "manifestVersion")),
                SingleOrNull(MapStrings(provenance, "contentHash"))));
        }
        var resultLimitTruncated = paths.Count > pathLimit;
        return paths.Take(pathLimit)
            .Select(path => resultLimitTruncated ? path with { Truncated = true } : path)
            .ToList();
    }

    public async Task<(int Nodes, int Edges)> GetStatsAsync(string projectId, CancellationToken ct = default)
    {
        await using var session = OpenSession();
        var cursor = await session.RunAsync(
            """
            MATCH (n:CodeNode {projectId: $projectId})
            OPTIONAL MATCH (n)-[r]->(:CodeNode {projectId: $projectId})
            RETURN count(DISTINCT n) AS nodes, count(r) AS edges
            """,
            new { projectId });
        var record = await cursor.SingleAsync();
        return (record["nodes"].As<int>(), record["edges"].As<int>());
    }

    public async Task SaveCommunitySummaryAsync(
        string projectId, string targetManifestVersion,
        string communityId, string title, string summary,
        IReadOnlyList<string> memberKeys, CancellationToken ct = default)
    {
        await using var session = OpenSession();
        var cursor = await session.RunAsync(
            """
            MATCH (:ProjectGraphPointer {projectId: $projectId})-[:ACTIVE]->
                  (g:ProjectGraph {projectId: $projectId, manifestVersion: $targetManifestVersion})
            MERGE (c:Community {
                projectId: $projectId,
                graphId: g.graphId,
                communityId: $communityId
            })
            SET c.title = $title, c.summary = $summary, c.memberKeys = $memberKeys,
                c.manifestVersion = $targetManifestVersion, c.updatedAt = datetime()
            RETURN count(c) AS saved
            """,
            new
            {
                projectId,
                targetManifestVersion,
                communityId,
                title,
                summary,
                memberKeys = memberKeys.ToList(),
            });
        var saved = (await cursor.SingleAsync())["saved"].As<long>();
        if (saved != 1)
            throw new InvalidOperationException(
                $"Community summary target manifest is no longer active: {projectId}/{targetManifestVersion}");
    }

    public async Task<IReadOnlyList<CommunitySummary>> ListCommunitySummariesAsync(
        string projectId, CancellationToken ct = default)
    {
        await using var session = OpenSession();
        var cursor = await session.RunAsync(
            """
            MATCH (:ProjectGraphPointer {projectId: $projectId})-[:ACTIVE]->
                  (g:ProjectGraph {projectId: $projectId})
            MATCH (c:Community {projectId: $projectId, graphId: g.graphId})
            RETURN c.communityId AS id, c.title AS title, c.summary AS summary,
                   c.memberKeys AS memberKeys
            ORDER BY size(c.memberKeys) DESC
            """,
            new { projectId });

        var summaries = new List<CommunitySummary>();
        await foreach (var record in cursor)
        {
            summaries.Add(new CommunitySummary(
                record["id"].As<string>(),
                record["title"].As<string>(),
                record["summary"].As<string>(),
                record["memberKeys"].As<List<string>>()));
        }
        return summaries;
    }

    /// <summary>
    /// 社群偵測。優先使用 Neo4j GDS（Leiden/Louvain）；GDS 未安裝時
    /// fallback 到 namespace 分群（同 namespace = 同社群），保證功能可用。
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> DetectCommunitiesAsync(
        string projectId, CancellationToken ct = default)
    {
        // 嘗試 GDS Louvain
        try
        {
            return await DetectWithGdsAsync(projectId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogInformation(
                "GDS 不可用（{Message}），fallback 至 namespace 分群", ex.Message.Split('\n')[0]);
            ct.ThrowIfCancellationRequested();
            return await DetectByNamespaceAsync(projectId);
        }
    }

    private async Task<IReadOnlyDictionary<string, string>> DetectWithGdsAsync(
        string projectId,
        CancellationToken ct)
    {
        await using var session = OpenSession();
        var graphName = $"wingman-{projectId}";
        try
        {
            ct.ThrowIfCancellationRequested();
            await RunAndConsumeAsync(session,
                "CALL gds.graph.drop($graphName, false)",
                new { graphName });

            ct.ThrowIfCancellationRequested();
            await RunAndConsumeAsync(session,
                """
                MATCH (source:CodeNode {projectId: $projectId})-[r:CALLS|CONTAINS|REFERENCES]->(target:CodeNode {projectId: $projectId})
                WITH gds.graph.project($graphName, source, target) AS g
                RETURN g
                """,
                new { projectId, graphName });

            var cursor = await session.RunAsync(
                """
                CALL gds.louvain.stream($graphName)
                YIELD nodeId, communityId
                RETURN gds.util.asNode(nodeId).key AS key, toString(communityId) AS community
                """,
                new { graphName });

            var map = new Dictionary<string, string>();
            await foreach (var record in cursor)
            {
                ct.ThrowIfCancellationRequested();
                map[record["key"].As<string>()] = record["community"].As<string>();
            }
            return map;
        }
        finally
        {
            await RunAndConsumeAsync(
                session,
                "CALL gds.graph.drop($graphName, false)",
                new { graphName });
        }
    }

    private static async Task RunAndConsumeAsync(
        IAsyncSession session,
        string query,
        object parameters)
    {
        var cursor = await session.RunAsync(query, parameters);
        await cursor.ConsumeAsync();
    }

    public async Task<CodeGraphVisualData> GetVisualGraphAsync(
        string projectId,
        int limit = 1000,
        IReadOnlyList<string>? kinds = null,
        IReadOnlyList<string>? relationTypes = null,
        CancellationToken ct = default)
    {
        limit = ClampVisualLimit(limit);
        var kindFilter = NormalizeFilter(kinds);
        var relationFilter = NormalizeFilter(relationTypes);

        await using var session = OpenReadSession();
        var nodes = new List<CodeGraphVisualNode>();

        var cursor = await session.RunAsync(
            """
            MATCH (n:CodeNode {projectId: $projectId})
            WHERE size($kinds) = 0 OR n.kind IN $kinds
            OPTIONAL MATCH (n)-[r]-(:CodeNode {projectId: $projectId})
            WITH n, count(r) AS degree
            ORDER BY degree DESC, n.kind ASC, n.name ASC
            LIMIT $limit
            RETURN n.key AS id, n.kind AS kind, n.name AS name,
                   n.signature AS signature, n.filePath AS filePath,
                   n.startLine AS startLine, n.endLine AS endLine,
                   n.language AS language, properties(n) AS props, degree
            """,
            new { projectId, kinds = kindFilter, limit });

        await foreach (var record in cursor)
        {
            ct.ThrowIfCancellationRequested();
            nodes.Add(ToVisualNode(record));
        }

        var edges = await LoadEdgesForNodesAsync(
            session,
            projectId,
            nodes.Select(n => n.Id).ToList(),
            relationFilter);

        var totalNodes = await CountProjectNodesAsync(session, projectId);
        return new CodeGraphVisualData(
            nodes,
            edges,
            totalNodes,
            nodes.Count,
            edges.Count,
            totalNodes > nodes.Count);
    }

    public async Task<CodeGraphSchema> GetVisualSchemaAsync(
        string projectId,
        CancellationToken ct = default)
    {
        await using var session = OpenReadSession();

        var kindFacets = new List<CodeGraphFacet>();
        var kindCursor = await session.RunAsync(
            """
            MATCH (n:CodeNode {projectId: $projectId})
            RETURN n.kind AS name, count(n) AS count
            ORDER BY count DESC, name ASC
            """,
            new { projectId });
        await foreach (var record in kindCursor)
        {
            ct.ThrowIfCancellationRequested();
            kindFacets.Add(ToFacet(record));
        }

        var relationFacets = new List<CodeGraphFacet>();
        var relationCursor = await session.RunAsync(
            """
            MATCH (:CodeNode {projectId: $projectId})-[r]->(:CodeNode {projectId: $projectId})
            RETURN type(r) AS name, count(r) AS count
            ORDER BY count DESC, name ASC
            """,
            new { projectId });
        await foreach (var record in relationCursor)
        {
            ct.ThrowIfCancellationRequested();
            relationFacets.Add(ToFacet(record));
        }

        var propertyKeys = new List<string>();
        var propertyCursor = await session.RunAsync(
            """
            MATCH (n:CodeNode {projectId: $projectId})
            UNWIND keys(n) AS key
            RETURN DISTINCT key
            ORDER BY key ASC
            """,
            new { projectId });
        await foreach (var record in propertyCursor)
        {
            ct.ThrowIfCancellationRequested();
            propertyKeys.Add(record["key"].As<string>());
        }

        var (nodes, edges) = await GetStatsAsync(projectId, ct);
        return new CodeGraphSchema(nodes, edges, kindFacets, relationFacets, propertyKeys);
    }

    public async Task<CodeGraphQueryResult> QueryVisualGraphAsync(
        string projectId,
        string cypher,
        int limit = 1000,
        CancellationToken ct = default)
    {
        limit = ClampVisualLimit(limit);
        var safeCypher = EnsureReadOnlyCypher(cypher);
        var boundedCypher = $"CALL {{\n{safeCypher}\n}}\nRETURN *\nLIMIT $limit";

        await using var session = OpenReadSession();
        var cursor = await session.RunAsync(boundedCypher, new { projectId, limit });
        var columns = new List<string>();
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var queryNodes = new Dictionary<string, CodeGraphVisualNode>();
        var elementIdToKey = new Dictionary<string, string>();
        var pendingEdges = new List<PendingVisualEdge>();

        await foreach (var record in cursor)
        {
            ct.ThrowIfCancellationRequested();
            if (columns.Count == 0)
            {
                columns.AddRange(record.Keys);
            }
            var row = new Dictionary<string, object?>();
            foreach (var key in record.Keys)
            {
                var value = record[key];
                ExtractQueryGraphValue(value, projectId, queryNodes, elementIdToKey, pendingEdges);
                row[key] = ToSerializableValue(value);
            }
            rows.Add(row);
        }

        var edges = ResolvePendingEdges(pendingEdges, elementIdToKey);
        var graph = new CodeGraphVisualData(
            queryNodes.Values.ToList(),
            edges,
            queryNodes.Count,
            queryNodes.Count,
            edges.Count,
            false);

        return new CodeGraphQueryResult(columns, rows, graph);
    }

    public async Task<CodeGraphVisualData> GetVisualNeighborsAsync(
        string projectId,
        IReadOnlyList<string> nodeKeys,
        int depth = 1,
        int limit = 1000,
        string mode = "all",
        CancellationToken ct = default)
    {
        limit = ClampVisualLimit(limit);
        depth = Math.Clamp(depth, 1, 3);
        var keys = nodeKeys
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Distinct(StringComparer.Ordinal)
            .Take(50)
            .ToList();

        if (keys.Count == 0)
        {
            return new CodeGraphVisualData(
                Array.Empty<CodeGraphVisualNode>(),
                Array.Empty<CodeGraphVisualEdge>(),
                0,
                0,
                0,
                false);
        }

        var depthToken = depth.ToString();
        var query = mode.ToLowerInvariant() switch
        {
            "callers" => """
                MATCH (seed:CodeNode {projectId: $projectId})
                WHERE seed.key IN $nodeKeys
                OPTIONAL MATCH path = (n:CodeNode {projectId: $projectId})-[:CALLS*1..__DEPTH__]->(seed)
                WITH collect(seed) + collect(DISTINCT n) AS selected
                UNWIND selected AS n
                WITH DISTINCT n
                WHERE n IS NOT NULL
                OPTIONAL MATCH (n)-[r]-(:CodeNode {projectId: $projectId})
                WITH n, count(r) AS degree
                ORDER BY degree DESC, n.kind ASC, n.name ASC
                LIMIT $limit
                RETURN n.key AS id, n.kind AS kind, n.name AS name,
                       n.signature AS signature, n.filePath AS filePath,
                       n.startLine AS startLine, n.endLine AS endLine,
                       n.language AS language, properties(n) AS props, degree
                """.Replace("__DEPTH__", depthToken),
            "callees" => """
                MATCH (seed:CodeNode {projectId: $projectId})
                WHERE seed.key IN $nodeKeys
                OPTIONAL MATCH path = (seed)-[:CALLS*1..__DEPTH__]->(n:CodeNode {projectId: $projectId})
                WITH collect(seed) + collect(DISTINCT n) AS selected
                UNWIND selected AS n
                WITH DISTINCT n
                WHERE n IS NOT NULL
                OPTIONAL MATCH (n)-[r]-(:CodeNode {projectId: $projectId})
                WITH n, count(r) AS degree
                ORDER BY degree DESC, n.kind ASC, n.name ASC
                LIMIT $limit
                RETURN n.key AS id, n.kind AS kind, n.name AS name,
                       n.signature AS signature, n.filePath AS filePath,
                       n.startLine AS startLine, n.endLine AS endLine,
                       n.language AS language, properties(n) AS props, degree
                """.Replace("__DEPTH__", depthToken),
            "same-file" => """
                MATCH (seed:CodeNode {projectId: $projectId})
                WHERE seed.key IN $nodeKeys
                WITH collect(seed) AS seeds, collect(DISTINCT seed.filePath) AS files
                MATCH (n:CodeNode {projectId: $projectId})
                WHERE n IN seeds OR (n.filePath IS NOT NULL AND n.filePath IN files)
                OPTIONAL MATCH (n)-[r]-(:CodeNode {projectId: $projectId})
                WITH n, count(r) AS degree
                ORDER BY degree DESC, n.kind ASC, n.name ASC
                LIMIT $limit
                RETURN n.key AS id, n.kind AS kind, n.name AS name,
                       n.signature AS signature, n.filePath AS filePath,
                       n.startLine AS startLine, n.endLine AS endLine,
                       n.language AS language, properties(n) AS props, degree
                """,
            _ => """
                MATCH (seed:CodeNode {projectId: $projectId})
                WHERE seed.key IN $nodeKeys
                OPTIONAL MATCH path = (seed)-[*1..__DEPTH__]-(n:CodeNode {projectId: $projectId})
                WITH collect(seed) + collect(DISTINCT n) AS selected
                UNWIND selected AS n
                WITH DISTINCT n
                WHERE n IS NOT NULL
                OPTIONAL MATCH (n)-[r]-(:CodeNode {projectId: $projectId})
                WITH n, count(r) AS degree
                ORDER BY degree DESC, n.kind ASC, n.name ASC
                LIMIT $limit
                RETURN n.key AS id, n.kind AS kind, n.name AS name,
                       n.signature AS signature, n.filePath AS filePath,
                       n.startLine AS startLine, n.endLine AS endLine,
                       n.language AS language, properties(n) AS props, degree
                """.Replace("__DEPTH__", depthToken),
        };

        await using var session = OpenReadSession();
        var nodes = new List<CodeGraphVisualNode>();
        var cursor = await session.RunAsync(query, new { projectId, nodeKeys = keys, limit });
        await foreach (var record in cursor)
        {
            ct.ThrowIfCancellationRequested();
            nodes.Add(ToVisualNode(record));
        }

        var edges = await LoadEdgesForNodesAsync(
            session,
            projectId,
            nodes.Select(n => n.Id).ToList(),
            Array.Empty<string>());

        return new CodeGraphVisualData(
            nodes,
            edges,
            nodes.Count,
            nodes.Count,
            edges.Count,
            nodes.Count >= limit);
    }

    private async Task<IReadOnlyDictionary<string, string>> DetectByNamespaceAsync(string projectId)
    {
        await using var session = OpenSession();
        var cursor = await session.RunAsync(
            """
            MATCH (ns:CodeNode {projectId: $projectId, kind: 'Namespace'})-[:CONTAINS]->(t:CodeNode)
            OPTIONAL MATCH (t)-[:CONTAINS]->(m:CodeNode)
            RETURN ns.key AS community, collect(DISTINCT t.key) + collect(DISTINCT m.key) AS members
            """,
            new { projectId });

        var map = new Dictionary<string, string>();
        await foreach (var record in cursor)
        {
            var community = record["community"].As<string>();
            foreach (var member in record["members"].As<List<string>>().Where(m => m is not null))
            {
                map[member] = community;
            }
        }
        return map;
    }

    private static GraphSearchHit ToHit(IRecord record) => new(
        record["key"].As<string>(),
        record["kind"].As<string>(),
        record["name"].As<string>(),
        record["signature"].As<string?>(),
        record["filePath"].As<string?>(),
        record["startLine"].As<int?>(),
        record["score"].As<double>(),
        record["endLine"].As<int?>(),
        ParseEnum(record["sourceKind"].As<string?>(), GraphSourceKind.Unknown),
        ParseEnum(record["confidence"].As<string?>(), GraphConfidence.Unknown),
        record["extractorId"].As<string?>(),
        record["extractorVersion"].As<string?>(),
        record["reason"].As<string?>(),
        record["manifestVersion"].As<string?>(),
        record["contentHash"].As<string?>());

    private static GraphSearchHit ToHit(IReadOnlyDictionary<string, object> map) => new(
        MapString(map, "key") ?? string.Empty,
        MapString(map, "kind") ?? nameof(CodeNodeKind.File),
        MapString(map, "name") ?? string.Empty,
        MapString(map, "signature"),
        MapString(map, "filePath"),
        MapNullableInt(map, "startLine"),
        1.0,
        MapNullableInt(map, "endLine"),
        ParseEnum(MapString(map, "sourceKind"), GraphSourceKind.Unknown),
        ParseEnum(MapString(map, "confidence"), GraphConfidence.Unknown),
        MapString(map, "extractorId"),
        MapString(map, "extractorVersion"),
        MapString(map, "reason"),
        MapString(map, "manifestVersion"),
        MapString(map, "contentHash"));

    private static IReadOnlyList<string> RecordStrings(IRecord record, string key) =>
        record[key].As<List<object>>()
            .Select(ValueToString)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToList();

    private static IReadOnlyList<string> MapStrings(
        IEnumerable<IReadOnlyDictionary<string, object>> maps,
        string key) => maps
        .Select(map => MapString(map, key))
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value!)
        .ToList();

    private static string? MapString(IReadOnlyDictionary<string, object> map, string key) =>
        map.TryGetValue(key, out var value) ? ValueToString(value) : null;

    private static int? MapNullableInt(IReadOnlyDictionary<string, object> map, string key) =>
        map.TryGetValue(key, out var value) ? ValueToNullableInt(value) : null;

    private static TEnum ParseEnum<TEnum>(string? value, TEnum fallback)
        where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) ? parsed : fallback;

    private static TEnum SingleOrUnknown<TEnum>(IEnumerable<string> values)
        where TEnum : struct, Enum
    {
        var parsed = values
            .Select(value => ParseEnum(value, default(TEnum)))
            .Distinct()
            .ToList();
        return parsed.Count == 1 ? parsed[0] : default;
    }

    private static GraphConfidence WeakestConfidence(IEnumerable<string> values)
    {
        var parsed = values.Select(value => ParseEnum(value, GraphConfidence.Unknown)).ToList();
        return parsed.Count == 0
            ? GraphConfidence.Unknown
            : parsed.MaxBy(ConfidenceRank);
    }

    private static int ConfidenceRank(GraphConfidence confidence) => confidence switch
    {
        GraphConfidence.Confirmed => 0,
        GraphConfidence.Exact => 1,
        GraphConfidence.Resolved => 2,
        GraphConfidence.Heuristic => 3,
        GraphConfidence.Inferred => 4,
        _ => 5,
    };

    private static string? JoinDistinct(IEnumerable<string> values)
    {
        var distinct = values.Distinct(StringComparer.Ordinal).ToList();
        return distinct.Count == 0 ? null : string.Join("; ", distinct);
    }

    private static string? SingleOrNull(IEnumerable<string> values)
    {
        var distinct = values.Distinct(StringComparer.Ordinal).Take(2).ToList();
        return distinct.Count == 1 ? distinct[0] : null;
    }

    private static CodeGraphVisualNode ToVisualNode(IRecord record) => new(
        record["id"].As<string>(),
        ValueToString(record["kind"]) ?? "Unknown",
        ValueToString(record["name"]) ?? "(unnamed)",
        ValueToString(record["signature"]),
        ValueToString(record["filePath"]),
        ValueToNullableInt(record["startLine"]),
        ValueToNullableInt(record["endLine"]),
        ValueToString(record["language"]),
        ValueToNullableInt(record["degree"]) ?? 0,
        ToSerializableDictionary(record["props"]));

    private static CodeGraphFacet ToFacet(IRecord record) => new(
        ValueToString(record["name"]) ?? "Unknown",
        ValueToNullableInt(record["count"]) ?? 0);

    private static async Task<string?> GetActiveManifestVersionAsync(
        IAsyncQueryRunner session,
        string projectId)
    {
        var cursor = await session.RunAsync(
            """
            MATCH (:ProjectGraphPointer {projectId: $projectId})-[:ACTIVE]->
                  (g:ProjectGraph {projectId: $projectId, status: 'Active'})
            RETURN g.manifestVersion AS manifestVersion
            """,
            new { projectId });
        var records = await cursor.ToListAsync();
        return records.Count == 1 ? records[0]["manifestVersion"].As<string?>() : null;
    }

    private async Task<int> CountProjectNodesAsync(IAsyncQueryRunner session, string projectId)
    {
        var cursor = await session.RunAsync(
            "MATCH (n:CodeNode {projectId: $projectId}) RETURN count(n) AS count",
            new { projectId });
        var record = await cursor.SingleAsync();
        return ValueToNullableInt(record["count"]) ?? 0;
    }

    private static async Task<IReadOnlyList<CodeGraphVisualEdge>> LoadEdgesForNodesAsync(
        IAsyncQueryRunner session,
        string projectId,
        IReadOnlyList<string> nodeKeys,
        IReadOnlyList<string> relationTypes)
    {
        if (nodeKeys.Count == 0) return Array.Empty<CodeGraphVisualEdge>();

        var edgeLimit = Math.Clamp(nodeKeys.Count * 8, 500, 30000);
        var cursor = await session.RunAsync(
            """
            MATCH (s:CodeNode {projectId: $projectId})-[r]->(t:CodeNode {projectId: $projectId})
            WHERE s.key IN $nodeKeys
              AND t.key IN $nodeKeys
              AND (size($relationTypes) = 0 OR type(r) IN $relationTypes)
            RETURN elementId(r) AS id, s.key AS source, t.key AS target,
                   type(r) AS type, properties(r) AS props
            LIMIT $edgeLimit
            """,
            new { projectId, nodeKeys = nodeKeys.ToList(), relationTypes = relationTypes.ToList(), edgeLimit });

        var edges = new List<CodeGraphVisualEdge>();
        await foreach (var record in cursor)
        {
            edges.Add(new CodeGraphVisualEdge(
                record["id"].As<string>(),
                record["source"].As<string>(),
                record["target"].As<string>(),
                record["type"].As<string>(),
                ToSerializableDictionary(record["props"])));
        }

        return edges;
    }

    internal static string EnsureReadOnlyCypher(string cypher)
    {
        var normalized = StripCypherComments(cypher).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidOperationException("Cypher 查詢不可為空。");

        if (normalized.Contains(';'))
            throw new InvalidOperationException("基於安全考量，知識圖譜頁一次只允許執行一段 read-only Cypher。");

        if (UnsafeReadOnlyCypher.IsMatch(normalized))
            throw new InvalidOperationException("知識圖譜頁只允許 read-only Cypher，已禁止寫入、刪除、管理與 APOC/GDS 類操作。");

        if (InternalGraphLabels.IsMatch(normalized))
            throw new InvalidOperationException("查詢只能讀取 active CodeNode/Community projection，不可存取內部 staging、retired 或 graph anchor。");

        if (UnlabeledNodePattern.IsMatch(normalized))
            throw new InvalidOperationException("圖譜節點必須明確指定 CodeNode 或 Community label，禁止無 label 的全資料庫掃描。");

        if (Regex.IsMatch(normalized, @"\bCALL\b", RegexOptions.IgnoreCase))
            throw new InvalidOperationException("知識圖譜頁不允許執行 procedure CALL；請使用有 project scope 的 MATCH 查詢。");

        var startsReadOnly =
            normalized.StartsWith("MATCH", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("OPTIONAL MATCH", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("WITH", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("RETURN", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("UNWIND", StringComparison.OrdinalIgnoreCase);

        if (!startsReadOnly)
            throw new InvalidOperationException("只允許 MATCH、RETURN、WITH、UNWIND 或安全 CALL 開頭的 read-only Cypher。");

        var projectPatterns = Regex.Matches(
            normalized,
            @"\([^()]*:\s*(?:CodeNode|Community)\b[^()]*\)",
            RegexOptions.IgnoreCase);
        foreach (Match pattern in projectPatterns)
        {
            if (!Regex.IsMatch(
                    pattern.Value,
                    @"\bprojectId\s*:\s*\$projectId\b",
                    RegexOptions.IgnoreCase))
                throw new InvalidOperationException(
                    "每一個 CodeNode 或 Community pattern 都必須直接包含 {projectId: $projectId}，避免跨專案讀取。");
        }

        if (Regex.IsMatch(normalized, @"\b(CodeNode|Community)\b", RegexOptions.IgnoreCase) &&
            projectPatterns.Count == 0)
            throw new InvalidOperationException("無法證明查詢具有完整 project scope，已拒絕執行。");

        return normalized;
    }

    private static string StripCypherComments(string cypher)
    {
        var withoutBlock = Regex.Replace(cypher, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return Regex.Replace(withoutBlock, @"//.*?$", "", RegexOptions.Multiline);
    }

    private static int ClampVisualLimit(int limit) => Math.Clamp(limit, 50, 10000);

    private static IReadOnlyList<string> NormalizeFilter(IReadOnlyList<string>? values) =>
        values is null
            ? Array.Empty<string>()
            : values
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

    private static void ExtractQueryGraphValue(
        object? value,
        string projectId,
        IDictionary<string, CodeGraphVisualNode> nodes,
        IDictionary<string, string> elementIdToKey,
        IList<PendingVisualEdge> pendingEdges)
    {
        switch (value)
        {
            case null:
                return;
            case INode node:
                TryAddQueryNode(node, projectId, nodes, elementIdToKey);
                return;
            case IRelationship relationship:
                pendingEdges.Add(new PendingVisualEdge(
                    relationship.ElementId,
                    relationship.StartNodeElementId,
                    relationship.EndNodeElementId,
                    relationship.Type,
                    ToSerializableDictionary(relationship.Properties)));
                return;
            case IPath path:
                foreach (var node in path.Nodes)
                {
                    TryAddQueryNode(node, projectId, nodes, elementIdToKey);
                }
                foreach (var relationship in path.Relationships)
                {
                    pendingEdges.Add(new PendingVisualEdge(
                        relationship.ElementId,
                        relationship.StartNodeElementId,
                        relationship.EndNodeElementId,
                        relationship.Type,
                        ToSerializableDictionary(relationship.Properties)));
                }
                return;
            case IReadOnlyDictionary<string, object> map:
                foreach (var item in map.Values)
                {
                    ExtractQueryGraphValue(item, projectId, nodes, elementIdToKey, pendingEdges);
                }
                return;
            case IEnumerable<object> items:
                foreach (var item in items)
                {
                    ExtractQueryGraphValue(item, projectId, nodes, elementIdToKey, pendingEdges);
                }
                return;
        }
    }

    private static void TryAddQueryNode(
        INode node,
        string projectId,
        IDictionary<string, CodeGraphVisualNode> nodes,
        IDictionary<string, string> elementIdToKey)
    {
        if (!node.Labels.Contains("CodeNode")) return;
        if (!node.Properties.TryGetValue("projectId", out var nodeProjectId)) return;
        if (!string.Equals(ValueToString(nodeProjectId), projectId, StringComparison.Ordinal)) return;
        if (!node.Properties.TryGetValue("key", out var keyValue)) return;

        var key = ValueToString(keyValue);
        if (string.IsNullOrWhiteSpace(key)) return;

        elementIdToKey[node.ElementId] = key;
        if (nodes.ContainsKey(key)) return;

        var props = ToSerializableDictionary(node.Properties);
        nodes[key] = new CodeGraphVisualNode(
            key,
            GetMapString(props, "kind") ?? "Unknown",
            GetMapString(props, "name") ?? "(unnamed)",
            GetMapString(props, "signature"),
            GetMapString(props, "filePath"),
            ValueToNullableInt(props.TryGetValue("startLine", out var startLine) ? startLine : null),
            ValueToNullableInt(props.TryGetValue("endLine", out var endLine) ? endLine : null),
            GetMapString(props, "language"),
            0,
            props);
    }

    private static IReadOnlyList<CodeGraphVisualEdge> ResolvePendingEdges(
        IEnumerable<PendingVisualEdge> pendingEdges,
        IReadOnlyDictionary<string, string> elementIdToKey)
    {
        var edges = new Dictionary<string, CodeGraphVisualEdge>();
        foreach (var edge in pendingEdges)
        {
            if (!elementIdToKey.TryGetValue(edge.StartElementId, out var source)) continue;
            if (!elementIdToKey.TryGetValue(edge.EndElementId, out var target)) continue;
            if (edges.ContainsKey(edge.Id)) continue;

            edges[edge.Id] = new CodeGraphVisualEdge(
                edge.Id,
                source,
                target,
                edge.Type,
                edge.Properties);
        }
        return edges.Values.ToList();
    }

    private static IReadOnlyDictionary<string, object?> ToSerializableDictionary(object? value)
    {
        if (value is IReadOnlyDictionary<string, object> map)
        {
            return map
                .Where(kv => kv.Key != "projectId")
                .ToDictionary(kv => kv.Key, kv => ToSerializableValue(kv.Value));
        }

        return new Dictionary<string, object?>();
    }

    private static object? ToSerializableValue(object? value) => value switch
    {
        null => null,
        string or bool or int or long or double or float or decimal => value,
        INode node => new Dictionary<string, object?>
        {
            ["elementId"] = node.ElementId,
            ["labels"] = node.Labels.ToList(),
            ["properties"] = ToSerializableDictionary(node.Properties),
        },
        IRelationship relationship => new Dictionary<string, object?>
        {
            ["elementId"] = relationship.ElementId,
            ["startNodeElementId"] = relationship.StartNodeElementId,
            ["endNodeElementId"] = relationship.EndNodeElementId,
            ["type"] = relationship.Type,
            ["properties"] = ToSerializableDictionary(relationship.Properties),
        },
        IPath path => new Dictionary<string, object?>
        {
            ["nodes"] = path.Nodes.Select(ToSerializableValue).ToList(),
            ["relationships"] = path.Relationships.Select(ToSerializableValue).ToList(),
        },
        IReadOnlyDictionary<string, object> map => map.ToDictionary(kv => kv.Key, kv => ToSerializableValue(kv.Value)),
        IEnumerable<object> list => list.Select(ToSerializableValue).ToList(),
        _ => value.ToString(),
    };

    private static string? GetMapString(IReadOnlyDictionary<string, object?> map, string key) =>
        map.TryGetValue(key, out var value) ? ValueToString(value) : null;

    private static string? ValueToString(object? value) => value switch
    {
        null => null,
        string s => s,
        _ => value.ToString(),
    };

    private static int? ValueToNullableInt(object? value) => value switch
    {
        null => null,
        int i => i,
        long l => (int)l,
        short s => s,
        double d => (int)d,
        float f => (int)f,
        decimal d => (int)d,
        _ when int.TryParse(value.ToString(), out var parsed) => parsed,
        _ => null,
    };

    private sealed record PendingVisualEdge(
        string Id,
        string StartElementId,
        string EndElementId,
        string Type,
        IReadOnlyDictionary<string, object?> Properties);

    private static string ToRelType(CodeEdgeKind kind) => kind switch
    {
        CodeEdgeKind.Contains => "CONTAINS",
        CodeEdgeKind.Calls => "CALLS",
        CodeEdgeKind.Implements => "IMPLEMENTS",
        CodeEdgeKind.Inherits => "INHERITS",
        CodeEdgeKind.References => "REFERENCES",
        CodeEdgeKind.DeclaredIn => "DECLARED_IN",
        CodeEdgeKind.ProjectReferences => "PROJECT_REFERENCES",
        CodeEdgeKind.DependsOnPackage => "DEPENDS_ON_PACKAGE",
        CodeEdgeKind.DispatchesTo => "DISPATCHES_TO",
        CodeEdgeKind.Overrides => "OVERRIDES",
        CodeEdgeKind.Tests => "TESTS",
        CodeEdgeKind.Covers => "COVERS",
        CodeEdgeKind.Handles => "HANDLES",
        CodeEdgeKind.Consumes => "CONSUMES",
        CodeEdgeKind.Produces => "PRODUCES",
        CodeEdgeKind.BindsConfiguration => "BINDS_CONFIGURATION",
        CodeEdgeKind.MapsTo => "MAPS_TO",
        CodeEdgeKind.Reads => "READS",
        CodeEdgeKind.Writes => "WRITES",
        CodeEdgeKind.ForeignKeyTo => "FK_TO",
        CodeEdgeKind.Migrates => "MIGRATES",
        CodeEdgeKind.SerializesTo => "SERIALIZES_TO",
        CodeEdgeKind.Publishes => "PUBLISHES",
        CodeEdgeKind.Aliases => "ALIASES",
        CodeEdgeKind.SupportedBy => "SUPPORTED_BY",
        _ => "RELATED",
    };

    internal static string BuildLuceneQuery(string query)
    {
        var terms = Regex.Matches(query, @"[\p{L}\p{N}_][\p{L}\p{N}_.-]*")
            .Select(match => EscapeLuceneTerm(match.Value))
            .Where(term => term.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(16)
            .ToList();
        return string.Join(" OR ", terms);
    }

    private static string EscapeLuceneTerm(string query)
    {
        const string specials = "+-&|!(){}[]^\"~*?:\\/";
        var escaped = new System.Text.StringBuilder(query.Length + 8);
        foreach (var character in query)
        {
            if (specials.Contains(character))
                escaped.Append('\\');
            escaped.Append(character);
        }
        return escaped.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        if (_driver is not null)
            await _driver.DisposeAsync();
    }
}
