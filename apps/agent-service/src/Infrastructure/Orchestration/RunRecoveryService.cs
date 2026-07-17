using AgentService.Application.Contracts;
using AgentService.Domain.Models;

namespace AgentService.Infrastructure.Orchestration;

public sealed class RunRecoveryService(IRunRepository repository,ILogger<RunRecoveryService> logger):IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        var interrupted=await repository.ListByStatusesAsync([RunStatus.Running,RunStatus.WaitingApproval],ct);
        foreach(var run in interrupted){run.Status=RunStatus.Paused;run.Error="Agent Service restarted while this run was active. Review the workspace before resuming.";await repository.SaveAsync(run,ct);logger.LogWarning("Recovered interrupted run {RunId} as paused",run.Id);}
    }
    public Task StopAsync(CancellationToken ct)=>Task.CompletedTask;
}
