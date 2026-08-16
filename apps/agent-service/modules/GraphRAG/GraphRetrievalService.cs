using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentService.Application.Contracts;
using AgentService.Modules.GraphRAG.FblAuthority;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AuthorityGraphNode = AgentService.Modules.GraphRAG.FblAuthority.GraphNode;
using AuthorityGraphNodeKind = AgentService.Modules.GraphRAG.FblAuthority.GraphNodeKind;
using AuthorityGraphRelationship = AgentService.Modules.GraphRAG.FblAuthority.GraphRelationship;

namespace AgentService.Modules.GraphRAG;

/// <summary>
/// FBL 專案問答的檢索預算。
/// 所有上限都是效能保護值，不會改變權威圖的節點或關係定義。
/// </summary>
public sealed class GraphRetrievalOptions
{
    /// <summary>第一輪 full-text 最多保留的種子節點數。</summary>
    public int SeedLimit { get; set; } = 12;

    /// <summary>從種子向外展開的最大層數。</summary>
    public int MaximumDepth { get; set; } = 3;

    /// <summary>每個節點每層最多讀取的鄰接關係數。</summary>
    public int NeighborsPerNode { get; set; } = 20;

    /// <summary>單次問答最多保留的圖節點數。</summary>
    public int MaximumNodes { get; set; } = 80;

    /// <summary>
    /// 問題含精確識別碼（例如菜單代號）時，針對最相關種子額外沿主幹關係深入追蹤的層數。
    /// 這個追蹤只沿 Opens/RoutesTo/ImplementedBy/Calls/Uses/ReadsVia/WritesVia/MapsTo/ReadsData/Executes/Queries
    /// 等主幹關係前進，因此即使層數比 <see cref="MaximumDepth"/> 深很多，展開的節點數仍受限。
    /// 目的是讓「從菜單代號出發」的資料流程問題，第一輪 Context Pack 就直接涵蓋到資料庫層，
    /// 不必讓 Agent 再多次呼叫 trace_project_graph_paths 才能補齊 Service/DAL/DB。
    /// </summary>
    public int PrimarySeedChaseDepth { get; set; } = 8;

    /// <summary>主幹追蹤每個節點最多讀取的鄰接關係數；刻意比 <see cref="NeighborsPerNode"/> 窄，只保留主要鏈路。</summary>
    public int PrimarySeedChaseNeighborsPerNode { get; set; } = 8;

    /// <summary>主幹追蹤額外允許保留的節點數上限，獨立於 <see cref="MaximumNodes"/>，避免深追時吃光一般展開的預算。</summary>
    public int PrimarySeedChaseMaximumNodes { get; set; } = 60;

    /// <summary>放入 LLM Prompt 的最大字元數。</summary>
    public int MaximumPromptCharacters { get; set; } = 28_000;

    /// <summary>單次問題最多執行的確定性查詢變體數。</summary>
    public int MaximumQueryVariants { get; set; } = 10;

    /// <summary>是否在確定性查詢完全沒有命中時，使用一次 LLM 產生 FBL 查詢候選。</summary>
    public bool EnableLlmQueryRewrite { get; set; } = true;

    /// <summary>LLM Query Rewrite 的總逾時秒數；逾時時回到原本的唯讀搜尋流程。</summary>
    public int LlmQueryRewriteTimeoutSeconds { get; set; } = 8;

    /// <summary>LLM 最多提供給 Graph 搜尋的候選詞數。</summary>
    public int MaximumLlmQueryVariants { get; set; } = 6;

    /// <summary>
    /// 「直接證據」區塊在結構鏈路與證據兩區塊可用字元預算中的基準佔比（0~1）。
    /// 問題沒有精確識別碼、無法觸發主幹深追時使用這個較保守的比例。
    /// </summary>
    public double EvidenceBudgetRatio { get; set; } = 0.45;

    /// <summary>
    /// 觸發主幹深追（問題含精確識別碼）時「直接證據」的佔比；此時結構鏈路已經涵蓋完整資料流程，
    /// 實際程式碼證據片段比更多鏈路預覽更有回答價值，因此把預算往證據傾斜。
    /// </summary>
    public double EvidenceBudgetRatioWithExactIdentifier { get; set; } = 0.6;

    /// <summary>「直接證據」區塊最多收錄的筆數，獨立於字元預算，避免單題塞入過多雷同片段。</summary>
    public int MaximumEvidenceItems { get; set; } = 30;
}

/// <summary>
/// 將自然語言問題編譯成有界、附直接證據的 FBL Graph Context Pack。
/// 本服務不要求 Community AI Summary 完成，也不把整張圖下載到記憶體；
/// 若圖中資訊不足，Prompt 會明確要求 Agent 使用唯讀程式碼工具繼續調查。
/// </summary>
public sealed class GraphRetrievalService
{
    private static readonly Regex SearchTokenPattern = new(
        @"[\p{L}\p{N}_./:-]{2,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex MenuIdPattern = new(
        @"(?<!\d)\d{3,12}(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TechnicalTokenPattern = new(
        @"(?<![\p{L}\p{N}_])[A-Za-z_][A-Za-z0-9_./:-]{2,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions RewriteJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
    private static readonly IReadOnlyDictionary<string, string[]> FblAliases =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["登入"] = ["Login", "ProcessLogin", "驗證", "權限"],
            ["交割"] = ["Settlement", "SettleDate", "SettlementDate"],
            ["補登"] = ["BackEntry", "ManualTrade", "InsertDeal"],
            ["覆核"] = ["Confirm", "Approve", "覆核放行"],
            ["報表"] = ["Report", "CustomReport", "PluginReport"],
            ["菜單"] = ["Menu", "tblMenuMap"],
            ["存檔"] = ["Save", "Insert", "Update"],
        };

    private readonly IGraphStore _store;
    private readonly GraphRetrievalOptions _options;
    private readonly ILogger<GraphRetrievalService> _logger;
    private readonly ILlmCompletionService? _llm;
    private readonly SemaphoreSlim _queryConcurrency = new(4, 4);

    /// <summary>建立 FBL Graph 檢索服務。</summary>
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

