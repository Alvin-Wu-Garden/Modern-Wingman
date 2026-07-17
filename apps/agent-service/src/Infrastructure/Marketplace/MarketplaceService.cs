using Wingman.Marketplace.Application;
using Wingman.Marketplace.Contracts;
using Wingman.Marketplace.Domain;

namespace AgentService.Infrastructure.Marketplace;

/// <summary>Application coordinator：只協調 provider、classification、score 與 store，不知道 HTTP 或 SQLite 細節。</summary>
public sealed class MarketplaceService(
    IEnumerable<IDiscoveryProvider> discoveryProviders,
    IMarketplaceStore store,
    MarketplaceDiscoveryClassifier classifier,
    MarketplaceDiscoveryScorer scorer,
    IMarketplaceActivityRecorder? activity = null) : IMarketplaceService
{
    private static readonly DiscoveryQuery[] Queries =
    [
        new("skills", "topic:agent-skill", MarketplaceArtifactKind.Skill, 100),
        new("mcp", "topic:mcp", MarketplaceArtifactKind.McpServer, 100),
    ];

    public Task<MarketplacePage> ListAsync(MarketplaceListFilter filter, CancellationToken cancellationToken = default)
        => store.ListDiscoveryAsync(filter with { Take = Math.Clamp(filter.Take, 1, 200) }, cancellationToken);

    public Task<MarketplaceDiscoveryRecord?> GetAsync(string id, CancellationToken cancellationToken = default)
        => store.GetDiscoveryAsync(id, cancellationToken);

    public Task SetFavoriteAsync(string id, bool isFavorite, CancellationToken cancellationToken = default)
        => store.SetFavoriteAsync(id, isFavorite, cancellationToken);

    public async Task<MarketplaceRefreshResult> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var syncRunId = await store.StartRefreshAsync(cancellationToken);
        if (activity is not null)
            await activity.RecordAsync(new(Guid.NewGuid().ToString("N"), syncRunId, "discovery-refresh", "Started", null, null, null, DateTimeOffset.UtcNow), cancellationToken);
        var provider = discoveryProviders.SingleOrDefault(candidate => candidate.ProviderId == "github-discovery")
            ?? throw new InvalidOperationException("GitHub Discovery provider is not registered.");
        var now = DateTimeOffset.UtcNow;
        var records = new List<MarketplaceDiscoveryRecord>();
        var snapshots = new List<MarketplaceScoreSnapshot>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var newCount = 0;
        var updatedCount = 0;
        var unchangedCount = 0;
        var successful = 0;
        var partial = false;
        Exception? prerequisite = null;

        foreach (var query in Queries)
        {
            IReadOnlyList<DiscoveryCandidate> candidates;
            try
            {
                candidates = await provider.DiscoverAsync(query, cancellationToken);
                successful++;
            }
            catch (MarketplacePrerequisiteException ex)
            {
                prerequisite = ex;
                partial = true;
                break;
            }
            catch when (!cancellationToken.IsCancellationRequested)
            {
                partial = true;
                continue;
            }

            foreach (var candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate.GitHubNodeId) || !seen.Add(candidate.GitHubNodeId)) continue;
                var existing = await store.GetDiscoveryByGitHubNodeIdAsync(candidate.GitHubNodeId, cancellationToken);
                var fingerprint = MarketplaceDiscoveryScorer.MetadataFingerprint(candidate);
                if (existing is not null && existing.MetadataFingerprint == fingerprint)
                {
                    records.Add(existing with { LastSeenAt = now, ConsecutiveMissCount = 0, Status = MarketplaceDiscoveryStatus.Scored });
                    unchangedCount++;
                    continue;
                }

                var classified = classifier.Classify(candidate);
                var id = existing?.Id ?? Guid.NewGuid().ToString("N");
                var snapshot = scorer.Score(id, candidate, now);
                var record = new MarketplaceDiscoveryRecord(
                    id,
                    "github-discovery",
                    candidate.GitHubNodeId,
                    candidate.CanonicalUrl,
                    candidate.Owner,
                    candidate.Repository,
                    candidate.Name,
                    candidate.Description,
                    classified.Kind,
                    classified.Confidence,
                    classified.Category,
                    classified.SecondaryCategories,
                    candidate.Topics,
                    candidate.License,
                    candidate.IsArchived,
                    candidate.Stars,
                    candidate.Forks,
                    candidate.GitHubUpdatedAt,
                    candidate.PushedAt,
                    existing?.FirstSeenAt ?? now,
                    now,
                    0,
                    MarketplaceDiscoveryStatus.Scored,
                    fingerprint,
                    snapshot.TotalScore,
                    snapshot.ProfileId,
                    existing?.ArtifactQualityScoreProfileId,
                    existing?.ArtifactQualityScore,
                    existing?.IsFavorite ?? false,
                    false);
                records.Add(record);
                snapshots.Add(snapshot);
                if (existing is null) newCount++; else updatedCount++;
            }
        }

        if (prerequisite is not null && successful == 0)
        {
            await store.FailRefreshAsync(syncRunId, prerequisite.Message, cancellationToken);
            if (activity is not null)
                await activity.RecordAsync(new(Guid.NewGuid().ToString("N"), syncRunId, "discovery-refresh", "Failed", null, null, prerequisite.Message, DateTimeOffset.UtcNow), cancellationToken);
            throw prerequisite;
        }

        await store.UpsertDiscoveryAsync(records, snapshots, cancellationToken);
        var result = await store.CompleteRefreshAsync(syncRunId, seen, newCount, updatedCount, unchangedCount,
            successful, Queries.Length, partial || successful != Queries.Length, cancellationToken);
        if (activity is not null)
            await activity.RecordAsync(new(Guid.NewGuid().ToString("N"), syncRunId, "discovery-refresh", result.IsPartial ? "Partial" : "Completed", null, null,
                $"new={result.NewCount};updated={result.UpdatedCount};unchanged={result.UnchangedCount}", DateTimeOffset.UtcNow), cancellationToken);
        return result;
    }
}
