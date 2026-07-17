using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;
using AgentService.Infrastructure.Tools;
using AgentService.Infrastructure.VersionControl;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;

namespace AgentService.UnitTests;

public sealed class GitClientTests
{
    [Fact]
    public async Task Credentials_ArePassedOnlyThroughTemporaryEnvironment()
    {
        var profile = Profile(secret: "top-secret", sslVerification: false, username: "developer");
        var runner = new CapturingRunner(new ProcessExecutionResult(
            0, "abc\trefs/heads/main\n", "", false, 1));
        var client = new GitClient(
            new FixedRuntime("git.exe"), new FixedProfiles(profile), runner);

        var branches = await client.ListRemoteBranchesAsync(profile.Id, "https://git.example/repo.git");

        Assert.Equal(["main"], branches);
        var invocation = Assert.Single(runner.Invocations);
        Assert.DoesNotContain(invocation.Arguments, value => value.Contains("top-secret", StringComparison.Ordinal));
        Assert.Contains(invocation.Arguments, value => value.Contains("developer@", StringComparison.Ordinal));
        Assert.Equal("top-secret", invocation.Environment!["WINGMAN_GIT_TOKEN"]);
        Assert.Equal("true", invocation.Environment["GIT_SSL_NO_VERIFY"]);
        var askPass = invocation.Environment["GIT_ASKPASS"];
        Assert.NotNull(askPass);
        Assert.False(File.Exists(askPass));
    }