    /// <summary>
    /// 取得與目前問題相關的最小子圖，並編譯成可直接交給 LLM 的繁體中文 Context Pack。
    /// 專案根目錄只用於標示工具作用域，不會由本方法直接遞迴掃描原始碼。
    /// </summary>
    /// <param name="projectId">Modern Wingman 專案 ID。</param>
    /// <param name="rootPath">已驗證的專案原始碼根目錄。</param>
    /// <param name="question">本輪使用者問題，不包含舊對話文字。</param>
    /// <param name="cancellationToken">取消檢索的 token。</param>
    /// <param name="providerProfileId">本輪對話使用的 Provider；只供低信心 Query Rewrite 使用。</param>
    /// <param name="modelId">本輪對話使用的模型；只供低信心 Query Rewrite 使用。</param>
    /// <param name="allowLlmQueryRewrite">是否允許低信心時呼叫一次 LLM；診斷與驗收可關閉。</param>
    /// <param name="activity">目前問答要求的進度事件回報器；可為 null。</param>
    public async Task<string> BuildAnswerPromptAsync(
        string projectId,
        string rootPath,
        string question,
        CancellationToken cancellationToken = default,
        string? providerProfileId = null,
        string? modelId = null,
        bool allowLlmQueryRewrite = true,
        AgentActivityReporter? activity = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(question);

        var retrievalActivityId = activity is null
            ? null
            : await activity.StartAsync(
                "retrieval.started",
                "搜尋知識圖譜",
                tool: "graph_retrieval",
                detail: "正在尋找與問題相關的 FBL 節點與鏈路");
        var retrievalStopwatch = Stopwatch.StartNew();
        try
        {
            var graphVersion = await _store.GetActiveManifestAsync(projectId, cancellationToken);
            if (string.IsNullOrWhiteSpace(graphVersion))
            {
                if (retrievalActivityId is not null)
                    await activity!.CompleteAsync(
                        retrievalActivityId,
                        "目前沒有可用的 Graph 版本",
                        "知識圖譜檢索完成");
                return BuildMissingGraphPrompt(question);
            }

            var seedStopwatch = Stopwatch.StartNew();
            var seeds = await SearchSeedsAsync(
                projectId,
                question,
                providerProfileId,
                modelId,
                allowLlmQueryRewrite,
                cancellationToken,
                activity);
            seedStopwatch.Stop();
            _logger.LogInformation(
                "GraphRAG Seeds 查詢完成。耗時={ElapsedMs}ms，找到 {Count} 個候選，ProjectId={ProjectId}",
                seedStopwatch.ElapsedMilliseconds,
                seeds.Count,
                projectId);
            if (seeds.Count == 0)
            {
                if (retrievalActivityId is not null)
                    await activity!.CompleteAsync(
                        retrievalActivityId,
                        "沒有找到可用的候選節點",
                        "知識圖譜檢索完成");
                return BuildMissingSeedPrompt(question, graphVersion);
            }

            var expandStopwatch = Stopwatch.StartNew();
            var context = await ExpandAsync(projectId, seeds, cancellationToken);
            expandStopwatch.Stop();
            _logger.LogInformation(
                "GraphRAG 子圖展開完成。耗時={ElapsedMs}ms，節點數={Nodes}，關係數={Relationships}",
                expandStopwatch.ElapsedMilliseconds,
                context.Nodes.Count,
                context.Relationships.Count);
            var hasExactIdentifier = BuildQueryPlan(question, _options.MaximumQueryVariants).HasExactIdentifier;
            if (hasExactIdentifier)
            {
                var primarySeed = seeds
                    .OrderByDescending(hit => hit.Node.Kind == AuthorityGraphNodeKind.Menu ? 1 : 0)
                    .ThenByDescending(hit => hit.Score)
                    .First();
                var chaseStopwatch = Stopwatch.StartNew();
                context = await ChaseBackboneAsync(
                    projectId,
                    primarySeed.Node.Key,
                    context,
                    cancellationToken);
                chaseStopwatch.Stop();
                _logger.LogInformation(
                    "GraphRAG 主幹深追完成。耗時={ElapsedMs}ms，合併後節點數={Nodes}",
                    chaseStopwatch.ElapsedMilliseconds,
                    context.Nodes.Count);
            }
            var prompt = BuildContextPack(question, rootPath, graphVersion, seeds, context, hasExactIdentifier);
            retrievalStopwatch.Stop();
            _logger.LogInformation(
                "GraphRAG 檢索總耗時={ElapsedMs}ms，Prompt 字元數={PromptLength}，ProjectId={ProjectId}",
                retrievalStopwatch.ElapsedMilliseconds,
                prompt.Length,
                projectId);
            if (retrievalActivityId is not null)
                await activity!.CompleteAsync(
                    retrievalActivityId,
                    $"找到 {seeds.Count} 個候選節點，已建立 Graph Context",
                    "知識圖譜檢索完成");
            return prompt;
        }
        catch (Exception exception)
        {
            retrievalStopwatch.Stop();
            if (retrievalActivityId is not null)
                await activity!.FailAsync(
                    retrievalActivityId,
                    "知識圖譜檢索失敗，請檢查 Graph 連線或索引狀態");
            _logger.LogWarning(
                exception,
                "GraphRAG 建立問答 Context 失敗，耗時={ElapsedMs}ms，ProjectId={ProjectId}",
                retrievalStopwatch.ElapsedMilliseconds,
                projectId);
            throw;
        }
    }

    /// <summary>
    /// 執行不呼叫 LLM 的確定性 seed 診斷，供驗收與診斷端點確認 Query Plan 是否命中。
    /// 這個方法不展開 Graph，也不改變 active graph；它只回傳多路查詢合併後的候選節點。
    /// </summary>
    public async Task<IReadOnlyList<GraphSearchHit>> SearchSeedCandidatesAsync(
        string projectId,
        string question,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        var plan = BuildQueryPlan(question, _options.MaximumQueryVariants);
        return await SearchQueryPlanAsync(
            projectId,
            question,
            plan.Queries,
            cancellationToken,
            Math.Clamp(limit, 1, 30));
    }

    /// <summary>
    /// 建立知識圖譜 Viewer 使用的安全 Lucene 查詢。
    /// Viewer 只需要完整詞與詞首匹配，不應直接把使用者輸入當成 Lucene 語法；
    /// 因此沿用 FBL Query Plan 的 deterministic tokenization，再逐詞跳脫控制字元。
    /// </summary>
    /// <param name="query">Viewer 搜尋框輸入的自然語言或程式符號。</param>
    /// <returns>可交給 Neo4j V4 full-text index 的 bounded query。</returns>
    public static string BuildViewerLuceneQuery(string query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var terms = BuildQueryPlan(query, 20).Queries
            .SelectMany(searchQuery => SearchTokenPattern.Matches(searchQuery.Text)
                .Select(match => match.Value.Trim()))
            .Where(value => value.Length is >= 2 and <= 100)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .Select(EscapeLuceneTerm)
            .ToArray();
        if (terms.Length == 0)
            throw new ArgumentException("搜尋文字沒有可用的 FBL token。", nameof(query));
        return string.Join(" OR ", terms.Select(term => $"\"{term}\" OR {term}*"));
    }

