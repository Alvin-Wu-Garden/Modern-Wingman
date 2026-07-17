using AgentService.Application.Contracts;
using AgentService.Domain.Models;
using Microsoft.EntityFrameworkCore;

namespace AgentService.Infrastructure.Persistence;

public sealed class RunStepRecord
{
    public string Id { get; set; }="";public string RunId { get; set; }="";public string Phase { get; set; }="";
    public int Attempt { get; set; }=1;public string Status { get; set; }="running";public string? CheckpointId { get; set; }
    public string? ErrorSanitized { get; set; }public DateTimeOffset StartedAt { get; set; }public DateTimeOffset? EndedAt { get; set; }
}

public sealed class RunStepRepository(IDbContextFactory<AppDbContext> factory):IRunStepRepository
{
    public async Task SaveAsync(RunStep step,CancellationToken ct=default){await using var db=await factory.CreateDbContextAsync(ct);var row=await db.RunSteps.FindAsync([step.Id],ct);if(row is null){row=new RunStepRecord{Id=step.Id};db.RunSteps.Add(row);}row.RunId=step.RunId;row.Phase=step.Phase;row.Attempt=step.Attempt;row.Status=step.Status;row.CheckpointId=step.CheckpointId;row.ErrorSanitized=step.ErrorSanitized;row.StartedAt=step.StartedAt;row.EndedAt=step.EndedAt;await db.SaveChangesAsync(ct);}
    public async Task<RunStep?> GetActiveAsync(string runId,CancellationToken ct=default){await using var db=await factory.CreateDbContextAsync(ct);var row=await db.RunSteps.AsNoTracking().Where(x=>x.RunId==runId&&x.Status=="running").OrderByDescending(x=>x.StartedAt).FirstOrDefaultAsync(ct);return row is null?null:Map(row);}
    public async Task<IReadOnlyList<RunStep>> ListAsync(string runId,CancellationToken ct=default){await using var db=await factory.CreateDbContextAsync(ct);return (await db.RunSteps.AsNoTracking().Where(x=>x.RunId==runId).OrderBy(x=>x.StartedAt).ToListAsync(ct)).Select(Map).ToList();}
    private static RunStep Map(RunStepRecord x)=>new(){Id=x.Id,RunId=x.RunId,Phase=x.Phase,Attempt=x.Attempt,Status=x.Status,CheckpointId=x.CheckpointId,ErrorSanitized=x.ErrorSanitized,StartedAt=x.StartedAt,EndedAt=x.EndedAt};
}
