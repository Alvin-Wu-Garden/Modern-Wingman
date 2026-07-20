using AgentService.Application.Contracts;
using AgentService.Domain.Models;
using AgentService.Infrastructure.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgentService.UnitTests;

public sealed class CopilotClientServiceTests
{
    [Fact]
    public void CreatePatOnlyOptions_UsesExplicitPatAndDisablesLocalLogin()
    {
        const string pat = "github_pat_11AA22BB33CC44DD55EE";

        var options = CopilotClientService.CreatePatOnlyOptions(pat);

        Assert.Equal(pat, options.GitHubToken);
        Assert.False(options.UseLoggedInUser);
    }

    [Fact]
    public async Task StartAsync_WithoutPat_DoesNotStartRuntimeOrUseLocalCredentials()
    {
        var service = new CopilotClientService(
            Options.Create(new AgentServiceOptions()),
            new TestProviderSettingStore(apiKey: null),
            NullLogger<CopilotClientService>.Instance);

        await service.StartAsync(CancellationToken.None);

        var status = service.GetRuntimeStatus();
        Assert.Equal("not_configured", status.State);
        Assert.False(status.IsAuthenticated);
        Assert.Throws<InvalidOperationException>(() => service.GetClient());

        await service.DisposeAsync();
    }

    [Fact]
    public void SanitizeError_RedactsExactPatBeforeItCanReachStatusOrLogs()
    {
        const string pat = "github_pat_11AA22BB33CC44DD55EE";

        var safe = CopilotClientService.SanitizeError($"Authentication failed for {pat}", pat);

        Assert.DoesNotContain(pat, safe);
        Assert.Contains("[REDACTED]", safe);
    }

    private sealed class TestProviderSettingStore(string? apiKey) : IProviderSettingStore
    {
        public string? GetApiKey(string profileId) => profileId == "copilot-default" ? apiKey : null;
        public bool HasEnvVar(string profileId) => false;
        public Task<IReadOnlyList<ProviderSettingEntity>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ProviderSettingEntity>>([]);
        public Task<ProviderSettingEntity?> GetAsync(string profileId, CancellationToken ct = default) =>
            Task.FromResult<ProviderSettingEntity?>(null);
        public Task SetApiKeyAsync(string profileId, string value, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveApiKeyAsync(string profileId, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetBaseUrlAsync(string profileId, string? baseUrl, CancellationToken ct = default) => Task.CompletedTask;
        public Task ReorderAsync(IReadOnlyList<(string ProfileId, int SortOrder)> order, CancellationToken ct = default) => Task.CompletedTask;
        public Task EnsureSeedAsync(IReadOnlyList<string> profileIds, CancellationToken ct = default) => Task.CompletedTask;
    }
}