    /// <summary>
    /// 提供給仍使用舊 JIRA GraphRAG 契約的應用層相容入口。
    ///
    /// V4 的權威圖仍只使用 FBL authority node/relationship；本方法只在邊界上
    /// 將查詢結果投影成 JIRA 需要的扁平資料，避免把舊 GraphModel 重新帶回索引核心。
    /// </summary>
    public async Task<LegacyGraphRetrievalContext> LocalSearchAsync(
        string projectId,
        string question,
        CancellationToken cancellationToken = default)
    {
        var seeds = await SearchSeedCandidatesAsync(
            projectId,
            question,
            _options.SeedLimit,
            cancellationToken);
        if (seeds.Count == 0)
        {
            return new LegacyGraphRetrievalContext([], []);
        }

        var subgraph = await ExpandAsync(projectId, seeds, cancellationToken);
        var seedScores = seeds.ToDictionary(
            hit => hit.Node.Key,
            hit => hit.Score,
            StringComparer.Ordinal);

        var nodes = subgraph.Nodes
            .Select(node =>
            {
                var isSeed = seedScores.TryGetValue(node.Key, out var seedScore);
                return new LegacyScoredGraphNode(
                    ToLegacyGraphNode(node),
                    isSeed ? seedScore : 0.35,
                    isSeed,
                    isSeed ? 0 : 1);
            })
            .OrderByDescending(node => node.Score)
            .ThenBy(node => node.Node.Id, StringComparer.Ordinal)
            .ToArray();

        var edges = subgraph.Relationships
            .Select(relationship => new LegacyGraphRelationship(
                relationship.Id,
                relationship.SourceKey,
                relationship.TargetKey,
                GraphSchema.GetRelationshipType(relationship.Kind)))
            .ToArray();

        return new LegacyGraphRetrievalContext(nodes, edges);
    }

