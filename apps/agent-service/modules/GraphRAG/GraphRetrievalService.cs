using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentService.Modules.GraphRAG;

/// <summary>
/// GraphRAG Local／Global Search 的技術 budget。
/// 這些設定只限制回傳量與效能，不會改變 NodeKind、EdgeKind 或 schema 語意，因此不是 profile。
/// </summary>
public sealed class GraphRetrievalOptions
{
    /// <summary>BM25 種子節點上限。</summary>
    public int SeedLimit { get; set; } = 12;

    /// <summary>Local Search 最多回傳 node 數。</summary>
    public int MaximumNodes { get; set; } = 80;

    /// <summary>Local Search 最多回傳 edge 數。</summary>
    public int MaximumEdges { get; set; } = 120;

    /// <summary>從種子向外遍歷的最大 hop。</summary>
    public int MaximumDepth { get; set; } = 3;

    /// <summary>單一 node 最多載入的鄰接關係，避免 shared table 爆量。</summary>
    public int NeighborsPerNode { get; set; } = 50;

    /// <summary>每增加一 hop 乘上的衰減。</summary>
    public double HopDecay { get; set; } = 0.75;
}

/// <summary>Local Search 結果中的 node 與綜合分數。</summary>
/// <param name="Node">完整 domain node。</param>
/// <param name="Score">BM25、關係權重與 hop decay 合成後的分數。</param>
/// <param name="Depth">離最近 seed 的 hop 數。</param>
/// <param name="Seed">是否為 BM25 直接命中的 seed。</param>
public sealed record ScoredGraphNode(
    GraphNode Node,
    double Score,
    int Depth,
    bool Seed);

/// <summary>
/// 可直接提供給 LLM 的 bounded GraphRAG context。
/// Nodes 與 Edges 已依關聯分數排序，Diagnostics 說明截斷或資料缺口。
/// </summary>
/// <param name="Query">原始使用者問題。</param>
/// <param name="Nodes">有界且排序後的相關節點。</param>
/// <param name="Edges">連接已選節點的必要關係。</param>
/// <param name="Communities">命中的 primary／secondary community reports。</param>
/// <param name="Diagnostics">檢索降級或截斷說明。</param>
public sealed record GraphRetrievalContext(
    string Query,
    IReadOnlyList<ScoredGraphNode> Nodes,
    IReadOnlyList<GraphEdge> Edges,
    IReadOnlyList<GraphCommunityReport> Communities,
    IReadOnlyList<string> Diagnostics);

/// <summary>AI community enrichment 的獨立狀態；失敗不回滾 canonical graph。</summary>
public sealed record GraphAiEnrichmentStatus(
    string ProjectId,
    string? TargetManifestVersion,
    string State,
    int CompletedCommunities,
    int TotalCommunities,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? Message = null);

/// <summary>
/// 使用 Neo4j BM25 種子加 relation-aware BFS 建立修改範圍上下文。
/// 本服務不呼叫 LLM 來猜 canonical edge；LLM 只閱讀已抽取的 node、edge、evidence 與社群摘要。
/// </summary>
public sealed partial class GraphRetrievalService
{
    /// <summary>
    /// 專案問答的固定證據規則；最後一輪問題優先於歷史，且不得引用 context 外路徑。
    /// 保持單一文字來源，避免 Local、Global fallback 與 Community prompt 漂移。
    /// </summary>
    private const string ProjectAnswerRules = """
        本輪只能回答最後一個「# 本輪唯一要回答的問題」；對話歷史只供語意參考，
        不得把上一輪問題或上一輪回答誤認成本輪任務。
        只能引用 GraphRAG context 或本次附件明確出現的檔案與路徑；
        不得引用 Modern Wingman AgentService 工作目錄或待分析專案外的路徑。
        證據不足時列出缺口並建議檢查索引／資料庫設定，不得猜測 auth.*、login.* 等檔名。
        """;

    /// <summary>
    /// 每次送往 Graph Store 的 frontier 節點上限。
    /// 此值只控制資料庫 round trip，不放寬整體 node、edge 或 depth budget。
    /// </summary>
    private const int MaximumFrontierBatchSize = 32;

    /// <summary>
    /// Community AI enrichment 的固定並行上限。
    /// 小型本機服務以兩個請求平衡完成時間與供應商 rate limit，不提供可無限調高的設定。
    /// </summary>
    private const int CommunityEnrichmentConcurrency = 2;
    private static readonly TimeSpan CommunityEnrichmentTimeout = TimeSpan.FromSeconds(45);

    private readonly IGraphStore _store;
    private readonly GraphRetrievalOptions _options;
    private readonly ILogger<GraphRetrievalService> _logger;
    private readonly ILlmCompletionService? _llm;
    private readonly RepositoryQuestionPlanner _questionPlanner = new();
    private readonly GraphContextCompiler _contextCompiler = new();
    private readonly ConcurrentDictionary<string, GraphAiEnrichmentStatus> _enrichment =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _enrichmentGates =
        new(StringComparer.Ordinal);

    /// <summary>建立 V3 retrieval service 並驗證所有 budget。</summary>
    /// <param name="store">只查 active manifest 的 V3 store。</param>
    /// <param name="options">技術 budget，不是 schema profile。</param>
    /// <param name="logger">結構化 logger。</param>
    public GraphRetrievalService(
        IGraphStore store,
        IOptions<GraphRetrievalOptions> options,
        ILogger<GraphRetrievalService> logger,
        ILlmCompletionService? llm = null)
    {
        _store = store;
        _options = options.Value;
        _logger = logger;
        _llm = llm;
        ValidateOptions(_options);
    }

    /// <summary>取得 community AI enrichment 進度。</summary>
    public GraphAiEnrichmentStatus GetEnrichmentStatus(string projectId) =>
        _enrichment.TryGetValue(projectId, out var value)
            ? value
            : new GraphAiEnrichmentStatus(
                projectId, null, "NotRequested", 0, 0, null, null);

