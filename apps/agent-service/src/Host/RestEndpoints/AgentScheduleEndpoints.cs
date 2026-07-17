using AgentService.Application.Contracts;
using AgentService.Domain.Models;

namespace AgentService.Host.RestEndpoints;

public static class AgentScheduleEndpoints
{
    public sealed record SaveScheduleRequest(
        string Name, string Task, string WorkspacePath, string? ProjectId,
        string? ProviderProfileId, string? Mode, int? IntervalMinutes,
        DateTimeOffset NextRunAt, bool Enabled = true);

    public static IEndpointRouteBuilder MapAgentScheduleEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/agent-schedules");
        group.MapGet("/", async (IAgentScheduleStore store,CancellationToken ct)=>Results.Ok(await store.ListAsync(ct)));
        group.MapPost("/", Save);
        group.MapPut("/{id}", async(string id,SaveScheduleRequest request,IAgentScheduleStore store,CancellationToken ct)=>await Save(request,store,ct,id));
        group.MapDelete("/{id}",async(string id,IAgentScheduleStore store,CancellationToken ct)=>{await store.DeleteAsync(id,ct);return Results.NoContent();});
        return app;
    }

    private static async Task<IResult> Save(
        SaveScheduleRequest request,
        IAgentScheduleStore store,
        CancellationToken ct,
        string? id = null)
    {
        if (!Directory.Exists(request.WorkspacePath)) return Results.BadRequest(new{error="Workspace does not exist."});
        if (string.IsNullOrWhiteSpace(request.Name)||string.IsNullOrWhiteSpace(request.Task)) return Results.BadRequest(new{error="Name and task are required."});
        if (request.IntervalMinutes is <= 0) return Results.BadRequest(new{error="Interval must be at least one minute."});
        var existing=id is null?null:await store.GetAsync(id,ct);
        var schedule=new AgentSchedule{Id=id??Guid.NewGuid().ToString("N"),Name=request.Name.Trim(),Task=request.Task.Trim(),WorkspacePath=Path.GetFullPath(request.WorkspacePath),ProjectId=request.ProjectId,ProviderProfileId=request.ProviderProfileId,Mode=ParseMode(request.Mode),IntervalMinutes=request.IntervalMinutes,NextRunAt=request.NextRunAt,Enabled=request.Enabled,CreatedAt=existing?.CreatedAt??DateTimeOffset.UtcNow,LastRunId=existing?.LastRunId,LastError=existing?.LastError};
        await store.SaveAsync(schedule,ct);return Results.Ok(schedule);
    }

    private static AgentMode ParseMode(string? mode)=>mode?.Trim().ToLowerInvariant() switch{"ask"=>AgentMode.Ask,"auto"=>AgentMode.Auto,"full_auto" or "fullauto"=>AgentMode.FullAuto,_=>AgentMode.Plan};
}