    /// <summary>將 V4 authority node 投影為 JIRA 邊界相容模型。</summary>
    private static LegacyGraphNode ToLegacyGraphNode(
        AuthorityGraphNode node)
    {
        var properties = node.Properties;
        var name = ReadProperty(properties, "name", "display_name") ?? node.Key;
        var role = ReadProperty(properties, "role", "code_role")
            ?? LegacyGraphMappings.RoleFor(node.Kind);
        var aliases = ReadStringValues(properties, "aliases", "alias");
        var searchableText = string.Join(
            " ",
            new[] { node.Key, name, role }
                .Concat(aliases)
                .Concat(properties.Values.Select(value => value?.ToString() ?? string.Empty))
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        var evidence = new List<LegacyGraphEvidence>();

        if (properties.TryGetValue("source_file", out var sourceFile) && sourceFile is not null)
        {
            evidence.Add(new LegacyGraphEvidence(sourceFile.ToString()!));
        }

        return new LegacyGraphNode(
            node.Key,
            LegacyGraphMappings.KindFor(node.Kind),
            role,
            name,
            ReadProperty(properties, "file_path", "source_file"),
            ReadIntProperty(properties, "start_line", "line"),
            ReadIntProperty(properties, "end_line"),
            ReadProperty(properties, "language") ?? "C#",
            aliases,
            searchableText,
            evidence);
    }

    /// <summary>讀取 authority node 的第一個非空字串屬性。</summary>
    private static string? ReadProperty(
        IReadOnlyDictionary<string, object?> properties,
        params string[] names) => names
        .Select(name => properties.GetValueOrDefault(name)?.ToString())
        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    /// <summary>讀取可能是單值、陣列或 JSON 陣列的字串屬性。</summary>
    private static IReadOnlyList<string> ReadStringValues(
        IReadOnlyDictionary<string, object?> properties,
        params string[] names)
    {
        foreach (var name in names)
        {
            if (!properties.TryGetValue(name, out var value) || value is null)
            {
                continue;
            }

            if (value is IEnumerable<object?> values)
            {
                return values.Select(item => item?.ToString() ?? string.Empty)
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }

            var text = value.ToString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text.Split(['、', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
        }

        return [];
    }

    /// <summary>以安全轉換讀取 optional 整數行號。</summary>
    private static int? ReadIntProperty(
        IReadOnlyDictionary<string, object?> properties,
        params string[] names)
    {
        var text = ReadProperty(properties, names);
        return int.TryParse(text, out var value) ? value : null;
    }

    /// <summary>
    /// 先以完整問題、精確識別碼、技術名稱與 FBL 別名執行多路 BM25；
    /// 確定性查詢完全沒有命中時，才使用一次受限的 LLM Query Rewrite。
    /// 原始問題永遠保留，LLM 只提供候選搜尋詞，不得直接產生 nodeId 或 Cypher。
    /// </summary>
    private async Task<IReadOnlyList<GraphSearchHit>> SearchSeedsAsync(
        string projectId,
        string question,
        string? providerProfileId,
        string? modelId,
        bool allowLlmQueryRewrite,
        CancellationToken cancellationToken,
        AgentActivityReporter? activity = null)
    {
        var plan = BuildQueryPlan(question, _options.MaximumQueryVariants);
        var deterministic = await SearchQueryPlanAsync(
            projectId,
            question,
            plan.Queries,
            cancellationToken);

        if (!ShouldUseLlmRewrite(plan, deterministic, allowLlmQueryRewrite))
        {
            return deterministic;
        }

        var rewriteActivityId = activity is null
            ? null
            : await activity.StartAsync(
                "query_rewrite.started",
                "改寫搜尋查詢",
                tool: "query_rewrite",
                detail: "確定性查詢沒有命中，正在產生 FBL 同義詞與技術名稱");
        IReadOnlyList<RepositorySearchQuery> rewriteQueries;
        try
        {
            rewriteQueries = await TryRewriteQueryAsync(
                question,
                providerProfileId,
                modelId,
                cancellationToken);
            if (rewriteActivityId is not null)
                await activity!.CompleteAsync(
                    rewriteActivityId,
                    rewriteQueries.Count == 0
                        ? "沒有產生可用的替代查詢"
                        : $"產生 {rewriteQueries.Count} 個替代查詢");
        }
        catch
        {
            if (rewriteActivityId is not null)
                await activity!.FailAsync(
                    rewriteActivityId,
                    "查詢改寫失敗，將保留原始查詢結果");
            throw;
        }
        if (rewriteQueries.Count == 0)
        {
            return deterministic;
        }

        var expandedPlan = plan with
        {
            Queries = plan.Queries
                .Concat(rewriteQueries)
                .GroupBy(query => query.Text, StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(query => query.Priority)
                    .First())
                .Take(_options.MaximumQueryVariants + _options.MaximumLlmQueryVariants)
                .ToArray(),
        };

        var expanded = await SearchQueryPlanAsync(
            projectId,
            question,
            expandedPlan.Queries,
            cancellationToken);
        return expanded.Count > 0 ? expanded : deterministic;
    }

    /// <summary>執行一組有優先順序的 Graph 查詢，並合併相同節點的最佳候選。</summary>
    private async Task<IReadOnlyList<GraphSearchHit>> SearchQueryPlanAsync(
        string projectId,
        string question,
        IReadOnlyList<RepositorySearchQuery> queries,
        CancellationToken cancellationToken,
        int? resultLimit = null)
    {
        var selectedQueries = queries
            .Where(query => !string.IsNullOrWhiteSpace(query.Text))
            .Take(_options.MaximumQueryVariants + _options.MaximumLlmQueryVariants)
            .ToArray();
        if (selectedQueries.Length == 0)
        {
            return [];
        }

        var effectiveLimit = Math.Clamp(
            resultLimit ?? _options.SeedLimit,
            1,
            30);
        var perQuery = Math.Clamp(effectiveLimit / 2, 3, 8);
        // 同一輪查詢變體最多四個同時進入 Neo4j；保留全部候選與原有排序，
        // 只限制資料庫連線尖峰，避免多個使用者同時問答時形成無上限 Task.WhenAll。
        var resultSets = await Task.WhenAll(selectedQueries.Select(async query =>
        {
            await _queryConcurrency.WaitAsync(cancellationToken);
            try
            {
                return await SearchOneQueryAsync(
                    projectId,
                    query,
                    perQuery,
                    cancellationToken);
            }
            finally
            {
                _queryConcurrency.Release();
            }
        }));

        return resultSets
            .SelectMany(values => values)
            .GroupBy(hit => hit.Hit.Node.Key, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(item => item.Query.Priority)
                .ThenByDescending(item => item.Hit.Score)
                .First())
            .OrderByDescending(item => IntentBoost(question, item.Hit.Node))
            .ThenByDescending(item => item.Query.Priority)
            .ThenByDescending(item => item.Hit.Score)
            .ThenBy(item => item.Hit.Node.Key, StringComparer.Ordinal)
            .Take(effectiveLimit)
            .Select(item => item.Hit)
            .ToArray();
    }

    /// <summary>
    /// 執行單一查詢並隔離個別 full-text 解析錯誤；一個候選失敗不得讓其他查詢整批失敗。
    /// 使用者取消仍會正常向上傳遞，避免背景工作被吞掉。
    /// </summary>
    private async Task<IReadOnlyList<WeightedSearchHit>> SearchOneQueryAsync(
        string projectId,
        RepositorySearchQuery query,
        int limit,
        CancellationToken cancellationToken)
    {
        try
        {
            var hits = await _store.SearchAsync(
                projectId,
                query.Text,
                limit,
                cancellationToken);
            return hits
                .Select(hit => new WeightedSearchHit(hit, query))
                .ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogDebug(
                exception,
                "GraphRAG 單一查詢失敗，忽略候選並繼續其它查詢：{Query}",
                query.Text);
            return [];
        }
    }

    /// <summary>
    /// 建立 FBL 專用 Query Plan。完整問題不會被丟棄；數字菜單、路由、
    /// C# symbol 與已知業務別名只是額外的查詢通道，避免把自然語言硬切成單一片段。
    /// </summary>
    internal static RepositoryQueryPlan BuildQueryPlan(
        string question,
        int maximumVariants = 10)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        var queries = new List<RepositorySearchQuery>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = string.Join(' ', SearchTokenPattern.Matches(question)
            .Select(match => match.Value.Trim())
            .Where(value => value.Length is >= 2 and <= 100));
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            AddQuery(queries, seen, normalized, QueryPriority.OriginalQuestion);
        }

        var exactIdentifiers = MenuIdPattern.Matches(question)
            .Select(match => match.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var identifier in exactIdentifiers)
        {
            AddQuery(queries, seen, identifier, QueryPriority.ExactIdentifier);
        }

        foreach (Match match in TechnicalTokenPattern.Matches(question))
        {
            AddQuery(queries, seen, match.Value.Trim(), QueryPriority.TechnicalToken);
        }

        foreach (var alias in FblAliases.Where(pair =>
                     question.Contains(pair.Key, StringComparison.OrdinalIgnoreCase)))
        {
            AddQuery(queries, seen, alias.Key, QueryPriority.BusinessTerm);
            foreach (var expanded in alias.Value)
            {
                AddQuery(queries, seen, expanded, QueryPriority.Alias);
            }
        }

        if (queries.Count == 0)
        {
            AddQuery(
                queries,
                seen,
                question[..Math.Min(question.Length, 100)],
                QueryPriority.OriginalQuestion);
        }

        return new RepositoryQueryPlan(
            queries
                .OrderByDescending(query => query.Priority)
                .ThenBy(query => query.Text, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Clamp(maximumVariants, 1, 20))
                .ToArray(),
            exactIdentifiers.Length > 0);
    }

    /// <summary>將單一查詢加入計畫，避免同一詞因不同抽取通道重複打 Neo4j。</summary>
    private static void AddQuery(
        ICollection<RepositorySearchQuery> queries,
        ISet<string> seen,
        string text,
        int priority)
    {
        var normalized = NormalizeQueryText(text);
        if (normalized is null || !seen.Add(normalized))
        {
            return;
        }
        queries.Add(new RepositorySearchQuery(normalized, priority, false));
    }

    /// <summary>
    /// 將查詢限制為現有 FBL Full-text 可接受的文字 token，移除引號、括號與任意
    /// Lucene 控制字元；這同時防止 LLM 輸出把任意查詢語法直接送進 Neo4j。
    /// </summary>
    private static string? NormalizeQueryText(string text)
    {
        var normalized = string.Join(' ', SearchTokenPattern.Matches(text)
            .Select(match => match.Value.Trim())
            .Where(value => value.Length is >= 2 and <= 100));
        return normalized.Length is >= 2 and <= 100 ? normalized : null;
    }

    /// <summary>跳脫 Neo4j full-text 使用的 Lucene 控制字元，避免搜尋輸入改變查詢語意。</summary>
    private static string EscapeLuceneTerm(string value)
    {
        const string luceneControlCharacters = "+-!(){}[]^\"~*?:/|&";
        var builder = new StringBuilder(value.Length + 8);
        foreach (var character in value)
        {
            if (luceneControlCharacters.Contains(character, StringComparison.Ordinal))
                builder.Append('\\');
            builder.Append(character);
        }
        return builder.ToString();
    }

    /// <summary>只有沒有精確識別碼且確定性查詢完全無命中時，才啟用一次 LLM Rewrite。</summary>
    private bool ShouldUseLlmRewrite(
        RepositoryQueryPlan plan,
        IReadOnlyList<GraphSearchHit> deterministicHits,
        bool allowLlmQueryRewrite) =>
        allowLlmQueryRewrite &&
        _options.EnableLlmQueryRewrite &&
        _llm is not null &&
        !plan.HasExactIdentifier &&
        deterministicHits.Count == 0;

    /// <summary>
    /// 呼叫一次受限 LLM 取得查詢候選。輸出只接受 queries、terms、aliases 三個陣列，
    /// 並截斷長度與數量；任何逾時、格式錯誤或 Provider 未就緒都回傳空集合。
    /// </summary>
    private async Task<IReadOnlyList<RepositorySearchQuery>> TryRewriteQueryAsync(
        string question,
        string? providerProfileId,
        string? modelId,
        CancellationToken cancellationToken)
    {
        if (_llm is null)
        {
            return [];
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.LlmQueryRewriteTimeoutSeconds));
        var prompt = $"""
            你是 FBL 投資系統的唯讀搜尋查詢改寫器。
            只為知識圖譜與原始碼搜尋產生候選詞，不要回答問題，不要產生 Cypher，
            不要捏造 nodeId、檔案路徑或不存在的程式符號。
            請只回傳 JSON 物件，欄位為 queries、terms、aliases，三者都必須是字串陣列。
            每個陣列最多 {_options.MaximumLlmQueryVariants} 項，每項最多 100 字。
            保留 FBL 專有名詞、英文類別名、Controller、Method、Table、欄位與路由。

            <user-question>{TruncateForRewrite(question)}</user-question>
            """;

        try
        {
            var raw = await _llm.CompleteAsync(
                prompt,
                providerProfileId,
                modelId,
                timeout.Token);
            return ParseRewriteQueries(raw, _options.MaximumLlmQueryVariants);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("GraphRAG Query Rewrite 逾時，回到確定性搜尋。" );
            return [];
        }
        catch (Exception exception)
        {
            _logger.LogDebug(
                exception,
                "GraphRAG Query Rewrite 失敗，回到確定性搜尋。" );
            return [];
        }
    }

