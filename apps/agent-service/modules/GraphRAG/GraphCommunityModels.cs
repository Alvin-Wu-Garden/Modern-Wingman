namespace AgentService.Modules.GraphRAG;

/// <summary>Community 結構模板與背景 AI 摘要共用的狀態常數。</summary>
public static class GraphCommunitySummaryStates
{
    /// <summary>只包含確定性結構摘要，可立即查詢。</summary>
    public const string Template = "template";
    /// <summary>已排入背景佇列。</summary>
    public const string Queued = "queued";
    /// <summary>背景模型正在處理。</summary>
    public const string Running = "running";
    /// <summary>AI 摘要已完成且 cache key 仍有效。</summary>
    public const string AiReady = "ai-ready";
    /// <summary>重試耗盡，繼續使用結構模板。</summary>
    public const string Failed = "failed";

    /// <summary>確認狀態值是否為已知固定值。</summary>
    public static bool IsKnown(string state) => state is Template or Queued or Running or AiReady or Failed;
}

/// <summary>
/// 可直接發布的 deterministic Community template。
/// MemberIds 與 CacheKey 都由同一份 FBL authority document 產生，不重新推測關係。
/// </summary>
public sealed record GraphCommunityReportV4(
    string CommunityId,
    string Tier,
    string? ParentCommunityId,
    bool Resolved,
    string Title,
    string Summary,
    string SummaryState,
    int RetryCount,
    IReadOnlyList<string> MemberIds,
    int MemberCount,
    IReadOnlyList<string> TopTables,
    IReadOnlyList<string> TopEntryPoints,
    string CacheKey,
    bool Truncated,
    int TruncatedMemberCount,
    IReadOnlyDictionary<string, string> Attributes);

/// <summary>LLM 產生的 Community 顯示文字。</summary>
public sealed record GraphCommunityGeneratedText(string Title, string Summary);

/// <summary>Neo4j 專案目前 active 與 previous immutable version 指標。</summary>
public sealed record GraphVersionPointers(
    string ProjectId,
    string? ActiveVersion,
    string? PreviousVersion);

/// <summary>Neo4j staging 版本的節點、關係與 schema 驗證結果。</summary>
public sealed record GraphVersionValidation(
    bool IsValid,
    int NodeCount,
    int EdgeCount,
    IReadOnlyList<string> Errors);

/// <summary>單一 Neo4j store 在穩定狀態下的版本與殘留資料統計。</summary>
public sealed record GraphStorageAcceptanceDiagnostics(
    string ProjectId,
    string? ActiveVersion,
    string? PreviousVersion,
    int VersionCount,
    int ActiveNodeCount,
    int ActiveEdgeCount,
    int InactiveNodeCount,
    int InactiveEdgeCount,
    int InactiveCommunityCount);
