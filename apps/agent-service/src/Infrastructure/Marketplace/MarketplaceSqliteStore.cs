using System.Data;
using System.Text.Json;
using AgentService.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Wingman.Marketplace.Contracts;
using Wingman.Marketplace.Domain;

namespace AgentService.Infrastructure.Marketplace;

/// <summary>
/// Marketplace 的唯一 SQLite persistence adapter。所有存取皆經 IDbContextFactory，
/// 因此不會共享非 thread-safe 的 DbContext，也不影響既有 Tauri Skill tables。
/// </summary>
public sealed class MarketplaceSqliteStore(IDbContextFactory<AppDbContext> factory) : IMarketplaceStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<MarketplacePage> ListDiscoveryAsync(MarketplaceListFilter filter, CancellationToken cancellationToken = default)
    {
        var take = Math.Clamp(filter.Take, 1, 200);
        var skip = Math.Max(0, filter.Skip);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        var where = BuildWhere(command, filter);
        command.CommandText = $"""
            SELECT Id,SourceId,GitHubNodeId,CanonicalUrl,Owner,Repository,Name,Description,SuggestedKind,
                   ClassificationConfidence,PrimaryCategory,SecondaryCategoriesJson,TopicsJson,License,IsArchived,
                   Stars,Forks,GitHubUpdatedAt,PushedAt,FirstSeenAt,LastSeenAt,ConsecutiveMissCount,Status,
                   MetadataFingerprint,DiscoveryScore,DiscoveryScoreProfileId,ArtifactQualityScoreProfileId,
                   ArtifactQualityScore,IsFavorite,IsManualSource
            FROM discovery_records {where}
            ORDER BY IsFavorite DESC, DiscoveryScore DESC, LastSeenAt DESC
            LIMIT $take OFFSET $skip;
            """;
        Add(command, "$take", take);
        Add(command, "$skip", skip);
        if (command.Connection!.State != ConnectionState.Open) await command.Connection.OpenAsync(cancellationToken);
        var records = new List<MarketplaceDiscoveryRecord>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) records.Add(Map(reader));

        await using var countCommand = db.Database.GetDbConnection().CreateCommand();
        var countWhere = BuildWhere(countCommand, filter);
        countCommand.CommandText = $"SELECT COUNT(*) FROM discovery_records {countWhere};";
        if (countCommand.Connection!.State != ConnectionState.Open) await countCommand.Connection.OpenAsync(cancellationToken);
        var total = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken));
        return new(records, total);
    }

    public Task<MarketplaceDiscoveryRecord?> GetDiscoveryAsync(string id, CancellationToken cancellationToken = default)
        => GetSingleAsync("Id=$value", id, cancellationToken);

    public Task<MarketplaceDiscoveryRecord?> GetDiscoveryByGitHubNodeIdAsync(string gitHubNodeId, CancellationToken cancellationToken = default)
        => GetSingleAsync("GitHubNodeId=$value", gitHubNodeId, cancellationToken);

    public async Task UpsertDiscoveryAsync(
        IReadOnlyList<MarketplaceDiscoveryRecord> records,
        IReadOnlyList<MarketplaceScoreSnapshot> scoreSnapshots,
        CancellationToken cancellationToken = default)
    {
        if (records.Count == 0 && scoreSnapshots.Count == 0) return;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.Database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var record in records)
            {
                await using var command = db.Database.GetDbConnection().CreateCommand();
                command.Transaction = transaction.GetDbTransaction();
                command.CommandText = """
                    INSERT INTO discovery_records (
                        Id,SourceId,GitHubNodeId,CanonicalUrl,Owner,Repository,Name,Description,SuggestedKind,
                        ClassificationConfidence,PrimaryCategory,SecondaryCategoriesJson,TopicsJson,License,IsArchived,
                        Stars,Forks,GitHubUpdatedAt,PushedAt,FirstSeenAt,LastSeenAt,ConsecutiveMissCount,Status,
                        MetadataFingerprint,DiscoveryScore,DiscoveryScoreProfileId,ArtifactQualityScoreProfileId,
                        ArtifactQualityScore,IsFavorite,IsManualSource)
                    VALUES ($id,$sourceId,$nodeId,$url,$owner,$repository,$name,$description,$kind,$confidence,
                        $category,$secondary,$topics,$license,$archived,$stars,$forks,$githubUpdated,$pushed,
                        $firstSeen,$lastSeen,$misses,$status,$fingerprint,$score,$scoreProfile,$artifactProfile,
                        $artifactScore,$favorite,$manual)
                    ON CONFLICT(GitHubNodeId) WHERE GitHubNodeId IS NOT NULL DO UPDATE SET
                        SourceId=excluded.SourceId,CanonicalUrl=excluded.CanonicalUrl,Owner=excluded.Owner,
                        Repository=excluded.Repository,Name=excluded.Name,Description=excluded.Description,
                        SuggestedKind=excluded.SuggestedKind,ClassificationConfidence=excluded.ClassificationConfidence,
                        PrimaryCategory=excluded.PrimaryCategory,SecondaryCategoriesJson=excluded.SecondaryCategoriesJson,
                        TopicsJson=excluded.TopicsJson,License=excluded.License,IsArchived=excluded.IsArchived,
                        Stars=excluded.Stars,Forks=excluded.Forks,GitHubUpdatedAt=excluded.GitHubUpdatedAt,
                        PushedAt=excluded.PushedAt,LastSeenAt=excluded.LastSeenAt,ConsecutiveMissCount=excluded.ConsecutiveMissCount,
                        Status=excluded.Status,MetadataFingerprint=excluded.MetadataFingerprint,DiscoveryScore=excluded.DiscoveryScore,
                        DiscoveryScoreProfileId=excluded.DiscoveryScoreProfileId,
                        ArtifactQualityScoreProfileId=excluded.ArtifactQualityScoreProfileId,
                        ArtifactQualityScore=excluded.ArtifactQualityScore,IsManualSource=excluded.IsManualSource;
                    """;
                BindRecord(command, record);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var snapshot in scoreSnapshots)
            {
                await using var command = db.Database.GetDbConnection().CreateCommand();
                command.Transaction = transaction.GetDbTransaction();
                command.CommandText = """
                    INSERT INTO discovery_score_snapshots
                    (Id,DiscoveryRecordId,ScoreKind,ProfileId,TotalScore,ComponentsJson,EvidenceJson,ComputedAt)
                    VALUES ($id,$recordId,$kind,$profile,$total,$components,$evidence,$computedAt);
                    """;
                Add(command, "$id", snapshot.Id);
                Add(command, "$recordId", snapshot.DiscoveryRecordId);
                Add(command, "$kind", snapshot.ScoreKind);
                Add(command, "$profile", snapshot.ProfileId);
                Add(command, "$total", snapshot.TotalScore);
                Add(command, "$components", JsonSerializer.Serialize(snapshot.Components, Json));
                Add(command, "$evidence", snapshot.EvidenceJson);
                Add(command, "$computedAt", ToDb(snapshot.ComputedAt));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<string> StartRefreshAsync(CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid().ToString("N");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO marketplace_sync_runs (Id,Status,StartedAt) VALUES ({0},'running',{1});",
            [id, ToDb(DateTimeOffset.UtcNow)!], cancellationToken);
        return id;
    }

    public async Task<MarketplaceRefreshResult> CompleteRefreshAsync(
        string syncRunId,
        IReadOnlyCollection<string> seenGitHubNodeIds,
        int newCount,
        int updatedCount,
        int unchangedCount,
        int successfulQueries,
        int totalQueries,
        bool isPartial,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var staleCount = 0;
        var prunedCount = 0;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.Database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            if (!isPartial)
            {
                var seen = seenGitHubNodeIds.Count == 0 ? "''" : string.Join(',', seenGitHubNodeIds.Select((_, index) => $"$node{index}"));
                await using var miss = db.Database.GetDbConnection().CreateCommand();
                miss.Transaction = transaction.GetDbTransaction();
                miss.CommandText = $"""
                    UPDATE discovery_records
                    SET ConsecutiveMissCount=ConsecutiveMissCount+1,
                        Status=CASE WHEN ConsecutiveMissCount+1 >= 3 THEN 'Stale' ELSE Status END
                    WHERE SourceId='github-discovery' AND GitHubNodeId NOT IN ({seen});
                    """;
                var index = 0;
                foreach (var nodeId in seenGitHubNodeIds) Add(miss, $"$node{index++}", nodeId);
                await miss.ExecuteNonQueryAsync(cancellationToken);

                await using var stale = db.Database.GetDbConnection().CreateCommand();
                stale.Transaction = transaction.GetDbTransaction();
                stale.CommandText = "SELECT COUNT(*) FROM discovery_records WHERE SourceId='github-discovery' AND Status='Stale';";
                staleCount = Convert.ToInt32(await stale.ExecuteScalarAsync(cancellationToken));

                await using var count = db.Database.GetDbConnection().CreateCommand();
                count.Transaction = transaction.GetDbTransaction();
                count.CommandText = "SELECT COUNT(*) FROM discovery_records;";
                var overflow = Math.Max(0, Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken)) - 5000);
                if (overflow > 0)
                {
                    await using var prune = db.Database.GetDbConnection().CreateCommand();
                    prune.Transaction = transaction.GetDbTransaction();
                    prune.CommandText = """
                        DELETE FROM discovery_records WHERE Id IN (
                          SELECT Id FROM discovery_records
                          WHERE Status='Stale' AND IsFavorite=0 AND IsManualSource=0
                          ORDER BY DiscoveryScore ASC, LastSeenAt ASC LIMIT $limit
                        );
                        """;
                    Add(prune, "$limit", overflow);
                    prunedCount = await prune.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            await using var complete = db.Database.GetDbConnection().CreateCommand();
            complete.Transaction = transaction.GetDbTransaction();
            complete.CommandText = """
                UPDATE marketplace_sync_runs
                SET Status=$status,NewCount=$new,UpdatedCount=$updated,UnchangedCount=$unchanged,
                    StaleCount=$stale,PrunedCount=$pruned,SuccessfulQueries=$successful,TotalQueries=$total,
                    CompletedAt=$completed,Error=NULL
                WHERE Id=$id;
                """;
            Add(complete, "$status", isPartial ? "partial_success" : "completed");
            Add(complete, "$new", newCount); Add(complete, "$updated", updatedCount);
            Add(complete, "$unchanged", unchangedCount); Add(complete, "$stale", staleCount);
            Add(complete, "$pruned", prunedCount); Add(complete, "$successful", successfulQueries);
            Add(complete, "$total", totalQueries); Add(complete, "$completed", ToDb(now)); Add(complete, "$id", syncRunId);
            await complete.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }

        return new(syncRunId, newCount, updatedCount, unchangedCount, staleCount, prunedCount,
            successfulQueries, totalQueries, isPartial, now);
    }

    public async Task FailRefreshAsync(string syncRunId, string error, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE marketplace_sync_runs SET Status='failed', CompletedAt={0}, Error={1} WHERE Id={2};",
            [ToDb(DateTimeOffset.UtcNow)!, error[..Math.Min(error.Length, 500)], syncRunId], cancellationToken);
    }

    public async Task SetFavoriteAsync(string id, bool isFavorite, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var changed = await db.Database.ExecuteSqlRawAsync(
            "UPDATE discovery_records SET IsFavorite={0} WHERE Id={1};", [isFavorite ? 1 : 0, id], cancellationToken);
        if (changed == 0) throw new KeyNotFoundException("Marketplace discovery record was not found.");
    }

    private async Task<MarketplaceDiscoveryRecord?> GetSingleAsync(string condition, string value, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"""
            SELECT Id,SourceId,GitHubNodeId,CanonicalUrl,Owner,Repository,Name,Description,SuggestedKind,
                   ClassificationConfidence,PrimaryCategory,SecondaryCategoriesJson,TopicsJson,License,IsArchived,
                   Stars,Forks,GitHubUpdatedAt,PushedAt,FirstSeenAt,LastSeenAt,ConsecutiveMissCount,Status,
                   MetadataFingerprint,DiscoveryScore,DiscoveryScoreProfileId,ArtifactQualityScoreProfileId,
                   ArtifactQualityScore,IsFavorite,IsManualSource
            FROM discovery_records WHERE {condition} LIMIT 1;
            """;
        Add(command, "$value", value);
        if (command.Connection!.State != ConnectionState.Open) await command.Connection.OpenAsync(cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Map(reader) : null;
    }

    private static string BuildWhere(System.Data.Common.DbCommand command, MarketplaceListFilter filter)
    {
        var clauses = new List<string>();
        if (!filter.IncludeUnsupported) clauses.Add("SuggestedKind <> 'UnsupportedProject'");
        if (!filter.IncludeStale) clauses.Add("Status <> 'Stale'");
        if (filter.Kind is not null) { clauses.Add("SuggestedKind=$kind"); Add(command, "$kind", filter.Kind.ToString()); }
        if (!string.IsNullOrWhiteSpace(filter.Category)) { clauses.Add("PrimaryCategory=$category"); Add(command, "$category", filter.Category.Trim()); }
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            clauses.Add("(Name LIKE $search OR Description LIKE $search OR Owner LIKE $search OR Repository LIKE $search)");
            Add(command, "$search", $"%{filter.Search.Trim()}%");
        }
        return clauses.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", clauses);
    }

    private static MarketplaceDiscoveryRecord Map(IDataRecord row)
        => new(
            row.GetString(0), row.GetString(1), GetString(row, 2), row.GetString(3), row.GetString(4), row.GetString(5),
            row.GetString(6), GetString(row, 7), Enum.Parse<MarketplaceArtifactKind>(row.GetString(8)),
            Enum.Parse<MarketplaceClassificationConfidence>(row.GetString(9)), row.GetString(10),
            JsonSerializer.Deserialize<string[]>(row.GetString(11), Json) ?? [], JsonSerializer.Deserialize<string[]>(row.GetString(12), Json) ?? [],
            GetString(row, 13), row.GetInt64(14) != 0, row.GetInt32(15), row.GetInt32(16), GetDate(row, 17), GetDate(row, 18),
            GetDateRequired(row, 19), GetDateRequired(row, 20), row.GetInt32(21), Enum.Parse<MarketplaceDiscoveryStatus>(row.GetString(22)),
            row.GetString(23), row.GetDouble(24), row.GetString(25), GetString(row, 26), row.IsDBNull(27) ? null : row.GetDouble(27),
            row.GetInt64(28) != 0, row.GetInt64(29) != 0);

    private static void BindRecord(System.Data.Common.DbCommand command, MarketplaceDiscoveryRecord record)
    {
        Add(command, "$id", record.Id); Add(command, "$sourceId", record.SourceId); Add(command, "$nodeId", record.GitHubNodeId);
        Add(command, "$url", record.CanonicalUrl); Add(command, "$owner", record.Owner); Add(command, "$repository", record.Repository);
        Add(command, "$name", record.Name); Add(command, "$description", record.Description); Add(command, "$kind", record.SuggestedKind.ToString());
        Add(command, "$confidence", record.ClassificationConfidence.ToString()); Add(command, "$category", record.PrimaryCategory);
        Add(command, "$secondary", JsonSerializer.Serialize(record.SecondaryCategories, Json)); Add(command, "$topics", JsonSerializer.Serialize(record.Topics, Json));
        Add(command, "$license", record.License); Add(command, "$archived", record.IsArchived ? 1 : 0); Add(command, "$stars", record.Stars); Add(command, "$forks", record.Forks);
        Add(command, "$githubUpdated", ToDb(record.GitHubUpdatedAt)); Add(command, "$pushed", ToDb(record.PushedAt)); Add(command, "$firstSeen", ToDb(record.FirstSeenAt));
        Add(command, "$lastSeen", ToDb(record.LastSeenAt)); Add(command, "$misses", record.ConsecutiveMissCount); Add(command, "$status", record.Status.ToString());
        Add(command, "$fingerprint", record.MetadataFingerprint); Add(command, "$score", record.DiscoveryScore); Add(command, "$scoreProfile", record.DiscoveryScoreProfileId);
        Add(command, "$artifactProfile", record.ArtifactQualityScoreProfileId); Add(command, "$artifactScore", record.ArtifactQualityScore);
        Add(command, "$favorite", record.IsFavorite ? 1 : 0); Add(command, "$manual", record.IsManualSource ? 1 : 0);
    }

    private static void Add(System.Data.Common.DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value ?? DBNull.Value; command.Parameters.Add(parameter);
    }

    private static string? GetString(IDataRecord row, int index) => row.IsDBNull(index) ? null : row.GetString(index);
    private static DateTimeOffset? GetDate(IDataRecord row, int index) => row.IsDBNull(index) ? null : DateTimeOffset.Parse(row.GetString(index), null, System.Globalization.DateTimeStyles.RoundtripKind);
    private static DateTimeOffset GetDateRequired(IDataRecord row, int index) => DateTimeOffset.Parse(row.GetString(index), null, System.Globalization.DateTimeStyles.RoundtripKind);
    private static string? ToDb(DateTimeOffset? value) => value?.ToString("O");
}
