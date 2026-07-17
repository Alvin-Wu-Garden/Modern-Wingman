using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;
using AgentService.Infrastructure.Tools;
using AgentService.Infrastructure.VersionControl;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentService.UnitTests;

public sealed class ShadowGitServiceTests
{
    [Fact]
    public async Task Initialize_CreatesSeparateRemoteFreeRepositoryAndRunWorktree()
    {
        var git = FindGit();
        var root = Path.Combine(Path.GetTempPath(), "wingman-shadow-test-" + Guid.NewGuid().ToString("N"));
        var workingCopy = Path.Combine(root, "svn-working-copy");
        var shadow = Path.Combine(root, "shadow");
        var worktree = Path.Combine(root, "run-worktree");
        Directory.CreateDirectory(Path.Combine(workingCopy, ".svn"));
        await File.WriteAllTextAsync(Path.Combine(workingCopy, ".svn", "wc.db"), "metadata");
        await File.WriteAllTextAsync(Path.Combine(workingCopy, "source.txt"), "baseline\n");
        try
        {
            var runner = new ManagedProcessRunner(NullLogger<ManagedProcessRunner>.Instance);
            var runtime = new Runtime(git);
            var client = new GitClient(runtime, new EmptyProfiles(), runner);
            var service = new ShadowGitService(runtime, runner, client);

            var baseline = await service.InitializeAsync(
                workingCopy, shadow, "https://svn.example/trunk", "42");

            Assert.False(Directory.Exists(Path.Combine(workingCopy, ".git")));
            Assert.True(Directory.Exists(Path.Combine(shadow, ".git")));
            Assert.False(Directory.Exists(Path.Combine(shadow, ".svn")));
            Assert.True(File.Exists(Path.Combine(shadow, ".wingman-svn-baseline.json")));
            Assert.False(string.IsNullOrWhiteSpace(baseline.CommitId));
            var remotes = await Run(runner, git, shadow, "remote");
            Assert.True(string.IsNullOrWhiteSpace(remotes.StandardOutput));

            var created = await service.CreateRunWorktreeAsync(
                shadow, worktree, "wingman/run-1");
            Assert.True(created.Success, created.Error);
            Assert.True(File.Exists(Path.Combine(worktree, "source.txt")));
            Assert.False(Directory.Exists(Path.Combine(worktree, ".svn")));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static string FindGit() => (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .SelectMany(path => new[] { "git.exe", "git" }.Select(name => Path.Combine(path, name)))
        .FirstOrDefault(File.Exists) ?? throw new InvalidOperationException("Git is required.");

    private static Task<ProcessExecutionResult> Run(IProcessRunner runner, string executable, string cwd, params string[] args) =>
        runner.RunAsync(new ProcessInvocation(executable, args, cwd, TimeSpan.FromSeconds(30)));

    private sealed class Runtime(string executable) : IVcsRuntimeResolver
    {
        public Task<VcsRuntimeInfo> ResolveAsync(VcsType type, CancellationToken ct = default) => Task.FromResult(new VcsRuntimeInfo(type, true, executable, "test", "test"));
    }

    private sealed class EmptyProfiles : IVcsProfileRepository
    {
        public Task<IReadOnlyList<VcsConnectionProfile>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<VcsConnectionProfile>>([]);
        public Task<VcsConnectionProfile?> GetAsync(string id, CancellationToken ct = default) => Task.FromResult<VcsConnectionProfile?>(null);
        public Task SaveAsync(VcsConnectionProfile profile, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
    }
}
