namespace AgentService.Application.Contracts;

public sealed record ShadowGitBaseline(
    string RepositoryPath,
    string SvnWorkingCopyPath,
    string SvnUrl,
    string SvnRevision,
    string CommitId);

public interface IShadowGitService
{
    Task<ShadowGitBaseline> InitializeAsync(
        string svnWorkingCopyPath,
        string shadowRepositoryPath,
        string svnUrl,
        string svnRevision,
        CancellationToken ct = default);

    Task<GitCommandResult> CreateRunWorktreeAsync(
        string shadowRepositoryPath,
        string worktreePath,
        string branchName,
        CancellationToken ct = default);
    Task<ShadowApplyResult> ApplyToSvnAsync(string profileId,string shadowRepositoryPath,string runWorktreePath,CancellationToken ct=default);
}

public sealed record ShadowApplyResult(bool Success,bool RevisionConflict,IReadOnlyList<string> Added,IReadOnlyList<string> Modified,IReadOnlyList<string> Deleted,string? Error=null,IReadOnlyList<string>? Renamed=null);
