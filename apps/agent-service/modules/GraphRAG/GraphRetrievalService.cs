using System.Text.RegularExpressions;
using System.Collections.Concurrent;
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

/// <summary>V3 影響分析結果，直接以 type/file/data 修改單位呈現。</summary>
public sealed record GraphImpactResult(
    ScoredGraphNode? Target,
    IReadOnlyList<ScoredGraphNode> AffectedNodes,
    IReadOnlyList<GraphEdge> Relationships,
    IReadOnlyList<string> AffectedFiles,
    IReadOnlyList<string> SuggestedTestFilters,
    bool Truncated);

/// <summary>
/// 使用 Neo4j BM25 種子加 relation-aware BFS 建立修改範圍上下文。
/// 本服務不呼叫 LLM 來猜 canonical edge；LLM 只閱讀已抽取的 node、edge、evidence 與社群摘要。
/// </summary>
public sealed partial class GraphRetrievalService
{
    private readonly IGraphStore _store;
    private readonly GraphRetrievalOptions _options;
    private readonly ILogger<GraphRetrievalService> _logger;
    private readonly ILlmCompletionService? _llm;
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
        var llm = RequiredLlm();
        var gate = _enrichmentGates.GetOrAdd(
            projectId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var manifest = await _store.GetActiveManifestAsync(
                projectId, cancellationToken) ??
                throw new InvalidOperationException(
                    "專案尚無 active V3 graph，無法建立社群摘要。");
            var reports = await _store.ListCommunityReportsAsync(
                projectId, cancellationToken);
            _enrichment[projectId] = new GraphAiEnrichmentStatus(
                projectId, manifest, "Summarizing", 0, reports.Count,
                DateTimeOffset.UtcNow, null, "正在建立業務社群摘要。");
            var enriched = new List<GraphCommunityReport>(reports.Count);
            var failures = 0;
            for (var index = 0; index < reports.Count; index++)
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
                    return enriched.Count;
                }
                var report = reports[index];
                progress?.Report($"生成社群摘要 {index + 1}/{reports.Count}...");
                if (report.AiEnriched &&
                    !string.IsNullOrWhiteSpace(report.CacheKey))
                {
                    // 相同 member evidence 與 prompt version 已有摘要時直接沿用；
                    // canonical graph 不因 AI cache hit 產生任何變更。
                    enriched.Add(report);
                    _enrichment[projectId] = GetEnrichmentStatus(projectId) with
                    {
                        CompletedCommunities = index + 1,
                        Message = $"已完成 {index + 1}/{reports.Count} 個社群摘要。",
                    };
                    continue;
                }
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
                    var summary = await llm.CompleteAsync(
                        prompt,
                        new LlmTelemetryContext(
                            FeatureArea: "project_community_summary_v3",
                            ProjectId: projectId),
                        cancellationToken);
                    enriched.Add(report with
                    {
                        Summary = summary.Trim(),
                        AiEnriched = true,
                    });
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    failures++;
                    enriched.Add(report);
                }
                _enrichment[projectId] = GetEnrichmentStatus(projectId) with
                {
                    CompletedCommunities = index + 1,
                    Message = $"已完成 {index + 1}/{reports.Count} 個社群摘要。",
                };
            }
            await _store.SaveCommunityReportsAsync(
                projectId, manifest, enriched, cancellationToken);
            _enrichment[projectId] = GetEnrichmentStatus(projectId) with
            {
                State = failures == 0 ? "Ready" : "Degraded",
                CompletedAt = DateTimeOffset.UtcNow,
                Message = failures == 0
                    ? $"社群摘要完成（{enriched.Count} 個）。"
                    : $"完成 {enriched.Count} 個，其中 {failures} 個保留 deterministic 摘要。",
            };
            return enriched.Count;
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
        finally
        {
            gate.Release();
        }
    }

    /// <summary>用 Local Search context 產生有檔案與 evidence 引用的繁體中文答案。</summary>
    public async Task<string> AnswerLocalAsync(
        string projectId,
        string question,
        CancellationToken cancellationToken = default,
        string? providerProfileId = null,
        string? modelId = null)
    {
        var context = await LocalSearchAsync(
            projectId, question, cancellationToken);
        if (context.Nodes.Count == 0)
            return context.Diagnostics.FirstOrDefault() ??
                   "找不到與問題相關的可靠圖譜實體。";
        var prompt = $"""
            你是熟悉此企業程式碼庫的資深工程師。請只根據下列 GraphRAG V3 context，
            用繁體中文回答使用者問題，指出可能要修改的 Feature、EntryPoint、Code、Data，
            並引用 filePath、line 與 evidence。無法由 context 證明的內容必須標示未知，不得編造。

            # Graph context
            {FormatContext(context)}

            # 使用者問題
            {question}
            """;
        return await CompleteAsync(
            prompt, projectId, "project_qa_local_v3",
            providerProfileId, modelId, cancellationToken);
    }

    /// <summary>
    /// 以 primary/secondary reports 做 bounded map-reduce 回答跨功能總覽；
    /// 無摘要時安全退回 Local Search。Map 階段分批萃取與問題相關的已知事實，
    /// Reduce 階段才整合跨社群結論，避免把 20 份原始報告一次塞入 prompt。
    /// </summary>
    public async Task<string> AnswerGlobalAsync(
        string projectId,
        string question,
        CancellationToken cancellationToken = default,
        string? providerProfileId = null,
        string? modelId = null)
    {
        var reports = await GlobalSearchAsync(
            projectId, question, 20, cancellationToken);
        if (reports.Count == 0)
            return await AnswerLocalAsync(
                projectId, question, cancellationToken,
                providerProfileId, modelId);
        var batches = reports
            .Select((report, index) => new { report, index })
            .GroupBy(item => item.index / 4)
            .Select(group => group.Select(item => item.report).ToList())
            .ToList();
        var mapped = await Task.WhenAll(batches.Select(async (batch, index) =>
        {
            var mapPrompt = $"""
                你是 GraphRAG Global Search 的 map worker。請只根據下列社群報告，
                萃取與使用者問題直接相關的跨功能事實，使用繁體中文條列；每點標示 communityId。
                若本批沒有相關證據，輸出「本批無直接證據」。不得補造類別、資料表或流程，
                也不得把 community 摘要當成精確程式碼行號。

                # 使用者問題
                {question}

                # 社群報告批次 {index + 1}
                {string.Join("\n\n", batch.Select(report =>
                    $"communityId={report.CommunityId}\n" +
                    $"title={report.Title}\nkind={report.Kind}\nsummary={report.Summary}"))}
                """;
            return await CompleteAsync(
                mapPrompt,
                projectId,
                "project_qa_global_map_v3",
                providerProfileId,
                modelId,
                cancellationToken);
        }));
        var prompt = $"""
            你是 GraphRAG Global Search 的 reduce worker。請只整合下列 map 結果，
            用繁體中文回答跨模組問題，保留 communityId 引用並區分已知事實與未知資訊。
            不得新增 map 結果沒有提供的類別、資料表或流程；若需要精確檔案與行號，
            請明說應再執行 Local Search。

            # 使用者問題
            {question}

            # Map 結果
            {string.Join("\n\n", mapped.Select((value, index) =>
                $"## Map {index + 1}\n{value}"))}
            """;
        return await CompleteAsync(
            prompt, projectId, "project_qa_global_reduce_v3",
            providerProfileId, modelId, cancellationToken);
    }

    /// <summary>依具體識別字與需求語氣自動選擇 Local 或 Global answer。</summary>
    public Task<string> AnswerAsync(
        string projectId,
        string question,
        CancellationToken cancellationToken = default,
        string? providerProfileId = null,
        string? modelId = null)
    {
        return LooksLikeLocalQuestion(question)
            ? AnswerLocalAsync(
                projectId, question, cancellationToken,
                providerProfileId, modelId)
            : AnswerGlobalAsync(
                projectId, question, cancellationToken,
                providerProfileId, modelId);
    }

    internal static bool LooksLikeLocalQuestion(string question)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        string[] changeOrDefectSignals =
        [
            "bug", "error", "exception", "錯誤", "異常", "失敗", "無法",
            "沒有", "未能", "修正", "修改", "調整", "新增", "實作", "需求",
        ];
        if (changeOrDefectSignals.Any(signal =>
                question.Contains(signal, StringComparison.OrdinalIgnoreCase)))
            return true;

        return question.Split(' ', '，', '。', '？', '?', '：', ':')
            .Any(token =>
                token.Contains('.') ||
                token.Contains('/') ||
                token.Contains('\\') ||
                token.StartsWith("tbl", StringComparison.OrdinalIgnoreCase) ||
                token.EndsWith("Controller", StringComparison.OrdinalIgnoreCase) ||
                token.EndsWith("Service", StringComparison.OrdinalIgnoreCase) ||
                token.EndsWith("Repository", StringComparison.OrdinalIgnoreCase));
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

    /// <summary>以 Local Search 的雙向關係計算 type/file/data 層級影響範圍。</summary>
    public async Task<GraphImpactResult> AnalyzeImpactAsync(
        string projectId,
        string symbolQuery,
        int maximumDepth = 3,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolQuery);
        var originalDepth = _options.MaximumDepth;
        maximumDepth = Math.Clamp(maximumDepth, 1, originalDepth);
        var context = await LocalSearchAsync(
            projectId, symbolQuery, cancellationToken);
        var target = context.Nodes.FirstOrDefault(node => node.Seed);
        var affected = context.Nodes
            .Where(node => target is null || node.Node.Id != target.Node.Id)
            .ToList();
        var files = affected.Select(node => node.Node.FilePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var filters = affected
            .Where(node => node.Node.Kind == GraphNodeKind.Code)
            .Select(node => node.Node.Name)
            .Distinct(StringComparer.Ordinal)
            .Take(10)
            .Select(name => $"dotnet test --filter \"FullyQualifiedName~{name}\"")
            .ToList();
        return new GraphImpactResult(
            target,
            affected,
            context.Edges,
            files,
            filters,
            context.Diagnostics.Count > 0);
    }

    /// <summary>以工具鏈偵測與 V3 community reports 生成 AGENTS.md 並寫入專案根目錄。</summary>
    public async Task<string> GenerateAgentsMdAsync(
        string projectId,
        string projectRoot,
        CancellationToken cancellationToken = default)
    {
        var facts = DetectProjectFacts(projectRoot);
        var reports = await _store.ListCommunityReportsAsync(
            projectId, cancellationToken);
        var prompt = $"""
            請為以下專案撰寫精簡的 AGENTS.md，使用繁體中文，最多 60 行。
            只保留 AI coding agent 無法直接推斷的建置、測試、架構與慣例；
            GraphRAG 社群內容不可擴寫成不存在的規則。

            # 工具鏈事實
            {facts}

            # 業務社群
            {string.Join('\n', reports.Take(12).Select(report =>
                $"- {report.Title}: {report.Summary}"))}

            只輸出 Markdown 內容。
            """;
        var content = await CompleteAsync(
            prompt, projectId, "agents_md_generation_v3",
            null, null, cancellationToken);
        var path = Path.GetFullPath(Path.Combine(projectRoot, "AGENTS.md"));
        if (!Path.GetRelativePath(Path.GetFullPath(projectRoot), path)
                .Equals("AGENTS.md", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("AGENTS.md 目標路徑不在專案根目錄。");
        await File.WriteAllTextAsync(path, content, cancellationToken);
        return content;
    }

    private ILlmCompletionService RequiredLlm() =>
        _llm ?? throw new InvalidOperationException(
            "GraphRAG AI enrichment／answer 尚未註冊 ILlmCompletionService。");

    private Task<string> CompleteAsync(
        string prompt,
        string projectId,
        string featureArea,
        string? providerProfileId,
        string? modelId,
        CancellationToken cancellationToken)
    {
        var llm = RequiredLlm();
        var telemetry = new LlmTelemetryContext(
            FeatureArea: featureArea,
            ProjectId: projectId);
        return string.IsNullOrWhiteSpace(providerProfileId) &&
               string.IsNullOrWhiteSpace(modelId)
            ? llm.CompleteAsync(prompt, telemetry, cancellationToken)
            : llm.CompleteAsync(
                prompt,
                providerProfileId,
                modelId,
                telemetry,
                cancellationToken);
    }

    private static string FormatContext(GraphRetrievalContext context)
    {
        var builder = new StringBuilder();
        builder.AppendLine("## Nodes");
        foreach (var item in context.Nodes)
        {
            var node = item.Node;
            builder.Append("- [").Append(node.Kind).Append('/').Append(node.Role)
                .Append("] ").Append(node.Name)
                .Append(" id=").Append(node.Id)
                .Append(" score=").Append(item.Score.ToString("0.000"));
            if (node.FilePath is not null)
                builder.Append(" location=").Append(node.FilePath)
                    .Append(':').Append(node.StartLine?.ToString() ?? "?");
            builder.AppendLine();
            foreach (var evidence in node.Evidence.Take(3))
                builder.Append("  evidence: ").Append(evidence.Reason)
                    .Append(" [").Append(evidence.Artifact)
                    .Append(':').Append(evidence.StartLine?.ToString() ?? "?")
                    .AppendLine("]");
        }
        builder.AppendLine("## Relationships");
        foreach (var edge in context.Edges)
            builder.Append("- ").Append(edge.SourceId).Append(" --")
                .Append(edge.Kind.ToString().ToUpperInvariant())
                .Append("--> ").AppendLine(edge.TargetId);
        if (context.Communities.Count > 0)
        {
            builder.AppendLine("## Communities");
            foreach (var report in context.Communities)
                builder.Append("- ").Append(report.Title).Append(": ")
                    .AppendLine(report.Summary);
        }
        return builder.ToString();
    }

    private static string DetectProjectFacts(string root)
    {
        var builder = new StringBuilder();
        AddTopLevel("*.sln", "含 .NET solution（dotnet build / dotnet test）");
        AddRecursive("*.csproj", "含 C# 專案（dotnet build）");
        AddTopLevel("pom.xml", "Maven 專案（mvn compile / mvn test）");
        AddTopLevel("build.gradle", "Gradle 專案（gradle build / gradle test）");
        AddTopLevel("build.gradle.kts", "Gradle Kotlin DSL 專案");
        AddTopLevel("package.json", "Node.js 專案（依 package.json scripts 執行）");
        AddTopLevel("pnpm-workspace.yaml", "pnpm monorepo");
        AddTopLevel("Dockerfile", "含 Dockerfile");
        AddTopLevel(".editorconfig", "有 .editorconfig 編碼慣例");
        if (Directory.Exists(Path.Combine(root, ".github", "workflows")))
            builder.AppendLine("- 有 GitHub Actions CI");
        return builder.Length == 0 ? "（未偵測到已知建置系統）" : builder.ToString();

        void AddTopLevel(string pattern, string fact)
        {
            try
            {
                if (Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly).Any())
                    builder.AppendLine($"- {fact}");
            }
            catch
            {
            }
        }

        void AddRecursive(string pattern, string fact)
        {
            try
            {
                if (Directory.EnumerateFiles(root, pattern, new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        MaxRecursionDepth = 3,
                        IgnoreInaccessible = true,
                    }).Any())
                    builder.AppendLine($"- {fact}");
            }
            catch
            {
            }
        }
    }

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
        var luceneQuery = BuildLuceneQuery(query);
        if (luceneQuery.Length == 0)
            return new GraphRetrievalContext(
                query, [], [], [],
                ["問題沒有可搜尋的文字或識別碼，未執行圖譜遍歷。"]);

        var hits = await SearchSeedHitsAsync(
            projectId, query, luceneQuery, cancellationToken);
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
        var intentKinds = IntentEdgeKinds(query);
        if (intentKinds.Count > 0)
        {
            var seedStates = selected.Values
                .Where(item => item.Seed)
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Node.Id, StringComparer.Ordinal)
                .ToList();
            var directSets = await Task.WhenAll(seedStates.Select(async seed => new
            {
                Seed = seed,
                Neighbors = await _store.GetNeighborsAsync(
                    projectId,
                    seed.Node.Id,
                    _options.NeighborsPerNode,
                    cancellationToken),
            }));
            var directIntentNeighbors = directSets
                .SelectMany(set => set.Neighbors
                    .Where(neighbor => intentKinds.Contains(neighbor.Edge.Kind))
                    .Select(neighbor => new
                    {
                        set.Seed,
                        Neighbor = neighbor,
                        Score = set.Seed.Score *
                                EdgeWeight(neighbor.Edge.Kind) *
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
        }

        var expandedAtScore = new Dictionary<string, double>(StringComparer.Ordinal);
        while (frontier.Count > 0 && selected.Count < _options.MaximumNodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = frontier.Dequeue();
            if (state.Depth >= _options.MaximumDepth) continue;
            if (expandedAtScore.TryGetValue(state.NodeId, out var prior) &&
                prior >= state.Score)
                continue;
            expandedAtScore[state.NodeId] = state.Score;

            var neighbors = await _store.GetNeighborsAsync(
                projectId,
                state.NodeId,
                _options.NeighborsPerNode,
                cancellationToken);
            foreach (var neighbor in neighbors)
            {
                var relationScore = EdgeWeight(neighbor.Edge.Kind);
                var nextScore = state.Score * relationScore * _options.HopDecay;
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
