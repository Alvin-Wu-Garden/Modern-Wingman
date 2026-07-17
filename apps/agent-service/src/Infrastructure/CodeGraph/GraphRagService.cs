using System.Text;
using System.Collections.Concurrent;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using Microsoft.Extensions.Logging;

namespace AgentService.Infrastructure.CodeGraph;

public sealed record AiEnrichmentStatus(
    string ProjectId,
    string? TargetManifestVersion,
    string State,
    int CompletedCommunities,
    int TotalCommunities,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? Message = null,
    string? Error = null);

/// <summary>
/// GraphRAG 查詢與社群摘要服務（WS3.3）。
///
/// 方法論（Microsoft GraphRAG，適配程式碼場景）：
///   索引期：社群偵測（Louvain/namespace fallback）→ 由下而上 LLM 社群摘要
///   查詢期：
///     - Global Search：跨社群摘要 map-reduce，回答全局 know-how 問題
///     - Local Search：full-text 定位實體 → 圖遍歷鄰域 → LLM 彙整，回答特定實體問題
///
/// 檢索不依賴 embeddings（Neo4j full-text BM25 + 圖遍歷），
/// 未來可加 IEmbeddingProvider 增強（介面留白）。
/// </summary>
public sealed class GraphRagService(
    ICodeGraphStore graphStore,
    ILlmCompletionService llm,
    ILogger<GraphRagService> logger)
{
    private const int MaxCommunitySummarySize = 40;   // 每個社群摘要最多送 LLM 的成員數
    private const int MaxCommunitiesPerQuery = 12;    // Global Search 一次最多用的社群數
    private readonly ConcurrentDictionary<string, AiEnrichmentStatus> _status = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _enrichmentGates = new();

    public AiEnrichmentStatus GetEnrichmentStatus(string projectId) =>
        _status.TryGetValue(projectId, out var status)
            ? status
            : new AiEnrichmentStatus(projectId, null, "NotRequested", 0, 0, null, null);

    // ── 索引期：社群摘要 ─────────────────────────────────────────────────────

    /// <summary>
    /// 偵測社群並為每個社群生成 LLM 摘要（全自動，使用者決策）。
    /// </summary>
    public async Task<int> BuildCommunitySummariesAsync(
        string projectId, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var gate = _enrichmentGates.GetOrAdd(projectId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var activeVersion = await graphStore.GetProjectManifestVersionAsync(projectId, ct);
            var existing = GetEnrichmentStatus(projectId);
            if (existing.State == "Ready" &&
                string.Equals(existing.TargetManifestVersion, activeVersion, StringComparison.Ordinal))
                return existing.CompletedCommunities;
            return await BuildCommunitySummariesCoreAsync(projectId, progress, ct);
        }
        catch (OperationCanceledException)
        {
            var current = GetEnrichmentStatus(projectId);
            _status[projectId] = current with
            {
                State = "Canceled",
                CompletedAt = DateTimeOffset.UtcNow,
                Message = "AI Enrichment 已取消；Fast Index 不受影響。",
            };
            throw;
        }
        catch (Exception ex)
        {
            var current = GetEnrichmentStatus(projectId);
            _status[projectId] = current with
            {
                State = "Degraded",
                CompletedAt = DateTimeOffset.UtcNow,
                Error = ex.Message,
                Message = "AI Enrichment 失敗，可安全重試；Fast Index 不受影響。",
            };
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<int> BuildCommunitySummariesCoreAsync(
        string projectId, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var targetManifestVersion = await graphStore.GetProjectManifestVersionAsync(projectId, ct)
            ?? throw new InvalidOperationException("專案尚無可用的 active graph，無法建立社群摘要。");
        var startedAt = DateTimeOffset.UtcNow;
        _status[projectId] = new AiEnrichmentStatus(
            projectId, targetManifestVersion, "Detecting", 0, 0, startedAt, null,
            "偵測程式碼社群...");
        progress?.Report("偵測程式碼社群...");
        var communities = await graphStore.DetectCommunitiesAsync(projectId, ct);

        var groups = communities
            .GroupBy(kv => kv.Value)
            .Where(g => g.Count() >= 2) // 單節點社群不值得摘要
            .OrderByDescending(g => g.Count())
            .Take(50)                    // 成本上限
            .ToList();

        logger.LogInformation("偵測到 {Count} 個社群（≥2 成員）", groups.Count);
        _status[projectId] = new AiEnrichmentStatus(
            projectId, targetManifestVersion, "Summarizing", 0, groups.Count,
            startedAt, null, "建立社群摘要...");

        var built = 0;
        var failures = 0;
        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            var activeManifest = await graphStore.GetProjectManifestVersionAsync(projectId, ct);
            if (!string.Equals(activeManifest, targetManifestVersion, StringComparison.Ordinal))
            {
                _status[projectId] = new AiEnrichmentStatus(
                    projectId, targetManifestVersion, "Superseded", built, groups.Count,
                    startedAt, DateTimeOffset.UtcNow,
                    "索引版本已更新，舊版本摘要工作已停止。");
                return built;
            }
            var communityId = group.Key;
            var memberKeys = group.Select(kv => kv.Key).ToList();

            progress?.Report($"生成社群摘要 {built + 1}/{groups.Count}...");

            // 取樣成員細節（過大社群截斷）
            var sample = memberKeys.Take(MaxCommunitySummarySize).ToList();
            var details = new StringBuilder();
            foreach (var key in sample)
            {
                var hood = await graphStore.GetNeighborhoodAsync(projectId, key, 1, ct);
                if (hood.Center is { } c)
                {
                    details.AppendLine($"- [{c.Kind}] {c.Signature ?? c.Name}" +
                        (c.FilePath is not null ? $"（{c.FilePath}）" : ""));
                }
            }

            var prompt = $"""
                你是資深軟體架構師。以下是一個程式碼模組（社群）的成員清單，
                請用繁體中文寫出：
                1. 第一行：此模組的簡短標題（10 字內）
                2. 之後：2-4 句摘要，說明此模組的職責、關鍵類別與其協作方式。

                成員清單：
                {details}

                只輸出標題與摘要，不要其他前後綴。
                """;

            try
            {
                var response = await llm.CompleteAsync(
                    prompt,
                    new LlmTelemetryContext(
                        FeatureArea: "project_community_summary",
                        ProjectId: projectId,
                        MetadataJson: $$"""{"communityId":"{{communityId}}"}"""),
                    ct);
                var lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var title = lines.Length > 0 ? lines[0].Trim().TrimStart('#', ' ') : communityId;
                var summary = lines.Length > 1 ? string.Join("\n", lines[1..]).Trim() : response;

                await graphStore.SaveCommunitySummaryAsync(
                    projectId, targetManifestVersion, communityId, title, summary, memberKeys, ct);
                built++;
                _status[projectId] = new AiEnrichmentStatus(
                    projectId, targetManifestVersion, "Summarizing", built, groups.Count,
                    startedAt, null, $"已完成 {built}/{groups.Count} 個社群摘要");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures++;
                logger.LogWarning(ex, "社群 {CommunityId} 摘要生成失敗，跳過", communityId);
            }
        }

        progress?.Report($"社群摘要完成（{built} 個）");
        _status[projectId] = new AiEnrichmentStatus(
            projectId,
            targetManifestVersion,
            failures == 0 ? "Ready" : "Degraded",
            built,
            groups.Count,
            startedAt,
            DateTimeOffset.UtcNow,
            failures == 0 ? $"社群摘要完成（{built} 個）" : $"完成 {built} 個，{failures} 個失敗。",
            failures == 0 ? null : "部分社群摘要生成失敗，可安全重試；Fast Index 不受影響。");
        return built;
    }

    // ── 查詢期 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Global Search：全局 know-how 問題（"這個系統的訂單流程怎麼運作？"）。
    /// map：各社群摘要對問題的相關回答 → reduce：彙整最終答案。
    /// </summary>
    public async Task<string> GlobalSearchAsync(
        string projectId,
        string question,
        CancellationToken ct = default,
        string? providerProfileId = null,
        string? modelId = null)
    {
        var summaries = await graphStore.ListCommunitySummariesAsync(projectId, ct);
        if (summaries.Count == 0)
        {
            // 無社群摘要 → 降級為 Local Search
            logger.LogInformation("無社群摘要，Global Search 降級為 Local Search");
            return await LocalSearchAsync(projectId, question, ct, providerProfileId, modelId);
        }

        var context = new StringBuilder();
        foreach (var s in summaries.Take(MaxCommunitiesPerQuery))
        {
            context.AppendLine($"## {s.Title}");
            context.AppendLine(s.Summary);
            context.AppendLine();
        }

        var prompt = $"""
            你是熟悉此程式碼庫的資深工程師。以下是程式碼庫各模組的摘要，
            請根據這些資訊用繁體中文回答問題。若資訊不足請明說，不要編造。

            # 模組摘要
            {context}

            # 問題
            {question}
            """;

        return await CompleteWithSelectionAsync(prompt, ct, providerProfileId, modelId, projectId, "project_qa_global");
    }

    /// <summary>
    /// Local Search：特定實體問題（"OrderService.CalculateTotal 在哪裡被用到？"）。
    /// full-text 定位 → 鄰域展開 → LLM 彙整（附程式碼位置引用）。
    /// </summary>
    public async Task<string> LocalSearchAsync(
        string projectId,
        string question,
        CancellationToken ct = default,
        string? providerProfileId = null,
        string? modelId = null)
    {
        // 1. full-text 找相關節點
        var hits = await graphStore.SearchAsync(projectId, question, 8, ct);
        if (hits.Count == 0)
            return "找不到與問題相關的程式碼實體。請嘗試使用類別或方法名稱提問。";

        // 2. 展開前 3 個節點的鄰域
        var context = new StringBuilder();
        foreach (var hit in hits.Take(3))
        {
            var hood = await graphStore.GetNeighborhoodAsync(projectId, hit.Key, 1, ct);
            context.AppendLine($"## {hit.Signature ?? hit.Name}");
            context.AppendLine($"種類: {hit.Kind}，位置: {hit.FilePath}:{hit.StartLine}");
            if (hood.Neighbors.Count > 0)
            {
                context.AppendLine("關係:");
                foreach (var n in hood.Neighbors.Take(20))
                {
                    var arrow = n.Direction == "out" ? "→" : "←";
                    context.AppendLine($"  {arrow} [{n.RelationKind}] {n.Name}（{n.FilePath ?? "?"}）");
                }
            }
            context.AppendLine();
        }

        // 其餘 hits 只列基本資訊
        if (hits.Count > 3)
        {
            context.AppendLine("## 其他相關實體");
            foreach (var hit in hits.Skip(3))
                context.AppendLine($"- [{hit.Kind}] {hit.Name}（{hit.FilePath}:{hit.StartLine}）");
        }

        var prompt = $"""
            你是熟悉此程式碼庫的資深工程師。以下是與問題相關的程式碼實體及其關係圖，
            請用繁體中文回答問題，回答中引用具體的檔案路徑與行號。若資訊不足請明說。

            # 相關程式碼實體
            {context}

            # 問題
            {question}
            """;

        return await CompleteWithSelectionAsync(prompt, ct, providerProfileId, modelId, projectId, "project_qa_local");
    }

    /// <summary>
    /// 自動路由：判斷問題是全局型或實體型。
    /// 啟發式：包含具體識別字（駝峰/點號）→ Local；否則 Global。
    /// </summary>
    public Task<string> QueryAsync(
        string projectId,
        string question,
        CancellationToken ct = default,
        string? providerProfileId = null,
        string? modelId = null)
    {
        var looksLocal = question.Split(' ', '，', '。', '？', '?').Any(token =>
            token.Length > 2 &&
            (token.Contains('.') || token.Contains("::") ||
             (char.IsUpper(token[0]) && token.Skip(1).Any(char.IsLower) && token.Any(char.IsUpper))));

        return looksLocal
            ? LocalSearchAsync(projectId, question, ct, providerProfileId, modelId)
            : GlobalSearchAsync(projectId, question, ct, providerProfileId, modelId);
    }

    private Task<string> CompleteWithSelectionAsync(
        string prompt,
        CancellationToken ct,
        string? providerProfileId,
        string? modelId,
        string projectId,
        string featureArea) =>
        string.IsNullOrWhiteSpace(providerProfileId) && string.IsNullOrWhiteSpace(modelId)
            ? llm.CompleteAsync(
                prompt,
                new LlmTelemetryContext(FeatureArea: featureArea, ProjectId: projectId),
                ct)
            : llm.CompleteAsync(
                prompt,
                providerProfileId,
                modelId,
                new LlmTelemetryContext(FeatureArea: featureArea, ProjectId: projectId),
                ct);
}
