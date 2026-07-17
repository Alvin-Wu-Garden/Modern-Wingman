using AgentService.Application.Models;
using AgentService.Domain.Models;

namespace AgentService.Application.Contracts;

public sealed record SubagentTask(string Role, string Task, string? ProviderProfileId = null);

public interface ISubagentCoordinator
{
    Task<IReadOnlyList<RunEntity>> StartParallelAsync(
        string parentRunId,
        IReadOnlyList<SubagentTask> tasks,
        CancellationToken ct = default);
}
