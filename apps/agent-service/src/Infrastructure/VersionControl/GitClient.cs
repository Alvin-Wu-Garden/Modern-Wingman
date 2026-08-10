using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;

namespace AgentService.Infrastructure.VersionControl;

/// <summary>只實作專案匯入與同步需要的 Git 唯讀／快轉更新命令。</summary>
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
            return new(true, "連線成功。");
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
        var result = await RunAsync(
            runtime,
            ["ls-remote", "--heads", credential.AddUsername(repositoryUrl, profile.Username)],
            Environment.CurrentDirectory,
            credential.Variables,
            ct);
        EnsureSuccess(result, "無法列出遠端分支");
        return result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t').LastOrDefault()?.Trim() ?? "")
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
        if (Directory.Exists(destinationPath) &&
            Directory.EnumerateFileSystemEntries(destinationPath).Any())
            return Failure("Clone 目的目錄不是空目錄。");

        var (runtime, profile) = await ResolveAsync(profileId, ct);
        using var credential = new GitCredentialEnvironment(profile);
        var fullDestination = Path.GetFullPath(destinationPath);
        var parent = Path.GetDirectoryName(fullDestination)!;
        Directory.CreateDirectory(parent);
        var result = await RunAsync(
            runtime,
            [
                "clone", "--branch", branch, "--single-branch", "--",
                credential.AddUsername(repositoryUrl, profile.Username),
                fullDestination,
            ],
            parent,
            credential.Variables,
            ct,
            onOutput);
        return ToResult(result);
    }

    public async Task<GitCommandResult> UpdateAsync(
        string profileId,
        string repositoryPath,
        CancellationToken ct = default)
    {
        var (runtime, profile) = await ResolveAsync(profileId, ct);
        using var credential = new GitCredentialEnvironment(profile);
        // 只允許 fast-forward，避免 Modern Wingman 自動產生 merge commit。
        return ToResult(await RunAsync(
            runtime,
            ["pull", "--ff-only"],
            Path.GetFullPath(repositoryPath),
            credential.Variables,
            ct));
    }

    public async Task<GitCommandResult> StatusAsync(
        string repositoryPath,
        CancellationToken ct = default)
    {
        var runtime = await RequireRuntimeAsync(ct);
        return ToResult(await RunAsync(
            runtime,
            ["status", "--porcelain=v2", "--branch"],
            Path.GetFullPath(repositoryPath),
            null,
            ct));
    }

    private async Task<(VcsRuntimeInfo Runtime, VcsConnectionProfile Profile)> ResolveAsync(
        string profileId,
        CancellationToken ct)
    {
        var profile = await profileRepository.GetAsync(profileId, ct)
            ?? throw new KeyNotFoundException($"找不到 VCS profile：{profileId}");
        if (profile.VcsType != VcsType.Git)
            throw new InvalidOperationException("選取的 VCS profile 不是 Git。");
        return (await RequireRuntimeAsync(ct), profile);
    }

    private async Task<VcsRuntimeInfo> RequireRuntimeAsync(CancellationToken ct)
    {
        var runtime = await runtimeResolver.ResolveAsync(VcsType.Git, ct);
        if (!runtime.Available || runtime.ExecutablePath is null)
            throw new InvalidOperationException(runtime.Error ?? "Git runtime 無法使用。");
        return runtime;
    }

    private Task<ProcessExecutionResult> RunAsync(
        VcsRuntimeInfo runtime,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment,
        CancellationToken ct,
        Func<ProcessOutputLine, CancellationToken, ValueTask>? onOutput = null) =>
        processRunner.RunAsync(
            new(
                runtime.ExecutablePath!,
                arguments,
                workingDirectory,
                TimeSpan.FromMinutes(10),
                environment,
                OnOutput: onOutput),
            ct);

    private static void EnsureSuccess(ProcessExecutionResult result, string message)
    {
        if (result.ExitCode != 0 || result.TimedOut)
            throw new InvalidOperationException($"{message}：{result.StandardError}");
    }

    private static GitCommandResult ToResult(ProcessExecutionResult result) =>
        new(
            result.ExitCode == 0 && !result.TimedOut,
            result.StandardOutput,
            result.ExitCode == 0 ? null : result.StandardError);

    private static GitCommandResult Failure(string error) => new(false, "", error);
}
