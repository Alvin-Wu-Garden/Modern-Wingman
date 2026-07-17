using AgentService.Application.Contracts;
using AgentService.Application.Models;
using System.Text;
namespace AgentService.Host.RestEndpoints;
public static class AuditEndpoints
{
    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        var group=app.MapGroup("/api/audit");
        group.MapGet("/events",async(DateTimeOffset? from,DateTimeOffset? to,string? eventType,string? targetType,string? targetId,string? result,string? traceId,int? offset,int? limit,IAuditQueryService query,CancellationToken ct)=>Results.Ok(await query.QueryAsync(new(from,to,eventType,targetType,targetId,result,traceId,offset??0,limit??100),ct)));
        group.MapGet("/export.csv",async(DateTimeOffset? from,DateTimeOffset? to,string? eventType,string? targetType,string? targetId,string? result,string? traceId,IAuditQueryService query,CancellationToken ct)=>Results.Text(await query.ExportCsvAsync(new(from,to,eventType,targetType,targetId,result,traceId,0,1000),ct),"text/csv",Encoding.UTF8));
        group.MapPost("/cleanup",async(IAuditMaintenanceService maintenance,CancellationToken ct)=>Results.Ok(new{deleted=await maintenance.DeleteExpiredAsync(ct)}));
        group.MapGet("/tool-calls",async(DateTimeOffset? from,DateTimeOffset? to,string? projectId,string? runId,string? provider,string? tool,string? status,int? offset,int? limit,IAuditQueryService query,CancellationToken ct)=>Results.Ok(await query.QueryToolCallsAsync(new(from,to,projectId,runId,provider,tool,status,offset??0,limit??100),ct)));
        group.MapGet("/tool-calls/export.csv",async(DateTimeOffset? from,DateTimeOffset? to,string? projectId,string? runId,string? provider,string? tool,string? status,IAuditQueryService query,CancellationToken ct)=>Results.Text(await query.ExportToolCallsCsvAsync(new(from,to,projectId,runId,provider,tool,status,0,1000),ct),"text/csv",Encoding.UTF8));
        group.MapGet("/facets",async(IAuditQueryService query,CancellationToken ct)=>Results.Ok(await query.GetFacetsAsync(ct)));
        group.MapGet("/tool-calls/facets",async(IAuditQueryService query,CancellationToken ct)=>Results.Ok(await query.GetToolCallFacetsAsync(ct)));
        return app;
    }
}