    /// <summary>解析並清理 LLM JSON；無法解析時不讓模型輸出污染 Graph 查詢。</summary>
    internal static IReadOnlyList<RepositorySearchQuery> ParseRewriteQueries(
        string raw,
        int maximumVariants = 6)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var json = StripJsonFence(raw.Trim());
        try
        {
            var response = JsonSerializer.Deserialize<QueryRewriteResponse>(
                json,
                RewriteJsonOptions);
            if (response is null)
            {
                return [];
            }

            var values = (response.Queries ?? [])
                .Concat(response.Terms ?? [])
                .Concat(response.Aliases ?? []);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return values
                .Select(value => value is null ? null : NormalizeQueryText(value))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Where(value => seen.Add(value!))
                .Take(Math.Clamp(maximumVariants, 1, 20))
                .Select(value => new RepositorySearchQuery(
                    value!,
                    QueryPriority.LlmRewrite,
                    true))
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>移除常見 Markdown code fence，讓模型偶爾包住 JSON 時仍可安全解析。</summary>
    private static string StripJsonFence(string value)
    {
        if (!value.StartsWith("```", StringComparison.Ordinal))
        {
            return value;
        }
        var firstLineEnd = value.IndexOf('\n');
        var lastFence = value.LastIndexOf("```", StringComparison.Ordinal);
        if (firstLineEnd < 0 || lastFence <= firstLineEnd)
        {
            return value;
        }
        return value[(firstLineEnd + 1)..lastFence].Trim();
    }

    /// <summary>限制送入 Query Rewrite 的使用者文字，避免不受控 Prompt 大小。</summary>
    private static string TruncateForRewrite(string question) =>
        question.Length <= 1_000 ? question : question[..1_000] + "…";

    /// <summary>
    /// 以批次 BFS 展開上下游。每個節點只展開一次，且以節點數、深度及鄰居數三層上限
    /// 防止大型共用元件造成圖爆炸。
    /// </summary>
    /// <remarks>
    /// <see cref="GraphRetrievalOptions.MaximumNodes"/> 是所有種子共用的全域預算；
    /// 展開順序若只按節點 key 字母排序，只為湊多樣性補進來的低分種子可能搶先耗用
    /// 預算，導致真正命中的高分種子（例如 Menu 精確代號）反而展不到 DAL／資料庫層。
    /// 這裡改以種子分數（並沿路徑衰減後傳給鄰居）由高到低展開，讓預算優先花在
    /// 最貼近問題的節點鏈路上。
    /// </remarks>
    private async Task<RetrievalSubgraph> ExpandAsync(
        string projectId,
        IReadOnlyList<GraphSearchHit> seeds,
        CancellationToken cancellationToken)
    {
        var nodes = seeds.ToDictionary(hit => hit.Node.Key, hit => hit.Node, StringComparer.Ordinal);
        var relationships = new Dictionary<string, AuthorityGraphRelationship>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var relevance = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var hit in seeds)
        {
            if (!relevance.TryGetValue(hit.Node.Key, out var existing) || hit.Score > existing)
                relevance[hit.Node.Key] = hit.Score;
        }
        var frontier = seeds
            .Select(hit => hit.Node.Key)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(key => relevance.GetValueOrDefault(key))
            .ToArray();

        for (var depth = 0;
             depth < _options.MaximumDepth && frontier.Length > 0 && nodes.Count < _options.MaximumNodes;
             depth++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = await _store.GetNeighborsBatchAsync(
                projectId,
                frontier,
                _options.NeighborsPerNode,
                cancellationToken);
            var next = new List<string>();
            foreach (var centerKey in frontier
                         .OrderByDescending(key => relevance.GetValueOrDefault(key))
                         .ThenBy(key => key, StringComparer.Ordinal))
            {
                if (!visited.Add(centerKey) || !batch.TryGetValue(centerKey, out var neighbors))
                {
                    continue;
                }

                var centerScore = relevance.GetValueOrDefault(centerKey);
                foreach (var neighbor in neighbors
                             .OrderByDescending(item => RelationshipWeight(item.Relationship.Kind))
                             .ThenBy(item => item.Relationship.Id, StringComparer.Ordinal))
                {
                    relationships.TryAdd(neighbor.Relationship.Id, neighbor.Relationship);
                    // 鄰居沿用來源節點分數的衰減值，讓遠離高分種子的分支自然排到預算後段。
                    var neighborScore = centerScore * 0.85;
                    if (!relevance.TryGetValue(neighbor.Node.Key, out var existingScore) ||
                        neighborScore > existingScore)
                        relevance[neighbor.Node.Key] = neighborScore;
                    if (!nodes.ContainsKey(neighbor.Node.Key) && nodes.Count < _options.MaximumNodes)
                    {
                        nodes[neighbor.Node.Key] = neighbor.Node;
                        next.Add(neighbor.Node.Key);
                    }
                }
            }

            frontier = next
                .Distinct(StringComparer.Ordinal)
                .OrderByDescending(key => relevance.GetValueOrDefault(key))
                .ToArray();
        }

        return new RetrievalSubgraph(
            nodes.Values.OrderBy(node => node.Key, StringComparer.Ordinal).ToArray(),
            relationships.Values
                .OrderByDescending(relationship => RelationshipWeight(relationship.Kind))
                .ThenBy(relationship => relationship.Id, StringComparer.Ordinal)
                .ToArray(),
            nodes.Count >= _options.MaximumNodes);
    }

