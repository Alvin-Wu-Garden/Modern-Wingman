namespace AgentService.Application.Contracts;

using AgentService.Application.Models;

public sealed record GitCommandResult(
    bool Success,
    string Output,
    string? Error = null,
    string? CommitId = null);

public interface IGitClient
{
    Task<GitCommandResult> TestConnectionAsync(
        string profileId,
        string repositoryUrl,
        CancellationToken ct = default);

    Task<IReadOnlyList<string>> ListRemoteBranchesAsync(
        string profileId,
        string repositoryUrl,
        CancellationToken ct = default);

    Task<GitCommandResult> CloneAsync(
        string profileId,
        string repositoryUrl,
        string branch,
        string destinationPath,
        CancellationToken ct = default,
        Func<ProcessOutputLine, CancellationToken, ValueTask>? onOutput = null);

    Task<GitCommandResult> CreateWorktreeAsync(
        string repositoryPath,
        string worktreePath,
        string branchName,
        string startPoint,
        CancellationToken ct = default);

    Task<GitCommandResult> FetchAsync(
        string profileId,
        string repositoryPath,
        string remote,
        CancellationToken ct = default);

    Task<GitCommandResult> PullAsync(
        string profileId,
        string repositoryPath,
        string remote,
        string branch,
        CancellationToken ct = default);

    Task<GitCommandResult> SwitchAsync(
        string repositoryPath,
        string branch,
        bool create,
        string? startPoint,
        CancellationToken ct = default);

    Task<GitCommandResult> StatusAsync(
        string repositoryPath,
        CancellationToken ct = default);

    Task<GitCommandResult> DiffAsync(
        string repositoryPath,
        bool staged,
        CancellationToken ct = default);

    Task<GitCommandResult> BranchesAsync(string repositoryPath,CancellationToken ct=default);

    Task<GitCommandResult> CommitAsync(
        string profileId,
        string repositoryPath,
        string message,
        CancellationToken ct = default);

    Task<GitCommandResult> PushAsync(
        string profileId,
        string repositoryPath,
        string remote,
        string branch,
        CancellationToken ct = default);
}
