using AgentService.Application.Contracts;
using AgentService.Application.Models;
using Microsoft.EntityFrameworkCore;
namespace AgentService.Infrastructure.Persistence;
public sealed class RunEventRecord{public long Sequence{get;set;}public string RunId{get;set;}="";public string EventType{get;set;}="";public string PayloadJson{get;set;}="{}";public DateTimeOffset Timestamp{get;set;}}
public sealed class RunEventRepository(IDbContextFactory<AppDbContext> factory):IRunEventRepository
{
    public async Task<long> AppendAsync(RunStreamEvent evt,CancellationToken ct=default){await using var db=await factory.CreateDbContextAsync(ct);var row=new RunEventRecord{RunId=evt.RunId,EventType=evt.EventType,PayloadJson=evt.PayloadJson,Timestamp=evt.Timestamp};db.RunEvents.Add(row);await db.SaveChangesAsync(ct);return row.Sequence;}
    public async Task<IReadOnlyList<PersistedRunEvent>> ListAsync(string runId,long afterSequence,int limit,CancellationToken ct=default){await using var db=await factory.CreateDbContextAsync(ct);var rows=await db.RunEvents.AsNoTracking().Where(x=>x.RunId==runId&&x.Sequence>afterSequence).OrderBy(x=>x.Sequence).Take(Math.Clamp(limit,1,1000)).ToListAsync(ct);return rows.Select(x=>new PersistedRunEvent(x.Sequence,new RunStreamEvent{RunId=x.RunId,EventType=x.EventType,PayloadJson=x.PayloadJson,Timestamp=x.Timestamp})).ToList();}
}