    /// <summary>
    /// 只沿主幹關係（Opens/RoutesTo/ImplementedBy/Calls/Uses/ReadsVia/WritesVia/MapsTo/
    /// ReadsData/Executes/Queries 等 <see cref="RelationshipWeight"/> 為 100 的種類）從單一高信心
    /// 種子（例如精確命中的 Menu 代號）向下深追，並把結果併入既有子圖。
    /// </summary>
    /// <remarks>
    /// 這是專門處理「從菜單代號出發問資料流程」這類問題的捷徑：一般 <see cref="ExpandAsync"/> 的
    /// <see cref="GraphRetrievalOptions.MaximumDepth"/> 只保護大範圍展開不爆炸，通常到不了
    /// Service/DAL/資料庫層；讓 Agent 自己再呼叫 trace_project_graph_paths 補齊，會多花一到兩輪
    /// 完整的 LLM 往返時間。單一種子、只沿主幹關係的深追範圍很小，在 Neo4j 端幾乎不增加延遲，
    /// 卻能讓第一版 Context Pack 就直接涵蓋完整鏈路。
    /// </remarks>
    private async Task<RetrievalSubgraph> ChaseBackboneAsync(
        string projectId,
        string seedKey,
        RetrievalSubgraph baseContext,
        CancellationToken cancellationToken)
    {
        var nodes = baseContext.Nodes.ToDictionary(node => node.Key, StringComparer.Ordinal);
        var relationships = baseContext.Relationships.ToDictionary(
            relationship => relationship.Id, StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal) { seedKey };
        var frontier = new[] { seedKey };
        var chaseBudget = nodes.Count + _options.PrimarySeedChaseMaximumNodes;

        for (var depth = 0;
             depth < _options.PrimarySeedChaseDepth && frontier.Length > 0 && nodes.Count < chaseBudget;
             depth++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = await _store.GetNeighborsBatchAsync(
                projectId,
                frontier,
                _options.PrimarySeedChaseNeighborsPerNode,
                cancellationToken);
            var next = new List<string>();
            foreach (var centerKey in frontier)
            {
                if (!visited.Add(centerKey) || !batch.TryGetValue(centerKey, out var neighbors))
                {
                    continue;
                }

                foreach (var neighbor in neighbors
                             .Where(item => RelationshipWeight(item.Relationship.Kind) == 100)
                             .OrderBy(item => item.Relationship.Id, StringComparer.Ordinal))
                {
                    relationships.TryAdd(neighbor.Relationship.Id, neighbor.Relationship);
                    if (!nodes.ContainsKey(neighbor.Node.Key) && nodes.Count < chaseBudget)
                    {
                        nodes[neighbor.Node.Key] = neighbor.Node;
                        next.Add(neighbor.Node.Key);
                    }
                }
            }

            frontier = next.Distinct(StringComparer.Ordinal).ToArray();
        }

        return new RetrievalSubgraph(
            nodes.Values.OrderBy(node => node.Key, StringComparer.Ordinal).ToArray(),
            relationships.Values
                .OrderByDescending(relationship => RelationshipWeight(relationship.Kind))
                .ThenBy(relationship => relationship.Id, StringComparer.Ordinal)
                .ToArray(),
            baseContext.WasTruncated || nodes.Count >= chaseBudget);
    }

    /// <summary>建立有明確事實、證據與缺口區段的 Prompt，並依總字元上限裁切。</summary>
    private string BuildContextPack(
        string question,
        string rootPath,
        string graphVersion,
        IReadOnlyList<GraphSearchHit> seeds,
        RetrievalSubgraph context,
        bool hasExactIdentifier)
    {
        var nodes = context.Nodes.ToDictionary(node => node.Key, StringComparer.Ordinal);
        var builder = new StringBuilder();
        builder.AppendLine("# FBL 投資系統專案問答");
        builder.AppendLine($"使用者問題：{question}");
        builder.AppendLine($"Graph 版本：{graphVersion}");
        builder.AppendLine($"原始碼工具根目錄：{rootPath}");
        builder.AppendLine();
        builder.AppendLine("## 命中的入口與實體");
        foreach (var seed in seeds)
        {
            builder.AppendLine($"- [{seed.Node.Kind}] {DisplayName(seed.Node)} " +
                               $"(nodeId: {seed.Node.Key}, score: {seed.Score:0.###})");
        }

        // Token Budget Solver：結構鏈路與直接證據各自拿到獨立的字元預算上限，
        // 避免其中一個區塊（通常是結構鏈路，項目多但單行資訊量低）把另一個擠到只剩幾行。
        const int reservedTailCharacters = 2_000;
        var overallCeiling = Math.Max(_options.MaximumPromptCharacters - reservedTailCharacters, builder.Length);
        var plan = ComputeBudgetPlan(
            Math.Max(overallCeiling - builder.Length, 0),
            hasExactIdentifier,
            _options.EvidenceBudgetRatio,
            _options.EvidenceBudgetRatioWithExactIdentifier);
        var structuralCeiling = Math.Min(builder.Length + plan.StructuralBudget, overallCeiling);

        builder.AppendLine();
        builder.AppendLine("## 已確認的結構鏈路");
        var appended = 0;
        foreach (var relationship in context.Relationships)
        {
            if (!nodes.TryGetValue(relationship.SourceKey, out var source) ||
                !nodes.TryGetValue(relationship.TargetKey, out var target))
            {
                continue;
            }

            var line = $"- [{source.Kind}] {DisplayName(source)} (nodeId: {source.Key}) " +
                       $"-({GraphSchema.GetRelationshipType(relationship.Kind)})-> " +
                       $"[{target.Kind}] {DisplayName(target)} (nodeId: {target.Key})";
            if (!TryAppendWithinBudget(builder, line, structuralCeiling))
            {
                break;
            }
            appended++;
        }
        if (appended == 0)
        {
            builder.AppendLine("- 目前只找到實體，沒有在預算內取得可確認的相鄰關係。");
        }

        builder.AppendLine();
        builder.AppendLine("## 直接證據");
        // Context Reranker：結構鏈路已被 structuralCeiling 卡住，證據區塊保證至少拿得到
        // plan.EvidenceBudget 的預算；候選順序改用 DiversifyEvidenceOrder，去除幾乎重複的證據、
        // 跨檔案輪流選取，避免同一個檔案的大量雷同關係把其他來源的證據擠出預算之外。
        var evidenceCandidates = DiversifyEvidenceOrder(context.Relationships);
        var evidenceCount = 0;
        foreach (var relationship in evidenceCandidates)
        {
            var evidence = EvidenceLine(relationship);
            if (evidence is null)
            {
                continue;
            }
            if (!TryAppendWithinBudget(builder, $"- {evidence}", overallCeiling))
            {
                break;
            }
            evidenceCount++;
            if (evidenceCount >= _options.MaximumEvidenceItems)
            {
                break;
            }
        }
        if (evidenceCount == 0)
        {
            builder.AppendLine("- 本次子圖未包含可顯示的檔案行號；請使用原始碼工具確認。");
        }

        builder.AppendLine();
        builder.AppendLine("## 回答與後續探索規則");
        builder.AppendLine("- 先根據上述權威圖回答；重要結論要指出節點、關係或來源檔案。");
        builder.AppendLine("- 若問題要求方法內細節、條件分支、最新未索引修改，或上述資料不足，必須使用唯讀工具 search_project_text、read_project_file_range、find_csharp_symbol 繼續查證。");
        builder.AppendLine("- 「已確認的結構鏈路」每個節點後面都已附上 nodeId；需要繼續往下游追查時，直接用該 nodeId 呼叫 trace_project_graph_paths，不必先呼叫 search_project_graph 重新查一次同一個節點。");
        builder.AppendLine("- 想從任一節點一次取得完整資料流程（Menu/Endpoint/Controller/Service/DAL/資料庫），呼叫 trace_project_graph_paths 時把 backboneOnly 設為 true、maxDepth 提高（最多 8），一次呼叫取代逐層多次呼叫。");
        builder.AppendLine("- 需要繼續沿圖導航時，使用 search_project_graph 取得 nodeId，再用 trace_project_graph_paths；不得自行捏造 nodeId。");
        builder.AppendLine("- 明確區分已確認事實、合理推論與尚未確認項目；資訊不足時不可假裝已完整涵蓋。");
        if (context.WasTruncated)
        {
            builder.AppendLine("- 本次子圖已達節點預算；這不是完整影響清單，請針對高風險節點使用工具繼續展開。");
        }

        return builder.Length <= _options.MaximumPromptCharacters
            ? builder.ToString()
            : builder.ToString(0, _options.MaximumPromptCharacters);
    }

