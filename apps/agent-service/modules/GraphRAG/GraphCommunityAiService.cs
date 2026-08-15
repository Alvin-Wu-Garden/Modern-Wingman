using AgentService.Application.Contracts;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AgentService.Modules.GraphRAG;

/// <summary>
/// 將 C0 預熱與 C1/C2 首次命中轉成低優先背景工作。
/// 本服務不參與 canonical publish；任何模型或 CAS 失敗都保留 deterministic template。
/// </summary>
public sealed class GraphCommunityAiService(
    IGraphStore store,
    IGraphCommunitySummaryQueue queue,
    ILlmCompletionService llm,
    IModelProviderService providers,
    ILogger<GraphCommunityAiService> logger)
{
    private const int MaximumMemberIdsInPrompt = 80;
    private const int MaximumPrewarmedCommunities = 3;

    /// <summary>取得 Progress API 要回傳的目前版本統計。</summary>
    public GraphCommunitySummaryProgress GetProgress(string projectId) =>
        queue.GetProgress(projectId);

    /// <summary>專案刪除或 Host 忘記專案狀態時，一併釋放 Queue 的版本與 gate 狀態。</summary>
    public void ForgetProject(string projectId) => queue.ForgetProject(projectId);

    /// <summary>
    /// 在 Graph publish 完成後立即標記結構可用，並只預熱尚未 ai-ready 的 C0。
    /// 本方法只排程、不等待模型，因此 AI call 數不計入 publish critical path。
    /// </summary>
    public async Task PrewarmC0Async(
        string projectId,
        string graphVersion,
        CancellationToken cancellationToken = default)
    {
        queue.ActivateGraphVersion(projectId, graphVersion, true);
        var reports = await store.ListCommunityTemplatesAsync(projectId, cancellationToken);
        foreach (var report in SelectPrewarmCandidates(reports))
            await TryEnqueueSafelyAsync(projectId, graphVersion, report, cancellationToken);
    }

    /// <summary>
    /// Local/Global Search 命中 C1 或 C2 時排程 template；回答直接使用現有 template，
    /// 不等待背景工作，避免使用者問題被 Community AI 延遲。
    /// </summary>
    public async Task EnqueueOnHitAsync(
        string projectId,
        string graphVersion,
        IEnumerable<GraphCommunityReportV4> reports,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reports);
        queue.ActivateGraphVersion(projectId, graphVersion, true);
        foreach (var report in reports.Where(report =>
                     report.Tier is "C1" or "C2"))
            await TryEnqueueSafelyAsync(projectId, graphVersion, report, cancellationToken);
    }

    /// <summary>
    /// Host 重啟後重新排入 persisted queued/running 與尚未執行的 C0；
    /// ai-ready cacheKey 不重跑，failed 只在 retryCount 未達二時重試。
    /// </summary>
    public async Task ResumeAsync(
        string projectId,
        string graphVersion,
        CancellationToken cancellationToken = default)
    {
        var reports = await store.ListCommunityTemplatesAsync(projectId, cancellationToken);
        queue.RestoreGraphVersion(projectId, graphVersion, true, reports);
        foreach (var report in SelectPrewarmCandidates(reports))
            await TryEnqueueSafelyAsync(projectId, graphVersion, report, cancellationToken);
    }

    /// <summary>
    /// 每版只預熱節點數最多的三個 C0。先前已排程或可重試者也必須位於這三個之內，
    /// 避免 Host 重啟後把所有 deterministic community 一次塞入 AI 佇列。
    /// </summary>
    internal static IReadOnlyList<GraphCommunityReportV4> SelectPrewarmCandidates(
        IEnumerable<GraphCommunityReportV4> reports) =>
        reports
            .Where(report => string.Equals(report.Tier, "C0", StringComparison.Ordinal))
            .OrderByDescending(report => report.MemberCount)
            .ThenBy(report => report.CommunityId, StringComparer.Ordinal)
            .Take(MaximumPrewarmedCommunities)
            .Where(report =>
                report.SummaryState != GraphCommunitySummaryStates.AiReady &&
                !(report.SummaryState == GraphCommunitySummaryStates.Failed && report.RetryCount >= 2))
            .ToArray();

    /// <summary>
    /// 將單一 Community 的 CAS 或 bounded queue 失敗隔離在背景摘要層。
    /// 呼叫端取消仍照常向上傳遞；基礎結構、查詢與已發布 graph 不受 AI 失敗影響。
    /// </summary>
    private async Task TryEnqueueSafelyAsync(
        string projectId,
        string graphVersion,
        GraphCommunityReportV4 report,
        CancellationToken cancellationToken)
    {
        try
        {
            await EnqueueIfNeededAsync(
                projectId,
                graphVersion,
                report,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Community AI 排程失敗，保留確定性模板。ProjectId={ProjectId}, CommunityId={CommunityId}",
                projectId,
                report.CommunityId);
        }
    }

    /// <summary>檢查 cache/retry state，以 Neo4j CAS 標成 queued 後才交給 bounded queue。</summary>
    private async Task EnqueueIfNeededAsync(
        string projectId,
        string graphVersion,
        GraphCommunityReportV4 report,
        CancellationToken cancellationToken)
    {
        if (report.SummaryState == GraphCommunitySummaryStates.AiReady ||
            (report.SummaryState == GraphCommunitySummaryStates.Failed &&
             report.RetryCount >= 2))
            return;

        // failed 表示上一個 execution 已消耗目前 retry ordinal；
        // queued/running 則表示該 ordinal 尚未可靠完成，重啟後重做同一 ordinal。
        var initialRetryCount =
            report.SummaryState == GraphCommunitySummaryStates.Failed
                ? Math.Min(2, report.RetryCount + 1)
                : Math.Clamp(report.RetryCount, 0, 2);
        var queued = await store.TryUpdateCommunitySummaryAsync(
            projectId,
            graphVersion,
            report.CommunityId,
            report.CacheKey,
            report.Summary,
            GraphCommunitySummaryStates.Queued,
            report.RetryCount,
            cancellationToken);
        if (!queued)
            return;

        var executionCount = 0;
        var enqueued = queue.TryEnqueue(new GraphCommunitySummaryJob(
            projectId,
            graphVersion,
            report.CommunityId,
            report.CacheKey,
            async token =>
            {
                var attempt = Interlocked.Increment(ref executionCount);
                var executionRetryCount = Math.Clamp(
                    initialRetryCount + attempt - 1,
                    0,
                    2);
                await store.TryUpdateCommunitySummaryAsync(
                    projectId,
                    graphVersion,
                    report.CommunityId,
                    report.CacheKey,
                    report.Summary,
                    GraphCommunitySummaryStates.Running,
                    executionRetryCount,
                    token);
                var profile = await providers.GetProfileAsync(null, token);
                var response = await llm.CompleteAsync(
                    BuildPrompt(report),
                    profile.Id,
                    profile.ModelId,
                    token);
                var generated = ParseGeneratedText(response);
                var updated = await store.TryUpdateCommunityTextAsync(
                    projectId,
                    graphVersion,
                    report.CommunityId,
                    report.CacheKey,
                    generated.Title,
                    generated.Summary,
                    GraphCommunitySummaryStates.AiReady,
                    executionRetryCount,
                    token);
                if (!updated)
                    logger.LogInformation(
                        "Community AI 結果已過期，未覆寫新版本。ProjectId={ProjectId}, CommunityId={CommunityId}",
                        projectId,
                        report.CommunityId);
            },
            initialRetryCount,
            async (retryCount, token) =>
            {
                await store.TryUpdateCommunitySummaryAsync(
                    projectId,
                    graphVersion,
                    report.CommunityId,
                    report.CacheKey,
                    report.Summary,
                    GraphCommunitySummaryStates.Failed,
                    retryCount,
                    token);
            }));
        if (!enqueued)
        {
            // CAS 已先標成 queued；若有界佇列拒收，必須還原 template，否則 UI 會
            // 永久顯示背景工作中，而 Host 重啟也會把不存在的工作誤判為待恢復。
            await store.TryUpdateCommunitySummaryAsync(
                projectId,
                graphVersion,
                report.CommunityId,
                report.CacheKey,
                report.Summary,
                GraphCommunitySummaryStates.Template,
                report.RetryCount,
                cancellationToken);
        }
    }

    /// <summary>建立只允許改寫既有事實的有界繁體中文摘要 Prompt。</summary>
    private static string BuildPrompt(GraphCommunityReportV4 report) =>
        $"""
        你是熟悉大型投資交易與風控系統的資深架構師。
        請只根據下列 deterministic Graph template，產生繁體中文標題與 2 到 4 句摘要。
        說明功能入口、主要程式責任與資料依賴；不得新增未提供的類別、資料表或流程。

        Tier：{report.Tier}
        社群：{report.Title}
        原摘要：{report.Summary}
        Top tables：{string.Join(", ", report.TopTables)}
        Top entry points：{string.Join(", ", report.TopEntryPoints)}
        成員 ID：
        {string.Join('\n', report.MemberIds.Take(MaximumMemberIdsInPrompt))}

        只輸出具有 title 與 summary 兩個字串欄位的 JSON，不要 Markdown 或前後綴。
        """;

    private static GraphCommunityGeneratedText ParseGeneratedText(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            throw new InvalidOperationException("模型回傳空白 Community 標題與摘要。");
        var text = response.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLine = text.IndexOf('\n');
            var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLine >= 0 && lastFence > firstLine)
                text = text[(firstLine + 1)..lastFence].Trim();
        }
        var value = JsonSerializer.Deserialize<GraphCommunityGeneratedText>(
            text,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (value is null || string.IsNullOrWhiteSpace(value.Title) ||
            string.IsNullOrWhiteSpace(value.Summary))
            throw new InvalidOperationException("模型未回傳有效的 Community 標題與摘要 JSON。");
        return new GraphCommunityGeneratedText(value.Title.Trim(), value.Summary.Trim());
    }
}
