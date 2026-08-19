using AgentService.Host.RestEndpoints;
using AgentService.Domain.Models;
using AgentService.Infrastructure.Providers;
using AgentService.Application.Contracts;
using Microsoft.Extensions.Options;

namespace AgentService.CoreRegression;

/// <summary>驗證 Copilot 冷啟動期間不會被誤判成永久無模型。</summary>
public sealed class ProviderModelsAvailabilityRegressionTests
{
    [Fact]
    public void ValidatingRuntime_ReturnsRetryableNotReadyError()
    {
        var error = ProviderEndpoints.GetCopilotModelsAvailabilityError(
            CopilotRuntimeStatus.Validating(),
            isReady: false);

        Assert.NotNull(error);
        Assert.Equal("copilot_runtime_not_ready", error.Code);
        Assert.True(error.Retryable);
    }

    [Fact]
    public void AuthenticatedRuntimeBeforeClientReady_RemainsRetryable()
    {
        var error = ProviderEndpoints.GetCopilotModelsAvailabilityError(
            CopilotRuntimeStatus.Configured(
                login: "wingman-test",
                authType: "pat",
                copilotPlan: null,
                modelCount: 1),
            isReady: false);

        Assert.NotNull(error);
        Assert.Equal("copilot_runtime_not_ready", error.Code);
        Assert.True(error.Retryable);
    }

    [Fact]
    public void InvalidRuntime_ReturnsNonRetryableUnavailableError()
    {
        var error = ProviderEndpoints.GetCopilotModelsAvailabilityError(
            CopilotRuntimeStatus.Invalid("PAT 無效。"),
            isReady: false);

        Assert.NotNull(error);
        Assert.Equal("copilot_runtime_unavailable", error.Code);
        Assert.False(error.Retryable);
    }

    [Theory]
    [InlineData(ProviderProtocol.OpenAI, ProviderProtocol.OpenAI)]
    [InlineData(ProviderProtocol.OpenAICompatible, ProviderProtocol.OpenAICompatible)]
    [InlineData(ProviderProtocol.Anthropic, ProviderProtocol.Anthropic)]
    [InlineData(ProviderProtocol.AzureOpenAI, ProviderProtocol.AzureOpenAI)]
    public void BYOKProfile會使用明確Protocol而非模糊ProviderType(
        ProviderProtocol configured,
        ProviderProtocol expected)
    {
        var profile = new ModelProviderProfile
        {
            Id = "test",
            DisplayName = "測試",
            Protocol = configured,
            ProviderType = "這個舊欄位不應覆寫新協定",
        };

        Assert.Equal(expected, profile.EffectiveProtocol);
    }

    [Fact]
    public async Task 不存在的ProviderId不得靜默改用第一個設定()
    {
        var service = new ModelProviderService(
            new StaticOptionsMonitor<AgentServiceOptions>(new AgentServiceOptions
            {
                ActiveProfileId = "first",
                ModelProviders =
                [
                    new ModelProviderProfileConfig
                    {
                        Id = "first",
                        DisplayName = "第一個",
                    },
                ],
            }),
            new EmptyProviderSettingStore());

        var exception = await Assert.ThrowsAsync<ProviderProfileNotFoundException>(
            () => service.GetProfileAsync("missing").AsTask());
        Assert.Equal("missing", exception.ProfileId);
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable OnChange(Action<T, string?> listener) => NoopDisposable.Instance;

        private sealed class NoopDisposable : IDisposable
        {
            public static readonly NoopDisposable Instance = new();
            public void Dispose() { }
        }
    }

    private sealed class EmptyProviderSettingStore : IProviderSettingStore
    {
        public Task<IReadOnlyList<ProviderSettingEntity>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ProviderSettingEntity>>([]);
        public Task<ProviderSettingEntity?> GetAsync(string profileId, CancellationToken ct = default) =>
            Task.FromResult<ProviderSettingEntity?>(null);
        public string? GetApiKey(string profileId) => null;
        public Task SetValidatedCredentialAsync(string profileId, string apiKey, string? baseUrl, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task RemoveApiKeyAsync(string profileId, CancellationToken ct = default) => Task.CompletedTask;
        public Task ReorderAsync(IReadOnlyList<(string ProfileId, int SortOrder)> order, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
