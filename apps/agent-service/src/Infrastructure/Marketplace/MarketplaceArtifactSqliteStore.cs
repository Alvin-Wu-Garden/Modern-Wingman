using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using AgentService.Infrastructure.Persistence;
using Wingman.Marketplace.Contracts;
using Wingman.Marketplace.Domain;

namespace AgentService.Infrastructure.Marketplace;

public sealed class MarketplaceArtifactSqliteStore(IDbContextFactory<AppDbContext> factory) : IMarketplaceArtifactStore
{
    public async Task SaveImportAsync(IReadOnlyList<MarketplaceArtifactCandidate> candidates, IReadOnlyList<MarketplaceArtifact> artifacts, IReadOnlyList<MarketplaceArtifactScoreSnapshot> scoreSnapshots, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.Database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var candidate in candidates)
            {
                await using var command = db.Database.GetDbConnection().CreateCommand(); command.Transaction = transaction.GetDbTransaction();
                command.CommandText = """
                    INSERT INTO artifact_candidates (Id,SourceLocation,ArtifactPath,Kind,DisplayName,Status,ValidationProfileId,ValidationMessage,CreatedAt)
                    VALUES ($id,$source,$path,$kind,$name,$status,$profile,$message,$created);
                    """;
                Add(command, "$id", candidate.Id); Add(command, "$source", candidate.SourceLocation); Add(command, "$path", candidate.ArtifactPath);
                Add(command, "$kind", candidate.Kind.ToString()); Add(command, "$name", candidate.DisplayName); Add(command, "$status", candidate.Status.ToString());
                Add(command, "$profile", candidate.ValidationProfileId); Add(command, "$message", candidate.ValidationMessage); Add(command, "$created", ToDb(candidate.CreatedAt));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            foreach (var artifact in artifacts)
            {
                await using var command = db.Database.GetDbConnection().CreateCommand(); command.Transaction = transaction.GetDbTransaction();
                command.CommandText = """
                    INSERT INTO artifacts (Id,CandidateId,Kind,DisplayName,SnapshotPath,ContentHash,Status,ValidationProfileId,ImportedAt)
                    VALUES ($id,$candidate,$kind,$name,$snapshot,$hash,$status,$profile,$imported)
                    ON CONFLICT(ContentHash,Kind) DO UPDATE SET ImportedAt=excluded.ImportedAt;
                    """;
                Add(command, "$id", artifact.Id); Add(command, "$candidate", artifact.CandidateId); Add(command, "$kind", artifact.Kind.ToString());
                Add(command, "$name", artifact.DisplayName); Add(command, "$snapshot", artifact.SnapshotPath); Add(command, "$hash", artifact.ContentHash);
                Add(command, "$status", artifact.Status.ToString()); Add(command, "$profile", artifact.ValidationProfileId); Add(command, "$imported", ToDb(artifact.ImportedAt));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            foreach (var snapshot in scoreSnapshots)
            {
                await using var command = db.Database.GetDbConnection().CreateCommand(); command.Transaction = transaction.GetDbTransaction();
                command.CommandText = "INSERT INTO artifact_score_snapshots (Id,ArtifactId,ProfileId,TotalScore,ComponentsJson,EvidenceJson,ComputedAt) VALUES ($id,$artifact,$profile,$total,$components,$evidence,$computed);";
                Add(command, "$id", snapshot.Id); Add(command, "$artifact", snapshot.ArtifactId); Add(command, "$profile", snapshot.ProfileId); Add(command, "$total", snapshot.TotalScore);
                Add(command, "$components", System.Text.Json.JsonSerializer.Serialize(snapshot.Components)); Add(command, "$evidence", snapshot.EvidenceJson); Add(command, "$computed", ToDb(snapshot.ComputedAt));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        catch { await transaction.RollbackAsync(cancellationToken); throw; }
    }

    public async Task<IReadOnlyList<MarketplaceArtifact>> ListArtifactsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT Id,CandidateId,Kind,DisplayName,SnapshotPath,ContentHash,Status,ValidationProfileId,ImportedAt FROM artifacts ORDER BY ImportedAt DESC;";
        if (command.Connection!.State != ConnectionState.Open) await command.Connection.OpenAsync(cancellationToken);
        var artifacts = new List<MarketplaceArtifact>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) artifacts.Add(new(reader.GetString(0), reader.GetString(1), Enum.Parse<MarketplaceArtifactKind>(reader.GetString(2)), reader.GetString(3), reader.GetString(4), reader.GetString(5), Enum.Parse<MarketplaceDiscoveryStatus>(reader.GetString(6)), reader.IsDBNull(7) ? null : reader.GetString(7), DateTimeOffset.Parse(reader.GetString(8), null, System.Globalization.DateTimeStyles.RoundtripKind)));
        return artifacts;
    }

