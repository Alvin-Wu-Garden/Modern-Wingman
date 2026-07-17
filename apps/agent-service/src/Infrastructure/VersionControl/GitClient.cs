using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;

namespace AgentService.Infrastructure.VersionControl;

public sealed class GitClient(
    IVcsRuntimeResolver runtimeResolver,
    IVcsProfileRepository profileRepository,
    IProcessRunner processRunner) : IGitClient
{
    public async Task<GitCommandResult> TestConnectionAsync(
        string profileId,
        string repositoryUrl,
        CancellationToken ct = default)
    {
        try
        {
            await ListRemoteBranchesAsync(profileId, repositoryUrl, ct);
            return new GitCommandResult(true, "Connection successful.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or UriFormatException)
        {
            return Failure(ex.Message);
        }
    }

    public async Task<IReadOnlyList<string>> ListRemoteBranchesAsync(
        string profileId,
        string repositoryUrl,
        CancellationToken ct = default)
    {
        var (runtime, profile) = await ResolveAsync(profileId, ct);
        using var credential = new GitCredentialEnvironment(profile);
        var remoteUrl = credential.AddUsername(repositoryUrl, profile.Username);
        var result = await RunAsync(
            runtime,
            ["ls-remote", "--heads", remoteUrl],
            Environment.CurrentDirectory,
            credential.Variables,
            ct);
        EnsureSuccess(result, "Unable to list Git branches");
        return result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => (line.Split('\t').LastOrDefault() ?? "").Trim())
            .Where(reference => reference.StartsWith("refs/heads/", StringComparison.Ordinal))
            .Select(reference => reference["refs/heads/".Length..])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(branch => branch, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<GitCommandResult> CloneAsync(
        string profileId,
        string repositoryUrl,
        string branch,
        string destinationPath,
        CancellationToken ct = default,
        Func<ProcessOutputLine, CancellationToken, ValueTask>? onOutput = null)
    {
        if (Directory.Exists(destinationPath) && Directory.EnumerateFileSystemEntries(destinationPath).Any())
            return Failure("Clone destination is not empty.");
        var (runtime, profile) = await ResolveAsync(profileId, ct);
        using var credential = new GitCredentialEnvironment(profile);
        var remoteUrl = credential.AddUsername(repositoryUrl, profile.Username);
        var parent = Path.GetDirectoryName(Path.GetFullPath(destinationPath))!;
        Directory.CreateDirectory(parent);
        var result = await RunAsync(
            runtime,
            ["clone", "--branch", branch, "--single-branch", "--", remoteUrl, Path.GetFullPath(destinationPath)],
            parent,
            credential.Variables,
            ct,
            onOutput);
        return ToResult(result);
    }

    public async Task<GitCommandResult> CreateWorktreeAsync(
        string repositoryPath,
        string worktreePath,
        string branchName,
        string startPoint,
        CancellationToken ct = default)
    {
        var runtime = await RequireRuntimeAsync(ct);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(worktreePath))!);
        var result = await RunAsync(
            runtime,
            ["worktree", "add", "-b", branchName, Path.GetFullPath(worktreePath), startPoint],
            repositoryPath,
            null,
            ct);
        return ToResult(result);
    }

    public async Task<GitCommandResult> FetchAsync(
        string profileId,
        string repositoryPath,
        string remote,
        CancellationToken ct = default)
    {
        var (runtime, profile) = await ResolveAsync(profileId, ct);
        using var credential = new GitCredentialEnvironment(profile);
        return ToResult(await RunAsync(
            runtime, ["fetch", "--prune", remote], repositoryPath, credential.Variables, ct));
    }

    public async Task<GitCommandResult> PullAsync(
        string profileId,
        string repositoryPath,
        string remote,
        string branch,
        CancellationToken ct = default)
    {
        var (runtime, profile) = await ResolveAsync(profileId, ct);
        using var credential = new GitCredentialEnvironment(profile);
        return ToResult(await RunAsync(
            runtime, ["pull", "--ff-only", remote, branch], repositoryPath, credential.Variables, ct));
    }

    public async Task<GitCommandResult> SwitchAsync(
        string repositoryPath,
        string branch,
        bool create,
        string? startPoint,
        CancellationToken ct = default)
    {
        var runtime = await RequireRuntimeAsync(ct);
        IReadOnlyList<string> arguments = create
            ? string.IsNullOrWhiteSpace(startPoint)
                ? ["switch", "-c", branch]
                : ["switch", "-c", branch, startPoint]
            : ["switch", branch];
        return ToResult(await RunAsync(runtime, arguments, repositoryPath, null, ct));
    }

    public async Task<GitCommandResult> StatusAsync(
        string repositoryPath,
        CancellationToken ct = default)
    {
        var runtime = await RequireRuntimeAsync(ct);
        return ToResult(await RunAsync(
            runtime, ["status", "--porcelain=v2", "--branch"], repositoryPath, null, ct));
    }

    public async Task<GitCommandResult> DiffAsync(
        string repositoryPath,
        bool staged,
        CancellationToken ct = default)
    {
        var runtime = await RequireRuntimeAsync(ct);
        IReadOnlyList<string> arguments = staged
            ? ["diff", "--cached", "--no-ext-diff"]
            : ["diff", "--no-ext-diff"];
        return ToResult(await RunAsync(runtime, arguments, repositoryPath, null, ct));
    }

    public async Task<GitCommandResult> BranchesAsync(string repositoryPath,CancellationToken ct=default)
    {
        var runtime=await RequireRuntimeAsync(ct);return ToResult(await RunAsync(runtime,["branch","--format=%(refname:short)"],repositoryPath,null,ct));
    }

    public async Task<GitCommandResult> CommitAsync(
        string profileId,
        string repositoryPath,
        string message,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message))
            return Failure("Commit message is required.");
        var (runtime, profile) = await ResolveAsync(profileId, ct);
        var identity = await ResolveIdentityAsync(runtime, repositoryPath, profile, ct);
        var add = await RunAsync(runtime, ["add", "--all"], repositoryPath, null, ct);
        if (add.ExitCode != 0) return ToResult(add);
        var commit = await RunAsync(
            runtime,
            ["-c", $"user.name={identity.Name}", "-c", $"user.email={identity.Email}", "commit", "-m", message],
            repositoryPath,
            null,
            ct);
        if (commit.ExitCode != 0) return ToResult(commit);
        var rev = await RunAsync(runtime, ["rev-parse", "HEAD"], repositoryPath, null, ct);
        return new GitCommandResult(true, commit.StandardOutput, CommitId: rev.StandardOutput.Trim());
    }

    public async Task<GitCommandResult> PushAsync(
        string profileId,
        string repositoryPath,
        string remote,
        string branch,
        CancellationToken ct = default)
    {
        var (runtime, profile) = await ResolveAsync(profileId, ct);
        using var credential = new GitCredentialEnvironment(profile);
        var fetch = await RunAsync(
            runtime, ["fetch", "--prune", remote], repositoryPath, credential.Variables, ct);
        if (fetch.ExitCode != 0)
            return ToResult(fetch);

        var remoteRef = $"refs/remotes/{remote}/{branch}";
        var remoteExists = await RunAsync(runtime, ["show-ref", "--verify", "--quiet", remoteRef], repositoryPath, null, ct);
        if (remoteExists.ExitCode == 0)
        {
            var ancestor = await RunAsync(
                runtime, ["merge-base", "--is-ancestor", remoteRef, branch], repositoryPath, null, ct);
            if (ancestor.ExitCode != 0)
                return Failure("Remote branch has advanced. Pull and resolve changes before pushing.");
        }

        var result = await RunAsync(runtime, ["push", "--", remote, branch], repositoryPath, credential.Variables, ct);
        return ToResult(result);
    }

    private async Task<(VcsRuntimeInfo Runtime, VcsConnectionProfile Profile)> ResolveAsync(
        string profileId,
        CancellationToken ct)
    {
        var profile = await profileRepository.GetAsync(profileId, ct)
            ?? throw new KeyNotFoundException($"VCS profile not found: {profileId}");
        if (profile.VcsType != VcsType.Git)
            throw new InvalidOperationException("The selected profile is not a Git profile.");
        return (await RequireRuntimeAsync(ct), profile);
    }

    private async Task<VcsRuntimeInfo> RequireRuntimeAsync(CancellationToken ct)
    {
        var runtime = await runtimeResolver.ResolveAsync(VcsType.Git, ct);
        if (!runtime.Available || runtime.ExecutablePath is null)
            throw new InvalidOperationException(runtime.Error ?? "Git runtime is unavailable.");
        return runtime;
    }

    private Task<ProcessExecutionResult> RunAsync(
        VcsRuntimeInfo runtime,
        IReadOnlyList<string> arguments,
        string cwd,
        IReadOnlyDictionary<string, string?>? environment,
        CancellationToken ct,
        Func<ProcessOutputLine, CancellationToken, ValueTask>? onOutput = null) => processRunner.RunAsync(
        new ProcessInvocation(runtime.ExecutablePath!, arguments, cwd, TimeSpan.FromMinutes(10), environment, OnOutput: onOutput),
        ct);

    private async Task<(string Name, string Email)> ResolveIdentityAsync(
        VcsRuntimeInfo runtime,
        string repositoryPath,
        VcsConnectionProfile profile,
        CancellationToken ct)
    {
        var name = await RunAsync(runtime, ["config", "user.name"], repositoryPath, null, ct);
        var email = await RunAsync(runtime, ["config", "user.email"], repositoryPath, null, ct);
        var resolvedName = !string.IsNullOrWhiteSpace(profile.CommitAuthorName)
            ? profile.CommitAuthorName
            : name.ExitCode == 0 && !string.IsNullOrWhiteSpace(name.StandardOutput)
            ? name.StandardOutput.Trim()
            : profile.Username ?? "Modern Wingman";
        var resolvedEmail = !string.IsNullOrWhiteSpace(profile.CommitAuthorEmail)
            ? profile.CommitAuthorEmail
            : email.ExitCode == 0 && !string.IsNullOrWhiteSpace(email.StandardOutput)
            ? email.StandardOutput.Trim()
            : $"{SanitizeIdentity(profile.Username ?? "wingman")}@wingman.local";
        return (resolvedName, resolvedEmail);
    }

    private static string SanitizeIdentity(string value) =>
        new(value.Where(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_').ToArray());

    private static void EnsureSuccess(ProcessExecutionResult result, string message)
    {
        if (result.ExitCode != 0 || result.TimedOut)
            throw new InvalidOperationException($"{message}: {result.StandardError}");
    }

    private static GitCommandResult ToResult(ProcessExecutionResult result) =>
        new(result.ExitCode == 0 && !result.TimedOut, result.StandardOutput,
            result.ExitCode == 0 ? null : result.StandardError);

    private static GitCommandResult Failure(string error) => new(false, "", error);
}
