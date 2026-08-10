using Wingman.Marketplace.Domain;

namespace Wingman.Marketplace.Contracts;

/// <summary>Marketplace 探索查詢的條件。</summary>
public sealed record DiscoveryQuery(
    string QueryId,
    string QueryText,
    MarketplaceArtifactKind KindHint,
    int MaxResults);

/// <summary>由探索提供者回傳的候選項目。</summary>
public sealed record DiscoveryCandidate(
    string? GitHubNodeId,
    string CanonicalUrl,
    string Owner,
    string Repository,
    string Name,
    string? Description,
    IReadOnlyList<string> Topics,
    string? License,
    bool IsArchived,
    int Stars,
    int Forks,
    DateTimeOffset? GitHubUpdatedAt,
    DateTimeOffset? PushedAt,
    MarketplaceArtifactKind KindHint);

/// <summary>從外部來源探索 Marketplace 候選項目的提供者。</summary>
public interface IDiscoveryProvider
{
    /// <summary>取得提供者的穩定識別字。</summary>
    string ProviderId { get; }

    /// <summary>依查詢條件非同步探索候選項目。</summary>
    Task<IReadOnlyList<DiscoveryCandidate>> DiscoverAsync(
        DiscoveryQuery query,
        CancellationToken cancellationToken = default);
}

/// <summary>提供 Marketplace 探索資料的持久化存取介面。</summary>
public interface IMarketplaceStore
{
    /// <summary>依篩選條件分頁列出探索結果。</summary>
    Task<MarketplacePage> ListDiscoveryAsync(MarketplaceListFilter filter, CancellationToken cancellationToken = default);
    /// <summary>依識別字取得探索紀錄。</summary>
    Task<MarketplaceDiscoveryRecord?> GetDiscoveryAsync(string id, CancellationToken cancellationToken = default);
    /// <summary>依 GitHub 節點識別字取得探索紀錄。</summary>
    Task<MarketplaceDiscoveryRecord?> GetDiscoveryByGitHubNodeIdAsync(string gitHubNodeId, CancellationToken cancellationToken = default);
    /// <summary>新增或更新探索紀錄及其評分快照。</summary>
    Task UpsertDiscoveryAsync(
        IReadOnlyList<MarketplaceDiscoveryRecord> records,
        IReadOnlyList<MarketplaceScoreSnapshot> scoreSnapshots,
        CancellationToken cancellationToken = default);
    /// <summary>完成一次 Marketplace 同步作業。</summary>
    Task<MarketplaceRefreshResult> CompleteRefreshAsync(
        string syncRunId,
        IReadOnlyCollection<string> seenGitHubNodeIds,
        int newCount,
        int updatedCount,
        int unchangedCount,
        int successfulQueries,
        int totalQueries,
        bool isPartial,
        CancellationToken cancellationToken = default);
    /// <summary>開始一次 Marketplace 同步作業並回傳作業識別字。</summary>
    Task<string> StartRefreshAsync(CancellationToken cancellationToken = default);
    /// <summary>將同步作業標記為失敗。</summary>
    Task FailRefreshAsync(string syncRunId, string error, CancellationToken cancellationToken = default);
    /// <summary>設定探索項目的最愛狀態。</summary>
    Task SetFavoriteAsync(string id, bool isFavorite, CancellationToken cancellationToken = default);
}

/// <summary>提供 Marketplace 探索功能的應用程式服務。</summary>
public interface IMarketplaceService
{
    /// <summary>列出 Marketplace 探索結果。</summary>
    Task<MarketplacePage> ListAsync(MarketplaceListFilter filter, CancellationToken cancellationToken = default);
    /// <summary>取得單一 Marketplace 探索紀錄。</summary>
    Task<MarketplaceDiscoveryRecord?> GetAsync(string id, CancellationToken cancellationToken = default);
    /// <summary>執行 Marketplace 同步。</summary>
    Task<MarketplaceRefreshResult> RefreshAsync(CancellationToken cancellationToken = default);
    /// <summary>設定探索項目的最愛狀態。</summary>
    Task SetFavoriteAsync(string id, bool isFavorite, CancellationToken cancellationToken = default);
}

/// <summary>將本機資料夾解析為可匯入的 Marketplace artifact。</summary>
public interface IArtifactResolver
{
    /// <summary>解析指定資料夾中的 artifact 候選項目。</summary>
    Task<IReadOnlyList<MarketplaceArtifactCandidate>> ResolveFolderAsync(
        string sourceFolder,
        CancellationToken cancellationToken = default);
}

/// <summary>提供 Marketplace artifact 的持久化存取介面。</summary>
public interface IMarketplaceArtifactStore
{
    /// <summary>儲存匯入候選項目、artifact 及評分快照。</summary>
    Task SaveImportAsync(
        IReadOnlyList<MarketplaceArtifactCandidate> candidates,
        IReadOnlyList<MarketplaceArtifact> artifacts,
        IReadOnlyList<MarketplaceArtifactScoreSnapshot> scoreSnapshots,
        CancellationToken cancellationToken = default);
    /// <summary>列出目前已匯入的 artifact。</summary>
    Task<IReadOnlyList<MarketplaceArtifact>> ListArtifactsAsync(CancellationToken cancellationToken = default);
    /// <summary>依識別字取得 artifact。</summary>
    Task<MarketplaceArtifact?> GetArtifactAsync(string id, CancellationToken cancellationToken = default);
    /// <summary>依內容雜湊及類型取得 artifact。</summary>
    Task<MarketplaceArtifact?> GetArtifactByContentHashAsync(string contentHash, MarketplaceArtifactKind kind, CancellationToken cancellationToken = default);
    /// <summary>列出 artifact 的來源。</summary>
    Task<IReadOnlyList<MarketplaceArtifactSource>> ListArtifactSourcesAsync(CancellationToken cancellationToken = default);
}

