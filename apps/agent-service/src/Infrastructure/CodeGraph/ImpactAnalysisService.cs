using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;
using Microsoft.Extensions.Logging;

namespace AgentService.Infrastructure.CodeGraph;

/// <summary>Impact Analysis 結果（前端視覺化用，P2）。</summary>
public sealed record ImpactAnalysisResult(
    GraphSearchHit? Target,
    IReadOnlyList<ImpactPath> CallChains,
    IReadOnlyList<GraphSearchHit> AffectedMethods,
    IReadOnlyList<string> AffectedFiles,
    IReadOnlyList<string> SuggestedTestFilters,
    string? ManifestVersion = null,
    IndexFreshness Freshness = IndexFreshness.Unknown,
    IReadOnlyList<GraphNeighborNode>? RelatedEvidence = null,
    bool Truncated = false);

/// <summary>
/// 「不改 A 壞 B」影響分析（WS3.5）。
///
/// 修改前：以 Neo4j 反向呼叫鏈找出所有（遞移）呼叫者
/// → 受影響方法/檔案清單 + 建議測試過濾條件（dotnet test --filter / mvn -Dtest）。
/// </summary>
public sealed class ImpactAnalysisService(
    ICodeGraphStore graphStore,
    IProjectRepository projects,
    ILogger<ImpactAnalysisService> logger)
{
    /// <summary>
    /// 分析修改 <paramref name="symbolQuery"/>（方法/類別名稱）的影響範圍。
    /// </summary>
    public async Task<ImpactAnalysisResult> AnalyzeAsync(
        string projectId, string symbolQuery, int maxDepth = 3, CancellationToken ct = default)
    {
        var project = await projects.GetAsync(projectId, ct);
        var manifestVersion = project?.IndexManifestVersion;
        var freshness = project?.IndexStatus switch
        {
            ProjectIndexStatus.Indexed => IndexFreshness.Fresh,
            ProjectIndexStatus.PendingChanges => IndexFreshness.PendingChanges,
            ProjectIndexStatus.Indexing => IndexFreshness.Indexing,
            ProjectIndexStatus.Partial => IndexFreshness.Partial,
            ProjectIndexStatus.Failed => IndexFreshness.Failed,
            _ => IndexFreshness.Stale,
        };
        for (var attempt = 0; attempt < 2; attempt++)
        {
            // 1. 定位目標符號
            var hits = await graphStore.SearchAsync(projectId, symbolQuery, 5, ct);
            var target = hits.FirstOrDefault(h => h.Kind is
                "Method" or "Type" or "Endpoint" or "Query" or "Table" or "Column" or
                "View" or "Procedure" or "Migration" or "ConfigurationKey");
            if (target is null)
                return new ImpactAnalysisResult(null, [], [], [], [], manifestVersion, freshness);

            // 2. 反向依賴鏈與鄰域。每個 store query 本身固定在單一 active
            // graph；此處再防止發布恰好發生在三次 query 之間而混合版本。
            var chains = await graphStore.GetReverseCallChainAsync(projectId, target.Key, maxDepth, ct);
            var related = await graphStore.GetNeighborhoodAsync(projectId, target.Key, Math.Min(maxDepth, 3), ct);
            if (target.ManifestVersion is { Length: > 0 } queryVersion &&
                !HasSingleManifestVersion(queryVersion, chains, related))
            {
                if (attempt == 0)
                    continue;
                throw new InvalidOperationException(
                    "圖譜在影響分析期間已切換版本；未回傳混合結果，請重試。");
            }

            // 3. 彙整受影響方法/檔案
            var affectedMethods = chains
                .SelectMany(c => c.Chain)
                .Where(n => n.Key != target.Key)
                .DistinctBy(n => n.Key)
                .OrderBy(n => n.FilePath)
                .ToList();

            var affectedFiles = affectedMethods
                .Select(m => m.FilePath)
                .Where(f => f is not null)
                .Distinct()
                .Select(f => f!)
                .ToList();

            // 4. 建議測試過濾條件：受影響的測試類別（名稱含 Test/Tests 的類別）
            var testFilters = BuildTestFilters(affectedMethods);

            logger.LogInformation(
                "Impact analysis [{Symbol}]: {Chains} 條呼叫鏈, {Methods} 個受影響方法, {Files} 個檔案",
                target.Name, chains.Count, affectedMethods.Count, affectedFiles.Count);

            return new ImpactAnalysisResult(
                target,
                chains,
                affectedMethods,
                affectedFiles,
                testFilters,
                target.ManifestVersion ?? manifestVersion,
                freshness,
                related.Neighbors,
                related.Truncated || chains.Count >= 50);
        }

        throw new InvalidOperationException("影響分析未能取得一致的圖譜版本。");
    }

    private static bool HasSingleManifestVersion(
        string expected,
        IReadOnlyList<ImpactPath> chains,
        GraphNeighborhood related) =>
        chains.All(path =>
            string.Equals(path.ManifestVersion, expected, StringComparison.Ordinal) &&
            path.Chain.All(node => string.Equals(node.ManifestVersion, expected, StringComparison.Ordinal))) &&
        (related.Center is null || string.Equals(related.Center.ManifestVersion, expected, StringComparison.Ordinal)) &&
        related.Neighbors.All(node => string.Equals(node.ManifestVersion, expected, StringComparison.Ordinal));

    private static List<string> BuildTestFilters(IReadOnlyList<GraphSearchHit> affectedMethods)
    {
        var filters = new List<string>();

        // 從受影響方法的 Key 抽出所屬型別名稱（"Ns.Type.Method(...)" → "Type"）
        var typeNames = affectedMethods
            .Select(m => ExtractTypeName(m.Key))
            .Where(t => t is not null)
            .Distinct()
            .ToList();

        // 測試類別直接受影響 → 直接跑它們
        var testTypes = typeNames
            .Where(t => t!.EndsWith("Test", StringComparison.Ordinal) ||
                        t.EndsWith("Tests", StringComparison.Ordinal))
            .ToList();

        foreach (var t in testTypes)
        {
            filters.Add($"dotnet test --filter \"FullyQualifiedName~{t}\"");
            filters.Add($"mvn test -Dtest={t}");
        }

        // 沒有直接受影響的測試 → 建議依受影響型別名稱跑對應測試
        if (testTypes.Count == 0)
        {
            foreach (var t in typeNames.Take(5))
            {
                filters.Add($"dotnet test --filter \"FullyQualifiedName~{t}\"");
            }
        }

        return filters.Distinct().Take(10).ToList();
    }

    internal static string? ExtractTypeName(string key)
    {
        // 方法 key 含參數括號："Ns.Sub.Type.Method(int)" → Type
        // 型別 key 無括號："Ns.Type" → Type
        var isMethod = key.Contains('(');
        var withoutParams = key.Split('(')[0];
        var parts = withoutParams.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return null;
        if (parts.Length == 1)
            return parts[0];

        return isMethod ? parts[^2] : parts[^1];
    }
}
