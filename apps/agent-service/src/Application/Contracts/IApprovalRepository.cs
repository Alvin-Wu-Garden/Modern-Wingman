using AgentService.Domain.Models;

namespace AgentService.Application.Contracts;

public interface IApprovalRepository
{
    Task SaveAsync(AgentApproval approval, CancellationToken ct = default);
    Task<AgentApproval?> GetAsync(string approvalId, CancellationToken ct = default);
    Task<IReadOnlyList<AgentApproval>> ListPendingByRunAsync(
        string runId,
        CancellationToken ct = default);
}
