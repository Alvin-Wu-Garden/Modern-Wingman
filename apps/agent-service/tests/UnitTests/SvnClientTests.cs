using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;
using AgentService.Infrastructure.Tools;
using AgentService.Infrastructure.VersionControl;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentService.UnitTests;

public sealed class SvnClientTests
{
    [Fact]
    public async Task Credential_UsesStandardInputAndScopedSslFlags()
    {
        var profile = Profile("password", ssl: false);
        var runner = new CapturingRunner();
        var client = new SvnClient(new Runtime("svn.exe"), new Profiles(profile), runner);

        await client.BrowseAsync(profile.Id, "https://svn.example/repo");

        var invocation = Assert.Single(runner.Invocations);
        Assert.Equal("password", invocation.StandardInput);
        Assert.DoesNotContain(invocation.Arguments, x => x == "password");
        Assert.Contains("--password-from-stdin", invocation.Arguments);
        Assert.Contains(invocation.Arguments, x => x.StartsWith("--trust-server-cert-failures=", StringComparison.Ordinal));
        Assert.Contains("--no-auth-cache", invocation.Arguments);
    }

    [Fact]
    public async Task SslIgnoreFlags_AreAbsentForVerifyingProfile()
    {
        var profile = Profile("password", ssl: true);
        var runner = new CapturingRunner();
        var client = new SvnClient(new Runtime("svn.exe"), new Profiles(profile), runner);

        await client.BrowseAsync(profile.Id, "https://svn.example/repo");

        var invocation = Assert.Single(runner.Invocations);
        Assert.DoesNotContain(invocation.Arguments, argument =>
            argument.StartsWith("--trust-server-cert-failures=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LocalRepository_BrowseCheckoutAddCommitMoveAndDelete()
    {
        var svn = Find("svn.exe");
        var svnadmin = Find("svnadmin.exe");
        var root = Path.Combine(Path.GetTempPath(), "wingman-svn-test-" + Guid.NewGuid().ToString("N"));
        var repository = Path.Combine(root, "repository");
        var workingCopy = Path.Combine(root, "working-copy");
        Directory.CreateDirectory(root);
        try
        {
            var runner = new ManagedProcessRunner(NullLogger<ManagedProcessRunner>.Instance);
            await Run(runner, svnadmin, root, "create", repository);
            var url = new Uri(repository).AbsoluteUri.TrimEnd('/');
            await Run(runner, svn, root, "mkdir", $"{url}/trunk", $"{url}/branches", $"{url}/tags", "-m", "layout");

            var profile = Profile();
            var client = new SvnClient(new Runtime(svn), new Profiles(profile), runner);
            Assert.True((await client.BrowseAsync(profile.Id, url)).Success);
            Assert.True((await client.CheckoutAsync(profile.Id, $"{url}/trunk", workingCopy)).Success);
            await File.WriteAllTextAsync(Path.Combine(workingCopy, "feature.txt"), "feature\n");
            await File.WriteAllTextAsync(Path.Combine(workingCopy,"move-me.txt"),"move\n");
            Assert.True((await client.AddAsync(workingCopy, "feature.txt")).Success);
            Assert.True((await client.AddAsync(workingCopy,"move-me.txt")).Success);
            var commit = await client.CommitAsync(profile.Id, workingCopy, "add feature");
            Assert.True(commit.Success, commit.Error);
            Assert.NotNull(commit.Revision);

            var git = Find("git.exe");
            var shadowPath = Path.Combine(root, "shadow");
            var runWorktree = Path.Combine(root, "run-worktree");
            var dualRuntime = new DualRuntime(git, svn);
            var gitClient = new GitClient(dualRuntime, new Profiles(profile), runner);
            var shadow = new ShadowGitService(dualRuntime, runner, gitClient, client);
            await shadow.InitializeAsync(workingCopy, shadowPath, $"{url}/trunk", commit.Revision!);
            Assert.True((await shadow.CreateRunWorktreeAsync(
                shadowPath, runWorktree, "wingman/svn-run")).Success);
            Assert.True(File.Exists(Path.Combine(shadowPath, "move-me.txt")));
            Assert.True(File.Exists(Path.Combine(runWorktree, "move-me.txt")));
            await File.AppendAllTextAsync(Path.Combine(runWorktree, "feature.txt"), "changed\n");
            await File.WriteAllTextAsync(Path.Combine(runWorktree, "added.txt"), "added\n");
            Directory.CreateDirectory(Path.Combine(runWorktree, "empty-folder"));
            File.Move(Path.Combine(runWorktree, "move-me.txt"), Path.Combine(runWorktree, "moved.txt"));
            var applied = await shadow.ApplyToSvnAsync(profile.Id, shadowPath, runWorktree);
            Assert.True(applied.Success, applied.Error);
            Assert.Contains("added.txt", applied.Added);
            Assert.Contains("feature.txt", applied.Modified);
            Assert.Contains("move-me.txt -> moved.txt", applied.Renamed!);
            Assert.Contains("empty-folder", (await client.StatusAsync(workingCopy)).Output);
            Assert.True((await client.CommitAsync(profile.Id, workingCopy, "apply shadow changes")).Success);

            var merged = await shadow.ApplyToSvnAsync(profile.Id, shadowPath, runWorktree);
            Assert.True(merged.Success, merged.Error);
            Assert.False(merged.RevisionConflict);

            await File.WriteAllTextAsync(Path.Combine(workingCopy, "feature.txt"), "remote change\n");
            Assert.True((await client.CommitAsync(profile.Id, workingCopy, "remote conflict")).Success);
            await File.WriteAllTextAsync(Path.Combine(runWorktree, "feature.txt"), "agent change\n");
            var conflict = await shadow.ApplyToSvnAsync(profile.Id, shadowPath, runWorktree);
            Assert.True(conflict.RevisionConflict);
            Assert.False(conflict.Success);
            Assert.Contains("feature.txt", conflict.Error);

            Assert.True((await client.MoveAsync(workingCopy, "feature.txt", "renamed.txt")).Success);
            Assert.True((await client.CommitAsync(profile.Id, workingCopy, "rename feature")).Success);
            Assert.True((await client.DeleteAsync(workingCopy, "renamed.txt")).Success);
            Assert.True((await client.CommitAsync(profile.Id, workingCopy, "delete feature")).Success);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static VcsConnectionProfile Profile(string? secret = null, bool ssl = true) => new()
    {
        Name="svn", VcsType=VcsType.Svn, ServerType=VcsServerType.Svn,
        BaseUrl="https://svn.example", Username=secret is null ? null : "developer",
        SecretType=VcsSecretType.Password, SecretValue=secret, SslVerificationEnabled=ssl,
    };

    private static string Find(string name) => (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(path => Path.Combine(path, name)).FirstOrDefault(File.Exists)
        ?? throw new InvalidOperationException($"{name} is required for integration tests.");

    private static async Task Run(IProcessRunner runner, string executable, string cwd, params string[] args)
    {
        var result = await runner.RunAsync(new ProcessInvocation(executable, args, cwd, TimeSpan.FromSeconds(30)));
        Assert.True(result.ExitCode == 0, result.StandardError);
    }

    private sealed class Runtime(string executable) : IVcsRuntimeResolver
    {
        public Task<VcsRuntimeInfo> ResolveAsync(VcsType type, CancellationToken ct = default) => Task.FromResult(new VcsRuntimeInfo(type, true, executable, "test", "test"));
    }

    private sealed class DualRuntime(string git,string svn):IVcsRuntimeResolver
    {
        public Task<VcsRuntimeInfo> ResolveAsync(VcsType type,CancellationToken ct=default){var executable=type==VcsType.Git?git:svn;return Task.FromResult(new VcsRuntimeInfo(type,true,executable,"test","test"));}
    }

    private sealed class Profiles(VcsConnectionProfile profile) : IVcsProfileRepository
    {
        public Task<IReadOnlyList<VcsConnectionProfile>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<VcsConnectionProfile>>([profile]);
        public Task<VcsConnectionProfile?> GetAsync(string id, CancellationToken ct = default) => Task.FromResult<VcsConnectionProfile?>(id == profile.Id ? profile : null);
        public Task SaveAsync(VcsConnectionProfile value, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class CapturingRunner : IProcessRunner
    {
        public List<ProcessInvocation> Invocations { get; } = [];
        public Task<ProcessExecutionResult> RunAsync(ProcessInvocation invocation, CancellationToken ct = default)
        {
            Invocations.Add(invocation);
            return Task.FromResult(new ProcessExecutionResult(0, "", "", false, 1));
        }
    }
}
