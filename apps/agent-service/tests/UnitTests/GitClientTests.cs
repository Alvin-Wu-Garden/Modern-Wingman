using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;
using AgentService.Infrastructure.VersionControl;

namespace AgentService.UnitTests;

/// <summary>驗證保留下來的 Git 匯入能力不會把權杖放入命令列。</summary>
public sealed class GitClientTests
{
    [Fact]
    public async Task ListRemoteBranches_UsesTemporaryEnvironmentForCredential()
    {
        var profile = CreateProfile(secret: "top-secret", username: "developer");
        var runner = new CapturingRunner(
            new ProcessExecutionResult(0, "abc\trefs/heads/main\n", "", false, 1));
        var client = new GitClient(
            new FixedRuntime("git.exe"),
            new FixedProfiles(profile),
            runner);

        var branches = await client.ListRemoteBranchesAsync(
            profile.Id,
            "https://git.example/repo.git");

        Assert.Equal(["main"], branches);
        var invocation = Assert.Single(runner.Invocations);
        Assert.DoesNotContain(
            invocation.Arguments,
            argument => argument.Contains("top-secret", StringComparison.Ordinal));
        Assert.Equal("top-secret", invocation.Environment!["WINGMAN_GIT_TOKEN"]);
        Assert.Equal("0", invocation.Environment["GIT_TERMINAL_PROMPT"]);
        var askPassPath = invocation.Environment["GIT_ASKPASS"];
        Assert.NotNull(askPassPath);
        Assert.False(File.Exists(askPassPath));
    }

    [Fact]
    public async Task Update_UsesFastForwardOnly()
    {
        var profile = CreateProfile();
        var runner = new CapturingRunner(
            new ProcessExecutionResult(0, "Already up to date.", "", false, 1));
        var client = new GitClient(
            new FixedRuntime("git.exe"),
            new FixedProfiles(profile),
            runner);

        var result = await client.UpdateAsync(profile.Id, Path.GetTempPath());

        Assert.True(result.Success);
        Assert.Equal(["pull", "--ff-only"], Assert.Single(runner.Invocations).Arguments);
    }

    private static VcsConnectionProfile CreateProfile(
        string? secret = null,
        string? username = null) =>
        new()
        {
            Name = "test",
            VcsType = VcsType.Git,
            ServerType = VcsServerType.BitbucketServer,
            BaseUrl = "https://git.example",
            Username = username,
            SecretType = VcsSecretType.AccessToken,
            SecretValue = secret,
        };

    private sealed class FixedRuntime(string executable) : IVcsRuntimeResolver
    {
        public Task<VcsRuntimeInfo> ResolveAsync(
            VcsType type,
            CancellationToken ct = default) =>
            Task.FromResult(new VcsRuntimeInfo(type, true, executable, "test", null));
    }

    private sealed class FixedProfiles(VcsConnectionProfile profile) : IVcsProfileRepository
    {
        public Task<IReadOnlyList<VcsConnectionProfile>> ListAsync(
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<VcsConnectionProfile>>([profile]);

        public Task<VcsConnectionProfile?> GetAsync(
            string id,
            CancellationToken ct = default) =>
            Task.FromResult<VcsConnectionProfile?>(id == profile.Id ? profile : null);

        public Task SaveAsync(
            VcsConnectionProfile value,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DeleteAsync(string id, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class CapturingRunner(ProcessExecutionResult result) : IProcessRunner
    {
        public List<ProcessInvocation> Invocations { get; } = [];

        public Task<ProcessExecutionResult> RunAsync(
            ProcessInvocation invocation,
            CancellationToken ct = default)
        {
            Invocations.Add(invocation);
            return Task.FromResult(result);
        }
    }
}
