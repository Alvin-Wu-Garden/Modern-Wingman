using AgentService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
namespace AgentService.Host.RestEndpoints;
public static class ProviderHealthEndpoints
{
    public static IEndpointRouteBuilder MapProviderHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/providers/health",async(IDbContextFactory<AppDbContext> factory,CancellationToken ct)=>{await using var db=await factory.CreateDbContextAsync(ct);var since=DateTimeOffset.UtcNow.AddDays(-7);var rows=await db.AiRequestLogs.AsNoTracking().Where(x=>x.StartedAt>=since).OrderByDescending(x=>x.StartedAt).Take(5000).Select(x=>new{x.ProviderProfileId,x.RequestedModelRecordId,x.Status,x.DurationMs,x.TimeToFirstTokenMs}).ToListAsync(ct);var modelNames=await db.AiModels.AsNoTracking().ToDictionaryAsync(x=>x.Id,x=>x.ModelId,ct);var result=rows.GroupBy(x=>new{x.ProviderProfileId,x.RequestedModelRecordId}).Select(group=>new{providerId=group.Key.ProviderProfileId,modelId=group.Key.RequestedModelRecordId is not null&&modelNames.TryGetValue(group.Key.RequestedModelRecordId,out var model)?model:null,requests=group.Count(),successRate=Math.Round(group.Count(x=>x.Status=="succeeded")*100d/group.Count(),1),timeoutRate=Math.Round(group.Count(x=>x.Status=="timed_out")*100d/group.Count(),1),averageDurationMs=group.Where(x=>x.DurationMs.HasValue).Select(x=>(double?)x.DurationMs).Average(),averageTimeToFirstTokenMs=group.Where(x=>x.TimeToFirstTokenMs.HasValue).Select(x=>(double?)x.TimeToFirstTokenMs).Average()}).OrderBy(x=>x.providerId);return Results.Ok(result);});return app;
    }
}