    [Fact]
    public async Task LocalBareRemote_CloneBranchWorktreeCommitAndPush()
    {
        var git = FindGit();
        var root = Path.Combine(Path.GetTempPath(), "wingman-git-test-" + Guid.NewGuid().ToString("N"));
        var remote = Path.Combine(root, "remote.git");
        var seed = Path.Combine(root, "seed");
        var clone = Path.Combine(root, "clone");
        var worktree = Path.Combine(root, "worktree");
        Directory.CreateDirectory(root);
        try
        {
            var runner = new ManagedProcessRunner(NullLogger<ManagedProcessRunner>.Instance);
            await Git(runner, git, root, "init", "--bare", "--initial-branch=main", remote);
            Directory.CreateDirectory(seed);
            await Git(runner, git, seed, "init", "--initial-branch=main");
            await File.WriteAllTextAsync(Path.Combine(seed, "README.md"), "baseline\n");
            await Git(runner, git, seed, "add", "--all");
            await Git(runner, git, seed, "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit", "-m", "initial");
            await Git(runner, git, seed, "remote", "add", "origin", remote);
            await Git(runner, git, seed, "push", "origin", "main");

            var profile = Profile();
            var client = new GitClient(new FixedRuntime(git), new FixedProfiles(profile), runner);
            Assert.Contains("main", await client.ListRemoteBranchesAsync(profile.Id, remote));
            Assert.True((await client.CloneAsync(profile.Id, remote, "main", clone)).Success);
            Assert.True((await client.SwitchAsync(clone, "wingman/task", true, "main")).Success);
            await File.WriteAllTextAsync(Path.Combine(clone, "feature.txt"), "feature\n");
            var commit = await client.CommitAsync(profile.Id, clone, "add feature");
            Assert.True(commit.Success, commit.Error);
            Assert.False(string.IsNullOrWhiteSpace(commit.CommitId));
            Assert.True((await client.PushAsync(profile.Id, clone, "origin", "wingman/task")).Success);
            Assert.Contains("wingman/task", await client.ListRemoteBranchesAsync(profile.Id, remote));

            await File.WriteAllTextAsync(Path.Combine(clone, "README.md"), "local uncommitted\n");
            await File.WriteAllTextAsync(Path.Combine(clone, "untracked.txt"), "not committed\n");
            var run=new RunEntity{SessionId="s",UserMessage="task",WorkspacePath=clone,Mode=AgentMode.Auto,WorkspaceStrategy=WorkspaceStrategy.GitWorktree};
            var manager=new RunWorkspaceManager(client,new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Workspace:WorktreeRoot",Path.Combine(root,"managed-worktrees")}}).Build());
            var prepared=await manager.PrepareAsync(run);
            Assert.NotEqual(clone,prepared.Path);
            Assert.StartsWith("wingman/",prepared.Branch);
            Assert.False(string.IsNullOrWhiteSpace(prepared.BaseRevision));
            Assert.True(File.Exists(Path.Combine(prepared.Path!,"README.md")));
            Assert.Equal("local uncommitted\n", await File.ReadAllTextAsync(Path.Combine(prepared.Path!, "README.md")));
            Assert.Equal("not committed\n", await File.ReadAllTextAsync(Path.Combine(prepared.Path!, "untracked.txt")));

            var worktreeResult = await client.CreateWorktreeAsync(
                clone, worktree, "wingman/worktree", "main");
            Assert.True(worktreeResult.Success, worktreeResult.Error);
            Assert.True(File.Exists(Path.Combine(worktree, "README.md")));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void DirtyPathParser_HandlesModifiedUntrackedAndRenameRecords()
    {
        var paths = RunWorkspaceManager.ParseDirtyPaths("""
            # branch.oid abc
            1 .M N... 100644 100644 100644 abc abc src/file one.cs
            ? new file.txt
            2 R. N... 100644 100644 100644 abc def R100 src/new.cs	src/old.cs
            """);

        Assert.Contains(paths, item => item.Path == "src/file one.cs" && item.OriginalPath is null);
        Assert.Contains(paths, item => item.Path == "new file.txt" && item.OriginalPath is null);
        Assert.Contains(paths, item => item.Path == "src/new.cs" && item.OriginalPath == "src/old.cs");
    }

    private static VcsConnectionProfile Profile(
        string? secret = null,
        bool sslVerification = true,
        string? username = null) => new()
    {
        Name = "test",
        VcsType = VcsType.Git,
        ServerType = VcsServerType.BitbucketServer,
        BaseUrl = "https://git.example",
        Username = username,
        SecretType = VcsSecretType.AccessToken,
        SecretValue = secret,
        SslVerificationEnabled = sslVerification,
    };

    private static string FindGit()
    {
        var path = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(directory => new[] { "git.exe", "git" }.Select(name => Path.Combine(directory, name)))
            .FirstOrDefault(File.Exists);
        return path ?? throw new InvalidOperationException("Git is required for integration tests.");
    }

    private static async Task Git(
        IProcessRunner runner,
        string executable,
        string cwd,
        params string[] arguments)
    {
        var result = await runner.RunAsync(new ProcessInvocation(
            executable, arguments, cwd, TimeSpan.FromSeconds(30)));
        Assert.True(result.ExitCode == 0, result.StandardError);
    }

    private sealed class FixedRuntime(string executable) : IVcsRuntimeResolver
    {
        public Task<VcsRuntimeInfo> ResolveAsync(VcsType type, CancellationToken ct = default) =>
            Task.FromResult(new VcsRuntimeInfo(type, true, executable, "test", "test"));
    }

    private sealed class FixedProfiles(VcsConnectionProfile profile) : IVcsProfileRepository
    {
        public Task<IReadOnlyList<VcsConnectionProfile>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<VcsConnectionProfile>>([profile]);
        public Task<VcsConnectionProfile?> GetAsync(string id, CancellationToken ct = default) =>
            Task.FromResult<VcsConnectionProfile?>(id == profile.Id ? profile : null);
        public Task SaveAsync(VcsConnectionProfile value, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class CapturingRunner(ProcessExecutionResult result) : IProcessRunner
    {
        public List<ProcessInvocation> Invocations { get; } = [];
        public Task<ProcessExecutionResult> RunAsync(ProcessInvocation invocation, CancellationToken ct = default)
        {
            Invocations.Add(invocation);
            return Task.FromResult(result);
        }
    }
}
