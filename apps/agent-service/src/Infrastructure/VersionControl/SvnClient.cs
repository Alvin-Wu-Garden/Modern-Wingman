using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;
using AgentService.Infrastructure.Tools;

namespace AgentService.Infrastructure.VersionControl;

public sealed class SvnClient(
    IVcsRuntimeResolver runtimeResolver,
    IVcsProfileRepository profileRepository,
    IProcessRunner processRunner) : ISvnClient
{
    public async Task<SvnCommandResult> TestConnectionAsync(string profileId, string repositoryUrl, CancellationToken ct = default)
    {
        var result = await BrowseAsync(profileId, repositoryUrl, ct);
        return result.Success ? result with { Output = "Connection successful." } : result;
    }

    public Task<SvnCommandResult> BrowseAsync(string profileId, string repositoryUrl, CancellationToken ct = default) =>
        RunAuthenticatedAsync(profileId, ["list", "--xml", repositoryUrl], Environment.CurrentDirectory, ct);

    public async Task<SvnCommandResult> CheckoutAsync(
        string profileId,
        string repositoryUrl,
        string destinationPath,
        CancellationToken ct = default,
        Func<ProcessOutputLine, CancellationToken, ValueTask>? onOutput = null)
    {
        if (Directory.Exists(destinationPath) && Directory.EnumerateFileSystemEntries(destinationPath).Any())
            return Failure("Checkout destination is not empty.");
        var fullPath = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        return await RunAuthenticatedAsync(profileId, ["checkout", repositoryUrl, fullPath], Path.GetDirectoryName(fullPath)!, ct, onOutput);
    }

    public Task<SvnCommandResult> UpdateAsync(string profileId, string workingCopyPath, CancellationToken ct = default) =>
        RunAuthenticatedAsync(profileId, ["update"], workingCopyPath, ct);

    public Task<SvnCommandResult> SwitchAsync(string profileId, string workingCopyPath, string repositoryUrl, CancellationToken ct = default) =>
        RunAuthenticatedAsync(profileId, ["switch", repositoryUrl], workingCopyPath, ct);

    public Task<SvnCommandResult> StatusAsync(string workingCopyPath, CancellationToken ct = default) =>
        RunLocalAsync(["status", "--xml"], workingCopyPath, ct);

    public Task<SvnCommandResult> DiffAsync(string workingCopyPath, CancellationToken ct = default) =>
        RunLocalAsync(["diff", "--internal-diff"], workingCopyPath, ct);

    public Task<SvnCommandResult> AddAsync(string workingCopyPath, string path, CancellationToken ct = default)
    {
        var fullPath=WorkspacePathGuard.Resolve(workingCopyPath,path);return RunLocalAsync(["add","--parents","--",fullPath],workingCopyPath,ct);
    }

    public Task<SvnCommandResult> DeleteAsync(string workingCopyPath, string path, CancellationToken ct = default) =>
        RunLocalPathAsync("delete", workingCopyPath, path, ct);

    public async Task<SvnCommandResult> MoveAsync(string workingCopyPath, string source, string destination, CancellationToken ct = default)
    {
        var sourcePath = WorkspacePathGuard.Resolve(workingCopyPath, source);
        var destinationPath = WorkspacePathGuard.Resolve(workingCopyPath, destination);
        return await RunLocalAsync(["move", "--", sourcePath, destinationPath], workingCopyPath, ct);
    }

    public async Task<SvnCommandResult> CommitAsync(string profileId, string workingCopyPath, string message, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message))
            return Failure("Commit message is required.");
        return await RunAuthenticatedAsync(profileId, ["commit", "-m", message], workingCopyPath, ct);
    }

    public Task<SvnCommandResult> GetRevisionAsync(string profileId,string target,CancellationToken ct=default)=>RunAuthenticatedAsync(profileId,["info","--show-item","revision",target],Directory.Exists(target)?target:Environment.CurrentDirectory,ct);

    private async Task<SvnCommandResult> RunLocalPathAsync(string command, string workingCopyPath, string path, CancellationToken ct)
    {
        var fullPath = WorkspacePathGuard.Resolve(workingCopyPath, path);
        return await RunLocalAsync([command, "--", fullPath], workingCopyPath, ct);
    }

    private async Task<SvnCommandResult> RunLocalAsync(IReadOnlyList<string> args, string cwd, CancellationToken ct)
    {
        var runtime = await RequireRuntimeAsync(ct);
        return ToResult(await RunAsync(runtime, args, cwd, null, ct));
    }

    private async Task<SvnCommandResult> RunAuthenticatedAsync(
        string profileId,
        IReadOnlyList<string> args,
        string cwd,
        CancellationToken ct,
        Func<ProcessOutputLine, CancellationToken, ValueTask>? onOutput = null)
    {
        var profile = await profileRepository.GetAsync(profileId, ct)
            ?? throw new KeyNotFoundException($"VCS profile not found: {profileId}");
        if (profile.VcsType != VcsType.Svn)
            throw new InvalidOperationException("The selected profile is not an SVN profile.");
        var runtime = await RequireRuntimeAsync(ct);
        var command = args.ToList();
        command.Add("--non-interactive");
        command.Add("--no-auth-cache");
        if (!string.IsNullOrWhiteSpace(profile.Username))
        {
            command.Add("--username");
            command.Add(profile.Username);
        }
        if (profile.HasSecret)
            command.Add("--password-from-stdin");
        if (!profile.SslVerificationEnabled)
            command.Add("--trust-server-cert-failures=unknown-ca,cn-mismatch,expired,not-yet-valid,other");
        return ToResult(await RunAsync(runtime, command, cwd, profile.SecretValue, ct, onOutput));
    }

    private async Task<VcsRuntimeInfo> RequireRuntimeAsync(CancellationToken ct)
    {
        var runtime = await runtimeResolver.ResolveAsync(VcsType.Svn, ct);
        if (!runtime.Available || runtime.ExecutablePath is null)
            throw new InvalidOperationException(runtime.Error ?? "SVN runtime is unavailable.");
        return runtime;
    }

    private Task<ProcessExecutionResult> RunAsync(
        VcsRuntimeInfo runtime,
        IReadOnlyList<string> args,
        string cwd,
        string? input,
        CancellationToken ct,
        Func<ProcessOutputLine, CancellationToken, ValueTask>? onOutput = null) =>
        processRunner.RunAsync(new ProcessInvocation(runtime.ExecutablePath!, args, cwd, TimeSpan.FromMinutes(10), StandardInput: input, OnOutput: onOutput), ct);

    private static SvnCommandResult ToResult(ProcessExecutionResult result) =>
        new(result.ExitCode == 0 && !result.TimedOut, result.StandardOutput, result.ExitCode == 0 ? null : result.StandardError, ParseRevision(result.StandardOutput));

    private static string? ParseRevision(string output)
    {
        const string marker = "revision ";
        var index = output.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return null;
        return new string(output[(index + marker.Length)..].TakeWhile(char.IsDigit).ToArray()) is { Length: > 0 } value ? value : null;
    }

    private static SvnCommandResult Failure(string error) => new(false, "", error);
}
