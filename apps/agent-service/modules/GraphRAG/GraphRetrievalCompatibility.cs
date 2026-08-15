namespace AgentService.Modules.GraphRAG;

/// <summary>JIRA 邊界使用的舊式節點分類；不參與 V4 索引或 Neo4j 發布。</summary>
public enum LegacyGraphNodeKind
{
    /// <summary>可作為功能入口的節點。</summary>
    EntryPoint,
    /// <summary>功能或菜單節點。</summary>
    Feature,
    /// <summary>程式碼節點。</summary>
    Code,
    /// <summary>資料庫或報表資料節點。</summary>
    Data,
}
/// <summary>JIRA 檢索結果使用的扁平節點投影。</summary>
public sealed record LegacyGraphNode(
    string Id,
    LegacyGraphNodeKind Kind,
    string Role,
    string Name,
    string? FilePath,
    int? StartLine,
    int? EndLine,
    string? Language,
    IReadOnlyList<string> Aliases,
    string SearchableText,
    IReadOnlyList<LegacyGraphEvidence> Evidence);

/// <summary>JIRA 檢索結果使用的最小證據來源摘要。</summary>
public sealed record LegacyGraphEvidence(string Artifact);

/// <summary>JIRA 檢索結果使用的節點分數與遍歷資訊。</summary>
public sealed record LegacyScoredGraphNode(
    LegacyGraphNode Node,
    double Score,
    bool Seed,
    int Depth);

/// <summary>相容 JIRA 的局部檢索結果；只在應用邊界傳遞，不取代 V4 GraphDocument。</summary>
public sealed record LegacyGraphRetrievalContext(
    IReadOnlyList<LegacyScoredGraphNode> Nodes,
    IReadOnlyList<LegacyGraphRelationship> Edges);

/// <summary>相容 JIRA 的關係摘要。</summary>
public sealed record LegacyGraphRelationship(
    string Id,
    string SourceId,
    string TargetId,
    string Type);

/// <summary>JIRA 需要的節點分類與角色映射。</summary>
internal static class LegacyGraphMappings
{
    /// <summary>將 V4 authority kind 映射到 JIRA 舊分類。</summary>
    public static LegacyGraphNodeKind KindFor(FblAuthority.GraphNodeKind kind) => kind switch
    {
        FblAuthority.GraphNodeKind.Database or
        FblAuthority.GraphNodeKind.DatabaseObject or
        FblAuthority.GraphNodeKind.DatabaseColumn or
        FblAuthority.GraphNodeKind.StoredProcedureParameter => LegacyGraphNodeKind.Data,
        FblAuthority.GraphNodeKind.CustomParameterDataSource => LegacyGraphNodeKind.Data,
        FblAuthority.GraphNodeKind.ReportDataSource => LegacyGraphNodeKind.Data,
        FblAuthority.GraphNodeKind.ReportTemplate => LegacyGraphNodeKind.Data,
        FblAuthority.GraphNodeKind.ReportDocument => LegacyGraphNodeKind.Data,
        FblAuthority.GraphNodeKind.ReportParameter => LegacyGraphNodeKind.Data,
        FblAuthority.GraphNodeKind.ResultColumn => LegacyGraphNodeKind.Data,
        FblAuthority.GraphNodeKind.MenuItem => LegacyGraphNodeKind.Feature,
        FblAuthority.GraphNodeKind.ApiEndpoint or
        FblAuthority.GraphNodeKind.WebFormPage or
        FblAuthority.GraphNodeKind.View => LegacyGraphNodeKind.EntryPoint,
        _ => LegacyGraphNodeKind.Code,
    };

    /// <summary>將 V4 authority kind 映射到 JIRA 的入口角色文字。</summary>
    public static string RoleFor(FblAuthority.GraphNodeKind kind) => kind switch
    {
        FblAuthority.GraphNodeKind.MenuItem => GraphRoles.MenuFeature,
        FblAuthority.GraphNodeKind.ApiEndpoint => GraphRoles.WebRoute,
        FblAuthority.GraphNodeKind.WebFormPage or
        FblAuthority.GraphNodeKind.View or
        FblAuthority.GraphNodeKind.ScriptAsset => GraphRoles.FrontendPage,
        FblAuthority.GraphNodeKind.Type => GraphRoles.Controller,
        _ => string.Empty,
    };
}

/// <summary>維持 JIRA 服務原有的入口角色白名單。</summary>
internal static class GraphRoles
{
    /// <summary>Controller action 入口。</summary>
    public const string ControllerAction = "ControllerAction";
    /// <summary>Controller 類別入口。</summary>
    public const string Controller = "Controller";
    /// <summary>Web route 入口。</summary>
    public const string WebRoute = "WebRoute";
    /// <summary>菜單功能入口。</summary>
    public const string MenuFeature = "MenuFeature";
    /// <summary>排程任務入口。</summary>
    public const string ScheduledTask = "ScheduledTask";
    /// <summary>訊息消費者入口。</summary>
    public const string MessageConsumer = "MessageConsumer";
    /// <summary>前端頁面入口。</summary>
    public const string FrontendPage = "FrontendPage";
    /// <summary>命令列入口。</summary>
    public const string CliCommand = "CliCommand";
    /// <summary>排程入口。</summary>
    public const string Schedule = "Schedule";
}
