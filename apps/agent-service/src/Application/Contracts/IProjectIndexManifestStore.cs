using AgentService.Application.Models;

namespace AgentService.Application.Contracts;

public interface IProjectIndexManifestStore
{
    Task PromoteAsync(ProjectIndexManifest manifest, CancellationToken ct = default);
    Task<ProjectIndexManifest?> GetCurrentAsync(string projectId, CancellationToken ct = default);
    Task<ProjectIndexManifest?> GetLatestAttemptAsync(string projectId, CancellationToken ct = default);
    Task<ProjectIndexManifest?> GetByVersionAsync(
        string projectId, string version, CancellationToken ct = default);
    Task<IReadOnlyList<ProjectIndexManifest>> ListSuccessfulAsync(
        string projectId, CancellationToken ct = default);
    Task ActivateAsync(string projectId, string version, CancellationToken ct = default);
    Task DeleteVersionAsync(string projectId, string version, CancellationToken ct = default);
    Task PruneSuccessfulAsync(
        string projectId,
        string? previousVersion,
        CancellationToken ct = default);
    Task SaveFileSnapshotAsync(
        string projectId,
        string version,
        IReadOnlyList<ProjectIndexedFile> files,
        CancellationToken ct = default);
    Task<IReadOnlyList<ProjectIndexedFile>> GetFileSnapshotAsync(
        string projectId,
        string version,
        CancellationToken ct = default);
    /// <summary>發布後續步驟失敗時，將本機目前指標恢復到上一個版本。</summary>
    Task RestoreCurrentAsync(
        string projectId, string? previousVersion, CancellationToken ct = default) =>
        Task.CompletedTask;
    Task DeleteProjectAsync(string projectId, CancellationToken ct = default);
}
