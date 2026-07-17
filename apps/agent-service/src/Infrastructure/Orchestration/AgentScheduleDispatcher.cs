using AgentService.Application.Contracts;
using AgentService.Application.Models;

namespace AgentService.Infrastructure.Orchestration;

public sealed class AgentScheduleDispatcher(
    IAgentScheduleStore schedules,
    IRunOrchestrator orchestrator,
    ILogger<AgentScheduleDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        do
        {
            foreach (var schedule in await schedules.ListDueAsync(DateTimeOffset.UtcNow, stoppingToken))
                await DispatchAsync(schedule, stoppingToken);
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task DispatchAsync(AgentSchedule schedule, CancellationToken ct)
    {
        schedule.Enabled = schedule.IntervalMinutes is > 0;
        schedule.NextRunAt = schedule.IntervalMinutes is > 0
            ? DateTimeOffset.UtcNow.AddMinutes(schedule.IntervalMinutes.Value)
            : DateTimeOffset.MaxValue;
        try
        {
            var run = await orchestrator.StartRunAsync(new CreateRunCommand(
                $"schedule:{schedule.Id}", schedule.Task, schedule.ProviderProfileId,
                schedule.WorkspacePath, schedule.ProjectId, schedule.Mode), ct);
            schedule.LastRunId = run.Id;
            schedule.LastError = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            schedule.LastError = ex.Message;
            logger.LogError(ex, "Scheduled Agent task {ScheduleId} failed to dispatch", schedule.Id);
        }
        await schedules.SaveAsync(schedule, CancellationToken.None);
    }
}
