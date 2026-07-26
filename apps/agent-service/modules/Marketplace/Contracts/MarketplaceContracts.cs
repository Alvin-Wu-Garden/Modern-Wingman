using Wingman.Marketplace.Domain;

namespace Wingman.Marketplace.Contracts;

public sealed record DiscoveryQuery(
    string QueryId,
    string QueryText,
    MarketplaceArtifactKind KindHint,
    int MaxResults);

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

public interface IDiscoveryProvider
{
    string ProviderId { get; }
    Task<IReadOnlyList<DiscoveryCandidate>> DiscoverAsync(
        DiscoveryQuery query,
        CancellationToken cancellationToken = default);
}

public interface IMarketplaceStore
{
    Task<MarketplacePage> ListDiscoveryAsync(MarketplaceListFilter filter, CancellationToken cancellationToken = default);
    Task<MarketplaceDiscoveryRecord?> GetDiscoveryAsync(string id, CancellationToken cancellationToken = default);
    Task<MarketplaceDiscoveryRecord?> GetDiscoveryByGitHubNodeIdAsync(string gitHubNodeId, CancellationToken cancellationToken = default);
    Task UpsertDiscoveryAsync(
        IReadOnlyList<MarketplaceDiscoveryRecord> records,
        IReadOnlyList<MarketplaceScoreSnapshot> scoreSnapshots,
        CancellationToken cancellationToken = default);
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
    Task<string> StartRefreshAsync(CancellationToken cancellationToken = default);
    Task FailRefreshAsync(string syncRunId, string error, CancellationToken cancellationToken = default);
    Task SetFavoriteAsync(string id, bool isFavorite, CancellationToken cancellationToken = default);
}

public interface IMarketplaceService
{
    Task<MarketplacePage> ListAsync(MarketplaceListFilter filter, CancellationToken cancellationToken = default);
    Task<MarketplaceDiscoveryRecord?> GetAsync(string id, CancellationToken cancellationToken = default);
    Task<MarketplaceRefreshResult> RefreshAsync(CancellationToken cancellationToken = default);
    Task SetFavoriteAsync(string id, bool isFavorite, CancellationToken cancellationToken = default);
}

public interface IArtifactResolver
{
    Task<IReadOnlyList<MarketplaceArtifactCandidate>> ResolveFolderAsync(
        string sourceFolder,
        CancellationToken cancellationToken = default);
}

public interface IMarketplaceArtifactStore
{
    Task SaveImportAsync(
        IReadOnlyList<MarketplaceArtifactCandidate> candidates,
        IReadOnlyList<MarketplaceArtifact> artifacts,
        IReadOnlyList<MarketplaceArtifactScoreSnapshot> scoreSnapshots,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MarketplaceArtifact>> ListArtifactsAsync(CancellationToken cancellationToken = default);
    Task<MarketplaceArtifact?> GetArtifactAsync(string id, CancellationToken cancellationToken = default);
    Task<MarketplaceArtifact?> GetArtifactByContentHashAsync(string contentHash, MarketplaceArtifactKind kind, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MarketplaceArtifactSource>> ListArtifactSourcesAsync(CancellationToken cancellationToken = default);
}

public interface IMarketplaceArtifactService
{
    Task<MarketplaceImportResult> ImportFolderAsync(string sourceFolder, CancellationToken cancellationToken = default);
    Task<MarketplaceImportResult> ImportArchiveAsync(string archivePath, string? sourceLocation = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MarketplaceArtifact>> ListArtifactsAsync(CancellationToken cancellationToken = default);
}

public interface IAgentTargetAdapter
{
    MarketplaceTargetDescriptor Descriptor { get; }
    /// <summary>JSON MCP 設定中保存 server map 的根屬性。</summary>
    string McpRootProperty { get; }
    string ResolveSkillDirectory(MarketplaceDeploymentScope scope, string? projectPath);
    string ResolveMcpConfigPath(MarketplaceDeploymentScope scope, string? projectPath);
}

public interface IMarketplaceDeploymentStore
{
    Task SaveDeploymentAsync(string artifactId, MarketplaceDeploymentRequest request, string targetPath, string deployedHash, string status, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<(string TargetId, MarketplaceDeploymentScope Scope, string TargetPath, string DeployedHash, string Status)>> ListDeploymentsAsync(string artifactId, CancellationToken cancellationToken = default);
    Task UpdateDeploymentStatusAsync(string artifactId, string targetId, MarketplaceDeploymentScope scope, string status, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MarketplaceDeploymentState>> ListDeploymentStatesAsync(string artifactId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<MarketplaceDeploymentState>>([]);
}

public interface IMarketplaceInstallabilityStore
{
    Task SaveAsync(IReadOnlyList<MarketplaceInstallabilityResult> results, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MarketplaceInstallabilityResult>> ListAsync(string artifactId, CancellationToken cancellationToken = default);
}

public interface IMarketplaceDeploymentService
{
    Task<IReadOnlyList<MarketplaceTargetDescriptor>> ListTargetsAsync(CancellationToken cancellationToken = default);
    Task<MarketplaceDeploymentBatchResult> DeploySkillAsync(IReadOnlyList<MarketplaceDeploymentRequest> requests, CancellationToken cancellationToken = default);
    Task<MarketplaceDeploymentBatchResult> RemoveFromAllManagedTargetsAsync(string artifactId, CancellationToken cancellationToken = default);
    Task<MarketplaceDeploymentPlan> PreviewSkillAsync(IReadOnlyList<MarketplaceDeploymentRequest> requests, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MarketplaceDeploymentState>> ListDeploymentStatesAsync(string artifactId, CancellationToken cancellationToken = default);
}

/// <summary>Writes an external Agent's MCP configuration only. It never starts or probes an MCP process.</summary>
public interface IMarketplaceMcpDeploymentService
{
    Task<MarketplaceDeploymentBatchResult> ConfigureAsync(IReadOnlyList<MarketplaceDeploymentRequest> requests, CancellationToken cancellationToken = default);
    Task<MarketplaceDeploymentBatchResult> RemoveFromAllManagedTargetsAsync(string artifactId, CancellationToken cancellationToken = default);
    Task<MarketplaceDeploymentPlan> PreviewAsync(IReadOnlyList<MarketplaceDeploymentRequest> requests, CancellationToken cancellationToken = default);
}

public interface IGitHubRepositoryImportService
{
    Task<GitHubRepositoryImportResult> ImportAsync(string repositoryUrl, string? reference, CancellationToken cancellationToken = default);
}
