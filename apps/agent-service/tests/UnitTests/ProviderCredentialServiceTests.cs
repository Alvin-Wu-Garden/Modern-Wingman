using AgentService.Application.Contracts;
using AgentService.Application.Models;
using AgentService.Domain.Models;
using AgentService.Infrastructure.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentService.UnitTests;

public sealed class ProviderCredentialServiceTests
{
    [Fact]
    public async Task InvalidCandidate_IsNeverPersisted()
    {
        var store = new RecordingProviderSettingStore();
        var service = CreateService(store, ApiKeyValidationResult.Invalid("rejected"));

        var result = await service.ValidateAndSaveAsync(ByokProfile(), "bad-key", "https://api.example.test/v1");

        Assert.False(result.IsValid);
        Assert.Equal(0, store.ValidatedWriteCount);
        Assert.Null(store.StoredApiKey);
    }

    [Fact]
    public async Task InvalidReplacement_PreservesExistingCredentialAndBaseUrl()
    {
        var store = new RecordingProviderSettingStore("old-key", "https://old.example.test/v1");
        var service = CreateService(store, ApiKeyValidationResult.Invalid("rejected"));

        var result = await service.ValidateAndSaveAsync(ByokProfile(), "bad-new-key", "https://new.example.test/v1");

        Assert.False(result.IsValid);
        Assert.Equal(0, store.ValidatedWriteCount);
        Assert.Equal("old-key", store.StoredApiKey);
        Assert.Equal("https://old.example.test/v1", store.StoredBaseUrl);
    }

    [Fact]
    public async Task ValidCandidate_PersistsKeyAndBaseUrlTogether()
    {
        var store = new RecordingProviderSettingStore("old-key", "https://old.example.test/v1");
        var service = CreateService(store, ApiKeyValidationResult.Valid());

        var result = await service.ValidateAndSaveAsync(ByokProfile(), "new-key", "https://new.example.test/v1");

        Assert.True(result.IsValid);
        Assert.Equal(1, store.ValidatedWriteCount);
        Assert.Equal("new-key", store.StoredApiKey);
        Assert.Equal("https://new.example.test/v1", store.StoredBaseUrl);
    }

    [Fact]
    public async Task CopilotActivationFailure_RestoresPreviousPat()
    {
        var store = new RecordingProviderSettingStore("old-pat");
        var runtime = new RecordingCopilotRuntime(tokenToReject: "new-pat");
        var service = CreateService(store, ApiKeyValidationResult.Valid(), runtime);
        var profile = new ModelProviderProfile
        {
            Id = "copilot-default",
            DisplayName = "GitHub Copilot",
            Kind = ProviderKind.CopilotDefault,
            ProviderType = "github",
        };

        var result = await service.ValidateAndSaveAsync(profile, "new-pat", null);

        Assert.False(result.IsValid);
        Assert.Equal("old-pat", store.StoredApiKey);
        Assert.Equal(["new-pat", "old-pat"], runtime.RestartedTokens);
    }

    private static ProviderCredentialService CreateService(
        RecordingProviderSettingStore store,
        ApiKeyValidationResult validation,
        RecordingCopilotRuntime? runtime = null) =>
        new(
            [new StubValidator(validation)],
            store,
            runtime ?? new RecordingCopilotRuntime(),
            NullLogger<ProviderCredentialService>.Instance);

    private static ModelProviderProfile ByokProfile() => new()
    {
        Id = "openai-byok",
        DisplayName = "OpenAI",
        Kind = ProviderKind.CopilotByok,
        ProviderType = "openai",
        BaseUrl = "https://api.openai.com/v1",
    };

    private sealed class StubValidator(ApiKeyValidationResult result) : IProviderApiKeyValidator
    {
        public bool CanValidate(ModelProviderProfile profile) => true;

        public Task<ApiKeyValidationResult> ValidateAsync(
            ModelProviderProfile profile,
            string apiKey,
            string? baseUrl,
            CancellationToken ct = default) => Task.FromResult(result);
    }

    private sealed class RecordingCopilotRuntime(string? tokenToReject = null) : ICopilotCredentialRuntime
    {
        public List<string?> RestartedTokens { get; } = [];

        public Task<ApiKeyValidationResult> ValidateAsync(string githubToken, CancellationToken ct = default) =>
            Task.FromResult(ApiKeyValidationResult.Valid());

        public Task RestartWithTokenAsync(string? githubToken, CancellationToken ct = default)
        {
            RestartedTokens.Add(githubToken);
            return string.Equals(githubToken, tokenToReject, StringComparison.Ordinal)
                ? Task.FromException(new InvalidOperationException("activation failed"))
                : Task.CompletedTask;
        }
    }

    private sealed class RecordingProviderSettingStore(
        string? initialApiKey = null,
        string? initialBaseUrl = null) : IProviderSettingStore
    {
        public string? StoredApiKey { get; private set; } = initialApiKey;
        public string? StoredBaseUrl { get; private set; } = initialBaseUrl;
        public int ValidatedWriteCount { get; private set; }

        public Task<IReadOnlyList<ProviderSettingEntity>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ProviderSettingEntity>>([]);

        public Task<ProviderSettingEntity?> GetAsync(string profileId, CancellationToken ct = default) =>
            Task.FromResult(StoredApiKey is null && StoredBaseUrl is null
                ? null
                : new ProviderSettingEntity
                {
                    ProfileId = profileId,
                    ApiKey = StoredApiKey,
                    BaseUrl = StoredBaseUrl,
                });

        public string? GetApiKey(string profileId) => StoredApiKey;
        public bool HasEnvVar(string profileId) => false;

        public Task SetValidatedCredentialAsync(
            string profileId,
            string apiKey,
            string? baseUrl,
            CancellationToken ct = default)
        {
            ValidatedWriteCount++;
            StoredApiKey = apiKey;
            StoredBaseUrl = baseUrl;
            return Task.CompletedTask;
        }

        public Task RemoveApiKeyAsync(string profileId, CancellationToken ct = default)
        {
            StoredApiKey = null;
            return Task.CompletedTask;
        }

        public Task ReorderAsync(
            IReadOnlyList<(string ProfileId, int SortOrder)> order,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task EnsureSeedAsync(IReadOnlyList<string> profileIds, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
