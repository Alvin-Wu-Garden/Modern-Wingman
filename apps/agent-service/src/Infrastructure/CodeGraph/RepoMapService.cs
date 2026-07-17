using System.Text;
using AgentService.Application.Contracts;
using Microsoft.Extensions.Logging;

namespace AgentService.Infrastructure.CodeGraph;

/// <summary>
/// Repo Map 生成（WS3.4，Aider 方法論）：
///   從圖譜取出被引用最多的符號（degree centrality 作為 PageRank 的低成本近似），
///   在 token 預算內生成「檔案 → 關鍵符號」骨架圖，注入 Run context。
/// </summary>
public sealed class RepoMapService(
    ICodeGraphStore graphStore,
    ILogger<RepoMapService> logger)
{
    /// <summary>
    /// 生成 repo map 文字（約 tokenBudget * 4 字元）。
    /// </summary>
    public async Task<string> GenerateAsync(
        string projectId, int tokenBudget = 1024, CancellationToken ct = default)
    {
        var charBudget = tokenBudget * 4;

        var hits = await graphStore.GetCentralNodesAsync(projectId, 200, ct);

        // 依檔案分組，每檔列出符號（Type 優先、行號排序）
        var byFile = hits
            .Where(h => h.FilePath is not null && h.Kind is "Type" or "Method" or "Property")
            .GroupBy(h => h.FilePath!)
            .OrderByDescending(g => g.Count())
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("# Repo Map（關鍵符號骨架）");
        foreach (var group in byFile)
        {
            if (sb.Length > charBudget)
                break;
            sb.AppendLine($"{group.Key}:");
            foreach (var hit in group.OrderBy(h => h.StartLine ?? 0).Take(12))
            {
                var indent = hit.Kind == "Type" ? "  " : "    ";
                sb.AppendLine($"{indent}{hit.Kind}: {hit.Signature ?? hit.Name}");
            }
        }

        var map = sb.ToString();
        if (map.Length > charBudget)
            map = map[..charBudget] + "\n…（已截斷）";

        logger.LogDebug("Repo map 生成: {Files} 檔案, {Chars} 字元", byFile.Count, map.Length);
        return map;
    }
}
