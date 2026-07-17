namespace Wingman.Marketplace.Domain;

public enum MarketplaceArtifactKind
{
    Unknown,
    Skill,
    McpServer,
    WingmanPlugin,
    UnsupportedProject,
}

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

public enum MarketplaceSourceKind
{
    GitHubDiscovery,
    GitHubRepository,
    LocalFolder,
    LocalArchive,
    CodexMarketplaceImport,
}

public enum MarketplaceClassificationConfidence
{
    Unknown,
    Inferred,
    Declared,
    Verified,
}

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

public sealed record MarketplaceSource(
    string Id,
    MarketplaceSourceKind Kind,
    string DisplayName,
    string Location,
    bool IsEnabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastSyncedAt = null);

public sealed record MarketplaceScoreSnapshot(
    string Id,
    string DiscoveryRecordId,
    string ScoreKind,
    string ProfileId,
    double TotalScore,
    IReadOnlyDictionary<string, double> Components,
    string EvidenceJson,
    DateTimeOffset ComputedAt);

public sealed record MarketplaceArtifactScoreSnapshot(
    string Id,
    string ArtifactId,
    string ProfileId,
    double TotalScore,
    IReadOnlyDictionary<string, double> Components,
    string EvidenceJson,
    DateTimeOffset ComputedAt);

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

public sealed record MarketplaceListFilter(
    MarketplaceArtifactKind? Kind = null,
    string? Search = null,
    string? Category = null,
    bool IncludeStale = false,
    bool IncludeUnsupported = false,
    int Take = 100,
    int Skip = 0);

public sealed record MarketplacePage(
    IReadOnlyList<MarketplaceDiscoveryRecord> Items,
    int TotalCount);

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

public sealed record MarketplaceImportResult(
    string SourceLocation,
    IReadOnlyList<MarketplaceArtifactCandidate> Candidates,
    IReadOnlyList<MarketplaceArtifact> Artifacts);

public enum MarketplaceDeploymentScope
{
    Global,
    Project,
}

public sealed record MarketplaceTargetDescriptor(
    string Id,
    string DisplayName,
    bool SupportsSkill,
    bool SupportsMcp,
    bool SupportsGlobalScope,
    bool SupportsProjectScope,
    bool IsDetected = false,
    string? DetectionReason = null);

public sealed record MarketplaceDeploymentRequest(
    string ArtifactId,
    string TargetId,
    MarketplaceDeploymentScope Scope,
    string? ProjectPath = null);

public sealed record MarketplaceDeploymentResult(
    string TargetId,
    MarketplaceDeploymentScope Scope,
    string Status,
    string? TargetPath,
    string? Message);

public sealed record MarketplaceDeploymentBatchResult(
    IReadOnlyList<MarketplaceDeploymentResult> Results)
{
    public bool IsPartialSuccess => Results.Any(result => result.Status == "Deployed") && Results.Any(result => result.Status != "Deployed");
}

/// <summary>Persisted compatibility outcome for one explicit artifact/target/scope selection.</summary>
public sealed record MarketplaceInstallabilityResult(
    string ArtifactId,
    string TargetId,
    MarketplaceDeploymentScope Scope,
    string Status,
    string? TargetPath,
    string? Reason,
    DateTimeOffset ComputedAt);

/// <summary>A side-effect-free deployment/configuration plan. The caller must explicitly execute it.</summary>
public sealed record MarketplaceDeploymentPlan(
    string ArtifactId,
    string Operation,
    IReadOnlyList<MarketplaceInstallabilityResult> Items);

public sealed record MarketplaceDeploymentState(
    string TargetId,
    MarketplaceDeploymentScope Scope,
    string? ProjectPath,
    string TargetPath,
    string DeployedHash,
    string Status,
    DateTimeOffset UpdatedAt);

public sealed record MarketplacePluginInstallation(
    string Id,
    string ArtifactId,
    string PluginId,
    string Version,
    bool Enabled,
    string InstalledPath,
    DateTimeOffset InstalledAt);

public sealed record MarketplacePluginPreview(
    string InstallationId,
    string PluginId,
    string Version,
    IReadOnlyList<string> SkillPaths,
    IReadOnlyList<string> McpPaths,
    IReadOnlyList<string> FunctionIds,
    IReadOnlyList<string> HookIds,
    string SafetySummary);

/// <summary>Configuration metadata only; values are never returned to the Desktop client.</summary>
public sealed record MarketplacePluginConfiguration(
    string InstallationId,
    string PluginId,
    IReadOnlyList<MarketplacePluginConfigurationField> Fields);

public sealed record MarketplacePluginConfigurationField(
    string Name,
    bool IsSecret,
    bool IsConfigured);

public sealed record EnabledPluginCapabilities(
    string PluginId,
    string Version,
    IReadOnlyList<string> SkillPaths,
    IReadOnlyList<string> McpPaths,
    IReadOnlyList<string> FunctionIds,
    IReadOnlyList<string> HookIds,
    string? InstalledPath = null);

public sealed record CodexMarketplaceImportResult(
    int ImportedArtifacts,
    int InvalidCandidates,
    IReadOnlyList<string> SkippedEntries);

public sealed record MarketplaceArtifactSource(MarketplaceArtifact Artifact, string SourceLocation);

public sealed record MarketplaceArtifactUpdate(
    string ArtifactId,
    string DisplayName,
    string SourceLocation,
    string? InstalledCommitSha,
    string Status,
    string? AvailableCommitSha = null,
    string? Message = null);

/// <summary>Immutable result of a user-initiated update check. Checks never alter an artifact.</summary>
public sealed record MarketplaceUpdateCheck(
    string Id,
    string ArtifactId,
    string SourceLocation,
    string? InstalledCommitSha,
    string Status,
    string? AvailableCommitSha,
    string? Message,
    DateTimeOffset CheckedAt);

/// <summary>Result of an explicit update import. Deployments are deliberately never changed here.</summary>
public sealed record MarketplaceArtifactUpdateApplicationResult(
    string ArtifactId,
    string PreviousCommitSha,
    string ImportedCommitSha,
    MarketplaceImportResult Import);

/// <summary>
/// Local Marketplace activity projected to the existing run-event timeline under
/// <c>marketplace:&lt;operationId&gt;</c>. It contains metadata only; never credentials or file contents.
/// </summary>
public sealed record MarketplaceActivityEvent(
    string Id,
    string OperationId,
    string EventType,
    string Status,
    string? ArtifactId,
    string? TargetId,
    string? Detail,
    DateTimeOffset OccurredAt);

public sealed record GitHubRepositoryImportResult(
    string CanonicalUrl,
    string RequestedRef,
    string CommitSha,
    MarketplaceImportResult Import);
