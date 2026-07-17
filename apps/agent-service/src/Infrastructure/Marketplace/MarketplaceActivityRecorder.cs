using System.Data;
using System.Text.Json;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Wingman.Marketplace.Contracts;
using Wingman.Marketplace.Domain;

namespace AgentService.Infrastructure.Marketplace;

/// <summary>
/// Persists Marketplace activity and projects it into the existing run-event repository.
/// Marketplace operations are not agent runs, so each operation gets a synthetic, queryable
/// run id. This avoids coupling Marketplace domain code to agent orchestration while keeping
/// the existing Timeline endpoint useful.
/// </summary>
public sealed class MarketplaceActivityRecorder(
    IDbContextFactory<AppDbContext> factory,
    IRunEventRepository runEvents) : IMarketplaceActivityRecorder
{
    public async Task RecordAsync(MarketplaceActivityEvent activity, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            INSERT INTO marketplace_activity_events (Id,OperationId,EventType,Status,ArtifactId,TargetId,Detail,OccurredAt)
            VALUES ($id,$operation,$type,$status,$artifact,$target,$detail,$occurred);
            """;
        Add(command, "$id", activity.Id); Add(command, "$operation", activity.OperationId); Add(command, "$type", activity.EventType);
        Add(command, "$status", activity.Status); Add(command, "$artifact", activity.ArtifactId); Add(command, "$target", activity.TargetId);
        Add(command, "$detail", activity.Detail); Add(command, "$occurred", activity.OccurredAt.ToString("O"));
        if (command.Connection!.State != ConnectionState.Open) await command.Connection.OpenAsync(cancellationToken);
        await command.ExecuteNonQueryAsync(cancellationToken);

        var payload = JsonSerializer.Serialize(new
        {
            operationId = activity.OperationId,
            status = activity.Status,
            artifactId = activity.ArtifactId,
            targetId = activity.TargetId,
            detail = activity.Detail,
        });
        await runEvents.AppendAsync(new RunStreamEvent
        {
            RunId = "marketplace:" + activity.OperationId,
            EventType = "marketplace:" + activity.EventType,
            PayloadJson = payload,
            Timestamp = activity.OccurredAt,
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<MarketplaceActivityEvent>> ListAsync(int take = 100, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT Id,OperationId,EventType,Status,ArtifactId,TargetId,Detail,OccurredAt FROM marketplace_activity_events ORDER BY OccurredAt DESC LIMIT $take;";
        Add(command, "$take", Math.Clamp(take, 1, 500));
        if (command.Connection!.State != ConnectionState.Open) await command.Connection.OpenAsync(cancellationToken);
        var results = new List<MarketplaceActivityEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            results.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6), DateTimeOffset.Parse(reader.GetString(7), null, System.Globalization.DateTimeStyles.RoundtripKind)));
        return results;
    }

    private static void Add(System.Data.Common.DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value ?? DBNull.Value; command.Parameters.Add(parameter);
    }
}
