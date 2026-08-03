using System.Diagnostics;
using AgentService.Application.Atlassian;
using AgentService.Modules.GraphRAG;
using Microsoft.Extensions.Logging;
using GraphNode = AgentService.Modules.GraphRAG.LegacyGraphNode;
using GraphNodeKind = AgentService.Modules.GraphRAG.LegacyGraphNodeKind;
using GraphRelationship = AgentService.Modules.GraphRAG.LegacyGraphRelationship;
using ScoredGraphNode = AgentService.Modules.GraphRAG.LegacyScoredGraphNode;
using GraphRetrievalContext = AgentService.Modules.GraphRAG.LegacyGraphRetrievalContext;

namespace AgentService.Infrastructure.Atlassian;

/// <summary>
/// 依規格第 5 節執行三階段 GraphRAG 檢索，並將結果整理為 <see cref="JiraGraphRagContext"/>。
///
/// Stage 1：每個高信心功能候選各以代號+名稱、代號、名稱分別呼叫 LocalSearch 定位入口。
/// Stage 2：LocalSearch 內建 BFS 擴展，會自動從種子展開呼叫鏈與資料關聯。
/// Stage 3：Stage 1 入口不足時，以 JIRA Summary 執行語意補充搜尋。
///
/// 所有查詢均以 projectId 隔離，Neo4j 不可用時降級回傳空 context 而不拋例外。
/// </summary>
public sealed class JiraGraphRagRetrievalService(
    GraphRetrievalService graphRag,
    ILogger<JiraGraphRagRetrievalService> logger)
{
    /// <summary>每個功能候選最多送出的 LocalSearch 查詢數。</summary>
    private const int MaxQueriesPerFeature = 4;

    /// <summary>最多處理的功能候選數量。</summary>
    private const int MaxFeatureCandidates = 8;

    /// <summary>單次 LocalSearch 最多納入 context 的 node 數。</summary>
    private const int MaxHitsPerQuery = 60;

    /// <summary>Context 總 token 預算（粗估 4 字元/token）。</summary>
    private const int TokenBudget = 12_000;

    /// <summary>確認入口所需的最低分數門檻。</summary>
    private const double EntryPointConfirmScore = 0.50;

    private static readonly IReadOnlySet<string> EntryPointRoles = new HashSet<string>(
        [
            GraphRoles.ControllerAction,
            GraphRoles.Controller,
            GraphRoles.WebRoute,
            GraphRoles.MenuFeature,
            GraphRoles.ScheduledTask,
            GraphRoles.MessageConsumer,
            GraphRoles.FrontendPage,
            GraphRoles.CliCommand,
            GraphRoles.Schedule,
        ],
        StringComparer.Ordinal);

    // ─── 公開入口 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 依 JIRA 議題與功能識別清單執行三階段 GraphRAG 檢索。
    /// </summary>
    /// <param name="projectId">Wingman 專案識別碼，所有查詢均限定於此專案。</param>
    /// <param name="issue">完整正規化 JIRA 議題。</param>
    /// <param name="identifiers">由 JiraFeatureIdentifierExtractor 擷取的功能候選清單。</param>
    /// <param name="ct">可取消本次整批檢索。</param>
    /// <returns>彙整後的 JiraGraphRagContext；Neo4j 不可用時回傳降級 context。</returns>
    public async Task<JiraGraphRagContext> RetrieveAsync(
        string projectId,
        NormalizedJiraIssue issue,
        IReadOnlyList<JiraFeatureIdentifier> identifiers,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return JiraGraphRagContext.Degraded("ProjectId 無效，已停止 GraphRAG 檢索。");
        }

        var sw = Stopwatch.StartNew();
        var allNodes = new Dictionary<string, (ScoredGraphNode Node, string? FeatureCode, string? FeatureName, string Query)>(StringComparer.Ordinal);
        var allEdgeIds = new HashSet<string>(StringComparer.Ordinal);
        var executedQueries = new List<string>();
        var warnings = new List<string>();
        bool wasDegraded = false;

        // ── 選取高信心功能候選（上限 8 個）───────────────────────────────────────
        var selected = identifiers
            .Where(id => id.IsConfirmed || id.Confidence >= 0.60)
            .OrderByDescending(id => id.Confidence)
            .ThenByDescending(id => id.OccurrenceCount)
            .Take(MaxFeatureCandidates)
            .ToList();

        // ── Stage 1 + 2：每個功能分別定位入口，BFS 自動展開呼叫鏈 ───────────────
        foreach (var feature in selected)
        {
            var queries = BuildFeatureQueries(feature);
            int queriesRun = 0;
            bool foundEntry = false;

            foreach (var q in queries)
            {
                if (queriesRun >= MaxQueriesPerFeature)
                {
                    break;
                }

                if (executedQueries.Contains(q, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                GraphRetrievalContext ctx;
                try
                {
                    ctx = await graphRag.LocalSearchAsync(projectId, q, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        ex,
                        "GraphRAG LocalSearch 失敗，略過此查詢。ProjectId={ProjectId}, Query={Query}",
                        projectId,
                        q);
                    warnings.Add($"查詢「{q}」執行失敗，已略過。");
                    wasDegraded = true;
                    executedQueries.Add(q);
                    queriesRun++;
                    continue;
                }

                executedQueries.Add(q);
                queriesRun++;

                foreach (var scored in ctx.Nodes.Take(MaxHitsPerQuery))
                {
                    if (allNodes.TryGetValue(scored.Node.Id, out var existing))
                    {
                        // 保留較高分數，但合併兩個 FeatureCode/FeatureName
                        if (scored.Score > existing.Node.Score)
                        {
                            allNodes[scored.Node.Id] = (scored, feature.FeatureCode ?? existing.FeatureCode, feature.FeatureName ?? existing.FeatureName, q);
                        }
                    }
                    else
                    {
                        allNodes[scored.Node.Id] = (scored, feature.FeatureCode, feature.FeatureName, q);
                    }

                    if (IsEntryPoint(scored.Node) && scored.Score >= EntryPointConfirmScore)
                    {
                        foundEntry = true;
                    }
                }

                foreach (var edge in ctx.Edges)
                {
                    allEdgeIds.Add(edge.Id);
                }

                // 若第一個查詢已找到高分入口，不需繼續後續低優先查詢
                if (foundEntry && queriesRun >= 2)
                {
                    break;
                }
            }
        }

        // ── Stage 3：若入口不足，以 JIRA Summary 補充語意搜尋 ────────────────────
        var entryNodes = allNodes.Values.Where(v => IsEntryPoint(v.Node.Node)).ToList();
        bool needsFallback = entryNodes.Count == 0
            || (selected.Count > 0 && entryNodes.Count < selected.Count);

        if (needsFallback && !string.IsNullOrWhiteSpace(issue.Preview.Summary))
        {
            var fallbackQuery = issue.Preview.Summary;
            if (!executedQueries.Contains(fallbackQuery, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var fallbackCtx = await graphRag.LocalSearchAsync(projectId, fallbackQuery, ct);
                    executedQueries.Add(fallbackQuery);

                    foreach (var scored in fallbackCtx.Nodes.Take(MaxHitsPerQuery))
                    {
                        if (!allNodes.ContainsKey(scored.Node.Id))
                        {
                            allNodes[scored.Node.Id] = (scored, null, null, fallbackQuery);
                        }
                    }

                    foreach (var edge in fallbackCtx.Edges)
                    {
                        allEdgeIds.Add(edge.Id);
                    }

                    if (allNodes.Count == 0)
                    {
                        warnings.Add("Stage 3 語意補充搜尋查無結果，改以純 JIRA 內容繼續分析。");
                        wasDegraded = true;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        ex,
                        "GraphRAG Stage 3 語意補充搜尋失敗。ProjectId={ProjectId}",
                        projectId);
                    warnings.Add("語意補充搜尋失敗，已以現有結果繼續。");
                    wasDegraded = true;
                }
            }
        }

        // ── 整理結果 ───────────────────────────────────────────────────────────
        var ranked = allNodes.Values
            .OrderByDescending(v => EntryPointSortPriority(v.Node.Node))
            .ThenByDescending(v => v.Node.Score)
            .ToList();

        var (confirmed, candidates) = ClassifyEntryPoints(ranked);

        var hits = BuildHits(projectId, ranked);
        var (included, wasTruncated) = TruncateToTokenBudget(hits);
        var estimatedTokens = EstimateTokens(included);

        logger.LogInformation(
            "JIRA GraphRAG 檢索完成。ProjectId={ProjectId}, Features={FeatureCount}, Queries={QueryCount}, TotalNodes={TotalNodes}, Confirmed={ConfirmedCount}, Candidates={CandidateCount}, Included={IncludedCount}, Degraded={Degraded}, ElapsedMs={ElapsedMs}",
            projectId,
            selected.Count,
            executedQueries.Count,
            allNodes.Count,
            confirmed.Count,
            candidates.Count,
            included.Count,
            wasDegraded,
            sw.ElapsedMilliseconds);

        return new JiraGraphRagContext(
            identifiers,
            executedQueries,
            confirmed,
            candidates,
            included,
            hits.Count,
            included.Count,
            wasTruncated,
            wasDegraded,
            warnings,
            estimatedTokens);
    }

    // ─── 私有：建立每個功能的查詢清單 ──────────────────────────────────────────

    private static IReadOnlyList<string> BuildFeatureQueries(JiraFeatureIdentifier feature)
    {
        var queries = new List<string>();

        var hasCode = !string.IsNullOrWhiteSpace(feature.FeatureCode);
        var hasName = !string.IsNullOrWhiteSpace(feature.FeatureName);

        if (hasCode && hasName)
        {
            queries.Add($"{feature.FeatureCode}-{feature.FeatureName}");
            queries.Add($"{feature.FeatureCode} {feature.FeatureName}");
        }

        if (hasCode)
        {
            queries.Add(feature.FeatureCode!);
        }

        if (hasName)
        {
            queries.Add(feature.FeatureName!);
            queries.Add($"{feature.FeatureName} Controller");
        }

        // 去重（保持順序）
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return queries.Where(q => seen.Add(q)).ToList();
    }

    // ─── 私有：判斷是否為功能入口節點 ──────────────────────────────────────────

    private static bool IsEntryPoint(GraphNode node) =>
        node.Kind == GraphNodeKind.EntryPoint
        || node.Kind == GraphNodeKind.Feature
        || EntryPointRoles.Contains(node.Role);

    private static int EntryPointSortPriority(GraphNode node) =>
        node.Kind switch
        {
            GraphNodeKind.EntryPoint => 4,
            GraphNodeKind.Feature => 3,
            GraphNodeKind.Code => 2,
            GraphNodeKind.Data => 1,
            _ => 0,
        };

    // ─── 私有：分類入口為已確認 vs 候選 ────────────────────────────────────────

    private static (IReadOnlyList<JiraEntryPoint> Confirmed, IReadOnlyList<JiraEntryPoint> Candidates)
        ClassifyEntryPoints(
            IReadOnlyList<(ScoredGraphNode Node, string? FeatureCode, string? FeatureName, string Query)> ranked)
    {
        var confirmed = new List<JiraEntryPoint>();
        var candidates = new List<JiraEntryPoint>();

        foreach (var (scored, featureCode, featureName, query) in ranked)
        {
            if (!IsEntryPoint(scored.Node))
            {
                continue;
            }

            var evidence = BuildEntryPointEvidence(scored, featureCode, featureName, query);
            var status = DetermineStatus(scored, featureCode, featureName);

            var ep = new JiraEntryPoint(
                scored.Node.Id,
                scored.Node.Name,
                scored.Node.Role,
                scored.Node.FilePath,
                featureCode,
                featureName,
                scored.Score,
                status,
                evidence);

            if (status == JiraEntryPointStatus.Confirmed)
            {
                confirmed.Add(ep);
            }
            else
            {
                candidates.Add(ep);
            }
        }

        return (confirmed, candidates);
    }

    private static JiraEntryPointStatus DetermineStatus(
        ScoredGraphNode scored,
        string? featureCode,
        string? featureName)
    {
        if (scored.Score < EntryPointConfirmScore)
        {
            return JiraEntryPointStatus.Candidate;
        }

        // 節點別名或搜尋文字中明確包含功能代號或名稱
        bool codeInNode = featureCode is not null
            && (scored.Node.Name.Contains(featureCode, StringComparison.OrdinalIgnoreCase)
                || scored.Node.Aliases.Any(a => a.Contains(featureCode, StringComparison.OrdinalIgnoreCase))
                || scored.Node.SearchableText.Contains(featureCode, StringComparison.OrdinalIgnoreCase));

        bool nameInNode = featureName is not null
            && (scored.Node.Name.Contains(featureName, StringComparison.OrdinalIgnoreCase)
                || scored.Node.Aliases.Any(a => a.Contains(featureName, StringComparison.OrdinalIgnoreCase))
                || scored.Node.SearchableText.Contains(featureName, StringComparison.OrdinalIgnoreCase));

        if (codeInNode || nameInNode)
        {
            return JiraEntryPointStatus.Confirmed;
        }

        // 高分 Seed 入口，雖沒有直接代號/名稱比對，但由 BM25 直接命中
        if (scored.Seed && scored.Score >= 0.70 && IsEntryPoint(scored.Node))
        {
            return JiraEntryPointStatus.Confirmed;
        }

        return JiraEntryPointStatus.Candidate;
    }

    private static IReadOnlyList<string> BuildEntryPointEvidence(
        ScoredGraphNode scored,
        string? featureCode,
        string? featureName,
        string query)
    {
        var ev = new List<string>();
        if (featureCode is not null)
        {
            ev.Add($"FeatureCode={featureCode}");
        }

        if (featureName is not null)
        {
            ev.Add($"FeatureName={featureName}");
        }

        ev.Add($"Query={query}");
        ev.Add($"Score={scored.Score:F3}");
        ev.Add($"Seed={scored.Seed}");
        ev.Add($"Depth={scored.Depth}");
        return ev;
    }

    // ─── 私有：將 ranked nodes 轉為 JiraGraphRagHit ─────────────────────────────

    private static IReadOnlyList<JiraGraphRagHit> BuildHits(
        string projectId,
        IReadOnlyList<(ScoredGraphNode Node, string? FeatureCode, string? FeatureName, string Query)> ranked)
    {
        return ranked.Select(v => new JiraGraphRagHit(
            projectId,
            v.FeatureCode,
            v.FeatureName,
            v.Query,
            v.Node.Node.Id,
            v.Node.Node.Kind.ToString(),
            v.Node.Node.Role,
            v.Node.Node.Name,
            v.Node.Node.FilePath,
            v.Node.Node.StartLine,
            v.Node.Node.EndLine,
            v.Node.Node.Language,
            v.Node.Score,
            BuildMatchReason(v.Node, v.FeatureCode, v.FeatureName),
            v.Node.Node.Evidence
                .Select(e => e.Artifact)
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
        )).ToList();
    }

    private static string BuildMatchReason(
        ScoredGraphNode scored,
        string? featureCode,
        string? featureName)
    {
        var parts = new List<string>();
        if (featureCode is not null)
        {
            parts.Add($"code:{featureCode}");
        }

        if (featureName is not null)
        {
            parts.Add($"name:{featureName}");
        }

        if (scored.Seed)
        {
            parts.Add("seed");
        }

        parts.Add($"depth:{scored.Depth}");
        return string.Join(", ", parts);
    }

    // ─── 私有：裁切到 token 預算 ────────────────────────────────────────────────

    private static (IReadOnlyList<JiraGraphRagHit> Included, bool WasTruncated)
        TruncateToTokenBudget(IReadOnlyList<JiraGraphRagHit> hits)
    {
        if (hits.Count == 0)
        {
            return (hits, false);
        }

        var included = new List<JiraGraphRagHit>();
        int usedTokens = 0;

        foreach (var hit in hits)
        {
            var estimate = EstimateHitTokens(hit);
            if (usedTokens + estimate > TokenBudget && included.Count > 0)
            {
                return (included, true);
            }

            included.Add(hit);
            usedTokens += estimate;
        }

        return (included, false);
    }

    private static int EstimateHitTokens(JiraGraphRagHit hit)
    {
        var length = (hit.NodeName?.Length ?? 0)
            + (hit.FilePath?.Length ?? 0)
            + (hit.MatchReason?.Length ?? 0)
            + (hit.NodeRole?.Length ?? 0)
            + 40; // 固定欄位開銷
        return Math.Max(1, length / 4);
    }

    private static int EstimateTokens(IReadOnlyList<JiraGraphRagHit> hits) =>
        hits.Sum(EstimateHitTokens);
}
