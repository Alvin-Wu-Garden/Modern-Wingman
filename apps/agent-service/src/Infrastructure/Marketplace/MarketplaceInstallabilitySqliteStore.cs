using System.Data;
using AgentService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Wingman.Marketplace.Contracts;
using Wingman.Marketplace.Domain;

namespace AgentService.Infrastructure.Marketplace;

/// <summary>預覽結果的輕量持久化轉接器；刻意不保存來源內容。</summary>
public sealed class MarketplaceInstallabilitySqliteStore(IDbContextFactory<AppDbContext> factory) : IMarketplaceInstallabilityStore
{
    public async Task SaveAsync(IReadOnlyList<MarketplaceInstallabilityResult> results, CancellationToken cancellationToken = default)
    {
        if (results.Count == 0) return;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.Database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var item in results)
            {
                await using var delete = db.Database.GetDbConnection().CreateCommand();
                delete.Transaction = transaction.GetDbTransaction();
                delete.CommandText = "DELETE FROM installability_results WHERE ArtifactId=$artifact AND TargetId=$target AND Scope=$scope;";
                Add(delete, "$artifact", item.ArtifactId); Add(delete, "$target", item.TargetId); Add(delete, "$scope", item.Scope.ToString());
                await delete.ExecuteNonQueryAsync(cancellationToken);

                await using var insert = db.Database.GetDbConnection().CreateCommand();
                insert.Transaction = transaction.GetDbTransaction();
                insert.CommandText = "INSERT INTO installability_results (Id,ArtifactId,TargetId,Scope,Status,Reason,ComputedAt) VALUES ($id,$artifact,$target,$scope,$status,$reason,$computed);";
                Add(insert, "$id", Guid.NewGuid().ToString("N")); Add(insert, "$artifact", item.ArtifactId); Add(insert, "$target", item.TargetId); Add(insert, "$scope", item.Scope.ToString()); Add(insert, "$status", item.Status); Add(insert, "$reason", item.Reason); Add(insert, "$computed", item.ComputedAt.ToString("O"));
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        catch { await transaction.RollbackAsync(cancellationToken); throw; }
    }

    public async Task<IReadOnlyList<MarketplaceInstallabilityResult>> ListAsync(string artifactId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT ArtifactId,TargetId,Scope,Status,Reason,ComputedAt FROM installability_results WHERE ArtifactId=$artifact ORDER BY TargetId,Scope;";
        Add(command, "$artifact", artifactId);
        if (command.Connection!.State != ConnectionState.Open) await command.Connection.OpenAsync(cancellationToken);
        var items = new List<MarketplaceInstallabilityResult>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            items.Add(new(reader.GetString(0), reader.GetString(1), Enum.Parse<MarketplaceDeploymentScope>(reader.GetString(2)), reader.GetString(3), null, reader.IsDBNull(4) ? null : reader.GetString(4), DateTimeOffset.Parse(reader.GetString(5))));
        return items;
    }

    private static void Add(System.Data.Common.DbCommand command, string name, object? value) { var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value ?? DBNull.Value; command.Parameters.Add(parameter); }
}
