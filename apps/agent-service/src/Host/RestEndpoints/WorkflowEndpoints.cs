using AgentService.Application.Contracts;
using AgentService.Infrastructure.Workflow;
using AgentService.Domain.Models;

namespace AgentService.Host.RestEndpoints;

/// <summary>
/// WS4 工作流 REST 端點。
///
/// POST /api/workflow/run   → 啟動 Explore→Plan→Code→Verify 工作流（背景執行）
/// POST /api/workflow/plan  → Plan mode：只產出計畫（同步回傳）
/// </summary>
public static class WorkflowEndpoints
{
    public sealed record WorkflowRequest(
        string Task,
        string WorkspacePath,
        string? ProjectId,
        int? MaxVerifyAttempts,
        string? AgentMode = null,
        string? WorkspaceStrategy = null,
        bool IncludeUncommittedChanges = true);

    public static IEndpointRouteBuilder MapWorkflowEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/workflow");

        group.MapPost("/run", StartRun);
        group.MapPost("/plan", MakePlan);
        group.MapPost("/{runId}/approve",ApprovePlan);

        return app;
    }

    private static async Task<IResult> StartRun(
        WorkflowRequest request,
        ExplorePlanCodeVerifyWorkflow workflow,
        IRunEventBus eventBus,
        IRunExecutionQueue executionQueue,
        IRunRepository runs,
        IChangeSetService changes,
        IRunWorkspaceManager workspaceManager,
        CancellationToken ct)
    {
        if (!Directory.Exists(request.WorkspacePath))
            return Results.BadRequest(new { error = $"目錄不存在: {request.WorkspacePath}" });

        var runId = Guid.NewGuid().ToString("N");
        var strategy = workspaceManager.ResolveStrategy(
            request.WorkspacePath,
            ParseWorkspaceStrategy(request.WorkspaceStrategy));
        var run=new RunEntity(runId){SessionId=request.ProjectId??"workflow",UserMessage=request.Task,WorkspacePath=request.WorkspacePath,ProjectId=request.ProjectId,Mode=ParseMode(request.AgentMode),WorkspaceStrategy=strategy,IncludeUncommittedChanges=request.IncludeUncommittedChanges};await runs.SaveAsync(run,ct);
        eventBus.Subscribe(runId); // 預先建立 channel，避免早期事件遺失

        var workflowRequest = new WorkflowRunRequest(
            runId, request.Task, request.WorkspacePath, request.ProjectId,
            PlanOnly: false,
            MaxVerifyAttempts: request.MaxVerifyAttempts ?? 3,
            Mode: ParseMode(request.AgentMode),
            TraceId: run.TraceId);

        await executionQueue.EnqueueAsync(async workerCt =>
        {
            try
            {
                run.Status=RunStatus.Running;run.StartedAt=DateTimeOffset.UtcNow;
                var prepared=await workspaceManager.PrepareAsync(run,workerCt);run.ExecutionWorkspacePath=prepared.Path;run.Branch=prepared.Branch;run.BaseRevision=prepared.BaseRevision;
                var effectivePath=prepared.Path??request.WorkspacePath;run.CheckpointId=await changes.CreateCheckpointAsync(run.Id,effectivePath,workerCt);await runs.SaveAsync(run,workerCt);
                await workflow.RunAsync(workflowRequest with { WorkspacePath=effectivePath }, workerCt);
                run.Status=RunStatus.Completed;run.EndedAt=DateTimeOffset.UtcNow;await runs.SaveAsync(run,CancellationToken.None);
            }
            catch (Exception ex)
            {
                run.Status=RunStatus.Failed;run.Error=ex.Message;run.EndedAt=DateTimeOffset.UtcNow;await runs.SaveAsync(run,CancellationToken.None);
            }
            finally
            {
                eventBus.Complete(runId);
            }
        },ct);

        return Results.Accepted(value: new { runId });
    }

    private static async Task<IResult> MakePlan(
        WorkflowRequest request,
        ExplorePlanCodeVerifyWorkflow workflow,
        IRunEventBus eventBus,
        IRunRepository runs,
        IRunWorkspaceManager workspaceManager,
        CancellationToken ct)
    {
        if (!Directory.Exists(request.WorkspacePath))
            return Results.BadRequest(new { error = $"目錄不存在: {request.WorkspacePath}" });

        var runId = Guid.NewGuid().ToString("N");
        var strategy=workspaceManager.ResolveStrategy(request.WorkspacePath,ParseWorkspaceStrategy(request.WorkspaceStrategy));
        var run=new RunEntity(runId){SessionId=request.ProjectId??"workflow",UserMessage=request.Task,WorkspacePath=request.WorkspacePath,ProjectId=request.ProjectId,Mode=AgentMode.Plan,WorkspaceStrategy=strategy,IncludeUncommittedChanges=request.IncludeUncommittedChanges,Status=RunStatus.Running,StartedAt=DateTimeOffset.UtcNow};await runs.SaveAsync(run,ct);
        eventBus.Subscribe(runId);
        try
        {
            var plan = await workflow.RunAsync(
                new WorkflowRunRequest(
                    runId, request.Task, request.WorkspacePath, request.ProjectId,
                    PlanOnly: true,
                    TraceId: run.TraceId),
                ct);
            run.Status=RunStatus.WaitingApproval;await runs.SaveAsync(run,ct);
            return Results.Ok(new { runId, plan });
        }
        finally
        {
            eventBus.Complete(runId);
        }
    }

    private static async Task<IResult> ApprovePlan(string runId,IRunRepository runs,ExplorePlanCodeVerifyWorkflow workflow,IRunEventBus events,IRunExecutionQueue queue,IChangeSetService changes,IRunWorkspaceManager workspaceManager,CancellationToken ct)
    {
        var run=await runs.GetAsync(runId,ct);if(run is null)return Results.NotFound();if(run.Status!=RunStatus.WaitingApproval||string.IsNullOrWhiteSpace(run.WorkspacePath))return Results.Conflict(new{error="Run is not waiting for plan approval."});run.Mode=AgentMode.Auto;run.Status=RunStatus.Created;await runs.SaveAsync(run,ct);events.Subscribe(run.Id);await queue.EnqueueAsync(async workerCt=>{try{run.Status=RunStatus.Running;var prepared=await workspaceManager.PrepareAsync(run,workerCt);run.ExecutionWorkspacePath=prepared.Path;run.Branch=prepared.Branch;run.BaseRevision=prepared.BaseRevision;var effectivePath=prepared.Path??run.WorkspacePath;run.CheckpointId=await changes.CreateCheckpointAsync(run.Id,effectivePath,workerCt);await runs.SaveAsync(run,workerCt);await workflow.RunAsync(new(run.Id,run.UserMessage,effectivePath,run.ProjectId,false,3,AgentMode.Auto,run.TraceId),workerCt);run.Status=RunStatus.Completed;run.EndedAt=DateTimeOffset.UtcNow;}catch(Exception ex){run.Status=RunStatus.Failed;run.Error=ex.Message;run.EndedAt=DateTimeOffset.UtcNow;}finally{await runs.SaveAsync(run,CancellationToken.None);events.Complete(run.Id);}},ct);return Results.Accepted(value:new{runId,mode="auto"});
    }

    private static AgentMode ParseMode(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        "full_auto" or "fullauto" => AgentMode.FullAuto,
        "ask" => AgentMode.Ask,
        "plan" => AgentMode.Plan,
        _ => AgentMode.Auto,
    };

    private static WorkspaceStrategy ParseWorkspaceStrategy(string? strategy) =>
        strategy?.Trim().ToLowerInvariant() switch
        {
            "git_worktree" or "gitworktree" => WorkspaceStrategy.GitWorktree,
            "svn_shadow_git" or "svnshadowgit" => WorkspaceStrategy.SvnShadowGit,
            "snapshot" => WorkspaceStrategy.Snapshot,
            _ => WorkspaceStrategy.Direct,
        };
}
