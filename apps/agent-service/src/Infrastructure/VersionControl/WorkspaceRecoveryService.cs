using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;

namespace AgentService.Infrastructure.VersionControl;

public sealed class WorkspaceRecoveryService(
    IRunRepository runs,
    IAuditEventRecorder audit,
    ILogger<WorkspaceRecoveryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var terminal=await runs.ListByStatusesAsync([RunStatus.Completed,RunStatus.Failed,RunStatus.Cancelled],stoppingToken);
            foreach(var run in terminal.Where(run=>(run.WorkspaceStrategy is WorkspaceStrategy.GitWorktree or WorkspaceStrategy.SvnShadowGit)&& !string.IsNullOrWhiteSpace(run.ExecutionWorkspacePath)&&Directory.Exists(run.ExecutionWorkspacePath)))
            {
                await audit.RecordAsync(new AuditEventWrite("retained_workspace_detected","agent_run",run.Id,"recover","pending_user_action","system",TraceId:run.TraceId,DetailsJson:System.Text.Json.JsonSerializer.Serialize(new{run.ExecutionWorkspacePath,run.WorkspaceStrategy,run.Branch,run.Status})),stoppingToken);
            }
        }
        catch(Exception ex)when(ex is not OperationCanceledException){logger.LogWarning(ex,"Failed to scan retained Agent workspaces.");}
    }
}
