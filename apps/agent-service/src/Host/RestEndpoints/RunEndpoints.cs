using AgentService.Application.Contracts;

namespace AgentService.Host.RestEndpoints;

public static class RunEndpoints
{
    public sealed record RestoreCheckpointRequest(bool Force = false);
    public sealed record ChangeSetFilesRequest(IReadOnlyList<string> Paths, bool Force = false);
    public sealed record ChangeSetHunksRequest(string Path, IReadOnlyList<int> HunkIndexes);
    public sealed record WorkspaceActionRequest(string Action,string? Message=null,bool ProtectedConfirmed=false);
    public sealed record RetryRunRequest(string? ProviderProfileId=null);
    public sealed record StartSubagentsRequest(IReadOnlyList<SubagentTask> Tasks);

    public static IEndpointRouteBuilder MapRunEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/runs");
        group.MapGet("/{runId}", GetRun);
        group.MapGet("/{runId}/changeset", GetChangeSet);
        group.MapPost("/{runId}/changeset/restore", RestoreChangeSet);
        group.MapPost("/{runId}/changeset/files/restore", RestoreFiles);
        group.MapPost("/{runId}/changeset/files/accept", AcceptFiles);
        group.MapPost("/{runId}/changeset/hunks/restore", RestoreHunks);
        group.MapPost("/{runId}/changeset/hunks/accept", AcceptHunks);
        group.MapPost("/{runId}/pause",async(string runId,IRunOrchestrator orchestrator,CancellationToken ct)=>{await orchestrator.PauseRunAsync(runId,ct);return Results.Accepted();});
        group.MapPost("/{runId}/resume",async(string runId,IRunOrchestrator orchestrator,CancellationToken ct)=>Results.Ok(await orchestrator.ResumeRunAsync(runId,ct)));
        group.MapPost("/{runId}/retry",async(string runId,RetryRunRequest request,IRunOrchestrator orchestrator,CancellationToken ct)=>{try{return Results.Ok(await orchestrator.RetryRunAsync(runId,request.ProviderProfileId,ct));}catch(KeyNotFoundException){return Results.NotFound();}catch(InvalidOperationException ex){return Results.Conflict(new{error=ex.Message});}});
        group.MapPost("/{runId}/cancel",async(string runId,IRunOrchestrator orchestrator,CancellationToken ct)=>{await orchestrator.CancelRunAsync(runId,ct);return Results.Accepted();});
        group.MapGet("/{runId}/events",async(string runId,long? after,int? limit,IRunEventRepository events,CancellationToken ct)=>Results.Ok(await events.ListAsync(runId,after??0,limit??200,ct)));
        group.MapGet("/{runId}/steps",async(string runId,IRunStepRepository steps,CancellationToken ct)=>Results.Ok(await steps.ListAsync(runId,ct)));
        group.MapGet("/{runId}/context-snapshots",async(string runId,IContextSnapshotRepository snapshots,CancellationToken ct)=>Results.Ok(await snapshots.ListByRunAsync(runId,ct)));
        group.MapPost("/{runId}/workspace/actions",async(string runId,WorkspaceActionRequest request,IRunWorkspaceLifecycleService lifecycle,CancellationToken ct)=>{try{var result=await lifecycle.ExecuteAsync(runId,request.Action,request.Message,request.ProtectedConfirmed,ct);return result.Success?Results.Ok(result):result.RequiresProtectedConfirmation?Results.Conflict(result):Results.BadRequest(result);}catch(KeyNotFoundException){return Results.NotFound();}});
        group.MapGet("/{runId}/workspace/preview",async(string runId,IRunWorkspaceLifecycleService lifecycle,CancellationToken ct)=>{try{return Results.Ok(await lifecycle.PreviewAsync(runId,ct));}catch(KeyNotFoundException){return Results.NotFound();}});
        group.MapPost("/{runId}/subagents",async(string runId,StartSubagentsRequest request,ISubagentCoordinator coordinator,CancellationToken ct)=>{try{return Results.Accepted(value:await coordinator.StartParallelAsync(runId,request.Tasks,ct));}catch(KeyNotFoundException){return Results.NotFound();}catch(ArgumentException ex){return Results.BadRequest(new{error=ex.Message});}});
        return app;
    }

    private static async Task<IResult> GetRun(
        string runId,
        IRunRepository repository,
        CancellationToken ct)
    {
        var run = await repository.GetAsync(runId, ct);
        return run is null ? Results.NotFound() : Results.Ok(run);
    }

    private static async Task<IResult> GetChangeSet(
        string runId,
        IRunRepository repository,
        IChangeSetService changeSetService,
        IRunStepRepository runSteps,
        CancellationToken ct)
    {
        var run = await repository.GetAsync(runId, ct);
        if (run is null)
            return Results.NotFound();
        if (run.CheckpointId is null)
            return Results.Conflict(new { error = "This run has no checkpoint." });
        var changeSet = await changeSetService.GetChangeSetAsync(run.CheckpointId, ct);
        var latestVerify = (await runSteps.ListAsync(runId, ct))
            .Where(step => string.Equals(step.Phase, "verify", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(step => step.Attempt)
            .ThenByDescending(step => step.StartedAt)
            .FirstOrDefault();
        return Results.Ok(new
        {
            changeSet.CheckpointId,
            changeSet.RunId,
            changeSet.WorkspacePath,
            changeSet.CreatedAt,
            changeSet.Files,
            validation = latestVerify is null ? null : new
            {
                latestVerify.Status,
                latestVerify.Attempt,
                latestVerify.ErrorSanitized,
                latestVerify.EndedAt,
            },
        });
    }

    private static async Task<IResult> RestoreChangeSet(
        string runId,
        RestoreCheckpointRequest request,
        IRunRepository repository,
        IChangeSetService changeSetService,
        CancellationToken ct)
    {
        var run = await repository.GetAsync(runId, ct);
        if (run is null)
            return Results.NotFound();
        if (run.CheckpointId is null)
            return Results.Conflict(new { error = "This run has no checkpoint." });

        var result = await changeSetService.RestoreAsync(run.CheckpointId, request.Force, ct);
        return result.Success
            ? Results.Ok(result)
            : Results.Conflict(result);
    }

    private static async Task<IResult> RestoreFiles(
        string runId,
        ChangeSetFilesRequest request,
        IRunRepository repository,
        IChangeSetService changes,
        CancellationToken ct) => await ChangeFiles(runId, request, repository, changes, false, ct);

    private static async Task<IResult> AcceptFiles(
        string runId,
        ChangeSetFilesRequest request,
        IRunRepository repository,
        IChangeSetService changes,
        CancellationToken ct) => await ChangeFiles(runId, request, repository, changes, true, ct);

    private static async Task<IResult> ChangeFiles(
        string runId,
        ChangeSetFilesRequest request,
        IRunRepository repository,
        IChangeSetService changes,
        bool accept,
        CancellationToken ct)
    {
        var run = await repository.GetAsync(runId, ct);
        if (run?.CheckpointId is null)
            return run is null ? Results.NotFound() : Results.Conflict(new { error = "This run has no checkpoint." });
        var result = accept
            ? await changes.AcceptFilesAsync(run.CheckpointId, request.Paths, ct)
            : await changes.RestoreFilesAsync(run.CheckpointId, request.Paths, request.Force, ct);
        return result.Success ? Results.Ok(result) : Results.Conflict(result);
    }

    private static async Task<IResult> RestoreHunks(
        string runId,
        ChangeSetHunksRequest request,
        IRunRepository repository,
        IChangeSetService changes,
        CancellationToken ct)
    {
        var run = await repository.GetAsync(runId, ct);
        if (run?.CheckpointId is null)
            return run is null ? Results.NotFound() : Results.Conflict(new { error = "This run has no checkpoint." });
        var result = await changes.RestoreHunksAsync(run.CheckpointId, request.Path, request.HunkIndexes, ct);
        return result.Success ? Results.Ok(result) : Results.Conflict(result);
    }

    private static async Task<IResult> AcceptHunks(
        string runId,
        ChangeSetHunksRequest request,
        IRunRepository repository,
        IChangeSetService changes,
        CancellationToken ct)
    {
        var run = await repository.GetAsync(runId, ct);
        if (run?.CheckpointId is null)
            return run is null ? Results.NotFound() : Results.Conflict(new { error = "This run has no checkpoint." });
        var result = await changes.AcceptHunksAsync(run.CheckpointId, request.Path, request.HunkIndexes, ct);
        return result.Success ? Results.Ok(result) : Results.Conflict(result);
    }
}
