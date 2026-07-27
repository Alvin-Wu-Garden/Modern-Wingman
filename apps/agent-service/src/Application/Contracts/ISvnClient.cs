using AgentService.Application.Models;

namespace AgentService.Application.Contracts;

public sealed record SvnCommandResult(
    bool Success,
    string Output,
    string? Error = null,
    string? Revision = null);

/// <summary>
/// 專案匯入所需的最小 SVN 能力；刻意不提供 add、delete、move、switch 或 commit。
/// </summary>
public interface ISvnClient
{
    Task<SvnCommandResult> TestConnectionAsync(
        string profileId,
        string repositoryUrl,
        CancellationToken ct = default);

    Task<SvnCommandResult> BrowseAsync(
        string profileId,
        string repositoryUrl,
        CancellationToken ct = default);

    Task<SvnCommandResult> CheckoutAsync(
        string profileId,
        string repositoryUrl,
        string destinationPath,
        CancellationToken ct = default,
        Func<ProcessOutputLine, CancellationToken, ValueTask>? onOutput = null);

    Task<SvnCommandResult> UpdateAsync(
        string profileId,
        string workingCopyPath,
        CancellationToken ct = default);

    Task<SvnCommandResult> StatusAsync(
        string workingCopyPath,
        CancellationToken ct = default);

    Task<SvnCommandResult> GetRevisionAsync(
        string profileId,
        string target,
        CancellationToken ct = default);
}
