namespace AgentService.Application.Atlassian;

/// <summary>
/// 從 GraphRAG 檢索結果對應到 JIRA 分析用途的單筆命中。
/// 欄位值直接對應 <see cref="AgentService.Modules.GraphRAG.ExtractedGraph.GraphNode"/> 屬性，
/// 避免重複抽象化現有 Graph Domain Model。
/// </summary>
public sealed record JiraGraphRagHit(
    string ProjectId,
    string? FeatureCode,
    string? FeatureName,
    string Query,
    string NodeId,
    string NodeKind,
    string NodeRole,
    string NodeName,
    string? FilePath,
    int? StartLine,
    int? EndLine,
    string? Language,
    double Score,
    string MatchReason,
    IReadOnlyList<string> RelatedNodeIds);

/// <summary>
/// 是否已確認為功能入口的狀態。
/// </summary>
public enum JiraEntryPointStatus
{
    /// <summary>節點角色明確屬於 EntryPoint 且有一項以上判定依據。</summary>
    Confirmed,
    /// <summary>節點分數高但缺少代號或名稱直接命中的佐證，需使用者確認。</summary>
    Candidate,
}

/// <summary>
/// 入口候選節點，含確認狀態與判定依據。
/// </summary>
public sealed record JiraEntryPoint(
    string NodeId,
    string NodeName,
    string NodeRole,
    string? FilePath,
    string? FeatureCode,
    string? FeatureName,
    double Score,
    JiraEntryPointStatus Status,
    IReadOnlyList<string> Evidence);

/// <summary>
/// 對單一 JIRA 議題執行三階段 GraphRAG 檢索後的彙整結果。
/// </summary>
public sealed record JiraGraphRagContext(
    IReadOnlyList<JiraFeatureIdentifier> Features,
    IReadOnlyList<string> Queries,
    IReadOnlyList<JiraEntryPoint> ConfirmedEntryPoints,
    IReadOnlyList<JiraEntryPoint> CandidateEntryPoints,
    IReadOnlyList<JiraGraphRagHit> Hits,
    int TotalHitCount,
    int IncludedHitCount,
    bool WasTruncated,
    bool WasDegraded,
    IReadOnlyList<string> Warnings,
    int EstimatedTokens)
{
    /// <summary>是否有任何可用的 GraphRAG 結果（含已確認或候選入口，或一般命中）。</summary>
    public bool HasResults => ConfirmedEntryPoints.Count > 0
        || CandidateEntryPoints.Count > 0
        || Hits.Count > 0;

    /// <summary>空白降級 context，供 Neo4j 不可用時使用。</summary>
    public static JiraGraphRagContext Degraded(string reason) => new(
        [],
        [],
        [],
        [],
        [],
        0,
        0,
        false,
        true,
        [reason],
        0);
}
