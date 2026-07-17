using AgentService.Application.Contracts;
using AgentService.Domain.Models;
using Microsoft.EntityFrameworkCore;

namespace AgentService.Infrastructure.Persistence;

public sealed class AgentScheduleRecord
{
    public string Id { get; set; } = ""; public string Name { get; set; } = ""; public string Task { get; set; } = "";
    public string WorkspacePath { get; set; } = ""; public string? ProjectId { get; set; } public string? ProviderProfileId { get; set; }
    public string Mode { get; set; } = nameof(AgentMode.Plan); public int? IntervalMinutes { get; set; }
    public DateTimeOffset NextRunAt { get; set; } public bool Enabled { get; set; } = true;
    public string? LastRunId { get; set; } public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class AgentScheduleStore(IDbContextFactory<AppDbContext> factory) : IAgentScheduleStore
{
    public async Task<IReadOnlyList<AgentSchedule>> ListAsync(CancellationToken ct = default)
    { await using var db=await factory.CreateDbContextAsync(ct);return (await db.AgentSchedules.AsNoTracking().OrderBy(x=>x.NextRunAt).ToListAsync(ct)).Select(Map).ToList(); }
    public async Task<AgentSchedule?> GetAsync(string id,CancellationToken ct=default)
    { await using var db=await factory.CreateDbContextAsync(ct);var row=await db.AgentSchedules.AsNoTracking().FirstOrDefaultAsync(x=>x.Id==id,ct);return row is null?null:Map(row); }
    public async Task SaveAsync(AgentSchedule schedule,CancellationToken ct=default)
    { await using var db=await factory.CreateDbContextAsync(ct);var row=await db.AgentSchedules.FindAsync([schedule.Id],ct);if(row is null){row=new(){Id=schedule.Id,CreatedAt=schedule.CreatedAt};db.AgentSchedules.Add(row);}row.Name=schedule.Name;row.Task=schedule.Task;row.WorkspacePath=schedule.WorkspacePath;row.ProjectId=schedule.ProjectId;row.ProviderProfileId=schedule.ProviderProfileId;row.Mode=schedule.Mode.ToString();row.IntervalMinutes=schedule.IntervalMinutes;row.NextRunAt=schedule.NextRunAt;row.Enabled=schedule.Enabled;row.LastRunId=schedule.LastRunId;row.LastError=schedule.LastError;row.UpdatedAt=DateTimeOffset.UtcNow;await db.SaveChangesAsync(ct); }
    public async Task DeleteAsync(string id,CancellationToken ct=default)
    { await using var db=await factory.CreateDbContextAsync(ct);var row=await db.AgentSchedules.FindAsync([id],ct);if(row is not null){db.Remove(row);await db.SaveChangesAsync(ct);} }
    public async Task<IReadOnlyList<AgentSchedule>> ListDueAsync(DateTimeOffset now,CancellationToken ct=default)
    { await using var db=await factory.CreateDbContextAsync(ct);return (await db.AgentSchedules.AsNoTracking().Where(x=>x.Enabled&&x.NextRunAt<=now).OrderBy(x=>x.NextRunAt).Take(20).ToListAsync(ct)).Select(Map).ToList(); }
    private static AgentSchedule Map(AgentScheduleRecord x)=>new(){Id=x.Id,Name=x.Name,Task=x.Task,WorkspacePath=x.WorkspacePath,ProjectId=x.ProjectId,ProviderProfileId=x.ProviderProfileId,Mode=Enum.TryParse<AgentMode>(x.Mode,true,out var mode)?mode:AgentMode.Plan,IntervalMinutes=x.IntervalMinutes,NextRunAt=x.NextRunAt,Enabled=x.Enabled,LastRunId=x.LastRunId,LastError=x.LastError,CreatedAt=x.CreatedAt,UpdatedAt=x.UpdatedAt};
}
