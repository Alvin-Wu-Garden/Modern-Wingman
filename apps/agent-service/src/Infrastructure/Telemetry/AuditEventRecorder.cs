using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;
using AgentService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AgentService.Infrastructure.Telemetry;

public sealed class AuditEventRecorder(
    IDbContextFactory<AppDbContext> dbFactory,
    ISensitiveDataRedactor redactor,
    ILogger<AuditEventRecorder> logger) : IAuditEventRecorder
{
    public async Task RecordAsync(AuditEventWrite evt, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            db.AuditEvents.Add(new AuditEventRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                TraceId = evt.TraceId,
                ActorType = evt.ActorType,
                ActorId = evt.ActorId,
                EventType = evt.EventType,
                TargetType = evt.TargetType,
                TargetId = evt.TargetId,
                Action = evt.Action,
                Result = evt.Result,
                MachineName = Environment.MachineName,
                BeforeHash = evt.BeforeHash,
                AfterHash = evt.AfterHash,
                DetailsJson = evt.DetailsJson is null ? null : redactor.Redact(evt.DetailsJson),
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Audit event write failed; continuing request");
        }
    }
}
