using AgentService.Application.Contracts;
using AgentService.Application.Models;

namespace AgentService.Infrastructure.Orchestration;

public sealed class SubagentCoordinator(
    IRunRepository runs,
    IRunOrchestrator orchestrator) : ISubagentCoordinator
{
    public async Task<IReadOnlyList<Domain.Models.RunEntity>> StartParallelAsync(
        string parentRunId,
        IReadOnlyList<SubagentTask> tasks,
        CancellationToken ct = default)
    {
        var parent = await runs.GetAsync(parentRunId, ct) ?? throw new KeyNotFoundException(parentRunId);
        if (tasks.Count is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(tasks), "A parallel group must contain 1-8 subagents.");
        if (tasks.Any(task => string.IsNullOrWhiteSpace(task.Role) || string.IsNullOrWhiteSpace(task.Task)))
            throw new ArgumentException("Every subagent requires a role and task.", nameof(tasks));

        var starts = tasks.Select(task => orchestrator.StartRunAsync(new CreateRunCommand(
            parent.SessionId,
            task.Task,
            task.ProviderProfileId ?? parent.ProviderProfileId,
            parent.WorkspacePath,
            parent.ProjectId,
            parent.Mode,
            parent.WorkspaceStrategy,
            parent.IncludeUncommittedChanges,
            parent.Id,
            task.Role), ct));
        return await Task.WhenAll(starts);
    }
}
