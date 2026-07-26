using AgentService.Application.Models;

namespace AgentService.Application.Contracts;

public sealed record GitCommandResult(
    bool Success,
    string Output,
    string? Error = null);

/// <summary>
/// 專案匯入所需的最小 Git 能力；刻意不提供 branch、commit、push 或 worktree 寫入操作。
/// </summary>
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

    Task<GitCommandResult> UpdateAsync(
        string profileId,
        string repositoryPath,
        CancellationToken ct = default);

    Task<GitCommandResult> StatusAsync(
        string repositoryPath,
        CancellationToken ct = default);
}