    /// <summary>
    /// 依「是否有精確識別碼觸發主幹深追」動態切分結構鏈路與直接證據兩區塊的字元預算。
    /// 有精確識別碼時，結構鏈路已由主幹深追涵蓋完整資料流程，證據片段比大量鏈路預覽更有回答價值。
    /// 純函式（不讀 _options），方便回歸測試直接驗證配比邏輯。
    /// </summary>
    internal static PromptBudgetPlan ComputeBudgetPlan(
        int remainingCharacters,
        bool hasExactIdentifier,
        double evidenceRatio,
        double evidenceRatioWithExactIdentifier)
    {
        var ratio = hasExactIdentifier ? evidenceRatioWithExactIdentifier : evidenceRatio;
        var evidenceBudget = (int)(remainingCharacters * ratio);
        var structuralBudget = remainingCharacters - evidenceBudget;
        return new PromptBudgetPlan(Math.Max(structuralBudget, 0), Math.Max(evidenceBudget, 0));
    }

    /// <summary>結構鏈路／直接證據兩區塊各自分配到的字元預算上限。</summary>
    internal sealed record PromptBudgetPlan(int StructuralBudget, int EvidenceBudget);

    /// <summary>
    /// 依「跨檔案輪流、同檔案內維持原權重順序」重排證據候選，並剔除幾乎相同的重複描述；
    /// 避免單一鏈路的重複證據把預算洗版、排擠其他來源檔案的證據。
    /// </summary>
    internal static IReadOnlyList<AuthorityGraphRelationship> DiversifyEvidenceOrder(
        IReadOnlyList<AuthorityGraphRelationship> relationships)
    {
        var seenText = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groupOrder = new List<string>();
        var groups = new Dictionary<string, List<AuthorityGraphRelationship>>(StringComparer.OrdinalIgnoreCase);

        foreach (var relationship in relationships)
        {
            var line = EvidenceLine(relationship);
            if (line is null || !seenText.Add(NormalizeEvidenceText(line)))
            {
                continue;
            }

            var fileKey = relationship.Evidence.SourceFile
                ?? relationship.Evidence.DatabaseObject
                ?? relationship.Evidence.XmlPath
                ?? "(no-file)";
            if (!groups.TryGetValue(fileKey, out var bucket))
            {
                bucket = [];
                groups[fileKey] = bucket;
                groupOrder.Add(fileKey);
            }
            bucket.Add(relationship);
        }

        var ordered = new List<AuthorityGraphRelationship>(seenText.Count);
        for (var cursor = 0; ; cursor++)
        {
            var addedAny = false;
            foreach (var fileKey in groupOrder)
            {
                var bucket = groups[fileKey];
                if (cursor < bucket.Count)
                {
                    ordered.Add(bucket[cursor]);
                    addedAny = true;
                }
            }
            if (!addedAny)
            {
                break;
            }
        }
        return ordered;
    }

    /// <summary>把證據文字正規化成去除空白差異的比較用字串，用來判斷是否為幾乎相同的重複證據。</summary>
    private static string NormalizeEvidenceText(string evidenceLine) =>
        Regex.Replace(evidenceLine, @"\s+", " ").Trim().ToLowerInvariant();

    /// <summary>在不超過區塊字元上限的前提下嘗試附加一行內容。</summary>
    private static bool TryAppendWithinBudget(StringBuilder builder, string line, int ceiling)
    {
        if (builder.Length + line.Length + Environment.NewLine.Length > ceiling)
        {
            return false;
        }
        builder.AppendLine(line);
        return true;
    }

    /// <summary>將直接 Evidence 轉成不超過 500 字元的單行說明。</summary>
    private static string? EvidenceLine(AuthorityGraphRelationship relationship)
    {
        var evidence = relationship.Evidence;
        var location = evidence.SourceFile;
        if (evidence.SourceLine is > 0)
        {
            location = $"{location ?? "source"}:{evidence.SourceLine}";
        }
        location ??= evidence.DatabaseObject ?? evidence.XmlPath;
        if (string.IsNullOrWhiteSpace(location) && string.IsNullOrWhiteSpace(evidence.SourceText))
        {
            return null;
        }

        var text = evidence.SourceText?.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (text?.Length > 300)
        {
            text = text[..300] + "…";
        }
        return $"{GraphSchema.GetRelationshipType(relationship.Kind)}；來源 {location ?? evidence.SourceKind.ToString()}" +
               (string.IsNullOrWhiteSpace(text) ? string.Empty : $"；{text}");
    }

