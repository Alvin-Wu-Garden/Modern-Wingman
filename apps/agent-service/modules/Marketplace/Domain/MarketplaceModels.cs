namespace Wingman.Marketplace.Domain;

/// <summary>Marketplace artifact 的類型。</summary>
public enum MarketplaceArtifactKind
{
    /// <summary>只用於尚未分類的搜尋候選，不允許匯入或部署。</summary>
    Unknown,
    Skill,
    McpServer,
}

/// <summary>Marketplace 探索或匯入流程的狀態。</summary>
public enum MarketplaceDiscoveryStatus
{
    Discovered,
    Scored,
    Stale,
    Resolving,
    Resolved,
    ManualSetupRequired,
    ManualReviewRequired,
    Invalid,
}

/// <summary>Marketplace 資料來源的類型。</summary>
public enum MarketplaceSourceKind
{
    GitHubDiscovery,
    GitHubRepository,
    LocalFolder,
}

/// <summary>artifact 類型分類結果的可信度。</summary>
public enum MarketplaceClassificationConfidence
{
    Unknown,
    Inferred,
    Declared,
    Verified,
}

/// <summary>Marketplace 探索結果及其分類、評分與同步狀態。</summary>
public sealed record MarketplaceDiscoveryRecord(
    string Id,
    string SourceId,
    string? GitHubNodeId,
    string CanonicalUrl,
    string Owner,
    string Repository,
    string Name,
    string? Description,
    MarketplaceArtifactKind SuggestedKind,
    MarketplaceClassificationConfidence ClassificationConfidence,
    string PrimaryCategory,
    IReadOnlyList<string> SecondaryCategories,
    IReadOnlyList<string> Topics,
    string? License,
    bool IsArchived,
    int Stars,
    int Forks,
    DateTimeOffset? GitHubUpdatedAt,
    DateTimeOffset? PushedAt,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    int ConsecutiveMissCount,
    MarketplaceDiscoveryStatus Status,
    string MetadataFingerprint,
    double DiscoveryScore,
    string DiscoveryScoreProfileId,
    string? ArtifactQualityScoreProfileId = null,
    double? ArtifactQualityScore = null,
    bool IsFavorite = false,
    bool IsManualSource = false);

/// <summary>Marketplace 探索或匯入來源的描述。</summary>
public sealed record MarketplaceSource(
    string Id,
    MarketplaceSourceKind Kind,
    string DisplayName,
    string Location,
    bool IsEnabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastSyncedAt = null);

/// <summary>一次探索結果的評分快照。</summary>
public sealed record MarketplaceScoreSnapshot(
    string Id,
    string DiscoveryRecordId,
    string ScoreKind,
    string ProfileId,
    double TotalScore,
    IReadOnlyDictionary<string, double> Components,
    string EvidenceJson,
    DateTimeOffset ComputedAt);

/// <summary>一次 artifact 品質評估的快照。</summary>
public sealed record MarketplaceArtifactScoreSnapshot(
    string Id,
    string ArtifactId,
    string ProfileId,
    double TotalScore,
    IReadOnlyDictionary<string, double> Components,
    string EvidenceJson,
    DateTimeOffset ComputedAt);

/// <summary>Marketplace 同步作業的結果摘要。</summary>
public sealed record MarketplaceRefreshResult(
    string SyncRunId,
    int NewCount,
    int UpdatedCount,
    int UnchangedCount,
    int StaleCount,
    int PrunedCount,
    int SuccessfulQueries,
    int TotalQueries,
    bool IsPartial,
    DateTimeOffset CompletedAt);

/// <summary>Marketplace 探索結果的篩選與分頁條件。</summary>
public sealed record MarketplaceListFilter(
    MarketplaceArtifactKind? Kind = null,
    string? Search = null,
    string? Category = null,
    bool IncludeStale = false,
    bool IncludeUnsupported = false,
    int Take = 100,
    int Skip = 0);

/// <summary>Marketplace 探索結果的分頁資料。</summary>
public sealed record MarketplacePage(
    IReadOnlyList<MarketplaceDiscoveryRecord> Items,
    int TotalCount);

