using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;

namespace AgentService.Infrastructure.VersionControl;

public sealed class VcsRuntimeResolver(
    IConfiguration configuration,
    IProcessRunner processRunner) : IVcsRuntimeResolver
{
    public async Task<VcsRuntimeInfo> ResolveAsync(VcsType type, CancellationToken ct = default)
    {
        var executable = type == VcsType.Git ? "git.exe" : "svn.exe";
        foreach (var candidate in Candidates(type, executable))
        {
            if (!File.Exists(candidate.Path))
                continue;
            try
            {
                var result = await processRunner.RunAsync(
                    new ProcessInvocation(
                        candidate.Path,
                        ["--version"],
                        Environment.CurrentDirectory,
                        TimeSpan.FromSeconds(10),
                        MaxOutputCharacters: 4096),
                    ct);
                if (result.ExitCode == 0)
                {
                    return new VcsRuntimeInfo(
                        type,
                        true,
                        candidate.Path,
                        result.StandardOutput.Trim(),
                        candidate.Source);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (candidate.Source == "configured")
                    return new VcsRuntimeInfo(type, false, candidate.Path, null, candidate.Source, ex.Message);
            }
        }
        return new VcsRuntimeInfo(type, false, null, null, null, $"{executable} was not found.");
    }

    private IEnumerable<(string Path, string Source)> Candidates(VcsType type, string executable)
    {
        var configured = configuration[$"VersionControl:{type}:ExecutablePath"];
        if (!string.IsNullOrWhiteSpace(configured))
            yield return (Path.GetFullPath(configured), "configured");

        var system = FindOnPath(executable);
        if (system is not null)
            yield return (system, "system");

        var relative = type == VcsType.Git
            ? Path.Combine("tools", "vcs", "git", "cmd", executable)
            : Path.Combine("tools", "vcs", "svn", "bin", executable);
        yield return (Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, relative)), "wingman");
    }

    private static string? FindOnPath(string executable)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), executable);
                if (File.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }
            catch { }
        }
        return null;
    }
}
