using AgentService.Application.Contracts;
using AgentService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentService.Infrastructure.Telemetry;

public sealed class AuditMaintenanceService(IDbContextFactory<AppDbContext> factory,IConfiguration configuration):IAuditMaintenanceService
{
    public async Task<int> DeleteExpiredAsync(CancellationToken ct=default)
    {
        var days=Math.Clamp(configuration.GetValue("Audit:RetentionDays",365),30,3650);
        var cutoff=DateTimeOffset.UtcNow.AddDays(-days);
        await using var db=await factory.CreateDbContextAsync(ct);
        var events=await db.AuditEvents.Where(x=>x.CreatedAt<cutoff).ExecuteDeleteAsync(ct);
        await db.AiToolCallLogs.Where(x=>x.StartedAt<cutoff).ExecuteDeleteAsync(ct);
        await db.AiRequestAttempts.Where(x=>x.StartedAt<cutoff).ExecuteDeleteAsync(ct);
        await db.AiRequestLogs.Where(x=>x.StartedAt<cutoff).ExecuteDeleteAsync(ct);
        return events;
    }
}
