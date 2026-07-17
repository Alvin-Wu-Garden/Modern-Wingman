using System.Text;
using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentService.Infrastructure.Telemetry;

public sealed class AuditQueryService(IDbContextFactory<AppDbContext> factory,ISensitiveDataRedactor redactor):IAuditQueryService
{
    public async Task<AuditPage> QueryAsync(AuditQuery query,CancellationToken ct=default)
    {
        var limit=Math.Clamp(query.Limit,1,1000);var offset=Math.Max(0,query.Offset);
        await using var db=await factory.CreateDbContextAsync(ct);var rows=db.AuditEvents.AsNoTracking().AsQueryable();
        if(query.From is not null)rows=rows.Where(x=>x.CreatedAt>=query.From);
        if(query.To is not null)rows=rows.Where(x=>x.CreatedAt<=query.To);
        if(!string.IsNullOrWhiteSpace(query.EventType))rows=rows.Where(x=>x.EventType==query.EventType);
        if(!string.IsNullOrWhiteSpace(query.TargetType))rows=rows.Where(x=>x.TargetType==query.TargetType);
        if(!string.IsNullOrWhiteSpace(query.TargetId))rows=rows.Where(x=>x.TargetId==query.TargetId);
        if(!string.IsNullOrWhiteSpace(query.Result))rows=rows.Where(x=>x.Result==query.Result);
        if(!string.IsNullOrWhiteSpace(query.TraceId))rows=rows.Where(x=>x.TraceId==query.TraceId);
        var total=await rows.CountAsync(ct);var items=await rows.OrderByDescending(x=>x.CreatedAt).Skip(offset).Take(limit).ToListAsync(ct);
        return new(items.Select(x=>new AuditEventDto(x.Id,x.TraceId,x.ActorType,x.ActorId,x.EventType,x.TargetType,x.TargetId,x.Action,x.Result,x.MachineName,x.BeforeHash,x.AfterHash,x.DetailsJson is null?null:redactor.Redact(x.DetailsJson),x.CreatedAt)).ToList(),total,offset,limit);
    }
    public async Task<string> ExportCsvAsync(AuditQuery query,CancellationToken ct=default)
    {
        var page=await QueryAsync(query with{Offset=0,Limit=1000},ct);var csv=new StringBuilder("createdAt,id,traceId,eventType,targetType,targetId,action,result,details\r\n");
        foreach(var x in page.Items)csv.AppendJoin(',',Cell(x.CreatedAt.ToString("O")),Cell(x.Id),Cell(x.TraceId),Cell(x.EventType),Cell(x.TargetType),Cell(x.TargetId),Cell(x.Action),Cell(x.Result),Cell(x.DetailsJson)).Append("\r\n");return csv.ToString();
    }
    public async Task<ToolCallAuditPage> QueryToolCallsAsync(ToolCallAuditQuery query,CancellationToken ct=default)
    {
        var limit=Math.Clamp(query.Limit,1,1000);var offset=Math.Max(0,query.Offset);await using var db=await factory.CreateDbContextAsync(ct);var rows=db.AiToolCallLogs.AsNoTracking().Include(x=>x.RequestLog).AsQueryable();
        if(query.From is not null)rows=rows.Where(x=>x.StartedAt>=query.From);if(query.To is not null)rows=rows.Where(x=>x.StartedAt<=query.To);if(!string.IsNullOrWhiteSpace(query.ProjectId))rows=rows.Where(x=>x.RequestLog!.ProjectId==query.ProjectId);if(!string.IsNullOrWhiteSpace(query.RunId))rows=rows.Where(x=>x.RequestLog!.RunId==query.RunId);if(!string.IsNullOrWhiteSpace(query.Provider))rows=rows.Where(x=>x.RequestLog!.ProviderProfileId==query.Provider);if(!string.IsNullOrWhiteSpace(query.Tool))rows=rows.Where(x=>x.ToolName==query.Tool);if(!string.IsNullOrWhiteSpace(query.Status))rows=rows.Where(x=>x.Status==query.Status);
        var total=await rows.CountAsync(ct);var items=await rows.OrderByDescending(x=>x.StartedAt).Skip(offset).Take(limit).Select(x=>new ToolCallAuditDto(x.Id,x.RequestLog!.TraceId,x.RequestLog.ProjectId,x.RequestLog.RunId,x.RequestLog.ProviderProfileId,x.ToolName,x.ToolType,x.Status,x.StartedAt,x.DurationMs,x.ApprovalRequired,x.ApprovalResult,x.ErrorMessageSanitized)).ToListAsync(ct);return new(items,total,offset,limit);
    }
    public async Task<string> ExportToolCallsCsvAsync(ToolCallAuditQuery query,CancellationToken ct=default)
    {
        var page=await QueryToolCallsAsync(query with{Offset=0,Limit=1000},ct);var csv=new StringBuilder("startedAt,id,traceId,projectId,runId,provider,toolName,toolType,status,durationMs,approvalRequired,approvalResult,error\r\n");
        foreach(var x in page.Items)csv.AppendJoin(',',Cell(x.StartedAt.ToString("O")),Cell(x.Id),Cell(x.TraceId),Cell(x.ProjectId),Cell(x.RunId),Cell(x.Provider),Cell(x.ToolName),Cell(x.ToolType),Cell(x.Status),Cell(x.DurationMs?.ToString()),Cell(x.ApprovalRequired.ToString()),Cell(x.ApprovalResult),Cell(x.Error)).Append("\r\n");return csv.ToString();
    }
    public async Task<AuditFacets> GetFacetsAsync(CancellationToken ct=default)
    {
        await using var db=await factory.CreateDbContextAsync(ct);
        var eventTypes=await db.AuditEvents.AsNoTracking().Select(x=>x.EventType).Distinct().OrderBy(x=>x).ToListAsync(ct);
        var targetTypes=await db.AuditEvents.AsNoTracking().Select(x=>x.TargetType).Distinct().OrderBy(x=>x).ToListAsync(ct);
        var results=await db.AuditEvents.AsNoTracking().Select(x=>x.Result).Distinct().OrderBy(x=>x).ToListAsync(ct);
        var traces=await db.AuditEvents.AsNoTracking().Where(x=>x.TraceId!=null).OrderByDescending(x=>x.CreatedAt).Select(x=>x.TraceId!).Distinct().Take(200).ToListAsync(ct);
        var rawTargets=await db.AuditEvents.AsNoTracking().Where(x=>x.TargetId!=null).Select(x=>new{x.TargetType,x.TargetId}).Distinct().Take(500).ToListAsync(ct);
        var projectNames=await db.Projects.AsNoTracking().ToDictionaryAsync(x=>x.Id,x=>x.Name,ct);
        var runNames=await db.Runs.AsNoTracking().ToDictionaryAsync(x=>x.Id,x=>x.UserMessage,ct);
        var targets=rawTargets.Select(x=>new AuditFilterOption(x.TargetId!,FriendlyTarget(x.TargetType,x.TargetId!,projectNames,runNames),x.TargetType)).OrderBy(x=>x.Group).ThenBy(x=>x.Label).ToList();
        return new(eventTypes,targetTypes,targets,results,traces);
    }
    public async Task<ToolCallAuditFacets> GetToolCallFacetsAsync(CancellationToken ct=default)
    {
        await using var db=await factory.CreateDbContextAsync(ct);
        var requestRows=db.AiRequestLogs.AsNoTracking();
        var projectIds=await requestRows.Where(x=>x.ProjectId!=null).Select(x=>x.ProjectId!).Distinct().Take(200).ToListAsync(ct);
        var runIds=await requestRows.Where(x=>x.RunId!=null).Select(x=>x.RunId!).Distinct().Take(200).ToListAsync(ct);
        var projectNames=await db.Projects.AsNoTracking().Where(x=>projectIds.Contains(x.Id)).ToDictionaryAsync(x=>x.Id,x=>x.Name,ct);
        var runNames=await db.Runs.AsNoTracking().Where(x=>runIds.Contains(x.Id)).ToDictionaryAsync(x=>x.Id,x=>x.UserMessage,ct);
        var providers=await requestRows.Select(x=>x.ProviderProfileId).Distinct().OrderBy(x=>x).ToListAsync(ct);
        var tools=await db.AiToolCallLogs.AsNoTracking().Select(x=>x.ToolName).Distinct().OrderBy(x=>x).ToListAsync(ct);
        var statuses=await db.AiToolCallLogs.AsNoTracking().Select(x=>x.Status).Distinct().OrderBy(x=>x).ToListAsync(ct);
        return new(projectIds.Select(x=>new AuditFilterOption(x,projectNames.TryGetValue(x,out var name)?$"{name} · {ShortId(x)}":ShortId(x))).ToList(),runIds.Select(x=>new AuditFilterOption(x,runNames.TryGetValue(x,out var task)?$"{Trim(task,42)} · {ShortId(x)}":ShortId(x))).ToList(),providers,tools,statuses);
    }
    private static string FriendlyTarget(string type,string id,IReadOnlyDictionary<string,string> projects,IReadOnlyDictionary<string,string> runs)=>type switch{"project" when projects.TryGetValue(id,out var name)=>$"{name} · {ShortId(id)}","agent_run" when runs.TryGetValue(id,out var task)=>$"{Trim(task,42)} · {ShortId(id)}",_=>ShortId(id)};
    private static string ShortId(string value)=>value.Length<=16?value:$"{value[..8]}…{value[^4..]}";
    private static string Trim(string value,int max)=>value.Length<=max?value:value[..max]+"…";
    private static string Cell(string? value)=>$"\"{(value??"").Replace("\"","\"\"")}\"";
}
