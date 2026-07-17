using AgentService.Application.Models;

namespace AgentService.Application.Contracts;

public interface IChangeSetService
{
    Task<string> CreateCheckpointAsync(
        string runId,
        string workspacePath,
        CancellationToken ct = default);

    Task<ChangeSet> GetChangeSetAsync(
        string checkpointId,
        CancellationToken ct = default);

    Task<RestoreCheckpointResult> RestoreAsync(
        string checkpointId,
        bool force = false,
        CancellationToken ct = default);

    Task<RestoreCheckpointResult> RestoreFilesAsync(
        string checkpointId,
        IReadOnlyCollection<string> relativePaths,
        bool force = false,
        CancellationToken ct = default);

    Task<RestoreCheckpointResult> AcceptFilesAsync(
        string checkpointId,
        IReadOnlyCollection<string> relativePaths,
        CancellationToken ct = default);

    Task<RestoreCheckpointResult> ApplyToWorkspaceAsync(
        string checkpointId,
        string targetWorkspacePath,
        CancellationToken ct = default);

    Task<RestoreCheckpointResult> RestoreHunksAsync(
        string checkpointId,
        string relativePath,
        IReadOnlyCollection<int> hunkIndexes,
        CancellationToken ct = default);

    Task<RestoreCheckpointResult> AcceptHunksAsync(
        string checkpointId,
        string relativePath,
        IReadOnlyCollection<int> hunkIndexes,
        CancellationToken ct = default);
}
