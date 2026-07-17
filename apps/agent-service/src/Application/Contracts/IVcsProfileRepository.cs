using AgentService.Domain.Models;

namespace AgentService.Application.Contracts;

public interface IVcsProfileRepository
{
    Task<IReadOnlyList<VcsConnectionProfile>> ListAsync(CancellationToken ct = default);
    Task<VcsConnectionProfile?> GetAsync(string id, CancellationToken ct = default);
    Task SaveAsync(VcsConnectionProfile profile, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
}
