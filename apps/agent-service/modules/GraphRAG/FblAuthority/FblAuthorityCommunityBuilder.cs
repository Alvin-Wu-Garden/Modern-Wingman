using System.Security.Cryptography;
using System.Text;
using AgentService.Modules.GraphRAG;

namespace AgentService.Modules.GraphRAG.FblAuthority;

/// <summary>
/// 由已完成驗證的 FBL 權威圖建立 Community 結構模板。
/// 此類別不呼叫模型，也不重新推測程式關係；索引發布只需付出一次線性走訪成本，
/// AI 摘要則交由既有背景佇列處理，避免 696 個功能阻塞圖譜可用時間。
/// </summary>
public static class FblAuthorityCommunityBuilder
{
    private const int MaximumFunctionDepth = 8;
    private const int MaximumFunctionMembers = 100;

    /// <summary>
    /// 建立三個入口類型 C0 與每個 Menu 一個 C1 模板。
    /// C0 只供全域導覽並在發布後背景預熱；C1 保存單一功能的實際可達鏈路，
    /// 只有問答命中時才排入 AI 摘要，因此模板數量不會轉化成索引等待時間。
    /// </summary>
    /// <param name="document">已通過 Preflight 的完整 FBL 圖文件。</param>
    /// <returns>排序穩定、可直接持久化的 Community 模板。</returns>
    public static IReadOnlyList<GraphCommunityReportV4> Build(GraphDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var nodes = document.Nodes.ToDictionary(node => node.Key, StringComparer.Ordinal);
        var outgoing = document.Relationships
            .GroupBy(relationship => relationship.SourceKey, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(relationship => relationship.Kind)
                    .ThenBy(relationship => relationship.TargetKey, StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.Ordinal);
        var menus = document.Nodes
            .Where(node => node.Kind == GraphNodeKind.Menu)
            .OrderBy(node => node.Key, StringComparer.Ordinal)
            .ToArray();

        var reports = new List<GraphCommunityReportV4>(menus.Length + 3);
        var parentByResolver = BuildC0(menus, reports);
        foreach (var menu in menus)
        {
            reports.Add(BuildFunctionTemplate(
                menu,
                parentByResolver[ResolverKind(menu)],
                nodes,
                outgoing));
        }

        return reports
            .OrderBy(report => report.Tier, StringComparer.Ordinal)
            .ThenBy(report => report.CommunityId, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>依 StandardWeb、PluginReport、CustomReport 建立少量頂層模板。</summary>
    private static IReadOnlyDictionary<string, string> BuildC0(
        IReadOnlyList<GraphNode> menus,
        ICollection<GraphCommunityReportV4> reports)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in menus
                     .GroupBy(ResolverKind, StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var communityId = CommunityId("c0", group.Key);
            var memberIds = group.Select(menu => menu.Key)
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToArray();
            var title = group.Key switch
            {
                nameof(MenuResolverKind.StandardWeb) => "FBL 一般交易與管理功能",
                nameof(MenuResolverKind.PluginReport) => "FBL PluginReport 功能",
                nameof(MenuResolverKind.CustomReport) => "FBL CustomReport 功能",
                _ => $"FBL {group.Key} 功能",
            };
            var summary = $"此社群包含 {memberIds.Length} 個 {group.Key} 菜單入口；" +
                          "每個功能的程式與資料鏈請查詢對應 C1 結構模板。";
            reports.Add(CreateReport(
                communityId,
                "C0",
                null,
                title,
                summary,
                memberIds,
                topTables: [],
                topEntryPoints: memberIds.Take(20).ToArray(),
                truncated: memberIds.Length > 20,
                truncatedMemberCount: Math.Max(0, memberIds.Length - 20),
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["resolverKind"] = group.Key,
                    ["source"] = "fbl-menu-inventory",
                }));
            result[group.Key] = communityId;
        }

        return result;
    }

    /// <summary>以有界 BFS 保存單一 Menu 的可達鏈路，不跨入其他 Menu。</summary>
    private static GraphCommunityReportV4 BuildFunctionTemplate(
        GraphNode menu,
        string parentCommunityId,
        IReadOnlyDictionary<string, GraphNode> nodes,
        IReadOnlyDictionary<string, GraphRelationship[]> outgoing)
    {
        var queue = new Queue<(string Key, int Depth)>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { menu.Key };
        var members = new List<string>(MaximumFunctionMembers) { menu.Key };
        queue.Enqueue((menu.Key, 0));
        var truncatedCount = 0;

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current.Depth >= MaximumFunctionDepth ||
                !outgoing.TryGetValue(current.Key, out var relationships))
            {
                continue;
            }

            foreach (var relationship in relationships)
            {
                if (!nodes.TryGetValue(relationship.TargetKey, out var target) ||
                    target.Kind == GraphNodeKind.Menu ||
                    !visited.Add(target.Key))
                {
                    continue;
                }

                if (members.Count < MaximumFunctionMembers)
                {
                    members.Add(target.Key);
                    queue.Enqueue((target.Key, current.Depth + 1));
                }
                else
                {
                    truncatedCount++;
                }
            }
        }

