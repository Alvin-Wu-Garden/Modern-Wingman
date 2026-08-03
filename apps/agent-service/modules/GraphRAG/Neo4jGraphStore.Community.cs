using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Neo4j.Driver;

namespace AgentService.Modules.GraphRAG;

/// <summary>
/// Neo4j Graph Store 的 Community template、分層檢索與 AI Summary CAS 更新功能。
/// 此 partial 僅拆分職責，不建立第二份 Driver 或狀態。
/// </summary>
public sealed partial class Neo4jGraphStore
{
    private const int FullTextPageSize = 512;
    private const int MaximumScopedSearchCandidates = 2_000;



    /// <inheritdoc />
    public async Task SaveCommunityTemplatesAsync(
        string projectId,
        string graphVersion,
        IReadOnlyList<GraphCommunityReportV4> templates,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(graphVersion);
        ArgumentNullException.ThrowIfNull(templates);
        if (_driver is null)
            throw new InvalidOperationException("Neo4j V4 已停用。");
        await using var session = OpenWriteSession();
        cancellationToken.ThrowIfCancellationRequested();
        await session.ExecuteWriteAsync(async transaction =>
        {
            var delete = await transaction.RunAsync(
                """
                MATCH (c:GraphCommunity {
                    projectId: $projectId,
                    graphVersion: $graphVersion
                })
                DELETE c
                """,
                new { projectId, graphVersion });
            await delete.ConsumeAsync();
            if (templates.Count == 0) return;
            foreach (var batch in templates.Chunk(_options.WriteBatchSize))
            {
                var rows = batch.Select(template => new
                {
                    template.CommunityId,
                    template.Tier,
                    template.ParentCommunityId,
                    template.Resolved,
                    template.Title,
                    template.Summary,
                    template.SummaryState,
                    template.RetryCount,
                    memberIdsJson = JsonSerializer.Serialize(
                        template.MemberIds,
                        JsonOptions),
                    template.MemberCount,
                    topTablesJson = JsonSerializer.Serialize(
                        template.TopTables,
                        JsonOptions),
                    topEntryPointsJson = JsonSerializer.Serialize(
                        template.TopEntryPoints,
                        JsonOptions),
                    template.CacheKey,
                    template.Truncated,
                    template.TruncatedMemberCount,
                    attributesJson = JsonSerializer.Serialize(
                        template.Attributes,
                        JsonOptions),
                }).ToList();
                var insert = await transaction.RunAsync(
                    """
                    UNWIND $rows AS row
                    CREATE (c:GraphCommunity {
                        projectId: $projectId,
                        graphVersion: $graphVersion,
                        communityId: row.CommunityId,
                        tier: row.Tier,
                        parentCommunityId: row.ParentCommunityId,
                        resolved: row.Resolved,
                        title: row.Title,
                        summary: row.Summary,
                        summaryState: row.SummaryState,
                        retryCount: row.RetryCount,
                        memberIdsJson: row.memberIdsJson,
                        memberCount: row.MemberCount,
                        topTablesJson: row.topTablesJson,
                        topEntryPointsJson: row.topEntryPointsJson,
                        cacheKey: row.CacheKey,
                        truncated: row.Truncated,
                        truncatedMemberCount: row.TruncatedMemberCount,
                        attributesJson: row.attributesJson
                    })
                    """,
                    new { projectId, graphVersion, rows });
                await insert.ConsumeAsync();
            }
        });
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GraphCommunityReportV4>> ListCommunityTemplatesAsync(
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
                MATCH (c:GraphCommunity {
                    projectId: $projectId,
                    graphVersion: p.activeManifestVersion
                })
                RETURN c
                ORDER BY c.tier, c.parentCommunityId, c.communityId
                """,
                new { projectId });
            return await ReadCommunityTemplatesAsync(cursor);
        });
    }

    /// <inheritdoc />
    public async Task<GraphCommunityAcceptanceDiagnostics?>
        GetCommunityAcceptanceDiagnosticsAsync(
            string projectId,
            CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        if (_driver is null) return null;

        var graphVersion = await GetActiveManifestAsync(projectId, cancellationToken);
        if (string.IsNullOrWhiteSpace(graphVersion))
            return null;

        var templates = await ListCommunityTemplatesAsync(
            projectId,
            cancellationToken);
        var c0 = templates.Where(report => report.Tier == "C0").ToList();
        var c1 = templates.Where(report => report.Tier == "C1").ToList();
        var c2 = templates.Where(report => report.Tier == "C2").ToList();
        var c0Ids = c0.Select(report => report.CommunityId)
            .ToHashSet(StringComparer.Ordinal);
        var resolvedC1 = c1.Where(report => report.Resolved).ToList();
        var unresolvedC1 = c1.Where(report => !report.Resolved).ToList();

        await using var session = OpenReadSession();
        cancellationToken.ThrowIfCancellationRequested();
        var counts = await session.ExecuteReadAsync(async transaction =>
        {
            var cursor = await transaction.RunAsync(
                """
                MATCH (n:GraphEntity {
                    projectId: $projectId,
                    graphVersion: $graphVersion
                })
                OPTIONAL MATCH (c:GraphCommunity {
                    projectId: $projectId,
                    graphVersion: $graphVersion,
                    communityId: n.communityId
                })
                RETURN
                    sum(CASE
                        WHEN n.kind = 'Feature'
                         AND n.role IN [
                            'menu-feature',
                            'custom-report',
                            'approval-feature',
                            'schedule',
                            'batch-report'
                         ]
                         AND coalesce(n.state, '') <> 'inactive'
                        THEN 1 ELSE 0 END) AS eligibleAnchors,
                    sum(CASE
                        WHEN coalesce(n.degree, 0) > 0
                         AND n.kind IN ['Feature', 'EntryPoint', 'Code']
                        THEN 1 ELSE 0 END) AS connectedEligible,
                    sum(CASE
                        WHEN coalesce(n.degree, 0) > 0
                         AND n.kind IN ['Feature', 'EntryPoint', 'Code']
                         AND n.communityId IS NOT NULL
                         AND trim(n.communityId) <> ''
                         AND c IS NOT NULL
                        THEN 1 ELSE 0 END) AS connectedAssigned
                """,
                new { projectId, graphVersion });
            var row = await cursor.SingleAsync();
            return (
                EligibleAnchors: row["eligibleAnchors"].As<int>(),
                ConnectedEligible: row["connectedEligible"].As<int>(),
                ConnectedAssigned: row["connectedAssigned"].As<int>());
        });

        // Digest 僅依 tier/community/member IDs；相同 snapshot/config 的輸入順序
        // 即使不同，也會先排序後得到相同值，可直接用於 B9 重現性比對。
        var digestPayload = string.Join(
            '\n',
            templates.OrderBy(report => report.Tier, StringComparer.Ordinal)
                .ThenBy(report => report.CommunityId, StringComparer.Ordinal)
                .Select(report =>
                    $"{report.Tier}\0{report.CommunityId}\0" +
                    string.Join(
                        '\0',
                        report.MemberIds.OrderBy(value => value, StringComparer.Ordinal))));

        return new GraphCommunityAcceptanceDiagnostics(
            projectId,
            graphVersion,
            c0.Count,
            counts.EligibleAnchors,
            c1.Count,
            resolvedC1.Count,
            unresolvedC1.Count,
            resolvedC1.Count == 0
                ? null
                : resolvedC1.Min(report => report.MemberCount),
            resolvedC1.Count == 0
                ? null
                : resolvedC1.Max(report => report.MemberCount),
            unresolvedC1.Count(report =>
                report.MemberCount is < 1 or > 2 ||
                !string.Equals(
                    report.Attributes.GetValueOrDefault("resolutionState"),
                    "unresolved",
                    StringComparison.Ordinal)),
            c1.Count(report =>
                string.IsNullOrWhiteSpace(report.ParentCommunityId) ||
                !c0Ids.Contains(report.ParentCommunityId)),
            c2.Count,
            c2.Count == 0 ? null : c2.Min(report => report.MemberCount),
            c2.Count(report => report.MemberCount < 3),
            counts.ConnectedEligible,
            counts.ConnectedAssigned,
            counts.ConnectedEligible - counts.ConnectedAssigned,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(digestPayload)))
                .ToLowerInvariant());
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GraphCommunityReportV4>> GetCommunityTemplatesByIdsAsync(
        string projectId,
        IReadOnlyList<string> communityIds,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(communityIds);
        var ids = communityIds
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .Take(20)
            .ToList();
        limit = Math.Clamp(limit, 1, 20);
        if (_driver is null || ids.Count == 0) return [];
        await using var session = OpenReadSession();
        cancellationToken.ThrowIfCancellationRequested();
        return await session.ExecuteReadAsync(async transaction =>
        {
            var cursor = await transaction.RunAsync(
                """
                MATCH (p:ProjectGraph {projectId: $projectId})
                MATCH (c:GraphCommunity {
                    projectId: $projectId,
                    graphVersion: p.activeManifestVersion
                })
                WHERE c.communityId IN $communityIds
                RETURN c
                ORDER BY c.tier, c.communityId
                LIMIT $limit
                """,
                new { projectId, communityIds = ids, limit });
            return await ReadCommunityTemplatesAsync(cursor);
        });
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<GraphCommunityReportV4>> SearchC0CommunityTemplatesAsync(
        string projectId,
        string query,
        int limit = 2,
        CancellationToken cancellationToken = default) =>
        SearchCommunityTemplatesAsync(
            projectId,
            query,
            tier: "C0",
            parentCommunityId: null,
            Math.Clamp(limit, 1, 2),
            cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<GraphCommunityReportV4>> SearchC1CommunityTemplatesAsync(
        string projectId,
        string parentCommunityId,
        string query,
        int limit = 12,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentCommunityId);
        return SearchCommunityTemplatesAsync(
            projectId,
            query,
            tier: "C1",
            parentCommunityId,
            Math.Clamp(limit, 1, 12),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<GraphCommunityReportV4>> SearchC2CommunityTemplatesAsync(
        string projectId,
        string query,
        int limit = 5,
        CancellationToken cancellationToken = default) =>
        SearchCommunityTemplatesAsync(
            projectId,
            query,
            tier: "C2",
            parentCommunityId: null,
            Math.Clamp(limit, 1, 5),
            cancellationToken);

    /// <inheritdoc />
    public async Task<bool> TryUpdateCommunitySummaryAsync(
        string projectId,
        string graphVersion,
        string communityId,
        string expectedCacheKey,
        string summary,
        string summaryState,
        int retryCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(graphVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(communityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedCacheKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);
        if (summaryState is not (
            GraphCommunitySummaryStates.Template or
            GraphCommunitySummaryStates.Queued or
            GraphCommunitySummaryStates.Running or
            GraphCommunitySummaryStates.AiReady or
            GraphCommunitySummaryStates.Failed))
            throw new ArgumentOutOfRangeException(
                nameof(summaryState),
                "未知 Community summary state。");
        retryCount = Math.Clamp(retryCount, 0, 2);
        if (_driver is null) return false;

        await using var session = OpenWriteSession();
        cancellationToken.ThrowIfCancellationRequested();
        return await session.ExecuteWriteAsync(async transaction =>
        {
            var cursor = await transaction.RunAsync(
                """
                MATCH (p:ProjectGraph {projectId: $projectId})
                MATCH (c:GraphCommunity {
                    projectId: $projectId,
                    graphVersion: $graphVersion,
                    communityId: $communityId,
                    cacheKey: $expectedCacheKey
                })
                WHERE p.activeManifestVersion = $graphVersion
                SET c.summary = $summary,
                    c.summaryState = $summaryState,
                    c.retryCount = $retryCount,
                    c.updatedAt = datetime()
                RETURN count(c) AS updated
                """,
                new
                {
                    projectId,
                    graphVersion,
                    communityId,
                    expectedCacheKey,
                    summary,
                    summaryState,
                    retryCount,
                });
            return (await cursor.SingleAsync())["updated"].As<int>() == 1;
        });
    }

    /// <summary>
    /// Community 搜尋在 Neo4j 端先套 active version、tier、parent 與 LIMIT，
    /// 避免 Global Search 將全部 C1/C2 載回應用程式排序。
    /// </summary>
    private async Task<IReadOnlyList<GraphCommunityReportV4>> SearchCommunityTemplatesAsync(
        string projectId,
        string query,
        string tier,
        string? parentCommunityId,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (tier is not ("C0" or "C1" or "C2"))
            throw new ArgumentOutOfRangeException(nameof(tier));
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

            // Community full-text 與 Entity full-text 相同，index 包含所有專案與
            // retained versions。逐頁掃描 global score，直到 active scope 有足夠
            // 候選，避免固定 5000 筆在大型安裝環境仍被其他版本截斷。
            var candidates = new List<ScoredCommunityTemplate>();
            var requiredCandidates = Math.Max(limit * 4, limit);
            var skip = 0;
            while (candidates.Count < requiredCandidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var cursor = await transaction.RunAsync(
                    """
                    CALL db.index.fulltext.queryNodes(
                        'graph_community_v4_search',
                        $query,
                        {skip: $skip, limit: $pageSize}
                    )
                    YIELD node, score
                    RETURN node AS c, score
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
                    var node = cursor.Current["c"].As<INode>();
                    if (!IsActiveSearchScope(node, projectId, graphVersion) ||
                        !string.Equals(
                            StringProperty(node.Properties, "tier"),
                            tier,
                            StringComparison.Ordinal) ||
                        parentCommunityId is not null &&
                        !string.Equals(
                            StringProperty(node.Properties, "parentCommunityId"),
                            parentCommunityId,
                            StringComparison.Ordinal))
                        continue;
                    candidates.Add(new ScoredCommunityTemplate(
                        MapCommunityTemplate(node),
                        cursor.Current["score"].As<double>()));
                }
                skip += pageCount;
                if (pageCount < FullTextPageSize)
                    break;
            }
            return candidates
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.Template.CommunityId, StringComparer.Ordinal)
                .Take(limit)
                .Select(candidate => candidate.Template)
                .ToList();
        });
    }

    private static async Task<IReadOnlyList<GraphCommunityReportV4>>
        ReadCommunityTemplatesAsync(IResultCursor cursor)
    {
        var templates = new List<GraphCommunityReportV4>();
        while (await cursor.FetchAsync())
            templates.Add(MapCommunityTemplate(cursor.Current["c"].As<INode>()));

        return templates;
    }

    /// <summary>將 Neo4j Community node 轉成 immutable V4 template。</summary>
    private static GraphCommunityReportV4 MapCommunityTemplate(INode node)
    {
        var properties = node.Properties;
        return new GraphCommunityReportV4(
            RequiredString(properties, "communityId"),
            RequiredString(properties, "tier"),
            StringProperty(properties, "parentCommunityId"),
            properties.TryGetValue("resolved", out var resolved) &&
            resolved.As<bool>(),
            RequiredString(properties, "title"),
            RequiredString(properties, "summary"),
            RequiredString(properties, "summaryState"),
            IntProperty(properties, "retryCount") ?? 0,
            DeserializeList(StringProperty(properties, "memberIdsJson")),
            IntProperty(properties, "memberCount") ?? 0,
            DeserializeList(StringProperty(properties, "topTablesJson")),
            DeserializeList(StringProperty(properties, "topEntryPointsJson")),
            RequiredString(properties, "cacheKey"),
            properties.TryGetValue("truncated", out var truncated) &&
            truncated.As<bool>(),
            IntProperty(properties, "truncatedMemberCount") ?? 0,
            DeserializeDictionary(StringProperty(properties, "attributesJson")));
    }

    /// <summary>
    /// 在應用程式端比對 full-text hit 的 project/version。
    /// 先分頁再篩選可避免其他專案或 retained version 的高分結果造成 starvation。
    /// </summary>
    private static bool IsActiveSearchScope(
        INode node,
        string projectId,
        string graphVersion) =>
        node.Properties.TryGetValue("projectId", out var projectValue) &&
        node.Properties.TryGetValue("graphVersion", out var versionValue) &&
        string.Equals(projectValue.As<string>(), projectId, StringComparison.Ordinal) &&
        string.Equals(versionValue.As<string>(), graphVersion, StringComparison.Ordinal);

    private sealed record ScoredCommunityTemplate(
        GraphCommunityReportV4 Template,
        double Score);
}
