using AgentService.Domain.Models;

namespace AgentService.Application.Contracts;

/// <summary>專案（WS3.1）持久化。</summary>
public interface IProjectRepository
{
    Task<IReadOnlyList<ProjectEntity>> ListAsync(CancellationToken ct = default);
    Task<ProjectEntity?> GetAsync(string projectId, CancellationToken ct = default);
    Task SaveAsync(ProjectEntity project, CancellationToken ct = default);
    Task DeleteAsync(string projectId, CancellationToken ct = default);
}