        var orderedMembers = members.OrderBy(key => key, StringComparer.Ordinal).ToArray();
        var memberNodes = orderedMembers.Select(key => nodes[key]).ToArray();
        var kindCounts = memberNodes.GroupBy(node => node.Kind)
            .OrderBy(group => group.Key)
            .Select(group => $"{group.Key} {group.Count()}")
            .ToArray();
        var title = StringProperty(menu, "name") ?? menu.Key;
        var summary = $"功能「{title}」已解析 {orderedMembers.Length} 個節點：" +
                      string.Join("、", kindCounts) + "。";
        var tables = memberNodes
            .Where(node => node.Kind == GraphNodeKind.DatabaseObject)
            .Select(node => StringProperty(node, "name") ?? node.Key)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToArray();
        var endpoints = memberNodes
            .Where(node => node.Kind is GraphNodeKind.Endpoint or GraphNodeKind.WebAction)
            .Select(node => StringProperty(node, "path") ?? node.Key)
            .Take(20)
            .ToArray();

        return CreateReport(
            CommunityId("c1", menu.Key),
            "C1",
            parentCommunityId,
            title,
            summary,
            orderedMembers,
            tables,
            endpoints,
            truncatedCount > 0,
            truncatedCount,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["anchorKey"] = menu.Key,
                ["menuId"] = StringProperty(menu, "menu_id") ?? string.Empty,
                ["resolverKind"] = ResolverKind(menu),
                ["maxDepth"] = MaximumFunctionDepth.ToString(),
            });
    }

    /// <summary>建立具有穩定 CacheKey 的 deterministic template。</summary>
    private static GraphCommunityReportV4 CreateReport(
        string communityId,
        string tier,
        string? parentCommunityId,
        string title,
        string summary,
        IReadOnlyList<string> memberIds,
        IReadOnlyList<string> topTables,
        IReadOnlyList<string> topEntryPoints,
        bool truncated,
        int truncatedMemberCount,
        IReadOnlyDictionary<string, string> attributes)
    {
        var cacheMaterial = string.Join('\n',
            "fbl-community-v1",
            communityId,
            title,
            summary,
            string.Join('|', memberIds));
        var cacheKey = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(cacheMaterial)))
            .ToLowerInvariant();
        return new GraphCommunityReportV4(
            communityId,
            tier,
            parentCommunityId,
            Resolved: true,
            title,
            summary,
            GraphCommunitySummaryStates.Template,
            RetryCount: 0,
            memberIds,
            memberIds.Count,
            topTables,
            topEntryPoints,
            cacheKey,
            truncated,
            truncatedMemberCount,
            attributes);
    }

    /// <summary>取得 Menu 的固定 resolver kind；缺值視為 StandardWeb。</summary>
    private static string ResolverKind(GraphNode menu) =>
        StringProperty(menu, "resolver_kind") ?? nameof(MenuResolverKind.StandardWeb);

    /// <summary>安全讀取權威節點的字串屬性。</summary>
    private static string? StringProperty(GraphNode node, string key) =>
        node.Properties.TryGetValue(key, out var value) && value is not null
            ? value.ToString()
            : null;

    /// <summary>以穩定文字產生固定 Community ID。</summary>
    private static string CommunityId(string tier, string identity)
    {
        var digest = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes($"{tier}|{identity}")))
            .ToLowerInvariant();
        return $"{tier}:{digest[..16]}";
    }
}
