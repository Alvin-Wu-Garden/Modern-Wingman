using System.Data;
using AgentService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Wingman.Marketplace.Contracts;
using Wingman.Marketplace.Domain;

namespace AgentService.Infrastructure.Marketplace;

public sealed class MarketplaceUpdateHistorySqliteStore(IDbContextFactory<AppDbContext> factory) : IMarketplaceUpdateHistoryStore
{
    public async Task SaveAsync(IReadOnlyList<MarketplaceUpdateCheck> checks, CancellationToken cancellationToken = default)
    {
        if (checks.Count == 0) return;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.Database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var check in checks)
            {
                await using var command = db.Database.GetDbConnection().CreateCommand(); command.Transaction = transaction.GetDbTransaction();
                command.CommandText = "INSERT INTO marketplace_update_checks (Id,ArtifactId,SourceLocation,InstalledCommitSha,Status,AvailableCommitSha,Message,CheckedAt) VALUES ($id,$artifact,$source,$installed,$status,$available,$message,$checked);";
                Add(command, "$id", check.Id); Add(command, "$artifact", check.ArtifactId); Add(command, "$source", check.SourceLocation);
                Add(command, "$installed", check.InstalledCommitSha); Add(command, "$status", check.Status); Add(command, "$available", check.AvailableCommitSha);
                Add(command, "$message", check.Message); Add(command, "$checked", check.CheckedAt.ToString("O"));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        catch { await transaction.RollbackAsync(cancellationToken); throw; }
    }

    public async Task<IReadOnlyList<MarketplaceUpdateCheck>> ListAsync(string? artifactId, int take, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = string.IsNullOrWhiteSpace(artifactId)
            ? "SELECT Id,ArtifactId,SourceLocation,InstalledCommitSha,Status,AvailableCommitSha,Message,CheckedAt FROM marketplace_update_checks ORDER BY CheckedAt DESC LIMIT $take;"
            : "SELECT Id,ArtifactId,SourceLocation,InstalledCommitSha,Status,AvailableCommitSha,Message,CheckedAt FROM marketplace_update_checks WHERE ArtifactId=$artifact ORDER BY CheckedAt DESC LIMIT $take;";
        if (!string.IsNullOrWhiteSpace(artifactId)) Add(command, "$artifact", artifactId);
        Add(command, "$take", Math.Clamp(take, 1, 500));
        if (command.Connection!.State != ConnectionState.Open) await command.Connection.OpenAsync(cancellationToken);
        var results = new List<MarketplaceUpdateCheck>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            results.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), DateTimeOffset.Parse(reader.GetString(7), null, System.Globalization.DateTimeStyles.RoundtripKind)));
        return results;
    }

    private static void Add(System.Data.Common.DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value ?? DBNull.Value; command.Parameters.Add(parameter);
    }
}