/// <summary>提供 Marketplace artifact 匯入功能的應用程式服務。</summary>
public interface IMarketplaceArtifactService
{
    /// <summary>匯入指定的本機資料夾。</summary>
    Task<MarketplaceImportResult> ImportFolderAsync(string sourceFolder, CancellationToken cancellationToken = default);
    /// <summary>匯入指定的壓縮檔。</summary>
    Task<MarketplaceImportResult> ImportArchiveAsync(string archivePath, string? sourceLocation = null, CancellationToken cancellationToken = default);
    /// <summary>列出目前已匯入的 artifact。</summary>
    Task<IReadOnlyList<MarketplaceArtifact>> ListArtifactsAsync(CancellationToken cancellationToken = default);
}

/// <summary>描述並解析外部 Agent 的部署目標位置。</summary>
public interface IAgentTargetAdapter
{
    /// <summary>取得目標 Agent 的描述資料。</summary>
    MarketplaceTargetDescriptor Descriptor { get; }
    /// <summary>JSON MCP 設定中保存 server map 的根屬性。</summary>
    string McpRootProperty { get; }
    /// <summary>解析 Skill 的安裝資料夾。</summary>
    string ResolveSkillDirectory(MarketplaceDeploymentScope scope, string? projectPath);
    /// <summary>解析 MCP 設定檔路徑。</summary>
    string ResolveMcpConfigPath(MarketplaceDeploymentScope scope, string? projectPath);
}

/// <summary>提供 Marketplace 部署狀態的持久化存取介面。</summary>
public interface IMarketplaceDeploymentStore
{
    /// <summary>儲存一次 artifact 部署結果。</summary>
    Task SaveDeploymentAsync(string artifactId, MarketplaceDeploymentRequest request, string targetPath, string deployedHash, string status, CancellationToken cancellationToken = default);
    /// <summary>列出指定 artifact 的部署紀錄。</summary>
    Task<IReadOnlyList<(string TargetId, MarketplaceDeploymentScope Scope, string TargetPath, string DeployedHash, string Status)>> ListDeploymentsAsync(string artifactId, CancellationToken cancellationToken = default);
    /// <summary>更新指定部署的狀態。</summary>
    Task UpdateDeploymentStatusAsync(string artifactId, string targetId, MarketplaceDeploymentScope scope, string status, CancellationToken cancellationToken = default);
    /// <summary>列出指定 artifact 的部署狀態。</summary>
    Task<IReadOnlyList<MarketplaceDeploymentState>> ListDeploymentStatesAsync(string artifactId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<MarketplaceDeploymentState>>([]);
}

/// <summary>提供部署可安裝性結果的持久化存取介面。</summary>
public interface IMarketplaceInstallabilityStore
{
    /// <summary>儲存可安裝性結果。</summary>
    Task SaveAsync(IReadOnlyList<MarketplaceInstallabilityResult> results, CancellationToken cancellationToken = default);
    /// <summary>列出指定 artifact 的可安裝性結果。</summary>
    Task<IReadOnlyList<MarketplaceInstallabilityResult>> ListAsync(string artifactId, CancellationToken cancellationToken = default);
}

/// <summary>提供 Marketplace artifact 部署與預覽功能的應用程式服務。</summary>
public interface IMarketplaceDeploymentService
{
    /// <summary>列出支援的部署目標。</summary>
    Task<IReadOnlyList<MarketplaceTargetDescriptor>> ListTargetsAsync(CancellationToken cancellationToken = default);
    /// <summary>將 Skill 部署到指定目標。</summary>
    Task<MarketplaceDeploymentBatchResult> DeploySkillAsync(IReadOnlyList<MarketplaceDeploymentRequest> requests, CancellationToken cancellationToken = default);
    /// <summary>從所有受管理目標移除 artifact。</summary>
    Task<MarketplaceDeploymentBatchResult> RemoveFromAllManagedTargetsAsync(string artifactId, CancellationToken cancellationToken = default);
    /// <summary>預覽 Skill 部署計畫。</summary>
    Task<MarketplaceDeploymentPlan> PreviewSkillAsync(IReadOnlyList<MarketplaceDeploymentRequest> requests, CancellationToken cancellationToken = default);
    /// <summary>列出指定 artifact 的部署狀態。</summary>
    Task<IReadOnlyList<MarketplaceDeploymentState>> ListDeploymentStatesAsync(string artifactId, CancellationToken cancellationToken = default);
}

/// <summary>只寫入外部 Agent 的 MCP 設定，不啟動或探測 MCP 程序。</summary>
public interface IMarketplaceMcpDeploymentService
{
    /// <summary>依部署要求寫入 MCP 設定。</summary>
    Task<MarketplaceDeploymentBatchResult> ConfigureAsync(IReadOnlyList<MarketplaceDeploymentRequest> requests, CancellationToken cancellationToken = default);
    /// <summary>從所有受管理目標移除 MCP 設定。</summary>
    Task<MarketplaceDeploymentBatchResult> RemoveFromAllManagedTargetsAsync(string artifactId, CancellationToken cancellationToken = default);
    /// <summary>預覽 MCP 設定計畫。</summary>
    Task<MarketplaceDeploymentPlan> PreviewAsync(IReadOnlyList<MarketplaceDeploymentRequest> requests, CancellationToken cancellationToken = default);
}

/// <summary>從 GitHub 儲存庫匯入 Marketplace artifact。</summary>
public interface IGitHubRepositoryImportService
{
    /// <summary>匯入指定的 GitHub 儲存庫及版本。</summary>
    Task<GitHubRepositoryImportResult> ImportAsync(string repositoryUrl, string? reference, CancellationToken cancellationToken = default);
}
