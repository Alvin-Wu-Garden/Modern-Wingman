using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;
using AgentService.Infrastructure.VersionControl;

namespace AgentService.UnitTests;

/// <summary>驗證保留下來的 SVN 匯入能力與密碼傳遞方式。</summary>
public sealed class SvnClientTests
{
    [Fact]
    public async Task Browse_PassesPasswordThroughStandardInput()
    {
        var profile = CreateProfile("password", sslVerification: false);
        var runner = new CapturingRunner();
        var client = new SvnClient(
            new FixedRuntime("svn.exe"),
            new FixedProfiles(profile),
            runner);

        await client.BrowseAsync(profile.Id, "https://svn.example/repository");

        var invocation = Assert.Single(runner.Invocations);
        Assert.Equal("password", invocation.StandardInput);
        Assert.DoesNotContain("password", invocation.Arguments);
        Assert.Contains("--password-from-stdin", invocation.Arguments);
        Assert.Contains(
            invocation.Arguments,
            argument => argument.StartsWith(
                "--trust-server-cert-failures",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task Browse_KeepsCertificateVerificationByDefault()
    {
        var profile = CreateProfile("password", sslVerification: true);
        var runner = new CapturingRunner();
        var client = new SvnClient(
            new FixedRuntime("svn.exe"),
            new FixedProfiles(profile),
            runner);

        await client.BrowseAsync(profile.Id, "https://svn.example/repository");

        Assert.DoesNotContain(
            Assert.Single(runner.Invocations).Arguments,
            argument => argument.StartsWith(
                "--trust-server-cert-failures",
                StringComparison.Ordinal));
    }

    private static VcsConnectionProfile CreateProfile(
        string? secret,
        bool sslVerification) =>
        new()
        {
            Name = "svn",
            VcsType = VcsType.Svn,
            ServerType = VcsServerType.Svn,
            BaseUrl = "https://svn.example",
            Username = "developer",
            SecretType = VcsSecretType.Password,
            SecretValue = secret,
            SslVerificationEnabled = sslVerification,
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

    private sealed class CapturingRunner : IProcessRunner
    {
        public List<ProcessInvocation> Invocations { get; } = [];

        public Task<ProcessExecutionResult> RunAsync(
            ProcessInvocation invocation,
            CancellationToken ct = default)
        {
            Invocations.Add(invocation);
            return Task.FromResult(
                new ProcessExecutionResult(0, "", "", false, 1));
        }
    }
}
