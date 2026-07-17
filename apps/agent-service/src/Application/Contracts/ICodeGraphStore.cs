using AgentService.Domain.Models;

namespace AgentService.Application.Contracts;

/// <summary>
/// 程式碼知識圖譜儲存（Neo4j 實作）。
/// 職責：節點/關係寫入、查詢（呼叫鏈、鄰域、full-text）、社群摘要儲存。
/// </summary>
public interface ICodeGraphStore
{
    /// <summary>確認連線可用。</summary>
    Task<bool> PingAsync(CancellationToken ct = default);

    /// <summary>初始化 constraint 與 full-text index。</summary>
    Task EnsureSchemaAsync(CancellationToken ct = default);

    /// <summary>
    /// 以單一資料庫交易替換專案圖譜。分析或寫入失敗時，上一個成功版本必須保持完整。
    /// </summary>
    Task ReplaceProjectAsync(
        string projectId,
        GraphPublishDescriptor descriptor,
        CodeAnalysisResult result,
        CancellationToken ct = default);

    /// <summary>
    /// 從指定的目前成功版本建立完整 staging clone，再套用 delta 並原子發布。
    /// 實作不得原地修改 active graph；不支援 delta 的 store 必須明確拒絕，
    /// 讓呼叫端安全退回完整 ReplaceProjectAsync。
    /// </summary>
    Task ApplyProjectDeltaAsync(
        string projectId,
        GraphPublishDescriptor descriptor,
        GraphPublishDelta delta,
        CancellationToken ct = default) =>
        Task.FromException(new NotSupportedException("This graph store does not support atomic delta publishing."));

    /// <summary>
    /// 取得目前原子圖譜版本。無圖譜時回傳 null；若節點缺少版本或混合多個版本，
    /// 視為資料不一致並回傳 null，不得提供給 recovery 當作成功版本。
    /// </summary>
    Task<string?> GetProjectManifestVersionAsync(
        string projectId,
        CancellationToken ct = default);

    /// <summary>刪除整個專案子圖。</summary>
    Task DeleteProjectAsync(string projectId, CancellationToken ct = default);

    /// <summary>full-text 搜尋節點（BM25）。</summary>
    Task<IReadOnlyList<GraphSearchHit>> SearchAsync(
        string projectId, string query, int limit = 20, CancellationToken ct = default);

    /// <summary>取得節點的鄰域（呼叫者/被呼叫者/包含關係），Local Search 用。</summary>
    Task<GraphNeighborhood> GetNeighborhoodAsync(
        string projectId, string nodeKey, int depth = 1, CancellationToken ct = default);

    /// <summary>反向呼叫鏈：誰（遞移地）呼叫了此方法 — Impact Analysis 核心。</summary>
    Task<IReadOnlyList<ImpactPath>> GetReverseCallChainAsync(
        string projectId, string nodeKey, int maxDepth = 3, CancellationToken ct = default);

    /// <summary>取得專案統計（節點數/邊數）。</summary>
    Task<(int Nodes, int Edges)> GetStatsAsync(string projectId, CancellationToken ct = default);

    /// <summary>以圖譜 degree 取得入口與核心符號，不依賴 full-text wildcard。</summary>
    Task<IReadOnlyList<GraphSearchHit>> GetCentralNodesAsync(
        string projectId, int limit = 200, CancellationToken ct = default);

    /// <summary>儲存社群摘要（GraphRAG community summary）。</summary>
    Task SaveCommunitySummaryAsync(
        string projectId, string targetManifestVersion,
        string communityId, string title, string summary,
        IReadOnlyList<string> memberKeys, CancellationToken ct = default);

    /// <summary>列出專案的所有社群摘要（Global Search 用）。</summary>
    Task<IReadOnlyList<CommunitySummary>> ListCommunitySummariesAsync(
        string projectId, CancellationToken ct = default);

    /// <summary>依 Louvain/Leiden 社群偵測分群（回傳 nodeKey → communityId）。</summary>
    Task<IReadOnlyDictionary<string, string>> DetectCommunitiesAsync(
        string projectId, CancellationToken ct = default);

    /// <summary>取得知識圖譜瀏覽器的初始節點/關係快照。</summary>
    Task<CodeGraphVisualData> GetVisualGraphAsync(
        string projectId,
        int limit = 1000,
        IReadOnlyList<string>? kinds = null,
        IReadOnlyList<string>? relationTypes = null,
        CancellationToken ct = default);

    /// <summary>取得知識圖譜瀏覽器的 schema 摘要。</summary>
    Task<CodeGraphSchema> GetVisualSchemaAsync(
        string projectId,
        CancellationToken ct = default);

