using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;

namespace AgentService.Infrastructure.VersionControl;

/// <summary>只實作專案匯入與同步需要的 SVN 讀取、checkout 與 update 命令。</summary>
public sealed class SvnClient(
    IVcsRuntimeResolver runtimeResolver,
    IVcsProfileRepository profileRepository,
    IProcessRunner processRunner) : ISvnClient
{
    public async Task<SvnCommandResult> TestConnectionAsync(
        string profileId,
        string repositoryUrl,
        CancellationToken ct = default)
    {
        var result = await BrowseAsync(profileId, repositoryUrl, ct);
        return result.Success ? result with { Output = "連線成功。" } : result;
    }

    public Task<SvnCommandResult> BrowseAsync(
        string profileId,
        string repositoryUrl,
        CancellationToken ct = default) =>
        RunAuthenticatedAsync(profileId, ["list", "--xml", repositoryUrl], Environment.CurrentDirectory, ct);

    public async Task<SvnCommandResult> CheckoutAsync(
        string profileId,
        string repositoryUrl,
        string destinationPath,
        CancellationToken ct = default,
        Func<ProcessOutputLine, CancellationToken, ValueTask>? onOutput = null)
    {
        if (Directory.Exists(destinationPath) &&
            Directory.EnumerateFileSystemEntries(destinationPath).Any())
            return Failure("Checkout 目的目錄不是空目錄。");
        var fullDestination = Path.GetFullPath(destinationPath);
        var parent = Path.GetDirectoryName(fullDestination)!;
        Directory.CreateDirectory(parent);
        return await RunAuthenticatedAsync(
            profileId,
            ["checkout", repositoryUrl, fullDestination],
            parent,
            ct,
            onOutput);
    }

    public Task<SvnCommandResult> UpdateAsync(
        string profileId,
        string workingCopyPath,
        CancellationToken ct = default) =>
        RunAuthenticatedAsync(profileId, ["update"], Path.GetFullPath(workingCopyPath), ct);

    public async Task<SvnCommandResult> StatusAsync(
        string workingCopyPath,
        CancellationToken ct = default)
    {
        var runtime = await RequireRuntimeAsync(ct);
        return ToResult(await processRunner.RunAsync(
            new(
                runtime.ExecutablePath!,
                ["status", "--xml"],
                Path.GetFullPath(workingCopyPath),
                TimeSpan.FromMinutes(10)),
            ct));
    }

    public Task<SvnCommandResult> GetRevisionAsync(
        string profileId,
        string target,
        CancellationToken ct = default) =>
        RunAuthenticatedAsync(
            profileId,
            ["info", "--show-item", "revision", target],
            Directory.Exists(target) ? Path.GetFullPath(target) : Environment.CurrentDirectory,
            ct);

    private async Task<SvnCommandResult> RunAuthenticatedAsync(
        string profileId,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken ct,
        Func<ProcessOutputLine, CancellationToken, ValueTask>? onOutput = null)
    {
        var runtime = await RequireRuntimeAsync(ct);
        var profile = await profileRepository.GetAsync(profileId, ct)
            ?? throw new KeyNotFoundException($"找不到 VCS profile：{profileId}");
        if (profile.VcsType != VcsType.Svn)
            throw new InvalidOperationException("選取的 VCS profile 不是 SVN。");

        var authenticatedArguments = arguments.ToList();
        authenticatedArguments.AddRange(["--non-interactive", "--no-auth-cache"]);
        if (!profile.SslVerificationEnabled)
            authenticatedArguments.AddRange(["--trust-server-cert-failures", "unknown-ca,cn-mismatch,expired,not-yet-valid,other"]);
        if (!string.IsNullOrWhiteSpace(profile.Username))
            authenticatedArguments.AddRange(["--username", profile.Username]);
        // SVN 1.10+ 可由標準輸入讀取密碼，避免密碼出現在程序命令列與系統程序清單。
        var standardInput = string.IsNullOrWhiteSpace(profile.SecretValue)
            ? null
            : profile.SecretValue;
        if (standardInput is not null)
            authenticatedArguments.Add("--password-from-stdin");

        return ToResult(await processRunner.RunAsync(
            new(
                runtime.ExecutablePath!,
                authenticatedArguments,
                workingDirectory,
                TimeSpan.FromMinutes(10),
                StandardInput: standardInput,
                OnOutput: onOutput),
            ct));
    }

    private async Task<VcsRuntimeInfo> RequireRuntimeAsync(CancellationToken ct)
    {
        var runtime = await runtimeResolver.ResolveAsync(VcsType.Svn, ct);
        if (!runtime.Available || runtime.ExecutablePath is null)
            throw new InvalidOperationException(runtime.Error ?? "SVN runtime 無法使用。");
        return runtime;
    }

    private static SvnCommandResult ToResult(ProcessExecutionResult result) =>
        new(
            result.ExitCode == 0 && !result.TimedOut,
            result.StandardOutput,
            result.ExitCode == 0 ? null : result.StandardError,
            Revision: result.ExitCode == 0 ? result.StandardOutput.Trim() : null);

    private static SvnCommandResult Failure(string error) => new(false, "", error);
}