    /// <summary>
    /// 使用 LLM 改寫目前 deterministic primary reports 的摘要。
    /// 任一摘要失敗時保留原摘要並標示 Degraded；canonical nodes/edges 永遠不受影響。
    /// </summary>
    public async Task<int> BuildCommunitySummariesAsync(
        string projectId,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var gate = _enrichmentGates.GetOrAdd(
            projectId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            // LLM 未註冊也屬背景 enrichment 的可降級錯誤，必須在狀態保護區內解析，
            // 確保前端取得 Degraded terminal，而不是永久輪詢 NotRequested。
            var llm = RequiredLlm();
            var manifest = await _store.GetActiveManifestAsync(
                projectId, cancellationToken) ??
                throw new InvalidOperationException(
                    "專案尚無 active V3 graph，無法建立社群摘要。");
            var reports = await _store.ListCommunityReportsAsync(
                projectId, cancellationToken);
            var primaryReports = reports
                .Where(report => report.Kind == "primary")
                .OrderBy(report => report.CommunityId, StringComparer.Ordinal)
                .ToList();
            _enrichment[projectId] = new GraphAiEnrichmentStatus(
                projectId, manifest, "Summarizing", 0, primaryReports.Count,
                DateTimeOffset.UtcNow, null, "正在建立業務社群摘要。");
            var enriched = reports.ToDictionary(
                report => report.CommunityId,
                report => report,
                StringComparer.Ordinal);
            var failures = 0;
            var completed = 0;
            foreach (var batch in primaryReports.Chunk(CommunityEnrichmentConcurrency))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var active = await _store.GetActiveManifestAsync(
                    projectId, cancellationToken);
                if (!string.Equals(active, manifest, StringComparison.Ordinal))
                {
                    _enrichment[projectId] = GetEnrichmentStatus(projectId) with
                    {
                        State = "Superseded",
                        CompletedAt = DateTimeOffset.UtcNow,
                        Message = "索引版本已更新，舊版摘要工作已停止。",
                    };
                    return completed;
                }
                progress?.Report(
                    $"生成業務社群摘要 {completed + 1}-" +
                    $"{Math.Min(completed + batch.Length, primaryReports.Count)}/" +
                    $"{primaryReports.Count}...");
                var results = await Task.WhenAll(batch.Select(report =>
                    EnrichCommunityReportAsync(report, llm, cancellationToken)));
                foreach (var result in results)
                {
                    enriched[result.Report.CommunityId] = result.Report;
                    if (result.Failed) failures++;
                }
                completed += batch.Length;
                _enrichment[projectId] = GetEnrichmentStatus(projectId) with
                {
                    CompletedCommunities = completed,
                    Message = $"已完成 {completed}/{primaryReports.Count} 個業務社群摘要。",
                };
            }
            await _store.SaveCommunityReportsAsync(
                projectId,
                manifest,
                reports.Select(report => enriched[report.CommunityId]).ToList(),
                cancellationToken);
            _enrichment[projectId] = GetEnrichmentStatus(projectId) with
            {
                State = failures == 0 ? "Ready" : "Degraded",
                CompletedAt = DateTimeOffset.UtcNow,
                Message = failures == 0
                    ? $"業務社群摘要完成（{completed} 個）。"
                    : $"完成 {completed} 個，其中 {failures} 個保留 deterministic 摘要。",
            };
            return completed;
        }
        catch (OperationCanceledException)
        {
            _enrichment[projectId] = GetEnrichmentStatus(projectId) with
            {
                State = "Canceled",
                CompletedAt = DateTimeOffset.UtcNow,
                Message = "AI enrichment 已取消；Fast Index 不受影響。",
            };
            throw;
        }
        catch (Exception exception)
        {
            // 背景摘要即使在讀寫圖資料時失敗，也必須進入終止狀態，
            // 否則桌面端會把 Summarizing 視為仍在工作而永久輪詢。
            _enrichment[projectId] = GetEnrichmentStatus(projectId) with
            {
                State = "Degraded",
                CompletedAt = DateTimeOffset.UtcNow,
                Message = "AI enrichment 發生錯誤；已保留 deterministic 摘要。",
            };
            _logger.LogWarning(
                exception,
                "Community AI enrichment degraded for project {ProjectId}.",
                projectId);
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// 將單一 Primary Business Community 的 deterministic summary 交給 LLM 改寫。
    /// 已有相同 cache key 的 AI 摘要直接沿用；模型失敗時回傳原 report 並標示失敗，
    /// 呼叫端仍會保存 deterministic summary，且不改動 canonical node 或 edge。
    /// </summary>
    /// <param name="report">待加值的 Primary Community report。</param>
    /// <param name="llm">目前已解析完成的唯讀文字 completion 服務。</param>
    /// <param name="cancellationToken">取消本次背景摘要工作的 token。</param>
    /// <returns>加值後 report，以及是否因模型錯誤而降級。</returns>
    private static async Task<(GraphCommunityReport Report, bool Failed)>
        EnrichCommunityReportAsync(
            GraphCommunityReport report,
            ILlmCompletionService llm,
            CancellationToken cancellationToken)
    {
        if (report.AiEnriched && !string.IsNullOrWhiteSpace(report.CacheKey))
            return (report, false);
        try
        {
            var prompt = $"""
                你是熟悉大型投資交易與風控系統的資深架構師。
                請根據以下「已由圖譜證據產生」的業務社群摘要，改寫成 2 到 4 句繁體中文，
                說明功能入口、主要程式碼責任與資料依賴。不得新增未提供的類別、資料表或流程。

                社群：{report.Title}
                原摘要：{report.Summary}
                成員 ID（最多 80 筆）：
                {string.Join('\n', report.MemberIds.Take(80))}

                只輸出摘要，不要標題或前後綴。
                """;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(CommunityEnrichmentTimeout);
            var summary = await llm.CompleteAsync(prompt, timeout.Token);
            return (report with
            {
                Summary = summary.Trim(),
                AiEnriched = true,
            }, false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return (report, true);
        }
    }

    /// <summary>
    /// 建立專案對話要交給共用 Agent 的唯讀 GraphRAG 提示。
    /// 本方法只做檢索與組裝，不直接呼叫模型，因此一般聊天與專案聊天能共用同一條串流、
    /// Provider、Model 與附件處理路徑。
    /// </summary>
    /// <param name="projectId">Modern Wingman 專案識別碼。</param>
    /// <param name="projectRoot">專案絕對根目錄；Source Evidence 不得越過此邊界。</param>
    /// <param name="question">不可為空白的使用者問題。</param>
    /// <param name="cancellationToken">取消 Graph、Source I/O 與 context 組裝。</param>
    /// <returns>可交給共用 Agent 的唯讀提示；讀檔失敗時保留 Graph 與缺口診斷。</returns>
    public async Task<string> BuildAnswerPromptAsync(
        string projectId,
        string projectRoot,
        string question,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        var stopwatch = Stopwatch.StartNew();
        var plan = _questionPlanner.Plan(question);
        var manifest = await _store.GetActiveManifestAsync(projectId, cancellationToken)
            ?? "unpublished";
        if (plan.Intent != RepositoryQuestionIntent.SystemOverview)
        {
            var context = await RetrieveWithSingleFallbackAsync(
                projectId, question, plan, cancellationToken);
            var source = await ReadSourceEvidenceAsync(
                projectRoot, manifest, plan, context, cancellationToken);
            var compiled = _contextCompiler.Compile(plan, context, source);
            _logger.LogInformation(
                "專案回答 Context 完成：Intent={Intent}, Terms={TermCount}, Nodes={NodeCount}, Edges={EdgeCount}, Snippets={SnippetCount}, Characters={CharacterCount}, ElapsedMs={ElapsedMs}",
                plan.Intent, plan.SearchTerms.Count, context.Nodes.Count, context.Edges.Count,
                source.Snippets.Count, compiled.Length, stopwatch.ElapsedMilliseconds);
            return $"""
                你正在回答 Modern Wingman 的專案解析問題。
                只能依據下列 GraphRAG context 與使用者本次附件回答，不得編造不存在的程式碼或資料流。
                請使用繁體中文，引用 filePath、line、evidence；推論與未知資訊必須明確標示。
                若問題是 bug 或新需求，只回答應修改的檔案／symbol、原因及做法，不要執行修改。
                {ProjectAnswerRules}

                # GraphRAG context
                {compiled}

                # 本輪唯一要回答的問題
                {question}
                """;
        }

        var reports = await GlobalSearchAsync(projectId, question, 20, cancellationToken);
        if (reports.Count == 0)
        {
            var context = await RetrieveWithSingleFallbackAsync(
                projectId, question, plan, cancellationToken);
            var source = await ReadSourceEvidenceAsync(
                projectRoot, manifest, plan, context, cancellationToken);
            return $"""
                你正在回答 Modern Wingman 的專案解析問題。
                只能依據下列 GraphRAG context 與使用者本次附件回答，不得編造。
                請使用繁體中文，引用 filePath、line、evidence，並標示未知資訊。
                {ProjectAnswerRules}

                # GraphRAG context
                {_contextCompiler.Compile(plan, context, source)}

                # 本輪唯一要回答的問題
                {question}
                """;
        }

        return $"""
            你正在回答 Modern Wingman 的跨功能專案解析問題。
            只能依據下列 GraphRAG 社群摘要與使用者本次附件回答，不得新增摘要未提供的流程、
            類別或資料表。請用繁體中文說明資料流，保留 communityId 引用；
            需要精確檔案與行號但摘要沒有提供時，必須明確標示未知。
            {ProjectAnswerRules}

            # GraphRAG 社群摘要
            {string.Join("\n\n", reports.Select(report =>
                $"communityId={report.CommunityId}\n" +
                $"title={report.Title}\nkind={report.Kind}\nsummary={report.Summary}"))}

            # 本輪唯一要回答的問題
            {question}
            """;
    }

    /// <summary>
    /// 執行第一輪 Graph 檢索並檢查 Intent 所需 coverage；不足時只允許以已發現的
    /// method、資料物件與節點名稱補查一次。兩輪結果仍受原本 node／edge budget，
    /// 不會形成不受控的 Agent 搜尋循環。
    /// </summary>
    /// <param name="projectId">Modern Wingman 專案識別碼。</param>
    /// <param name="question">使用者原始問題。</param>
    /// <param name="plan">已由固定規則建立的問題計畫。</param>
    /// <param name="cancellationToken">取消兩輪檢索的 token。</param>
    /// <returns>第一輪結果，或與唯一補查結果合併後的有界 context。</returns>
    private async Task<GraphRetrievalContext> RetrieveWithSingleFallbackAsync(
        string projectId,
        string question,
        RepositoryQuestionPlan plan,
        CancellationToken cancellationToken)
    {
        // 第一輪就帶入 planner 的受控別名；若只送原始中文問題，「登入」可能只
        // 命中 AccountController，卻漏掉名稱完全不同的 LoginAndPasswordProcess。
        // 搜尋詞已在 planner 去重並限制十二個，因此不會形成無界 query expansion。
        var plannedQuery = string.Join(
            ' ',
            new[] { question }.Concat(plan.SearchTerms)
                .Distinct(StringComparer.OrdinalIgnoreCase));
        var first = await LocalSearchAsync(
            projectId,
            plannedQuery,
            cancellationToken);
        var coverage = Coverage(first);
        var discovered = first.Nodes.Select(item => item.Node.Name)
            .Concat(first.Nodes.SelectMany(item => item.Node.Evidence)
                .SelectMany(evidence => evidence.Details?.Values ?? []))
            .Concat(first.Edges.SelectMany(edge => edge.Evidence)
                .SelectMany(evidence => evidence.Details?.Values ?? []));
        var fallback = _questionPlanner.PlanFallback(plan, coverage, discovered);
        if (!fallback.ShouldRun) return first;

        var second = await LocalSearchAsync(
            projectId,
            string.Join(' ', fallback.SearchTerms),
            cancellationToken);
        return MergeContexts(first, second, fallback.MissingEvidence);
    }

    /// <summary>
    /// 將 Graph node／edge evidence 轉成安全 Source Reader 候選。
    /// Edge evidence 通常指向實際 invocation，因此比整個 type 的宣告 evidence 略優先；
    /// 讀檔失敗只加入診斷，不阻止仍可使用結構化 Graph 回答。
    /// </summary>
    /// <param name="projectRoot">目前專案的絕對根目錄。</param>
    /// <param name="manifest">Active manifest；參與 Source cache 失效鍵，不含機密設定。</param>
    /// <param name="plan">固定規則產生的 Intent 與搜尋詞。</param>
    /// <param name="context">已完成有界檢索的 Graph context。</param>
    /// <param name="cancellationToken">取消 source I/O 的 token。</param>
    /// <returns>安全片段與所有拒絕、截斷或降級診斷。</returns>
    private static async Task<SourceEvidenceReadResult> ReadSourceEvidenceAsync(
        string projectRoot,
        string manifest,
        RepositoryQuestionPlan plan,
        GraphRetrievalContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var scores = context.Nodes.ToDictionary(
                item => item.Node.Id,
                item => item.Score,
                StringComparer.Ordinal);
            var candidates = context.Nodes.SelectMany(item =>
                item.Node.Evidence.Select(evidence => new SourceEvidenceCandidate(
                    evidence,
                    item.Score + SourceRoleBoost(item.Node.Role) +
                            SourceQuestionBoost(item.Node, plan.SearchTerms) +
                            SourceEvidenceQuestionBoost(
                                evidence,
                                plan.SearchTerms),
                        item.Node.Kind,
                        EvidenceSymbolForQuestion(
                            item.Node,
                            evidence,
                            plan.SearchTerms))))
                .Concat(context.Edges.SelectMany(edge =>
                {
                    var score = Math.Max(
                        scores.GetValueOrDefault(edge.SourceId),
                        scores.GetValueOrDefault(edge.TargetId)) +
                                SourceEdgeBoost(edge.Kind);
                    return edge.Evidence.Select(evidence => new SourceEvidenceCandidate(
                        evidence,
                        score,
                        null,
                        EvidenceSymbol(evidence)));
                }));
            var reader = new SourceEvidenceReader(projectRoot);
            var result = await reader.ReadAsync(
                candidates, plan.Intent, manifest, cancellationToken);
            return result.Snippets.Count > 0 || context.Nodes.Count > 0
                ? result
                : await reader.SearchFallbackAsync(plan.SearchTerms, cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                DirectoryNotFoundException or ArgumentException)
        {
            return new SourceEvidenceReadResult(
                [],
                ["無法安全讀取 Source Evidence；已保留結構化 Graph 結果。"],
                false);
        }
    }

    /// <summary>
    /// 從 Compiler／Framework evidence 的安全 details 組合可讀 symbol。
    /// details 已在索引階段限制為 method 名稱等非機密識別碼。
    /// </summary>
    private static string? EvidenceSymbol(GraphEvidence evidence)
    {
        if (evidence.Details is null) return null;
        var values = new[] { "sourceMethod", "targetMethod", "method" }
            .Where(evidence.Details.ContainsKey)
            .Select(key => evidence.Details[key])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return values.Count == 0 ? null : string.Join(" → ", values);
    }

    /// <summary>
    /// Source snippet 排序優先保留可解釋執行責任的 Controller、Service 與 Repository。
    /// 乘數不改變 Graph 分數，也不會讓沒有 evidence 的節點產生虛構片段。
    /// </summary>
    /// <param name="role">Canonical Graph node role。</param>
    /// <returns>介於 0 到 8 的 source-only quota 優先級。</returns>
    private static double SourceRoleBoost(string role) => role switch
    {
        GraphRoles.Controller => 8,
        GraphRoles.BusinessService => 7,
        GraphRoles.Repository => 6,
        GraphRoles.FrontendPage => 4,
        _ => 0,
    };

    /// <summary>
    /// 問題直接點名的 route、symbol 或資料物件必須早於同角色的一般候選；
    /// 僅比對既有名稱與 alias，不推測新實體。
    /// </summary>
    private static double SourceQuestionBoost(
        GraphNode node,
        IReadOnlyList<string> terms) =>
        terms.Any(term =>
            node.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            node.Aliases.Any(alias =>
                alias.Contains(term, StringComparison.OrdinalIgnoreCase)))
            ? 20
            : 0;

    /// <summary>
    /// Evidence details 內若直接含有使用者點名的方法（例如 ProcessLogin），應優先於
    /// 只因 Controller 角色而入選的整個類別。只比對 extractor 已產生的 method 等
    /// 非機密識別碼，不讀取或猜測新內容。
    /// </summary>
    /// <param name="evidence">Canonical extractor evidence。</param>
    /// <param name="terms">Planner 已限制十二個以內的搜尋詞。</param>
    /// <returns>直接命中時加 24 分，否則不加分。</returns>
    private static double SourceEvidenceQuestionBoost(
        GraphEvidence evidence,
        IReadOnlyList<string> terms)
    {
        if (evidence.Details is null || terms.Count == 0) return 0;
        return evidence.Details.Values.Any(value =>
            terms.Any(term =>
                value.Contains(term, StringComparison.OrdinalIgnoreCase)))
            ? 24
            : 0;
    }

    /// <summary>
    /// 型別 evidence 同時列出多個方法時，挑出問題直接點名的方法名稱，讓 Source
    /// Reader 能定位方法本體，而不是只從類別宣告行讀到建構子。沒有精確方法時仍
    /// 使用既有型別名稱，不建立任何新 symbol。
    /// </summary>
    private static string EvidenceSymbolForQuestion(
        GraphNode node,
        GraphEvidence evidence,
        IReadOnlyList<string> terms)
    {
        if (evidence.Details?.TryGetValue("methods", out var methods) != true ||
            string.IsNullOrWhiteSpace(methods))
            return node.Name;
        var methodNames = methods.Split(
                " | ",
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries)
            .Select(signature =>
            {
                var beforeParameters = signature.Split('(', 2)[0].Trim();
                return beforeParameters.Split(
                    ' ',
                    StringSplitOptions.RemoveEmptyEntries)[^1];
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return methodNames.FirstOrDefault(method =>
                   terms.Any(term => string.Equals(
                       method,
                       term,
                       StringComparison.OrdinalIgnoreCase)))
               ?? methodNames.FirstOrDefault(method =>
                   terms.Any(term =>
                       method.Contains(term, StringComparison.OrdinalIgnoreCase)))
               ?? node.Name;
    }

    /// <summary>
    /// HANDLES 與 CALLS evidence 最能補足「入口如何進入程式」；資料讀寫次之。
    /// 此加分只決定讀哪些檔案，不改寫或補造任何 canonical edge。
    /// </summary>
    /// <param name="kind">Evidence 所屬的既有關係種類。</param>
    /// <returns>介於 0.5 到 5 的 source-only 優先級；只用來建立片段 quota。</returns>
    private static double SourceEdgeBoost(GraphEdgeKind kind) => kind switch
    {
        GraphEdgeKind.Handles => 5,
        GraphEdgeKind.Calls or GraphEdgeKind.DispatchesTo => 3,
        GraphEdgeKind.Reads or GraphEdgeKind.Writes => 2,
        GraphEdgeKind.RoutesTo => 1,
        _ => 0.5,
    };

    /// <summary>
    /// 計算第一輪是否已涵蓋問題所需的五種最小證據。
    /// 這裡只判斷 retrieval coverage，不宣稱候選內容已由人工確認。
    /// </summary>
    private static RepositoryRetrievalCoverage Coverage(GraphRetrievalContext context) =>
        new(
            context.Nodes.Any(item => item.Node.Kind == GraphNodeKind.Feature),
            context.Nodes.Any(item => item.Node.Kind == GraphNodeKind.EntryPoint),
            context.Nodes.Any(item => item.Node.Kind == GraphNodeKind.Code),
            context.Nodes.Any(item => item.Node.Kind == GraphNodeKind.Data),
            context.Edges.Count > 0);

    /// <summary>
    /// 合併兩輪 context 並重新套用固定 node／edge 上限。
    /// 相同節點保留分數較高者，第二輪只補足缺口，不得擴張 retrieval budget。
    /// </summary>
    private GraphRetrievalContext MergeContexts(
        GraphRetrievalContext first,
        GraphRetrievalContext second,
        IReadOnlyList<string> originalMissingEvidence)
    {
        // 第一輪先占用 budget；第二輪只加入缺少的節點，避免兩輪各自正規化的
        // 分數讓一般候選擠掉第一輪已形成的完整關係路徑。
        var firstNodes = first.Nodes.Take(_options.MaximumNodes).ToList();
        var firstIds = firstNodes.Select(item => item.Node.Id)
            .ToHashSet(StringComparer.Ordinal);
        var nodes = firstNodes.Concat(second.Nodes
                .Where(item => !firstIds.Contains(item.Node.Id)))
            .GroupBy(item => item.Node.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .Take(_options.MaximumNodes)
            .ToList();
        var nodeIds = nodes.Select(item => item.Node.Id).ToHashSet(StringComparer.Ordinal);
        var edges = first.Edges.Concat(second.Edges)
            .DistinctBy(edge => edge.Id, StringComparer.Ordinal)
            .Where(edge => nodeIds.Contains(edge.SourceId) &&
                           nodeIds.Contains(edge.TargetId))
            .Take(_options.MaximumEdges)
            .ToList();
        var communities = first.Communities.Concat(second.Communities)
            .DistinctBy(report => report.CommunityId, StringComparer.Ordinal)
            .Take(10)
            .ToList();
        var remaining = _questionPlanner.PlanFallback(
            _questionPlanner.Plan(first.Query),
            Coverage(new GraphRetrievalContext(
                first.Query, nodes, edges, communities, [])),
            previousAttempts: 1);
        var diagnostics = first.Diagnostics.Concat(second.Diagnostics)
            .Append(
                $"第一輪缺少 {string.Join("、", originalMissingEvidence)}，已完成唯一一次補查。")
            .Concat(remaining.MissingEvidence.Count == 0
                ? []
                : [$"補查後仍缺少：{string.Join("、", remaining.MissingEvidence)}。"])
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return new GraphRetrievalContext(
            first.Query, nodes, edges, communities, diagnostics);
    }

    /// <summary>產生 type/file-level Repo Map，避免重新引入 Method／Property 節點。</summary>
    public async Task<string> GenerateRepoMapAsync(
        string projectId,
        int tokenBudget = 1_024,
        CancellationToken cancellationToken = default)
    {
        var maximumCharacters = Math.Clamp(tokenBudget, 128, 16_384) * 4;
        var hits = await _store.GetCentralNodesAsync(
            projectId, 300, cancellationToken);
        var builder = new StringBuilder("# Repo Map（GraphRAG V3 修改單位）\n");
        foreach (var group in hits
                     .Where(hit => hit.Node.Kind == GraphNodeKind.Code &&
                                   hit.Node.FilePath is not null)
                     .GroupBy(hit => hit.Node.FilePath!, StringComparer.Ordinal)
                     .OrderByDescending(group => group.Sum(item => item.Score))
                     .ThenBy(group => group.Key, StringComparer.Ordinal))
        {
            if (builder.Length >= maximumCharacters) break;
            builder.AppendLine(group.Key);
            foreach (var hit in group.Take(10))
                builder.AppendLine(
                    $"  - {hit.Node.Role}: {hit.Node.Name} (line {hit.Node.StartLine?.ToString() ?? "?"})");
        }
        var result = builder.ToString();
        return result.Length <= maximumCharacters
            ? result
            : result[..maximumCharacters] + "\n…（已截斷）";
    }

    private ILlmCompletionService RequiredLlm() =>
        _llm ?? throw new InvalidOperationException(
            "GraphRAG AI enrichment／answer 尚未註冊 ILlmCompletionService。");

    /// <summary>
    /// 回答 bug 或新需求的主要入口：先用 BM25 找業務／程式／資料 seed，
    /// 再依關係語意與 hop decay 補齊 Menu→EntryPoint→Code→Data 修改路徑。
    /// </summary>
    /// <param name="projectId">Modern Wingman 專案 ID。</param>
    /// <param name="query">使用者的自然語言問題。</param>
    /// <param name="cancellationToken">取消檢索工作的 token。</param>
    /// <returns>有界、可追溯且按分數排序的圖譜上下文。</returns>
    public async Task<GraphRetrievalContext> LocalSearchAsync(
        string projectId,
        string query,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var plan = _questionPlanner.Plan(query);
        var plannedQuery = string.Join(' ', plan.SearchTerms);
        var luceneQuery = BuildLuceneQuery(plannedQuery);
        if (luceneQuery.Length == 0)
            return new GraphRetrievalContext(
                query, [], [], [],
                ["問題沒有可搜尋的文字或識別碼，未執行圖譜遍歷。"]);

        var hits = await SearchSeedHitsAsync(
            projectId, plannedQuery, luceneQuery, cancellationToken);
        if (hits.Count == 0)
            return new GraphRetrievalContext(
                query, [], [], [],
                ["BM25 沒有命中可靠種子；系統不會虛構不存在的圖譜關係。"]);

        var maximumSeedScore = Math.Max(hits.Max(hit => hit.Score), double.Epsilon);
        var selected = new Dictionary<string, ScoredGraphNode>(StringComparer.Ordinal);
        var edges = new Dictionary<string, GraphEdge>(StringComparer.Ordinal);
        var frontier = new PriorityQueue<TraversalState, double>();
        foreach (var hit in hits)
        {
            var normalizedScore = Math.Clamp(hit.Score / maximumSeedScore, 0.05, 1.0);
            var state = new ScoredGraphNode(hit.Node, normalizedScore, 0, true);
            if (!selected.TryGetValue(hit.Node.Id, out var existing) ||
                state.Score > existing.Score)
                selected[hit.Node.Id] = state;
            frontier.Enqueue(
                new TraversalState(hit.Node.Id, normalizedScore, 0),
                -normalizedScore);
        }

        // 先完成「使用者意圖直接對應的關係」。一般 BFS 可能被高 degree 的共用 utility
        // 提前填滿 MaximumNodes，導致尚未展開的 WRITES owner 雖是 seed，卻沒有把資料表
        // 與 edge 帶進 context。這裡只追已存在的 evidence-backed edge，不推測新關係。
        var intentKinds = plan.RelationKinds;
        if (intentKinds.Count > 0)
        {
            var seedStates = selected.Values
                .Where(item => item.Seed)
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Node.Id, StringComparer.Ordinal)
                .ToList();
            var directNeighbors = await _store.GetNeighborsBatchAsync(
                projectId,
                seedStates.Select(seed => seed.Node.Id).ToList(),
                _options.NeighborsPerNode,
                cancellationToken);
            var directSets = seedStates.Select(seed => new
            {
                Seed = seed,
                Neighbors = directNeighbors.GetValueOrDefault(
                    seed.Node.Id,
                    Array.Empty<GraphNeighborV3>()),
            });
            var directIntentNeighbors = directSets
                .SelectMany(set => set.Neighbors
                    .Where(neighbor => intentKinds.Contains(neighbor.Edge.Kind))
                    .Select(neighbor => new
                    {
                        set.Seed,
                        Neighbor = neighbor,
                        Score = set.Seed.Score *
                                EdgeWeight(neighbor.Edge.Kind) *
                                DirectionWeight(neighbor.Direction, plan.Direction) *
                                _options.HopDecay,
                    }))
                .OrderByDescending(item => item.Score)
                .ThenByDescending(item => EdgeWeight(item.Neighbor.Edge.Kind))
                .ThenBy(item => item.Neighbor.Node.Id, StringComparer.Ordinal)
                .ToList();
            foreach (var item in directIntentNeighbors)
            {
                if (selected.Count >= _options.MaximumNodes &&
                    !selected.ContainsKey(item.Neighbor.Node.Id))
                    continue;
                if (!selected.TryGetValue(item.Neighbor.Node.Id, out var existing) ||
                    item.Score > existing.Score)
                {
                    selected[item.Neighbor.Node.Id] = new ScoredGraphNode(
                        item.Neighbor.Node,
                        item.Score,
                        1,
                        false);
                    frontier.Enqueue(
                        new TraversalState(item.Neighbor.Node.Id, item.Score, 1),
                        -item.Score);
                }
                if (edges.Count < _options.MaximumEdges)
                    edges[item.Neighbor.Edge.Id] = item.Neighbor.Edge;
            }

            // Feature seed 通常先找到 ROUTES_TO 的 EntryPoint。若直接進入一般 BFS，
            // 大量 table reader 可能先填滿 80-node budget，使真正的 Controller 被擠掉；
            // 因此只額外保留一層 evidence-backed HANDLES，完成入口到程式的最小骨架。
            var entryPoints = selected.Values
                .Where(item => item.Node.Kind == GraphNodeKind.EntryPoint)
                .OrderByDescending(item => item.Score)
                .Take(_options.SeedLimit)
                .ToList();
            var handled = await _store.GetNeighborsBatchAsync(
                projectId,
                entryPoints.Select(item => item.Node.Id).ToList(),
                _options.NeighborsPerNode,
                cancellationToken);
            foreach (var entry in entryPoints)
            {
                foreach (var neighbor in handled.GetValueOrDefault(
                             entry.Node.Id, Array.Empty<GraphNeighborV3>())
                         .Where(value => value.Edge.Kind == GraphEdgeKind.Handles))
                {
                    var score = entry.Score * EdgeWeight(GraphEdgeKind.Handles) *
                                _options.HopDecay;
                    if (selected.Count >= _options.MaximumNodes &&
                        !selected.ContainsKey(neighbor.Node.Id))
                        continue;
                    if (!selected.TryGetValue(neighbor.Node.Id, out var existing) ||
                        score > existing.Score)
                    {
                        selected[neighbor.Node.Id] = new ScoredGraphNode(
                            neighbor.Node, score, entry.Depth + 1, false);
                        frontier.Enqueue(
                            new TraversalState(
                                neighbor.Node.Id, score, entry.Depth + 1),
                            -score);
                    }
                    if (edges.Count < _options.MaximumEdges)
                        edges[neighbor.Edge.Id] = neighbor.Edge;
                }
            }
        }

        var expandedAtScore = new Dictionary<string, double>(StringComparer.Ordinal);
        while (frontier.Count > 0 && selected.Count < _options.MaximumNodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expansionBatch = DequeueExpansionBatch(
                frontier,
                expandedAtScore,
                Math.Min(_options.MaximumDepth, plan.MaximumDepth));
            if (expansionBatch.Count == 0) continue;
            var neighborSets = await _store.GetNeighborsBatchAsync(
                projectId,
                expansionBatch.Select(state => state.NodeId).ToList(),
                _options.NeighborsPerNode,
                cancellationToken);
            foreach (var state in expansionBatch)
            {
                var neighbors = neighborSets.GetValueOrDefault(
                    state.NodeId,
                    Array.Empty<GraphNeighborV3>());
                foreach (var neighbor in neighbors)
                {
                    var relationScore = EdgeWeight(neighbor.Edge.Kind);
                    // Planner 關係白名單是有界 traversal 的硬限制，避免共用資料表
                    // 或 utility 沿與問題無關的高 degree 關係擴散。
                    if (!plan.RelationKinds.Contains(neighbor.Edge.Kind)) continue;
                    var nextScore = state.Score * relationScore *
                                    DirectionWeight(neighbor.Direction, plan.Direction) *
                                    _options.HopDecay;
                    var nextDepth = state.Depth + 1;
                    if (nextScore < 0.08) continue;

                    var candidate = new ScoredGraphNode(
                        neighbor.Node, nextScore, nextDepth, false);
                    if (!selected.TryGetValue(neighbor.Node.Id, out var existing) ||
                        nextScore > existing.Score)
                    {
                        if (selected.Count >= _options.MaximumNodes &&
                            !selected.ContainsKey(neighbor.Node.Id))
                            continue;
                        selected[neighbor.Node.Id] = candidate;
                        frontier.Enqueue(
                            new TraversalState(neighbor.Node.Id, nextScore, nextDepth),
                            -nextScore);
                    }
                    if (edges.Count < _options.MaximumEdges)
                        edges[neighbor.Edge.Id] = neighbor.Edge;
                }
            }
        }

        var selectedIds = selected.Keys.ToHashSet(StringComparer.Ordinal);
        var boundedEdges = edges.Values
            .Where(edge => selectedIds.Contains(edge.SourceId) &&
                           selectedIds.Contains(edge.TargetId))
            .OrderByDescending(edge =>
                Math.Max(selected[edge.SourceId].Score, selected[edge.TargetId].Score) *
                EdgeWeight(edge.Kind))
            .ThenBy(edge => edge.SourceId, StringComparer.Ordinal)
            .ThenBy(edge => edge.Kind)
            .ThenBy(edge => edge.TargetId, StringComparer.Ordinal)
            .Take(_options.MaximumEdges)
            .ToList();
        var orderedNodes = selected.Values
            .OrderByDescending(node => node.Score)
            .ThenBy(node => node.Depth)
            .ThenBy(node => KindPriority(node.Node.Kind))
            .ThenBy(node => node.Node.Id, StringComparer.Ordinal)
            .Take(_options.MaximumNodes)
            .ToList();
        var reports = await MatchCommunityReportsAsync(
            projectId, query, orderedNodes, cancellationToken);
        var diagnostics = new List<string>();
        if (selected.Count >= _options.MaximumNodes)
            diagnostics.Add($"節點已達上限 {_options.MaximumNodes}，高 degree 鄰域已截斷。");
        if (edges.Count >= _options.MaximumEdges)
            diagnostics.Add($"關係已達上限 {_options.MaximumEdges}，只保留最高關聯路徑。");

        _logger.LogInformation(
            "GraphRAG Local Search 完成：Project={ProjectId}, Seeds={SeedCount}, Nodes={NodeCount}, Edges={EdgeCount}",
            projectId, hits.Count, orderedNodes.Count, boundedEdges.Count);
        return new GraphRetrievalContext(
            query, orderedNodes, boundedEdges, reports, diagnostics);
    }

    /// <summary>
    /// Global Search 只回傳與問題文字相關的 community reports，適用跨模組總覽；
    /// 不把 community summary 當成精確行號或 source-level evidence。
    /// </summary>
    /// <param name="projectId">Modern Wingman 專案 ID。</param>
    /// <param name="query">跨功能問題。</param>
    /// <param name="limit">最多回傳 community 數。</param>
    /// <param name="cancellationToken">取消檢索工作的 token。</param>
    /// <returns>按文字命中程度排序的 community reports。</returns>
    public async Task<IReadOnlyList<GraphCommunityReport>> GlobalSearchAsync(
        string projectId,
        string query,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        limit = Math.Clamp(limit, 1, 100);
        var reports = await _store.ListCommunityReportsAsync(
            projectId, cancellationToken);
        // 中文自然語言通常沒有空白；沿用 Local Search 的 CJK bigram 與意圖同義詞，
        // 否則「系統有哪些批次報表鏈」會被視為一個完整 token，實際 community title
        // 只有「批次報表」時反而完全無法命中。
        var terms = SearchSeedTerms(query).Take(20).ToList();
        return reports
            .Select(report => new
            {
                Report = report,
                Score = TextScore(
                    $"{report.Title} {report.Summary} {string.Join(' ', report.MemberIds)}",
                    terms),
            })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Report.Kind == "primary" ? 0 : 1)
            .ThenBy(item => item.Report.CommunityId, StringComparer.Ordinal)
            .Take(limit)
            .Select(item => item.Report)
            .ToList();
    }

    /// <summary>
    /// 將使用者文字轉成安全的 Lucene OR query。
    /// 特殊字元不直接傳給 Neo4j full-text parser，避免 parser error 或 query injection。
    /// </summary>
    /// <param name="query">自然語言或程式識別碼。</param>
    /// <returns>已 escape 且去重的 Lucene query。</returns>
    public static string BuildLuceneQuery(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return string.Join(" OR ", SearchSeedTerms(query)
            .Take(20)
            .Select(term => $"\"{EscapeLucene(term)}\""));
    }

    /// <summary>
    /// 同時執行完整 OR query 與少量 individual term query，再以 round-robin 合併種子。
    /// 單一高分功能（例如「新增商品」）不可吃掉全部 seed，否則同一句中的 CSV／報表／覆核
    /// 會完全沒有機會進入圖遍歷。各 query 的分數先在自己的結果內正規化，避免不同 IDF
    /// 量尺被錯誤直接比較。
    /// </summary>
    private async Task<IReadOnlyList<GraphSearchHitV3>> SearchSeedHitsAsync(
        string projectId,
        string naturalQuery,
        string combinedQuery,
        CancellationToken cancellationToken)
    {
        var terms = SearchSeedTerms(naturalQuery).Take(10).ToList();
        var queries = new[] { combinedQuery }
            .Concat(terms.Select(term => $"\"{EscapeLucene(term)}\""))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var perQueryLimit = Math.Clamp(_options.SeedLimit / 2, 3, 8);
        var resultSets = await Task.WhenAll(queries.Select(query =>
            _store.SearchAsync(
                projectId, query, perQueryLimit, cancellationToken)));
        var normalizedSets = resultSets.Select(results =>
        {
            var maximum = results.Count == 0
                ? 1
                : Math.Max(results.Max(hit => hit.Score), double.Epsilon);
            return results.Select(hit =>
                    hit with { Score = Math.Clamp(hit.Score / maximum, 0.05, 1) })
                .ToList();
        }).ToList();
        var selected = new Dictionary<string, GraphSearchHitV3>(StringComparer.Ordinal);
        for (var rank = 0;
             selected.Count < _options.SeedLimit &&
             normalizedSets.Any(set => rank < set.Count);
             rank++)
        {
            foreach (var set in normalizedSets)
            {
                if (rank >= set.Count) continue;
                var hit = set[rank];
                if (!selected.TryGetValue(hit.Node.Id, out var existing) ||
                    hit.Score > existing.Score)
                    selected[hit.Node.Id] = hit;
                if (selected.Count >= _options.SeedLimit) break;
            }
        }
        return selected.Values
            .OrderByDescending(hit => hit.Score)
            .ThenBy(hit => KindPriority(hit.Node.Kind))
            .ThenBy(hit => hit.Node.Id, StringComparer.Ordinal)
            .Take(_options.SeedLimit)
            .ToList();
    }

    private async Task<IReadOnlyList<GraphCommunityReport>> MatchCommunityReportsAsync(
        string projectId,
        string query,
        IReadOnlyList<ScoredGraphNode> nodes,
        CancellationToken cancellationToken)
    {
        var reports = await _store.ListCommunityReportsAsync(
            projectId, cancellationToken);
        var selectedIds = nodes.Select(node => node.Node.Id).ToHashSet(StringComparer.Ordinal);
        var terms = SearchTerms(query);
        return reports.Select(report => new
            {
                Report = report,
                MemberMatches = report.MemberIds.Count(selectedIds.Contains),
                TextMatches = TextScore($"{report.Title} {report.Summary}", terms),
            })
            .Where(item => item.MemberMatches > 0 || item.TextMatches > 0)
            .OrderByDescending(item => item.MemberMatches)
            .ThenByDescending(item => item.TextMatches)
            .ThenBy(item => item.Report.Kind == "primary" ? 0 : 1)
            .ThenBy(item => item.Report.CommunityId, StringComparer.Ordinal)
            .Take(10)
            .Select(item => item.Report)
            .ToList();
    }

    private static IReadOnlyList<string> SearchTerms(string value) =>
        SearchTermRegex().Matches(value)
            .Select(match => match.Value.Trim())
            .Where(term => term.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(term => term.Length)
            .ThenBy(term => term, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static IReadOnlyList<string> SearchSeedTerms(string value)
    {
        var result = new List<string>();
        foreach (var term in SearchTerms(value))
        {
            if (IgnoredSeedTerms.Contains(term)) continue;
            result.Add(term);
            if (!term.Any(IsCjk) || term.Length <= 2) continue;
            for (var index = 0; index < term.Length - 1; index++)
            {
                var pair = term.Substring(index, 2);
                if (!IgnoredSeedTerms.Contains(pair) &&
                    !pair.Any(IgnoredCjkBridgeCharacters.Contains))
                    result.Add(pair);
            }
        }
        AddIntentSynonyms(value, result);
        return result.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(term => term.Length)
            .ThenBy(term => term, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddIntentSynonyms(string query, ICollection<string> terms)
    {
        if (ContainsAny(query, "更新", "寫入", "儲存", "保存", "存檔"))
            foreach (var term in new[] { "Update", "Save", "Write", "Insert" })
                terms.Add(term);
        if (ContainsAny(query, "覆核", "放行", "核准"))
            foreach (var term in new[] { "Confirm", "Approval", "Approved" })
                terms.Add(term);
        if (ContainsAny(query, "匯入", "上傳"))
            foreach (var term in new[] { "Import", "Upload" })
                terms.Add(term);
    }

    private static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(candidate =>
            value.Contains(candidate, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlySet<GraphEdgeKind> IntentEdgeKinds(string query)
    {
        var result = new HashSet<GraphEdgeKind>();
        if (ContainsAny(
                query,
                "更新", "寫入", "儲存", "保存", "存檔", "新增", "刪除",
                "write", "save", "update", "insert", "delete"))
            result.Add(GraphEdgeKind.Writes);
        if (ContainsAny(
                query,
                "查詢", "讀取", "搜尋", "查不到", "顯示",
                "read", "search", "query", "select"))
            result.Add(GraphEdgeKind.Reads);
        return result;
    }

    private static bool IsCjk(char value) =>
        value is >= '\u3400' and <= '\u9FFF' or
            >= '\uF900' and <= '\uFAFF';

    private static string EscapeLucene(string value)
    {
        var result = value;
        foreach (var special in new[]
                 {
                     "\\", "+", "-", "&&", "||", "!", "(", ")", "{", "}", "[", "]",
                     "^", "\"", "~", "*", "?", ":", "/",
                 })
            result = result.Replace(special, $"\\{special}", StringComparison.Ordinal);
        return result;
    }

    private static int TextScore(string text, IReadOnlyList<string> terms) =>
        terms.Sum(term => CountOccurrences(text, term));

    private static int CountOccurrences(string text, string term)
    {
        var count = 0;
        var offset = 0;
        while ((offset = text.IndexOf(term, offset, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            offset += term.Length;
        }
        return count;
    }

    private static double EdgeWeight(GraphEdgeKind kind) => kind switch
    {
        GraphEdgeKind.RoutesTo => 1.00,
        GraphEdgeKind.Handles => 0.95,
        GraphEdgeKind.Triggers => 0.95,
        GraphEdgeKind.DispatchesTo => 0.90,
        GraphEdgeKind.Writes => 0.90,
        GraphEdgeKind.Reads => 0.85,
        GraphEdgeKind.MapsTo => 0.85,
        GraphEdgeKind.Calls => 0.75,
        GraphEdgeKind.DependsOn => 0.70,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "不允許的 V3 EdgeKind。"),
    };

    /// <summary>
    /// 讓問題計畫偏好的上下游方向獲得較高分，但不完全刪除反向證據。
    /// 保留 0.65 的反向分數可避免歷史 Graph 邊方向不一致時遺失有效路徑。
    /// </summary>
    /// <param name="actual">Graph Store 回傳的鄰居方向。</param>
    /// <param name="expected">Planner 依 Intent 選出的主要方向。</param>
    /// <returns>範圍固定在 0.65 到 1.0 的排序乘數。</returns>
    private static double DirectionWeight(
        string actual,
        RepositoryTraversalDirection expected) =>
        expected == RepositoryTraversalDirection.Both ||
        expected == RepositoryTraversalDirection.Outgoing &&
        actual.Equals("outgoing", StringComparison.OrdinalIgnoreCase) ||
        expected == RepositoryTraversalDirection.Incoming &&
        actual.Equals("incoming", StringComparison.OrdinalIgnoreCase)
            ? 1
            : 0.65;

    /// <summary>
    /// 從優先佇列取出一批尚未以相同或更高分數展開的節點。
    /// 分批只降低 Neo4j round trip；節點仍維持原本的分數、深度與全域數量限制，
    /// 因此不會因效能最佳化而擴大 LLM 可見的 Graph 範圍。
    /// </summary>
    /// <param name="frontier">依關聯分數排序的待展開節點。</param>
    /// <param name="expandedAtScore">每個節點已展開過的最高分數。</param>
    /// <param name="maximumDepth">允許向外遍歷的最大深度。</param>
    /// <returns>最多 32 個可安全批次查詢的 traversal state。</returns>
    private static IReadOnlyList<TraversalState> DequeueExpansionBatch(
        PriorityQueue<TraversalState, double> frontier,
        IDictionary<string, double> expandedAtScore,
        int maximumDepth)
    {
        var result = new List<TraversalState>(MaximumFrontierBatchSize);
        while (frontier.Count > 0 && result.Count < MaximumFrontierBatchSize)
        {
            var state = frontier.Dequeue();
            if (state.Depth >= maximumDepth) continue;
            if (expandedAtScore.TryGetValue(state.NodeId, out var prior) &&
                prior >= state.Score)
                continue;
            expandedAtScore[state.NodeId] = state.Score;
            result.Add(state);
        }
        return result;
    }

    private static int KindPriority(GraphNodeKind kind) => kind switch
    {
        GraphNodeKind.Feature => 0,
        GraphNodeKind.EntryPoint => 1,
        GraphNodeKind.Code => 2,
        GraphNodeKind.Data => 3,
        _ => 4,
    };

    private static void ValidateOptions(GraphRetrievalOptions options)
    {
        if (options.SeedLimit is < 1 or > 100)
            throw new InvalidOperationException("GraphRAG SeedLimit 必須介於 1 到 100。");
        if (options.MaximumNodes is < 10 or > 500)
            throw new InvalidOperationException("GraphRAG MaximumNodes 必須介於 10 到 500。");
        if (options.MaximumEdges is < 10 or > 1_000)
            throw new InvalidOperationException("GraphRAG MaximumEdges 必須介於 10 到 1000。");
        if (options.MaximumDepth is < 1 or > 5)
            throw new InvalidOperationException("GraphRAG MaximumDepth 必須介於 1 到 5。");
        if (options.NeighborsPerNode is < 5 or > 500)
            throw new InvalidOperationException("GraphRAG NeighborsPerNode 必須介於 5 到 500。");
        if (options.HopDecay is <= 0 or > 1)
            throw new InvalidOperationException("GraphRAG HopDecay 必須大於 0 且不超過 1。");
    }

    private sealed record TraversalState(string NodeId, double Score, int Depth);

    private static readonly IReadOnlySet<string> IgnoredSeedTerms =
        new HashSet<string>(
        [
            "bug", "error", "issue", "feature", "request",
            "問題", "錯誤", "異常", "資料", "沒有", "之後", "需要", "想要",
        ], StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlySet<char> IgnoredCjkBridgeCharacters =
        new HashSet<char>(['後', '沒', '有', '想', '要', '的', '了']);

    [GeneratedRegex(@"[\p{L}\p{N}_.:/-]+", RegexOptions.CultureInvariant)]
    private static partial Regex SearchTermRegex();
}

/// <summary>
/// 從 canonical snapshot 建立 Menu 主導的 primary community reports。
/// 同一 Code／Data 被多個 Feature 使用時只保存一份 node，並可同時出現在多個 report 的 memberIds；
/// 這是查詢視角，不會複製 domain graph。
/// </summary>
public static class GraphCommunityBuilder
{
    private const int MaximumMembersPerReport = 200;
    private const int MaximumNeighborsPerNode = 100;
    private const int LabelPropagationIterations = 8;
    private const string CommunitySummaryPromptVersion = "community-summary-v3.1";

    /// <summary>
    /// 同時建立 Menu 主導的 primary reports 與 deterministic label-propagation secondary reports。
    /// secondary 只提供 discovery，不會改寫或取代 primary ownership。
    /// </summary>
    /// <param name="snapshot">已通過 canonical validation 的 V3 snapshot。</param>
    /// <returns>先 primary、後 secondary 且穩定排序的 reports。</returns>
    public static IReadOnlyList<GraphCommunityReport> BuildReports(
        GraphSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        GraphAssembler.ValidateSnapshot(snapshot);
        return BuildPrimaryReportsValidated(snapshot)
            .Concat(BuildSecondaryReportsValidated(snapshot))
            .OrderBy(report => report.Kind, StringComparer.Ordinal)
            .ThenBy(report => report.CommunityId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// 依 Feature 的 Menu path／role 建立 deterministic primary reports。
    /// 共用資料與 utility code 可出現在多個 community，report 本身不新增 GraphNodeKind。
    /// </summary>
    /// <param name="snapshot">已通過 canonical validation 的 V3 snapshot。</param>
    /// <returns>可直接寫入 active manifest 的 primary community reports。</returns>
    public static IReadOnlyList<GraphCommunityReport> BuildPrimaryReports(
        GraphSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        GraphAssembler.ValidateSnapshot(snapshot);
        return BuildPrimaryReportsValidated(snapshot);
    }

    /// <summary>索引管線已由 assembler 驗證時使用，避免對大型 snapshot 重算 digest。</summary>
    internal static IReadOnlyList<GraphCommunityReport> BuildPrimaryReportsValidated(
        GraphSnapshot snapshot)
    {
        var nodes = snapshot.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var adjacency = BuildAdjacency(snapshot.Edges);
        var features = snapshot.Nodes
            .Where(node => node.Kind == GraphNodeKind.Feature)
            .OrderBy(node => node.Id, StringComparer.Ordinal)
            .ToList();
        var ownership = BuildPrimaryOwnership(features, snapshot.Edges, nodes);
        var reports = new List<GraphCommunityReport>();
        foreach (var group in features.GroupBy(
                         feature => ownership[feature.Id],
                         StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var memberIds = TraverseMembers(
                group.Select(feature => feature.Id),
                nodes,
                adjacency);
            var kinds = memberIds
                .Select(id => nodes[id].Kind)
                .GroupBy(kind => kind)
                .ToDictionary(item => item.Key, item => item.Count());
            var featureNames = group.Select(feature => feature.Name)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .Take(20)
                .ToList();
            var title = CommunityTitle(group.First());
            var summary =
                $"此業務社群包含 {kinds.GetValueOrDefault(GraphNodeKind.Feature)} 個功能、" +
                $"{kinds.GetValueOrDefault(GraphNodeKind.EntryPoint)} 個入口、" +
                $"{kinds.GetValueOrDefault(GraphNodeKind.Code)} 個程式碼單位與 " +
                $"{kinds.GetValueOrDefault(GraphNodeKind.Data)} 個資料節點。" +
                $"主要功能：{string.Join("、", featureNames)}。";
            reports.Add(new GraphCommunityReport(
                group.Key,
                "primary",
                title,
                summary,
                memberIds,
                CommunityCacheKey(nodes, memberIds)));
        }
        return reports;
    }

    /// <summary>
    /// 以固定 edge weight 的 deterministic label propagation 建立 secondary discovery community。
    /// 這是 Neo4j GDS 不可用時的保守 fallback；它不修改 domain node，也不影響 primary report。
    /// </summary>
    /// <param name="snapshot">V3 canonical snapshot。</param>
    /// <returns>至少包含兩個成員的次要社群。</returns>
    public static IReadOnlyList<GraphCommunityReport> BuildSecondaryReports(
        GraphSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        GraphAssembler.ValidateSnapshot(snapshot);
        return BuildSecondaryReportsValidated(snapshot);
    }

    /// <summary>索引管線已由 assembler 驗證時使用，避免 fallback 重複序列化整張圖。</summary>
    internal static IReadOnlyList<GraphCommunityReport> BuildSecondaryReportsValidated(
        GraphSnapshot snapshot)
    {
        var nodes = snapshot.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var weighted = new Dictionary<string, List<(string Target, double Weight)>>(
            StringComparer.Ordinal);
        foreach (var edge in snapshot.Edges)
        {
            var weight = EdgeWeight(edge.Kind);
            Add(edge.SourceId, edge.TargetId, weight);
            Add(edge.TargetId, edge.SourceId, weight);
        }

        var labels = nodes.Keys.ToDictionary(id => id, id => id, StringComparer.Ordinal);
        var orderedNodeIds = nodes.Keys.OrderBy(id => id, StringComparer.Ordinal).ToList();
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        for (var iteration = 0; iteration < LabelPropagationIterations; iteration++)
        {
            var changed = false;
            foreach (var nodeId in orderedNodeIds)
            {
                if (!weighted.TryGetValue(nodeId, out var neighbors) ||
                    neighbors.Count == 0)
                    continue;
                // 大型 FBL graph 每輪會造訪數萬個 node；逐 node 使用
                // GroupBy/Select/OrderBy 會製造數十萬個短命物件。重用 score map
                // 保留完全相同的「最高權重、label 字典序 tie-break」語意。
                scores.Clear();
                foreach (var neighbor in neighbors)
                {
                    var label = labels[neighbor.Target];
                    scores[label] = scores.GetValueOrDefault(label) + neighbor.Weight;
                }
                string? candidate = null;
                var candidateScore = double.NegativeInfinity;
                foreach (var pair in scores)
                {
                    if (pair.Value > candidateScore ||
                        pair.Value.Equals(candidateScore) &&
                        (candidate is null ||
                         StringComparer.Ordinal.Compare(pair.Key, candidate) < 0))
                    {
                        candidate = pair.Key;
                        candidateScore = pair.Value;
                    }
                }
                if (candidate is null) continue;
                if (string.Equals(labels[nodeId], candidate, StringComparison.Ordinal))
                    continue;
                labels[nodeId] = candidate;
                changed = true;
            }
            if (!changed) break;
        }

        var groups = labels.GroupBy(
                pair => pair.Value,
                pair => pair.Key,
                StringComparer.Ordinal)
            .Where(group => group.Count() >= 2)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => (IReadOnlyList<string>)group.ToList())
            .ToList();
        return BuildSecondaryReportsFromGroupsValidated(
            snapshot,
            groups,
            "label");

        void Add(string source, string target, double weight)
        {
            if (!weighted.TryGetValue(source, out var values))
            {
                values = [];
                weighted[source] = values;
            }
            values.Add((target, weight));
        }
    }

    /// <summary>
    /// 將 GDS Leiden 或 deterministic fallback 的 membership 轉成穩定 secondary reports。
    /// 執行期 community number 不進 identity；ID 只由排序後 member IDs 決定，
    /// 因此同一 topology 不會因 GDS 內部分配編號不同而破壞摘要 cache。
    /// </summary>
    /// <param name="snapshot">V3 canonical snapshot。</param>
    /// <param name="groups">每個 discovery community 的 domain node IDs。</param>
    /// <param name="algorithm">leiden 或 label，僅用於 report metadata 與可讀摘要。</param>
    /// <returns>至少兩個有效成員且每組有界的 secondary reports。</returns>
    public static IReadOnlyList<GraphCommunityReport> BuildSecondaryReportsFromGroups(
        GraphSnapshot snapshot,
        IEnumerable<IReadOnlyList<string>> groups,
        string algorithm)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentException.ThrowIfNullOrWhiteSpace(algorithm);
        GraphAssembler.ValidateSnapshot(snapshot);
        return BuildSecondaryReportsFromGroupsValidated(
            snapshot, groups, algorithm);
    }

    /// <summary>把已驗證 snapshot 的外部分群轉成 report，不重做 canonical digest。</summary>
    internal static IReadOnlyList<GraphCommunityReport>
        BuildSecondaryReportsFromGroupsValidated(
            GraphSnapshot snapshot,
            IEnumerable<IReadOnlyList<string>> groups,
            string algorithm)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentException.ThrowIfNullOrWhiteSpace(algorithm);
        if (algorithm is not ("leiden" or "label"))
            throw new ArgumentOutOfRangeException(
                nameof(algorithm),
                "Secondary community algorithm 只能是 leiden 或 label。");

        var nodes = snapshot.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var normalizedGroups = groups
            .Select(group => group
                .Where(nodes.ContainsKey)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .Take(MaximumMembersPerReport)
                .ToList())
            .Where(group => group.Count >= 2)
            .DistinctBy(
                group => string.Join('\0', group),
                StringComparer.Ordinal)
            .OrderBy(group => group[0], StringComparer.Ordinal)
            .ToList();
        var reports = new List<GraphCommunityReport>(normalizedGroups.Count);
        foreach (var members in normalizedGroups)
        {
            var memberNodes = members.Select(id => nodes[id]).ToList();
            var titleNode = memberNodes
                .OrderBy(node => node.Kind == GraphNodeKind.Feature ? 0 :
                    node.Kind == GraphNodeKind.EntryPoint ? 1 :
                    node.Kind == GraphNodeKind.Code ? 2 : 3)
                .ThenBy(node => node.Name, StringComparer.Ordinal)
                .First();
            var counts = memberNodes.GroupBy(node => node.Kind)
                .ToDictionary(item => item.Key, item => item.Count());
            var identity = GraphIdentity.Sha256(
                string.Join('\0', members))[..20];
            reports.Add(new GraphCommunityReport(
                $"secondary:{algorithm}:{identity}",
                "secondary",
                $"探索群組：{titleNode.Name}",
                $"此結構探索群組包含 " +
                $"{counts.GetValueOrDefault(GraphNodeKind.Feature)} 個功能、" +
                $"{counts.GetValueOrDefault(GraphNodeKind.EntryPoint)} 個入口、" +
                $"{counts.GetValueOrDefault(GraphNodeKind.Code)} 個程式碼單位與 " +
                $"{counts.GetValueOrDefault(GraphNodeKind.Data)} 個資料節點；" +
                (algorithm == "leiden"
                    ? "此群組由加權 Leiden 產生，"
                    : "此群組由 deterministic label propagation fallback 產生，") +
                "不代表業務 ownership。",
                members,
                CommunityCacheKey(nodes, members)));
        }
        return reports;
    }

    /// <summary>
    /// 依 SPEC 使用「member IDs＋member evidence hashes＋summary prompt version」建立快取鍵。
    /// Evidence 已由 assembler canonical sort；這裡仍固定排序 details，避免 Dictionary 列舉順序
    /// 使相同圖譜重複呼叫 LLM。只納入 report 實際有界成員，不把整張圖綁進單一社群 cache。
    /// </summary>
    private static string CommunityCacheKey(
        IReadOnlyDictionary<string, GraphNode> nodes,
        IReadOnlyList<string> memberIds)
    {
        var builder = new StringBuilder(CommunitySummaryPromptVersion);
        foreach (var memberId in memberIds.OrderBy(id => id, StringComparer.Ordinal))
        {
            builder.Append('\n').Append(memberId);
            if (!nodes.TryGetValue(memberId, out var node)) continue;
            foreach (var evidence in node.Evidence)
            {
                builder.Append('\n')
                    .Append((int)evidence.Source).Append('|')
                    .Append((int)evidence.Confidence).Append('|')
                    .Append(evidence.Artifact).Append('|')
                    .Append(evidence.StartLine).Append('|')
                    .Append(evidence.EndLine).Append('|')
                    .Append(evidence.Reason);
                if (evidence.Details is null) continue;
                foreach (var pair in evidence.Details.OrderBy(
                             pair => pair.Key,
                             StringComparer.Ordinal))
                    builder.Append('|').Append(pair.Key).Append('=').Append(pair.Value);
            }
        }
        return GraphIdentity.Sha256(builder.ToString());
    }

    /// <summary>
    /// 先依 Menu root／排程規則建立 ownership，再把 Maintain→Confirm 與 report feature 關係
    /// union 到同一 primary community。共享 Code／Data 不參與 ownership union，只在 report view 重用。
    /// </summary>
    private static IReadOnlyDictionary<string, string> BuildPrimaryOwnership(
        IReadOnlyList<GraphNode> features,
        IReadOnlyList<GraphEdge> edges,
        IReadOnlyDictionary<string, GraphNode> nodes)
    {
        var parent = features.ToDictionary(
            feature => feature.Id, feature => feature.Id, StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            if (!parent.ContainsKey(edge.SourceId) ||
                !parent.ContainsKey(edge.TargetId))
                continue;
            var source = nodes[edge.SourceId];
            var target = nodes[edge.TargetId];
            if (edge.Kind == GraphEdgeKind.Triggers ||
                source.Role == GraphRoles.CustomReport ||
                target.Role == GraphRoles.CustomReport)
                Union(edge.SourceId, edge.TargetId);
        }
        var identities = features.ToDictionary(
            feature => feature.Id,
            CommunityIdentity,
            StringComparer.Ordinal);
        var selectedByRoot = features
            .GroupBy(feature => Find(feature.Id), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(feature => identities[feature.Id])
                    .OrderBy(identity =>
                        identity == "primary:schedule-and-batch" ? 0 : 1)
                    .ThenBy(identity => identity, StringComparer.Ordinal)
                    .First(),
                StringComparer.Ordinal);
        return features.ToDictionary(
            feature => feature.Id,
            feature => selectedByRoot[Find(feature.Id)],
            StringComparer.Ordinal);

        string Find(string value)
        {
            var root = value;
            while (!string.Equals(parent[root], root, StringComparison.Ordinal))
                root = parent[root];
            while (!string.Equals(parent[value], value, StringComparison.Ordinal))
            {
                var next = parent[value];
                parent[value] = root;
                value = next;
            }
            return root;
        }

        void Union(string left, string right)
        {
            var leftRoot = Find(left);
            var rightRoot = Find(right);
            if (string.Equals(leftRoot, rightRoot, StringComparison.Ordinal)) return;
            if (StringComparer.Ordinal.Compare(leftRoot, rightRoot) < 0)
                parent[rightRoot] = leftRoot;
            else
                parent[leftRoot] = rightRoot;
        }
    }

    private static double EdgeWeight(GraphEdgeKind kind) => kind switch
    {
        GraphEdgeKind.RoutesTo => 1.00,
        GraphEdgeKind.Handles => 0.95,
        GraphEdgeKind.Triggers => 0.95,
        GraphEdgeKind.DispatchesTo => 0.90,
        GraphEdgeKind.Writes => 0.90,
        GraphEdgeKind.Reads => 0.85,
        GraphEdgeKind.MapsTo => 0.85,
        GraphEdgeKind.Calls => 0.75,
        _ => 0.70,
    };

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildAdjacency(
        IReadOnlyList<GraphEdge> edges)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            Add(edge.SourceId, edge.TargetId);
            Add(edge.TargetId, edge.SourceId);
        }
        return result.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value
                .OrderBy(value => value, StringComparer.Ordinal)
                .Take(MaximumNeighborsPerNode)
                .ToList(),
            StringComparer.Ordinal);

        void Add(string source, string target)
        {
            if (!result.TryGetValue(source, out var values))
            {
                values = new HashSet<string>(StringComparer.Ordinal);
                result[source] = values;
            }
            values.Add(target);
        }
    }

    private static IReadOnlyList<string> TraverseMembers(
        IEnumerable<string> featureIds,
        IReadOnlyDictionary<string, GraphNode> nodes,
        IReadOnlyDictionary<string, IReadOnlyList<string>> adjacency)
    {
        var selected = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<(string Id, int Depth)>();
        foreach (var featureId in featureIds)
        {
            if (selected.Add(featureId)) queue.Enqueue((featureId, 0));
        }
        while (queue.Count > 0 && selected.Count < MaximumMembersPerReport)
        {
            var current = queue.Dequeue();
            if (current.Depth >= 4 ||
                !adjacency.TryGetValue(current.Id, out var neighbors))
                continue;
            foreach (var neighbor in neighbors)
            {
                if (!nodes.ContainsKey(neighbor) || !selected.Add(neighbor)) continue;
                queue.Enqueue((neighbor, current.Depth + 1));
                if (selected.Count >= MaximumMembersPerReport) break;
            }
        }
        return selected.OrderBy(id => id, StringComparer.Ordinal).ToList();
    }

    private static string CommunityIdentity(GraphNode feature)
    {
        if (feature.Attributes.TryGetValue("menuPath", out var menuPath) &&
            !string.IsNullOrWhiteSpace(menuPath))
        {
            var root = menuPath.Split('>', StringSplitOptions.TrimEntries)[0];
            return $"primary:menu:{GraphIdentity.NormalizeRequiredToken(root, nameof(menuPath))}";
        }
        return feature.Role switch
        {
            GraphRoles.Schedule or GraphRoles.BatchReport => "primary:schedule-and-batch",
            GraphRoles.CustomReport => "primary:custom-report",
            _ => $"primary:feature:{GraphIdentity.NormalizeRequiredToken(feature.Name, nameof(feature))}",
        };
    }

    private static string CommunityTitle(GraphNode feature)
    {
        if (feature.Attributes.TryGetValue("menuPath", out var menuPath) &&
            !string.IsNullOrWhiteSpace(menuPath))
            return menuPath.Split('>', StringSplitOptions.TrimEntries)[0];
        return feature.Role switch
        {
            GraphRoles.Schedule or GraphRoles.BatchReport => "排程與批次",
            GraphRoles.CustomReport => "自訂報表",
            _ => feature.Name,
        };
    }
}