/// <summary>解析本機來源後產生的 artifact 候選項目。</summary>
public sealed record MarketplaceArtifactCandidate(
    string Id,
    string SourceLocation,
    string ArtifactPath,
    MarketplaceArtifactKind Kind,
    string DisplayName,
    MarketplaceDiscoveryStatus Status,
    string? ValidationProfileId,
    string? ValidationMessage,
    DateTimeOffset CreatedAt);

/// <summary>已匯入並保存快照的 Marketplace artifact。</summary>
public sealed record MarketplaceArtifact(
    string Id,
    string CandidateId,
    MarketplaceArtifactKind Kind,
    string DisplayName,
    string SnapshotPath,
    string ContentHash,
    MarketplaceDiscoveryStatus Status,
    string? ValidationProfileId,
    DateTimeOffset ImportedAt);

/// <summary>一次 Marketplace 匯入作業的結果。</summary>
public sealed record MarketplaceImportResult(
    string SourceLocation,
    IReadOnlyList<MarketplaceArtifactCandidate> Candidates,
    IReadOnlyList<MarketplaceArtifact> Artifacts);

/// <summary>artifact 部署的作用範圍。</summary>
public enum MarketplaceDeploymentScope
{
    Global,
    Project,
}

/// <summary>可供 Marketplace 部署的外部 Agent 目標描述。</summary>
public sealed record MarketplaceTargetDescriptor(
    string Id,
    string DisplayName,
    bool SupportsSkill,
    bool SupportsMcp,
    bool SupportsGlobalScope,
    bool SupportsProjectScope,
    bool IsDetected = false,
    string? DetectionReason = null,
    // MCP 的 global／project 設定位置不一定同時存在，必須分開揭露給 UI。
    bool SupportsGlobalMcp = false,
    bool SupportsProjectMcp = false);

/// <summary>單一 artifact 部署要求。</summary>
public sealed record MarketplaceDeploymentRequest(
    string ArtifactId,
    string TargetId,
    MarketplaceDeploymentScope Scope,
    string? ProjectPath = null);

/// <summary>單一部署目標的執行結果。</summary>
public sealed record MarketplaceDeploymentResult(
    string TargetId,
    MarketplaceDeploymentScope Scope,
    string Status,
    string? TargetPath,
    string? Message);

/// <summary>一批部署要求的執行結果。</summary>
public sealed record MarketplaceDeploymentBatchResult(
    IReadOnlyList<MarketplaceDeploymentResult> Results)
{
    public bool IsPartialSuccess => Results.Any(result => result.Status == "Deployed") && Results.Any(result => result.Status != "Deployed");
}

/// <summary>針對明確 artifact、目標與作用範圍選擇所保存的可相容性結果。</summary>
public sealed record MarketplaceInstallabilityResult(
    string ArtifactId,
    string TargetId,
    MarketplaceDeploymentScope Scope,
    string Status,
    string? TargetPath,
    string? Reason,
    DateTimeOffset ComputedAt);

/// <summary>不會產生副作用的部署或設定計畫，必須由呼叫端明確執行。</summary>
public sealed record MarketplaceDeploymentPlan(
    string ArtifactId,
    string Operation,
    IReadOnlyList<MarketplaceInstallabilityResult> Items);

/// <summary>已部署 artifact 的目前狀態。</summary>
public sealed record MarketplaceDeploymentState(
    string TargetId,
    MarketplaceDeploymentScope Scope,
    string? ProjectPath,
    string TargetPath,
    string DeployedHash,
    string Status,
    DateTimeOffset UpdatedAt);

/// <summary>artifact 與其來源位置的對應資料。</summary>
public sealed record MarketplaceArtifactSource(MarketplaceArtifact Artifact, string SourceLocation);

/// <summary>從 GitHub 儲存庫匯入 artifact 的結果。</summary>
public sealed record GitHubRepositoryImportResult(
    string CanonicalUrl,
    string RequestedRef,
    string CommitSha,
    MarketplaceImportResult Import);