    /// <summary>以 read-only Cypher 查詢圖譜，回傳表格結果與可視化子圖。</summary>
    Task<CodeGraphQueryResult> QueryVisualGraphAsync(
        string projectId,
        string cypher,
        int limit = 1000,
        CancellationToken ct = default);

    /// <summary>依節點清單展開更多鄰近圖譜。</summary>
    Task<CodeGraphVisualData> GetVisualNeighborsAsync(
        string projectId,
        IReadOnlyList<string> nodeKeys,
        int depth = 1,
        int limit = 1000,
        string mode = "all",
        CancellationToken ct = default);
}

/// <summary>
/// 一次完整 graph snapshot 的發布契約。版本僅存於 graph-level
/// anchor；node/edge 的 contentHash 仍代表其 producer provenance。
/// </summary>
public sealed record GraphPublishDescriptor(
    string ManifestVersion,
    string GraphId,
    string GraphSchemaVersion,
    string AnalysisSnapshotHash,
    int ExpectedNodeCount,
    int ExpectedEdgeCount,
    string? CanonicalDigest = null);

/// <summary>一條關係的穩定 identity；方向是 source → target，不可互換。</summary>
public sealed record GraphEdgeIdentity(
    string SourceKey,
    CodeEdgeKind Kind,
    string TargetKey);

/// <summary>
/// 對已發布 base manifest 套用的精確圖譜差異。Expected counts/hash 由
/// GraphPublishDescriptor 描述套用後的完整結果。
/// </summary>
public sealed record GraphPublishDelta(
    string BaseManifestVersion,
    IReadOnlyList<string> RemovedNodeKeys,
    IReadOnlyList<CodeNode> UpsertNodes,
    IReadOnlyList<GraphEdgeIdentity> RemovedEdges,
    IReadOnlyList<CodeEdge> UpsertEdges);

public sealed record GraphSearchHit(
    string Key, string Kind, string Name, string? Signature,
    string? FilePath, int? StartLine, double Score,
    int? EndLine = null,
    GraphSourceKind SourceKind = GraphSourceKind.Unknown,
    GraphConfidence Confidence = GraphConfidence.Unknown,
    string? ExtractorId = null,
    string? ExtractorVersion = null,
    string? Reason = null,
    string? ManifestVersion = null,
    string? ContentHash = null);

public sealed record GraphNeighborNode(
    string Key, string Kind, string Name, string? FilePath,
    string RelationKind, string Direction,
    int? StartLine = null,
    int? EndLine = null,
    GraphSourceKind SourceKind = GraphSourceKind.Unknown,
    GraphConfidence Confidence = GraphConfidence.Unknown,
    string? ExtractorId = null,
    string? ExtractorVersion = null,
    string? Reason = null,
    string? ManifestVersion = null,
    string? ContentHash = null);

public sealed record GraphNeighborhood(
    GraphSearchHit? Center,
    IReadOnlyList<GraphNeighborNode> Neighbors,
    bool Truncated = false,
    int Depth = 1);

/// <summary>一條反向呼叫鏈路徑（Impact Analysis 輸出）。</summary>
public sealed record ImpactPath(
    IReadOnlyList<GraphSearchHit> Chain,
    bool Truncated = false,
    int Depth = 1,
    GraphSourceKind SourceKind = GraphSourceKind.Unknown,
    GraphConfidence Confidence = GraphConfidence.Unknown,
    string? ExtractorId = null,
    string? ExtractorVersion = null,
    string? Reason = null,
    string? ManifestVersion = null,
    string? ContentHash = null);

public sealed record CommunitySummary(
    string CommunityId, string Title, string Summary, IReadOnlyList<string> MemberKeys);

public sealed record CodeGraphVisualNode(
    string Id,
    string Kind,
    string Name,
    string? Signature,
    string? FilePath,
    int? StartLine,
    int? EndLine,
    string? Language,
    int Degree,
    IReadOnlyDictionary<string, object?> Properties);

public sealed record CodeGraphVisualEdge(
    string Id,
    string Source,
    string Target,
    string Type,
    IReadOnlyDictionary<string, object?> Properties);

public sealed record CodeGraphVisualData(
    IReadOnlyList<CodeGraphVisualNode> Nodes,
    IReadOnlyList<CodeGraphVisualEdge> Edges,
    int TotalNodes,
    int LoadedNodes,
    int LoadedEdges,
    bool HasMore);

public sealed record CodeGraphFacet(string Name, int Count);

public sealed record CodeGraphSchema(
    int TotalNodes,
    int TotalEdges,
    IReadOnlyList<CodeGraphFacet> NodeKinds,
    IReadOnlyList<CodeGraphFacet> RelationshipTypes,
    IReadOnlyList<string> PropertyKeys);

public sealed record CodeGraphQueryResult(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    CodeGraphVisualData Graph);
