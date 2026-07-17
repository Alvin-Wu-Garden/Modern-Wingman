using AgentService.Application.Models;

namespace AgentService.Application.Contracts;

public interface IProjectIndexManifestStore
{
    Task SaveAttemptAsync(ProjectIndexManifest manifest, CancellationToken ct = default);
    Task PromoteAsync(ProjectIndexManifest manifest, CancellationToken ct = default);
    Task<ProjectIndexManifest?> GetCurrentAsync(string projectId, CancellationToken ct = default);
    Task<ProjectIndexManifest?> GetLatestAttemptAsync(string projectId, CancellationToken ct = default);
    Task<ProjectIndexManifest?> GetByVersionAsync(
        string projectId, string version, CancellationToken ct = default);
    Task DeleteProjectAsync(string projectId, CancellationToken ct = default);
}