    public async Task<MarketplaceArtifact?> GetArtifactAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT Id,CandidateId,Kind,DisplayName,SnapshotPath,ContentHash,Status,ValidationProfileId,ImportedAt FROM artifacts WHERE Id=$id LIMIT 1;";
        Add(command, "$id", id);
        return await ReadArtifactAsync(command, cancellationToken);
    }

    public async Task<MarketplaceArtifact?> GetArtifactByContentHashAsync(string contentHash, MarketplaceArtifactKind kind, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT Id,CandidateId,Kind,DisplayName,SnapshotPath,ContentHash,Status,ValidationProfileId,ImportedAt FROM artifacts WHERE ContentHash=$hash AND Kind=$kind LIMIT 1;";
        Add(command, "$hash", contentHash); Add(command, "$kind", kind.ToString());
        return await ReadArtifactAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<MarketplaceArtifactSource>> ListArtifactSourcesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT a.Id,a.CandidateId,a.Kind,a.DisplayName,a.SnapshotPath,a.ContentHash,a.Status,a.ValidationProfileId,a.ImportedAt,c.SourceLocation FROM artifacts a INNER JOIN artifact_candidates c ON c.Id=a.CandidateId ORDER BY a.ImportedAt DESC;";
        if (command.Connection!.State != ConnectionState.Open) await command.Connection.OpenAsync(cancellationToken);
        var results = new List<MarketplaceArtifactSource>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var artifact = new MarketplaceArtifact(reader.GetString(0), reader.GetString(1), Enum.Parse<MarketplaceArtifactKind>(reader.GetString(2)), reader.GetString(3), reader.GetString(4), reader.GetString(5), Enum.Parse<MarketplaceDiscoveryStatus>(reader.GetString(6)), reader.IsDBNull(7) ? null : reader.GetString(7), DateTimeOffset.Parse(reader.GetString(8), null, System.Globalization.DateTimeStyles.RoundtripKind));
            results.Add(new(artifact, reader.GetString(9)));
        }
        return results;
    }

    private static async Task<MarketplaceArtifact?> ReadArtifactAsync(System.Data.Common.DbCommand command, CancellationToken cancellationToken)
    {
        if (command.Connection!.State != ConnectionState.Open) await command.Connection.OpenAsync(cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new MarketplaceArtifact(reader.GetString(0), reader.GetString(1), Enum.Parse<MarketplaceArtifactKind>(reader.GetString(2)), reader.GetString(3), reader.GetString(4), reader.GetString(5), Enum.Parse<MarketplaceDiscoveryStatus>(reader.GetString(6)), reader.IsDBNull(7) ? null : reader.GetString(7), DateTimeOffset.Parse(reader.GetString(8), null, System.Globalization.DateTimeStyles.RoundtripKind))
            : null;
    }

    private static void Add(System.Data.Common.DbCommand command, string name, object? value) { var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value ?? DBNull.Value; command.Parameters.Add(parameter); }
    private static string ToDb(DateTimeOffset value) => value.ToString("O");
}
