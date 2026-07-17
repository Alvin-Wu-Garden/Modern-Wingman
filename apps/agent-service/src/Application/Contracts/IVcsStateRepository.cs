using AgentService.Domain.Models;

namespace AgentService.Application.Contracts;

public interface IVcsStateRepository
{
    Task<ProjectVcsBinding?> GetBindingAsync(string projectId, CancellationToken ct = default);
    Task SaveBindingAsync(ProjectVcsBinding binding, CancellationToken ct = default);
    Task<IReadOnlyList<VcsProtectedRef>> ListProtectedRefsAsync(
        VcsType type, string? projectId, CancellationToken ct = default);
    Task SaveProtectedRefAsync(VcsProtectedRef rule, CancellationToken ct = default);
    Task DeleteProtectedRefAsync(string id, CancellationToken ct = default);
    Task SaveOperationAsync(VcsOperation operation, CancellationToken ct = default);
}

public interface IProtectedRefMatcher
{
    Task<bool> IsProtectedAsync(
        VcsType type, string reference, string? projectId = null, CancellationToken ct = default);
}
