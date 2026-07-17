using AgentService.Application.Models;
using AgentService.Domain.Models;

namespace AgentService.Application.Contracts;

public interface IApprovalCoordinator
{
    Task<ApprovalOutcome> RequestAsync(
        string runId,
        AgentPermissionRequest request,
        CancellationToken ct = default);

    Task<bool> ResolveAsync(
        string approvalId,
        ResolveApprovalCommand command,
        CancellationToken ct = default);
}
