using AgentService.Domain.Models;

namespace AgentService.Application.Contracts;

public interface IVcsStateRepository
{
    Task<ProjectVcsBinding?> GetBindingAsync(string projectId, CancellationToken ct = default);
    Task SaveBindingAsync(ProjectVcsBinding binding, CancellationToken ct = default);
}
