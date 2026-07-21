using AgentService.Application.Contracts;
using AgentService.Infrastructure.Providers;
using Microsoft.Extensions.Options;

namespace AgentService.Infrastructure.Persistence;

/// <summary>
/// IApiKeyStore 實作 — 委派給 IProviderSettingStore（wingman.db）。
///
/// 讓 ProviderConfigResolver 與 ByokAgentFactory 以同一規則讀取環境變數或 SQLite Key。
/// </summary>
public sealed class ApiKeyStore : IApiKeyStore
{
    private readonly IProviderSettingStore _settingStore;
    private readonly AgentServiceOptions _options;

    public ApiKeyStore(
        IProviderSettingStore settingStore,
        IOptions<AgentServiceOptions> options)
    {
        _settingStore = settingStore;
        _options = options.Value;
    }

    public bool HasEnvVar(string profileId) => _settingStore.HasEnvVar(profileId);

    public string? Get(string profileId) => _settingStore.GetApiKey(profileId);
}
