namespace AgentService.Application.Contracts;

using AgentService.Application.Models;

public sealed record SvnCommandResult(
    bool Success,
    string Output,
    string? Error = null,
    string? Revision = null);

public interface ISvnClient
{
    Task<SvnCommandResult> TestConnectionAsync(string profileId, string repositoryUrl, CancellationToken ct = default);
    Task<SvnCommandResult> BrowseAsync(string profileId, string repositoryUrl, CancellationToken ct = default);
    Task<SvnCommandResult> CheckoutAsync(
        string profileId,
        string repositoryUrl,
        string destinationPath,
        CancellationToken ct = default,
        Func<ProcessOutputLine, CancellationToken, ValueTask>? onOutput = null);
    Task<SvnCommandResult> UpdateAsync(string profileId, string workingCopyPath, CancellationToken ct = default);
    Task<SvnCommandResult> SwitchAsync(string profileId, string workingCopyPath, string repositoryUrl, CancellationToken ct = default);
    Task<SvnCommandResult> StatusAsync(string workingCopyPath, CancellationToken ct = default);
    Task<SvnCommandResult> DiffAsync(string workingCopyPath, CancellationToken ct = default);
    Task<SvnCommandResult> AddAsync(string workingCopyPath, string path, CancellationToken ct = default);
    Task<SvnCommandResult> DeleteAsync(string workingCopyPath, string path, CancellationToken ct = default);
    Task<SvnCommandResult> MoveAsync(string workingCopyPath, string source, string destination, CancellationToken ct = default);
    Task<SvnCommandResult> CommitAsync(string profileId, string workingCopyPath, string message, CancellationToken ct = default);
    Task<SvnCommandResult> GetRevisionAsync(string profileId,string target,CancellationToken ct=default);
}
