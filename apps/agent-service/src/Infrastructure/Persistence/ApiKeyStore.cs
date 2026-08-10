using AgentService.Application.Contracts;

namespace AgentService.Infrastructure.Persistence;

/// <summary>
/// IApiKeyStore 實作 — 委派給 IProviderSettingStore（wingman.db）。
///
/// 讓 ProviderConfigResolver 與 ByokAgentFactory 以同一規則讀取 SQLite Key。
/// </summary>
public sealed class ApiKeyStore : IApiKeyStore
{
    private readonly IProviderSettingStore _settingStore;
    public ApiKeyStore(
        IProviderSettingStore settingStore)
    {
        _settingStore = settingStore;
    }

    public string? Get(string profileId) => _settingStore.GetApiKey(profileId);
}