    /// <summary>依節點 kind 與常見 FBL 屬性產生對人可讀名稱。</summary>
    internal static string DisplayName(AuthorityGraphNode node)
    {
        foreach (var key in new[] { "name", "description", "path", "action", "menu_id" })
        {
            if (node.Properties.TryGetValue(key, out var value) && value is not null &&
                !string.IsNullOrWhiteSpace(value.ToString()))
            {
                return value.ToString()!;
            }
        }
        return node.Key;
    }

    /// <summary>讓功能入口與使用者明確詢問的資料類型優先成為種子。</summary>
    private static int IntentBoost(string question, AuthorityGraphNode node)
    {
        var boost = node.Kind == AuthorityGraphNodeKind.Menu ? 10 : 0;
        if (question.Contains("報表", StringComparison.OrdinalIgnoreCase) &&
            node.Kind is AuthorityGraphNodeKind.CustomReportTemplate or
                AuthorityGraphNodeKind.CustomReportDataSource or
                AuthorityGraphNodeKind.CustomParameterDataSource)
        {
            boost += 8;
        }
        if (question.Contains("資料", StringComparison.OrdinalIgnoreCase) &&
            node.Kind == AuthorityGraphNodeKind.DatabaseObject)
        {
            boost += 8;
        }
        return boost;
    }

    /// <summary>
    /// 讓 FBL 的主要可執行與資料鏈優先於描述性邊。
    /// 標記為 <c>internal</c> 是刻意的：<see cref="ProjectAnalysisTools"/> 的
    /// <c>trace_project_graph_paths</c>（<c>backboneOnly</c> 模式）需要沿用同一份
    /// 「什麼算主幹關係」定義，避免兩處各自維護一份會逐漸不一致的 switch。
    /// </summary>
    internal static int RelationshipWeight(GraphRelationshipKind kind) => kind switch
    {
        GraphRelationshipKind.Opens or
        GraphRelationshipKind.RoutesTo or
        GraphRelationshipKind.ImplementedBy or
        GraphRelationshipKind.Calls or
        GraphRelationshipKind.Uses or
        GraphRelationshipKind.ReadsVia or
        GraphRelationshipKind.WritesVia or
        GraphRelationshipKind.MapsTo or
        GraphRelationshipKind.ReadsData or
        GraphRelationshipKind.Executes or
        GraphRelationshipKind.Queries or
        GraphRelationshipKind.LoadsPluginReport or
        GraphRelationshipKind.OpensCustomReport => 100,
        GraphRelationshipKind.Renders or
        GraphRelationshipKind.Loads or
        GraphRelationshipKind.DependsOn or
        GraphRelationshipKind.RequiresData or
        GraphRelationshipKind.ConfirmedBy => 80,
        _ => 50,
    };

    /// <summary>Graph 尚未發布時仍保留 Agent 使用原始碼工具回答的能力。</summary>
    private static string BuildMissingGraphPrompt(string question) =>
        $"""
        # FBL 投資系統專案問答
        使用者問題：{question}

        目前沒有可用的 active Graph 版本。請勿捏造圖譜結果；使用唯讀的
        search_project_text、find_csharp_symbol、read_project_file_range 工具探索原始碼，
        並在答案中明確說明本次未使用知識圖譜。
        """;

    /// <summary>Graph 有效但缺少語意種子時，指示 Agent 退回程式碼搜尋而非直接拒答。</summary>
    private static string BuildMissingSeedPrompt(string question, string graphVersion) =>
        $"""
        # FBL 投資系統專案問答
        使用者問題：{question}
        Graph 版本：{graphVersion}

        本次問題未在權威圖找到高信心入口。這不代表專案中沒有相關邏輯。
        請使用唯讀的 search_project_text、find_csharp_symbol、read_project_file_range 工具尋找候選，
        必要時再用 search_project_graph 與 trace_project_graph_paths 交叉驗證；不得捏造節點或關係。
        """;

    /// <summary>驗證檢索上限，避免設定錯誤造成查詢爆炸。</summary>
    private static void ValidateOptions(GraphRetrievalOptions options)
    {
        if (options.SeedLimit is < 1 or > 30 ||
            options.MaximumDepth is < 1 or > 5 ||
            options.NeighborsPerNode is < 1 or > 50 ||
            options.MaximumNodes is < 10 or > 300 ||
            options.MaximumPromptCharacters is < 4_000 or > 80_000 ||
            options.MaximumQueryVariants is < 1 or > 20 ||
            options.LlmQueryRewriteTimeoutSeconds is < 1 or > 30 ||
            options.MaximumLlmQueryVariants is < 1 or > 12 ||
            options.PrimarySeedChaseDepth is < 1 or > 12 ||
            options.PrimarySeedChaseNeighborsPerNode is < 1 or > 30 ||
            options.PrimarySeedChaseMaximumNodes is < 5 or > 200 ||
            options.EvidenceBudgetRatio is <= 0 or >= 1 ||
            options.EvidenceBudgetRatioWithExactIdentifier is <= 0 or >= 1 ||
            options.MaximumEvidenceItems is < 5 or > 100)
        {
            throw new InvalidOperationException("GraphRAG Retrieval 設定超出安全範圍。");
        }
    }

    /// <summary>單一查詢變體的來源與優先順序。</summary>
    internal sealed record RepositorySearchQuery(
        string Text,
        int Priority,
        bool IsLlmGenerated);

    /// <summary>一次問題分析產生的查詢計畫；保留原問題並標記是否包含精確識別碼。</summary>
    internal sealed record RepositoryQueryPlan(
        IReadOnlyList<RepositorySearchQuery> Queries,
        bool HasExactIdentifier);

    /// <summary>LLM Query Rewrite 只允許輸出的 JSON 欄位。</summary>
    private sealed record QueryRewriteResponse(
        string[]? Queries,
        string[]? Terms,
        string[]? Aliases);

    /// <summary>保留查詢來源，供相同 node 合併時以較高優先級候選為準。</summary>
    private sealed record WeightedSearchHit(
        GraphSearchHit Hit,
        RepositorySearchQuery Query);

    /// <summary>FBL Query Plan 的固定優先級；數字與技術名稱必須優先於語意推測。</summary>
    private static class QueryPriority
    {
        public const int ExactIdentifier = 100;
        public const int TechnicalToken = 90;
        public const int OriginalQuestion = 70;
        public const int BusinessTerm = 60;
        public const int Alias = 50;
        public const int LlmRewrite = 30;
    }

    /// <summary>單次問答保留的最小子圖。</summary>
    private sealed record RetrievalSubgraph(
        IReadOnlyList<AuthorityGraphNode> Nodes,
        IReadOnlyList<AuthorityGraphRelationship> Relationships,
        bool WasTruncated);
}
