using System.Data;
using AgentService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Wingman.Marketplace.Contracts;
using Wingman.Marketplace.Domain;

namespace AgentService.Infrastructure.Marketplace;

public sealed class MarketplaceDeploymentSqliteStore(IDbContextFactory<AppDbContext> factory) : IMarketplaceDeploymentStore
{
    public async Task SaveDeploymentAsync(string artifactId, MarketplaceDeploymentRequest request, string targetPath, string deployedHash, string status, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.Database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var update = db.Database.GetDbConnection().CreateCommand())
            {
                update.Transaction = transaction.GetDbTransaction();
                update.CommandText = """
                    UPDATE deployments SET TargetPath=$path,DeployedHash=$hash,Status=$status,UpdatedAt=$updated
                    WHERE ArtifactId=$artifact AND TargetId=$target AND Scope=$scope
                      AND ((ProjectPath IS NULL AND $project IS NULL) OR ProjectPath=$project);
                    """;
                Add(update, "$path", targetPath); Add(update, "$hash", deployedHash); Add(update, "$status", status); Add(update, "$updated", Now());
                Add(update, "$artifact", artifactId); Add(update, "$target", request.TargetId); Add(update, "$scope", request.Scope.ToString()); Add(update, "$project", request.ProjectPath);
                if (await update.ExecuteNonQueryAsync(cancellationToken) == 0)
                {
                    await using var insert = db.Database.GetDbConnection().CreateCommand(); insert.Transaction = transaction.GetDbTransaction();
                    insert.CommandText = "INSERT INTO deployments (Id,ArtifactId,TargetId,Scope,ProjectPath,TargetPath,DeployedHash,Status,CreatedAt,UpdatedAt) VALUES ($id,$artifact,$target,$scope,$project,$path,$hash,$status,$created,$updated);";
                    Add(insert, "$id", Guid.NewGuid().ToString("N")); Add(insert, "$artifact", artifactId); Add(insert, "$target", request.TargetId); Add(insert, "$scope", request.Scope.ToString()); Add(insert, "$project", request.ProjectPath);
                    Add(insert, "$path", targetPath); Add(insert, "$hash", deployedHash); Add(insert, "$status", status); Add(insert, "$created", Now()); Add(insert, "$updated", Now());
                    await insert.ExecuteNonQueryAsync(cancellationToken);
                }
            }
            await transaction.CommitAsync(cancellationToken);
        }
        catch { await transaction.RollbackAsync(cancellationToken); throw; }
    }

    public async Task<IReadOnlyList<(string TargetId, MarketplaceDeploymentScope Scope, string TargetPath, string DeployedHash, string Status)>> ListDeploymentsAsync(string artifactId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken); await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT TargetId,Scope,TargetPath,DeployedHash,Status FROM deployments WHERE ArtifactId=$artifact AND Status IN ('Deployed','BlockedByConflict','DetachedDueToDrift','Configured','NeedsUserInput','PrerequisiteMissing') ORDER BY TargetId;";
        Add(command, "$artifact", artifactId); if (command.Connection!.State != ConnectionState.Open) await command.Connection.OpenAsync(cancellationToken);
        var result = new List<(string, MarketplaceDeploymentScope, string, string, string)>(); await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add((reader.GetString(0), Enum.Parse<MarketplaceDeploymentScope>(reader.GetString(1)), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        return result;
    }

    public async Task UpdateDeploymentStatusAsync(string artifactId, string targetId, MarketplaceDeploymentScope scope, string status, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("UPDATE deployments SET Status={0},UpdatedAt={1} WHERE ArtifactId={2} AND TargetId={3} AND Scope={4};", [status, Now(), artifactId, targetId, scope.ToString()], cancellationToken);
    }

    public async Task<IReadOnlyList<MarketplaceDeploymentState>> ListDeploymentStatesAsync(string artifactId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT TargetId,Scope,ProjectPath,TargetPath,DeployedHash,Status,UpdatedAt FROM deployments WHERE ArtifactId=$artifact ORDER BY UpdatedAt DESC;";
        Add(command, "$artifact", artifactId);
        if (command.Connection!.State != ConnectionState.Open) await command.Connection.OpenAsync(cancellationToken);
        var items = new List<MarketplaceDeploymentState>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            items.Add(new(reader.GetString(0), Enum.Parse<MarketplaceDeploymentScope>(reader.GetString(1)), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), DateTimeOffset.Parse(reader.GetString(6))));
        return items;
    }

    private static string Now() => DateTimeOffset.UtcNow.ToString("O");
    private static void Add(System.Data.Common.DbCommand command, string name, object? value) { var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value ?? DBNull.Value; command.Parameters.Add(parameter); }
}
