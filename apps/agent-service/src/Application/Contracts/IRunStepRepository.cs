using AgentService.Domain.Models;

namespace AgentService.Application.Contracts;

public interface IRunStepRepository
{
    Task SaveAsync(RunStep step, CancellationToken ct = default);
    Task<RunStep?> GetActiveAsync(string runId, CancellationToken ct = default);
    Task<IReadOnlyList<RunStep>> ListAsync(string runId, CancellationToken ct = default);
}
