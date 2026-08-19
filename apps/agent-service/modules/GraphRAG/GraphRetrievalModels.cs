using AgentService.Modules.GraphRAG.ExtractedGraph;

namespace AgentService.Modules.GraphRAG;

/// <summary>
/// 問答檢索邊界的節點投影。
/// <see cref="Kind"/> 直接保留 ParallelExtractor 節點標籤，不壓縮成舊式分類。
/// </summary>
public sealed record RetrievedGraphNode(
    string Id,
    GraphNodeKind Kind,
    string Role,
    string Name,
    string? FilePath,
    int? StartLine,
    int? EndLine,
    string? Language,
    IReadOnlyList<string> Aliases,
    string SearchableText,
    IReadOnlyList<GraphRetrievalEvidence> Evidence);

/// <summary>檢索節點可追溯的原始碼或資料庫證據。</summary>
public sealed record GraphRetrievalEvidence(string Artifact);

/// <summary>附帶相關分數與遍歷深度的 Graph 節點。</summary>
public sealed record ScoredRetrievedGraphNode(
    RetrievedGraphNode Node,
    double Score,
    bool Seed,
    int Depth);

/// <summary>局部 Graph 檢索的節點與關係子圖。</summary>
public sealed record GraphRetrievalContext(
    IReadOnlyList<ScoredRetrievedGraphNode> Nodes,
    IReadOnlyList<RetrievedGraphRelationship> Edges);

/// <summary>局部 Graph 檢索中的關係投影；<see cref="Type"/> 保留 Neo4j 關係型別原名。</summary>
public sealed record RetrievedGraphRelationship(
    string Id,
    string SourceId,
    string TargetId,
    string Type);

/// <summary>由 ParallelExtractor 節點屬性推導 JIRA 需要的功能入口角色。</summary>
internal static class GraphRetrievalRoles
{
    public const string ControllerAction = "ControllerAction";
    public const string Controller = "Controller";
    public const string WebRoute = "WebRoute";
    public const string MenuFeature = "MenuFeature";
    public const string ScheduledTask = "ScheduledTask";
    public const string MessageConsumer = "MessageConsumer";
    public const string FrontendPage = "FrontendPage";
    public const string CliCommand = "CliCommand";
    public const string Schedule = "Schedule";

    /// <summary>在節點沒有明確 <c>role</c> 屬性時，以原始標籤與名稱做保守推導。</summary>
    public static string Infer(GraphNodeKind kind, string name) => kind switch
    {
        GraphNodeKind.MenuItem => MenuFeature,
        GraphNodeKind.ApiEndpoint => WebRoute,
        GraphNodeKind.WebFormPage or GraphNodeKind.View or GraphNodeKind.ScriptAsset => FrontendPage,
        GraphNodeKind.Schedule or GraphNodeKind.ScheduleTaskDefinition => Schedule,
        GraphNodeKind.Type when name.EndsWith("Controller", StringComparison.OrdinalIgnoreCase) => Controller,
        GraphNodeKind.Method when name.Contains("Controller", StringComparison.OrdinalIgnoreCase) => ControllerAction,
        _ => string.Empty,
    };
}
